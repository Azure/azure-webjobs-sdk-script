// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.File;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.WebHost.Configuration;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Management
{
    public class InstanceManager : IInstanceManager
    {
        private static readonly object _assignmentLock = new object();
        private static HostAssignmentContext _assignmentContext;

        private readonly ILogger _logger;
        private readonly IMetricsLogger _metricsLogger;
        private readonly IMeshServiceClient _meshServiceClient;
        private readonly IEnvironment _environment;
        private readonly IOptionsFactory<ScriptApplicationHostOptions> _optionsFactory;
        private readonly HttpClient _client;
        private readonly IScriptWebHostEnvironment _webHostEnvironment;

        public InstanceManager(IOptionsFactory<ScriptApplicationHostOptions> optionsFactory, HttpClient client, IScriptWebHostEnvironment webHostEnvironment,
            IEnvironment environment, ILogger<InstanceManager> logger, IMetricsLogger metricsLogger, IMeshServiceClient meshServiceClient)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _webHostEnvironment = webHostEnvironment ?? throw new ArgumentNullException(nameof(webHostEnvironment));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsLogger = metricsLogger;
            _meshServiceClient = meshServiceClient;
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _optionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
        }

        public async Task<string> SpecializeMSISidecar(HostAssignmentContext context)
        {
            // No cold start optimization needed for side car scenarios
            if (context.IsWarmupRequest)
            {
                return null;
            }

            var msiEnabled = context.IsMSIEnabled(out var endpoint);

            _logger.LogInformation($"MSI enabled status: {msiEnabled}");

            if (msiEnabled)
            {
                if (context.MSIContext == null)
                {
                    _logger.LogWarning("Skipping specialization of MSI sidecar since MSIContext was absent");
                    await _meshServiceClient.NotifyHealthEvent(ContainerHealthEventType.Fatal, this.GetType(),
                        "Could not specialize MSI sidecar");
                }
                else
                {
                    using (_metricsLogger.LatencyEvent(MetricEventNames.LinuxContainerSpecializationMSIInit))
                    {
                        var uri = new Uri(endpoint);
                        var address = $"http://{uri.Host}:{uri.Port}{ScriptConstants.LinuxMSISpecializationStem}";

                        _logger.LogDebug($"Specializing sidecar at {address}");

                        var requestMessage = new HttpRequestMessage(HttpMethod.Post, address)
                        {
                            Content = new StringContent(JsonConvert.SerializeObject(context.MSIContext),
                                Encoding.UTF8, "application/json")
                        };

                        var response = await _client.SendAsync(requestMessage);

                        _logger.LogInformation($"Specialize MSI sidecar returned {response.StatusCode}");

                        if (!response.IsSuccessStatusCode)
                        {
                            var message = $"Specialize MSI sidecar call failed. StatusCode={response.StatusCode}";
                            _logger.LogError(message);
                            return message;
                        }
                    }
                }
            }

            return null;
        }

        public bool StartAssignment(HostAssignmentContext context)
        {
            if (!_webHostEnvironment.InStandbyMode)
            {
                _logger.LogError("Assign called while host is not in placeholder mode");
                return false;
            }

            if (context.IsWarmupRequest)
            {
                // Based on profiling download code jit-ing holds up cold start.
                // Pre-jit to avoid paying the cost later.
                Task.Run(async () => await Download(context.GetRunFromPkgContext()));
                return true;
            }
            else if (_assignmentContext == null)
            {
                lock (_assignmentLock)
                {
                    if (_assignmentContext != null)
                    {
                        return _assignmentContext.Equals(context);
                    }
                    _assignmentContext = context;
                }

                _logger.LogInformation($"Starting Assignment. Cloud Name: {_environment.GetCloudName()}");

                // set a flag which will cause any incoming http requests to buffer
                // until specialization is complete
                // the host is guaranteed not to receive any requests until AFTER assign
                // has been initiated, so setting this flag here is sufficient to ensure
                // that any subsequent incoming requests while the assign is in progress
                // will be delayed until complete
                _webHostEnvironment.DelayRequests();

                // start the specialization process in the background
                Task.Run(async () => await Assign(context));

                return true;
            }
            else
            {
                // No lock needed here since _assignmentContext is not null when we are here
                return _assignmentContext.Equals(context);
            }
        }

        public async Task<string> ValidateContext(HostAssignmentContext assignmentContext)
        {
            _logger.LogInformation($"Validating host assignment context (SiteId: {assignmentContext.SiteId}, SiteName: '{assignmentContext.SiteName}'. IsWarmup: '{assignmentContext.IsWarmupRequest}')");
            RunFromPackageContext pkgContext = assignmentContext.GetRunFromPkgContext();
            _logger.LogInformation($"Will be using {pkgContext.EnvironmentVariableName} app setting as zip url. IsWarmup: '{assignmentContext.IsWarmupRequest}')");

            if (pkgContext.IsScmRunFromPackage())
            {
                // Not user assigned so limit validation
                return null;
            }
            else if (!string.IsNullOrEmpty(pkgContext.Url) && pkgContext.Url != "1")
            {
                // In AppService, ZipUrl == 1 means the package is hosted in azure files.
                // Otherwise we expect zipUrl to be a blobUri to a zip or a squashfs image
                (var error, var contentLength) = await ValidateBlobPackageContext(pkgContext);
                if (string.IsNullOrEmpty(error))
                {
                    assignmentContext.PackageContentLength = contentLength;
                }
                return error;
            }
            else if (!string.IsNullOrEmpty(assignmentContext.AzureFilesConnectionString))
            {
                return await ValidateAzureFilesContext(assignmentContext.AzureFilesConnectionString, assignmentContext.AzureFilesContentShare);
            }
            else
            {
                _logger.LogError($"Missing ZipUrl and AzureFiles config. Continue with empty root.");
                return null;
            }
        }

        private async Task<(string, long?)> ValidateBlobPackageContext(RunFromPackageContext context)
        {
            string blobUri = context.Url;
            string eventName = context.IsWarmUpRequest
                ? MetricEventNames.LinuxContainerSpecializationZipHeadWarmup
                : MetricEventNames.LinuxContainerSpecializationZipHead;
            string error = null;
            HttpResponseMessage response = null;
            long? contentLength = null;
            try
            {
                if (!string.IsNullOrEmpty(blobUri))
                {
                    // make sure the zip uri is valid and accessible
                    await Utility.InvokeWithRetriesAsync(async () =>
                    {
                        try
                        {
                            using (_metricsLogger.LatencyEvent(eventName))
                            {
                                var request = new HttpRequestMessage(HttpMethod.Head, blobUri);
                                response = await _client.SendAsync(request);
                                response.EnsureSuccessStatusCode();
                                if (response.Content != null && response.Content.Headers != null)
                                {
                                    contentLength = response.Content.Headers.ContentLength;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"{eventName} failed");
                            throw;
                        }
                    }, maxRetries: 2, retryInterval: TimeSpan.FromSeconds(0.3)); // Keep this less than ~1s total
                }
            }
            catch (Exception e)
            {
                error = $"Invalid zip url specified (StatusCode: {response?.StatusCode})";
                _logger.LogError(e, $"ValidateContext failed. IsWarmupRequest = {context.IsWarmUpRequest}");
            }

            return (error, contentLength);
        }

        private async Task<string> ValidateAzureFilesContext(string connectionString, string contentShare)
        {
            try
            {
                var storageAccount = CloudStorageAccount.Parse(connectionString);
                var fileClient = storageAccount.CreateCloudFileClient();
                var share = fileClient.GetShareReference(contentShare);
                await share.CreateIfNotExistsAsync();
                return null;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"{nameof(ValidateAzureFilesContext)}");
                return e.Message;
            }
        }

        private async Task Assign(HostAssignmentContext assignmentContext)
        {
            try
            {
                // first make all environment and file system changes required for
                // the host to be specialized
                await ApplyContext(assignmentContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assign failed");
                await _meshServiceClient.NotifyHealthEvent(ContainerHealthEventType.Fatal, GetType(), "Assign failed");
                throw;
            }
            finally
            {
                // all assignment settings/files have been applied so we can flip
                // the switch now on specialization
                // even if there are failures applying context above, we want to
                // leave placeholder mode
                _logger.LogInformation("Triggering specialization");
                _webHostEnvironment.FlagAsSpecializedAndReady();

                _webHostEnvironment.ResumeRequests();
            }
        }

        private async Task ApplyContext(HostAssignmentContext assignmentContext)
        {
            _logger.LogInformation($"Applying {assignmentContext.Environment.Count} app setting(s)");
            assignmentContext.ApplyAppSettings(_environment, _logger);

            // We need to get the non-PlaceholderMode script Path so we can unzip to the correct location.
            // This asks the factory to skip the PlaceholderMode check when configuring options.
            var options = _optionsFactory.Create(ScriptApplicationHostOptionsSetup.SkipPlaceholder);
            RunFromPackageContext pkgContext = assignmentContext.GetRunFromPkgContext();

            if ((pkgContext.IsScmRunFromPackage() && await pkgContext.BlobExistsAsync(_logger)) ||
                (!pkgContext.IsScmRunFromPackage() && !string.IsNullOrEmpty(pkgContext.Url) && pkgContext.Url != "1"))
            {
                await ApplyBlobPackageContext(pkgContext, options.ScriptPath);
            }
            else if (!string.IsNullOrEmpty(assignmentContext.AzureFilesConnectionString))
            {
                bool succeeded = await _meshServiceClient.MountCifs(assignmentContext.AzureFilesConnectionString, assignmentContext.AzureFilesContentShare, "/home");
                _logger.LogInformation($"Mounting {EnvironmentSettingNames.AzureFilesContentShare} Success = {succeeded}");
            }

            // BYOS
            var storageVolumes = assignmentContext.GetBYOSEnvironmentVariables()
                .Select(AzureStorageInfoValue.FromEnvironmentVariable).ToList();

            var mountedVolumes =
                (await Task.WhenAll(storageVolumes.Where(v => v != null).Select(MountStorageAccount))).Where(
                    result => result).ToList();

            if (storageVolumes.Any())
            {
                if (mountedVolumes.Count != storageVolumes.Count)
                {
                    _logger.LogWarning(
                        $"Successfully mounted {mountedVolumes.Count} / {storageVolumes.Count} BYOS storage accounts");
                }
                else
                {
                    _logger.LogInformation(
                        $"Successfully mounted {storageVolumes.Count} BYOS storage accounts");
                }
            }
        }

        private async Task<bool> MountStorageAccount(AzureStorageInfoValue storageInfoValue)
        {
            try
            {
                var storageConnectionString =
                    Utility.BuildStorageConnectionString(storageInfoValue.AccountName, storageInfoValue.AccessKey, _environment.GetStorageSuffix());

                await Utility.InvokeWithRetriesAsync(async () =>
                {
                    try
                    {
                        using (_metricsLogger.LatencyEvent($"{MetricEventNames.LinuxContainerSpecializationBYOSMountPrefix}.{storageInfoValue.Type.ToString().ToLowerInvariant()}.{storageInfoValue.Id?.ToLowerInvariant()}"))
                        {
                            switch (storageInfoValue.Type)
                            {
                                case AzureStorageType.AzureFiles:
                                    if (!await _meshServiceClient.MountCifs(storageConnectionString, storageInfoValue.ShareName, storageInfoValue.MountPath))
                                    {
                                        throw new Exception($"Failed to mount BYOS fileshare {storageInfoValue.Id}");
                                    }
                                    break;
                                case AzureStorageType.AzureBlob:
                                    await _meshServiceClient.MountBlob(storageConnectionString, storageInfoValue.ShareName, storageInfoValue.MountPath);
                                    break;
                                default:
                                    throw new NotSupportedException($"Unknown BYOS storage type {storageInfoValue.Type}");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        // todo: Expose any failures here as a part of a health check api.
                        _logger.LogError(e, $"Failed to mount BYOS storage account {storageInfoValue.Id}");
                        throw;
                    }
                    _logger.LogInformation(
                        $"Successfully mounted BYOS storage account {storageInfoValue.Id}");
                }, 1, TimeSpan.FromSeconds(0.5));

                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Failed to mount BYOS storage account {storageInfoValue.Id}");
                return false;
            }
        }

        private async Task ApplyBlobPackageContext(RunFromPackageContext pkgContext, string targetPath)
        {
            // download zip and extract
            var filePath = await Download(pkgContext);
            await UnpackPackage(filePath, targetPath, pkgContext);

            string bundlePath = Path.Combine(targetPath, "worker-bundle");
            if (Directory.Exists(bundlePath))
            {
                _logger.LogInformation($"Python worker bundle detected");
            }
        }

        private async Task<string> Download(RunFromPackageContext pkgContext)
        {
            var zipUri = new Uri(pkgContext.Url);
            if (!Utility.TryCleanUrl(zipUri.AbsoluteUri, out string cleanedUrl))
            {
                throw new Exception("Invalid url for the package");
            }

            var tmpPath = Path.GetTempPath();
            var fileName = Path.GetFileName(zipUri.AbsolutePath);
            var filePath = Path.Combine(tmpPath, fileName);
            if (pkgContext.PackageContentLength != null && pkgContext.PackageContentLength > 100 * 1024 * 1024)
            {
                _logger.LogInformation($"Downloading zip contents from '{cleanedUrl}' using aria2c'");
                AriaDownload(tmpPath, fileName, zipUri, pkgContext.IsWarmUpRequest);
            }
            else
            {
                _logger.LogInformation($"Downloading zip contents from '{cleanedUrl}' using httpclient'");
                await HttpClientDownload(filePath, zipUri, pkgContext.IsWarmUpRequest);
            }

            return filePath;
        }

        private async Task HttpClientDownload(string filePath, Uri zipUri, bool isWarmupRequest)
        {
            HttpResponseMessage response = null;
            await Utility.InvokeWithRetriesAsync(async () =>
            {
                try
                {
                    var downloadMetricName = isWarmupRequest
                        ? MetricEventNames.LinuxContainerSpecializationZipDownloadWarmup
                        : MetricEventNames.LinuxContainerSpecializationZipDownload;
                    using (_metricsLogger.LatencyEvent(downloadMetricName))
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, zipUri);
                        response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                    }
                }
                catch (Exception e)
                {
                    string error = $"Error downloading zip content";
                    _logger.LogError(e, error);
                    throw;
                }
                _logger.LogInformation($"{response.Content.Headers.ContentLength} bytes downloaded. IsWarmupRequest = {isWarmupRequest}");
            }, 2, TimeSpan.FromSeconds(0.5));

            using (_metricsLogger.LatencyEvent(isWarmupRequest ? MetricEventNames.LinuxContainerSpecializationZipWriteWarmup : MetricEventNames.LinuxContainerSpecializationZipWrite))
            {
                using (var content = await response.Content.ReadAsStreamAsync())
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
                {
                    await content.CopyToAsync(stream);
                }
                _logger.LogInformation($"{response.Content.Headers.ContentLength} bytes written. IsWarmupRequest = {isWarmupRequest}");
            }
        }

        private void AriaDownload(string directory, string fileName, Uri zipUri, bool isWarmupRequest)
        {
            var metricName = isWarmupRequest
                ? MetricEventNames.LinuxContainerSpecializationZipDownloadWarmup
                : MetricEventNames.LinuxContainerSpecializationZipDownload;
            (string stdout, string stderr, int exitCode) = RunBashCommand(
                $"aria2c --allow-overwrite -x12 -d {directory} -o {fileName} '{zipUri}'",
                metricName);
            if (exitCode != 0)
            {
                var msg = $"Error downloading package. stdout: {stdout}, stderr: {stderr}, exitCode: {exitCode}";
                _logger.LogError(msg);
                throw new InvalidOperationException(msg);
            }
            var fileInfo = FileUtility.FileInfoFromFileName(Path.Combine(directory, fileName));
            _logger.LogInformation($"{fileInfo.Length} bytes downloaded. IsWarmupRequest = {isWarmupRequest}");
        }

        private async Task UnpackPackage(string filePath, string scriptPath, RunFromPackageContext pkgContext)
        {
            CodePackageType packageType;
            using (_metricsLogger.LatencyEvent(MetricEventNames.LinuxContainerSpecializationGetPackageType))
            {
                packageType = GetPackageType(filePath, pkgContext);
            }

            if (packageType == CodePackageType.Squashfs)
            {
                // default to mount for squashfs images
                if (_environment.IsMountDisabled())
                {
                    UnsquashImage(filePath, scriptPath);
                }
                else
                {
                    await _meshServiceClient.MountFuse("squashfs", filePath, scriptPath);
                }
            }
            else if (packageType == CodePackageType.Zip)
            {
                // default to unzip for zip packages
                if (_environment.IsMountEnabled())
                {
                    await _meshServiceClient.MountFuse("zip", filePath, scriptPath);
                }
                else
                {
                    UnzipPackage(filePath, scriptPath);
                }
            }
        }

        private CodePackageType GetPackageType(string filePath, RunFromPackageContext pkgContext)
        {
            // cloud build always builds squashfs
            if (pkgContext.IsScmRunFromPackage())
            {
                return CodePackageType.Squashfs;
            }

            var uri = new Uri(pkgContext.Url);
            // check file name since it'll be faster than running `file`
            if (FileIsAny(".squashfs", ".sfs", ".sqsh", ".img", ".fs"))
            {
                return CodePackageType.Squashfs;
            }
            else if (FileIsAny(".zip"))
            {
                return CodePackageType.Zip;
            }

            // Check file magic-number using `file` command.
            (var output, _, _) = RunBashCommand($"file -b {filePath}", MetricEventNames.LinuxContainerSpecializationFileCommand);
            if (output.StartsWith("Squashfs", StringComparison.OrdinalIgnoreCase))
            {
                return CodePackageType.Squashfs;
            }
            else if (output.StartsWith("Zip", StringComparison.OrdinalIgnoreCase))
            {
                return CodePackageType.Zip;
            }
            else
            {
                throw new InvalidOperationException($"Can't find CodePackageType to match {filePath}");
            }

            bool FileIsAny(params string[] options)
                => options.Any(o => uri.AbsolutePath.EndsWith(o, StringComparison.OrdinalIgnoreCase));
        }

        private void UnzipPackage(string filePath, string scriptPath)
        {
            using (_metricsLogger.LatencyEvent(MetricEventNames.LinuxContainerSpecializationZipExtract))
            {
                _logger.LogInformation($"Extracting files to '{scriptPath}'");
                ZipFile.ExtractToDirectory(filePath, scriptPath, overwriteFiles: true);
                _logger.LogInformation($"Zip extraction complete");
            }
        }

        private void UnsquashImage(string filePath, string scriptPath)
            => RunBashCommand($"unsquashfs -f -d '{scriptPath}' '{filePath}'", MetricEventNames.LinuxContainerSpecializationUnsquash);

        private (string, string, int) RunBashCommand(string command, string metricName)
        {
            try
            {
                using (_metricsLogger.LatencyEvent(metricName))
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "bash",
                            Arguments = $"-c \"{command}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    _logger.LogInformation($"Running: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                    process.Start();
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    var error = process.StandardError.ReadToEnd().Trim();
                    process.WaitForExit();
                    _logger.LogInformation($"Output: {output}");
                    if (process.ExitCode != 0)
                    {
                        _logger.LogError(error);
                    }
                    else
                    {
                        _logger.LogInformation($"error: {error}");
                    }
                    _logger.LogInformation($"exitCode: {process.ExitCode}");
                    return (output, error, process.ExitCode);
                }
            }
            catch (Exception e)
            {
                _logger.LogError("Error running bash", e);
            }

            return (string.Empty, string.Empty, -1);
        }

        public IDictionary<string, string> GetInstanceInfo()
        {
            return new Dictionary<string, string>
            {
                { "FUNCTIONS_EXTENSION_VERSION", ScriptHost.Version },
                { "WEBSITE_NODE_DEFAULT_VERSION", "8.5.0" }
            };
        }

        // for testing
        internal static void Reset()
        {
            _assignmentContext = null;
        }
    }
}
