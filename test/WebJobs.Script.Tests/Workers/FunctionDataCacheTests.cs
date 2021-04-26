// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Workers.FunctionDataCache;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions; // TODO remove

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers
{
    /// <summary>
    /// Tests for <see cref="FunctionDataCacheTests"/>.
    /// </summary>
    public class FunctionDataCacheTests
    {
        private readonly ILoggerFactory _loggerFactory;

        private readonly IEnvironment _testEnvironment;

        private readonly IMemoryMappedFileAccessor _mapAccessor;

        private readonly ITestOutputHelper _output;

        public FunctionDataCacheTests(ITestOutputHelper output)
        {
            _output = output;

            _loggerFactory = MockNullLoggerFactory.CreateLoggerFactory();

            ILogger<MemoryMappedFileAccessor> logger = NullLogger<MemoryMappedFileAccessor>.Instance;

            _testEnvironment = new TestEnvironment();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _mapAccessor = new MemoryMappedFileAccessorWindows(logger);
            }
            else
            {
                _mapAccessor = new MemoryMappedFileAccessorUnix(logger, _testEnvironment);
            }
        }

        private void PrintLRU(FunctionDataCache cache)
        {
            StringBuilder builder = new StringBuilder();
            foreach (FunctionDataCacheKey key in cache.LRUList)
            {
                builder.Append($"{key.Id} -> ");
            }
            builder.Append("\n");
            _output.WriteLine(builder.ToString());
        }

        [InlineData(10)]
        [InlineData(100)]
        [InlineData(1024)] // 1KB
        [InlineData(1024 * 1024)] // 1MB
        [InlineData(15 * 1024 * 1024)] // 15MB
        [Theory]
        public async Task PutObject_NoEvictions_VerifyGet(int contentSize)
        {
            using (ISharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            using (FunctionDataCache cache = new FunctionDataCache(manager, _loggerFactory, _testEnvironment))
            {
                // Prepare content
                byte[] content = TestUtils.GetRandomBytesInArray(contentSize);

                // Put into shared memory
                SharedMemoryMetadata metadata = await manager.PutObjectAsync(content);

                // Put into cache
                FunctionDataCacheKey key = new FunctionDataCacheKey("foo", "bar");
                Assert.True(cache.TryPut(key, metadata, isIncrementActiveReference: false, isDeleteOnFailure: false));

                // Get from cache
                Assert.True(cache.TryGet(key, isIncrementActiveReference: false, out SharedMemoryMetadata getMetadata));

                // Compare if the obtained values are equal
                Assert.Equal(metadata, getMetadata);
            }
        }

        [Fact]
        public async Task PutThreeObjects_VerifyLRUOrder()
        {
            int contentSize = 2 * 1024 * 1024; // 2MB

            using (ISharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            using (FunctionDataCache cache = new FunctionDataCache(manager, _loggerFactory, _testEnvironment))
            {
                // Prepare content
                byte[] content = TestUtils.GetRandomBytesInArray(contentSize);

                // Put into shared memory as three distinct objects
                List<SharedMemoryMetadata> metadatas = new List<SharedMemoryMetadata>()
                {
                    await manager.PutObjectAsync(content),
                    await manager.PutObjectAsync(content),
                    await manager.PutObjectAsync(content),
                };

                // Put the three objects into the cache
                List<FunctionDataCacheKey> keys = new List<FunctionDataCacheKey>()
                {
                    new FunctionDataCacheKey("foo_1", "bar_1"),
                    new FunctionDataCacheKey("foo_2", "bar_2"),
                    new FunctionDataCacheKey("foo_3", "bar_3"),
                };
                for (int i = 0; i < 3; i++)
                {
                    Assert.True(cache.TryPut(keys[i], metadatas[i], isIncrementActiveReference: false, isDeleteOnFailure: false));
                }

                // The order of the LRU list should be the same as that in which objects were inserted above.
                // i.e. the first element of the list should be the least recently used (oldest inserted).
                int pos = 1;
                foreach (FunctionDataCacheKey key in cache.LRUList)
                {
                    string keyId = key.Id;
                    int num = int.Parse(keyId.Split("_")[1]);
                    Assert.Equal(pos, num);
                    pos++;
                }
            }
        }

        [Fact]
        public async Task PutThreeObjects_GetOne_VerifyLRUOrder()
        {
            int contentSize = 2 * 1024 * 1024; // 2MB

            using (ISharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            using (FunctionDataCache cache = new FunctionDataCache(manager, _loggerFactory, _testEnvironment))
            {
                // Prepare content
                byte[] content = TestUtils.GetRandomBytesInArray(contentSize);

                // Put into shared memory as three distinct objects
                List<SharedMemoryMetadata> metadatas = new List<SharedMemoryMetadata>()
                {
                    await manager.PutObjectAsync(content),
                    await manager.PutObjectAsync(content),
                    await manager.PutObjectAsync(content),
                };

                // Put the three objects into the cache
                List<FunctionDataCacheKey> keys = new List<FunctionDataCacheKey>()
                {
                    new FunctionDataCacheKey("foo_1", "bar_1"),
                    new FunctionDataCacheKey("foo_2", "bar_2"),
                    new FunctionDataCacheKey("foo_3", "bar_3"),
                };
                for (int i = 0; i < 3; i++)
                {
                    Assert.True(cache.TryPut(keys[i], metadatas[i], isIncrementActiveReference: false, isDeleteOnFailure: false));
                }

                // Access the middle object so that now it is the most recently used
                Assert.True(cache.TryGet(keys[1], isIncrementActiveReference: false, out var _));

                // The order of objects in LRU should be 1->3->2.
                // Since 2 is the most recently used, it is add the end of the list.
                int pos = 1;
                foreach (FunctionDataCacheKey key in cache.LRUList)
                {
                    string keyId = key.Id;
                    int num = int.Parse(keyId.Split("_")[1]);

                    int expected;
                    if (pos == 1)
                    {
                        expected = 1;
                    }
                    else if (pos == 2)
                    {
                        expected = 3;
                    }
                    else if (pos == 3)
                    {
                        expected = 2;
                    }
                    else
                    {
                        throw new Exception("Unexpected position");
                    }

                    Assert.Equal(expected, num);
                    pos++;
                }
            }
        }

        [Fact]
        public async Task PutThreeObjects_GetOne_NoActiveReferences_VerifyEvictionOrder()
        {
            int contentSize = 2 * 1024 * 1024; // 2MB

            using (ISharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            using (FunctionDataCache cache = new FunctionDataCache(manager, _loggerFactory, _testEnvironment))
            {
                // Prepare content
                byte[] content = TestUtils.GetRandomBytesInArray(contentSize);

                // Put into shared memory as three distinct objects
                List<SharedMemoryMetadata> metadatas = new List<SharedMemoryMetadata>()
                {
                    await manager.PutObjectAsync(content),
                    await manager.PutObjectAsync(content),
                    await manager.PutObjectAsync(content),
                };

                // Put the three objects into the cache
                List<FunctionDataCacheKey> keys = new List<FunctionDataCacheKey>()
                {
                    new FunctionDataCacheKey("foo_1", "bar_1"),
                    new FunctionDataCacheKey("foo_2", "bar_2"),
                    new FunctionDataCacheKey("foo_3", "bar_3"),
                };
                for (int i = 0; i < 3; i++)
                {
                    Assert.True(cache.TryPut(keys[i], metadatas[i], isIncrementActiveReference: false, isDeleteOnFailure: false));
                }

                // Access the middle object so that now it is the most recently used
                Assert.True(cache.TryGet(keys[1], isIncrementActiveReference: false, out var _));

                // The order of objects in LRU should be 1->3->2.
                // Since 2 is the most recently used, it is add the end of the list.

                // Evict an object and check that the right object was evicted (1)
                Assert.True(cache.EvictOne());
                Assert.False(cache.TryGet(keys[0], isIncrementActiveReference: false, out var _));
                // Check if the other objects are still present;
                // We cannot check using TryGet as that will impact the LRU ordering
                Assert.Contains(keys[1], cache.LRUList);
                Assert.Contains(keys[2], cache.LRUList);

                // Evict an object and check that the right object was evicted (3)
                Assert.True(cache.EvictOne());
                Assert.False(cache.TryGet(keys[2], isIncrementActiveReference: false, out var _));
                // Check if the other object are still present;
                // We cannot check using TryGet as that will impact the LRU ordering
                Assert.Contains(keys[1], cache.LRUList);

                // Evict an object and check that the right object was evicted (2)
                Assert.True(cache.EvictOne());
                Assert.Empty(cache.LRUList);
            }
        }

        [Fact]
        public async Task PutObject_ActiveReference_VerifyNotEvicted()
        {
            int contentSize = 2 * 1024 * 1024; // 2MB
            int cacheSize = 3 * 1024 * 1024; // 3MB
            string cacheSizeVal = cacheSize.ToString();

            IEnvironment environment = new TestEnvironment();
            environment.SetEnvironmentVariable(FunctionDataCacheConstants.FunctionDataCacheMaximumSizeBytesSettingName, cacheSizeVal);

            using (ISharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            using (FunctionDataCache cache = new FunctionDataCache(manager, _loggerFactory, environment))
            {
                // Prepare content
                byte[] content = TestUtils.GetRandomBytesInArray(contentSize);

                // Put into shared memory as two objects
                SharedMemoryMetadata metadata1 = await manager.PutObjectAsync(content);
                SharedMemoryMetadata metadata2 = await manager.PutObjectAsync(content);

                // Put one object into the cache and keep an active reference
                FunctionDataCacheKey key1 = new FunctionDataCacheKey("foo1", "bar1");
                Assert.True(cache.TryPut(key1, metadata1, isIncrementActiveReference: true, isDeleteOnFailure: false));

                // The first object has used up the cache space.
                // When trying to insert the second object into the cache, it should fail
                // since the first has an active reference and cannot be evicted.
                FunctionDataCacheKey key2 = new FunctionDataCacheKey("foo2", "bar2");
                Assert.False(cache.TryPut(key2, metadata2, isIncrementActiveReference: false, isDeleteOnFailure: false));
                // Ensure that the first object was not evicted
                Assert.True(cache.TryGet(key1, isIncrementActiveReference: false, out var _));

                // Drop the active reference on the first object
                cache.DecrementActiveReference(key1);

                // Now, when trying to insert the second object into the cache, it should succeed
                // since the first object can be evicted (since its active reference was dropped).
                Assert.True(cache.TryPut(key2, metadata2, isIncrementActiveReference: false, isDeleteOnFailure: false));
                // Ensure that the first object was evicted
                Assert.False(cache.TryGet(key1, isIncrementActiveReference: false, out var _));
            }
        }

        [Fact]
        public async Task PutObject_NoActiveReferences_ForceOneEviction_VerifyCorrectEviction()
        {
            int contentSize = 2 * 1024 * 1024; // 2MB
            int cacheSize = 6 * 1024 * 1024; // 6MB
            string cacheSizeVal = cacheSize.ToString();

            IEnvironment environment = new TestEnvironment();
            environment.SetEnvironmentVariable(FunctionDataCacheConstants.FunctionDataCacheMaximumSizeBytesSettingName, cacheSizeVal);

            using (ISharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            using (FunctionDataCache cache = new FunctionDataCache(manager, _loggerFactory, environment))
            {
                // Prepare content
                byte[] content = TestUtils.GetRandomBytesInArray(contentSize);

                // Put into shared memory as three distinct objects
                SharedMemoryMetadata metadata1 = await manager.PutObjectAsync(content);
                SharedMemoryMetadata metadata2 = await manager.PutObjectAsync(content);
                SharedMemoryMetadata metadata3 = await manager.PutObjectAsync(content);

                // Put the three objects into the cache
                FunctionDataCacheKey key1 = new FunctionDataCacheKey("foo1", "bar1");
                FunctionDataCacheKey key2 = new FunctionDataCacheKey("foo2", "bar2");
                FunctionDataCacheKey key3 = new FunctionDataCacheKey("foo3", "bar3");
                Assert.True(cache.TryPut(key1, metadata1, isIncrementActiveReference: false, isDeleteOnFailure: false));
                Assert.True(cache.TryPut(key2, metadata2, isIncrementActiveReference: false, isDeleteOnFailure: false));
                Assert.True(cache.TryPut(key3, metadata3, isIncrementActiveReference: false, isDeleteOnFailure: false));

                // Verify that the cache is full
                Assert.Equal(0, cache.RemainingCapacityBytes);

                // At this point, the cache is full.
                // We will create another object and try to insert it.
                SharedMemoryMetadata metadata4 = await manager.PutObjectAsync(content);
                FunctionDataCacheKey key4 = new FunctionDataCacheKey("foo4", "bar4");
                Assert.True(cache.TryPut(key4, metadata4, isIncrementActiveReference: false, isDeleteOnFailure: false));

                // The first object should be evicted (least recently used) by now
                Assert.False(cache.TryGet(key1, isIncrementActiveReference: false, out var _));

                // Try to open the shared memory map of the first object and ensure it is removed and cannot be opened
                Assert.False(_mapAccessor.TryOpen(metadata1.MemoryMapName, out var _));

                // The last three objects (the first two added before eviction and the one resulting in eviction) should be present
                Assert.True(cache.TryGet(key2, isIncrementActiveReference: false, out var _));
                Assert.True(cache.TryGet(key3, isIncrementActiveReference: false, out var _));
                Assert.True(cache.TryGet(key4, isIncrementActiveReference: false, out var _));

                // Verify that the cache is full
                Assert.Equal(0, cache.RemainingCapacityBytes);
            }
        }

        [Fact]
        public async Task PutObject_NoActiveReferences_ForceMultipleEvictions_VerifyCorrectEvictions()
        {
            int contentSizeInitial = 2 * 1024 * 1024; // 2MB
            int cacheSize = 6 * 1024 * 1024; // 6MB
            int contentSizeFinal = 5 * 1024 * 1024; // 5MB
            string cacheSizeVal = cacheSize.ToString();

            IEnvironment environment = new TestEnvironment();
            environment.SetEnvironmentVariable(FunctionDataCacheConstants.FunctionDataCacheMaximumSizeBytesSettingName, cacheSizeVal);

            using (ISharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            using (FunctionDataCache cache = new FunctionDataCache(manager, _loggerFactory, environment))
            {
                // Prepare content
                byte[] contentInitial = TestUtils.GetRandomBytesInArray(contentSizeInitial);

                // Put into shared memory as three distinct objects
                SharedMemoryMetadata metadata1 = await manager.PutObjectAsync(contentInitial);
                SharedMemoryMetadata metadata2 = await manager.PutObjectAsync(contentInitial);

                // Put the two objects into the cache
                FunctionDataCacheKey key1 = new FunctionDataCacheKey("foo1", "bar1");
                FunctionDataCacheKey key2 = new FunctionDataCacheKey("foo2", "bar2");
                Assert.True(cache.TryPut(key1, metadata1, isIncrementActiveReference: false, isDeleteOnFailure: false));
                Assert.True(cache.TryPut(key2, metadata2, isIncrementActiveReference: false, isDeleteOnFailure: false));

                // At this point, the cache is full.
                // We will create another object and try to insert it.
                byte[] contentFinal = TestUtils.GetRandomBytesInArray(contentSizeFinal);
                SharedMemoryMetadata metadata3 = await manager.PutObjectAsync(contentFinal);
                FunctionDataCacheKey key3 = new FunctionDataCacheKey("foo3", "bar3");
                Assert.True(cache.TryPut(key3, metadata3, isIncrementActiveReference: false, isDeleteOnFailure: false));

                // The first two objects should be evicted by now
                Assert.False(cache.TryGet(key1, isIncrementActiveReference: false, out var _));
                Assert.False(cache.TryGet(key2, isIncrementActiveReference: false, out var _));

                // Try to open the shared memory map of the first object and ensure it is removed and cannot be opened
                Assert.False(_mapAccessor.TryOpen(metadata1.MemoryMapName, out var _));
                Assert.False(_mapAccessor.TryOpen(metadata2.MemoryMapName, out var _));

                // The last inserted object should be present
                Assert.True(cache.TryGet(key3, isIncrementActiveReference: false, out var _));

                // Verify that the cache has 1MB remaining
                Assert.Equal(1 * 1024 * 1024, cache.RemainingCapacityBytes);
            }
        }
    }
}
