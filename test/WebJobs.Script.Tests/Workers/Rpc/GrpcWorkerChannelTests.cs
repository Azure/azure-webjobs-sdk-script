// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Grpc;
using Microsoft.Azure.WebJobs.Script.Grpc.Eventing;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers.Rpc
{
    public class GrpcWorkerChannelTests : IDisposable
    {
        private static string _expectedLogMsg = "Outbound event subscribe event handler invoked";
        private static string _expectedSystemLogMessage = "Random system log message";
        private static string _expectedLoadMsgPartial = "Sending FunctionLoadRequest for ";

        private readonly Mock<IWorkerProcess> _mockrpcWorkerProcess = new Mock<IWorkerProcess>();
        private readonly string _workerId = "testWorkerId";
        private readonly string _scriptRootPath = "c:\testdir";
        private readonly IScriptEventManager _eventManager = new ScriptEventManager();
        private readonly TestMetricsLogger _metricsLogger = new TestMetricsLogger();
        private readonly Mock<IWorkerConsoleLogSource> _mockConsoleLogger = new Mock<IWorkerConsoleLogSource>();
        private readonly Mock<FunctionRpc.FunctionRpcBase> _mockFunctionRpcService = new Mock<FunctionRpc.FunctionRpcBase>();
        private readonly TestRpcServer _testRpcServer = new TestRpcServer();
        private readonly ILoggerFactory _loggerFactory = MockNullLoggerFactory.CreateLoggerFactory();
        private readonly TestFunctionRpcService _testFunctionRpcService;
        private readonly TestLogger _logger;
        private readonly IEnumerable<FunctionMetadata> _functions = new List<FunctionMetadata>();
        private readonly RpcWorkerConfig _testWorkerConfig;
        private readonly TestEnvironment _testEnvironment;
        private readonly IOptionsMonitor<ScriptApplicationHostOptions> _hostOptionsMonitor;
        private readonly IMemoryMappedFileAccessor _mapAccessor;
        private readonly ISharedMemoryManager _sharedMemoryManager;
        private readonly IOptions<WorkerConcurrencyOptions> _concurrencyOptions;
        private GrpcWorkerChannel _workerChannel;

        public GrpcWorkerChannelTests()
        {
            _logger = new TestLogger("FunctionDispatcherTests");
            _testFunctionRpcService = new TestFunctionRpcService(_eventManager, _workerId, _logger, _expectedLogMsg);
            _testWorkerConfig = TestHelpers.GetTestWorkerConfigs().FirstOrDefault();
            _testWorkerConfig.CountOptions.ProcessStartupTimeout = TimeSpan.FromSeconds(5);
            _testWorkerConfig.CountOptions.InitializationTimeout = TimeSpan.FromSeconds(5);
            _testWorkerConfig.CountOptions.EnvironmentReloadTimeout = TimeSpan.FromSeconds(5);

            _mockrpcWorkerProcess.Setup(m => m.StartProcessAsync()).Returns(Task.CompletedTask);
            _testEnvironment = new TestEnvironment();
            ILogger<MemoryMappedFileAccessor> mmapAccessorLogger = NullLogger<MemoryMappedFileAccessor>.Instance;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _mapAccessor = new MemoryMappedFileAccessorWindows(mmapAccessorLogger);
            }
            else
            {
                _mapAccessor = new MemoryMappedFileAccessorUnix(mmapAccessorLogger, _testEnvironment);
            }
            _sharedMemoryManager = new SharedMemoryManager(_loggerFactory, _mapAccessor);

            var hostOptions = new ScriptApplicationHostOptions
            {
                IsSelfHost = true,
                ScriptPath = _scriptRootPath,
                LogPath = Environment.CurrentDirectory, // not tested
                SecretsPath = Environment.CurrentDirectory, // not tested
                HasParentScope = true
            };
            _hostOptionsMonitor = TestHelpers.CreateOptionsMonitor(hostOptions);
            _concurrencyOptions = Options.Create(new WorkerConcurrencyOptions());

            _workerChannel = new GrpcWorkerChannel(
               _workerId,
               _eventManager,
               _testWorkerConfig,
               _mockrpcWorkerProcess.Object,
               _logger,
               _metricsLogger,
               0,
               _testEnvironment,
               _hostOptionsMonitor,
               _sharedMemoryManager,
               _concurrencyOptions);
        }

        public void Dispose()
        {
            _sharedMemoryManager.Dispose();
        }

        [Fact]
        public async Task StartWorkerProcessAsync_Invoked_SetupFunctionBuffers_Verify_ReadyForInvocation()
        {
            var initTask = _workerChannel.StartWorkerProcessAsync();
            _testFunctionRpcService.PublishStartStreamEvent(_workerId);
            _testFunctionRpcService.PublishWorkerInitResponseEvent();
            await initTask;
            _mockrpcWorkerProcess.Verify(m => m.StartProcessAsync(), Times.Once);
            Assert.False(_workerChannel.IsChannelReadyForInvocations());
            _workerChannel.SetupFunctionInvocationBuffers(GetTestFunctionsList("node"));
            Assert.True(_workerChannel.IsChannelReadyForInvocations());
        }

        [Fact]
        public async Task DisposingChannel_NotReadyForInvocation()
        {
            var initTask = _workerChannel.StartWorkerProcessAsync();
            _testFunctionRpcService.PublishStartStreamEvent(_workerId);
            _testFunctionRpcService.PublishWorkerInitResponseEvent();
            await initTask;
            Assert.False(_workerChannel.IsChannelReadyForInvocations());
            _workerChannel.SetupFunctionInvocationBuffers(GetTestFunctionsList("node"));
            Assert.True(_workerChannel.IsChannelReadyForInvocations());
            _workerChannel.Dispose();
            Assert.False(_workerChannel.IsChannelReadyForInvocations());
        }

        [Fact]
        public void SetupFunctionBuffers_Verify_ReadyForInvocation_Returns_False()
        {
            Assert.False(_workerChannel.IsChannelReadyForInvocations());
            _workerChannel.SetupFunctionInvocationBuffers(GetTestFunctionsList("node"));
            Assert.False(_workerChannel.IsChannelReadyForInvocations());
        }

        [Fact]
        public async Task StartWorkerProcessAsync_TimesOut()
        {
            var initTask = _workerChannel.StartWorkerProcessAsync();
            await Assert.ThrowsAsync<TimeoutException>(async () => await initTask);
        }

        [Fact]
        public async Task SendEnvironmentReloadRequest_Generates_ExpectedMetrics()
        {
            _metricsLogger.ClearCollections();
            Task waitForMetricsTask = Task.Factory.StartNew(() =>
            {
                while (!_metricsLogger.EventsBegan.Contains(MetricEventNames.SpecializationEnvironmentReloadRequestResponse))
                {
                }
            });
            Task reloadRequestResponse = _workerChannel.SendFunctionEnvironmentReloadRequest().ContinueWith(t => { });
            await Task.WhenAny(reloadRequestResponse, waitForMetricsTask, Task.Delay(5000));
            Assert.True(_metricsLogger.EventsBegan.Contains(MetricEventNames.SpecializationEnvironmentReloadRequestResponse));
        }

        [Fact]
        public async Task StartWorkerProcessAsync_WorkerProcess_Throws()
        {
            Mock<IWorkerProcess> mockrpcWorkerProcessThatThrows = new Mock<IWorkerProcess>();
            mockrpcWorkerProcessThatThrows.Setup(m => m.StartProcessAsync()).Throws<FileNotFoundException>();

            _workerChannel = new GrpcWorkerChannel(
               _workerId,
               _eventManager,
               _testWorkerConfig,
               mockrpcWorkerProcessThatThrows.Object,
               _logger,
               _metricsLogger,
               0,
               _testEnvironment,
               _hostOptionsMonitor,
               _sharedMemoryManager,
               _concurrencyOptions);
            await Assert.ThrowsAsync<FileNotFoundException>(async () => await _workerChannel.StartWorkerProcessAsync());
        }

        [Fact]
        public void SendWorkerInitRequest_PublishesOutboundEvent()
        {
            StartStream startStream = new StartStream()
            {
                WorkerId = _workerId
            };
            StreamingMessage startStreamMessage = new StreamingMessage()
            {
                StartStream = startStream
            };
            GrpcEvent rpcEvent = new GrpcEvent(_workerId, startStreamMessage);
            _workerChannel.SendWorkerInitRequest(rpcEvent);
            _testFunctionRpcService.PublishWorkerInitResponseEvent();
            var traces = _logger.GetLogMessages();
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, _expectedLogMsg)));
        }

        [Fact]
        public void WorkerInitRequest_Expected()
        {
            WorkerInitRequest initRequest = _workerChannel.GetWorkerInitRequest();
            Assert.NotNull(initRequest.WorkerDirectory);
            Assert.NotNull(initRequest.HostVersion);
            Assert.Equal("testDir", initRequest.WorkerDirectory);
            Assert.Equal(ScriptHost.Version, initRequest.HostVersion);
        }

        [Fact]
        public void SendWorkerInitRequest_PublishesOutboundEvent_V2Compatable()
        {
            _testEnvironment.SetEnvironmentVariable(EnvironmentSettingNames.FunctionsV2CompatibilityModeKey, "true");
            StartStream startStream = new StartStream()
            {
                WorkerId = _workerId
            };
            StreamingMessage startStreamMessage = new StreamingMessage()
            {
                StartStream = startStream
            };
            GrpcEvent rpcEvent = new GrpcEvent(_workerId, startStreamMessage);
            _workerChannel.SendWorkerInitRequest(rpcEvent);
            _testFunctionRpcService.PublishWorkerInitResponseEvent();
            var traces = _logger.GetLogMessages();
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, _expectedLogMsg)));
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, "Worker and host running in V2 compatibility mode")));
        }

        [Theory]
        [InlineData(RpcLog.Types.Level.Information, RpcLog.Types.Level.Information)]
        [InlineData(RpcLog.Types.Level.Error, RpcLog.Types.Level.Error)]
        [InlineData(RpcLog.Types.Level.Warning, RpcLog.Types.Level.Warning)]
        [InlineData(RpcLog.Types.Level.Trace, RpcLog.Types.Level.Information)]
        public void SendSystemLogMessage_PublishesSystemLogMessage(RpcLog.Types.Level levelToTest, RpcLog.Types.Level expectedLogLevel)
        {
            _testFunctionRpcService.PublishSystemLogEvent(levelToTest);
            var traces = _logger.GetLogMessages();
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, _expectedSystemLogMessage) && m.Level.ToString().Equals(expectedLogLevel.ToString())));
        }

        [Fact]
        public async Task SendInvocationRequest_PublishesOutboundEvent()
        {
            ScriptInvocationContext scriptInvocationContext = GetTestScriptInvocationContext(Guid.NewGuid(), null);
            await _workerChannel.SendInvocationRequest(scriptInvocationContext);
            var traces = _logger.GetLogMessages();
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, _expectedLogMsg)));
        }

        [Fact]
        public async Task SendInvocationRequest_IsInExecutingInvocation()
        {
            ScriptInvocationContext scriptInvocationContext = GetTestScriptInvocationContext(Guid.NewGuid(), null);
            await _workerChannel.SendInvocationRequest(scriptInvocationContext);
            Assert.True(_workerChannel.IsExecutingInvocation(scriptInvocationContext.ExecutionContext.InvocationId.ToString()));
            Assert.False(_workerChannel.IsExecutingInvocation(Guid.NewGuid().ToString()));
        }

        /// <summary>
        /// Verify that <see cref="ScriptInvocationContext"/> with <see cref="RpcSharedMemory"/> input can be sent.
        /// </summary>
        [Fact]
        public async Task SendInvocationRequest_InputsTransferredOverSharedMemory()
        {
            EnableSharedMemoryDataTransfer();

            // Send invocation which will be using RpcSharedMemory for the inputs
            ScriptInvocationContext scriptInvocationContext = GetTestScriptInvocationContextWithSharedMemoryInputs(Guid.NewGuid(), null);
            await _workerChannel.SendInvocationRequest(scriptInvocationContext);
            var traces = _logger.GetLogMessages();
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, _expectedLogMsg)));
        }

        [Fact]
        public async Task Drain_Verify()
        {
            var resultSource = new TaskCompletionSource<ScriptInvocationResult>();
            Guid invocationId = Guid.NewGuid();
            GrpcWorkerChannel channel = new GrpcWorkerChannel(
               _workerId,
               _eventManager,
               _testWorkerConfig,
               _mockrpcWorkerProcess.Object,
               _logger,
               _metricsLogger,
               0,
               _testEnvironment,
               _hostOptionsMonitor,
               _sharedMemoryManager,
               _concurrencyOptions);
            channel.SetupFunctionInvocationBuffers(GetTestFunctionsList("node"));
            ScriptInvocationContext scriptInvocationContext = GetTestScriptInvocationContext(invocationId, resultSource);
            await channel.SendInvocationRequest(scriptInvocationContext);
            Task result = channel.DrainInvocationsAsync();
            Assert.NotEqual(result.Status, TaskStatus.RanToCompletion);
            await channel.InvokeResponse(new InvocationResponse
            {
                InvocationId = invocationId.ToString(),
                Result = new StatusResult
                {
                    Status = StatusResult.Types.Status.Success
                },
            });
            await result;
            Assert.Equal(result.Status, TaskStatus.RanToCompletion);
        }

        [Fact]
        public async Task InFlight_Functions_FailedWithException()
        {
            var resultSource = new TaskCompletionSource<ScriptInvocationResult>();
            ScriptInvocationContext scriptInvocationContext = GetTestScriptInvocationContext(Guid.NewGuid(), resultSource);
            await _workerChannel.SendInvocationRequest(scriptInvocationContext);
            Assert.True(_workerChannel.IsExecutingInvocation(scriptInvocationContext.ExecutionContext.InvocationId.ToString()));
            Exception workerException = new Exception("worker failed");
            _workerChannel.TryFailExecutions(workerException);
            Assert.False(_workerChannel.IsExecutingInvocation(scriptInvocationContext.ExecutionContext.InvocationId.ToString()));
            Assert.Equal(TaskStatus.Faulted, resultSource.Task.Status);
            Assert.Equal(workerException, resultSource.Task.Exception.InnerException);
        }

        [Fact]
        public void SendLoadRequests_PublishesOutboundEvents()
        {
            _metricsLogger.ClearCollections();
            _workerChannel.SetupFunctionInvocationBuffers(GetTestFunctionsList("node"));
            _workerChannel.SendFunctionLoadRequests(null, TimeSpan.FromMinutes(5));
            var traces = _logger.GetLogMessages();
            var functionLoadLogs = traces.Where(m => string.Equals(m.FormattedMessage, _expectedLogMsg));
            AreExpectedMetricsGenerated();
            Assert.True(functionLoadLogs.Count() == 2);
        }

        [Fact]
        public void SendLoadRequests_PublishesOutboundEvents_OrdersDisabled()
        {
            var funcName = "ADisabledFunc";
            var functions = GetTestFunctionsList_WithDisabled("node", funcName);

            // Make sure disabled func is input as first
            Assert.True(functions.First().Name == funcName);

            _workerChannel.SetupFunctionInvocationBuffers(functions);
            _workerChannel.SendFunctionLoadRequests(null, TimeSpan.FromMinutes(5));
            var traces = _logger.GetLogMessages();
            var functionLoadLogs = traces.Where(m => m.FormattedMessage?.Contains(_expectedLoadMsgPartial) ?? false);
            var t = functionLoadLogs.Last<LogMessage>().FormattedMessage;

            // Make sure that disabled func shows up last
            Assert.True(functionLoadLogs.Last<LogMessage>().FormattedMessage.Contains(funcName));
            Assert.False(functionLoadLogs.First<LogMessage>().FormattedMessage.Contains(funcName));
            Assert.True(functionLoadLogs.Count() == 3);
        }

        [Fact]
        public void SendLoadRequests_DoesNotTimeout_FunctionTimeoutNotSet()
        {
            var funcName = "ADisabledFunc";
            var functions = GetTestFunctionsList_WithDisabled("node", funcName);
            _workerChannel.SetupFunctionInvocationBuffers(functions);
            _workerChannel.SendFunctionLoadRequests(null, null);
            var traces = _logger.GetLogMessages();
            var errorLogs = traces.Where(m => m.Level == LogLevel.Error);
            Assert.Empty(errorLogs);
        }

        [Fact]
        public void SendSendFunctionEnvironmentReloadRequest_PublishesOutboundEvents()
        {
            Environment.SetEnvironmentVariable("TestNull", null);
            Environment.SetEnvironmentVariable("TestEmpty", string.Empty);
            Environment.SetEnvironmentVariable("TestValid", "TestValue");
            _workerChannel.SendFunctionEnvironmentReloadRequest();
            _testFunctionRpcService.PublishFunctionEnvironmentReloadResponseEvent();
            var traces = _logger.GetLogMessages();
            var functionLoadLogs = traces.Where(m => string.Equals(m.FormattedMessage, "Sending FunctionEnvironmentReloadRequest"));
            Assert.True(functionLoadLogs.Count() == 1);
        }

        [Fact]
        public async Task SendSendFunctionEnvironmentReloadRequest_ThrowsTimeout()
        {
            var reloadTask = _workerChannel.SendFunctionEnvironmentReloadRequest();
            await Assert.ThrowsAsync<TimeoutException>(async () => await reloadTask);
        }

        [Fact]
        public void SendFunctionEnvironmentReloadRequest_SanitizedEnvironmentVariables()
        {
            var environmentVariables = new Dictionary<string, string>()
            {
                { "TestNull", null },
                { "TestEmpty", string.Empty },
                { "TestValid", "TestValue" }
            };

            FunctionEnvironmentReloadRequest envReloadRequest = _workerChannel.GetFunctionEnvironmentReloadRequest(environmentVariables);
            Assert.False(envReloadRequest.EnvironmentVariables.ContainsKey("TestNull"));
            Assert.False(envReloadRequest.EnvironmentVariables.ContainsKey("TestEmpty"));
            Assert.True(envReloadRequest.EnvironmentVariables.ContainsKey("TestValid"));
            Assert.True(envReloadRequest.EnvironmentVariables["TestValid"] == "TestValue");
            Assert.True(envReloadRequest.EnvironmentVariables.ContainsKey(WorkerConstants.FunctionsWorkerDirectorySettingName));
            Assert.True(envReloadRequest.EnvironmentVariables[WorkerConstants.FunctionsWorkerDirectorySettingName] == "testDir");
        }

        [Fact]
        public void SendFunctionEnvironmentReloadRequest_WithDirectory()
        {
            var environmentVariables = new Dictionary<string, string>()
            {
                { "TestValid", "TestValue" }
            };

            FunctionEnvironmentReloadRequest envReloadRequest = _workerChannel.GetFunctionEnvironmentReloadRequest(environmentVariables);
            Assert.True(envReloadRequest.EnvironmentVariables["TestValid"] == "TestValue");
            Assert.True(envReloadRequest.FunctionAppDirectory == _scriptRootPath);
        }

        [Fact]
        public void ReceivesInboundEvent_InvocationResponse()
        {
            _testFunctionRpcService.PublishInvocationResponseEvent();
            var traces = _logger.GetLogMessages();
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, "InvocationResponse received for invocation id: TestInvocationId")));
        }

        [Fact]
        public void ReceivesInboundEvent_FunctionLoadResponse()
        {
            var functionMetadatas = GetTestFunctionsList("node");
            _workerChannel.SetupFunctionInvocationBuffers(functionMetadatas);
            _workerChannel.SendFunctionLoadRequests(null, TimeSpan.FromMinutes(5));
            _testFunctionRpcService.PublishFunctionLoadResponseEvent("TestFunctionId1");
            var traces = _logger.GetLogMessages();
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, "Setting up FunctionInvocationBuffer for function:js1 with functionId:TestFunctionId1")));
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, "Setting up FunctionInvocationBuffer for function:js2 with functionId:TestFunctionId2")));
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, "Received FunctionLoadResponse for functionId:TestFunctionId1")));
        }

        [Fact]
        public void ReceivesInboundEvent_WorkerMetadataResponse()
        {
            var functionMetadata = GetTestFunctionsList("python");
            var functions = _workerChannel.WorkerGetFunctionMetadata();
            _testFunctionRpcService.PublishWorkerMetadataResponse("TestFunctionId1", functionMetadata);
            var traces = _logger.GetLogMessages();
            Assert.True(traces.Any(m => string.Equals(m.FormattedMessage, $"Received the worker function metadata response from worker {_workerChannel.Id}")));
        }

        [Fact]
        public void FunctionLoadRequest_IsExpected()
        {
            FunctionMetadata metadata = new FunctionMetadata()
            {
                Language = "node",
                Name = "js1"
            };

            metadata.SetFunctionId("TestFunctionId1");

            var functionLoadRequest = _workerChannel.GetFunctionLoadRequest(metadata, null);
            Assert.False(functionLoadRequest.Metadata.IsProxy);
            ProxyFunctionMetadata proxyMetadata = new ProxyFunctionMetadata(null)
            {
                Language = "node",
                Name = "js1"
            };

            metadata.SetFunctionId("TestFunctionId1");

            var proxyFunctionLoadRequest = _workerChannel.GetFunctionLoadRequest(proxyMetadata, null);
            Assert.True(proxyFunctionLoadRequest.Metadata.IsProxy);
        }

        /// <summary>
        /// Verify that shared memory data transfer is enabled if all required settings are set.
        /// </summary>
        [Fact]
        public void SharedMemoryDataTransferSetting_VerifyEnabled()
        {
            EnableSharedMemoryDataTransfer();

            Assert.True(_workerChannel.IsSharedMemoryDataTransferEnabled());
        }

        /// <summary>
        /// Verify that shared memory data transfer is disabled if none of the required settings have been set.
        /// </summary>
        [Fact]
        public void SharedMemoryDataTransferSetting_VerifyDisabled()
        {
            Assert.False(_workerChannel.IsSharedMemoryDataTransferEnabled());
        }

        /// <summary>
        /// Verify that shared memory data transfer is disabled if the worker capability is absent.
        /// All other requirements for shared memory data transfer will be enabled.
        /// </summary>
        [Fact]
        public void SharedMemoryDataTransferSetting_VerifyDisabledIfWorkerCapabilityAbsent()
        {
            // Enable shared memory data transfer in the environment
            _testEnvironment.SetEnvironmentVariable(RpcWorkerConstants.FunctionsWorkerSharedMemoryDataTransferEnabledSettingName, "1");

            StartStream startStream = new StartStream()
            {
                WorkerId = _workerId
            };

            StreamingMessage startStreamMessage = new StreamingMessage()
            {
                StartStream = startStream
            };

            // Send worker init request and enable the capabilities
            GrpcEvent rpcEvent = new GrpcEvent(_workerId, startStreamMessage);
            _workerChannel.SendWorkerInitRequest(rpcEvent);
            _testFunctionRpcService.PublishWorkerInitResponseEvent();

            Assert.False(_workerChannel.IsSharedMemoryDataTransferEnabled());
        }

        /// <summary>
        /// Verify that shared memory data transfer is disabled if the environment variable is absent.
        /// All other requirements for shared memory data transfer will be enabled.
        /// </summary>
        [Fact]
        public void SharedMemoryDataTransferSetting_VerifyDisabledIfEnvironmentVariableAbsent()
        {
            // Enable shared memory data transfer capability in the worker
            IDictionary<string, string> capabilities = new Dictionary<string, string>()
            {
                { RpcWorkerConstants.SharedMemoryDataTransfer, "1" }
            };

            StartStream startStream = new StartStream()
            {
                WorkerId = _workerId
            };

            StreamingMessage startStreamMessage = new StreamingMessage()
            {
                StartStream = startStream
            };

            // Send worker init request and enable the capabilities
            GrpcEvent rpcEvent = new GrpcEvent(_workerId, startStreamMessage);
            _workerChannel.SendWorkerInitRequest(rpcEvent);
            _testFunctionRpcService.PublishWorkerInitResponseEvent(capabilities);

            Assert.False(_workerChannel.IsSharedMemoryDataTransferEnabled());
        }

        private IEnumerable<FunctionMetadata> GetTestFunctionsList(string runtime)
        {
            var metadata1 = new FunctionMetadata()
            {
                Language = runtime,
                Name = "js1"
            };

            metadata1.SetFunctionId("TestFunctionId1");

            var metadata2 = new FunctionMetadata()
            {
                Language = runtime,
                Name = "js2",
            };

            metadata2.SetFunctionId("TestFunctionId2");

            return new List<FunctionMetadata>()
            {
                metadata1,
                metadata2
            };
        }

        private ScriptInvocationContext GetTestScriptInvocationContext(Guid invocationId, TaskCompletionSource<ScriptInvocationResult> resultSource)
        {
            return new ScriptInvocationContext()
            {
                FunctionMetadata = GetTestFunctionsList("node").FirstOrDefault(),
                ExecutionContext = new ExecutionContext()
                {
                    InvocationId = invocationId,
                    FunctionName = "js1",
                    FunctionAppDirectory = _scriptRootPath,
                    FunctionDirectory = _scriptRootPath
                },
                BindingData = new Dictionary<string, object>(),
                Inputs = new List<(string name, DataType type, object val)>(),
                ResultSource = resultSource
            };
        }

        /// <summary>
        /// The <see cref="ScriptInvocationContext"/> would contain inputs that can be transferred over shared memory.
        /// </summary>
        /// <param name="invocationId">ID of the invocation.</param>
        /// <param name="resultSource">Task result source.</param>
        /// <returns>A test <see cref="ScriptInvocationContext"/></returns>
        private ScriptInvocationContext GetTestScriptInvocationContextWithSharedMemoryInputs(Guid invocationId, TaskCompletionSource<ScriptInvocationResult> resultSource)
        {
            const int inputStringLength = 2 * 1024 * 1024;
            string inputString = TestUtils.GetRandomString(inputStringLength);

            const int inputBytesLength = 2 * 1024 * 1024;
            byte[] inputBytes = TestUtils.GetRandomBytesInArray(inputBytesLength);

            var inputs = new List<(string name, DataType type, object val)>
            {
                ("fooStr", DataType.String, inputString),
                ("fooBytes", DataType.Binary, inputBytes),
            };

            return new ScriptInvocationContext()
            {
                FunctionMetadata = GetTestFunctionsList("node").FirstOrDefault(),
                ExecutionContext = new ExecutionContext()
                {
                    InvocationId = invocationId,
                    FunctionName = "js1",
                    FunctionAppDirectory = _scriptRootPath,
                    FunctionDirectory = _scriptRootPath
                },
                BindingData = new Dictionary<string, object>(),
                Inputs = inputs,
                ResultSource = resultSource
            };
        }

        private IEnumerable<FunctionMetadata> GetTestFunctionsList_WithDisabled(string runtime, string funcName)
        {
            var metadata = new FunctionMetadata()
            {
                Language = runtime,
                Name = funcName
            };

            metadata.SetFunctionId("DisabledFunctionId1");
            metadata.SetIsDisabled(true);

            var disabledList = new List<FunctionMetadata>()
            {
                metadata
            };

            return disabledList.Union(GetTestFunctionsList(runtime));
        }

        private bool AreExpectedMetricsGenerated()
        {
            return _metricsLogger.EventsBegan.Contains(MetricEventNames.FunctionLoadRequestResponse);
        }

        private void EnableSharedMemoryDataTransfer()
        {
            // Enable shared memory data transfer in the environment
            _testEnvironment.SetEnvironmentVariable(RpcWorkerConstants.FunctionsWorkerSharedMemoryDataTransferEnabledSettingName, "1");

            // Enable shared memory data transfer capability in the worker
            IDictionary<string, string> capabilities = new Dictionary<string, string>()
            {
                { RpcWorkerConstants.SharedMemoryDataTransfer, "1" }
            };

            StartStream startStream = new StartStream()
            {
                WorkerId = _workerId
            };

            StreamingMessage startStreamMessage = new StreamingMessage()
            {
                StartStream = startStream
            };

            // Send worker init request and enable the capabilities
            GrpcEvent rpcEvent = new GrpcEvent(_workerId, startStreamMessage);
            _workerChannel.SendWorkerInitRequest(rpcEvent);
            _testFunctionRpcService.PublishWorkerInitResponseEvent(capabilities);
        }
    }
}
