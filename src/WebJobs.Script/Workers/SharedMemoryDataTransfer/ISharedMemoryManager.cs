// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;

namespace Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer
{
    public interface ISharedMemoryManager : IDisposable
    {
        /// <summary>
        /// Put an object into shared memory.
        /// Note:
        /// Tracks the reference to the shared memory map after creating it and does not close it.
        /// </summary>
        /// <param name="input">Object to put into shared memory.</param>
        /// <returns>Metadata about the shared memory map for the object put into shared memory.</returns>
        Task<SharedMemoryMetadata> PutObjectAsync(object input);

        /// <summary>
        /// Get an object stored in a particular region of shared memory.
        /// </summary>
        /// <param name="mapName">Name of the shared memory map containing the object.</param>
        /// <param name="offset">Offset into the content section of the shared memory map to start reading from.</param>
        /// <param name="count">Number of bytes, starting from the offset, to read.</param>
        /// <param name="objectType">Type into which to convert the returned object.</param>
        /// <returns>Object obtained from shared memory.</returns>
        Task<object> GetObjectAsync(string mapName, int offset, int count, Type objectType);

        /// <summary>
        /// Check if a particular type of object can be transferred over shared memory.
        /// The object type must be one that is supported.
        /// Its size must be greater than the minimum threshold for transferring objects over shared memory.
        /// </summary>
        /// <param name="input">Object being checked for shared memory transfer support.</param>
        /// <returns><see cref="true"/> if the object can be transferred over shared memory, <see cref="false"/> otherwise.</returns>
        bool IsSupported(object input);

        /// <summary>
        /// Track a particular shared memory map corresponding to a particular invocation.
        /// Used to keep track of which shared memory maps were generated for which invocation.
        /// </summary>
        /// <param name="invocationId">ID of the invocation for which the shared memory map was generated for.</param>
        /// <param name="mapName">Name of the shared memory map.</param>
        void AddSharedMemoryMapForInvocation(string invocationId, string mapName);

        /// <summary>
        /// Free all shared memory maps that were generated for the given invocation.
        /// </summary>
        /// <param name="invocationId">ID of the invocation for which the shared memory maps need to be freed.</param>
        /// <returns><see cref="true"/> if all shared memory maps for the invocation were freed, <see cref="false"/> otherwise.</returns>
        bool TryFreeSharedMemoryMapsForInvocation(string invocationId);

        /// <summary>
        /// Free the shared memory map with the given name.
        /// </summary>
        /// <param name="mapName">Name of the shared memory map to free.</param>
        /// <returns><see cref="true"/> if the shared memory map was freed, <see cref="false"/> otherwise.</returns>
        bool TryFreeSharedMemoryMap(string mapName);

        /// <summary>
        /// Track a particular shared memory map.
        /// Used to hold a reference to a shared memory map so the OS does not clean it up.
        /// </summary>
        /// <param name="mapName">Name of the shared memory map.</param>
        /// </summary>
        bool TryTrackSharedMemoryMap(string mapName);
    }
}
