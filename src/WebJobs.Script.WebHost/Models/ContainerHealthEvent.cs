// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Models
{
    public class ContainerHealthEvent
    {
        public DateTime EventTime { get; set; }

        public ContainerHealthEventType EventType { get; set; }

        public string Source { get; set; }

        public string Details { get; set; }
    }
}