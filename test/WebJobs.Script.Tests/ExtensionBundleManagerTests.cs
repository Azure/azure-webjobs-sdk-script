﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.ExtensionBundle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NuGet.Versioning;
using Xunit;
using static Microsoft.Azure.WebJobs.Script.EnvironmentSettingNames;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class ExtensionBundleManagerTests : IDisposable
    {
        private const string BundleId = "Microsoft.Azure.Functions.ExtensionBundle";
        private string _downloadPath;

        public ExtensionBundleManagerTests()
        {
            // using temp path because not all windows build machines would have d drive
            _downloadPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "ExtensionBundles", "Microsoft.Azure.Functions.ExtensionBundle");

            if (Directory.Exists(_downloadPath))
            {
                Directory.Delete(_downloadPath, true);
            }
        }

        [Fact]
        public void TryLocateExtensionBundle_BundleDoesNotMatch_ReturnsFalse()
        {
            var options = GetTestExtensionBundleOptions(BundleId, "[1.*, 2.0.0)");
            var fileSystemTuple = CreateFileSystem();
            var directoryBase = fileSystemTuple.Item2;

            string firstDefaultProbingPath = options.ProbingPaths.ElementAt(0);
            directoryBase.Setup(d => d.Exists(firstDefaultProbingPath)).Returns(true);
            directoryBase.Setup(d => d.EnumerateDirectories(firstDefaultProbingPath)).Returns(new[] { Path.Combine(firstDefaultProbingPath, "3.0.2") });

            FileUtility.Instance = fileSystemTuple.Item1.Object;
            var manager = GetExtensionBundleManager(options, GetAppServiceEnvironment());
            Assert.False(manager.TryLocateExtensionBundle(out string path));
            Assert.Null(path);
        }

        [Fact]
        public void TryLocateExtensionBundle_BundleNotPersent_ReturnsFalse()
        {
            var options = GetTestExtensionBundleOptions(BundleId, "[1.*, 2.0.0)");
            var fileSystemTuple = CreateFileSystem();
            FileUtility.Instance = fileSystemTuple.Item1.Object;
            var manager = GetExtensionBundleManager(options, GetAppServiceEnvironment());
            Assert.False(manager.TryLocateExtensionBundle(out string path));
            Assert.Null(path);
        }

        [Fact]
        public async Task GetExtensionBundle_BundlePresentAtProbingLocation_ReturnsTrue()
        {
            var options = GetTestExtensionBundleOptions(BundleId, "[2.*, 3.0.0)");
            var fileSystemTuple = CreateFileSystem();
            var directoryBase = fileSystemTuple.Item2;
            var fileBase = fileSystemTuple.Item3;
            var httpclient = new HttpClient(new MockHttpHandler(statusCodeForIndexJson: HttpStatusCode.OK, statusCodeForZipFile: HttpStatusCode.OK));

            string firstDefaultProbingPath = options.ProbingPaths.ElementAt(0);
            directoryBase.Setup(d => d.Exists(firstDefaultProbingPath)).Returns(true);
            directoryBase.Setup(d => d.EnumerateDirectories(firstDefaultProbingPath)).Returns(
            new[]
            {
                    Path.Combine(firstDefaultProbingPath, "1.0.0"),
                    Path.Combine(firstDefaultProbingPath, "2.0.0"),
                    Path.Combine(firstDefaultProbingPath, "3.0.2"),
                    Path.Combine(firstDefaultProbingPath, "invalidVersion")
            });

            string defaultPath = Path.Combine(firstDefaultProbingPath, "2.0.0");
            fileBase.Setup(f => f.Exists(Path.Combine(defaultPath, "bundle.json"))).Returns(true);

            FileUtility.Instance = fileSystemTuple.Item1.Object;
            var manager = GetExtensionBundleManager(options, GetAppServiceEnvironment());
            string path = await manager.GetExtensionBundle(httpclient);
            Assert.NotNull(path);

            Assert.Equal(defaultPath, path);
        }

        [Fact]
        public async Task GetExtensionBundle_BundlePresentAtDownloadLocation_ReturnsCorrectPathAync()
        {
            var options = GetTestExtensionBundleOptions(BundleId, "[1.*, 2.0.0)");
            var fileSystemTuple = CreateFileSystem();
            var directoryBase = fileSystemTuple.Item2;
            var fileBase = fileSystemTuple.Item3;
            var httpclient = new HttpClient(new MockHttpHandler(statusCodeForIndexJson: HttpStatusCode.OK, statusCodeForZipFile: HttpStatusCode.OK));

            string firstDefaultProbingPath = options.ProbingPaths.ElementAt(0);
            directoryBase.Setup(d => d.Exists(firstDefaultProbingPath)).Returns(true);
            directoryBase.Setup(d => d.EnumerateDirectories(firstDefaultProbingPath)).Returns(new List<string>());

            string downloadPath = Path.Combine(options.DownloadPath, "1.0.2");
            directoryBase.Setup(d => d.Exists(options.DownloadPath)).Returns(true);
            directoryBase.Setup(d => d.EnumerateDirectories(options.DownloadPath)).Returns(new[] { downloadPath });
            fileBase.Setup(f => f.Exists(Path.Combine(downloadPath, "bundle.json"))).Returns(true);

            FileUtility.Instance = fileSystemTuple.Item1.Object;
            var manager = GetExtensionBundleManager(options, GetAppServiceEnvironment());
            string path = await manager.GetExtensionBundle(httpclient);
            Assert.NotNull(path);
            Assert.Equal(downloadPath, path);
        }

        [Fact]
        public async Task GetExtensionBundle_DownloadsMatchingVersion_ReturnsTrueAsync()
        {
            var options = GetTestExtensionBundleOptions(BundleId, "[1.*, 2.0.0)");
            var manager = GetExtensionBundleManager(options, GetAppServiceEnvironment());
            var httpclient = new HttpClient(new MockHttpHandler(statusCodeForIndexJson: HttpStatusCode.OK, statusCodeForZipFile: HttpStatusCode.OK));
            var path = await manager.GetExtensionBundle(httpclient);
            var bundleDirectory = Path.Combine(_downloadPath, "1.0.1");
            Assert.True(Directory.Exists(bundleDirectory));
            Assert.Equal(bundleDirectory, path);
        }

        [Fact]
        public async Task GetExtensionBundle_DownloadsLatest_WhenEnsureLatestTrue()
        {
            var options = GetTestExtensionBundleOptions(BundleId, "[1.0.0, 1.0.1)");
            var manager = GetExtensionBundleManager(options, GetAppServiceEnvironment());
            var httpclient = new HttpClient(new MockHttpHandler(statusCodeForIndexJson: HttpStatusCode.OK, statusCodeForZipFile: HttpStatusCode.OK));
            var path = await manager.GetExtensionBundle(httpclient);
            var bundleDirectory = Path.Combine(_downloadPath, "1.0.0");
            Assert.True(Directory.Exists(bundleDirectory));
            Assert.Equal(bundleDirectory, path);

            var newOptions = options;
            newOptions.Version = VersionRange.Parse("[1.*, 2.0.0)", true);
            newOptions.EnsureLatest = true;
            manager = GetExtensionBundleManager(newOptions, GetAppServiceEnvironment());
            httpclient = new HttpClient(new MockHttpHandler(statusCodeForIndexJson: HttpStatusCode.OK, statusCodeForZipFile: HttpStatusCode.OK));
            path = await manager.GetExtensionBundle(httpclient);
            bundleDirectory = Path.Combine(_downloadPath, "1.0.1");
            Assert.True(Directory.Exists(bundleDirectory));
            Assert.Equal(bundleDirectory, path);
        }

        [Fact]
        public async Task GetExtensionBundle_DoesNotDownload_WhenPersistentFileSystemNotAvailable()
        {
            var options = GetTestExtensionBundleOptions(BundleId, "[1.0.0, 1.0.1)");
            var manager = GetExtensionBundleManager(options, new TestEnvironment());

            var httpclient = new HttpClient(new MockHttpHandler(statusCodeForIndexJson: HttpStatusCode.OK, statusCodeForZipFile: HttpStatusCode.OK));
            var path = await manager.GetExtensionBundle(httpclient);
            Assert.Null(path);
        }

        [Fact]
        public async Task GetExtensionBundle_CannotReachIndexEndpoint_ReturnsNullAsync()
        {
            var options = GetTestExtensionBundleOptions(BundleId, "[1.*, 2.0.0)");
            var httpClient = new HttpClient(new MockHttpHandler(statusCodeForIndexJson: HttpStatusCode.NotFound, statusCodeForZipFile: HttpStatusCode.OK));
            var manager = GetExtensionBundleManager(options, GetAppServiceEnvironment());
            Assert.Null(await manager.GetExtensionBundle(httpClient));
        }

        [Fact]
        public async Task GetExtensionBundle_CannotReachZipEndpoint_ReturnsFalseAsync()
        {
            var options = GetTestExtensionBundleOptions(BundleId, "[1.*, 2.0.0)");
            var httpClient = new HttpClient(new MockHttpHandler(statusCodeForIndexJson: HttpStatusCode.OK, statusCodeForZipFile: HttpStatusCode.NotFound));
            var manager = GetExtensionBundleManager(options, GetAppServiceEnvironment());
            Assert.Null(await manager.GetExtensionBundle(httpClient));
        }

        private ExtensionBundleManager GetExtensionBundleManager(ExtensionBundleOptions bundleOptions, TestEnvironment environment = null)
        {
            environment = environment ?? new TestEnvironment();
            var options = new OptionsWrapper<ExtensionBundleOptions>(bundleOptions);
            return new ExtensionBundleManager(environment, options, NullLogger<ExtensionBundleManager>.Instance);
        }

        private TestEnvironment GetAppServiceEnvironment()
        {
            var environment = new TestEnvironment();
            environment.SetEnvironmentVariable(AzureWebsiteInstanceId, Guid.NewGuid().ToString("N"));
            string downloadPath = string.Empty;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                environment.SetEnvironmentVariable(AzureWebsiteHomePath, "D:\\home");
            }
            else
            {
                environment.SetEnvironmentVariable(AzureWebsiteHomePath, "//home");
            }
            return environment;
        }

        private ExtensionBundleOptions GetTestExtensionBundleOptions(string id, string version)
        {
            List<string> probingPaths = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                                    ? new List<string>
                                    {
                                        @"C:\Program Files (x86)\FuncExtensionBundles\Microsoft.Azure.Functions.ExtensionBundle"
                                    }
                                    : new List<string>
                                    {
                                        "/FuncExtensionBundles/Microsoft.Azure.Functions.ExtensionBundle",
                                        "/home/site/wwwroot/.azureFunctions/ExtensionBundles/Microsoft.Azure.Functions.ExtensionBundle"
                                    };

            return new ExtensionBundleOptions
            {
                Id = id,
                Version = VersionRange.Parse(version, true),
                ProbingPaths = probingPaths,
                DownloadPath = _downloadPath
            };
        }

        private Tuple<Mock<IFileSystem>, Mock<DirectoryBase>, Mock<FileBase>> CreateFileSystem()
        {
            var fileSystem = new Mock<IFileSystem>();
            var fileBase = new Mock<FileBase>();
            var dirBase = new Mock<DirectoryBase>();
            fileSystem.SetupGet(f => f.Directory).Returns(dirBase.Object);
            fileSystem.SetupGet(f => f.File).Returns(fileBase.Object);
            return new Tuple<Mock<IFileSystem>, Mock<DirectoryBase>, Mock<FileBase>>(fileSystem, dirBase, fileBase);
        }

        public void Dispose()
        {
            FileUtility.Instance = null;
        }

        private class MockHttpHandler : HttpClientHandler
        {
            private HttpStatusCode _statusCodeForIndexJson;
            private HttpStatusCode _statusCodeForZipFile;

            public MockHttpHandler(HttpStatusCode statusCodeForIndexJson, HttpStatusCode statusCodeForZipFile)
            {
                _statusCodeForIndexJson = statusCodeForIndexJson;
                _statusCodeForZipFile = statusCodeForZipFile;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(1);
                var response = new HttpResponseMessage();
                if (request.RequestUri.AbsolutePath.EndsWith("index.json"))
                {
                    response.Content = _statusCodeForIndexJson == HttpStatusCode.OK
                                       ? new StringContent("[ \"1.0.0\", \"1.0.1\", \"2.0.0\" ]")
                                       : null;
                    response.StatusCode = _statusCodeForIndexJson;
                }
                else
                {
                    response.Content = _statusCodeForZipFile == HttpStatusCode.OK
                                       ? GetBundleZip()
                                       : null;
                    response.StatusCode = _statusCodeForZipFile;
                }
                return response;
            }

            private StreamContent GetBundleZip()
            {
                var stream = new MemoryStream();
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    var file = zip.CreateEntry("bundle.json");
                    using (var entryStream = file.Open())
                    using (var streamWriter = new StreamWriter(entryStream))
                    {
                        streamWriter.Write(" { id: \"Microsoft.Azure.Functions.ExtensionBundle\" }");
                    }
                }
                stream.Seek(0, SeekOrigin.Begin);
                return new StreamContent(stream);
            }
        }
    }
}