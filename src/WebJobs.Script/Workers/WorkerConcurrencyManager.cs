// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.Workers
{
    internal class WorkerConcurrencyManager : IHostedService, IDisposable
    {
        private readonly TimeSpan _logStateInterval = TimeSpan.FromSeconds(60);
        private readonly IOptions<WorkerConcurrencyOptions> _concurrencyOptions;
        private readonly ILogger _logger;
        private readonly IFunctionInvocationDispatcherFactory _functionInvocationDispatcherFactory;

        private IFunctionInvocationDispatcher _functionInvocationDispatcher;
        private System.Timers.Timer _timer;
        private Stopwatch _addWorkerStopWatch = Stopwatch.StartNew();
        private Stopwatch _logStateStopWatch = Stopwatch.StartNew();
        private bool _disposed = false;

        public WorkerConcurrencyManager(IFunctionInvocationDispatcherFactory functionInvocationDispatcherFactory,
            IOptions<WorkerConcurrencyOptions> concurrencyOptions, ILoggerFactory loggerFactory)
        {
            _concurrencyOptions = concurrencyOptions ?? throw new ArgumentNullException(nameof(concurrencyOptions));
            _functionInvocationDispatcherFactory = functionInvocationDispatcherFactory ?? throw new ArgumentNullException(nameof(functionInvocationDispatcherFactory));

            _logger = loggerFactory?.CreateLogger<WorkerConcurrencyManager>();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_concurrencyOptions.Value.Enabled)
            {
                // Delay monitoring
                await Task.Delay(_concurrencyOptions.Value.AdjustmentPeriod);

                _functionInvocationDispatcher = _functionInvocationDispatcherFactory.GetFunctionDispatcher();
                if (_functionInvocationDispatcher is HttpFunctionInvocationDispatcher)
                {
                    _logger.LogDebug($"Http worker concurrency is not supported.");
                    return;
                }

                _logger.LogDebug($"Starting worker concurrency monitoring. Options: {_concurrencyOptions.Value.Format()}");
                _timer = new System.Timers.Timer()
                {
                    AutoReset = false,
                    Interval = _concurrencyOptions.Value.CheckInterval.TotalMilliseconds,
                };

                _timer.Elapsed += OnTimer;
                _timer.Start();
            }
            else
            {
                _logger.LogDebug($"Language worker concurrency is disabled.");
            }

            await Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_concurrencyOptions.Value.Enabled && _timer != null)
            {
                _logger.LogDebug("Stoping language worker concurrency monitoring.");
                _timer.Stop();
            }
            return Task.CompletedTask;
        }

        internal async void OnTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed && _functionInvocationDispatcher == null)
            {
                return;
            }

            try
            {
                var workerStatuses = await _functionInvocationDispatcher.GetWorkerStatusesAsync();

                if (AddWorkerIfNeeded(workerStatuses, _addWorkerStopWatch.Elapsed))
                {
                    await _functionInvocationDispatcher.StartWorkerChannel();
                    _logger.LogDebug("New worker is added.");
                    _addWorkerStopWatch.Restart();
                }
            }
            catch (Exception ex)
            {
                // don't allow background exceptions to escape
                _logger.LogError(ex.ToString());
            }
            _timer.Start();
        }

        internal bool AddWorkerIfNeeded(IDictionary<string, WorkerStatus> workerStatuses, TimeSpan elaspsedFromLastAdding)
        {
            if (elaspsedFromLastAdding < _concurrencyOptions.Value.AdjustmentPeriod)
            {
                return false;
            }

            bool result = false;
            if (workerStatuses.All(x => x.Value.IsReady))
            {
                // Check how many channels are oveloaded
                List<WorkerDescription> descriptions = new List<WorkerDescription>();
                foreach (string key in workerStatuses.Keys)
                {
                    WorkerStatus workerStatus = workerStatuses[key];
                    bool overloaded = IsOverloaded(workerStatus);
                    descriptions.Add(new WorkerDescription()
                    {
                        WorkerId = key,
                        WorkerStatus = workerStatus,
                        Overloaded = overloaded
                    });
                }

                int overloadedCount = descriptions.Where(x => x.Overloaded == true).Count();
                if (overloadedCount > 0)
                {
                    if (workerStatuses.Count() < _concurrencyOptions.Value.MaxWorkerCount)
                    {
                        _logger.LogDebug($"Adding a new worker, overloaded workers = {overloadedCount}, initialized workers = {workerStatuses.Count()} ");
                        result = true;
                    }
                }

                if (result == true || _logStateStopWatch.Elapsed > _logStateInterval)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (WorkerDescription description in descriptions)
                    {
                        sb.Append(LogWorkerState(description));
                        sb.Append(Environment.NewLine);
                    }
                    _logStateStopWatch.Restart();
                    _logger.LogDebug(sb.ToString());
                }
            }

            return result;
        }

        internal bool IsOverloaded(WorkerStatus status)
        {
            if (status.WorkerStats.LatencyHistory.Count() >= _concurrencyOptions.Value.HistorySize)
            {
                int overloadedCount = status.WorkerStats.LatencyHistory.Where(x => x.TotalMilliseconds >= _concurrencyOptions.Value.LatencyThreshold.TotalMilliseconds).Count();
                double proportion = (double)overloadedCount / _concurrencyOptions.Value.HistorySize;

                return proportion >= _concurrencyOptions.Value.HistoryThreshold;
            }
            return false;
        }

        internal string LogWorkerState(WorkerDescription desc)
        {
            string formattedLoadHistory = string.Empty, formattedLatencyHistory = string.Empty;
            double latencyAvg = 0, latencyMax = 0;
            if (desc.WorkerStatus != null)
            {
                if (desc.WorkerStatus.WorkerStats != null && desc.WorkerStatus.WorkerStats.LatencyHistory != null)
                {
                    formattedLatencyHistory = string.Join(",", desc.WorkerStatus.WorkerStats.LatencyHistory);
                    latencyMax = desc.WorkerStatus.WorkerStats.LatencyHistory.Select(x => x.TotalMilliseconds).Max();
                    if (desc.WorkerStatus.WorkerStats.LatencyHistory.Count() > 1)
                    {
                        latencyAvg = desc.WorkerStatus.WorkerStats.LatencyHistory.Select(x => x.TotalMilliseconds).Average();
                    }
                }
            }

            return $@"Worker process stats: ProcessId={desc.WorkerId}, Overloaded={desc.Overloaded} 
LatencyHistory=({formattedLatencyHistory}), AvgLatency={latencyAvg}, MaxLatency={latencyMax}";
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _timer?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        internal class WorkerDescription
        {
            public string WorkerId { get; set; }

            public WorkerStatus WorkerStatus { get; set; }

            public bool Overloaded { get; set; }
        }
    }
}
