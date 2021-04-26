// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers
{
    /// <summary>
    /// Tests for <see cref="SharedMemoryManager"/>.
    /// </summary>
    public class SharedMemoryManagerTests
    {
        private readonly IMemoryMappedFileAccessor _mapAccessor;

        private readonly ILoggerFactory _loggerFactory;

        private readonly IEnvironment _testEnvironment;

        public SharedMemoryManagerTests()
        {
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

        /// <summary>
        /// Put a <see cref="byte[]"/> object into shared memory.
        /// </summary>
        /// <param name="contentSize">Size of <see cref="byte[]"/> to put in number of bytes.</param>
        [InlineData(10)]
        [InlineData(100)]
        [InlineData(1024)] // 1KB
        [InlineData(1024 * 1024)] // 1MB
        [InlineData(256 * 1024 * 1024)] // 256MB
        [InlineData(1024 * 1024 * 1024, Skip = "Results in OOM when run on test infra")] // 1GB
        [Theory]
        public async Task PutObject_ByteArray_VerifySuccess(int contentSize)
        {
            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                // Prepare content
                byte[] content = TestUtils.GetRandomBytesInArray(contentSize);

                // Put into shared memory
                SharedMemoryMetadata metadata = await manager.PutObjectAsync(content);

                // Verify expected results
                Assert.NotNull(metadata);
                Assert.NotNull(metadata.MemoryMapName);
                Assert.True(Guid.TryParse(metadata.MemoryMapName, out _));
                Assert.Equal(contentSize, metadata.Count);
            }
        }

        /// <summary>
        /// Put a <see cref="string"/> object into shared memory.
        /// </summary>
        /// <param name="content">String content to put.</param>
        [InlineData("")]
        [InlineData("foo")]
        [Theory]
        public async Task PutObject_String_VerifySuccess(string content)
        {
            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                // Put into shared memory
                SharedMemoryMetadata metadata = await manager.PutObjectAsync(content);

                // Verify expected results
                Assert.NotNull(metadata);
                Assert.NotNull(metadata.MemoryMapName);
                Assert.True(Guid.TryParse(metadata.MemoryMapName, out _));
                Assert.Equal(content.Length, metadata.Count);
            }
        }

        /// <summary>
        /// Get a <see cref="byte[]"/> object from shared memory.
        /// </summary>
        /// <param name="contentSize">Size of <see cref="byte[]"/> to put/get in number of bytes.</param>
        [InlineData(10)]
        [InlineData(100)]
        [InlineData(1024)] // 1KB
        [InlineData(1024 * 1024)] // 1MB
        [InlineData(256 * 1024 * 1024)] // 256MB
        [InlineData(1024 * 1024 * 1024, Skip = "Results in OOM when run on test infra")] // 1GB
        [Theory]
        public async Task GetObject_ByteArray_VerifyMatches(int contentSize)
        {
            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                // Prepare content and put into shared memory
                byte[] content = TestUtils.GetRandomBytesInArray(contentSize);
                SharedMemoryMetadata metadata = await manager.PutObjectAsync(content);

                // Get object from shared memory
                object readObject = await manager.GetObjectAsync(metadata.MemoryMapName, 0, contentSize, typeof(byte[]));
                byte[] readContent = readObject as byte[];

                // Verify read content matches the content that was written
                Assert.True(TestUtils.UnsafeCompare(content, readContent));
            }
        }

        /// <summary>
        /// Get a <see cref="string"/> object from shared memory.
        /// </summary>
        /// <param name="content">String content to put/get.</param>
        [InlineData("")]
        [InlineData("foo")]
        [Theory]
        public async Task GetObject_String_VerifyMatches(string content)
        {
            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                // Put content into shared memory
                SharedMemoryMetadata metadata = await manager.PutObjectAsync(content);

                // Get object from shared memory
                object readObject = await manager.GetObjectAsync(metadata.MemoryMapName, 0, content.Length, typeof(string));
                string readContent = readObject as string;

                // Verify read content matches the content that was written
                Assert.Equal(content, readContent);
            }
        }

        /// <summary>
        /// Add mappings of invocation ID to shared memory map names.
        /// Verify correct mappings exist for each invocation ID.
        /// </summary>
        [Fact]
        public void AddSharedMemoryMapsForInvocation_VerifySuccess()
        {
            // Prepare two invocation IDs and shared memory map names corresponding to each
            string invocationId1 = Guid.NewGuid().ToString();
            string invocationId2 = Guid.NewGuid().ToString();
            List<string> mapNames1 = new List<string>
            {
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString()
            };
            List<string> mapNames2 = new List<string>
            {
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString()
            };

            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                // Create mappings of invocation ID with shared memory map names
                mapNames1.ForEach(mapName => manager.AddSharedMemoryMapForInvocation(invocationId1, mapName));
                mapNames2.ForEach(mapName => manager.AddSharedMemoryMapForInvocation(invocationId2, mapName));

                // Verify the mappings
                Assert.True(manager.InvocationSharedMemoryMaps.TryGetValue(invocationId1, out HashSet<string> invocationMapNames1));
                Assert.True(manager.InvocationSharedMemoryMaps.TryGetValue(invocationId2, out HashSet<string> invocationMapNames2));
                Assert.Equal(mapNames1, invocationMapNames1);
                Assert.Equal(mapNames2, invocationMapNames2);
            }
        }

        [Fact]
        public void AddDuplicateSharedMemoryMapsForInvocation_VerifyOnlyOneIsAdded()
        {
            // Prepare invocation ID and shared memory map names
            string invocationId = Guid.NewGuid().ToString();
            string mapName = Guid.NewGuid().ToString();

            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                // Create mapping of invocation ID with same shared memory map name, twice
                manager.AddSharedMemoryMapForInvocation(invocationId, mapName);
                manager.AddSharedMemoryMapForInvocation(invocationId, mapName);

                // Verify only one mapping exists
                Assert.True(manager.InvocationSharedMemoryMaps.TryGetValue(invocationId, out HashSet<string> mapNames));
                Assert.Single(mapNames);
            }
        }

        /// <summary>
        /// Put an object into shared memory and try to free the shared memory map that was created for it.
        /// Verify that the shared memory map was freed and cannot be opened.
        /// </summary>
        [Fact]
        public async Task FreeSharedMemoryMap_VerifySuccess()
        {
            // Prepare content
            string content = "foobar";

            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                // Put content into shared memory
                SharedMemoryMetadata metadata = await manager.PutObjectAsync(content);
                string mapName = metadata.MemoryMapName;

                // Free the shared memory map and try top open it after freeing; should not open
                Assert.True(manager.TryFreeSharedMemoryMap(mapName));
                Assert.False(_mapAccessor.TryOpen(mapName, out _));
            }
        }

        /// <summary>
        /// Put objects into shared memory and create mappings for them for two different invocations.
        /// For one invocation, free shared memory maps associated with it.
        /// For the other invocation, do not free shared memory maps associated with it.
        /// Verify that the shared memory maps for the first invocation have been freed and cannot be opened.
        /// Verify that the shared memory maps for the second invocation are available and can be opened.
        /// </summary>
        [Fact]
        public async Task FreeSharedMemoryMapsForInvocation_VerifySuccess()
        {
            // Prepare content
            string invocationId1 = Guid.NewGuid().ToString();
            string invocationId2 = Guid.NewGuid().ToString();
            string content = "foobar";

            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                // Put content into shared memory and add mapping to invocations
                SharedMemoryMetadata metadata1 = await manager.PutObjectAsync(content);
                string mapName1 = metadata1.MemoryMapName;
                manager.AddSharedMemoryMapForInvocation(invocationId1, mapName1);
                SharedMemoryMetadata metadata2 = await manager.PutObjectAsync(content);
                string mapName2 = metadata2.MemoryMapName;
                manager.AddSharedMemoryMapForInvocation(invocationId2, mapName2);

                // Free the shared memory maps for invocation1 and try top open it after freeing; should not open
                Assert.True(manager.TryFreeSharedMemoryMapsForInvocation(invocationId1));
                Assert.False(_mapAccessor.TryOpen(mapName1, out _));

                // Shared memory maps for invocation2 should still be available to open
                Assert.True(_mapAccessor.TryOpen(mapName2, out MemoryMappedFile mmf));
                mmf.Dispose();
            }
        }

        /// <summary>
        /// Try to free a shared memory map that does not exist.
        /// Verify that an attempt to free it fails.
        /// </summary>
        [Fact]
        public void FreeNonExistentSharedMemoryMap_VerifyFailure()
        {
            string mapName = Guid.NewGuid().ToString();

            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                Assert.False(manager.TryFreeSharedMemoryMap(mapName));
            }
        }

        /// <summary>
        /// Try to free a shared memory maps for an invocation that does not exist.
        /// Verify that it succeeds.
        /// </summary>
        [Fact]
        public void FreeNonExistentInvocationIdSharedMemoryMaps_VerifySuccess()
        {
            string invocationId = Guid.NewGuid().ToString();

            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                Assert.True(manager.TryFreeSharedMemoryMapsForInvocation(invocationId));
            }
        }

        /// <summary>
        /// Verify that the objects are supported for shared memory data transfer.
        /// i.e. they meet the type and minimum size requirements.
        /// </summary>
        [Fact]
        public void TestSupportedObjects()
        {
            // 5MB byte[]
            object objectA = TestUtils.GetRandomBytesInArray(5 * 1024 * 1024);

            // string containing 5 * 1024 * 1024 chars (total size = 5 * 1024 * 1024 * sizeof(char))
            object objectB = new StringBuilder().Append('a', 5 * 1024 * 1024).ToString();

            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                Assert.True(manager.IsSupported(objectA));
                Assert.True(manager.IsSupported(objectB));
            }
        }

        /// <summary>
        /// Verify that the objects are not supported for shared memory data transfer.
        /// i.e. they fail to meet the type and/or minimum/maximum size requirements.
        /// </summary>
        [Fact]
        public void TestUnsupportedObjects()
        {
            // Objects that don't meet the minimum size requirements
            object objectA = new byte[5];
            object objectB = new string("abc");

            // Objects that don't meet the type requirements
            object objectC = new int[5 * 1024 * 1024];

            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                Assert.False(manager.IsSupported(objectA));
                Assert.False(manager.IsSupported(objectB));
                Assert.False(manager.IsSupported(objectC));
            }
        }

        /// <summary>
        /// Put objects into shared memory.
        /// Then dispose the shared memory manager.
        /// Verify all shared memory maps are now freed.
        /// </summary>
        [Fact]
        public async Task Dispose_VerifyAllSharedMemoryResourcesFreed()
        {
            // Prepare content
            string invocationId1 = Guid.NewGuid().ToString();
            string invocationId2 = Guid.NewGuid().ToString();
            string content = "foobar";

            SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor);

            // Put content into shared memory and add mapping to invocations
            SharedMemoryMetadata metadata1 = await manager.PutObjectAsync(content);
            string mapName1 = metadata1.MemoryMapName;
            manager.AddSharedMemoryMapForInvocation(invocationId1, mapName1);
            SharedMemoryMetadata metadata2 = await manager.PutObjectAsync(content);
            string mapName2 = metadata2.MemoryMapName;
            manager.AddSharedMemoryMapForInvocation(invocationId2, mapName2);

            // Open the shared memory map; should open
            Assert.True(_mapAccessor.TryOpen(mapName1, out MemoryMappedFile mmf1));
            Assert.True(_mapAccessor.TryOpen(mapName1, out MemoryMappedFile mmf2));
            mmf1.Dispose();
            mmf2.Dispose();

            // Dispose the shared memory manager; all shared memory maps it was tracking should be freed
            manager.Dispose();

            // Open the shared memory map; should not open
            Assert.False(_mapAccessor.TryOpen(mapName1, out _));
            Assert.False(_mapAccessor.TryOpen(mapName1, out _));
        }

        /// <summary>
        /// Try to create two <see cref="SharedMemoryMap"/> with the same name.
        /// Verify that the second attempt to create fails.
        /// This test tries various inputs which have content length, which when converted to bytes, has the first byte as \x00.
        /// E.g. content length == 256, or 512.
        /// This should, however, still be detected as an initialized memory map because we have an extra byte in the header (first byte) which is set to \x01 when the memory map is created.
        /// Since we check the first byte of the memory map and compare it against \x01 to distinguish new and initialized memory maps, the byte representation of the content length does not matter.
        /// </summary>
        /// <param name="contentSize">Size of memory map to allocate.</param>
        [InlineData(1)]
        [InlineData(100)]
        [InlineData(256)]
        [InlineData(512)]
        [Theory]
        public void CreateTwoNewSharedMemoryMapsWithSameName_VerifyFailure(long contentSize)
        {
            string mapName = Guid.NewGuid().ToString();

            using (SharedMemoryManager manager = new SharedMemoryManager(_loggerFactory, _mapAccessor))
            {
                Assert.NotNull(manager.Create(mapName, contentSize));
                Assert.Null(manager.Create(mapName, contentSize));
            }
        }
    }
}
