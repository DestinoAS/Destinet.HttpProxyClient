﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Destinet
{
    /// <summary>
    /// Default implementation of <see cref="IStreamCopier"/>.
    /// </summary>
    internal class StreamCopier
    {
        // Taken from https://github.com/aspnet/Proxy/blob/816f65429b29d98e3ca98dd6b4d5e990f5cc7c02/src/Microsoft.AspNetCore.Proxy/ProxyAdvancedExtensions.cs#L19
        private const int DefaultBufferSize = 81920;

        /// <inheritdoc/>
        /// <remarks>
        /// Based on <c>Microsoft.AspNetCore.Http.StreamCopyOperationInternal.CopyToAsync</c>.
        /// See: <see href="https://github.com/dotnet/aspnetcore/blob/080660967b6043f731d4b7163af9e9e6047ef0c4/src/Http/Shared/StreamCopyOperationInternal.cs"/>.
        /// </remarks>
        public async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellation)
        {
            _ = source ?? throw new ArgumentNullException(nameof(source));
            _ = destination ?? throw new ArgumentNullException(nameof(destination));

            // TODO: Consider System.IO.Pipelines for better perf (e.g. reads during writes)
            var buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
            long iops = 0;
            long totalBytes = 0;
            try
            {
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    iops++;
                    var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellation);

                    // End of the source stream.
                    if (read == 0)
                    {
                        return;
                    }

                    cancellation.ThrowIfCancellationRequested();
                    await destination.WriteAsync(buffer, 0, read, cancellation);
                    totalBytes += read;
                }
            }
            finally
            {
                // We can afford the perf impact of clearArray == true since we only do this twice per request.
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);

            }
        }
    }
}