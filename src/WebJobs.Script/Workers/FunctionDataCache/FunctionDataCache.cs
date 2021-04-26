// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Collections.Generic;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Workers.FunctionDataCache
{
    public class FunctionDataCache : IFunctionDataCache
    {
        /// <summary>
        /// Logger.
        /// </summary>
        private readonly ILogger _logger;

        private readonly ISharedMemoryManager _sharedMemoryManager;

        /// <summary>
        /// Lock to be used with any operations that access state of this cache.
        /// </summary>
        private readonly object _lock;

        /// <summary>
        /// Mapping of <see cref="FunctionDataCacheKey"/> to the <see cref="SharedMemoryMetadata"/>
        /// which indicates where in shared memory the object exists.
        /// Note: This must be accessed while holding the <see cref="_lock"/> lock.
        /// </summary>
        private readonly Dictionary<FunctionDataCacheKey, SharedMemoryMetadata> _localCache;

        private readonly long _maximumCapacityBytes;

        public FunctionDataCache(ISharedMemoryManager sharedMemoryManager, ILoggerFactory loggerFactory, IEnvironment environment)
        {
            _logger = loggerFactory.CreateLogger<FunctionDataCache>();
            _sharedMemoryManager = sharedMemoryManager;
            _maximumCapacityBytes = GetMaximumCapacityBytes(environment);
            _lock = new object();
            _localCache = new Dictionary<FunctionDataCacheKey, SharedMemoryMetadata>();
            LRUList = new LinkedList<FunctionDataCacheKey>();
            ActiveReferences = new Dictionary<FunctionDataCacheKey, long>();
            RemainingCapacityBytes = _maximumCapacityBytes;
            IsEnabled = true; // TODO USE THE BELOW CHECK
            //IsEnabled = GetIsEnabled(environment);
        }

        public bool IsEnabled { get; private set; }

        internal long RemainingCapacityBytes { get; private set; }

        /// <summary>
        /// Gets the list maintaining the order of use of <see cref="FunctionDataCacheKey"/> in an LRU fashion.
        /// The last element of the list is the most recently used item.
        /// The first element of the list is the least recently used item.
        /// </summary>
        internal LinkedList<FunctionDataCacheKey> LRUList { get; private set; }

        internal Dictionary<FunctionDataCacheKey, long> ActiveReferences { get; private set; }

        public bool TryPut(FunctionDataCacheKey cacheKey, SharedMemoryMetadata sharedMemoryMeta, bool isIncrementActiveReference, bool isDeleteOnFailure)
        {
            bool success = false;

            try
            {
                lock (_lock)
                {
                    // Check if the key is already present in the cache
                    if (_localCache.ContainsKey(cacheKey))
                    {
                        // Key already exists in the local cache; do not overwrite
                        return false;
                    }

                    long bytesRequired = sharedMemoryMeta.Count;
                    if (!EvictUntilCapacityAvailable(bytesRequired))
                    {
                        return false;
                    }

                    // Add the mapping into the local cache
                    _localCache.Add(cacheKey, sharedMemoryMeta);

                    // Update the LRU list (mark this key as the most recently used)
                    AddToEndOfLRU(cacheKey);

                    if (isIncrementActiveReference)
                    {
                        IncrementActiveReference(cacheKey);
                    }

                    // Update the cache utilization
                    RemainingCapacityBytes -= sharedMemoryMeta.Count;

                    success = true;
                    return true;
                }
            }
            finally
            {
                if (!success && isDeleteOnFailure)
                {
                    if (!_sharedMemoryManager.TryFreeSharedMemoryMap(sharedMemoryMeta.MemoryMapName))
                    {
                        _logger.LogTrace("Cannot free shared memory map: {MapName} with size: {Size} bytes on failure to insert into the cache", sharedMemoryMeta.MemoryMapName, sharedMemoryMeta.Count);
                    }
                }
            }
        }

        public bool TryGet(FunctionDataCacheKey cacheKey, bool isIncrementActiveReference, out SharedMemoryMetadata sharedMemoryMeta)
        {
            lock (_lock)
            {
                // Get the value from the local cache
                if (!_localCache.TryGetValue(cacheKey, out sharedMemoryMeta))
                {
                    // Key does not exist in the local cache
                    return false;
                }

                // Update the LRU list (mark this key as the most recently used)
                AddToEndOfLRU(cacheKey);

                if (isIncrementActiveReference)
                {
                    IncrementActiveReference(cacheKey);
                }

                return true;
            }
        }

        /// <summary>
        /// Note: This will remove the entry even if it has active references. It is the responsibility of the caller to
        /// ensure the entry is safe to be removed.
        /// </summary>
        /// <param name="cacheKey">The <see cref="FunctionDataCacheKey"/> corresponding to the entry to be removed.</param>
        /// <returns><see cref="true"/> if the entry was successfully removed, <see cref="false"/> if not.</returns>
        public bool TryRemove(FunctionDataCacheKey cacheKey)
        {
            lock (_lock)
            {
                // TODO remove this Get and check if we can do remove with *out*
                if (!TryGet(cacheKey, isIncrementActiveReference: false, out SharedMemoryMetadata sharedMemoryMeta))
                {
                    return false;
                }

                // Free the shared memory containing data for the given key that is being removed
                if (!_sharedMemoryManager.TryFreeSharedMemoryMap(sharedMemoryMeta.MemoryMapName))
                {
                    // Unable to free the shared memory
                    return false;
                }

                // Remove the key from the local cache
                if (!_localCache.Remove(cacheKey))
                {
                    // Key does not exist in the local cache
                    return false;
                }

                // Remove from LRU list
                RemoveFromLRU(cacheKey);

                // Remove the key from the list of active references
                ActiveReferences.Remove(cacheKey);

                // Update the cache utilization
                RemainingCapacityBytes += sharedMemoryMeta.Count;

                return true;
            }
        }

        public void DecrementActiveReference(FunctionDataCacheKey cacheKey)
        {
            lock (_lock)
            {
                DecrementActiveReferenceCore(cacheKey);
            }
        }

        public void Dispose()
        {
            _logger.LogTrace("TODO");
        }

        private long GetMaximumCapacityBytes(IEnvironment environment)
        {
            string capacityVal = environment.GetEnvironmentVariable(FunctionDataCacheConstants.FunctionDataCacheMaximumSizeBytesSettingName);
            if (!string.IsNullOrEmpty(capacityVal))
            {
                if (long.TryParse(capacityVal, out long capacity))
                {
                    return capacity;
                }
            }
            return FunctionDataCacheConstants.FunctionDataCacheDefaultMaximumSizeBytes;
        }

        private bool GetIsEnabled(IEnvironment environment)
        {
            // Check if the environment variable (AppSetting) has this feature enabled
            string envVal = environment.GetEnvironmentVariable(FunctionDataCacheConstants.FunctionDataCacheEnabledSettingName);
            if (string.IsNullOrEmpty(envVal))
            {
                return false;
            }

            bool envValEnabled = false;
            if (bool.TryParse(envVal, out bool boolResult))
            {
                // Check if value was specified as a bool (true/false)
                envValEnabled = boolResult;
            }
            else if (int.TryParse(envVal, out int intResult) && intResult == 1)
            {
                // Check if value was specified as an int (1/0)
                envValEnabled = true;
            }

            return envValEnabled;
        }

        private void IncrementActiveReference(FunctionDataCacheKey cacheKey)
        {
            // TODO mention assumes lock
            long activeReferences = 0;
            if (ActiveReferences.TryGetValue(cacheKey, out activeReferences))
            {
                ActiveReferences.Remove(cacheKey);
            }
            ActiveReferences.Add(cacheKey, activeReferences + 1);
        }

        private void DecrementActiveReferenceCore(FunctionDataCacheKey cacheKey)
        {
            long activeReferences = 0;
            if (ActiveReferences.TryGetValue(cacheKey, out activeReferences))
            {
                ActiveReferences.Remove(cacheKey);
                if (activeReferences > 1)
                {
                    ActiveReferences.Add(cacheKey, activeReferences - 1);
                }
            }
        }

        internal bool EvictOne()
        {
            // TODO add docstring to mention that this assumes lock is taken
            FunctionDataCacheKey cacheKey = GetFromFrontOfLRU();
            if (cacheKey == null)
            {
                return false;
            }

            return TryRemove(cacheKey);
        }

        internal bool EvictUntilCapacityAvailable(long capacityRequiredBytes)
        {
            // TODO add docstring to mention that this assumes lock is taken
            if (capacityRequiredBytes > _maximumCapacityBytes)
            {
                return false;
            }

            while (RemainingCapacityBytes < capacityRequiredBytes)
            {
                if (!EvictOne())
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Adds the given key to the end of the LRU list.
        /// This key is the most recently used key.
        /// Assumes that the key is not already present.
        /// Note: Assumes that it is called in a thread-safe manner (i.e. <see cref="_lock"/> is held
        /// by the caller.)
        /// </summary>
        /// <param name="cacheKey">Key to add to the end of the LRU list.</param>
        private void AddToEndOfLRU(FunctionDataCacheKey cacheKey)
        {
            if (LRUList.Contains(cacheKey))
            {
                RemoveFromLRU(cacheKey);
            }

            LRUList.AddLast(cacheKey);
        }

        /// <summary>
        /// Gets the key at the front of the LRU list.
        /// This key is the least recently used key.
        /// </summary>
        /// <returns>The first <see cref="FunctionDataCacheKey"/> from the front of the list which does not have any active references,
        /// or <see cref="null"/> if no such valid key is found.</returns>
        private FunctionDataCacheKey GetFromFrontOfLRU()
        {
            if (LRUList.Count == 0)
            {
                return null;
            }

            foreach (FunctionDataCacheKey cacheKey in LRUList)
            {
                if (!ActiveReferences.ContainsKey(cacheKey))
                {
                    return cacheKey;
                }
            }

            return null;
        }

        private void RemoveFromLRU(FunctionDataCacheKey cacheKey)
        {
            LRUList.Remove(cacheKey);
        }
    }
}
