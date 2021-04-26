// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer
{
    /// <summary>
    /// Manages accesses to <see cref="SharedMemoryMap"/>.
    /// Also controls lifetime of <see cref="SharedMemoryMap"/> allocated from this component by adding/removing references.
    /// </summary>
    public class SharedMemoryManager : ISharedMemoryManager
    {
        private readonly ILoggerFactory _loggerFactory;

        private readonly ILogger _logger;

        private readonly IMemoryMappedFileAccessor _mapAccessor;

        private bool _isDisposed;

        public SharedMemoryManager(ILoggerFactory loggerFactory, IMemoryMappedFileAccessor mapAccessor)
        {
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<SharedMemoryManager>();
            _mapAccessor = mapAccessor;
            _isDisposed = false;
            AllocatedSharedMemoryMaps = new ConcurrentDictionary<string, SharedMemoryMap>();
            InvocationSharedMemoryMaps = new ConcurrentDictionary<string, HashSet<string>>();
        }

        /// <summary>
        /// Gets mapping of invocation IDs to list of names of shared memory maps allocated for that invocation.
        /// </summary>
        internal ConcurrentDictionary<string, HashSet<string>> InvocationSharedMemoryMaps { get; private set; }

        /// <summary>
        /// Gets mapping of shared memory map names to the <see cref="SharedMemoryMap"/> that were allocated.
        /// </summary>
        internal ConcurrentDictionary<string, SharedMemoryMap> AllocatedSharedMemoryMaps { get; private set; }

        public Task<SharedMemoryMetadata> PutObjectAsync(object input)
        {
            if (input is byte[] arr)
            {
                return PutBytesAsync(arr);
            }
            else if (input is string str)
            {
                return PutStringAsync(str);
            }
            else if (input is Stream stream)
            {
                return PutStreamAsync(stream);
            }

            return null;
        }

        public void AddSharedMemoryMapForInvocation(string invocationId, string mapName)
        {
            HashSet<string> sharedMemoryMaps = InvocationSharedMemoryMaps.GetOrAdd(invocationId, (key) => new HashSet<string>());
            sharedMemoryMaps.Add(mapName);
        }

        public async Task<object> GetObjectAsync(string mapName, int offset, int count, Type objectType)
        {
            if (objectType == typeof(byte[]))
            {
                return await GetBytesAsync(mapName, offset, count);
            }
            else if (objectType == typeof(SharedMemoryObject))
            {
                // TODO add tests
                Stream contentStream = await GetStreamAsync(mapName, offset, count);
                return new SharedMemoryObject(mapName, count, contentStream);
            }
            else if (objectType == typeof(string))
            {
                return await GetStringAsync(mapName, offset, count);
            }

            return null;
        }

        public bool TryFreeSharedMemoryMapsForInvocation(string invocationId)
        {
            if (!InvocationSharedMemoryMaps.TryRemove(invocationId, out HashSet<string> mapNames))
            {
                _logger.LogTrace("No shared memory maps allocated for invocation id: {Id} by the host", invocationId);
                return true;
            }

            bool freedAll = true;
            int numFreed = 0;
            foreach (string mapName in mapNames)
            {
                if (TryFreeSharedMemoryMap(mapName))
                {
                    numFreed++;
                }
                else
                {
                    freedAll = false;
                }
            }

            _logger.LogTrace("Freed shared memory maps for invocation id: {Id} - Count: {Count}, FreedAll: {FreedAll}", invocationId, numFreed, freedAll);
            return freedAll;
        }

        public bool TryFreeSharedMemoryMap(string mapName)
        {
            // Check if the shared memory map was allocated by the host
            if (!AllocatedSharedMemoryMaps.TryRemove(mapName, out SharedMemoryMap sharedMemoryMap))
            {
                // The shared memory map was not allocated by the host (could be allocated by the worker),
                // but we still try to open it.
                sharedMemoryMap = Open(mapName);
                if (sharedMemoryMap == null)
                {
                    // Shared memory map was not found
                    _logger.LogTrace("Cannot find SharedMemoryMap: {mapName}", mapName);
                    return false;
                }
            }

            // Delete the shared memory map and its resources
            sharedMemoryMap.Dispose();
            return true;
        }

        public bool IsSupported(object input)
        {
            if (input is byte[] arr)
            {
                int arrBytes = arr.Length;
                if (arrBytes >= SharedMemoryConstants.MinObjectBytesForSharedMemoryTransfer && arrBytes <= SharedMemoryConstants.MaxObjectBytesForSharedMemoryTransfer)
                {
                    return true;
                }

                _logger.LogTrace("Cannot transfer bytes over shared memory; size {Size} not supported", arrBytes);
                return false;
            }
            else if (input is string str)
            {
                int strBytes = str.Length * sizeof(char);
                if (strBytes >= SharedMemoryConstants.MinObjectBytesForSharedMemoryTransfer && strBytes <= SharedMemoryConstants.MaxObjectBytesForSharedMemoryTransfer)
                {
                    return true;
                }

                _logger.LogTrace("Cannot transfer string over shared memory; size {Size} not supported", strBytes);
                return false;
            }
            else if (input is CacheableObjectStream stream)
            {
                long streamBytes = stream.Length;
                if (streamBytes >= SharedMemoryConstants.MinObjectBytesForSharedMemoryTransfer && streamBytes <= SharedMemoryConstants.MaxObjectBytesForSharedMemoryTransfer)
                {
                    return true;
                }

                _logger.LogTrace("Cannot transfer bytes over shared memory; size {Size} not supported", streamBytes);
                return false;
            }
            else if (input is SharedMemoryMetadata)
            {
                return true;
            }

            Type objType = input.GetType();
            _logger.LogTrace("Cannot transfer object over shared memory; type {Type} not supported", objType);
            return false;
        }

        public bool TryTrackSharedMemoryMap(string mapName)
        {
            SharedMemoryMap sharedMemoryMap = Open(mapName);
            if (sharedMemoryMap == null)
            {
                return false;
            }
            return AllocatedSharedMemoryMaps.TryAdd(mapName, sharedMemoryMap);
        }

        /// <summary>
        /// Dispose all the <see cref="SharedMemoryMap"/> allocated and tracked.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            IList<string> mapNames = AllocatedSharedMemoryMaps.Keys.ToList();
            int numSuccess = 0;
            int numError = 0;

            foreach (string mapName in mapNames)
            {
                if (TryFreeSharedMemoryMap(mapName))
                {
                    numSuccess++;
                }
                else
                {
                    numError++;
                }
            }

            _logger.LogTrace("Successfully freed: {Freed}, Failed to free: {error} shared memory maps", numSuccess, numError);
            _isDisposed = true;
        }

        /// <summary>
        /// Writes the given <see cref="byte[]"/> data into a <see cref="SharedMemoryMap"/>.
        /// Note:
        /// Tracks the reference to the <see cref="SharedMemoryMap"/> after creating it and does not close it.
        /// </summary>
        /// <param name="content">Content to write into shared memory.</param>
        /// <returns>Metadata of the shared memory map into which data is written if successful, <see cref="null"/> otherwise.</returns>
        private async Task<SharedMemoryMetadata> PutBytesAsync(byte[] content)
        {
            // Generate name of shared memory map to write content into
            string mapName = Guid.NewGuid().ToString();

            // Create shared memory map which can hold the content
            long contentSize = content.Length;
            SharedMemoryMap sharedMemoryMap = Create(mapName, contentSize);

            // Ensure the shared memory map was created
            if (sharedMemoryMap == null)
            {
                _logger.LogError("Cannot write content into shared memory");
                return null;
            }

            // Write content into shared memory map
            long bytesWritten = await sharedMemoryMap.PutBytesAsync(content);

            // Ensure that the entire content has been written into the shared memory map
            if (bytesWritten != contentSize)
            {
                _logger.LogError("Cannot write complete content into shared memory map: {MapName}; wrote: {BytesWritten} bytes out of total: {ContentSize} bytes", mapName, bytesWritten, contentSize);
                return null;
            }

            // Track the shared memory map (to keep a reference open so that the OS does not free the memory)
            if (!AllocatedSharedMemoryMaps.TryAdd(mapName, sharedMemoryMap))
            {
                _logger.LogError("Cannot add shared memory map: {MapName} to list of allocated maps", mapName);
                sharedMemoryMap.Dispose();
                return null;
            }

            // Respond back with metadata about the created and written shared memory map
            SharedMemoryMetadata response = new SharedMemoryMetadata(mapName, contentSize);
            return response;
        }

        /// <summary>
        /// Writes the given <see cref="string"/> data into a <see cref="SharedMemoryMap"/>.
        /// Note:
        /// Tracks the reference to the <see cref="SharedMemoryMap"/> after creating it and does not close it.
        /// </summary>
        /// <param name="content">Content to write into shared memory.</param>
        /// <returns>Metadata of the shared memory map into which data is written if successful, <see cref="null"/> otherwise.</returns>
        private Task<SharedMemoryMetadata> PutStringAsync(string content)
        {
            byte[] contentBytes = Encoding.UTF8.GetBytes(content);
            return PutBytesAsync(contentBytes);
        }

        /// <summary>
        /// Writes the given <see cref="Stream"/> data into a <see cref="SharedMemoryMap"/>.
        /// Note:
        /// Tracks the reference to the <see cref="SharedMemoryMap"/> after creating it and does not close it.
        /// </summary>
        /// <param name="content">Content to write into shared memory.</param>
        /// <returns>Metadata of the shared memory map into which data is written if successful, <see cref="null"/> otherwise.</returns>
        private async Task<SharedMemoryMetadata> PutStreamAsync(Stream content)
        {
            // Generate name of shared memory map to write content into
            string mapName = Guid.NewGuid().ToString();

            // Create shared memory map which can hold the content
            long contentSize = content.Length;
            SharedMemoryMap sharedMemoryMap = Create(mapName, contentSize);

            // Ensure the shared memory map was created
            if (sharedMemoryMap == null)
            {
                _logger.LogError("Cannot write content into shared memory");
                return null;
            }

            // Write content into shared memory map
            long bytesWritten = await sharedMemoryMap.PutStreamAsync(content);

            // Ensure that the entire content has been written into the shared memory map
            if (bytesWritten != contentSize)
            {
                _logger.LogError("Cannot write complete content into shared memory map: {MapName}; wrote: {BytesWritten} bytes out of total: {ContentSize} bytes", mapName, bytesWritten, contentSize);
                return null;
            }

            // Track the shared memory map (to keep a reference open so that the OS does not free the memory)
            if (!AllocatedSharedMemoryMaps.TryAdd(mapName, sharedMemoryMap))
            {
                _logger.LogError("Cannot add shared memory map: {MapName} to list of allocated maps", mapName);
                sharedMemoryMap.Dispose();
                return null;
            }

            // Respond back with metadata about the created and written shared memory map
            SharedMemoryMetadata response = new SharedMemoryMetadata(mapName, contentSize);
            return response;
        }

        /// <summary>
        /// Reads data as <see cref="byte[]"/> from the <see cref="SharedMemoryMap"/> with the given name.
        /// </summary>
        /// <param name="mapName">Name of the <see cref="MemoryMappedFile"/> to read from.</param>
        /// <param name="offset">Offset to start reading data from in the <see cref="SharedMemoryMap"/>.</param>
        /// <param name="count">Number of bytes to read from, starting from the offset, in the <see cref="SharedMemoryMap"/>.</param>
        /// <returns>Data read as <see cref="byte[]"/> if successful, <see cref="null"/> otherwise.
        /// </returns>
        private async Task<byte[]> GetBytesAsync(string mapName, int offset, int count)
        {
            SharedMemoryMap sharedMemoryMap = Open(mapName);

            try
            {
                byte[] content = await sharedMemoryMap.GetBytesAsync(offset, count);

                if (content == null)
                {
                    _logger.LogError($"Cannot read content from MemoryMappedFile: {mapName}");
                    throw new Exception($"Cannot read content from MemoryMappedFile: {mapName} with offset: {offset} and count: {count}");
                }

                return content;
            }
            finally
            {
                sharedMemoryMap.Dispose(deleteFile: false);
            }
        }

        /// <summary>
        /// Reads data as <see cref="string"/> from the <see cref="SharedMemoryMap"/> with the given name.
        /// </summary>
        /// <param name="mapName">Name of the <see cref="MemoryMappedFile"/> to read from.</param>
        /// <param name="offset">Offset to start reading data from in the <see cref="SharedMemoryMap"/>.</param>
        /// <param name="count">Number of bytes to read from, starting from the offset, in the <see cref="SharedMemoryMap"/>.</param>
        /// <returns>Data read as <see cref="string"/> if successful, <see cref="null"/> otherwise.
        /// </returns>
        private async Task<string> GetStringAsync(string mapName, int offset, int count)
        {
            byte[] contentBytes = await GetBytesAsync(mapName, offset, count);
            return Encoding.UTF8.GetString(contentBytes);
        }

        /// <summary>
        /// Gets a <see cref="Stream"/> over the data from the <see cref="SharedMemoryMap"/> with the given name.
        /// </summary>
        /// <param name="mapName">Name of the <see cref="MemoryMappedFile"/> to read from.</param>
        /// <param name="offset">Offset to start reading data from in the <see cref="SharedMemoryMap"/>.</param>
        /// <param name="count">Number of bytes to read from, starting from the offset, in the <see cref="SharedMemoryMap"/>.</param>
        /// <returns><see cref="Stream"/> over the data if successful, <see cref="null"/> otherwise.
        /// </returns>
        private async Task<Stream> GetStreamAsync(string mapName, int offset, int count)
        {
            // TODO add tests.
            SharedMemoryMap sharedMemoryMap = Open(mapName);

            try
            {
                Stream content = await sharedMemoryMap.GetStreamAsync(offset, count);

                if (content == null)
                {
                    _logger.LogError($"Cannot get Stream of MemoryMappedFile: {mapName}");
                    throw new Exception($"Cannot get Stream of MemoryMappedFile: {mapName} with offset: {offset} and count: {count}");
                }

                return content;
            }
            finally
            {
                sharedMemoryMap.Dispose(deleteFile: false);
            }
        }

        /// <summary>
        /// Create a <see cref="SharedMemoryMap"/> with the given name and size.
        /// </summary>
        /// <param name="mapName">Size of <see cref="SharedMemoryMap"/> to create.</param>
        /// <param name="contentSize">Size in bytes to allocate for the <see cref="SharedMemoryMap"/>.</param>
        /// <returns><see cref="SharedMemoryMap"/> if created successfully, <see cref="null"/> otherwise.</returns>
        internal SharedMemoryMap Create(string mapName, long contentSize)
        {
            long size = contentSize + SharedMemoryConstants.HeaderTotalBytes;
            if (_mapAccessor.TryCreate(mapName, size, out MemoryMappedFile mmf))
            {
                return new SharedMemoryMap(_loggerFactory, _mapAccessor, mapName, mmf);
            }

            _logger.LogTrace("Cannot create shared memory map: {MapName} with size: {Size} bytes", mapName, contentSize);
            return null;
        }

        /// <summary>
        /// Open a <see cref="SharedMemoryMap"/> with the given name.
        /// </summary>
        /// <param name="mapName">Size of <see cref="SharedMemoryMap"/> to open.</param>
        /// <returns><see cref="SharedMemoryMap"/> if opened successfully, <see cref="null"/> otherwise.</returns>
        private SharedMemoryMap Open(string mapName)
        {
            if (_mapAccessor.TryOpen(mapName, out MemoryMappedFile mmf))
            {
                return new SharedMemoryMap(_loggerFactory, _mapAccessor, mapName, mmf);
            }

            _logger.LogTrace("Cannot open shared memory map: {MapName}", mapName);
            return null;
        }
    }
}