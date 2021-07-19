// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer
{
    /// <summary>
    /// Note: This is platform specific implementation for Linux and OSX.
    /// </summary>
    public class MemoryMappedFileAccessorUnix : MemoryMappedFileAccessor
    {
        private readonly IEnvironment _environment;

        public MemoryMappedFileAccessorUnix(ILogger<MemoryMappedFileAccessor> logger, IEnvironment environment) : base(logger)
        {
            ValidatePlatform(new List<OSPlatform>()
            {
                OSPlatform.Linux,
                OSPlatform.OSX
            });

            _environment = environment;
            ValidDirectories = GetValidDirectories();
        }

        internal List<string> ValidDirectories { get; private set; }

        public override bool TryCreate(string mapName, long size, out MemoryMappedFile mmf)
        {
            mmf = null;

            if (string.IsNullOrEmpty(mapName))
            {
                Logger.LogError("Cannot create MemoryMappedFile with invalid name");
                return false;
            }

            if (size <= 0)
            {
                Logger.LogError("Cannot create MemoryMappedFile: {MapName} with size: {Size} bytes", mapName, size);
                return false;
            }

            try
            {
                // Ensure the file is already not present
                string filePath = GetPath(mapName);
                if (filePath != null && File.Exists(filePath))
                {
                    Logger.LogError("Cannot create MemoryMappedFile: {MapName}, file already exists", mapName);
                    return false;
                }

                // Get path of where to create the new file-based MemoryMappedFile
                filePath = CreatePath(mapName, size);
                if (string.IsNullOrEmpty(filePath))
                {
                    Logger.LogError("Cannot create MemoryMappedFile: {MapName}, invalid file path", mapName);
                    return false;
                }

                mmf = MemoryMappedFile.CreateFromFile(
                    filePath,
                    FileMode.OpenOrCreate,
                    null, // Named maps are not supported on Linux
                    size,
                    MemoryMappedFileAccess.ReadWrite);

                if (IsMemoryMapInitialized(mmf))
                {
                    Logger.LogError("Cannot create MemoryMappedFile: {mapName}, it already exists", mapName);
                    mmf = null;
                    return false;
                }

                SetMemoryMapInitialized(mmf);

                return true;
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Cannot create MemoryMappedFile: {mapName} with size: {Size} bytes", mapName, size);
            }

            return false;
        }

        public override bool TryOpen(string mapName, out MemoryMappedFile mmf)
        {
            mmf = null;

            try
            {
                string filePath = GetPath(mapName);
                if (filePath != null && File.Exists(filePath))
                {
                    mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open);
                    return true;
                }
                else
                {
                    Logger.LogDebug("Cannot open file path: {FilePath}, path not found", filePath);
                    return false;
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Cannot open MemoryMappedFile: {MapName}", mapName);
                return false;
            }
        }

        public override void Delete(string mapName, MemoryMappedFile mmf)
        {
            if (mmf != null)
            {
                mmf.Dispose();
            }

            try
            {
                string filePath = GetPath(mapName);
                File.Delete(filePath);
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "Cannot delete MemoryMappedFile: {MapName}", mapName);
            }
        }

        /// <summary>
        /// Checks if a list of directories is specified in AppSettings to create <see cref="MemoryMappedFile"/>.
        /// If one is specified, returns that list. Otherwise returns the default list.
        /// </summary>
        /// <returns>List of paths of directories where <see cref="MemoryMappedFile"/> are allowed to be created.</returns>
        internal List<string> GetAllowedDirectories()
        {
            string envVal = _environment.GetEnvironmentVariable(RpcWorkerConstants.FunctionsUnixSharedMemoryDirectories);
            if (string.IsNullOrEmpty(envVal))
            {
                return SharedMemoryConstants.TempDirs;
            }

            return envVal.Split(',').ToList();
        }

        /// <summary>
        /// From a list of allowed directories where <see cref="MemoryMappedFile"/> can be created, it will return
        /// a list of those that are valid (i.e. exist, or have been successfully created).
        /// </summary>
        /// <returns>List of paths of directories where <see cref="MemoryMappedFile"/> can be created.</returns>
        internal List<string> GetValidDirectories()
        {
            List<string> allowedDirectories = GetAllowedDirectories();
            List<string> validDirectories = new List<string>();

            foreach (string directory in allowedDirectories)
            {
                string path = Path.Combine(directory, SharedMemoryConstants.TempDirSuffix);
                if (Directory.Exists(path))
                {
                    Logger.LogTrace("Found directory for shared memory usage: {Directory}", path);
                    try
                    {
                        // If the directory already exists (maybe from a previous run of the host) then clean it up and start afresh
                        // The previously created memory maps in that directory are not needed and we need to should clean up the memory
                        Directory.Delete(path, recursive: true);
                        Logger.LogTrace("Cleaned up existing directory for shared memory usage: {Directory}", path);
                    }
                    catch (Exception exception)
                    {
                        Logger.LogWarning(exception, "Cannot delete existing directory for shared memory usage: {Directory}", path);
                    }
                }

                try
                {
                    DirectoryInfo info = Directory.CreateDirectory(path);
                    if (info.Exists)
                    {
                        validDirectories.Add(path);
                        Logger.LogTrace("Created directory for shared memory usage: {Directory}", path);
                    }
                    else
                    {
                        Logger.LogWarning("Directory for shared memory usage does not exist: {Directory}", path);
                    }
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(exception, "Cannot create directory for shared memory usage: {Directory}", path);
                }
            }

            Logger.LogDebug("Valid directories for shared memory usage: {Directories}", string.Join(",", validDirectories));
            return validDirectories;
        }

        /// <summary>
        /// Get the path of the file to store a <see cref="MemoryMappedFile"/>.
        /// We first try to mount it in memory-mounted directories (e.g. /dev/shm/).
        /// </summary>
        /// <param name="mapName">Name of the <see cref="MemoryMappedFile"/>.</param>
        /// <returns>Path to the <see cref="MemoryMappedFile"/> if found an existing one
        /// <see cref="null"/> otherwise.</returns>
        private string GetPath(string mapName)
        {
            // Check if the file already exists
            string filePath;
            foreach (string tempDir in ValidDirectories)
            {
                filePath = Path.Combine(tempDir, mapName);
                if (File.Exists(filePath))
                {
                    return filePath;
                }
            }

            return null;
        }

        /// <summary>
        /// Create a path which will be used to create a file backing a <see cref="MemoryMappedFile"/>.
        /// </summary>
        /// <param name="mapName">Name of the <see cref="MemoryMappedFile"/>.</param>
        /// <param name="size">Required size in the directory.</param>
        /// <returns>Created path.</returns>
        private string CreatePath(string mapName, long size)
        {
            // Create a new file
            string tempDir = GetDirectory(size);
            if (tempDir != null)
            {
                return Path.Combine(tempDir, mapName);
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Get the path of the directory to create <see cref="MemoryMappedFile"/>.
        /// It checks which one has enough free space.
        /// </summary>
        /// <param name="size">Required size in the directory.</param>
        /// <returns>Directory with enough space to create <see cref="MemoryMappedFile"/>.
        /// If none of them has enough free space, the
        /// <see cref="SharedMemoryConstants.TempDirSuffix"/> is used.</returns>
        private string GetDirectory(long size)
        {
            foreach (string tempDir in ValidDirectories)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        DriveInfo driveInfo = new DriveInfo(tempDir);
                        long minSize = size + SharedMemoryConstants.TempDirMinSize;
                        if (driveInfo.AvailableFreeSpace > minSize)
                        {
                            return tempDir;
                        }
                    }
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(exception, "Cannot get directory: {Directory}", tempDir);
                    continue;
                }
            }

            return null;
        }
    }
}