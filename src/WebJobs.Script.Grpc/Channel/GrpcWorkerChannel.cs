// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Grpc.Eventing;
using Microsoft.Azure.WebJobs.Script.Grpc.Extensions;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Azure.WebJobs.Script.ManagedDependencies;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using static Microsoft.Azure.WebJobs.Script.Grpc.Messages.RpcLog.Types;
using FunctionMetadata = Microsoft.Azure.WebJobs.Script.Description.FunctionMetadata;
using MsgType = Microsoft.Azure.WebJobs.Script.Grpc.Messages.StreamingMessage.ContentOneofCase;
using ParameterBindingType = Microsoft.Azure.WebJobs.Script.Grpc.Messages.ParameterBinding.RpcDataOneofCase;

namespace Microsoft.Azure.WebJobs.Script.Grpc
{
    internal class GrpcWorkerChannel : IRpcWorkerChannel, IDisposable
    {
        private readonly IScriptEventManager _eventManager;
        private readonly RpcWorkerConfig _workerConfig;
        private readonly string _runtime;
        private readonly IEnvironment _environment;
        private readonly IOptionsMonitor<ScriptApplicationHostOptions> _applicationHostOptions;
        private readonly ISharedMemoryManager _sharedMemoryManager;
        private readonly IOptions<WorkerConcurrencyOptions> _concurrencyOptions;

        private IDisposable _functionLoadRequestResponseEvent;
        private bool _disposed;
        private bool _disposing;
        private WorkerInitResponse _initMessage;
        private string _workerId;
        private RpcWorkerChannelState _state;
        private IDictionary<string, Exception> _functionLoadErrors = new Dictionary<string, Exception>();
        private IDictionary<string, Exception> _metadataRequestErrors = new Dictionary<string, Exception>();
        private ConcurrentDictionary<string, ScriptInvocationContext> _executingInvocations = new ConcurrentDictionary<string, ScriptInvocationContext>();
        private IDictionary<string, BufferBlock<ScriptInvocationContext>> _functionInputBuffers = new ConcurrentDictionary<string, BufferBlock<ScriptInvocationContext>>();
        private ConcurrentDictionary<string, TaskCompletionSource<bool>> _workerStatusRequests = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        private IObservable<InboundGrpcEvent> _inboundWorkerEvents;
        private List<IDisposable> _inputLinks = new List<IDisposable>();
        private List<IDisposable> _eventSubscriptions = new List<IDisposable>();
        private IDisposable _startSubscription;
        private IDisposable _startLatencyMetric;
        private IEnumerable<FunctionMetadata> _functions;
        private GrpcCapabilities _workerCapabilities;
        private ILogger _workerChannelLogger;
        private IMetricsLogger _metricsLogger;
        private IWorkerProcess _rpcWorkerProcess;
        private TaskCompletionSource<bool> _reloadTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _workerInitTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<List<FunctionMetadata>> _functionsIndexingTask = new TaskCompletionSource<List<FunctionMetadata>>(TaskCreationOptions.RunContinuationsAsynchronously);
        private TimeSpan _functionLoadTimeout = TimeSpan.FromMinutes(10);
        private bool _isSharedMemoryDataTransferEnabled;
        private WorkerChannelMonitor _monitor;

        internal GrpcWorkerChannel(
           string workerId,
           IScriptEventManager eventManager,
           RpcWorkerConfig workerConfig,
           IWorkerProcess rpcWorkerProcess,
           ILogger logger,
           IMetricsLogger metricsLogger,
           int attemptCount,
           IEnvironment environment,
           IOptionsMonitor<ScriptApplicationHostOptions> applicationHostOptions,
           ISharedMemoryManager sharedMemoryManager,
           IOptions<WorkerConcurrencyOptions> concurrencyOptions)
        {
            _workerId = workerId;
            _eventManager = eventManager;
            _workerConfig = workerConfig;
            _runtime = workerConfig.Description.Language;
            _rpcWorkerProcess = rpcWorkerProcess;
            _workerChannelLogger = logger;
            _metricsLogger = metricsLogger;
            _environment = environment;
            _applicationHostOptions = applicationHostOptions;
            _sharedMemoryManager = sharedMemoryManager;
            _concurrencyOptions = concurrencyOptions;

            _workerCapabilities = new GrpcCapabilities(_workerChannelLogger);

            _inboundWorkerEvents = _eventManager.OfType<InboundGrpcEvent>()
                .Where(msg => msg.WorkerId == _workerId);

            _eventSubscriptions.Add(_inboundWorkerEvents
                .Where(msg => msg.IsMessageOfType(MsgType.RpcLog) && !msg.IsLogOfCategory(RpcLogCategory.System))
                .Subscribe(Log));

            _eventSubscriptions.Add(_inboundWorkerEvents
                .Where(msg => msg.IsMessageOfType(MsgType.RpcLog) && msg.IsLogOfCategory(RpcLogCategory.System))
                .Subscribe(SystemLog));

            _eventSubscriptions.Add(_eventManager.OfType<FileEvent>()
                .Where(msg => _workerConfig.Description.Extensions.Contains(Path.GetExtension(msg.FileChangeArguments.FullPath)))
                .Throttle(TimeSpan.FromMilliseconds(300)) // debounce
                .Subscribe(msg => _eventManager.Publish(new HostRestartEvent())));

            _eventSubscriptions.Add(_inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.InvocationResponse)
                .Subscribe(async (msg) => await InvokeResponse(msg.Message.InvocationResponse)));

            _inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.WorkerStatusResponse)
               .Subscribe((msg) => ReceiveWorkerStatusResponse(msg.Message.RequestId, msg.Message.WorkerStatusResponse));

            _startLatencyMetric = metricsLogger?.LatencyEvent(string.Format(MetricEventNames.WorkerInitializeLatency, workerConfig.Description.Language, attemptCount));

            _state = RpcWorkerChannelState.Default;

            _monitor = new WorkerChannelMonitor(this, _concurrencyOptions);
        }

        public string Id => _workerId;

        public IDictionary<string, BufferBlock<ScriptInvocationContext>> FunctionInputBuffers => _functionInputBuffers;

        internal IWorkerProcess WorkerProcess => _rpcWorkerProcess;

        internal RpcWorkerConfig Config => _workerConfig;

        public bool IsChannelReadyForInvocations()
        {
            return !_disposing && !_disposed && _state.HasFlag(RpcWorkerChannelState.InvocationBuffersInitialized | RpcWorkerChannelState.Initialized);
        }

        public async Task StartWorkerProcessAsync()
        {
            _startSubscription = _inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.StartStream)
                .Timeout(_workerConfig.CountOptions.ProcessStartupTimeout)
                .Take(1)
                .Subscribe(SendWorkerInitRequest, HandleWorkerStartStreamError);

            _workerChannelLogger.LogDebug("Initiating Worker Process start up");
            await _rpcWorkerProcess.StartProcessAsync();
            _state = _state | RpcWorkerChannelState.Initializing;
            await _workerInitTask.Task;
        }

        public async Task<WorkerStatus> GetWorkerStatusAsync()
        {
            var workerStatus = new WorkerStatus();

            if (!string.IsNullOrEmpty(_workerCapabilities.GetCapabilityState(RpcWorkerConstants.WorkerStatus)))
            {
                // get the worker's current status
                // this will include the OOP worker's channel latency in the request, which can be used upstream
                // to make scale decisions
                var message = new StreamingMessage
                {
                    RequestId = Guid.NewGuid().ToString(),
                    WorkerStatusRequest = new WorkerStatusRequest()
                };

                var sw = Stopwatch.StartNew();
                var tcs = new TaskCompletionSource<bool>();
                if (_workerStatusRequests.TryAdd(message.RequestId, tcs))
                {
                    SendStreamingMessage(message);
                    await tcs.Task;
                    sw.Stop();
                    workerStatus.Latency = sw.Elapsed;
                    _workerChannelLogger.LogDebug($"[HostMonitor] Worker status request took {sw.ElapsedMilliseconds}ms");
                }
            }

            if (_concurrencyOptions.Value.Enabled)
            {
                workerStatus.IsReady = IsChannelReadyForInvocations();
                workerStatus.WorkerStats = _monitor.GetStats();
            }

            return workerStatus;
        }

        // send capabilities to worker, wait for WorkerInitResponse
        internal void SendWorkerInitRequest(GrpcEvent startEvent)
        {
            _workerChannelLogger.LogDebug("Worker Process started. Received StartStream message");
            _inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.WorkerInitResponse)
                .Timeout(_workerConfig.CountOptions.InitializationTimeout)
                .Take(1)
                .Subscribe(WorkerInitResponse, HandleWorkerInitError);

            WorkerInitRequest initRequest = GetWorkerInitRequest();

            // Run as Functions Host V2 compatible
            if (_environment.IsV2CompatibilityMode())
            {
                _workerChannelLogger.LogDebug("Worker and host running in V2 compatibility mode");
                initRequest.Capabilities.Add(RpcWorkerConstants.V2Compatable, "true");
            }

            SendStreamingMessage(new StreamingMessage
            {
                WorkerInitRequest = initRequest
            });
        }

        internal WorkerInitRequest GetWorkerInitRequest()
        {
            return new WorkerInitRequest()
            {
                HostVersion = ScriptHost.Version,
                WorkerDirectory = _workerConfig.Description.WorkerDirectory
            };
        }

        internal void FunctionEnvironmentReloadResponse(FunctionEnvironmentReloadResponse res, IDisposable latencyEvent)
        {
            _workerChannelLogger.LogDebug("Received FunctionEnvironmentReloadResponse");
            if (res.Result.IsFailure(out Exception reloadEnvironmentVariablesException))
            {
                _workerChannelLogger.LogError(reloadEnvironmentVariablesException, "Failed to reload environment variables");
                _reloadTask.SetException(reloadEnvironmentVariablesException);
            }
            _reloadTask.SetResult(true);
            latencyEvent.Dispose();
        }

        internal void WorkerInitResponse(GrpcEvent initEvent)
        {
            _startLatencyMetric?.Dispose();
            _startLatencyMetric = null;

            _workerChannelLogger.LogDebug("Received WorkerInitResponse. Worker process initialized");
            _initMessage = initEvent.Message.WorkerInitResponse;
            _workerChannelLogger.LogDebug($"Worker capabilities: {_initMessage.Capabilities}");
            if (_initMessage.Result.IsFailure(out Exception exc))
            {
                HandleWorkerInitError(exc);
                _workerInitTask.SetResult(false);
                return;
            }
            _state = _state | RpcWorkerChannelState.Initialized;
            _workerCapabilities.UpdateCapabilities(_initMessage.Capabilities);
            _isSharedMemoryDataTransferEnabled = IsSharedMemoryDataTransferEnabled();
            _workerInitTask.SetResult(true);
        }

        public void SetupFunctionInvocationBuffers(IEnumerable<FunctionMetadata> functions)
        {
            _functions = functions;
            foreach (FunctionMetadata metadata in functions)
            {
                _workerChannelLogger.LogDebug("Setting up FunctionInvocationBuffer for function:{functionName} with functionId:{id}", metadata.Name, metadata.GetFunctionId());
                _functionInputBuffers[metadata.GetFunctionId()] = new BufferBlock<ScriptInvocationContext>();
            }
            _state = _state | RpcWorkerChannelState.InvocationBuffersInitialized;
        }

        public void SendFunctionLoadRequests(ManagedDependencyOptions managedDependencyOptions, TimeSpan? functionTimeout)
        {
            if (_functions != null)
            {
                if (functionTimeout.HasValue)
                {
                    _functionLoadTimeout = functionTimeout.Value > _functionLoadTimeout ? functionTimeout.Value : _functionLoadTimeout;
                    _eventSubscriptions.Add(_inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.FunctionLoadResponse)
                        .Timeout(_functionLoadTimeout)
                        .Take(_functions.Count())
                        .Subscribe((msg) => LoadResponse(msg.Message.FunctionLoadResponse), HandleWorkerFunctionLoadError));
                }
                else
                {
                    _eventSubscriptions.Add(_inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.FunctionLoadResponse)
                        .Subscribe((msg) => LoadResponse(msg.Message.FunctionLoadResponse), HandleWorkerFunctionLoadError));
                }
                foreach (FunctionMetadata metadata in _functions.OrderBy(metadata => metadata.IsDisabled()))
                {
                    SendFunctionLoadRequest(metadata, managedDependencyOptions);
                }
            }
        }

        public Task SendFunctionEnvironmentReloadRequest()
        {
            _workerChannelLogger.LogDebug("Sending FunctionEnvironmentReloadRequest");
            IDisposable latencyEvent = _metricsLogger.LatencyEvent(MetricEventNames.SpecializationEnvironmentReloadRequestResponse);

            _eventSubscriptions
                .Add(_inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.FunctionEnvironmentReloadResponse)
                .Timeout(_workerConfig.CountOptions.EnvironmentReloadTimeout)
                .Take(1)
                .Subscribe((msg) => FunctionEnvironmentReloadResponse(msg.Message.FunctionEnvironmentReloadResponse, latencyEvent), HandleWorkerEnvReloadError));

            IDictionary processEnv = Environment.GetEnvironmentVariables();

            FunctionEnvironmentReloadRequest request = GetFunctionEnvironmentReloadRequest(processEnv);

            SendStreamingMessage(new StreamingMessage
            {
                FunctionEnvironmentReloadRequest = request
            });

            return _reloadTask.Task;
        }

        internal FunctionEnvironmentReloadRequest GetFunctionEnvironmentReloadRequest(IDictionary processEnv)
        {
            FunctionEnvironmentReloadRequest request = new FunctionEnvironmentReloadRequest();
            foreach (DictionaryEntry entry in processEnv)
            {
                // Do not add environment variables with empty or null values (see issue #4488 for context)
                if (!string.IsNullOrEmpty(entry.Value?.ToString()))
                {
                    request.EnvironmentVariables.Add(entry.Key.ToString(), entry.Value.ToString());
                }
            }
            request.EnvironmentVariables.Add(WorkerConstants.FunctionsWorkerDirectorySettingName, _workerConfig.Description.WorkerDirectory);
            request.FunctionAppDirectory = _applicationHostOptions.CurrentValue.ScriptPath;

            return request;
        }

        internal void SendFunctionLoadRequest(FunctionMetadata metadata, ManagedDependencyOptions managedDependencyOptions)
        {
            _functionLoadRequestResponseEvent = _metricsLogger.LatencyEvent(MetricEventNames.FunctionLoadRequestResponse);
            _workerChannelLogger.LogDebug("Sending FunctionLoadRequest for function:{functionName} with functionId:{id}", metadata.Name, metadata.GetFunctionId());

            // send a load request for the registered function
            SendStreamingMessage(new StreamingMessage
            {
                FunctionLoadRequest = GetFunctionLoadRequest(metadata, managedDependencyOptions)
            });
        }

        internal FunctionLoadRequest GetFunctionLoadRequest(FunctionMetadata metadata, ManagedDependencyOptions managedDependencyOptions)
        {
            FunctionLoadRequest request = new FunctionLoadRequest()
            {
                FunctionId = metadata.GetFunctionId(),
                Metadata = new RpcFunctionMetadata()
                {
                    Name = metadata.Name,
                    Directory = metadata.FunctionDirectory ?? string.Empty,
                    EntryPoint = metadata.EntryPoint ?? string.Empty,
                    ScriptFile = metadata.ScriptFile ?? string.Empty,
                    IsProxy = metadata.IsProxy()
                }
            };

            if (managedDependencyOptions != null && managedDependencyOptions.Enabled)
            {
                _workerChannelLogger?.LogDebug($"Adding dependency download request to {_workerConfig.Description.Language} language worker");
                request.ManagedDependencyEnabled = managedDependencyOptions.Enabled;
            }

            foreach (var binding in metadata.Bindings)
            {
                BindingInfo bindingInfo = binding.ToBindingInfo();

                request.Metadata.Bindings.Add(binding.Name, bindingInfo);
            }
            return request;
        }

        internal void LoadResponse(FunctionLoadResponse loadResponse)
        {
            _functionLoadRequestResponseEvent?.Dispose();
            _workerChannelLogger.LogDebug("Received FunctionLoadResponse for functionId:{functionId}", loadResponse.FunctionId);
            if (loadResponse.Result.IsFailure(out Exception functionLoadEx))
            {
                if (functionLoadEx == null)
                {
                    _workerChannelLogger?.LogError("Worker failed to function id {functionId}. Function load exception is not set by the worker", loadResponse.FunctionId);
                }
                else
                {
                    _workerChannelLogger?.LogError(functionLoadEx, "Worker failed to function id {functionId}.", loadResponse.FunctionId);
                }
                //Cache function load errors to replay error messages on invoking failed functions
                _functionLoadErrors[loadResponse.FunctionId] = functionLoadEx;
            }

            if (loadResponse.IsDependencyDownloaded)
            {
                _workerChannelLogger?.LogDebug($"Managed dependency successfully downloaded by the {_workerConfig.Description.Language} language worker");
            }

            // link the invocation inputs to the invoke call
            var invokeBlock = new ActionBlock<ScriptInvocationContext>(async ctx => await SendInvocationRequest(ctx));
            // associate the invocation input buffer with the function
            var disposableLink = _functionInputBuffers[loadResponse.FunctionId].LinkTo(invokeBlock);
            _inputLinks.Add(disposableLink);
        }

        internal async Task SendInvocationRequest(ScriptInvocationContext context)
        {
            try
            {
                // do not send invocation requests for functions that failed to load or for functions that could not be indexed by the worker
                if (_functionLoadErrors.ContainsKey(context.FunctionMetadata.GetFunctionId()))
                {
                    _workerChannelLogger.LogDebug($"Function {context.FunctionMetadata.Name} failed to load");
                    context.ResultSource.TrySetException(_functionLoadErrors[context.FunctionMetadata.GetFunctionId()]);
                    _executingInvocations.TryRemove(context.ExecutionContext.InvocationId.ToString(), out ScriptInvocationContext _);
                }
                else if (_metadataRequestErrors.ContainsKey(context.FunctionMetadata.GetFunctionId()))
                {
                    _workerChannelLogger.LogDebug($"Worker failed to load metadata for {context.FunctionMetadata.Name}");
                    context.ResultSource.TrySetException(_metadataRequestErrors[context.FunctionMetadata.GetFunctionId()]);
                    _executingInvocations.TryRemove(context.ExecutionContext.InvocationId.ToString(), out ScriptInvocationContext _);
                }
                else
                {
                    if (context.CancellationToken.IsCancellationRequested)
                    {
                        context.ResultSource.SetCanceled();
                        return;
                    }
                    var invocationRequest = await context.ToRpcInvocationRequest(_workerChannelLogger, _workerCapabilities, _isSharedMemoryDataTransferEnabled, _sharedMemoryManager);
                    _executingInvocations.TryAdd(invocationRequest.InvocationId, context);

                    SendStreamingMessage(new StreamingMessage
                    {
                        InvocationRequest = invocationRequest
                    });
                }
            }
            catch (Exception invokeEx)
            {
                context.ResultSource.TrySetException(invokeEx);
            }
        }

        // gets metadata from worker
        public Task<List<FunctionMetadata>> WorkerGetFunctionMetadata()
        {
            return SendWorkerMetadataRequest();
        }

        internal Task<List<FunctionMetadata>> SendWorkerMetadataRequest()
        {
            _eventSubscriptions.Add(_inboundWorkerEvents.Where(msg => msg.MessageType == MsgType.WorkerFunctionMetadataResponse)
                        .Timeout(_functionLoadTimeout)
                        .Take(1)
                        .Subscribe((msg) => ProcessMetadata(msg.Message.WorkerFunctionMetadataResponse), HandleWorkerMetadataRequestError));

            _workerChannelLogger.LogDebug("Sending WorkerMetadataRequest to {language} worker with worker ID {workerID}", _runtime, _workerId);

            // sends the function app directory path to worker for indexing
            SendStreamingMessage(new StreamingMessage
            {
                WorkerFunctionMetadataRequest = new WorkerFunctionMetadataRequest()
                {
                    Directory = _applicationHostOptions.CurrentValue.ScriptPath
                }
            });
            return _functionsIndexingTask.Task;
        }

        // parse metadata response into FunctionMetadata objects
        internal void ProcessMetadata(WorkerFunctionMetadataResponse workerFunctionMetadataResponse)
        {
            _workerChannelLogger.LogDebug("Received the worker function metadata response from worker {worker_id}", _workerId);

            var responseStatus = workerFunctionMetadataResponse.OverallStatus;

            var functions = new List<FunctionMetadata>();

            foreach (var metadata in workerFunctionMetadataResponse.Results)
            {
                if (metadata.Status != null && metadata.Status.IsFailure(out Exception metadataRequestEx))
                {
                    _workerChannelLogger.LogError($"Worker failed to index function {metadata.Id}");
                    _metadataRequestErrors[metadata.Id] = metadataRequestEx;
                }
                var functionMetadata = new FunctionMetadata()
                {
                    FunctionDirectory = metadata.Directory,
                    ScriptFile = metadata.ScriptFile,
                    EntryPoint = metadata.EntryPoint,
                    Name = metadata.Name,
                    Language = metadata.Language
                };

                functionMetadata.SetFunctionId(metadata.Id);

                foreach (string binding in metadata.Bindings)
                {
                    var functionBinding = BindingMetadata.Create(JObject.Parse(binding));
                    functionMetadata.Bindings.Add(functionBinding);
                }
                functions.Add(functionMetadata);
            }
            // set it as task result because we cannot directly return from SendWorkerMetadataRequest
            _functionsIndexingTask.SetResult(functions);
        }

        private async Task<object> GetBindingDataAsync(ParameterBinding binding, string invocationId)
        {
            switch (binding.RpcDataCase)
            {
                case ParameterBindingType.RpcSharedMemory:
                    // Data was transferred by the worker using shared memory
                    return await binding.RpcSharedMemory.ToObjectAsync(_workerChannelLogger, invocationId, _sharedMemoryManager);
                case ParameterBindingType.Data:
                    // Data was transferred by the worker using RPC
                    return binding.Data.ToObject();
                default:
                    throw new InvalidOperationException("Unknown ParameterBindingType");
            }
        }

        /// <summary>
        /// From the output data produced by the worker, get a list of the shared memory maps that were created for this invocation.
        /// </summary>
        /// <param name="bindings">List of <see cref="ParameterBinding"/> produced by the worker as output.</param>
        /// <returns>List of names of shared memory maps produced by the worker.</returns>
        private IList<string> GetOutputMaps(IList<ParameterBinding> bindings)
        {
            IList<string> outputMaps = new List<string>();
            foreach (ParameterBinding binding in bindings)
            {
                if (binding.RpcSharedMemory != null)
                {
                    outputMaps.Add(binding.RpcSharedMemory.Name);
                }
            }

            return outputMaps;
        }

        internal async Task InvokeResponse(InvocationResponse invokeResponse)
        {
            _workerChannelLogger.LogDebug("InvocationResponse received for invocation id: {Id}", invokeResponse.InvocationId);

            if (_executingInvocations.TryRemove(invokeResponse.InvocationId, out ScriptInvocationContext context)
                && invokeResponse.Result.IsSuccess(context.ResultSource))
            {
                try
                {
                    StringBuilder logBuilder = new StringBuilder();
                    bool usedSharedMemory = false;

                    foreach (ParameterBinding binding in invokeResponse.OutputData)
                    {
                        switch (binding.RpcDataCase)
                        {
                            case ParameterBindingType.RpcSharedMemory:
                                logBuilder.AppendFormat("{0}:{1},", binding.Name, binding.RpcSharedMemory.Count);
                                usedSharedMemory = true;
                                break;
                            default:
                                break;
                        }
                    }

                    if (usedSharedMemory)
                    {
                        _workerChannelLogger.LogDebug("Shared memory usage for response of invocation Id: {Id} is {SharedMemoryUsage}", invokeResponse.InvocationId, logBuilder.ToString());
                    }

                    IDictionary<string, object> bindingsDictionary = await invokeResponse.OutputData
                        .ToDictionaryAsync(binding => binding.Name, binding => GetBindingDataAsync(binding, invokeResponse.InvocationId));

                    var result = new ScriptInvocationResult()
                    {
                        Outputs = bindingsDictionary,
                        Return = invokeResponse?.ReturnValue?.ToObject()
                    };
                    context.ResultSource.SetResult(result);
                }
                catch (Exception responseEx)
                {
                    context.ResultSource.TrySetException(responseEx);
                }
                finally
                {
                    // Free memory allocated by the host (for input bindings)
                    if (!_sharedMemoryManager.TryFreeSharedMemoryMapsForInvocation(invokeResponse.InvocationId))
                    {
                        _workerChannelLogger.LogWarning($"Cannot free all shared memory resources for invocation: {invokeResponse.InvocationId}");
                    }

                    // List of shared memory maps that were produced by the worker (for output bindings)
                    IList<string> outputMaps = GetOutputMaps(invokeResponse.OutputData);
                    if (outputMaps.Count > 0)
                    {
                        // If this invocation was using any shared memory maps produced by the worker, close them to free memory
                        SendCloseSharedMemoryResourcesForInvocationRequest(outputMaps);
                    }
                }
            }
        }

        /// <summary>
        /// Request to free memory allocated by the worker (for output bindings)
        /// </summary>
        /// <param name="outputMaps">List of names of shared memory maps to close from the worker.</param>
        internal void SendCloseSharedMemoryResourcesForInvocationRequest(IList<string> outputMaps)
        {
            CloseSharedMemoryResourcesRequest closeSharedMemoryResourcesRequest = new CloseSharedMemoryResourcesRequest();
            closeSharedMemoryResourcesRequest.MapNames.AddRange(outputMaps);

            SendStreamingMessage(new StreamingMessage()
            {
                CloseSharedMemoryResourcesRequest = closeSharedMemoryResourcesRequest
            });
        }

        internal void Log(GrpcEvent msg)
        {
            var rpcLog = msg.Message.RpcLog;
            LogLevel logLevel = (LogLevel)rpcLog.Level;
            if (_executingInvocations.TryGetValue(rpcLog.InvocationId, out ScriptInvocationContext context))
            {
                // Restore the execution context from the original invocation. This allows AsyncLocal state to flow to loggers.
                System.Threading.ExecutionContext.Run(context.AsyncExecutionContext, (s) =>
                {
                    if (rpcLog.Exception != null)
                    {
                        var exception = new Workers.Rpc.RpcException(rpcLog.Message, rpcLog.Exception.Message, rpcLog.Exception.StackTrace);
                        context.Logger.Log(logLevel, new EventId(0, rpcLog.EventId), rpcLog.Message, exception, (state, exc) => state);
                    }
                    else
                    {
                        context.Logger.Log(logLevel, new EventId(0, rpcLog.EventId), rpcLog.Message, null, (state, exc) => state);
                    }
                }, null);
            }
        }

        internal void SystemLog(GrpcEvent msg)
        {
            RpcLog systemLog = msg.Message.RpcLog;
            LogLevel logLevel = (LogLevel)systemLog.Level;
            switch (logLevel)
            {
                case LogLevel.Warning:
                    _workerChannelLogger.LogWarning(systemLog.Message);
                    break;

                case LogLevel.Information:
                    _workerChannelLogger.LogInformation(systemLog.Message);
                    break;

                case LogLevel.Error:
                    {
                        if (systemLog.Exception != null)
                        {
                            Workers.Rpc.RpcException exception = new Workers.Rpc.RpcException(systemLog.Message, systemLog.Exception.Message, systemLog.Exception.StackTrace);
                            _workerChannelLogger.LogError(exception, systemLog.Message);
                        }
                        else
                        {
                            _workerChannelLogger.LogError(systemLog.Message);
                        }
                    }
                    break;

                default:
                    _workerChannelLogger.LogInformation(systemLog.Message);
                    break;
            }
        }

        internal void HandleWorkerStartStreamError(Exception exc)
        {
            _workerChannelLogger.LogError(exc, "Starting worker process failed");
            PublishWorkerErrorEvent(exc);
        }

        internal void HandleWorkerEnvReloadError(Exception exc)
        {
            _workerChannelLogger.LogError(exc, "Reloading environment variables failed");
            _reloadTask.SetException(exc);
        }

        internal void HandleWorkerInitError(Exception exc)
        {
            _workerChannelLogger.LogError(exc, "Initializing worker process failed");
            PublishWorkerErrorEvent(exc);
        }

        internal void HandleWorkerFunctionLoadError(Exception exc)
        {
            _workerChannelLogger.LogError(exc, "Loading function failed.");
            if (_disposing || _disposed)
            {
                return;
            }
            _eventManager.Publish(new WorkerErrorEvent(_runtime, Id, exc));
        }

        private void PublishWorkerErrorEvent(Exception exc)
        {
            _workerInitTask.SetException(exc);
            if (_disposing || _disposed)
            {
                return;
            }
            _eventManager.Publish(new WorkerErrorEvent(_runtime, Id, exc));
        }

        internal void HandleWorkerMetadataRequestError(Exception exc)
        {
            _workerChannelLogger.LogError(exc, "Requesting metadata from worker failed.");
            if (_disposing || _disposed)
            {
                return;
            }
            _eventManager.Publish(new WorkerErrorEvent(_runtime, Id, exc));
        }

        private void SendStreamingMessage(StreamingMessage msg)
        {
            _eventManager.Publish(new OutboundGrpcEvent(_workerId, msg));
        }

        internal void ReceiveWorkerStatusResponse(string requestId, WorkerStatusResponse response)
        {
            if (_workerStatusRequests.TryRemove(requestId, out var workerStatusTask))
            {
                workerStatusTask.SetResult(true);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _startLatencyMetric?.Dispose();
                    _startSubscription?.Dispose();

                    // unlink function inputs
                    foreach (var link in _inputLinks)
                    {
                        link.Dispose();
                    }

                    (_rpcWorkerProcess as IDisposable)?.Dispose();

                    foreach (var sub in _eventSubscriptions)
                    {
                        sub.Dispose();
                    }
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            _disposing = true;
            Dispose(true);
        }

        public async Task DrainInvocationsAsync()
        {
            _workerChannelLogger.LogDebug($"Count of in-buffer invocations waiting to be drained out: {_executingInvocations.Count}");
            foreach (ScriptInvocationContext currContext in _executingInvocations.Values)
            {
                await currContext.ResultSource.Task;
            }
        }

        public bool IsExecutingInvocation(string invocationId)
        {
            return _executingInvocations.ContainsKey(invocationId);
        }

        public bool TryFailExecutions(Exception workerException)
        {
            if (workerException == null)
            {
                return false;
            }

            foreach (ScriptInvocationContext currContext in _executingInvocations?.Values)
            {
                string invocationId = currContext?.ExecutionContext?.InvocationId.ToString();
                _workerChannelLogger.LogDebug("Worker '{workerId}' encountered a fatal error. Failing invocation id: {Id}", _workerId, invocationId);
                currContext?.ResultSource?.TrySetException(workerException);
                _executingInvocations.TryRemove(invocationId, out ScriptInvocationContext _);
            }
            return true;
        }

        /// <summary>
        /// Determine if shared memory transfer is enabled.
        /// The following conditions must be met:
        ///     1) <see cref="RpcWorkerConstants.FunctionsWorkerSharedMemoryDataTransferEnabledSettingName"/> must be set in environment variable (AppSetting).
        ///     2) Worker must have the capability <see cref="RpcWorkerConstants.SharedMemoryDataTransfer"/>.
        /// </summary>
        /// <returns><see cref="true"/> if shared memory data transfer is enabled, <see cref="false"/> otherwise.</returns>
        internal bool IsSharedMemoryDataTransferEnabled()
        {
            // Check if the environment variable (AppSetting) has this feature enabled
            string envVal = _environment.GetEnvironmentVariable(RpcWorkerConstants.FunctionsWorkerSharedMemoryDataTransferEnabledSettingName);
            if (string.IsNullOrEmpty(envVal))
            {
                return false;
            }

            bool envValEnabled = false;
            if (bool.TryParse(envVal, out bool boolResult))
            {
                // Check if value was specified as a bool (true/false)
                envValEnabled = boolResult;
            }
            else if (int.TryParse(envVal, out int intResult) && intResult == 1)
            {
                // Check if value was specified as an int (1/0)
                envValEnabled = true;
            }

            if (!envValEnabled)
            {
                return false;
            }

            // Check if the worker supports this feature
            bool capabilityEnabled = !string.IsNullOrEmpty(_workerCapabilities.GetCapabilityState(RpcWorkerConstants.SharedMemoryDataTransfer));
            _workerChannelLogger.LogDebug("IsSharedMemoryDataTransferEnabled: {SharedMemoryDataTransferEnabled}", capabilityEnabled);
            return capabilityEnabled;
        }
    }
}
