// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers
{
    public class WorkerChannelMonitorTest
    {
        [Fact]
        public async Task GetStats_StartsTimer()
        {
            Mock<IWorkerChannel> channelMock = new Mock<IWorkerChannel>(MockBehavior.Strict);
            channelMock.Setup(x => x.GetWorkerStatusAsync()).Returns(async () =>
            {
                return await Task.FromResult(
                    new WorkerStatus()
                    {
                        Latency = TimeSpan.FromMilliseconds(50)
                    });
            });
            var options = Options.Create(new WorkerConcurrencyOptions()
            {
                Enabled = true,
                CheckInterval = TimeSpan.FromMilliseconds(100),
                LatencyThreshold = TimeSpan.FromMilliseconds(200)
            });
            var monitor = new WorkerChannelMonitor(channelMock.Object, options);

            WorkerStats stats = null;
            await TestHelpers.Await(() =>
            {
                stats = monitor.GetStats();
                return stats.LatencyHistory.Count() > 0;
            }, pollingInterval: 1000, timeout: 10 * 1000);

            Assert.True(stats.LatencyHistory.All(x => x.TotalMilliseconds == 50));
        }

        [Fact]
        public async Task GetStats_DoesNot_StartTimer()
        {
            Mock<IWorkerChannel> channelMock = new Mock<IWorkerChannel>(MockBehavior.Strict);
            channelMock.Setup(x => x.GetWorkerStatusAsync()).Returns(async () =>
            {
                return await Task.FromResult(
                    new WorkerStatus()
                    {
                        Latency = TimeSpan.FromMilliseconds(50)
                    });
            });
            var options = Options.Create(new WorkerConcurrencyOptions()
            {
                Enabled = false,
                CheckInterval = TimeSpan.FromMilliseconds(100),
                LatencyThreshold = TimeSpan.FromMilliseconds(200)
            });
            var monitor = new WorkerChannelMonitor(channelMock.Object, options);

            await Task.Delay(1000);
            WorkerStats stats = monitor.GetStats();

            Assert.True(stats.LatencyHistory.Count() == 0);
        }
    }
}
