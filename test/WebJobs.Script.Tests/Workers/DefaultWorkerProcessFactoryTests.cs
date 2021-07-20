// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Http;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers
{
    public class DefaultWorkerProcessFactoryTests
    {
        private ILoggerFactory _loggerFactory = new LoggerFactory();

        public static IEnumerable<object[]> TestWorkerContexts
        {
            get
            {
                yield return new object[]
                {
                    new HttpWorkerContext()
                    {
                        Arguments = new WorkerProcessArguments()
                        {
                            ExecutablePath = "test",
                            ExecutableArguments = new List<string>() { "%httpkey1%", "%TestEnv%" },
                            WorkerArguments = new List<string>() { "%httpkey2%" }
                        },
                        Port = 456,
                        EnvironmentVariables = new Dictionary<string, string>() { { "httpkey1", "httpvalue1" }, { "httpkey2", "httpvalue2" } }
                    }
                };
                yield return new object[]
                {
                    new RpcWorkerContext(
                        "testId",
                        500,
                        "testWorkerId",
                        new WorkerProcessArguments()
                        {
                            ExecutablePath = "test",
                            ExecutableArguments = new List<string>() { "%httpkey1%", "%TestEnv%" },
                            WorkerArguments = new List<string>() { "%httpkey2%" }
                        },
                        "c:\testDir",
                        new Uri("http://localhost"),
                        new Dictionary<string, string>() { { "httpkey1", "httpvalue1" }, { "httpkey2", "httpvalue2" } })
                };
            }
        }

        public static IEnumerable<object[]> InvalidWorkerContexts
        {
            get
            {
                yield return new object[] { new HttpWorkerContext() { Port = 456, EnvironmentVariables = new Dictionary<string, string>() { { "httpkey1", "httpvalue1" }, { "httpkey2", "httpvalue2" } } } };
            }
        }

        [Theory]
        [MemberData(nameof(TestWorkerContexts))]
        public void DefaultWorkerProcessFactory_Returns_ExpectedProcess(WorkerContext workerContext)
        {
            Environment.SetEnvironmentVariable("TestEnv", "TestVal");
            DefaultWorkerProcessFactory defaultWorkerProcessFactory = new DefaultWorkerProcessFactory(_loggerFactory, Options.Create(new WorkerConcurrencyOptions()));
            Process childProcess = defaultWorkerProcessFactory.CreateWorkerProcess(workerContext);

            var expectedEnvVars = workerContext.EnvironmentVariables;
            var actualEnvVars = childProcess.StartInfo.EnvironmentVariables;
            var parentProessEnvVars = Environment.GetEnvironmentVariables();
            Assert.True(expectedEnvVars.Count + parentProessEnvVars.Count == actualEnvVars.Count);
            foreach (var envVar in expectedEnvVars)
            {
                Assert.Equal(expectedEnvVars[envVar.Key], actualEnvVars[envVar.Key]);
            }
            if (workerContext is RpcWorkerContext)
            {
                Assert.Equal(" httpvalue1 TestVal httpvalue2 --host localhost --port 80 --workerId testWorkerId --requestId testId --grpcMaxMessageLength 2147483647", childProcess.StartInfo.Arguments);
            }
            else
            {
                Assert.Equal(" httpvalue1 TestVal httpvalue2", childProcess.StartInfo.Arguments);
            }
            childProcess.Dispose();
            Environment.SetEnvironmentVariable("TestEnv", string.Empty);
        }

        [Theory]
        [MemberData(nameof(InvalidWorkerContexts))]
        public void DefaultWorkerProcessFactory_InvalidWorkerContext_Throws(WorkerContext workerContext)
        {
            DefaultWorkerProcessFactory defaultWorkerProcessFactory = new DefaultWorkerProcessFactory(_loggerFactory, Options.Create(new WorkerConcurrencyOptions()));
            Assert.Throws<ArgumentNullException>(() => defaultWorkerProcessFactory.CreateWorkerProcess(workerContext));
        }

        [Theory]
        [InlineData("%TestEnv%%duh%", "TestVal")]
        [InlineData("%TestEnv%", "TestVal")]
        [InlineData("%TestEnv2%%duh%", "")]
        public void DefaultWorkerProcessFactory_SanitizeExpandedArgs(string inputString, string expectedResult)
        {
            Environment.SetEnvironmentVariable("TestEnv", "TestVal");
            DefaultWorkerProcessFactory defaultWorkerProcessFactory = new DefaultWorkerProcessFactory(_loggerFactory, Options.Create(new WorkerConcurrencyOptions()));
            var expandedArgs = Environment.ExpandEnvironmentVariables(inputString);
            var result = defaultWorkerProcessFactory.SanitizeExpandedArgument(expandedArgs);
            Assert.Equal(expectedResult, result);
            Environment.SetEnvironmentVariable("TestEnv", string.Empty);
        }

        [Theory]
        [InlineData(RpcWorkerConstants.PythonTreadpoolThreadCount, RpcWorkerConstants.PythonLanguageWorkerName, RpcWorkerConstants.DefaultConcurrencyPython)]
        [InlineData(RpcWorkerConstants.PSWorkerInProcConcurrencyUpperBound, RpcWorkerConstants.PowerShellLanguageWorkerName, RpcWorkerConstants.DefaultConcurrencyPS)]
        public void DefaultWorkerProcessFactory_LanguageWorkerConcurrency_Overwrite(string appSettingName, string funcrionWorkerRuntime, string defaultConcurrecny)
        {
            Environment.SetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerRuntimeSettingName, funcrionWorkerRuntime);
            DefaultWorkerProcessFactory defaultWorkerProcessFactory = new DefaultWorkerProcessFactory(_loggerFactory,
                Options.Create(new WorkerConcurrencyOptions()
                {
                    Enabled = true
                }));
            Process process = defaultWorkerProcessFactory.CreateWorkerProcess(TestWorkerContexts.ToList()[1][0] as WorkerContext);
            Assert.Equal(process.StartInfo.EnvironmentVariables[appSettingName], defaultConcurrecny);
            Environment.SetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerRuntimeSettingName, string.Empty);
        }

        [Theory]
        [InlineData(RpcWorkerConstants.PythonTreadpoolThreadCount, RpcWorkerConstants.PythonLanguageWorkerName)]
        [InlineData(RpcWorkerConstants.PSWorkerInProcConcurrencyUpperBound, RpcWorkerConstants.PowerShellLanguageWorkerName)]
        public void DefaultWorkerProcessFactory_LanguageWorkerConcurrency_NoChanges(string appSettingName, string funcrionWorkerRuntime)
        {
            Environment.SetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerRuntimeSettingName, funcrionWorkerRuntime);
            DefaultWorkerProcessFactory defaultWorkerProcessFactory = new DefaultWorkerProcessFactory(_loggerFactory,
                Options.Create(new WorkerConcurrencyOptions()));
            Process process = defaultWorkerProcessFactory.CreateWorkerProcess(TestWorkerContexts.ToList()[1][0] as WorkerContext);
            Assert.False(process.StartInfo.EnvironmentVariables.ContainsKey(appSettingName));
            Environment.SetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerRuntimeSettingName, string.Empty);
        }

        public IDictionary<string, string> GetTestEnvVars()
        {
            return new Dictionary<string, string>() { { "rpckey1", "rpcvalue1" }, { "rpckey2", "rpcvalue2" } };
        }
    }
}
