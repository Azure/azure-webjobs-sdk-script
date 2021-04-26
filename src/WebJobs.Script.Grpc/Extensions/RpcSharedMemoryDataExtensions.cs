// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Grpc.Extensions
{
    internal static class RpcSharedMemoryDataExtensions
    {
        internal static async Task<RpcSharedMemory> ToRpcSharedMemoryAsync(this object value, ILogger logger, string invocationId, ISharedMemoryManager sharedMemoryManager, IFunctionDataCache functionDataCache)
        {
            if (value == null)
            {
                return new RpcSharedMemory();
            }

            if (!sharedMemoryManager.IsSupported(value))
            {
                return null;
            }

            SharedMemoryMetadata sharedMemoryMeta;
            bool needToFreeAfterInvocation = true;

            if (value is SharedMemoryMetadata)
            {
                // Content was already present in shared memory (cache hit)
                logger.LogTrace("Object already present in shared memory for invocation id: {Id}", invocationId);
                sharedMemoryMeta = value as SharedMemoryMetadata;
                needToFreeAfterInvocation = false;
            }
            else
            {
                // Put the content into shared memory and get the name of the shared memory map written to.
                // This will make the SharedMemoryManager keep an active reference to the memory map.
                sharedMemoryMeta = await sharedMemoryManager.PutObjectAsync(value);
                if (sharedMemoryMeta == null)
                {
                    logger.LogTrace("Cannot write to shared memory for invocation id: {Id}", invocationId);
                    return null;
                }

                if (functionDataCache.IsEnabled)
                {
                    if (value is CacheableObjectStream)
                    {
                        CacheableObjectStream cacheableObj = value as CacheableObjectStream;
                        FunctionDataCacheKey cacheKey = cacheableObj.CacheKey;

                        // Try to add the object into the cache and keep an active ref-count for it so that it does not get
                        // evicted while it is still being used by the invocation.
                        if (cacheableObj.TryCacheObject(sharedMemoryMeta))
                        {
                            logger.LogTrace("Put object: {CacheKey} in cache with metadata: {SharedMemoryMetadata} for invocation id: {Id}", cacheKey, sharedMemoryMeta, invocationId);
                            // We don't need to free the object after the invocation; it will be freed as part of the cache's
                            // eviction policy.
                            needToFreeAfterInvocation = false;
                        }
                        else
                        {
                            logger.LogTrace("Cannot put object: {CacheKey} in cache with metadata: {SharedMemoryMetadata} for invocation id: {Id}", cacheKey, sharedMemoryMeta, invocationId);
                            // Since we could not add this object to the cache (and therefore the cache will not be able to evict
                            // it as part of its eviction policy) we will need to free it after the invocation is done.
                            needToFreeAfterInvocation = true;
                        }
                    }
                }
            }

            RpcDataType? dataType = GetRpcDataType(value);
            if (!dataType.HasValue)
            {
                logger.LogTrace("Cannot get shared memory data type for invocation id: {Id}", invocationId);
                return null;
            }

            // When using the cache, we don't need to free the memory map after using it;
            // it will be freed as per the eviction policy of the cache.
            // However, if either the cache was not enabled or the object could not be added to the cache,
            // we will need to free it after the invocation.
            if (needToFreeAfterInvocation)
            {
                // If written to shared memory successfully, add this shared memory map to the list of maps for this invocation
                // so that once the invocation is over, the memory map's resources can be freed.
                sharedMemoryManager.AddSharedMemoryMapForInvocation(invocationId, sharedMemoryMeta.MemoryMapName);
            }

            // Generate a response
            RpcSharedMemory sharedMem = new RpcSharedMemory()
            {
                Name = sharedMemoryMeta.MemoryMapName,
                Offset = 0,
                Count = sharedMemoryMeta.Count,
                Type = dataType.Value
            };

            logger.LogTrace("Put object in shared memory for invocation id: {Id}", invocationId);
            return sharedMem;
        }

        internal static async Task<object> ToObjectAsync(this RpcSharedMemory sharedMem, ILogger logger, string invocationId, ISharedMemoryManager sharedMemoryManager, bool isFunctionDataCacheEnabled)
        {
            // Data was transferred by the worker using shared memory
            string mapName = sharedMem.Name;
            int offset = (int)sharedMem.Offset;
            int count = (int)sharedMem.Count;
            logger.LogTrace("Shared memory data transfer for invocation id: {Id} with shared memory map name: {MapName} and size: {Size} bytes", invocationId, mapName, count);

            switch (sharedMem.Type)
            {
                case RpcDataType.Bytes:
                    // If cache is enabled, we hold a reference (in the host) to the memory map created by the worker
                    // so that the worker can be asked to drop its reference.
                    // We will later add this object into the cache and then the memory map will be freed as per the eviction
                    // logic of the cache.
                    if (isFunctionDataCacheEnabled)
                    {
                        // This is where the SharedMemoryManager will hold a reference to the memory map
                        if (sharedMemoryManager.TryTrackSharedMemoryMap(mapName))
                        {
                            return await sharedMemoryManager.GetObjectAsync(mapName, offset, count, typeof(SharedMemoryObject));
                        }
                    }

                    // If the cache is not used, we copy the object content from the memory map so that the worker
                    // can be asked to drop the reference to the memory map and also free the memory map.
                    return await sharedMemoryManager.GetObjectAsync(mapName, offset, count, typeof(byte[]));
                case RpcDataType.String:
                    return await sharedMemoryManager.GetObjectAsync(mapName, offset, count, typeof(string));
                default:
                    logger.LogError("Unsupported shared memory data type: {SharedMemDataType} for invocation id: {Id}", sharedMem.Type, invocationId);
                    throw new InvalidDataException($"Unsupported shared memory data type: {sharedMem.Type}");
            }
        }

        private static RpcDataType? GetRpcDataType(object value)
        {
            if (value is byte[])
            {
                return RpcDataType.Bytes;
            }
            else if (value is CacheableObjectStream)
            {
                return RpcDataType.Bytes;
            }
            else if (value is SharedMemoryMetadata)
            {
                return RpcDataType.Bytes;
            }
            else if (value is string)
            {
                return RpcDataType.String;
            }

            return null;
        }
    }
}
