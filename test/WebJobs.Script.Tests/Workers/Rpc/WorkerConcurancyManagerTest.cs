// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.WebJobs.Script.Tests;
using Moq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers
{
    public class WorkerConcurancyManagerTest
    {
        private TestLoggerProvider _loggerProvider;
        private ILoggerFactory _loggerFactory;

        public WorkerConcurancyManagerTest()
        {
            _loggerProvider = new TestLoggerProvider();
            _loggerFactory = new LoggerFactory();
            _loggerFactory.AddProvider(_loggerProvider);
        }

        public static IEnumerable<object[]> DataForIsOverloaded =>
            new List<object[]>
            {
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        HistorySize = 5
                    }),
                    new int[] { 1, 2, 3, 4 },
                    false
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        LatencyThreshold = TimeSpan.FromMilliseconds(10),
                        HistorySize = 5
                    }),
                    new int[] { 1, 2, 3, 4, 5 },
                    false
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        LatencyThreshold = TimeSpan.FromMilliseconds(10),
                        HistorySize = 5
                    }),
                    new int[] { 11, 12, 13, 14, 15 },
                    true
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        LatencyThreshold = TimeSpan.FromMilliseconds(13),
                        HistorySize = 6,
                        HistoryThreshold = 0.5F
                    }),
                    new int[] { 11, 12, 13, 14, 15, 16 },
                    true
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        LatencyThreshold = TimeSpan.FromMilliseconds(15),
                        HistorySize = 6,
                        HistoryThreshold = 0.5F
                    }),
                    new int[] { 11, 12, 13, 14, 15, 16 },
                    false
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        LatencyThreshold = TimeSpan.FromMilliseconds(14),
                        HistorySize = 5,
                    }),
                    new int[] { },
                    false
                }
            };

        public static IEnumerable<object[]> DataForForAddWorkerIfNeeded =>
            new List<object[]>
            {
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        HistorySize = 5,
                        LatencyThreshold = TimeSpan.FromMilliseconds(200),
                        AdjustmentPeriod = TimeSpan.FromMilliseconds(1000),
                        MaxWorkerCount = 3,
                    }),
                    new int[] { 100, 100, 100, 100, 100 },
                    new int[] { 150, 150, 150, 150, 150 },
                    true,
                    true,
                    TimeSpan.FromSeconds(2000),
                    false
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        HistorySize = 5,
                        LatencyThreshold = TimeSpan.FromMilliseconds(110),
                        AdjustmentPeriod = TimeSpan.FromMilliseconds(1000),
                        MaxWorkerCount = 3,
                    }),
                    new int[] { 100, 100, 100, 100, 100 },
                    new int[] { 150, 150, 150, 150, 150 },
                    true,
                    true,
                    TimeSpan.FromSeconds(2000),
                    true
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        HistorySize = 5,
                        LatencyThreshold = TimeSpan.FromMilliseconds(110),
                        AdjustmentPeriod = TimeSpan.FromMilliseconds(1000),
                        MaxWorkerCount = 3,
                    }),
                    new int[] { 100, 100, 100, 100, 100 },
                    new int[] { 150, 150, 150, 150, 150 },
                    true,
                    false,
                    TimeSpan.FromSeconds(2000),
                    false
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        HistorySize = 5,
                        LatencyThreshold = TimeSpan.FromMilliseconds(110),
                        AdjustmentPeriod = TimeSpan.FromMilliseconds(1000),
                        MaxWorkerCount = 3,
                    }),
                    new int[] { 100, 100, 100 },
                    new int[] { 150, 150, 150 },
                    true,
                    false,
                    TimeSpan.FromSeconds(2000),
                    false
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        HistorySize = 5,
                        LatencyThreshold = TimeSpan.FromMilliseconds(110),
                        AdjustmentPeriod = TimeSpan.FromMilliseconds(1000),
                        MaxWorkerCount = 3,
                    }),
                    new int[] { 100, 100, 100, 100, 100 },
                    new int[] { 150, 150, 150, 150, 150 },
                    true,
                    false,
                    TimeSpan.FromSeconds(500),
                    false
                },
                new object[]
                {
                    Options.Create(new WorkerConcurrencyOptions()
                    {
                        Enabled = true,
                        HistorySize = 5,
                        LatencyThreshold = TimeSpan.FromMilliseconds(110),
                        AdjustmentPeriod = TimeSpan.FromMilliseconds(1000),
                        MaxWorkerCount = 2,
                    }),
                    new int[] { 100, 100, 100, 100, 100 },
                    new int[] { 150, 150, 150, 150, 150 },
                    true,
                    false,
                    TimeSpan.FromSeconds(2000),
                    false
                }
            };

        [Fact]
        public async Task Start_StartsTimer()
        {
            IOptions<WorkerConcurrencyOptions> options = Options.Create(new WorkerConcurrencyOptions()
            {
                Enabled = true,
                AdjustmentPeriod = TimeSpan.Zero
            });
            Mock<IFunctionInvocationDispatcher> functionInvocationDispatcher = new Mock<IFunctionInvocationDispatcher>(MockBehavior.Strict);
            Mock<IFunctionInvocationDispatcherFactory> functionInvocationDispatcherFactory = new Mock<IFunctionInvocationDispatcherFactory>(MockBehavior.Strict);
            functionInvocationDispatcherFactory.Setup(x => x.GetFunctionDispatcher()).Returns(functionInvocationDispatcher.Object);
            WorkerConcurrencyManager concurrancyManger = new WorkerConcurrencyManager(functionInvocationDispatcherFactory.Object, options, _loggerFactory);
            await concurrancyManger.StartAsync(CancellationToken.None);

            await TestHelpers.Await(() =>
            {
                var sratedLog = _loggerProvider.GetAllLogMessages().FirstOrDefault(x => x.FormattedMessage.StartsWith("Starting worker concurrency monitoring."));
                return sratedLog != null;
            }, pollingInterval: 1000, timeout: 10 * 1000);
        }

        [Fact]
        public async Task Start_DoesNot_StartTimer()
        {
            IOptions<WorkerConcurrencyOptions> options = Options.Create(new WorkerConcurrencyOptions()
            {
                Enabled = false
            });
            Mock<IFunctionInvocationDispatcher> functionInvocationDispatcher = new Mock<IFunctionInvocationDispatcher>(MockBehavior.Strict);
            Mock<IFunctionInvocationDispatcherFactory> functionInvocationDispatcherFactory = new Mock<IFunctionInvocationDispatcherFactory>(MockBehavior.Strict);
            functionInvocationDispatcherFactory.Setup(x => x.GetFunctionDispatcher()).Returns(functionInvocationDispatcher.Object);
            WorkerConcurrencyManager concurrancyManger = new WorkerConcurrencyManager(functionInvocationDispatcherFactory.Object, options, _loggerFactory);
            await concurrancyManger.StartAsync(CancellationToken.None);

            await Task.Delay(1000);
            var sratedLog = _loggerProvider.GetAllLogMessages().FirstOrDefault(x => x.FormattedMessage.StartsWith("Starting worker concurrency monitoring."));
            Assert.True(sratedLog == null);
        }

        [Fact]
        public async Task Start_HttpWorker_DoesNot_StartTimer()
        {
            IOptions<WorkerConcurrencyOptions> options = Options.Create(new WorkerConcurrencyOptions()
            {
                Enabled = false
            });
            Mock<IFunctionInvocationDispatcherFactory> functionInvocationDispatcherFactory = new Mock<IFunctionInvocationDispatcherFactory>(MockBehavior.Strict);
            functionInvocationDispatcherFactory.Setup(x => x.GetFunctionDispatcher()).Returns(new HttpFunctionInvocationDispatcher());
            WorkerConcurrencyManager concurrancyManger = new WorkerConcurrencyManager(functionInvocationDispatcherFactory.Object, options, _loggerFactory);
            await concurrancyManger.StartAsync(CancellationToken.None);

            await Task.Delay(1000);
            var sratedLog = _loggerProvider.GetAllLogMessages().FirstOrDefault(x => x.FormattedMessage.StartsWith("Http worker concurrency is not supported."));
            Assert.True(sratedLog == null);
        }

        [Theory]
        [MemberData(nameof(DataForIsOverloaded))]
        public async Task IsOverloaded_Returns_Expected(IOptions<WorkerConcurrencyOptions> options, int[] latencies, bool expected)
        {
            Mock<IFunctionInvocationDispatcher> functionInvocationDispatcher = new Mock<IFunctionInvocationDispatcher>(MockBehavior.Strict);
            Mock<IFunctionInvocationDispatcherFactory> functionInvocationDispatcherFactory = new Mock<IFunctionInvocationDispatcherFactory>(MockBehavior.Strict);
            functionInvocationDispatcherFactory.Setup(x => x.GetFunctionDispatcher()).Returns(functionInvocationDispatcher.Object);
            WorkerConcurrencyManager concurrancyManger = new WorkerConcurrencyManager(functionInvocationDispatcherFactory.Object, options, _loggerFactory);
            await concurrancyManger.StartAsync(CancellationToken.None);

            WorkerStatus status = new WorkerStatus()
            {
                WorkerStats = new WorkerStats()
                {
                    LatencyHistory = latencies.Select(x => TimeSpan.FromMilliseconds(x))
                }
            };

            Assert.Equal(concurrancyManger.IsOverloaded(status), expected);
        }

        [Theory]
        [MemberData(nameof(DataForForAddWorkerIfNeeded))]
        public void AddWorkerIfNeeded_Returns_Expected(IOptions<WorkerConcurrencyOptions> options,
            int[] latencies1, int[] latencies2, bool readyForInvocations1, bool readyForInvocations2,
            TimeSpan elapsedFromLastAdding, bool expected)
        {
            Mock<IFunctionInvocationDispatcher> functionInvocationDispatcher = new Mock<IFunctionInvocationDispatcher>(MockBehavior.Strict);
            Mock<IFunctionInvocationDispatcherFactory> functionInvocationDispatcherFactory = new Mock<IFunctionInvocationDispatcherFactory>(MockBehavior.Strict);
            functionInvocationDispatcherFactory.Setup(x => x.GetFunctionDispatcher()).Returns(functionInvocationDispatcher.Object);

            Dictionary<string, WorkerStatus> workerStatuses = new Dictionary<string, WorkerStatus>();

            workerStatuses.Add("test1", new WorkerStatus()
            {
                IsReady = readyForInvocations1,
                WorkerStats = new WorkerStats()
                {
                    LatencyHistory = latencies1.Select(x => TimeSpan.FromMilliseconds(x))
                }
            });
            workerStatuses.Add("test2", new WorkerStatus()
            {
                IsReady = readyForInvocations2,
                WorkerStats = new WorkerStats()
                {
                    LatencyHistory = latencies2.Select(x => TimeSpan.FromMilliseconds(x))
                }
            });

            WorkerConcurrencyManager concurrancyManager = new WorkerConcurrencyManager(functionInvocationDispatcherFactory.Object, options, _loggerFactory);

            bool value = concurrancyManager.AddWorkerIfNeeded(workerStatuses, elapsedFromLastAdding);

            Assert.Equal(value, expected);
        }
    }
}
