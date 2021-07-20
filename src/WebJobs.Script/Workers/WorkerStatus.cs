// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using Microsoft.Azure.WebJobs.Script.Scale;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;

namespace Microsoft.Azure.WebJobs.Script.Workers
{
    /// <summary>
    /// Represents the health status of a worker.
    /// </summary>
    public class WorkerStatus
    {
        /// <summary>
        /// Gets or sets the current latency for worker channel.
        /// </summary>
        public TimeSpan Latency { get; set; }

        /// <summary>
        /// Gets or sets the worker latency statistic.
        /// </summary>
        public WorkerStats WorkerStats { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether worker is ready
        /// </summary>
        public bool IsReady { get; set; }
    }
}
