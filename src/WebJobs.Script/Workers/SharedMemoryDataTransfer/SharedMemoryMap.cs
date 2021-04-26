// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer
{
    /// <summary>
    /// Shared memory region to read/write data from.
    /// </summary>
    public class SharedMemoryMap : IDisposable
    {
        private readonly ILoggerFactory _loggerFactory;

        private readonly ILogger _logger;

        private readonly IMemoryMappedFileAccessor _mapAccessor;

        /// <summary>
        /// <see cref="MemoryMappedFile"/> containg the header + content of this <see cref="SharedMemoryMap"/>.
        /// </summary>
        private readonly MemoryMappedFile _memoryMappedFile;

        /// <summary>
        /// Name of the <see cref="MemoryMappedFile"/> backing this <see cref="SharedMemoryMap"/>.
        /// </summary>
        private readonly string _mapName;

        public SharedMemoryMap(ILoggerFactory loggerFactory, IMemoryMappedFileAccessor mapAccessor, string mapName, MemoryMappedFile memoryMappedFile)
        {
            if (string.IsNullOrEmpty(mapName))
            {
                throw new ArgumentException(nameof(mapName));
            }

            _memoryMappedFile = memoryMappedFile ?? throw new ArgumentNullException(nameof(memoryMappedFile));
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<SharedMemoryMap>();
            _mapName = mapName;
            _mapAccessor = mapAccessor;
        }

        /// <summary>
        /// Write content from the given <see cref="Stream"/> into this <see cref="SharedMemoryMap"/>.
        /// The number of bytes of content being written must be less than or equal to the size of the <see cref="SharedMemoryMap"/>.
        /// </summary>
        /// <param name="content"><see cref="Stream"/> containing the content to write.</param>
        /// <returns>Number of bytes of content written.</returns>
        public async Task<long> PutStreamAsync(Stream content)
        {
            long contentLength = content.Length;
            long originalPosition = content.Position;

            try
            {
                using (MemoryMappedViewStream mmv = _memoryMappedFile.CreateViewStream())
                {
                    // Seek past the MemoryMapInitialized flag section of the header
                    mmv.Seek(SharedMemoryConstants.MemoryMapInitializedHeaderBytes, SeekOrigin.Begin);
                    await WriteLongAsync(mmv, contentLength);
                    await content.CopyToAsync(mmv, SharedMemoryConstants.MinBufferSize);
                    await mmv.FlushAsync();
                }

                return contentLength;
            }
            catch (Exception e)
            {
                // This exception can be triggered by:
                // 1) Size is greater than the total virtual memory.
                // 2) Access is invalid for the memory-mapped file.
                _mapAccessor.Delete(_mapName, _memoryMappedFile);

                _logger.LogError(e, "Cannot put stream into shared memory map: {MapName)", _mapName);

                // Seek the input stream back to the original position it was given with
                if (content.CanSeek)
                {
                    content.Seek(originalPosition, SeekOrigin.Begin);
                }

                return 0;
            }
        }

        /// <summary>
        /// Write content from the given <see cref="byte[]"/> into this <see cref="SharedMemoryMap"/>.
        /// The number of bytes of content being written must be less than or equal to the size of the <see cref="SharedMemoryMap"/>.
        /// </summary>
        /// <param name="content"><see cref="byte[]"/> containing the content to write.</param>
        /// <returns>Number of bytes of content written.</returns>
        public Task<long> PutBytesAsync(byte[] content)
        {
            using (MemoryStream contentStream = new MemoryStream(content))
            {
                return PutStreamAsync(contentStream);
            }
        }

        /// <summary>
        /// Read the content contained in this <see cref="SharedMemoryMap"/> as <see cref="byte[]"/>.
        /// </summary>
        /// <returns><see cref="byte[]"/> of content if successful, <see cref="null"/> otherwise.</returns>
        public Task<byte[]> GetBytesAsync()
        {
            return GetBytesAsync(0, -1);
        }

        /// <summary>
        /// Read the content contained in this <see cref="SharedMemoryMap"/> as <see cref="byte[]"/>.
        /// </summary>
        /// <param name="offset">Offset to start reading the content from.</param>
        /// <param name="count">Number of bytes to read. -1 means read to completion.</param>
        /// <returns><see cref="byte[]"/> of content if successful, <see cref="null"/> otherwise.</returns>
        public Task<byte[]> GetBytesAsync(int offset, int count)
        {
            return CopyStreamAsync(offset, count);
        }

        /// <summary>
        /// Read the length of content present in this <see cref="SharedMemoryMap"/>.
        /// </summary>
        /// <returns><see cref="long"/> containing length of content if successful, -1 otherwise.</returns>
        public async Task<long> GetContentLengthAsync()
        {
            try
            {
                // Create a MemoryMappedViewStream over the header
                using (MemoryMappedViewStream mmv = _memoryMappedFile.CreateViewStream(0, SharedMemoryConstants.HeaderTotalBytes))
                {
                    // Seek past the MemoryMapInitialized flag section of the header
                    mmv.Seek(SharedMemoryConstants.MemoryMapInitializedHeaderBytes, SeekOrigin.Begin);
                    // Read the content length (equal to the size of one long)
                    long contentLength = await ReadLongAsync(mmv);
                    if (contentLength >= 0)
                    {
                        return contentLength;
                    }
                    else
                    {
                        _logger.LogDebug("Content length cannot be negative: {ContentLength}", contentLength);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Cannot read content length for shared memory map: {MapName}", _mapName);
            }

            return -1;
        }

        /// <summary>
        /// Get a <see cref="Stream"/> over the entire content stored in this <see cref="SharedMemoryMap"/>.
        /// </summary>
        /// <returns><see cref="Stream"/> over the content if successful, <see cref="null"/> otherwise.</returns>
        public async Task<Stream> GetStreamAsync()
        {
            long contentLength = await GetContentLengthAsync();
            if (contentLength >= 0)
            {
                return _memoryMappedFile.CreateViewStream(SharedMemoryConstants.HeaderTotalBytes, contentLength);
            }

            return null;
        }

        /// <summary>
        /// Get a <see cref="Stream"/> over the content stored in this <see cref="SharedMemoryMap"/> starting from the offset
        /// for a total of the content length specified.
        /// </summary>
        /// <param name="offset">Offset to start reading the content from.</param>
        /// <param name="count">Number of bytes to read. -1 means read to completion.</param>
        /// <returns><see cref="Stream"/> over the content if successful, <see cref="null"/> otherwise.</returns>
        public async Task<Stream> GetStreamAsync(int offset, int count)
        {
            // TODO add tests
            long contentLength = await GetContentLengthAsync();
            if (contentLength >= 0)
            {
                if (count > contentLength || offset < 0)
                {
                    return null;
                }

                if (count == -1)
                {
                    count = (int)contentLength;
                }

                return _memoryMappedFile.CreateViewStream(SharedMemoryConstants.HeaderTotalBytes + offset, count);
            }

            return null;
        }

        public void Dispose(bool deleteFile)
        {
            if (deleteFile)
            {
                _mapAccessor.Delete(_mapName, _memoryMappedFile);
            }

            if (_memoryMappedFile != null)
            {
                _memoryMappedFile.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(deleteFile: true);
        }

        /// <summary>
        /// Copy contents of the <see cref="SharedMemoryMap"/> into a <see cref="byte[]"/>.
        /// Note:
        /// The maximum number of bytes copied are 2GB (i.e. what a <see cref="byte[]"/> can hold.)
        /// </summary>
        /// <param name="offset">Offset in the <see cref="SharedMemoryMap"/> content to start copying from.</param>
        /// <param name="count">Number of bytes to copy. If -1, then copy all the contents.</param>
        /// <returns><see cref="byte[]"/> containing copied content.</returns>
        public async Task<byte[]> CopyStreamAsync(int offset, int count)
        {
            Stream contentStream = await GetStreamAsync();
            if (contentStream == null)
            {
                _logger.LogError("Cannot get stream for shared memory map: {MapName}", _mapName);
                return null;
            }

            int contentLength = (int)contentStream.Length;
            if (contentLength == 0)
            {
                return null;
            }

            if (offset > contentLength)
            {
                _logger.LogError("Offset: {Offset} cannot be larger than content length {ContentLength}", offset, contentLength);
                return null;
            }

            contentStream.Seek(offset, SeekOrigin.Begin);
            int bytesToCopy = count == -1 ? (int)contentStream.Length : count;
            byte[] output = new byte[bytesToCopy];
            int read;
            while ((read = await contentStream.ReadAsync(output, offset, output.Length - offset)) > 0)
            {
                offset += read;
            }

            return output;
        }

        /// <summary>
        /// Write the given <see cref="long"/> value into the <see cref="MemoryMappedViewStream"/>.
        /// </summary>
        /// <param name="mmv"><see cref="MemoryMappedViewStream"/> to write to.</param>
        /// <param name="value">The <see cref="long"/> value to write.</param>
        public static Task WriteLongAsync(MemoryMappedViewStream mmv, long value)
        {
            byte[] valueBytes = BitConverter.GetBytes(value);
            return mmv.WriteAsync(valueBytes, 0, SharedMemoryConstants.ContentLengthHeaderBytes);
        }

        /// <summary>
        /// Read a <see cref="long"/> value from the <see cref="MemoryMappedViewStream"/>.
        /// </summary>
        /// <param name="mmv"><see cref="MemoryMappedViewStream"/> to read from.</param>
        /// <returns>The read <see cref="long"/> value.</returns>
        public static async Task<long> ReadLongAsync(MemoryMappedViewStream mmv)
        {
            int numBytes = sizeof(long);
            byte[] buffer = new byte[numBytes];
            int numReadBytes = await mmv.ReadAsync(buffer, 0, numBytes);

            if (numReadBytes != numBytes)
            {
                throw new IOException($"Invalid header format. Expected {numBytes} bytes, found {numReadBytes} bytes.");
            }

            return BitConverter.ToInt64(buffer, 0);
        }
    }
}
