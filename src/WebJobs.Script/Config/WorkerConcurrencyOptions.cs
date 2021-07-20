// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script
{
    public class WorkerConcurrencyOptions : Hosting.IOptionsFormatter
    {
        public WorkerConcurrencyOptions()
        {
            // Setting deafault values
            LatencyThreshold = TimeSpan.FromSeconds(1);
            AdjustmentPeriod = TimeSpan.FromSeconds(10);
            CheckInterval = TimeSpan.FromSeconds(1);
            HistorySize = 10;
            HistoryThreshold = 1F;
        }

        /// <summary>
        /// Gets or sets a value indicating whether worker concurrency
        /// is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the latency threshold dictating when worker channel is overloaded.
        /// </summary>
        public TimeSpan LatencyThreshold { get; set; }

        /// <summary>
        /// Gets or sets the time when worker
        /// channels will start to monitor again after adding a new worker.
        /// </summary>
        public TimeSpan AdjustmentPeriod { get; set; }

        /// <summary>
        /// Gets or sets interval to check worker channel state.
        /// </summary>
        public TimeSpan CheckInterval { get; set; }

        /// <summary>
        /// Gets or sets the history size to store workers channel states.
        /// </summary>
        public int HistorySize { get; set; }

        /// <summary>
        /// Gets or sets the history threshold.
        /// E. g. value equal to 1.0 means all states in history should be
        /// overloaded to consider worker state as overloaded.
        /// </summary>
        public float HistoryThreshold { get; set; }

        /// <summary>
        /// Gets or sets the max count of workers.
        /// It will be set depending on SKU if equal to 0.
        /// </summary>
        public int MaxWorkerCount { get; set; }

        public string Format()
        {
            var options = new JObject
            {
                { nameof(Enabled), Enabled },
                { nameof(LatencyThreshold), LatencyThreshold },
                { nameof(AdjustmentPeriod), AdjustmentPeriod },
                { nameof(CheckInterval), CheckInterval },
                { nameof(HistorySize), HistorySize },
                { nameof(HistoryThreshold), HistoryThreshold },
                { nameof(MaxWorkerCount), MaxWorkerCount }
            };

            return options.ToString(Formatting.Indented);
        }
    }
}
