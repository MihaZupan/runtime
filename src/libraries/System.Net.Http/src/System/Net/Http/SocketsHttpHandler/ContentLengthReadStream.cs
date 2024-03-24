// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal sealed partial class HttpConnection : IDisposable
    {
        private sealed class ContentLengthReadStream : HttpContentReadStream
        {
            private ulong _contentBytesRemaining;

            public ContentLengthReadStream(HttpConnection connection, ulong contentLength) : base(connection)
            {
                Debug.Assert(contentLength > 0, "Caller should have checked for 0.");
                _contentBytesRemaining = contentLength;
            }

            public override int Read(Span<byte> buffer)
            {
                if (_connection == null)
                {
                    // Response body fully consumed
                    return 0;
                }

                Debug.Assert(_contentBytesRemaining > 0);
                if ((ulong)buffer.Length > _contentBytesRemaining)
                {
                    buffer = buffer.Slice(0, (int)_contentBytesRemaining);
                }

                int bytesRead = _connection.Read(buffer);
                if (bytesRead <= 0 && buffer.Length != 0)
                {
                    // Unexpected end of response stream.
                    throw new HttpIOException(HttpRequestError.ResponseEnded, SR.Format(SR.net_http_invalid_response_premature_eof_bytecount, _contentBytesRemaining));
                }

                Debug.Assert((ulong)bytesRead <= _contentBytesRemaining);
                _contentBytesRemaining -= (ulong)bytesRead;

                if (_contentBytesRemaining == 0)
                {
                    // End of response body
                    _connection.CompleteResponse();
                    _connection = null;
                }

                return bytesRead;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromCanceled<int>(cancellationToken);
                }

                if (_connection is null)
                {
                    // Response body fully consumed
                    return new ValueTask<int>(0);
                }

                Debug.Assert(_contentBytesRemaining > 0);

                if ((ulong)buffer.Length > _contentBytesRemaining)
                {
                    buffer = buffer.Slice(0, (int)_contentBytesRemaining);
                }

                ValueTask<int> readTask = _connection.ReadAsync(buffer);

                if (readTask.IsCompletedSuccessfully)
                {
                    int bytesRead = readTask.Result;

                    if (!buffer.IsEmpty)
                    {
                        if (bytesRead == 0)
                        {
                            return ValueTask.FromException<int>(CreateEOFException());
                        }

                        AccountForBytesRead(bytesRead);
                    }

                    return new ValueTask<int>(bytesRead);
                }

                _connection._cancellationToken = cancellationToken;
                _connection.RegisterCancellation(cancellationToken);

                if (buffer.IsEmpty)
                {
                    return AwaitZeroByteReadTaskAsync(readTask);
                }
                else
                {
                    return AwaitReadAsyncTaskAsync(readTask);
                }
            }

            private async ValueTask<int> AwaitZeroByteReadTaskAsync(ValueTask<int> readTask)
            {
                Debug.Assert(_connection is not null);
                try
                {
                    return await readTask.ConfigureAwait(false);
                }
                catch (Exception exc) when (CancellationHelper.ShouldWrapInOperationCanceledException(exc, _connection._cancellationToken))
                {
                    throw CancellationHelper.CreateOperationCanceledException(exc, _connection._cancellationToken);
                }
                finally
                {
                    _connection._cancellationRegistration.Dispose();
                    _connection._cancellationRegistration = default;

                    _connection._cancellationToken = default;
                }
            }

            private async ValueTask<int> AwaitReadAsyncTaskAsync(ValueTask<int> readTask)
            {
                Debug.Assert(_connection is not null);
                try
                {
                    int bytesRead = await readTask.ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        throw CreateEOFException();
                    }

                    AccountForBytesRead(bytesRead);
                    return bytesRead;
                }
                catch (Exception exc) when (CancellationHelper.ShouldWrapInOperationCanceledException(exc, _connection._cancellationToken))
                {
                    throw CancellationHelper.CreateOperationCanceledException(exc, _connection._cancellationToken);
                }
                finally
                {
                    _connection._cancellationRegistration.Dispose();
                    _connection._cancellationRegistration = default;

                    _connection._cancellationToken = default;
                }
            }

            private void AccountForBytesRead(int bytesRead)
            {
                Debug.Assert(_connection is not null);
                Debug.Assert(bytesRead > 0);
                Debug.Assert((ulong)bytesRead <= _contentBytesRemaining);

                _contentBytesRemaining -= (ulong)bytesRead;

                if (_contentBytesRemaining == 0)
                {
                    // End of response body
                    _connection.CompleteResponse();
                    _connection = null;
                }
            }

            private HttpIOException CreateEOFException() =>
                new(HttpRequestError.ResponseEnded, SR.Format(SR.net_http_invalid_response_premature_eof_bytecount, _contentBytesRemaining));

            public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                ValidateCopyToArguments(destination, bufferSize);

                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled(cancellationToken);
                }

                if (_connection == null)
                {
                    // null if response body fully consumed
                    return Task.CompletedTask;
                }

                Task copyTask = _connection.CopyToContentLengthAsync(destination, async: true, _contentBytesRemaining, bufferSize, cancellationToken);
                if (copyTask.IsCompletedSuccessfully)
                {
                    Finish();
                    return Task.CompletedTask;
                }

                _connection._cancellationToken = cancellationToken;
                _connection.RegisterCancellation(cancellationToken);

                return CompleteCopyToAsync(copyTask);
            }

            private async Task CompleteCopyToAsync(Task copyTask)
            {
                Debug.Assert(_connection != null);
                try
                {
                    await copyTask.ConfigureAwait(false);
                }
                catch (Exception exc) when (CancellationHelper.ShouldWrapInOperationCanceledException(exc, _connection._cancellationToken))
                {
                    throw CancellationHelper.CreateOperationCanceledException(exc, _connection._cancellationToken);
                }
                finally
                {
                    _connection._cancellationRegistration.Dispose();
                    _connection._cancellationRegistration = default;

                    _connection._cancellationToken = default;
                }

                Finish();
            }

            private void Finish()
            {
                _contentBytesRemaining = 0;
                _connection!.CompleteResponse();
                _connection = null;
            }

            // Based on ReadChunkFromConnectionBuffer; perhaps we should refactor into a common routine.
            private ReadOnlyMemory<byte> ReadFromConnectionBuffer(int maxBytesToRead)
            {
                Debug.Assert(maxBytesToRead > 0);
                Debug.Assert(_contentBytesRemaining > 0);
                Debug.Assert(_connection != null);

                ReadOnlyMemory<byte> connectionBuffer = _connection.RemainingBuffer;
                if (connectionBuffer.Length == 0)
                {
                    return default;
                }

                int bytesToConsume = Math.Min(maxBytesToRead, (int)Math.Min((ulong)connectionBuffer.Length, _contentBytesRemaining));
                Debug.Assert(bytesToConsume > 0);

                _connection.ConsumeFromRemainingBuffer(bytesToConsume);
                _contentBytesRemaining -= (ulong)bytesToConsume;

                return connectionBuffer.Slice(0, bytesToConsume);
            }

            public override bool NeedsDrain => CanReadFromConnection;

            public override async ValueTask<bool> DrainAsync(int maxDrainBytes)
            {
                Debug.Assert(_connection != null);
                Debug.Assert(_contentBytesRemaining > 0);

                ReadFromConnectionBuffer(int.MaxValue);
                if (_contentBytesRemaining == 0)
                {
                    Finish();
                    return true;
                }

                if (_contentBytesRemaining > (ulong)maxDrainBytes)
                {
                    return false;
                }

                CancellationTokenSource? cts = null;
                TimeSpan drainTime = _connection._pool.Settings._maxResponseDrainTime;

                if (drainTime == TimeSpan.Zero)
                {
                    return false;
                }

                if (drainTime != Timeout.InfiniteTimeSpan)
                {
                    cts = new CancellationTokenSource((int)drainTime.TotalMilliseconds);
                    _connection.RegisterCancellation(cts.Token);
                }

                _connection._async = true;

                try
                {
                    while (true)
                    {
                        await _connection.FillAsync().ConfigureAwait(false);
                        ReadFromConnectionBuffer(int.MaxValue);
                        if (_contentBytesRemaining == 0)
                        {
                            // Dispose of the registration and then check whether cancellation has been
                            // requested. This is necessary to make deterministic a race condition between
                            // cancellation being requested and unregistering from the token.  Otherwise,
                            // it's possible cancellation could be requested just before we unregister and
                            // we then return a connection to the pool that has been or will be disposed
                            // (e.g. if a timer is used and has already queued its callback but the
                            // callback hasn't yet run).
                            _connection._cancellationRegistration.Dispose();
                            CancellationHelper.ThrowIfCancellationRequested(_connection._cancellationRegistration.Token);

                            Finish();
                            return true;
                        }
                    }
                }
                finally
                {
                    _connection._cancellationRegistration.Dispose();
                    _connection._cancellationRegistration = default;

                    cts?.Dispose();
                }
            }
        }
    }
}
