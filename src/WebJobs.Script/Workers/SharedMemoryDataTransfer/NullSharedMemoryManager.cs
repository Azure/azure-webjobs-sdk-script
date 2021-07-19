// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;

namespace Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer
{
    public class NullSharedMemoryManager : ISharedMemoryManager
    {
        public void AddSharedMemoryMapForInvocation(string invocationId, string mapName)
        {
        }

        public void Dispose()
        {
        }

        public Task<object> GetObjectAsync(string mapName, int offset, int count, Type objectType)
        {
            return null;
        }

        public bool IsSupported(object input)
        {
            return false;
        }

        public Task<SharedMemoryMetadata> PutObjectAsync(object input)
        {
            return null;
        }

        public bool TryFreeSharedMemoryMap(string mapName)
        {
            return true;
        }

        public bool TryFreeSharedMemoryMapsForInvocation(string invocationId)
        {
            return true;
        }
    }
}
