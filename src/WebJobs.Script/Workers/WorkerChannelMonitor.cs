// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.Workers
{
    internal class WorkerChannelMonitor : IDisposable
    {
        private readonly List<TimeSpan> _workerStatusLatecyHistory = new List<TimeSpan>();
        private readonly IOptions<WorkerConcurrencyOptions> _concurrencyOptions;

        private IWorkerChannel _channel;
        private object _syncLock = new object();

        private System.Timers.Timer _timer;
        private bool _disposed = false;

        internal WorkerChannelMonitor(IWorkerChannel channel, IOptions<WorkerConcurrencyOptions> concurrencyOptions)
        {
            _channel = channel;
            _concurrencyOptions = concurrencyOptions;
        }

        internal void EnsureTimerStarted()
        {
            if (_timer == null && _concurrencyOptions.Value.Enabled)
            {
                lock (_syncLock)
                {
                    if (_timer == null)
                    {
                        _timer = new System.Timers.Timer()
                        {
                            AutoReset = false,
                            Interval = _concurrencyOptions.Value.CheckInterval.TotalMilliseconds,
                        };

                        _timer.Elapsed += OnTimer;
                        _timer.Start();
                    }
                }
            }
        }

        public WorkerStats GetStats()
        {
            EnsureTimerStarted();

            WorkerStats stats = null;
            lock (_syncLock)
            {
                stats = new WorkerStats()
                {
                    LatencyHistory = _workerStatusLatecyHistory
                };
            }
            return stats;
        }

        internal async void OnTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                WorkerStatus workerStatus = await _channel.GetWorkerStatusAsync();
                AddSample(_workerStatusLatecyHistory, workerStatus.Latency);
            }
            catch
            {
                // Don't allow backround execptions to escape
                // E.g. when a rpc channel is shutting down we can process exceptions
            }
            _timer.Start();
        }

        private void AddSample<T>(List<T> samples, T sample)
        {
            lock (_syncLock)
            {
                if (samples.Count == _concurrencyOptions.Value.HistorySize)
                {
                    samples.RemoveAt(0);
                }
                samples.Add(sample);
            }
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
    }
}
