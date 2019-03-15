﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks.Dataflow;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FunctionMetadata = Microsoft.Azure.WebJobs.Script.Description.FunctionMetadata;

namespace Microsoft.Azure.WebJobs.Script.Rpc
{
    internal class FunctionDispatcher : IFunctionDispatcher
    {
        private readonly IMetricsLogger _metricsLogger;
        private readonly ILogger _logger;
        private IScriptEventManager _eventManager;
        private IEnumerable<WorkerConfig> _workerConfigs;
        private CreateChannel _channelFactory;
        private ILanguageWorkerChannelManager _languageWorkerChannelManager;
        private LanguageWorkerState _workerState = new LanguageWorkerState();
        private IDisposable _workerErrorSubscription;
        private IList<IDisposable> _workerStateSubscriptions = new List<IDisposable>();
        private ScriptJobHostOptions _scriptOptions;
        private bool disposedValue = false;

        public FunctionDispatcher(IOptions<ScriptJobHostOptions> scriptHostOptions,
            IMetricsLogger metricsLogger,
            IScriptEventManager eventManager,
            ILoggerFactory loggerFactory,
            IOptions<LanguageWorkerOptions> languageWorkerOptions,
            ILanguageWorkerChannelManager languageWorkerChannelManager)
        {
            _metricsLogger = metricsLogger;
            _scriptOptions = scriptHostOptions.Value;
            _languageWorkerChannelManager = languageWorkerChannelManager;
            _eventManager = eventManager;
            _workerConfigs = languageWorkerOptions.Value.WorkerConfigs;
            _logger = loggerFactory.CreateLogger(ScriptConstants.LogCategoryFunctionDispatcher);

            _workerErrorSubscription = _eventManager.OfType<WorkerErrorEvent>()
               .Subscribe(WorkerError);
        }

        public LanguageWorkerState LanguageWorkerChannelState => _workerState;

        internal List<Exception> Errors { get; set; } = new List<Exception>();

        internal ReplaySubject<FunctionRegistrationContext> Functions { get; set; } = new ReplaySubject<FunctionRegistrationContext>();

        internal CreateChannel ChannelFactory
        {
            get
            {
                if (_channelFactory == null)
                {
                    _channelFactory = (language, registrations, attemptCount) =>
                    {
                        string workerId = Guid.NewGuid().ToString();
                        var languageWorkerChannel = _languageWorkerChannelManager.CreateLanguageWorkerChannel(workerId, _scriptOptions.RootScriptPath, language, registrations, _metricsLogger, attemptCount);
                        var languageWorkerProcess = _languageWorkerChannelManager.StartWorkerProcess(workerId, language, _scriptOptions.RootScriptPath);
                        return Tuple.Create(languageWorkerChannel, languageWorkerProcess);
                    };
                }
                return _channelFactory;
            }
        }

        public bool IsSupported(FunctionMetadata functionMetadata, string workerRuntime)
        {
            if (string.IsNullOrEmpty(functionMetadata.Language))
            {
                return false;
            }
            if (string.IsNullOrEmpty(workerRuntime))
            {
                return true;
            }
            return functionMetadata.Language.Equals(workerRuntime, StringComparison.OrdinalIgnoreCase);
        }

        public void CreateWorkerStateWithExistingChannel(string language, ILanguageWorkerChannel languageWorkerChannel)
        {
            WorkerConfig config = _workerConfigs.Where(c => c.Language.Equals(language, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            languageWorkerChannel.RegisterFunctions(_workerState.Functions);
        }

        public void Initialize(string workerRuntime, IEnumerable<FunctionMetadata> functions)
        {
            _languageWorkerChannelManager.ShutdownStandbyChannels(functions);

            if (Utility.IsSupportedRuntime(workerRuntime, _workerConfigs))
            {
                ILanguageWorkerChannel initializedChannel = _languageWorkerChannelManager.GetChannel(workerRuntime);
                if (initializedChannel != null)
                {
                    _logger.LogDebug("Found initialized language worker channel for runtime: {workerRuntime}", workerRuntime);
                    CreateWorkerStateWithExistingChannel(workerRuntime, initializedChannel);
                }
                else
                {
                    _logger.LogDebug("Creating new language worker channel for runtime:{workerRuntime}", workerRuntime);
                    CreateWorkerState(workerRuntime);
                }
            }
        }

        private void CreateWorkerState(string runtime)
        {
            WorkerConfig config = _workerConfigs.Where(c => c.Language.Equals(runtime, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            Tuple<ILanguageWorkerChannel, ILanguageWorkerProcess> workerChannelAndProcess = ChannelFactory(runtime, _workerState.Functions, 0);
            _workerState.Channel = workerChannelAndProcess.Item1;
            _workerState.WorkerProcess = workerChannelAndProcess.Item2;
        }

        public void Register(FunctionRegistrationContext context)
        {
            _workerState.Functions.OnNext(context);
        }

        public void WorkerError(WorkerErrorEvent workerError)
        {
            _logger.LogDebug("Handling WorkerErrorEvent for runtime:{runtime}", workerError.Language);
            _workerState.Errors.Add(workerError.Exception);
            bool isPreInitializedChannel = _languageWorkerChannelManager.ShutdownChannelIfExists(workerError.Language);
            if (!isPreInitializedChannel)
            {
              _logger.LogDebug("Disposing errored channel for workerId: {channelId}, for runtime:{language}", _workerState.Channel.WorkerId, workerError.Language);
              _workerState.Channel.Dispose();
              _workerState.WorkerProcess.Dispose();
            }
            _logger.LogDebug("Restarting worker channel for runtime:{runtime}", workerError.Language);
            RestartWorkerChannel(workerError.Language);
        }

        private void RestartWorkerChannel(string runtime)
        {
            if (_workerState.Errors.Count < 3)
            {
                var newWorkerChannelAndProcess = CreateNewChannelWithExistingWorkerState(runtime);
                _workerState.Channel = newWorkerChannelAndProcess.Item1;
                _workerState.WorkerProcess = newWorkerChannelAndProcess.Item2;
            }
            else
            {
                _logger.LogDebug("Exceeded language worker restart retry count for runtime:{runtime}", runtime);
                PublishWorkerProcessErrorEvent(runtime);
            }
        }

        private void PublishWorkerProcessErrorEvent(string runtime)
        {
            var exMessage = $"Failed to start language worker for: {runtime}";
            var languageWorkerChannelException = (_workerState.Errors != null && _workerState.Errors.Count > 0) ? new LanguageWorkerChannelException(exMessage, new AggregateException(_workerState.Errors.ToList())) : new LanguageWorkerChannelException(exMessage);
            var errorBlock = new ActionBlock<ScriptInvocationContext>(ctx =>
            {
                ctx.ResultSource.TrySetException(languageWorkerChannelException);
            });
            _workerStateSubscriptions.Add(_workerState.Functions.Subscribe(reg =>
            {
                _workerState.AddRegistration(reg);
                reg.InputBuffer.LinkTo(errorBlock);
            }));
            _eventManager.Publish(new WorkerProcessErrorEvent(_workerState.Channel.WorkerId, runtime, languageWorkerChannelException));
        }

        private Tuple<ILanguageWorkerChannel, ILanguageWorkerProcess> CreateNewChannelWithExistingWorkerState(string language)
        {
            WorkerConfig config = _workerConfigs.Where(c => c.Language.Equals(language, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            return ChannelFactory(language, _workerState.Functions, _workerState.Errors.Count);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _workerErrorSubscription.Dispose();
                    foreach (var subscription in _workerStateSubscriptions)
                    {
                        subscription.Dispose();
                    }
                    _workerState.Channel?.Dispose();
                    _workerState.WorkerProcess?.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
