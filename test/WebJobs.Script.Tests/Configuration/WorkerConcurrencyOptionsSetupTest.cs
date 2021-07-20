// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Configuration
{
    public class WorkerConcurrencyOptionsSetupTest
    {
        [Theory]
        [InlineData("true", "", "node", "", "", true)]
        [InlineData("true", "1", "node", "", "", false)]
        [InlineData("true", "", "python", "1", "1", false)]
        [InlineData("true", "", "powershell", "1", "1", false)]
        public void Configure_SetsExpectedValues(
            string functionWorkerConcurrencyEnabled,
            string functionsWorkerProcessCount,
            string functionWorkerRuntime,
            string pythonTreadpoolThreadCount,
            string pSWorkerInProcConcurrencyUpperBound,
            bool enabled)
        {
            IConfiguration config = new ConfigurationBuilder().Build();
            TestEnvironment environment = new TestEnvironment();
            environment.SetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerDynamicConcurrencyEnabledSettingName, functionWorkerConcurrencyEnabled);
            environment.SetEnvironmentVariable(RpcWorkerConstants.FunctionsWorkerProcessCountSettingName, functionsWorkerProcessCount);
            environment.SetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerRuntimeSettingName, functionWorkerRuntime);
            environment.SetEnvironmentVariable(RpcWorkerConstants.PythonTreadpoolThreadCount, pythonTreadpoolThreadCount);
            environment.SetEnvironmentVariable(RpcWorkerConstants.PSWorkerInProcConcurrencyUpperBound, pSWorkerInProcConcurrencyUpperBound);

            WorkerConcurrencyOptionsSetup setup = new WorkerConcurrencyOptionsSetup(config, environment);
            WorkerConcurrencyOptions options = new WorkerConcurrencyOptions();
            setup.Configure(options);

            Assert.Equal(options.Enabled, enabled);
            if (enabled)
            {
                Assert.Equal(options.MaxWorkerCount, (Environment.ProcessorCount * 2) + 2);
            }
            else
            {
                Assert.Equal(options.MaxWorkerCount, 0);
            }
        }

        [Fact]
        public void Congigure_Binds_Congiguration()
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                [$"{nameof(WorkerConcurrencyOptions)}:MaxWorkerCount"] = "1",
                [$"{nameof(WorkerConcurrencyOptions)}:LatencyThreshold"] = "00:00:03"
            })
            .Build();

            TestEnvironment environment = new TestEnvironment();
            environment.SetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerDynamicConcurrencyEnabledSettingName, "true");

            WorkerConcurrencyOptionsSetup setup = new WorkerConcurrencyOptionsSetup(config, environment);
            WorkerConcurrencyOptions options = new WorkerConcurrencyOptions();
            setup.Configure(options);

            Assert.Equal(options.MaxWorkerCount, 1);
            Assert.Equal(options.LatencyThreshold, TimeSpan.FromSeconds(3));
        }
    }
}
