// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;

namespace Microsoft.Azure.WebJobs.Script.Workers
{
    public class WorkerStats
    {
        public IEnumerable<TimeSpan> LatencyHistory { get; set; }
    }
}
