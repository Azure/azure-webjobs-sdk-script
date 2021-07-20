// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.ManagedDependencies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FunctionMetadata = Microsoft.Azure.WebJobs.Script.Description.FunctionMetadata;

namespace Microsoft.Azure.WebJobs.Script.Workers.Rpc
{
    internal class RpcFunctionInvocationDispatcher : IFunctionInvocationDispatcher
    {
        private readonly IMetricsLogger _metricsLogger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private readonly IRpcWorkerChannelFactory _rpcWorkerChannelFactory;
        private readonly IEnvironment _environment;
        private readonly IApplicationLifetime _applicationLifetime;
        private readonly SemaphoreSlim _startWorkerProcessSLock = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _thresholdBetweenRestarts = TimeSpan.FromMinutes(WorkerConstants.WorkerRestartErrorIntervalThresholdInMinutes);
        private readonly IOptions<WorkerConcurrencyOptions> _concurrencyOptions;

        private IScriptEventManager _eventManager;
        private IEnumerable<RpcWorkerConfig> _workerConfigs;
        private IWebHostRpcWorkerChannelManager _webHostLanguageWorkerChannelManager;
        private IJobHostRpcWorkerChannelManager _jobHostLanguageWorkerChannelManager;
        private IDisposable _workerErrorSubscription;
        private IDisposable _workerRestartSubscription;
        private ScriptJobHostOptions _scriptOptions;
        private int _maxProcessCount;
        private IRpcFunctionInvocationDispatcherLoadBalancer _functionDispatcherLoadBalancer;
        private bool _disposed = false;
        private bool _disposing = false;
        private IOptions<ManagedDependencyOptions> _managedDependencyOptions;
        private string _workerRuntime;
        private Action _shutdownStandbyWorkerChannels;
        private IEnumerable<FunctionMetadata> _functions;
        private ConcurrentStack<WorkerErrorEvent> _languageWorkerErrors = new ConcurrentStack<WorkerErrorEvent>();
        private CancellationTokenSource _processStartCancellationToken = new CancellationTokenSource();
        private CancellationTokenSource _disposeToken = new CancellationTokenSource();
        private TimeSpan _processStartupInterval;
        private TimeSpan _restartWait;
        private TimeSpan _shutdownTimeout;

        public RpcFunctionInvocationDispatcher(IOptions<ScriptJobHostOptions> scriptHostOptions,
            IMetricsLogger metricsLogger,
            IEnvironment environment,
            IApplicationLifetime applicationLifetime,
            IScriptEventManager eventManager,
            ILoggerFactory loggerFactory,
            IRpcWorkerChannelFactory rpcWorkerChannelFactory,
            IOptionsMonitor<LanguageWorkerOptions> languageWorkerOptions,
            IWebHostRpcWorkerChannelManager webHostLanguageWorkerChannelManager,
            IJobHostRpcWorkerChannelManager jobHostLanguageWorkerChannelManager,
            IOptions<ManagedDependencyOptions> managedDependencyOptions,
            IRpcFunctionInvocationDispatcherLoadBalancer functionDispatcherLoadBalancer,
            IOptions<WorkerConcurrencyOptions> concurrencyOptions)
        {
            _metricsLogger = metricsLogger;
            _scriptOptions = scriptHostOptions.Value;
            _environment = environment;
            _applicationLifetime = applicationLifetime;
            _webHostLanguageWorkerChannelManager = webHostLanguageWorkerChannelManager;
            _jobHostLanguageWorkerChannelManager = jobHostLanguageWorkerChannelManager;
            _eventManager = eventManager;
            _workerConfigs = languageWorkerOptions.CurrentValue.WorkerConfigs;
            _managedDependencyOptions = managedDependencyOptions ?? throw new ArgumentNullException(nameof(managedDependencyOptions));
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<RpcFunctionInvocationDispatcher>();
            _rpcWorkerChannelFactory = rpcWorkerChannelFactory;
            _workerRuntime = _environment.GetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerRuntimeSettingName);
            _functionDispatcherLoadBalancer = functionDispatcherLoadBalancer;
            _concurrencyOptions = concurrencyOptions;
            State = FunctionInvocationDispatcherState.Default;

            _workerErrorSubscription = _eventManager.OfType<WorkerErrorEvent>()
               .Subscribe(WorkerError);
            _workerRestartSubscription = _eventManager.OfType<WorkerRestartEvent>()
               .Subscribe(WorkerRestart);

            _shutdownStandbyWorkerChannels = ShutdownWebhostLanguageWorkerChannels;
            _shutdownStandbyWorkerChannels = _shutdownStandbyWorkerChannels.Debounce(milliseconds: 5000);
        }

        public FunctionInvocationDispatcherState State { get; private set; }

        public int ErrorEventsThreshold { get; private set; }

        public IJobHostRpcWorkerChannelManager JobHostLanguageWorkerChannelManager => _jobHostLanguageWorkerChannelManager;

        internal ConcurrentStack<WorkerErrorEvent> LanguageWorkerErrors => _languageWorkerErrors;

        internal int MaxProcessCount => _maxProcessCount;

        internal IWebHostRpcWorkerChannelManager WebHostLanguageWorkerChannelManager => _webHostLanguageWorkerChannelManager;

        internal async Task InitializeJobhostLanguageWorkerChannelAsync()
        {
            await InitializeJobhostLanguageWorkerChannelAsync(0);
        }

        internal async Task InitializeJobhostLanguageWorkerChannelAsync(int attemptCount)
        {
            var rpcWorkerChannel = _rpcWorkerChannelFactory.Create(_scriptOptions.RootScriptPath, _workerRuntime, _metricsLogger, attemptCount, _workerConfigs);
            rpcWorkerChannel.SetupFunctionInvocationBuffers(_functions);
            _jobHostLanguageWorkerChannelManager.AddChannel(rpcWorkerChannel);
            await rpcWorkerChannel.StartWorkerProcessAsync();
            _logger.LogDebug("Adding jobhost language worker channel for runtime: {language}. workerId:{id}", _workerRuntime, rpcWorkerChannel.Id);
            rpcWorkerChannel.SendFunctionLoadRequests(_managedDependencyOptions.Value, _scriptOptions.FunctionTimeout);
            SetFunctionDispatcherStateToInitializedAndLog();
        }

        private void SetFunctionDispatcherStateToInitializedAndLog()
        {
            State = FunctionInvocationDispatcherState.Initialized;
            // Do not change this log message. Vs Code relies on this to figure out when to attach debuger to the worker process.
            _logger.LogInformation("Worker process started and initialized.");
        }

        internal async Task InitializeWebhostLanguageWorkerChannel()
        {
            _logger.LogDebug("Creating new webhost language worker channel for runtime:{workerRuntime}.", _workerRuntime);
            IRpcWorkerChannel workerChannel = await _webHostLanguageWorkerChannelManager.InitializeChannelAsync(_workerRuntime);
            workerChannel.SetupFunctionInvocationBuffers(_functions);
            workerChannel.SendFunctionLoadRequests(_managedDependencyOptions.Value, _scriptOptions.FunctionTimeout);
            SetFunctionDispatcherStateToInitializedAndLog();
        }

        internal async void ShutdownWebhostLanguageWorkerChannels()
        {
            _logger.LogDebug("{workerRuntimeConstant}={value}. Will shutdown all the worker channels that started in placeholder mode", RpcWorkerConstants.FunctionWorkerRuntimeSettingName, _workerRuntime);
            await _webHostLanguageWorkerChannelManager?.ShutdownChannelsAsync();
        }

        private void StartWorkerProcesses(int startIndex, Func<Task> startAction)
        {
            Task.Run(async () =>
            {
                for (var count = startIndex; count < _maxProcessCount
                    && !_processStartCancellationToken.IsCancellationRequested; count++)
                {
                    if (_concurrencyOptions.Value.Enabled && count > 0)
                    {
                        // Make sure only one worker is started if concurancy is enabled
                        return;
                    }
                    try
                    {
                        await startAction();
                        await Task.Delay(_processStartupInterval);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to start a new language worker for runtime: {_workerRuntime}.");
                    }
                }
            }, _processStartCancellationToken.Token);
        }

        public async Task InitializeAsync(IEnumerable<FunctionMetadata> functions, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_environment.IsPlaceholderModeEnabled())
            {
                return;
            }

            _workerRuntime = _workerRuntime ?? Utility.GetWorkerRuntime(functions);
            _functions = functions;
            if (string.IsNullOrEmpty(_workerRuntime) || _workerRuntime.Equals(RpcWorkerConstants.DotNetLanguageWorkerName, StringComparison.InvariantCultureIgnoreCase))
            {
                // Shutdown any placeholder channels for empty function apps or dotnet function apps.
                // This is needed as specilization does not kill standby placeholder channels if worker runtime is not set.
                // Debouce to ensure this does not effect cold start
                _shutdownStandbyWorkerChannels();
                return;
            }

            if (functions == null || functions.Count() == 0)
            {
                // do not initialize function dispatcher if there are no functions
                return;
            }

            var workerConfig = _workerConfigs.Where(c => c.Description.Language.Equals(_workerRuntime, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (workerConfig == null)
            {
                // Only throw if workerConfig is null AND some functions have been found.
                // With .NET out-of-proc, worker config comes from functions.
                throw new InvalidOperationException($"WorkerCofig for runtime: {_workerRuntime} not found");
            }
            _maxProcessCount = _concurrencyOptions.Value.Enabled ? _concurrencyOptions.Value.MaxWorkerCount : workerConfig.CountOptions.ProcessCount;
            _processStartupInterval = workerConfig.CountOptions.ProcessStartupInterval;
            _restartWait = workerConfig.CountOptions.ProcessRestartInterval;
            _shutdownTimeout = workerConfig.CountOptions.ProcessShutdownTimeout;
            ErrorEventsThreshold = 3 * _maxProcessCount;

            if (Utility.IsSupportedRuntime(_workerRuntime, _workerConfigs))
            {
                State = FunctionInvocationDispatcherState.Initializing;
                Dictionary<string, TaskCompletionSource<IRpcWorkerChannel>> webhostLanguageWorkerChannels = _webHostLanguageWorkerChannelManager.GetChannels(_workerRuntime);
                if (webhostLanguageWorkerChannels != null)
                {
                    foreach (string workerId in webhostLanguageWorkerChannels.Keys.ToList())
                    {
                        if (webhostLanguageWorkerChannels.TryGetValue(workerId, out TaskCompletionSource<IRpcWorkerChannel> initializedLanguageWorkerChannelTask))
                        {
                            _logger.LogDebug("Found initialized language worker channel for runtime: {workerRuntime} workerId:{workerId}", _workerRuntime, workerId);
                            try
                            {
                                IRpcWorkerChannel initializedLanguageWorkerChannel = await initializedLanguageWorkerChannelTask.Task;
                                initializedLanguageWorkerChannel.SetupFunctionInvocationBuffers(_functions);
                                initializedLanguageWorkerChannel.SendFunctionLoadRequests(_managedDependencyOptions.Value, _scriptOptions.FunctionTimeout);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Removing errored webhost language worker channel for runtime: {workerRuntime} workerId:{workerId}", _workerRuntime, workerId);
                                await _webHostLanguageWorkerChannelManager.ShutdownChannelIfExistsAsync(_workerRuntime, workerId, ex);
                            }
                        }
                    }
                    StartWorkerProcesses(webhostLanguageWorkerChannels.Count(), InitializeWebhostLanguageWorkerChannel);
                    if (webhostLanguageWorkerChannels.Any())
                    {
                        SetFunctionDispatcherStateToInitializedAndLog();
                    }
                }
                else
                {
                    StartWorkerProcesses(0, InitializeJobhostLanguageWorkerChannelAsync);
                }
            }
        }

        public async Task<IDictionary<string, WorkerStatus>> GetWorkerStatusesAsync()
        {
            var workerChannels = (await GetInitializedWorkerChannelsAsync()).ToArray();
            _logger.LogDebug($"[HostMonitor] Checking worker statuses (Count={workerChannels.Length})");

            // invoke the requests to each channel in parallel
            var workerStatuses = new Dictionary<string, WorkerStatus>(StringComparer.OrdinalIgnoreCase);
            var tasks = new List<Task<WorkerStatus>>();
            foreach (var channel in workerChannels)
            {
                tasks.Add(channel.GetWorkerStatusAsync());
            }

            await Task.WhenAll(tasks);

            for (int i = 0; i < workerChannels.Length; i++)
            {
                var workerChannel = workerChannels[i];
                var workerStatus = tasks[i].Result;
                _logger.LogDebug($"[HostMonitor] Worker status: ID={workerChannel.Id}, Latency={Math.Round(workerStatus.Latency.TotalMilliseconds)}ms");
                workerStatuses.Add(workerChannel.Id, workerStatus);
            }

            return workerStatuses;
        }

        public async Task ShutdownAsync()
        {
            _logger.LogDebug($"Waiting for {nameof(RpcFunctionInvocationDispatcher)} to shutdown");
            Task timeoutTask = Task.Delay(_shutdownTimeout);
            IList<Task> workerChannelTasks = (await GetInitializedWorkerChannelsAsync()).Select(a => a.DrainInvocationsAsync()).ToList();
            Task completedTask = await Task.WhenAny(timeoutTask, Task.WhenAll(workerChannelTasks));

            if (completedTask.Equals(timeoutTask))
            {
                _logger.LogDebug($"Draining invocations from language worker channel timed out. Shutting down '{nameof(RpcFunctionInvocationDispatcher)}'");
            }
            else
            {
                _logger.LogDebug($"Draining invocations from language worker channel completed. Shutting down '{nameof(RpcFunctionInvocationDispatcher)}'");
            }
        }

        public async Task InvokeAsync(ScriptInvocationContext invocationContext)
        {
            // This could throw if no initialized workers are found. Shut down instance and retry.
            IEnumerable<IRpcWorkerChannel> workerChannels = await GetInitializedWorkerChannelsAsync();
            var rpcWorkerChannel = _functionDispatcherLoadBalancer.GetLanguageWorkerChannel(workerChannels);
            if (rpcWorkerChannel.FunctionInputBuffers.TryGetValue(invocationContext.FunctionMetadata.GetFunctionId(), out BufferBlock<ScriptInvocationContext> bufferBlock))
            {
                _logger.LogTrace("Posting invocation id:{InvocationId} on workerId:{workerChannelId}", invocationContext.ExecutionContext.InvocationId, rpcWorkerChannel.Id);
                rpcWorkerChannel.FunctionInputBuffers[invocationContext.FunctionMetadata.GetFunctionId()].Post(invocationContext);
            }
            else
            {
                throw new InvalidOperationException($"Function:{invocationContext.FunctionMetadata.Name} is not loaded by the language worker: {rpcWorkerChannel.Id}");
            }
        }

        internal async Task<IEnumerable<IRpcWorkerChannel>> GetAllWorkerChannelsAsync()
        {
            var webhostChannelDictionary = _webHostLanguageWorkerChannelManager.GetChannels(_workerRuntime);
            List<IRpcWorkerChannel> webhostChannels = null;
            if (webhostChannelDictionary != null)
            {
                webhostChannels = new List<IRpcWorkerChannel>();
                foreach (var pair in webhostChannelDictionary)
                {
                    var workerChannel = await pair.Value.Task;
                    webhostChannels.Add(workerChannel);
                }
            }

            IEnumerable<IRpcWorkerChannel> workerChannels = webhostChannels == null ? _jobHostLanguageWorkerChannelManager.GetChannels() : webhostChannels.Union(_jobHostLanguageWorkerChannelManager.GetChannels());
            return workerChannels;
        }

        internal async Task<IEnumerable<IRpcWorkerChannel>> GetInitializedWorkerChannelsAsync()
        {
            IEnumerable<IRpcWorkerChannel> workerChannels = await GetAllWorkerChannelsAsync();
            IEnumerable<IRpcWorkerChannel> initializedWorkers = workerChannels.Where(ch => ch.IsChannelReadyForInvocations());
            if (initializedWorkers.Count() > _maxProcessCount)
            {
                throw new InvalidOperationException($"Number of initialized language workers exceeded:{initializedWorkers.Count()} exceeded maxProcessCount: {_maxProcessCount}");
            }

            return initializedWorkers;
        }

        public async void WorkerError(WorkerErrorEvent workerError)
        {
            if (_disposing || _disposed)
            {
                return;
            }

            if (string.Equals(_workerRuntime, workerError.Language))
            {
                _logger.LogDebug("Handling WorkerErrorEvent for runtime:{runtime}, workerId:{workerId}. Failed with: {exception}", workerError.Language, _workerRuntime, workerError.Exception);
                AddOrUpdateErrorBucket(workerError);
                await DisposeAndRestartWorkerChannel(workerError.Language, workerError.WorkerId, workerError.Exception);
            }
            else
            {
                _logger.LogDebug("Received WorkerErrorEvent for runtime:{runtime}, workerId:{workerId}", workerError.Language, workerError.WorkerId);
                _logger.LogDebug("WorkerErrorEvent runtime:{runtime} does not match current runtime:{currentRuntime}. Failed with: {exception}", workerError.Language, _workerRuntime, workerError.Exception);
            }
        }

        public async void WorkerRestart(WorkerRestartEvent workerRestart)
        {
            if (_disposing || _disposed)
            {
                return;
            }

            _logger.LogDebug("Handling WorkerRestartEvent for runtime:{runtime}, workerId:{workerId}", workerRestart.Language, workerRestart.WorkerId);
            await DisposeAndRestartWorkerChannel(workerRestart.Language, workerRestart.WorkerId);
        }

        public async Task StartWorkerChannel()
        {
            await StartWorkerChannel(null);
        }

        private async Task DisposeAndRestartWorkerChannel(string runtime, string workerId, Exception workerException = null)
        {
            if (_disposing || _disposed)
            {
                return;
            }

            _logger.LogDebug("Attempting to dispose webhost or jobhost channel for workerId: '{channelId}', runtime: '{language}'", workerId, runtime);
            bool isWebHostChannelDisposed = await _webHostLanguageWorkerChannelManager.ShutdownChannelIfExistsAsync(runtime, workerId, workerException);
            bool isJobHostChannelDisposed = false;
            if (!isWebHostChannelDisposed)
            {
                isJobHostChannelDisposed = await _jobHostLanguageWorkerChannelManager.ShutdownChannelIfExistsAsync(workerId, workerException);
            }

            if (!isWebHostChannelDisposed && !isJobHostChannelDisposed)
            {
                _logger.LogDebug("Did not find WebHost or JobHost channel to dispose for workerId: '{channelId}', runtime: '{language}'", workerId, runtime);
            }

            if (ShouldRestartWorkerChannel(runtime, isWebHostChannelDisposed, isJobHostChannelDisposed))
            {
                // Set state to "WorkerProcessRestarting" if there are no other workers to handle work
                if ((await GetInitializedWorkerChannelsAsync()).Count() == 0)
                {
                    State = FunctionInvocationDispatcherState.WorkerProcessRestarting;
                    _logger.LogDebug("No initialized worker channels for runtime '{runtime}'. Delaying future invocations", runtime);
                }
                // Restart worker channel
                _logger.LogDebug("Restarting worker channel for runtime: '{runtime}'", runtime);
                await StartWorkerChannel(runtime);
                // State is set back to "Initialized" when worker channel is up again
            }
            else
            {
                _logger.LogDebug("Skipping worker channel restart for errored worker runtime: '{runtime}', current runtime: '{currentRuntime}', isWebHostChannel: '{isWebHostChannel}', isJobHostChannel: '{isJobHostChannel}'",
                    runtime, _workerRuntime, isWebHostChannelDisposed, isJobHostChannelDisposed);
            }
        }

        internal bool ShouldRestartWorkerChannel(string runtime, bool isWebHostChannel, bool isJobHostChannel)
        {
            return string.Equals(_workerRuntime, runtime, StringComparison.InvariantCultureIgnoreCase) && (isWebHostChannel || isJobHostChannel);
        }

        private async Task StartWorkerChannel(string runtime)
        {
            if (_disposing || _disposed)
            {
                return;
            }

            if (string.IsNullOrEmpty(runtime))
            {
                runtime = _workerRuntime;
            }

            if (_languageWorkerErrors.Count < ErrorEventsThreshold)
            {
                try
                {
                    // Issue only one restart at a time.
                    await _startWorkerProcessSLock.WaitAsync();
                    await InitializeJobhostLanguageWorkerChannelAsync(_languageWorkerErrors.Count);
                }
                finally
                {
                    // Wait before releasing the lock to give time for the process to startup and initialize.
                    try
                    {
                        await Task.Delay(_restartWait, _disposeToken.Token);
                        _startWorkerProcessSLock.Release();
                    }
                    catch (TaskCanceledException)
                    {
                    }
                }
            }
            else if (_jobHostLanguageWorkerChannelManager.GetChannels().Count() == 0)
            {
                _logger.LogError("Exceeded language worker restart retry count for runtime:{runtime}. Shutting down and proactively recycling the Functions Host to recover", runtime);
                _applicationLifetime.StopApplication();
            }
        }

        private void AddOrUpdateErrorBucket(WorkerErrorEvent currentErrorEvent)
        {
            if (_languageWorkerErrors.TryPeek(out WorkerErrorEvent top))
            {
                if ((currentErrorEvent.CreatedAt - top.CreatedAt) > _thresholdBetweenRestarts)
                {
                    while (!_languageWorkerErrors.IsEmpty)
                    {
                        _languageWorkerErrors.TryPop(out WorkerErrorEvent popped);
                        _logger.LogDebug($"Popping out errorEvent createdAt: '{popped.CreatedAt}' workerId: '{popped.WorkerId}'");
                    }
                }
            }
            _languageWorkerErrors.Push(currentErrorEvent);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (_disposing)
                {
                    _logger.LogDebug("Disposing FunctionDispatcher");
                    _disposeToken.Cancel();
                    _disposeToken.Dispose();
                    _startWorkerProcessSLock.Dispose();
                    _workerErrorSubscription.Dispose();
                    _workerRestartSubscription.Dispose();
                    _processStartCancellationToken.Cancel();
                    _processStartCancellationToken.Dispose();
                    _jobHostLanguageWorkerChannelManager.ShutdownChannels();
                }

                State = FunctionInvocationDispatcherState.Disposed;
                _disposed = true;
            }
        }

        public void Dispose()
        {
            _disposing = true;
            State = FunctionInvocationDispatcherState.Disposing;
            Dispose(true);
        }

        public async Task<bool> RestartWorkerWithInvocationIdAsync(string invocationId)
        {
            // Dispose and restart errored channel with the particular invocation id
            var channels = await GetInitializedWorkerChannelsAsync();
            foreach (var channel in channels)
            {
                if (channel.IsExecutingInvocation(invocationId))
                {
                    _logger.LogDebug($"Restarting channel with workerId: '{channel.Id}' that is executing invocation: '{invocationId}' and timed out.");
                    await DisposeAndRestartWorkerChannel(_workerRuntime, channel.Id);
                    return true;
                }
            }
            return false;
        }
    }
}