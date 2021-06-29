// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Runtime.Serialization;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Models
{
    // Important: Needs to be in sync with container init process
    public enum ContainerHealthEventType
    {
        [EnumMember]
        Informational,

        [EnumMember]
        Warning,

        [EnumMember]
        Fatal
    }
}
