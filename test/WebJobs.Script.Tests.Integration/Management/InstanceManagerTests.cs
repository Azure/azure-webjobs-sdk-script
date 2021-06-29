// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.WebJobs.Script.Tests;
using Moq;
using Moq.Protected;
using Newtonsoft.Json;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Managment
{
    [Trait(TestTraits.Category, TestTraits.EndToEnd)]
    [Trait(TestTraits.Group, TestTraits.ContainerInstanceTests)]
    public class InstanceManagerTests : IDisposable
    {
        private const int assignmentWaitPeriod = 2000; //ms

        private readonly TestLoggerProvider _loggerProvider;
        private readonly TestEnvironmentEx _environment;
        private readonly ScriptWebHostEnvironment _scriptWebEnvironment;
        private readonly InstanceManager _instanceManager;
        private readonly Mock<IMeshServiceClient> _meshServiceClientMock;
        private readonly HttpClient _httpClient;
        private readonly LoggerFactory _loggerFactory = new LoggerFactory();
        private readonly TestOptionsFactory<ScriptApplicationHostOptions> _optionsFactory = new TestOptionsFactory<ScriptApplicationHostOptions>(new ScriptApplicationHostOptions() { ScriptPath = Path.GetTempPath() });

        public InstanceManagerTests()
        {
            _httpClient = new HttpClient();

            _loggerProvider = new TestLoggerProvider();
            _loggerFactory.AddProvider(_loggerProvider);

            _environment = new TestEnvironmentEx();
            _scriptWebEnvironment = new ScriptWebHostEnvironment(_environment);
            _meshServiceClientMock = new Mock<IMeshServiceClient>(MockBehavior.Strict);

            _instanceManager = new InstanceManager(_optionsFactory, _httpClient, _scriptWebEnvironment, _environment,
                _loggerFactory.CreateLogger<InstanceManager>(), new TestMetricsLogger(), _meshServiceClientMock.Object);

            InstanceManager.Reset();
        }

        [Fact]
        public async Task StartAssignment_AppliesAssignmentContext()
        {
            var envValue = new
            {
                Name = Path.GetTempFileName().Replace(".", string.Empty),
                Value = Guid.NewGuid().ToString()
            };
            var allowedOrigins = new string[]
            {
                "https://functions.azure.com",
                "https://functions-staging.azure.com",
                "https://functions-next.azure.com"
            };
            var supportCredentials = true;

            _environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            var context = new HostAssignmentContext
            {
                Environment = new Dictionary<string, string>
                {
                    { envValue.Name, envValue.Value }
                },
                CorsSettings = new CorsSettings
                {
                    AllowedOrigins = allowedOrigins,
                    SupportCredentials = supportCredentials,
                },
                IsWarmupRequest = false
            };
            bool result = _instanceManager.StartAssignment(context);
            Assert.True(result);
            Assert.True(_scriptWebEnvironment.InStandbyMode);

            // specialization is done in the background
            await Task.Delay(500);

            var value = _environment.GetEnvironmentVariable(envValue.Name);
            Assert.Equal(value, envValue.Value);

            var supportCredentialsValue = _environment.GetEnvironmentVariable(EnvironmentSettingNames.CorsSupportCredentials);
            Assert.Equal(supportCredentialsValue, supportCredentials.ToString());

            var allowedOriginsValue = _environment.GetEnvironmentVariable(EnvironmentSettingNames.CorsAllowedOrigins);
            Assert.Equal(allowedOriginsValue, JsonConvert.SerializeObject(allowedOrigins));

            // verify logs
            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Collection(logs,
                p => Assert.StartsWith("Starting Assignment", p),
                p => Assert.StartsWith("Applying 1 app setting(s)", p),
                p => Assert.StartsWith("Triggering specialization", p));

            // calling again should return false, since we're no longer
            // in placeholder mode
            _loggerProvider.ClearAllLogMessages();
            result = _instanceManager.StartAssignment(context);
            Assert.False(result);

            logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Collection(logs,
                p => Assert.StartsWith("Assign called while host is not in placeholder mode", p));
        }

        [Fact]
        public async Task StartAssignment_Failure_ExitsPlaceholderMode()
        {
            _environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            var context = new HostAssignmentContext
            {
                Environment = new Dictionary<string, string>
                {
                    // force the assignment to fail
                    { "throw", "test" }
                },
                IsWarmupRequest = false
            };

            _meshServiceClientMock.Setup(c => c.NotifyHealthEvent(ContainerHealthEventType.Fatal,
                It.Is<Type>(t => t == typeof(InstanceManager)), "Assign failed")).Returns(Task.CompletedTask);

            bool result = _instanceManager.StartAssignment(context);
            Assert.True(result);
            Assert.True(_scriptWebEnvironment.InStandbyMode);

            await TestHelpers.Await(() => !_scriptWebEnvironment.InStandbyMode, timeout: 5000);

            var error = _loggerProvider.GetAllLogMessages().First(p => p.Level == LogLevel.Error);
            Assert.Equal("Assign failed", error.FormattedMessage);
            Assert.Equal("Kaboom!", error.Exception.Message);

            _meshServiceClientMock.Verify(c => c.NotifyHealthEvent(ContainerHealthEventType.Fatal,
                It.Is<Type>(t => t == typeof(InstanceManager)), "Assign failed"), Times.Once);
        }

        [Fact]
        public async Task StartAssignment_Succeeds_With_No_RunFromPackage_AppSetting()
        {
            _environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            var context = new HostAssignmentContext
            {
                Environment = new Dictionary<string, string>(),
                IsWarmupRequest = false
            };
            bool result = _instanceManager.StartAssignment(context);
            Assert.True(result);
            Assert.True(_scriptWebEnvironment.InStandbyMode);

            await TestHelpers.Await(() => !_scriptWebEnvironment.InStandbyMode, timeout: 5000);

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Collection(logs,
                p => Assert.StartsWith("Starting Assignment", p),
                p => Assert.StartsWith("Applying 0 app setting(s)", p),
                p => Assert.StartsWith("Triggering specialization", p));
        }

        [Fact]
        public async void StartAssignment_Succeeds_With_NonEmpty_ScmRunFromPackage_Blob()
        {
            var contentRoot = Path.Combine(Path.GetTempPath(), @"FunctionsTest");
            var zipFilePath = Path.Combine(contentRoot, "content.zip");
            await TestHelpers.CreateContentZip(contentRoot, zipFilePath, Path.Combine(@"TestScripts", "DotNet"));

            IConfiguration configuration = TestHelpers.GetTestConfiguration();
            string connectionString = configuration.GetWebJobsConnectionString(ConnectionStringNames.Storage);
            Uri sasUri = await TestHelpers.CreateBlobSas(connectionString, zipFilePath, "scm-run-from-pkg-test", "NonEmpty.zip");

            _environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            var context = new HostAssignmentContext
            {
                Environment = new Dictionary<string, string>()
                {
                    { EnvironmentSettingNames.ScmRunFromPackage, sasUri.ToString() }
                },
                IsWarmupRequest = false
            };
            bool result = _instanceManager.StartAssignment(context);
            Assert.True(result);

            Thread.Sleep(assignmentWaitPeriod);

            Assert.False(_scriptWebEnvironment.InStandbyMode);

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            if (logs.Length == 10)
            {
                Assert.Collection(logs,
                    p => Assert.StartsWith("Starting Assignment", p),
                    p => Assert.StartsWith("Applying 1 app setting(s)", p),
                    p => Assert.StartsWith("Downloading zip contents from", p),
                    p => Assert.EndsWith(" bytes downloaded. IsWarmupRequest = False", p),
                    p => Assert.EndsWith(" bytes written. IsWarmupRequest = False", p),
                    p => Assert.StartsWith("Running: ", p),
                    p => Assert.StartsWith("Output:", p),
                    p => Assert.True(true), // this line varies depending on whether WSL is on the machine; just ignore it
                    p => Assert.StartsWith("exitCode:", p),
                    p => Assert.StartsWith("Triggering specialization", p));
            }
            else
            {
                Assert.Collection(logs,
                    p => Assert.StartsWith("Starting Assignment", p),
                    p => Assert.StartsWith("Applying 1 app setting(s)", p),
                    p => Assert.StartsWith("Downloading zip contents from", p),
                    p => Assert.EndsWith(" bytes downloaded. IsWarmupRequest = False", p),
                    p => Assert.EndsWith(" bytes written. IsWarmupRequest = False", p),
                    p => Assert.StartsWith("Running: ", p),
                    p => Assert.StartsWith("Error running bash", p),
                    p => Assert.StartsWith("Triggering specialization", p));
            }
        }

        [Fact]
        public async void StartAssignment_Does_Not_Assign_Settings_For_Warmup_Request()
        {
            var contentRoot = Path.Combine(Path.GetTempPath(), @"FunctionsTest");
            var zipFilePath = Path.Combine(contentRoot, "content.zip");
            await TestHelpers.CreateContentZip(contentRoot, zipFilePath, Path.Combine(@"TestScripts", "DotNet"));

            IConfiguration configuration = TestHelpers.GetTestConfiguration();
            string connectionString = configuration.GetWebJobsConnectionString(ConnectionStringNames.Storage);
            Uri sasUri = await TestHelpers.CreateBlobSas(connectionString, zipFilePath, "scm-run-from-pkg-test", "NonEmpty.zip");

            _environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            var context = new HostAssignmentContext
            {
                Environment = new Dictionary<string, string>()
                {
                    { EnvironmentSettingNames.ScmRunFromPackage, sasUri.ToString() }
                },
                IsWarmupRequest = true
            };
            bool result = _instanceManager.StartAssignment(context);
            Assert.True(result);

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.False(logs.Any(l => l.StartsWith("Starting Assignment.")));
        }

        [Fact]
        public async void StartAssignment_Succeeds_With_Empty_ScmRunFromPackage_Blob()
        {
            IConfiguration configuration = TestHelpers.GetTestConfiguration();
            string connectionString = configuration.GetWebJobsConnectionString(ConnectionStringNames.Storage);
            Uri sasUri = await TestHelpers.CreateBlobSas(connectionString, string.Empty, "scm-run-from-pkg-test", "Empty.zip");

            _environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            var context = new HostAssignmentContext
            {
                Environment = new Dictionary<string, string>()
                {
                    { EnvironmentSettingNames.ScmRunFromPackage, sasUri.ToString() }
                },
                IsWarmupRequest = false
            };
            bool result = _instanceManager.StartAssignment(context);
            Assert.True(result);

            Thread.Sleep(assignmentWaitPeriod);

            Assert.False(_scriptWebEnvironment.InStandbyMode);

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Collection(logs,
                p => Assert.StartsWith("Starting Assignment", p),
                p => Assert.StartsWith("Applying 1 app setting(s)", p),
                p => Assert.StartsWith($"{EnvironmentSettingNames.ScmRunFromPackage} points to an empty location. Function app has no content.", p),
                p => Assert.StartsWith("Triggering specialization", p));
        }

        [Fact]
        public void StartAssignment_ReturnsFalse_WhenNotInStandbyMode()
        {
            Assert.False(SystemEnvironment.Instance.IsPlaceholderModeEnabled());

            var context = new HostAssignmentContext();
            context.Environment = new Dictionary<string, string>();
            context.IsWarmupRequest = false;
            bool result = _instanceManager.StartAssignment(context);
            Assert.False(result);
        }

        [Fact]
        public async Task ValidateContext_InvalidZipUrl_WebsiteUseZip_ReturnsError()
        {
            var environmentSettings = new Dictionary<string, string>()
            {
                { EnvironmentSettingNames.AzureWebsiteZipDeployment, "http://invalid.com/invalid/dne" }
            };

            var environment = new TestEnvironment();
            foreach (var (key, value) in environmentSettings)
            {
                environment.SetEnvironmentVariable(key, value);
            }

            var scriptWebEnvironment = new ScriptWebHostEnvironment(environment);

            var loggerFactory = new LoggerFactory();
            var loggerProvider = new TestLoggerProvider();
            loggerFactory.AddProvider(loggerProvider);

            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

            handlerMock.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()).ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound
            });

            var instanceManager = new InstanceManager(_optionsFactory, new HttpClient(handlerMock.Object),
                scriptWebEnvironment, environment, loggerFactory.CreateLogger<InstanceManager>(),
                new TestMetricsLogger(), null);

            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environmentSettings,
                IsWarmupRequest = false
            };

            string error = await instanceManager.ValidateContext(assignmentContext);
            Assert.Equal("Invalid zip url specified (StatusCode: NotFound)", error);

            var logs = loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Collection(logs,
                p => Assert.StartsWith("Validating host assignment context (SiteId: 1234, SiteName: 'TestSite'. IsWarmup: 'False')", p),
                p => Assert.StartsWith($"Will be using {EnvironmentSettingNames.AzureWebsiteZipDeployment} app setting as zip url. IsWarmup: 'False'", p),
                p => Assert.StartsWith("linux.container.specialization.zip.head failed", p),
                p => Assert.StartsWith("linux.container.specialization.zip.head failed", p),
                p => Assert.StartsWith("linux.container.specialization.zip.head failed", p),
                p => Assert.StartsWith("ValidateContext failed", p));
        }

        [Fact]
        public async Task ValidateContext_EmptyZipUrl_ReturnsSuccess()
        {
            var environment = new Dictionary<string, string>();
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment,
                IsWarmupRequest = false
            };

            string error = await _instanceManager.ValidateContext(assignmentContext);
            Assert.Null(error);

            string[] expectedOutputLines =
            {
                "Validating host assignment context (SiteId: 1234, SiteName: 'TestSite'. IsWarmup: 'False')",
                $"Will be using  app setting as zip url. IsWarmup: 'False"
            };

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();

            for (int i = 0; i < expectedOutputLines.Length; i++)
            {
                Assert.StartsWith(expectedOutputLines[i], logs[i]);
            }
        }

        [Fact]
        public async Task ValidateContext_Succeeds_For_WebsiteUseZip_Only()
        {
            var environment = new Dictionary<string, string>()
            {
                { EnvironmentSettingNames.AzureWebsiteZipDeployment, "http://microsoft.com" }
            };
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment,
                IsWarmupRequest = false
            };

            string error = await _instanceManager.ValidateContext(assignmentContext);
            Assert.Null(error);

            string[] expectedOutputLines =
            {
                "Validating host assignment context (SiteId: 1234, SiteName: 'TestSite'. IsWarmup: 'False')",
                $"Will be using {EnvironmentSettingNames.AzureWebsiteZipDeployment} app setting as zip url. IsWarmup: 'False"
            };

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();

            for (int i = 0; i < expectedOutputLines.Length; i++)
            {
                Assert.StartsWith(expectedOutputLines[i], logs[i]);
            }
        }

        [Fact]
        public async Task ValidateContext_Succeeds_For_ScmBuildPackage_Only()
        {
            var environment = new Dictionary<string, string>()
            {
                { EnvironmentSettingNames.ScmRunFromPackage,
                    "https://notarealstorageaccount.blob.core.windows.net/releases/test.zip?st=2019-05-22T15%3A00%3A09Z&se=2099-05-23T15%3A00%3A00Z&sp=rwl&sv=2018-03-28&sr=b&sig=d%2F7gP6ZGXvv%2RfHegvbwO88HaX0URZ%2BbXR6WGK%2BpcZE4%3D" }
            };
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment,
                IsWarmupRequest = false
            };

            string error = await _instanceManager.ValidateContext(assignmentContext);
            Assert.Null(error);

            string[] expectedOutputLines =
            {
                "Validating host assignment context (SiteId: 1234, SiteName: 'TestSite'. IsWarmup: 'False')",
                $"Will be using {EnvironmentSettingNames.ScmRunFromPackage} app setting as zip url. IsWarmup: 'False"
            };

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();

            for (int i = 0; i < expectedOutputLines.Length; i++)
            {
                Assert.StartsWith(expectedOutputLines[i], logs[i]);
            }
        }

        [Fact]
        public async Task ValidateContext_Succeeds_For_WebsiteUseZip_With_ScmPackageDefined()
        {
            var environment = new Dictionary<string, string>()
            {
                { EnvironmentSettingNames.AzureWebsiteZipDeployment, "http://microsoft.com" },
                { EnvironmentSettingNames.ScmRunFromPackage, "http://microsoft.com" }
            };
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment,
                IsWarmupRequest = false
            };

            string error = await _instanceManager.ValidateContext(assignmentContext);
            Assert.Null(error);

            string[] expectedOutputLines =
            {
                "Validating host assignment context (SiteId: 1234, SiteName: 'TestSite'. IsWarmup: 'False')",
                $"Will be using {EnvironmentSettingNames.AzureWebsiteZipDeployment} app setting as zip url. IsWarmup: 'False"
            };

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();

            for (int i = 0; i < expectedOutputLines.Length; i++)
            {
                Assert.StartsWith(expectedOutputLines[i], logs[i]);
            }
        }

        [Fact]
        public async Task SpecializeMSISidecar_EmptyMSIEndpoint_NoOp()
        {
            var environment = new Dictionary<string, string>()
            {
                { EnvironmentSettingNames.MsiEndpoint, "" }
            };
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment,
                IsWarmupRequest = false
            };

            string error = await _instanceManager.SpecializeMSISidecar(assignmentContext);
            Assert.Null(error);

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Collection(logs, p => Assert.StartsWith("MSI enabled status: False", p));
        }

        [Fact]
        public async Task SpecializeMSISidecar_Succeeds()
        {
            var environment = new Dictionary<string, string>()
            {
                { EnvironmentSettingNames.MsiEndpoint, "http://localhost:8081" },
                { EnvironmentSettingNames.MsiSecret, "secret" }
            };
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment,
                IsWarmupRequest = false,
                MSIContext = new MSIContext()
            };

            var instanceManager = GetInstanceManagerForMSISpecialization(assignmentContext, HttpStatusCode.OK, null);

            string error = await instanceManager.SpecializeMSISidecar(assignmentContext);
            Assert.Null(error);

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Collection(logs,
                p => Assert.StartsWith("MSI enabled status: True", p),
                p => Assert.StartsWith("Specializing sidecar at http://localhost:8081", p),
                p => Assert.StartsWith("Specialize MSI sidecar returned OK", p));
        }

        [Fact]
        public async Task SpecializeMSISidecar_NoOp_ForWarmup_Request()
        {
            var environment = new Dictionary<string, string>()
            {
                { EnvironmentSettingNames.MsiEndpoint, "http://localhost:8081" },
                { EnvironmentSettingNames.MsiSecret, "secret" }
            };
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment,
                IsWarmupRequest = true
            };

            var instanceManager = GetInstanceManagerForMSISpecialization(assignmentContext, HttpStatusCode.OK, null);

            string error = await instanceManager.SpecializeMSISidecar(assignmentContext);
            Assert.Null(error);

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Empty(logs);
        }

        [Fact]
        public async Task SpecializeMSISidecar_Fails()
        {
            var environment = new Dictionary<string, string>()
            {
                { EnvironmentSettingNames.MsiEndpoint, "http://localhost:8081" },
                { EnvironmentSettingNames.MsiSecret, "secret" }
            };
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment,
                IsWarmupRequest = false,
                MSIContext = new MSIContext()
            };

            var instanceManager = GetInstanceManagerForMSISpecialization(assignmentContext, HttpStatusCode.BadRequest, null);

            string error = await instanceManager.SpecializeMSISidecar(assignmentContext);
            Assert.NotNull(error);

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Collection(logs,
                p => Assert.StartsWith("MSI enabled status: True", p),
                p => Assert.StartsWith("Specializing sidecar at http://localhost:8081", p),
                p => Assert.StartsWith("Specialize MSI sidecar returned BadRequest", p),
                p => Assert.StartsWith("Specialize MSI sidecar call failed. StatusCode=BadRequest", p));
        }

        [Fact]

        public async Task Mounts_Valid_BYOS_Accounts()
        {
            _environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");

            const string account1 = "storageaccount1";
            const string share1 = "share1";
            const string accessKey1 = "key1key1key1==";
            const string targetPath1 = "/data";

            const string account2 = "storageaccount2";
            const string share2 = "share2";
            const string accessKey2 = "key2key2key2==";
            const string targetPath2 = "/data/store2";

            const string account3 = "storageaccount3";
            const string share3 = "share3";
            const string accessKey3 = "key3key3key3==";
            const string targetPath3 = "/somepath";

            var hostAssignmentContext = new HostAssignmentContext()
            {
                Environment = new Dictionary<string, string>
                {
                    [EnvironmentSettingNames.MsiSecret] = "secret",
                    ["AZUREFILESSTORAGE_storage1"] = $"{account1}|{share1}|{accessKey1}|{targetPath1}",
                    ["AZUREFILESSTORAGE_storage2"] = $"{account2}|{share2}|{accessKey2}|{targetPath2}",
                    ["AZUREBLOBSTORAGE_blob1"] = $"{account3}|{share3}|{accessKey3}|{targetPath3}",
                    [EnvironmentSettingNames.MsiEndpoint] = "endpoint",
                },
                SiteId = 1234,
                SiteName = "TestSite",
                IsWarmupRequest = false
            };

            var meshInitServiceClient = new Mock<IMeshServiceClient>(MockBehavior.Strict);
            meshInitServiceClient.Setup(client =>
                    client.MountCifs(Utility.BuildStorageConnectionString(account1, accessKey1, CloudConstants.AzureStorageSuffix), share1, targetPath1))
                .Throws(new Exception("Mount failure"));
            meshInitServiceClient.Setup(client =>
                client.MountCifs(Utility.BuildStorageConnectionString(account2, accessKey2, CloudConstants.AzureStorageSuffix), share2, targetPath2)).Returns(Task.FromResult(true));
            meshInitServiceClient.Setup(client =>
                client.MountBlob(Utility.BuildStorageConnectionString(account3, accessKey3, CloudConstants.AzureStorageSuffix), share3, targetPath3)).Returns(Task.FromResult(true));

            var instanceManager = new InstanceManager(_optionsFactory, _httpClient, _scriptWebEnvironment, _environment,
                _loggerFactory.CreateLogger<InstanceManager>(), new TestMetricsLogger(), meshInitServiceClient.Object);

            instanceManager.StartAssignment(hostAssignmentContext);

            await TestHelpers.Await(() => !_scriptWebEnvironment.InStandbyMode, timeout: 5000);

            meshInitServiceClient.Verify(
                client => client.MountCifs(Utility.BuildStorageConnectionString(account1, accessKey1, CloudConstants.AzureStorageSuffix), share1,
                    targetPath1), Times.Exactly(2));
            meshInitServiceClient.Verify(
                client => client.MountCifs(Utility.BuildStorageConnectionString(account2, accessKey2, CloudConstants.AzureStorageSuffix), share2,
                    targetPath2), Times.Once);
            meshInitServiceClient.Verify(
                client => client.MountBlob(Utility.BuildStorageConnectionString(account3, accessKey3, CloudConstants.AzureStorageSuffix), share3,
                    targetPath3), Times.Once);
        }

        [Fact]
        public async Task DoesNotSpecializeMSISidecar_WhenMSIContextNull()
        {
            var environment = new Dictionary<string, string>()
            {
                { EnvironmentSettingNames.MsiEndpoint, "http://localhost:8081" },
                { EnvironmentSettingNames.MsiSecret, "secret" }
            };
            var assignmentContext = new HostAssignmentContext
            {
                SiteId = 1234,
                SiteName = "TestSite",
                Environment = environment,
                IsWarmupRequest = false,
                MSIContext = null
            };

            var instanceManager = GetInstanceManagerForMSISpecialization(assignmentContext, HttpStatusCode.BadRequest);

            string error = await instanceManager.SpecializeMSISidecar(assignmentContext);
            Assert.Null(error);

            var logs = _loggerProvider.GetAllLogMessages().Select(p => p.FormattedMessage).ToArray();
            Assert.Collection(logs,
                p => Assert.StartsWith("MSI enabled status: True", p),
                p => Assert.StartsWith("Skipping specialization of MSI sidecar since MSIContext was absent", p));
        }

        [Fact]
        public async Task Does_Not_Mount_Invalid_BYOS_Accounts()
        {
            _environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");

            const string account1 = "storageaccount1";
            const string share1 = "share1";
            const string accessKey1 = "key1key1key1==";
            const string targetPath1 = "/data";

            const string account2 = "storageaccount2";
            const string share2 = "share2";
            const string accessKey2 = "key2key2key2==";

            var hostAssignmentContext = new HostAssignmentContext()
            {
                Environment = new Dictionary<string, string>
                {
                    [EnvironmentSettingNames.MsiSecret] = "secret",
                    ["AZUREFILESSTORAGE_storage1"] = $"{account1}|{share1}|{accessKey1}|{targetPath1}",
                    ["AZUREFILESSTORAGE_storage2"] = $"{account2}|{share2}|{accessKey2}",
                    ["AZUREBLOBSTORAGE_blob1"] = $"",
                    [EnvironmentSettingNames.MsiEndpoint] = "endpoint",
                },
                SiteId = 1234,
                SiteName = "TestSite",
                IsWarmupRequest = false
            };

            var meshInitServiceClient = new Mock<IMeshServiceClient>(MockBehavior.Strict);

            meshInitServiceClient.Setup(client =>
                client.MountCifs(Utility.BuildStorageConnectionString(account1, accessKey1, CloudConstants.AzureStorageSuffix), share1, targetPath1)).Returns(Task.FromResult(true));

            var instanceManager = new InstanceManager(_optionsFactory, _httpClient, _scriptWebEnvironment, _environment,
                _loggerFactory.CreateLogger<InstanceManager>(), new TestMetricsLogger(), meshInitServiceClient.Object);

            instanceManager.StartAssignment(hostAssignmentContext);

            await Task.Delay(TimeSpan.FromSeconds(0.5));

            meshInitServiceClient.Verify(
                client => client.MountCifs(Utility.BuildStorageConnectionString(account1, accessKey1, CloudConstants.AzureStorageSuffix), share1,
                    targetPath1), Times.Once);

            meshInitServiceClient.Verify(
                client => client.MountCifs(It.IsAny<string>(), It.IsAny<string>(), It.Is<string>(s => s != targetPath1)), Times.Never());
        }

        private InstanceManager GetInstanceManagerForMSISpecialization(HostAssignmentContext hostAssignmentContext,
            HttpStatusCode httpStatusCode, IMeshServiceClient meshServiceClient)
        {
            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

            var msiEndpoint = hostAssignmentContext.Environment[EnvironmentSettingNames.MsiEndpoint] + ScriptConstants.LinuxMSISpecializationStem;

            handlerMock.Protected().Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.Method == HttpMethod.Post
                                                         && request.RequestUri.AbsoluteUri.Equals(msiEndpoint)
                                                         && request.Content != null),
                ItExpr.IsAny<CancellationToken>()).ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = httpStatusCode
                });

            InstanceManager.Reset();

            return new InstanceManager(_optionsFactory, new HttpClient(handlerMock.Object), _scriptWebEnvironment, _environment,
                _loggerFactory.CreateLogger<InstanceManager>(), new TestMetricsLogger(), meshServiceClient);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, null);
        }

        private class TestEnvironmentEx : TestEnvironment
        {
            public override void SetEnvironmentVariable(string name, string value)
            {
                if (name == "throw")
                {
                    throw new InvalidOperationException("Kaboom!");
                }
                base.SetEnvironmentVariable(name, value);
            }
        }
    }
}
