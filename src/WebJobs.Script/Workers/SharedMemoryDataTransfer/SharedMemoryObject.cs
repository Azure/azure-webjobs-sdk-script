// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.IO;

namespace Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer
{
    internal class SharedMemoryObject
    {
        public SharedMemoryObject(string memoryMapName, int count, Stream content)
        {
            MemoryMapName = memoryMapName;
            Content = content;
            Count = count;
        }

        public string MemoryMapName { get; private set; }

        public int Count { get; private set; }

        public Stream Content { get; private set; }
    }
}
