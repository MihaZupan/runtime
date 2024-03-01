// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal sealed partial class Http2Connection
    {
        private sealed class Http2FrameWriter
        {
            // When buffering outgoing writes, we will automatically buffer up to this number of bytes.
            // Single writes that are larger than the buffer can cause the buffer to expand beyond
            // this value, so this is not a hard maximum size.
            private const int UnflushedOutgoingBufferSize = 32 * 1024;
            private const int RentedOutgoingBufferSize = UnflushedOutgoingBufferSize * 2;

            // Avoid resizing the buffer too much for many small writes.
            private const int MinFireAndForgetBufferSize = 1024;

            private static readonly UnboundedChannelOptions s_channelOptions = new() { SingleReader = true };

            private static readonly Http2StreamWriteAwaitable s_fireAndForgetSentinel = new(null!, null!);

            private readonly Http2Connection _parent;
            private ArrayBuffer _outgoingBuffer = new(initialSize: 0, usePool: true);
            private ArrayBuffer _fireAndForgetBuffer = new(initialSize: 0, usePool: true);
            private bool _shouldFlush;
            private uint _flushCounter;

            // The objects written to this channel are either:
            // - null, indicating that a connection window update was received.
            // - s_fireAndForgetSentinel, indicating that there is data available in _fireAndForgetBuffer.
            // - An Http2StreamWriteAwaitable representing a flush/stream data write/headers.
            private readonly Channel<Http2StreamWriteAwaitable?> _channel = Channel.CreateUnbounded<Http2StreamWriteAwaitable?>(s_channelOptions);

            private int _connectionWindow;
            private readonly Queue<Http2StreamWriteAwaitable> _waitingForMoreConnectionWindow = new();

            private object FireAndForgetLock => _channel;
            private object WindowUpdateLock => _waitingForMoreConnectionWindow;

            public Http2FrameWriter(Http2Connection parent, int initialConnectionWindowSize)
            {
                _parent = parent;
                _connectionWindow = initialConnectionWindowSize;
            }

            public void StartWriteLoop()
            {
                using (ExecutionContext.SuppressFlow())
                {
                    Task.Run(ProcessOutgoingFramesAsync);
                }
            }

            public void Complete()
            {
                bool success = _channel.Writer.TryComplete();
                Debug.Assert(success);
            }

            public void AddConnectionWindow(int amount)
            {
                Debug.Assert(amount > 0);

                lock (WindowUpdateLock)
                {
                    Debug.Assert(amount <= int.MaxValue - _connectionWindow);

                    _connectionWindow = checked(_connectionWindow + amount);

                    if (_connectionWindow != amount)
                    {
                        // We already had some window available, so we don't need to wake up the writer loop.
                        return;
                    }
                }

                // Wake up the writer loop now that we have some connection window available.
                _channel.Writer.TryWrite(null);
            }

            private async Task ProcessOutgoingFramesAsync()
            {
                try
                {
                    while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
                    {
                        // We rent a larger buffer to avoid resizing too often for many small writes.
                        _outgoingBuffer.EnsureAvailableSpace(RentedOutgoingBufferSize);

                        while (true)
                        {
                            if (_channel.Reader.TryRead(out Http2StreamWriteAwaitable? stream))
                            {
                                if (stream is null)
                                {
                                    // A connection window update was received.
                                    // The next loop iteration will dequeue a stream waiting on connection window, if there is one.
                                    continue;
                                }

                                if (ReferenceEquals(stream, s_fireAndForgetSentinel))
                                {
                                    CopyFireAndForgetFramesToOutgoingBuffer();
                                    continue;
                                }
                            }
                            // If we have any connection window left, we should check for any pending writes.
                            else if (_connectionWindow == 0 || !_waitingForMoreConnectionWindow.TryDequeue(out stream))
                            {
                                break;
                            }

                            // Flush the buffer if we've accumulated enough data.
                            // Do this before disabling the cancellation on the writer in case the write takes a while.
                            if (_outgoingBuffer.ActiveLength > UnflushedOutgoingBufferSize)
                            {
                                _flushCounter++;
                                try
                                {
                                    if (NetEventSource.Log.IsEnabled()) LogFlushingBuffer();

                                    await _parent._stream.WriteAsync(_outgoingBuffer.ActiveMemory, CancellationToken.None).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _parent.Abort(ex);
                                }

                                _shouldFlush = false;
                                _outgoingBuffer.Discard(_outgoingBuffer.ActiveLength);
                            }

                            if (!stream.TryDisableCancellation())
                            {
                                continue;
                            }

                            if (stream.WritingHeaders)
                            {
                                WriteHeadersCore(stream);
                            }
                            else
                            {
                                WriteStreamDataCore(stream);
                            }
                        }

                        // Nothing left in the queue to process.
                        // Flush the buffer if we've accumulated enough data or if we decided we should flush as soon as possible.
                        if (_shouldFlush || _outgoingBuffer.ActiveLength > UnflushedOutgoingBufferSize)
                        {
                            Debug.Assert(_outgoingBuffer.ActiveLength > 0);

                            _flushCounter++;
                            try
                            {
                                if (NetEventSource.Log.IsEnabled()) LogFlushingBuffer();

                                await _parent._stream.WriteAsync(_outgoingBuffer.ActiveMemory, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _parent.Abort(ex);
                            }

                            _shouldFlush = false;
                            _outgoingBuffer.Discard(_outgoingBuffer.ActiveLength);
                        }

                        if (_outgoingBuffer.ActiveLength == 0)
                        {
                            // Return the buffer to the pool if it's empty as the connection may stay idle for a while.
                            _outgoingBuffer.ClearAndReturnBuffer();
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (NetEventSource.Log.IsEnabled()) LogUnexpectedException(ex);

                    Debug.Fail($"Unexpected exception in {nameof(ProcessOutgoingFramesAsync)}: {ex}");
                }
                finally
                {
                    _outgoingBuffer.Dispose();
                }

                void LogFlushingBuffer() =>
                    _parent.Trace($"Flushing {_outgoingBuffer.ActiveLength} bytes. {nameof(_shouldFlush)}={_shouldFlush}, {nameof(_flushCounter)}={_flushCounter}, {nameof(_connectionWindow)}={_connectionWindow}");

                void LogUnexpectedException(Exception ex) =>
                    _parent.Trace($"Unexpected exception in {nameof(ProcessOutgoingFramesAsync)}: {ex}");
            }

            public void ScheduleStreamWrite(Http2StreamWriteAwaitable stream)
            {
                if (!_channel.Writer.TryWrite(stream))
                {
                    HandleConnectionShutdown(stream);
                }

                void HandleConnectionShutdown(Http2StreamWriteAwaitable stream)
                {
                    if (!stream.TryDisableCancellation())
                    {
                        return;
                    }

                    if (_parent._abortException is not null)
                    {
                        stream.SetException(_parent._abortException);
                        return;
                    }

                    // We must be trying to send something asynchronously and it has raced with the connection tear down.
                    // As such, it should not matter that we were not able to actually send the frame.
                    // But just in case, throw ObjectDisposedException. Asynchronous callers will ignore the failure.
                    Debug.Assert(_parent._shutdown && _parent._streamsInUse == 0);
                    stream.SetException(new ObjectDisposedException(nameof(Http2Connection)));
                }
            }

            public bool ShouldScheduleFlushAsync(Http2StreamWriteAwaitable stream)
            {
                // This could technically give a false negative answer after ~4 billion writes to the network on the same connection, but that seems fine.
                return _flushCounter == stream.FlushCounterAtLastDataWrite;
            }

            private void WriteHeadersCore(Http2StreamWriteAwaitable stream)
            {
                Debug.Assert(stream.WritingHeaders);
                Debug.Assert(!stream.DataRemaining.IsEmpty);

                try
                {
                    _parent.AddStream(stream.Stream);

                    ReadOnlySpan<byte> headerBytes = stream.DataRemaining.Span;

                    if (NetEventSource.Log.IsEnabled()) _parent.Trace(stream.Stream.StreamId, $"Started writing. Total header bytes={headerBytes.Length}");

                    // Calculate the total number of bytes we're going to use (content + headers).
                    int frameCount = ((headerBytes.Length - 1) / FrameHeader.MaxPayloadLength) + 1;
                    int totalSize = headerBytes.Length + (frameCount * FrameHeader.Size);

                    _outgoingBuffer.EnsureAvailableSpace(totalSize);

                    Span<byte> output = _outgoingBuffer.AvailableSpan;

                    // Copy the HEADERS frame.
                    ReadOnlySpan<byte> current = headerBytes.Slice(0, Math.Min(headerBytes.Length, FrameHeader.MaxPayloadLength));
                    headerBytes = headerBytes.Slice(current.Length);
                    FrameFlags flags = headerBytes.IsEmpty ? FrameFlags.EndHeaders : FrameFlags.None;

                    HttpRequestMessage request = stream.Stream.Request;

                    if (request.Content is null && !request.IsExtendedConnectRequest)
                    {
                        flags |= FrameFlags.EndStream;
                        _shouldFlush = true;
                    }
                    else if (stream.Stream.ExpectContinue || request.IsExtendedConnectRequest)
                    {
                        _shouldFlush = true;
                    }

                    FrameHeader.WriteTo(output, current.Length, FrameType.Headers, flags, stream.Stream.StreamId);
                    output = output.Slice(FrameHeader.Size);
                    current.CopyTo(output);
                    output = output.Slice(current.Length);

                    if (NetEventSource.Log.IsEnabled()) _parent.Trace(stream.Stream.StreamId, $"Wrote HEADERS frame. Length={current.Length}, flags={flags}");

                    // Copy CONTINUATION frames, if any.
                    while (!headerBytes.IsEmpty)
                    {
                        current = headerBytes.Slice(0, Math.Min(headerBytes.Length, FrameHeader.MaxPayloadLength));
                        headerBytes = headerBytes.Slice(current.Length);

                        flags = headerBytes.IsEmpty ? FrameFlags.EndHeaders : FrameFlags.None;

                        FrameHeader.WriteTo(output, current.Length, FrameType.Continuation, flags, stream.Stream.StreamId);
                        output = output.Slice(FrameHeader.Size);
                        current.CopyTo(output);
                        output = output.Slice(current.Length);

                        if (NetEventSource.Log.IsEnabled()) _parent.Trace(stream.Stream.StreamId, $"Wrote CONTINUATION frame. Length={current.Length}, flags={flags}");
                    }

                    Debug.Assert(headerBytes.IsEmpty);
                    _outgoingBuffer.Commit(totalSize);

                    Debug.Assert(!stream.ShouldFlushAfterData);
                    stream.FlushCounterAtLastDataWrite = _flushCounter;

                    if (_shouldFlush)
                    {
                        // A flush is already scheduled and we're making forward progress.
                        // Lie about the counter to prevent the stream from wasting time trying to schedule another.
                        stream.FlushCounterAtLastDataWrite--;
                    }

                    stream.SetResult();
                }
                catch (Exception ex)
                {
                    stream.SetException(ex);
                }
            }

            private void WriteStreamDataCore(Http2StreamWriteAwaitable stream)
            {
                if (_parent._abortException is not null)
                {
                    stream.SetException(_parent._abortException);
                    return;
                }

                if (stream.DataRemaining.IsEmpty)
                {
                    // This is a FlushAsync call.
                    Debug.Assert(stream.ShouldFlushAfterData);
                    _shouldFlush |= _outgoingBuffer.ActiveLength != 0;
                    stream.SetResult();
                    return;
                }

                if (_connectionWindow != 0)
                {
                    int toWrite = Math.Min(_connectionWindow, stream.DataRemaining.Length);
                    Debug.Assert(toWrite > 0);

                    stream.ConsumeStreamWindow(toWrite);

                    // TODO: Lockless?
                    lock (WindowUpdateLock)
                    {
                        Debug.Assert(toWrite <= _connectionWindow);
                        _connectionWindow -= toWrite;
                    }

                    ReadOnlySpan<byte> dataLeftToWrite = stream.DataRemaining.Span.Slice(0, toWrite);
                    stream.DataRemaining = stream.DataRemaining.Slice(toWrite);

                    int frameCount = (int)((uint)(dataLeftToWrite.Length - 1) / FrameHeader.MaxPayloadLength) + 1;
                    int totalSize = dataLeftToWrite.Length + (frameCount * FrameHeader.Size);

                    _outgoingBuffer.EnsureAvailableSpace(totalSize);

                    // TODO: Do we need a more fair strategy for balancing the connection window between streams?
                    // Right now we'll write as much as possible on a first-come-first-served basis.
                    do
                    {
                        ReadOnlySpan<byte> chunk = dataLeftToWrite.Slice(0, Math.Min(dataLeftToWrite.Length, FrameHeader.MaxPayloadLength));
                        dataLeftToWrite = dataLeftToWrite.Slice(chunk.Length);

                        FrameHeader.WriteTo(_outgoingBuffer.AvailableSpan, chunk.Length, FrameType.Data, FrameFlags.None, stream.Stream.StreamId);
                        _outgoingBuffer.Commit(FrameHeader.Size);

                        chunk.CopyTo(_outgoingBuffer.AvailableSpan);
                        _outgoingBuffer.Commit(chunk.Length);
                    }
                    while (!dataLeftToWrite.IsEmpty);

                    if (stream.DataRemaining.IsEmpty)
                    {
                        // We were able to send the last of the stream data.
                        _shouldFlush |= stream.ShouldFlushAfterData;
                        stream.FlushCounterAtLastDataWrite = _flushCounter;

                        if (_shouldFlush)
                        {
                            // A flush is already scheduled and we're making forward progress.
                            // Lie about the counter to prevent the stream from wasting time trying to schedule another.
                            stream.FlushCounterAtLastDataWrite--;
                        }

                        stream.SetResult();
                        return;
                    }
                }

                // There's still more data to send, but we've exhausted the connection window.
                // We'll need to wait for a window update before we can send more.
                _shouldFlush |= _outgoingBuffer.ActiveLength > 0;

                // Until then the stream should still observe cancellation attempts.
                if (!stream.TryReRegisterForCancellation())
                {
                    // The write was cancelled.
                    return;
                }

                _waitingForMoreConnectionWindow.Enqueue(stream);
            }

            public void SendWindowUpdate(int streamId, int amount)
            {
                if (NetEventSource.Log.IsEnabled()) _parent.Trace($"{nameof(streamId)}={streamId}, {nameof(amount)}={amount}");

                Span<byte> frame = stackalloc byte[FrameHeader.Size + FrameHeader.WindowUpdateLength];

                FrameHeader.WriteTo(frame, FrameHeader.WindowUpdateLength, FrameType.WindowUpdate, FrameFlags.None, streamId);
                BinaryPrimitives.WriteInt32BigEndian(frame.Slice(FrameHeader.Size), amount);

                WriteFireAndForgetFrame(frame);
            }

            public void SendEndStream(int streamId)
            {
                if (NetEventSource.Log.IsEnabled()) _parent.Trace($"{nameof(streamId)}={streamId}");

                Span<byte> frame = stackalloc byte[FrameHeader.Size];

                FrameHeader.WriteTo(frame, 0, FrameType.Data, FrameFlags.EndStream, streamId);

                WriteFireAndForgetFrame(frame);
            }

            public void SendPing(long content, bool isAck)
            {
                if (NetEventSource.Log.IsEnabled()) _parent.Trace($"{nameof(content)}={content}, {nameof(isAck)}={isAck}");

                Debug.Assert(sizeof(long) == FrameHeader.PingLength);

                Span<byte> frame = stackalloc byte[FrameHeader.Size + FrameHeader.PingLength];

                FrameHeader.WriteTo(frame, FrameHeader.PingLength, FrameType.Ping, isAck ? FrameFlags.Ack : FrameFlags.None, streamId: 0);
                BinaryPrimitives.WriteInt64BigEndian(frame.Slice(FrameHeader.Size), content);

                WriteFireAndForgetFrame(frame);
            }

            public void SendSettingsAck()
            {
                if (NetEventSource.Log.IsEnabled()) _parent.Trace("");

                Span<byte> frame = stackalloc byte[FrameHeader.Size];

                FrameHeader.WriteTo(frame, 0, FrameType.Settings, FrameFlags.Ack, streamId: 0);

                WriteFireAndForgetFrame(frame);
            }

            public void SendRstStream(int streamId, Http2ProtocolErrorCode errorCode)
            {
                if (NetEventSource.Log.IsEnabled()) _parent.Trace(streamId, $"{nameof(errorCode)}={errorCode}");

                Span<byte> frame = stackalloc byte[FrameHeader.Size + FrameHeader.RstStreamLength];

                FrameHeader.WriteTo(frame, FrameHeader.RstStreamLength, FrameType.RstStream, FrameFlags.None, streamId);
                BinaryPrimitives.WriteInt32BigEndian(frame.Slice(FrameHeader.Size), (int)errorCode);

                WriteFireAndForgetFrame(frame);
            }

            private void WriteFireAndForgetFrame(ReadOnlySpan<byte> frame)
            {
                lock (FireAndForgetLock)
                {
                    _fireAndForgetBuffer.EnsureAvailableSpace(_fireAndForgetBuffer.Capacity == 0 ? MinFireAndForgetBufferSize : frame.Length);
                    frame.CopyTo(_fireAndForgetBuffer.AvailableSpan);
                    _fireAndForgetBuffer.Commit(frame.Length);

                    if (_fireAndForgetBuffer.ActiveLength != frame.Length)
                    {
                        // The buffer wasn't empty so this wasn't the first write to it.
                        // A previous write already scheduled the bytes to be copied to the outgoing buffer.
                        return;
                    }
                }

                // This was the first write to the buffer, so we schedule it to be copied to the outgoing buffer.
                _channel.Writer.TryWrite(s_fireAndForgetSentinel);
            }

            private void CopyFireAndForgetFramesToOutgoingBuffer()
            {
                lock (FireAndForgetLock)
                {
                    Debug.Assert(_fireAndForgetBuffer.ActiveLength > 0);

                    if (NetEventSource.Log.IsEnabled()) _parent.Trace($"Copying {_fireAndForgetBuffer.ActiveLength} fire-and-forget bytes");

                    ReadOnlySpan<byte> bytes = _fireAndForgetBuffer.ActiveSpan;

                    _outgoingBuffer.EnsureAvailableSpace(bytes.Length);
                    bytes.CopyTo(_outgoingBuffer.AvailableSpan);
                    _outgoingBuffer.Commit(bytes.Length);

                    _fireAndForgetBuffer.ClearAndReturnBuffer();
                }

                // All fire and forget frames should be flushed immediately.
                _shouldFlush = true;
            }
        }
    }
}
