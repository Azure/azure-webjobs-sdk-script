// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Azure.WebJobs.Script.Abstractions;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestPlatform.PlatformAbstractions.Interfaces;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Rpc
{
    public class WorkerConfigFactoryTests : IDisposable
    {
        private TestSystemRuntimeInformation testSysRuntimeInfo = new TestSystemRuntimeInformation();
        private TestEnvironment environment;

        public WorkerConfigFactoryTests()
        {
            environment = new TestEnvironment();
        }

        public void Dispose()
        {
            environment.Clear();
        }

        [Fact]
        public void DefaultLanguageWorkersDir()
        {
            var expectedWorkersDir = Path.Combine(Path.GetDirectoryName(new Uri(typeof(WorkerConfigFactory).Assembly.CodeBase).LocalPath), LanguageWorkerConstants.DefaultWorkersDirectoryName);
            var config = new ConfigurationBuilder().Build();
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);
            Assert.Equal(expectedWorkersDir, configFactory.WorkersDirPath);
        }

        [Fact]
        public void LanguageWorker_WorkersDir_Set()
        {
            var expectedWorkersDir = @"d:\testWorkersDir";
            var config = new ConfigurationBuilder()
                   .AddInMemoryCollection(new Dictionary<string, string>
                   {
                       [$"{LanguageWorkerConstants.LanguageWorkersSectionName}:{LanguageWorkerConstants.WorkersDirectorySectionName}"] = expectedWorkersDir
                   })
                   .Build();
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);
            Assert.Equal(expectedWorkersDir, configFactory.WorkersDirPath);
        }

        [Fact]
        public void LanguageWorker_WorkersDir_NotSet()
        {
            var expectedWorkersDir = Path.Combine(Path.GetDirectoryName(new Uri(typeof(WorkerConfigFactory).Assembly.CodeBase).LocalPath), LanguageWorkerConstants.DefaultWorkersDirectoryName);
            var configBuilder = ScriptSettingsManager.CreateDefaultConfigurationBuilder()
                   .AddInMemoryCollection(new Dictionary<string, string>
                   {
                       ["languageWorker"] = "test"
                   });
            var config = configBuilder.Build();
            var scriptSettingsManager = new ScriptSettingsManager(config);
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);
            Assert.Equal(expectedWorkersDir, configFactory.WorkersDirPath);
        }

        [Fact]
        public void JavaPath_AppServiceEnv()
        {
            var configBuilder = ScriptSettingsManager.CreateDefaultConfigurationBuilder()
                  .AddInMemoryCollection(new Dictionary<string, string>
                  {
                      ["languageWorker"] = "test"
                  });
            var config = configBuilder.Build();
            var scriptSettingsManager = new ScriptSettingsManager(config);
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);
            var testEnvVariables = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.AzureWebsiteInstanceId, "123" },
                { "JAVA_HOME", @"D:\Program Files\Java\jdk1.7.0_51" }
            };
            using (var variables = new TestScopedSettings(scriptSettingsManager, testEnvVariables))
            {
                var javaPath = configFactory.GetExecutablePathForJava("../../zulu8.23.0.3-jdk8.0.144-win_x64/bin/java");
                Assert.Equal(@"D:\Program Files\Java\zulu8.23.0.3-jdk8.0.144-win_x64\bin\java", javaPath);
            }
        }

        [Fact]
        public void JavaPath_AppServiceEnv_JavaHomeSet_AppServiceEnvOverrides()
        {
            var configBuilder = ScriptSettingsManager.CreateDefaultConfigurationBuilder()
                  .AddInMemoryCollection(new Dictionary<string, string>
                  {
                      ["languageWorker"] = "test"
                  });
            var config = configBuilder.Build();
            var scriptSettingsManager = new ScriptSettingsManager(config);
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);
            var testEnvVariables = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.AzureWebsiteInstanceId, "123" },
                { "JAVA_HOME", @"D:\Program Files\Java\zulu8.31.0.2-jre8.0.181-win_x64" }
            };
            using (var variables = new TestScopedSettings(scriptSettingsManager, testEnvVariables))
            {
                var javaPath = configFactory.GetExecutablePathForJava("../../zulu8.23.0.3-jdk8.0.144-win_x64/bin/java");
                Assert.Equal(@"D:\Program Files\Java\zulu8.23.0.3-jdk8.0.144-win_x64\bin\java", javaPath);
            }
        }

        [Fact]
        public void JavaPath_JavaHome_Set()
        {
            var configBuilder = ScriptSettingsManager.CreateDefaultConfigurationBuilder()
                  .AddInMemoryCollection(new Dictionary<string, string>
                  {
                      ["languageWorker"] = "test"
                  });
            var config = configBuilder.Build();
            var scriptSettingsManager = new ScriptSettingsManager(config);
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);
            var testEnvVariables = new Dictionary<string, string>
            {
                { "JAVA_HOME", @"D:\Program Files\Java\jdk1.7.0_51" }
            };
            using (var variables = new TestScopedSettings(scriptSettingsManager, testEnvVariables))
            {
                var javaPath = configFactory.GetExecutablePathForJava("java");
                Assert.Equal(@"D:\Program Files\Java\jdk1.7.0_51\bin\java", javaPath);
            }
        }

        [Fact]
        public void JavaPath_JavaHome_Set_DefaultExePathSet()
        {
            var configBuilder = ScriptSettingsManager.CreateDefaultConfigurationBuilder()
                  .AddInMemoryCollection(new Dictionary<string, string>
                  {
                      ["languageWorker"] = "test"
                  });
            var config = configBuilder.Build();
            var scriptSettingsManager = new ScriptSettingsManager(config);
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);
            var testEnvVariables = new Dictionary<string, string>
            {
                { "JAVA_HOME", @"D:\Program Files\Java\jdk1.7.0_51" }
            };
            using (var variables = new TestScopedSettings(scriptSettingsManager, testEnvVariables))
            {
                var javaPath = configFactory.GetExecutablePathForJava(@"D:\MyCustomPath\Java");
                Assert.Equal(@"D:\MyCustomPath\Java", javaPath);
            }
        }

        [Fact]
        public void JavaPath_JavaHome_NotSet()
        {
            var configBuilder = ScriptSettingsManager.CreateDefaultConfigurationBuilder()
                  .AddInMemoryCollection(new Dictionary<string, string>
                  {
                      ["languageWorker"] = "test"
                  });
            var config = configBuilder.Build();
            var scriptSettingsManager = new ScriptSettingsManager(config);
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);
            var testEnvVariables = new Dictionary<string, string>
            {
                { "JAVA_HOME", string.Empty }
            };
            using (var variables = new TestScopedSettings(scriptSettingsManager, testEnvVariables))
            {
                var javaPath = configFactory.GetExecutablePathForJava("java");
                Assert.Equal("java", javaPath);
            }
        }

        [Theory]
        [InlineData("-Djava.net.preferIPv4Stack = true", "-Djava.net.preferIPv4Stack = true")]
        [InlineData("-agentlib:jdwp=transport=dt_socket,server=y,suspend=n,address=5005", "")]
        public void AddArgumentsFromAppSettings_JavaOpts(string expectedArgument, string javaOpts)
        {
            WorkerDescription workerDescription = new WorkerDescription()
            {
                Arguments = new List<string>() { "-jar" },
                DefaultExecutablePath = "java",
                DefaultWorkerPath = "javaworker.jar",
                Extensions = new List<string>() { ".jar" },
                Language = "java"
            };
            var configBuilder = ScriptSettingsManager.CreateDefaultConfigurationBuilder()
                  .AddInMemoryCollection(new Dictionary<string, string>
                  {
                      ["languageWorker"] = "test",
                      ["languageWorkers:java:arguments"] = "-agentlib:jdwp=transport=dt_socket,server=y,suspend=n,address=5005"
                  });
            var config = configBuilder.Build();
            var scriptSettingsManager = new ScriptSettingsManager(config);
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);
            var languageSection = config.GetSection("languageWorkers:java");
            var testEnvVariables = new Dictionary<string, string>
            {
                { "JAVA_OPTS", javaOpts }
            };
            using (var variables = new TestScopedSettings(scriptSettingsManager, testEnvVariables))
            {
                WorkerConfigFactory.AddArgumentsFromAppSettings(workerDescription, languageSection);
                Assert.Equal(2, workerDescription.Arguments.Count);
                Assert.Equal(expectedArgument, workerDescription.Arguments[1]);
            }
        }

        [Theory]
        [InlineData("%FUNCTIONS_WORKER_RUNTIME_VERSION%/{os}/{architecture}", "3.7/LINUX/X64")]
        [InlineData("%FUNCTIONS_WORKER_RUNTIME_VERSION%/{architecture}", "3.7/X64")]
        [InlineData("%FUNCTIONS_WORKER_RUNTIME_VERSION%/{os}", "3.7/LINUX")]
        public void LanguageWorker_HydratedWorkerPath_EnvironmentVersionSet(string defaultWorkerPath, string expectedPath)
        {
            environment.SetEnvironmentVariable(LanguageWorkerConstants.FunctionWorkerRuntimeVersionSettingName, "3.7");

            WorkerDescription workerDescription = new WorkerDescription()
            {
                Arguments = new List<string>(),
                DefaultExecutablePath = "python",
                DefaultWorkerPath = defaultWorkerPath,
                WorkerDirectory = string.Empty,
                Extensions = new List<string>() { ".py" },
                Language = "python"
            };
            var configBuilder = ScriptSettingsManager.CreateDefaultConfigurationBuilder()
                  .AddInMemoryCollection(new Dictionary<string, string>
                  {
                      ["languageWorker"] = "test"
                  });
            var config = configBuilder.Build();
            var scriptSettingsManager = new ScriptSettingsManager(config);
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);

            Assert.Equal(expectedPath, configFactory.GetHydratedWorkerPath(workerDescription));
        }

        [Theory]
        [InlineData("%FUNCTIONS_WORKER_RUNTIME_VERSION%/{os}/{architecture}", "3.6/LINUX/X64")]
        [InlineData("%FUNCTIONS_WORKER_RUNTIME_VERSION%/{architecture}", "3.6/X64")]
        [InlineData("%FUNCTIONS_WORKER_RUNTIME_VERSION%/{os}", "3.6/LINUX")]
        public void LanguageWorker_HydratedWorkerPath_EnvironmentVersionNotSet(string defaultWorkerPath, string expectedPath)
        {
            // We fall back to the default version when this is not set
            // Environment.SetEnvironmentVariable(LanguageWorkerConstants.FunctionWorkerRuntimeVersionSettingName, "3.7");

            WorkerDescription workerDescription = new WorkerDescription()
            {
                Arguments = new List<string>(),
                DefaultExecutablePath = "python",
                DefaultWorkerPath = defaultWorkerPath,
                WorkerDirectory = string.Empty,
                Extensions = new List<string>() { ".py" },
                Language = "python",
                DefaultLanguageVersion = "3.6"
            };
            var configBuilder = ScriptSettingsManager.CreateDefaultConfigurationBuilder()
                  .AddInMemoryCollection(new Dictionary<string, string>
                  {
                      ["languageWorker"] = "test"
                  });
            var config = configBuilder.Build();
            var scriptSettingsManager = new ScriptSettingsManager(config);
            var testLogger = new TestLogger("test");
            var configFactory = new WorkerConfigFactory(config, testLogger, testSysRuntimeInfo, environment);

            Assert.Equal(expectedPath, configFactory.GetHydratedWorkerPath(workerDescription));
        }
    }
}