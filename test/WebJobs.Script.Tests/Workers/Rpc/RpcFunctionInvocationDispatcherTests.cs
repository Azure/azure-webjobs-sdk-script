// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.ManagedDependencies;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.WebJobs.Script.Tests;
using Moq;
using Xunit;
using FunctionMetadata = Microsoft.Azure.WebJobs.Script.Description.FunctionMetadata;

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers.Rpc
{
    public class RpcFunctionInvocationDispatcherTests
    {
        private static TestRpcWorkerChannel _javaTestChannel;
        private static ILogger _testLogger;
        private static TestLoggerProvider _testLoggerProvider;
        private static LoggerFactory _testLoggerFactory;

        public RpcFunctionInvocationDispatcherTests()
        {
            _testLoggerProvider = new TestLoggerProvider();
            _testLoggerFactory = new LoggerFactory();
            _testLoggerFactory.AddProvider(_testLoggerProvider);

            _testLogger = _testLoggerProvider.CreateLogger("FunctionDispatcherTests");
        }

        [Fact]
        public async Task GetWorkerStatusesAsync_ReturnsExpectedResult()
        {
            int expectedProcessCount = 3;
            var functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));

            var finalChannelCount = await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            Assert.Equal(expectedProcessCount, finalChannelCount);

            var result = await functionDispatcher.GetWorkerStatusesAsync();
            Assert.Equal(expectedProcessCount, result.Count);
            foreach (var status in result.Values)
            {
                Assert.Equal(TimeSpan.FromMilliseconds(10), status.Latency);
            }
        }

        [Fact]
        public async Task Starting_MultipleJobhostChannels_Succeeds()
        {
            _testLoggerProvider.ClearAllLogMessages();
            int expectedProcessCount = 3;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount, startupIntervals: TimeSpan.FromSeconds(1));
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));

            var finalChannelCount = await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            Assert.Equal(expectedProcessCount, finalChannelCount);

            VerifyStartIntervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        }

        [Fact]
        public async Task Starting_MultipleWebhostChannels_Succeeds()
        {
            _testLoggerProvider.ClearAllLogMessages();
            int expectedProcessCount = 3;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount, true);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.JavaLanguageWorkerName));

            var finalWebhostChannelCount = await WaitForWebhostWorkerChannelsToStartup(functionDispatcher.WebHostLanguageWorkerChannelManager, expectedProcessCount, "java");
            Assert.Equal(expectedProcessCount, finalWebhostChannelCount);

            var finalJobhostChannelCount = functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().Count();
            Assert.Equal(0, finalJobhostChannelCount);

            // ignore first start as we added a WebhostChannel on GetTestFunctionDispatcher call
            VerifyStartIntervals(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), true);
        }

        [Fact]
        public async Task Starting_MultipleJobhostChannels_Failed()
        {
            _testLoggerProvider.ClearAllLogMessages();
            int expectedProcessCount = 3;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount, throwOnProcessStartUp: true);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));

            var finalChannelCount = await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount, false);
            Assert.Equal(expectedProcessCount, finalChannelCount);

            await TestHelpers.Await(() =>
            {
                var logMessages = _testLoggerProvider.GetAllLogMessages().ToList();

                return logMessages.Where(x => x.FormattedMessage
                    .Contains("Failed to start a new language worker")).Count() == 3;
            });
        }

        [Fact]
        public async Task SuccessiveRestarts_WorkerCountsStayTheSame()
        {
            int expectedProcessCount = 3;
            List<Task> restartTasks = new List<Task>();
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            Guid[] invocationIds = new Guid[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            List<TestRpcWorkerChannel> workerChannels = functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().Cast<TestRpcWorkerChannel>().ToList();
            for (int i = 0; i < invocationIds.Length; ++i)
            {
                workerChannels[i % 3].SendInvocationRequest(new ScriptInvocationContext
                {
                    ExecutionContext = new ExecutionContext
                    {
                        InvocationId = invocationIds[i]
                    }
                });
            }

            foreach (var invocationId in invocationIds)
            {
                restartTasks.Add(functionDispatcher.RestartWorkerWithInvocationIdAsync(invocationId.ToString()));
            }
            await Task.WhenAll(restartTasks);
            Assert.Equal(expectedProcessCount, functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().Count());   // Ensure count always stays at the initial count
        }

        [Fact]
        public async Task Restart_ParticularWorkerChannel_Succeeds_OnlyThatIsDisposed()
        {
            int expectedProcessCount = 3;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            Guid invocationId = Guid.NewGuid();
            List<TestRpcWorkerChannel> workerChannels = functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().Cast<TestRpcWorkerChannel>().ToList();
            workerChannels[0].SendInvocationRequest(new ScriptInvocationContext
            {
                ExecutionContext = new ExecutionContext
                {
                    InvocationId = invocationId
                }
            });

            await functionDispatcher.RestartWorkerWithInvocationIdAsync(invocationId.ToString());
            Assert.True(workerChannels[0].IsDisposed);
            for (int i = 1; i < workerChannels.Count; ++i)
            {
                Assert.False(workerChannels[i].IsDisposed); // Ensure no other channel is disposed
            }

            Assert.Equal(expectedProcessCount, functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().Count());   // Ensure count goes back to initial count
        }

        [Fact]
        public async Task ShutdownTests()
        {
            int expectedProcessCount = 2;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);

            foreach (var currChannel in functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels())
            {
                var initializedChannel = (TestRpcWorkerChannel)currChannel;
                initializedChannel.ExecutionContexts.Add(Task.Factory.StartNew(() => { }));
            }

            await functionDispatcher.ShutdownAsync();
            foreach (var currChannel in functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels())
            {
                Assert.True(((TestRpcWorkerChannel)currChannel).ExecutionContexts.Count == 0);
            }
        }

        [Fact]
        public async Task ShutdownTests_WithInfinitelyRunningTasks_Timesout()
        {
            int expectedProcessCount = 2;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);

            foreach (var currChannel in functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels())
            {
                var initializedChannel = (TestRpcWorkerChannel)currChannel;
                initializedChannel.ExecutionContexts.Add(new Task<bool>(() => true));   // A task that never starts and therefore never runs to completion
            }

            await functionDispatcher.ShutdownAsync();
            foreach (var currChannel in functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels())
            {
                Assert.True(((TestRpcWorkerChannel)currChannel).ExecutionContexts.Count > 0);
            }
        }

        [Fact]
        public async Task FunctionDispatcherState_Default_DotNetFunctions()
        {
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher();
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);
            FunctionMetadata func1 = new FunctionMetadata()
            {
                Name = "func1",
                Language = "dotnet"
            };
            var functions = new List<FunctionMetadata>()
            {
                func1
            };
            await functionDispatcher.InitializeAsync(functions);
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);

            await functionDispatcher.InitializeAsync(functions);
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);

            await functionDispatcher.InitializeAsync(functions);
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);
        }

        [Fact]
        public async Task FunctionDispatcherState_Default_NoFunctions()
        {
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher();
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);
            await functionDispatcher.InitializeAsync(new List<FunctionMetadata>());
        }

        [Fact]
        public async Task ShutdownChannels_NoFunctions()
        {
            var mockLanguageWorkerChannelManager = new Mock<IWebHostRpcWorkerChannelManager>();
            mockLanguageWorkerChannelManager.Setup(m => m.ShutdownChannelsAsync()).Returns(Task.CompletedTask);
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(mockwebHostLanguageWorkerChannelManager: mockLanguageWorkerChannelManager);
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);
            await functionDispatcher.InitializeAsync(new List<FunctionMetadata>());
            // Wait longer than debouce action.
            await Task.Delay(6000);
            mockLanguageWorkerChannelManager.Verify(m => m.ShutdownChannelsAsync(), Times.Once);
        }

        [Fact]
        public async Task ShutdownChannels_DotNetFunctions()
        {
            FunctionMetadata func1 = new FunctionMetadata()
            {
                Name = "func1",
                Language = "dotnet"
            };
            var functions = new List<FunctionMetadata>()
            {
                func1
            };
            var mockLanguageWorkerChannelManager = new Mock<IWebHostRpcWorkerChannelManager>();
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(mockwebHostLanguageWorkerChannelManager: mockLanguageWorkerChannelManager);
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);
            await functionDispatcher.InitializeAsync(functions);
            // Wait longer than debouce action.
            await Task.Delay(6000);
            mockLanguageWorkerChannelManager.Verify(m => m.ShutdownChannelsAsync(), Times.Once);
        }

        [Fact]
        public async Task FunctionDispatcherState_Transitions_From_Starting_To_Initialized()
        {
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher();
            FunctionMetadata func1 = new FunctionMetadata()
            {
                Name = "func1",
                Language = "node"
            };
            var functions = new List<FunctionMetadata>()
            {
                func1
            };
            await functionDispatcher.InitializeAsync(functions);
            Assert.Equal(FunctionInvocationDispatcherState.Initializing, functionDispatcher.State);
            await WaitForFunctionDispactherStateInitialized(functionDispatcher);
            Assert.Equal(FunctionInvocationDispatcherState.Initialized, functionDispatcher.State);
        }

        [Fact]
        public async Task FunctionDispatcherState_Transitions_From_Default_To_Initialized_To_Disposing()
        {
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher();
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);
            FunctionMetadata func1 = new FunctionMetadata()
            {
                Name = "func1",
                Language = "node"
            };
            var functions = new List<FunctionMetadata>()
            {
                func1
            };
            await functionDispatcher.InitializeAsync(functions);
            Assert.Equal(FunctionInvocationDispatcherState.Initializing, functionDispatcher.State);
            await WaitForFunctionDispactherStateInitialized(functionDispatcher);
            Assert.Equal(FunctionInvocationDispatcherState.Initialized, functionDispatcher.State);
            functionDispatcher.Dispose();
            Assert.True(functionDispatcher == null || functionDispatcher.State == FunctionInvocationDispatcherState.Disposing || functionDispatcher.State == FunctionInvocationDispatcherState.Disposed);
        }

        [Fact]
        public async Task FunctionDispatcher_Restart_ErroredChannels_Succeeds()
        {
            int expectedProcessCount = 2;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));

            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            int finalChannelCount = 0;
            for (int restartCount = 0; restartCount < expectedProcessCount * 3; restartCount++)
            {
                TestRpcWorkerChannel testWorkerChannel = (TestRpcWorkerChannel)functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().FirstOrDefault();
                if (functionDispatcher.LanguageWorkerErrors.Count < (expectedProcessCount * 3) - 1)
                {
                    testWorkerChannel.RaiseWorkerError();
                }
                finalChannelCount = await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            }
            Assert.Equal(expectedProcessCount, finalChannelCount);
        }

        [Fact]
        public async Task FunctionDispatcher_Restart_ErroredChannels_And_Changes_State()
        {
            int expectedProcessCount = 1;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);
            // Add worker
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            TestRpcWorkerChannel testWorkerChannel = (TestRpcWorkerChannel)functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().FirstOrDefault();
            // Restart this channel
            testWorkerChannel.RaiseWorkerRestart();
            await TestHelpers.Await(() =>
            {
                return functionDispatcher.State == FunctionInvocationDispatcherState.WorkerProcessRestarting;
            }, 3000);
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            Assert.Equal(FunctionInvocationDispatcherState.Initialized, functionDispatcher.State);
        }

        [Fact]
        public async Task FunctionDispatcher_Restart_ErroredChannels_And_DoesNot_Change_State()
        {
            int expectedProcessCount = 2;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            Assert.Equal(FunctionInvocationDispatcherState.Default, functionDispatcher.State);
            // Add worker
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            TestRpcWorkerChannel testWorkerChannel = (TestRpcWorkerChannel)functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().FirstOrDefault();
            // Restart one channel
            testWorkerChannel.RaiseWorkerRestart();
            Assert.Equal(FunctionInvocationDispatcherState.Initialized, functionDispatcher.State);
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            Assert.Equal(FunctionInvocationDispatcherState.Initialized, functionDispatcher.State);
        }

        [Fact]
        public async Task FunctionDispatcher_Restart_ErroredChannels_ExceedsLimit()
        {
            int expectedProcessCount = 2;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));

            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            for (int restartCount = 0; restartCount < expectedProcessCount * 3; restartCount++)
            {
                foreach (var channel in functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels())
                {
                    TestRpcWorkerChannel testWorkerChannel = channel as TestRpcWorkerChannel;
                    testWorkerChannel.RaiseWorkerError();
                }
            }
            Assert.Equal(0, functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().Count());
        }

        [Fact]
        public async Task FunctionDispatcher_Restart_ErroredChannels_OnWorkerRestart_NotAffectedByLimit()
        {
            int expectedProcessCount = 2;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));

            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            for (int restartCount = 0; restartCount < expectedProcessCount * 3; restartCount++)
            {
                foreach (var channel in functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels())
                {
                    TestRpcWorkerChannel testWorkerChannel = channel as TestRpcWorkerChannel;
                    testWorkerChannel.RaiseWorkerRestart();
                }

                var finalChannelCount = await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
                Assert.Equal(expectedProcessCount, finalChannelCount);
            }
        }

        [Fact]
        public async Task FunctionDispatcher_Error_WithinThreshold_BucketFills()
        {
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(1);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, 1);
            for (int i = 0; i < 3; ++i)
            {
                foreach (var channel in functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels())
                {
                    TestRpcWorkerChannel testWorkerChannel = channel as TestRpcWorkerChannel;
                    testWorkerChannel.RaiseWorkerError();
                    if (i < 2)
                    {
                        // wait for restart to complete before raising another error
                        await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, 1);
                    }
                }
            }

            Assert.Equal(3, functionDispatcher.LanguageWorkerErrors.Count);
        }

        [Fact]
        public async Task FunctionDispatcher_Error_BeyondThreshold_BucketIsAtOne()
        {
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher();
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, 1);
            for (int i = 1; i < 10; ++i)
            {
                foreach (var channel in functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels())
                {
                    TestRpcWorkerChannel testWorkerChannel = channel as TestRpcWorkerChannel;
                    testWorkerChannel.RaiseWorkerErrorWithCustomTimestamp(DateTime.UtcNow.AddHours(i));
                }
            }

            Assert.Equal(1, functionDispatcher.LanguageWorkerErrors.Count);
        }

        [Fact]
        public async Task FunctionDispatcher_DoNot_Restart_ErroredChannels_If_WorkerRuntime_DoesNotMatch()
        {
            int expectedProcessCount = 1;
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(expectedProcessCount);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.NodeLanguageWorkerName));
            await WaitForJobhostWorkerChannelsToStartup(functionDispatcher, expectedProcessCount);
            _javaTestChannel.RaiseWorkerError();
            var testLogs = _testLoggerProvider.GetAllLogMessages();
            Assert.False(testLogs.Any(m => m.FormattedMessage.Contains("Restarting worker channel for runtime:java")));
            Assert.Equal(expectedProcessCount, functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().Count());
        }

        [Theory]
        [InlineData("node", "node", false, true, true)]
        [InlineData("node", "node", true, false, true)]
        [InlineData("node", "node", true, true, true)]
        [InlineData("node", "node", false, false, false)]
        [InlineData("node", "java", false, true, false)]
        [InlineData("node", "", false, true, false)]
        [InlineData(null, "", false, true, false)]
        [InlineData(null, "node", false, true, false)]
        [InlineData(null, "node", true, false, false)]
        [InlineData(null, "node", true, true, false)]
        [InlineData(null, "node", false, false, false)]
        [InlineData(null, "java", false, true, false)]
        public async Task FunctionDispatcher_ShouldRestartChannel_Returns_True(string workerRuntime, string channelLanguage, bool isWebHostChannel, bool isJobHostChannel, bool expectedResult)
        {
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher();
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(workerRuntime));
            Assert.Equal(expectedResult, functionDispatcher.ShouldRestartWorkerChannel(channelLanguage, isWebHostChannel, isJobHostChannel));
        }

        [Fact]
        public async Task FunctionDispatcher_ErroredWebHostChannel()
        {
            _testLoggerProvider.ClearAllLogMessages();
            RpcFunctionInvocationDispatcher functionDispatcher = GetTestFunctionDispatcher(throwOnProcessStartUp: true, addWebhostChannel: true);
            await functionDispatcher.InitializeAsync(GetTestFunctionsList(RpcWorkerConstants.JavaLanguageWorkerName));
            var testLogs = _testLoggerProvider.GetAllLogMessages();
            Assert.True(testLogs.Any(m => m.FormattedMessage.Contains("Removing errored webhost language worker channel for runtime")));
        }

        private static RpcFunctionInvocationDispatcher GetTestFunctionDispatcher(int maxProcessCountValue = 1, bool addWebhostChannel = false, Mock<IWebHostRpcWorkerChannelManager> mockwebHostLanguageWorkerChannelManager = null,
            bool throwOnProcessStartUp = false, TimeSpan? startupIntervals = null)
        {
            var eventManager = new ScriptEventManager();
            var metricsLogger = new Mock<IMetricsLogger>();
            var mockApplicationLifetime = new Mock<IApplicationLifetime>();
            var testEnv = new TestEnvironment();
            TimeSpan intervals = startupIntervals ?? TimeSpan.FromMilliseconds(100);

            testEnv.SetEnvironmentVariable(RpcWorkerConstants.FunctionsWorkerProcessCountSettingName, maxProcessCountValue.ToString());

            var options = new ScriptJobHostOptions
            {
                RootLogPath = Path.GetTempPath()
            };

            IOptions<ScriptJobHostOptions> scriptOptions = new OptionsManager<ScriptJobHostOptions>(new TestOptionsFactory<ScriptJobHostOptions>(options));

            var workerConfigOptions = new LanguageWorkerOptions
            {
                WorkerConfigs = TestHelpers.GetTestWorkerConfigs(processCountValue: maxProcessCountValue, processStartupInterval: intervals,
                                    processRestartInterval: intervals, processShutdownTimeout: TimeSpan.FromSeconds(1))
            };
            var concurrencyOptions = Options.Create(new WorkerConcurrencyOptions());
            IRpcWorkerChannelFactory testLanguageWorkerChannelFactory = new TestRpcWorkerChannelFactory(eventManager, _testLogger, scriptOptions.Value.RootScriptPath, throwOnProcessStartUp);
            IWebHostRpcWorkerChannelManager testWebHostLanguageWorkerChannelManager = new TestRpcWorkerChannelManager(eventManager, _testLogger, scriptOptions.Value.RootScriptPath, testLanguageWorkerChannelFactory);
            IJobHostRpcWorkerChannelManager jobHostLanguageWorkerChannelManager = new JobHostRpcWorkerChannelManager(_testLoggerFactory);
            if (addWebhostChannel)
            {
                testWebHostLanguageWorkerChannelManager.InitializeChannelAsync("java");
            }
            if (mockwebHostLanguageWorkerChannelManager != null)
            {
                testWebHostLanguageWorkerChannelManager = mockwebHostLanguageWorkerChannelManager.Object;
            }
            var mockFunctionDispatcherLoadBalancer = new Mock<IRpcFunctionInvocationDispatcherLoadBalancer>();

            _javaTestChannel = new TestRpcWorkerChannel(Guid.NewGuid().ToString(), "java", eventManager, _testLogger, false);
            var optionsMonitor = TestHelpers.CreateOptionsMonitor(workerConfigOptions);
            return new RpcFunctionInvocationDispatcher(scriptOptions,
                metricsLogger.Object,
                testEnv,
                mockApplicationLifetime.Object,
                eventManager,
                _testLoggerFactory,
                testLanguageWorkerChannelFactory,
                optionsMonitor,
                testWebHostLanguageWorkerChannelManager,
                jobHostLanguageWorkerChannelManager,
                new OptionsWrapper<ManagedDependencyOptions>(new ManagedDependencyOptions()),
                mockFunctionDispatcherLoadBalancer.Object,
                concurrencyOptions);
        }

        private async Task<int> WaitForJobhostWorkerChannelsToStartup(RpcFunctionInvocationDispatcher functionDispatcher, int expectedCount, bool allReadyForInvocations = true)
        {
            int currentChannelCount = 0;
            await TestHelpers.Await(() =>
            {
                currentChannelCount = functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels().Count();
                if (currentChannelCount == expectedCount)
                {
                    var channels = functionDispatcher.JobHostLanguageWorkerChannelManager.GetChannels();

                    return allReadyForInvocations ? channels.All(ch => ch.IsChannelReadyForInvocations()) : true;
                }
                return false;
            });
            return currentChannelCount;
        }

        private async Task<int> WaitForWebhostWorkerChannelsToStartup(IWebHostRpcWorkerChannelManager channelManager, int expectedCount, string language)
        {
            int currentChannelCount = 0;
            await TestHelpers.Await(() =>
            {
                currentChannelCount = channelManager.GetChannels(language).Count();
                return currentChannelCount == expectedCount;
            });
            return currentChannelCount;
        }

        private async Task WaitForFunctionDispactherStateInitialized(IFunctionInvocationDispatcher functionDispatcher)
        {
            await TestHelpers.Await(() =>
            {
                return functionDispatcher.State == FunctionInvocationDispatcherState.Initialized;
            });
        }

        private IEnumerable<FunctionMetadata> GetTestFunctionsList(string runtime)
        {
            if (string.IsNullOrEmpty(runtime))
            {
                return new List<FunctionMetadata>();
            }

            return new List<FunctionMetadata>()
            {
                new FunctionMetadata()
                {
                     Language = runtime,
                     Name = "js1"
                },

                new FunctionMetadata()
                {
                     Language = runtime,
                     Name = "js2"
                }
            };
        }

        private void VerifyStartIntervals(TimeSpan from, TimeSpan to, bool ignoreFirstStart = false)
        {
            var startTimestamps = _testLoggerProvider.GetAllLogMessages().Where(x => x.FormattedMessage
                .Contains("RegisterFunctions called")).Select(x => x.Timestamp).ToList();
            if (ignoreFirstStart)
            {
                startTimestamps.RemoveAt(0);
            }
            for (int i = 1; i < startTimestamps.Count(); i++)
            {
                var diff = startTimestamps[i] - startTimestamps[i - 1];
                Assert.True(diff > from && diff < to, $"Expected startup intervals between {from.TotalMilliseconds}ms and {to.TotalMilliseconds}ms. Actual: {diff.TotalMilliseconds}ms.");
            }
        }
    }
}