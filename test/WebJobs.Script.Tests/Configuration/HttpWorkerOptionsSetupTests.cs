// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.WebJobs.Script.Tests;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Configuration
{
    public class HttpWorkerOptionsSetupTests
    {
        private readonly TestEnvironment _environment = new TestEnvironment();
        private readonly TestLoggerProvider _loggerProvider = new TestLoggerProvider();
        private readonly TestMetricsLogger _metricsLogger = new TestMetricsLogger();
        private readonly string _hostJsonFile;
        private readonly string _rootPath;
        private readonly ScriptApplicationHostOptions _options;
        private ILoggerProvider _testLoggerProvider;
        private ILoggerFactory _testLoggerFactory;
        private ScriptJobHostOptions _scriptJobHostOptions;
        private static string _currentDirectory = Directory.GetCurrentDirectory();

        public HttpWorkerOptionsSetupTests()
        {
            _testLoggerProvider = new TestLoggerProvider();
            _testLoggerFactory = new LoggerFactory();
            _testLoggerFactory.AddProvider(_testLoggerProvider);
            _scriptJobHostOptions = new ScriptJobHostOptions()
            {
                RootScriptPath = $@"TestScripts\CSharp",
                FileLoggingMode = FileLoggingMode.Always,
                FunctionTimeout = TimeSpan.FromSeconds(3)
            };

            _rootPath = Path.Combine(Environment.CurrentDirectory, "HttpWorkerOptionsSetupTests");
            if (!Directory.Exists(_rootPath))
            {
                Directory.CreateDirectory(_rootPath);
            }

            _options = new ScriptApplicationHostOptions
            {
                ScriptPath = _rootPath
            };

            _hostJsonFile = Path.Combine(_rootPath, "host.json");
            if (File.Exists(_hostJsonFile))
            {
                File.Delete(_hostJsonFile);
            }
        }

        [Theory]
        [InlineData(@"{
                    'version': '2.0',
                    }")]
        [InlineData(@"{
                    'version': '2.0',
                    'httpWorker': {
                            'description': {
                                'defaultExecutablePath': 'testExe'
                            }
                        }
                    }")]
        [InlineData(@"{
                    'version': '2.0',
                    'customHandler': {
                            'description': {
                                'defaultExecutablePath': 'testExe'
                            }
                        }
                    }")]
        public void MissingOrValid_HttpWorkerConfig_DoesNotThrowException(string hostJsonContent)
        {
            File.WriteAllText(_hostJsonFile, hostJsonContent);
            var configuration = BuildHostJsonConfiguration();
            HttpWorkerOptionsSetup setup = new HttpWorkerOptionsSetup(new OptionsWrapper<ScriptJobHostOptions>(_scriptJobHostOptions), configuration, _testLoggerFactory, _metricsLogger);
            HttpWorkerOptions options = new HttpWorkerOptions();
            options.Description = new HttpWorkerDescription();
            options.Description.FileExists = path =>
            {
                return true;
            };
            setup.Configure(options);

            if (options.Description != null && !string.IsNullOrEmpty(options.Description.DefaultExecutablePath))
            {
                string expectedDefaultExecutablePath = Path.Combine(_scriptJobHostOptions.RootScriptPath, "testExe");
                Assert.Equal(expectedDefaultExecutablePath, options.Description.DefaultExecutablePath);
            }
        }

        [Theory]
        [InlineData(@"{
                    'version': '2.0',
                    'httpWorker': {
                            'invalid': {
                                'defaultExecutablePath': 'testExe'
                            }
                        }
                    }")]
        [InlineData(@"{
                    'version': '2.0',
                    'httpWorker': {
                            'description': {
                                'langauge': 'testExe'
                            }
                        }
                    }")]
        public void InValid_HttpWorkerConfig_Throws_Exception(string hostJsonContent)
        {
            File.WriteAllText(_hostJsonFile, hostJsonContent);
            var configuration = BuildHostJsonConfiguration();
            HttpWorkerOptionsSetup setup = new HttpWorkerOptionsSetup(new OptionsWrapper<ScriptJobHostOptions>(_scriptJobHostOptions), configuration, _testLoggerFactory, _metricsLogger);
            HttpWorkerOptions options = new HttpWorkerOptions();
            var ex = Record.Exception(() => setup.Configure(options));
            Assert.NotNull(ex);
            if (options.Description == null)
            {
                Assert.IsType<HostConfigurationException>(ex);
                Assert.Equal($"Missing worker Description.", ex.Message);
            }
            else
            {
                Assert.IsType<ValidationException>(ex);
                Assert.Equal($"WorkerDescription DefaultExecutablePath cannot be empty", ex.Message);
            }
        }

        [Fact]
        public void CustomHandlerConfig_ExpandEnvVars()
        {
            string hostJsonContent = @"{
                    'version': '2.0',
                    'customHandler': {
                            'description': {
                                'defaultExecutablePath': '%TestEnv%',
                                'defaultWorkerPath': '%TestEnv%'
                            }
                        }
                    }";
            try
            {
                Environment.SetEnvironmentVariable("TestEnv", "TestVal");
                File.WriteAllText(_hostJsonFile, hostJsonContent);
                var configuration = BuildHostJsonConfiguration();
                HttpWorkerOptionsSetup setup = new HttpWorkerOptionsSetup(new OptionsWrapper<ScriptJobHostOptions>(_scriptJobHostOptions), configuration, _testLoggerFactory, _metricsLogger);
                HttpWorkerOptions options = new HttpWorkerOptions();
                setup.Configure(options);
                Assert.Equal("TestVal", options.Description.DefaultExecutablePath);
                Assert.Contains("TestVal", options.Description.DefaultWorkerPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("TestEnv", string.Empty);
            }
        }

        [Theory]
        [InlineData(@"{
                    'version': '2.0',
                    'httpWorker': {
                            'description': {
                                'arguments': ['--xTest1 --xTest2'],
                                'defaultExecutablePath': 'node',
                                'defaultWorkerPath': 'httpWorker.js'
                            }
                        }
                    }", false, true, true)]
        [InlineData(@"{
                    'version': '2.0',
                    'httpWorker': {
                            'description': {
                                'arguments': ['--xTest1 --xTest2'],
                                'defaultExecutablePath': 'node'
                            }
                        }
                    }", false, false, false)]
        [InlineData(@"{
                    'version': '2.0',
                    'httpWorker': {
                            'description': {
                                'arguments': ['--xTest1 --xTest2'],
                                'defaultExecutablePath': 'c:/myruntime/node'
                            }
                        }
                    }", false, false, false)]
        [InlineData(@"{
                    'version': '2.0',
                    'httpWorker': {
                            'description': {
                                'arguments': ['--xTest1 --xTest2'],
                                'defaultExecutablePath': 'c:/myruntime/node',
                                'defaultWorkerPath': 'c:/myworkerPath/httpWorker.js'
                            }
                        }
                    }", false, false, true)]
        public void HttpWorker_Config_ExpectedValues(string hostJsonContent, bool appendCurrentDirectoryToExe, bool appendCurrentDirToWorkerPath, bool workerPathSet)
        {
            File.WriteAllText(_hostJsonFile, hostJsonContent);
            var configuration = BuildHostJsonConfiguration();
            HttpWorkerOptionsSetup setup = new HttpWorkerOptionsSetup(new OptionsWrapper<ScriptJobHostOptions>(_scriptJobHostOptions), configuration, _testLoggerFactory, _metricsLogger);
            HttpWorkerOptions options = new HttpWorkerOptions();
            setup.Configure(options);

            //Verify worker exe path is expected
            if (appendCurrentDirectoryToExe)
            {
                Assert.Equal(Path.Combine(_scriptJobHostOptions.RootScriptPath, "node"), options.Description.DefaultExecutablePath);
            }
            else if (Path.IsPathRooted(options.Description.DefaultExecutablePath))
            {
                Assert.Equal(@"c:/myruntime/node", options.Description.DefaultExecutablePath);
            }
            else
            {
                Assert.Equal("node", options.Description.DefaultExecutablePath);
            }

            //Verify worker path is expected
            if (appendCurrentDirToWorkerPath)
            {
                Assert.Equal("httpWorker.js", options.Description.DefaultWorkerPath);
            }
            else if (!workerPathSet)
            {
                Assert.Null(options.Description.DefaultWorkerPath);
            }
            else
            {
                Assert.Equal(@"c:/myworkerPath/httpWorker.js", options.Description.DefaultWorkerPath);
            }

            Assert.Equal(1, options.Description.Arguments.Count);
            Assert.Equal("--xTest1 --xTest2", options.Description.Arguments[0]);
        }

        [Theory]
        [InlineData(@"{
                    'version': '2.0',
                    'customHandler': {
                            'description': {
                                'defaultExecutablePath': 'node',
                                'arguments': ['httpWorker.js'],
                                'workingDirectory': 'c:/myWorkingDir',
                                'workerDirectory': 'c:/myWorkerDir'
                            }
                        }
                    }", false, false, false)]
        [InlineData(@"{
                    'version': '2.0',
                    'customHandler': {
                            'description': {
                                'defaultExecutablePath': 'node',
                                'workingDirectory': 'myWorkingDir',
                                'workerDirectory': 'myWorkerDir'
                            }
                        }
                    }", true, true, true)]
        public void CustomHandler_Config_ExpectedValues_WorkerDirectory_WorkingDirectory(string hostJsonContent, bool appendCurrentDirToDefaultExe, bool appendCurrentDirToWorkingDir, bool appendCurrentDirToWorkerDir)
        {
            File.WriteAllText(_hostJsonFile, hostJsonContent);
            var configuration = BuildHostJsonConfiguration();
            HttpWorkerOptionsSetup setup = new HttpWorkerOptionsSetup(new OptionsWrapper<ScriptJobHostOptions>(_scriptJobHostOptions), configuration, _testLoggerFactory, _metricsLogger);
            HttpWorkerOptions options = new HttpWorkerOptions();
            options.Description = new HttpWorkerDescription();
            options.Description.FileExists = path =>
            {
                return appendCurrentDirToDefaultExe;
            };
            setup.Configure(options);

            Assert.True(_metricsLogger.LoggedEvents.Contains(MetricEventNames.CustomHandlerConfiguration));

            //Verify worker exe path is expected
            if (appendCurrentDirToDefaultExe)
            {
                Assert.Equal(Path.Combine(_scriptJobHostOptions.RootScriptPath, "myWorkerDir", "node"), options.Description.DefaultExecutablePath);
            }
            else
            {
                Assert.Equal("node", options.Description.DefaultExecutablePath);
            }

            // Verify worker dir is expected
            if (appendCurrentDirToWorkerDir)
            {
                Assert.Equal(Path.Combine(_scriptJobHostOptions.RootScriptPath, "myWorkerDir"), options.Description.WorkerDirectory);
            }
            else
            {
                Assert.Equal(@"c:/myWorkerDir", options.Description.WorkerDirectory);
            }

            //Verify workering Dir is expected
            if (appendCurrentDirToWorkingDir)
            {
                Assert.Equal(Path.Combine(_scriptJobHostOptions.RootScriptPath, "myWorkingDir"), options.Description.WorkingDirectory);
            }
            else
            {
                Assert.Equal(@"c:/myWorkingDir", options.Description.WorkingDirectory);
            }
        }

        [Fact]
        public void HttpWorkerConfig_OverrideConfigViaEnvVars_Test()
        {
            string hostJsonContent = @"{
                    'version': '2.0',
                    'httpWorker': {
                            'description': {
                                'langauge': 'testExe',
                                'defaultExecutablePath': 'dotnet',
                                'defaultWorkerPath':'ManualTrigger/run.csx',
                                'arguments': ['--xTest1 --xTest2'],
                                'workerArguments': ['--xTest3 --xTest4']
                            }
                        }
                    }";
            try
            {
                File.WriteAllText(_hostJsonFile, hostJsonContent);
                Environment.SetEnvironmentVariable("AzureFunctionsJobHost:httpWorker:description:defaultWorkerPath", "OneSecondTimer/run.csx");
                Environment.SetEnvironmentVariable("AzureFunctionsJobHost:httpWorker:description:arguments", "[\"--xTest5\", \"--xTest6\", \"--xTest7\"]");
                var configuration = BuildHostJsonConfiguration();
                HttpWorkerOptionsSetup setup = new HttpWorkerOptionsSetup(new OptionsWrapper<ScriptJobHostOptions>(_scriptJobHostOptions), configuration, _testLoggerFactory, _metricsLogger);
                HttpWorkerOptions options = new HttpWorkerOptions();
                setup.Configure(options);
                Assert.Equal("dotnet", options.Description.DefaultExecutablePath);
                // Verify options are overridden
                Assert.Contains("OneSecondTimer/run.csx", options.Description.DefaultWorkerPath);
                Assert.Equal(3, options.Description.Arguments.Count);
                Assert.Contains("--xTest5", options.Description.Arguments);
                Assert.Contains("--xTest6", options.Description.Arguments);
                Assert.Contains("--xTest7", options.Description.Arguments);

                // Verify options not overridden
                Assert.Equal(1, options.Description.WorkerArguments.Count);
                Assert.Equal("--xTest3 --xTest4", options.Description.WorkerArguments.ElementAt(0));
            }
            finally
            {
                Environment.SetEnvironmentVariable("AzureFunctionsJobHost:httpWorker:description:defaultWorkerPath", string.Empty);
                Environment.SetEnvironmentVariable("AzureFunctionsJobHost:httpWorker:description:arguments", string.Empty);
            }
        }

        [Fact]
        public void GetUnusedTcpPort_Succeeds()
        {
            int unusedPort = WorkerUtilities.GetUnusedTcpPort();
            TcpListener tcpListener = null;
            try
            {
                tcpListener = new TcpListener(IPAddress.Loopback, unusedPort);
                tcpListener.Start();
            }
            finally
            {
                tcpListener?.Stop();
            }
        }

        private IConfiguration BuildHostJsonConfiguration(IEnvironment environment = null)
        {
            environment = environment ?? new TestEnvironment();
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(_loggerProvider);

            var configSource = new HostJsonFileConfigurationSource(_options, environment, loggerFactory, new TestMetricsLogger());

            var configurationBuilder = new ConfigurationBuilder()
                .Add(configSource)
                .Add(new ScriptEnvironmentVariablesConfigurationSource());

            return configurationBuilder.Build();
        }
    }
}