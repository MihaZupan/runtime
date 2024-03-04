// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.HPack;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace System.Net.Http
{
    internal sealed partial class Http2Connection : HttpConnectionBase
    {
        private sealed class Http2StreamWriteAwaitable : IValueTaskSource
        {
            private static readonly Action<object?, CancellationToken> s_cancelThisAwaitableCallback = static (state, cancellationToken) =>
            {
                Http2StreamWriteAwaitable thisRef = (Http2StreamWriteAwaitable)state!;

                Exception exception =
                    thisRef.Stream.ReplaceExceptionOnRequestBodyCancellationIfNeeded() ??
                    CancellationHelper.CreateOperationCanceledException(innerException: null, cancellationToken);

                thisRef._waitSource.SetException(ExceptionDispatchInfo.SetCurrentStackTrace(exception));
            };

            private static readonly Action<object?> s_cancelLinkedCtsCallback = static state =>
            {
                ((CancellationTokenSource)state!).Cancel(throwOnFirstException: false);
            };

            public readonly Http2Stream Stream;

            private int _streamWindow;
            private bool _waitingOnConnectionWindow;
            private readonly object _windowUpdateLock = new object();

            private ManualResetValueTaskSourceCore<bool> _waitSource = new() { RunContinuationsAsynchronously = true };
            private readonly CancellationTokenSource _requestBodyCTS;
            private CancellationToken _cancellationTokenForCurrentWrite;
            private CancellationTokenRegistration _cancelThisAwaitableRegistration;
            private CancellationTokenRegistration _cancelRequestCtsRegistration;

            public Http2StreamWriteAwaitable(Http2Stream stream, CancellationTokenSource requestBodyCTS)
            {
                Stream = stream;
                _requestBodyCTS = requestBodyCTS;
            }

            public bool WritingHeaders { get; private set; }
            public bool ShouldFlushAfterData { get; private set; }
            public ReadOnlyMemory<byte> DataRemaining { get; set; }
            public uint FlushCounterAtLastDataWrite { get; set; }

            public void SetInitialStreamWindow(int initialWindowSize)
            {
                Debug.Assert(_streamWindow == 0);
                Debug.Assert(!_waitingOnConnectionWindow);

                _streamWindow = initialWindowSize;
            }

            public void AdjustStreamWindow(int delta)
            {
                lock (_windowUpdateLock)
                {
                    _streamWindow = checked(_streamWindow + delta);

                    if (_streamWindow <= 0 || !_waitingOnConnectionWindow)
                    {
                        return;
                    }

                    _waitingOnConnectionWindow = false;
                }

                // Wake up the waiter in WaitForStreamWindowAndWriteDataAsync.
                if (TryDisableCancellation())
                {
                    SetResult();
                }
            }

            public void Complete()
            {
                lock (_windowUpdateLock)
                {
                    if (!_waitingOnConnectionWindow)
                    {
                        return;
                    }

                    _waitingOnConnectionWindow = false;
                }

                // Wake up the waiter in WaitForStreamWindowAndWriteDataAsync.
                if (TryDisableCancellation())
                {
                    SetException(ExceptionDispatchInfo.SetCurrentStackTrace(new ObjectDisposedException(nameof(Http2StreamWriteAwaitable), SR.net_http_disposed_while_in_use)));
                }
            }

            public ValueTask WriteStreamDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
            {
                if (data.IsEmpty)
                {
                    return default;
                }

                if (_streamWindow >= data.Length)
                {
                    // The entire write can be satisfied from the currently available stream window.
                    // If we just ran out of stream window, make sure to flush.
                    SetupForWrite(data, writingHeaders: false, shouldFlush: _streamWindow == data.Length, cancellationToken);

                    ScheduleStreamWrite();

                    return AsValueTask();
                }

                return WaitForStreamWindowAndWriteDataAsync(data, cancellationToken);
            }

            private async ValueTask WaitForStreamWindowAndWriteDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
            {
                while (!data.IsEmpty)
                {
                    int windowAvailable = 0;

                    lock (_windowUpdateLock)
                    {
                        if (_streamWindow > 0)
                        {
                            windowAvailable = Math.Min(_streamWindow, data.Length);
                            _streamWindow -= windowAvailable;
                        }
                        else
                        {
                            // We're reusing the ValueTaskSource infrastructure to efficiently wait for the stream window to become available.
                            // These arguments to SetupForWrite will be ignored.
                            SetupForWrite(ReadOnlyMemory<byte>.Empty, writingHeaders: false, shouldFlush: false, cancellationToken);

                            _waitingOnConnectionWindow = true;
                        }
                    }

                    if (windowAvailable == 0)
                    {
                        // Logically this is part of the else block above, but we can't await while holding the lock.
                        await AsValueTask().ConfigureAwait(false);
                        Debug.Assert(!_waitingOnConnectionWindow);
                        continue;
                    }

                    // We have some stream window available, so we can write some data.

                    // Keep flushing writes as long as we're running out of the stream window.
                    bool shouldFlush = data.Length >= _streamWindow;

                    ReadOnlyMemory<byte> currentChunk = data.Slice(0, windowAvailable);
                    data = data.Slice(currentChunk.Length);

                    // We're running out of the stream window
                    SetupForWrite(currentChunk, writingHeaders: false, shouldFlush, cancellationToken);

                    ScheduleStreamWrite();

                    await AsValueTask().ConfigureAwait(false);
                }
            }

            public Task FlushAsync(CancellationToken cancellationToken)
            {
                if (!Stream.Connection._frameWriter.ShouldScheduleFlushAsync(this))
                {
                    // A flush has either already been scheduled during the last write on this stream, or has happened since.
                    return Task.CompletedTask;
                }

                SetupForWrite(ReadOnlyMemory<byte>.Empty, writingHeaders: false, shouldFlush: true, cancellationToken);

                ScheduleStreamWrite();

                return AsValueTask().AsTask();
            }

            public ValueTask SendHeadersAsync(ReadOnlyMemory<byte> headers, CancellationToken cancellationToken)
            {
                Debug.Assert(!headers.IsEmpty);

                SetupForWrite(headers, writingHeaders: true, shouldFlush: false, cancellationToken);

                ScheduleStreamWrite();

                return AsValueTask();
            }

            private void SetupForWrite(ReadOnlyMemory<byte> buffer, bool writingHeaders, bool shouldFlush, CancellationToken cancellationToken)
            {
                Debug.Assert(_cancellationTokenForCurrentWrite == default);
                Debug.Assert(_cancelThisAwaitableRegistration == default);
                Debug.Assert(_cancelRequestCtsRegistration == default);
                Debug.Assert(_waitSource.GetStatus(_waitSource.Version) == ValueTaskSourceStatus.Pending);

                DataRemaining = buffer;
                WritingHeaders = writingHeaders;
                ShouldFlushAfterData = shouldFlush;

                _cancellationTokenForCurrentWrite = cancellationToken;
                _cancelRequestCtsRegistration = cancellationToken.UnsafeRegister(s_cancelLinkedCtsCallback, _requestBodyCTS);
                _cancelThisAwaitableRegistration = _requestBodyCTS.Token.UnsafeRegister(s_cancelThisAwaitableCallback, this);
            }

            private ValueTask AsValueTask() => new ValueTask(this, _waitSource.Version);

            private void ScheduleStreamWrite() => Stream.Connection._frameWriter.ScheduleStreamWrite(this);

            // The following methods should only be called by Http2FrameWriter.

            public void ConsumeStreamWindow(int max)
            {
                Debug.Assert(!DataRemaining.IsEmpty);
                Debug.Assert(max <= DataRemaining.Length);

                // TODO: Lockless?
                lock (_windowUpdateLock)
                {
                    // This may go into negatives if a window update was received after the write was scheduled.
                    // That's okay as we account for it in WaitForStreamWindowAndWriteDataAsync.
                    _streamWindow -= max;
                }
            }

            public bool TryDisableCancellation()
            {
                Debug.Assert(_waitSource.GetStatus(_waitSource.Version) != ValueTaskSourceStatus.Succeeded);

                // Only disable _cancelThisAwaitableRegistration.
                // We can keep _cancelRequestCtsRegistration around in case we need to re-register later.
                _cancelThisAwaitableRegistration.Dispose();
                _cancelThisAwaitableRegistration = default;

                // Checking GetStatus here instead of _requestBodyCTS.IsCancellationRequested as
                // as the latter may be canceled by other threads even after we've disabled the registration.
                return _waitSource.GetStatus(_waitSource.Version) == ValueTaskSourceStatus.Pending;
            }

            public bool TryReRegisterForCancellation()
            {
                Debug.Assert(_waitSource.GetStatus(_waitSource.Version) == ValueTaskSourceStatus.Pending);
                Debug.Assert(_cancelThisAwaitableRegistration == default);

                _cancelThisAwaitableRegistration = _requestBodyCTS.Token.UnsafeRegister(s_cancelThisAwaitableCallback, this);

                return !_requestBodyCTS.IsCancellationRequested;
            }

            public void SetException(Exception exception)
            {
                Debug.Assert(_waitSource.GetStatus(_waitSource.Version) == ValueTaskSourceStatus.Pending);
                Debug.Assert(_cancelThisAwaitableRegistration == default);

                _waitSource.SetException(exception);
            }

            public void SetResult()
            {
                Debug.Assert(_waitSource.GetStatus(_waitSource.Version) == ValueTaskSourceStatus.Pending);
                Debug.Assert(_cancelThisAwaitableRegistration == default);

                _waitSource.SetResult(false);
            }

            ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) =>
                _waitSource.GetStatus(token);

            void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
                _waitSource.OnCompleted(continuation, state, token, flags);

            void IValueTaskSource.GetResult(short token)
            {
                _cancelRequestCtsRegistration.Dispose();
                _cancelRequestCtsRegistration = default;

                _cancelThisAwaitableRegistration.Dispose();
                _cancelThisAwaitableRegistration = default;

                _cancellationTokenForCurrentWrite = default;

                DataRemaining = default;

                _waitSource.GetResult(token);

                // A Http2StreamWriteAwaitable should never be reused after being canceled / faulted.
                Debug.Assert(_waitSource.GetStatus(_waitSource.Version) == ValueTaskSourceStatus.Succeeded);
                _waitSource.Reset();
            }
        }

        private sealed class Http2FrameWriter
        {
            // When buffering outgoing writes, we will automatically buffer up to this number of bytes.
            // Single writes that are larger than the buffer can cause the buffer to expand beyond
            // this value, so this is not a hard maximum size.
            private const int UnflushedOutgoingBufferSize = 32 * 1024;
            private const int RentedOutgoingBufferSize = UnflushedOutgoingBufferSize * 2;

            // Avoid resizing the buffer too much for many small writes.
            private const int MinFireAndForgetBufferSize = 1024;

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
            private readonly Channel<Http2StreamWriteAwaitable?> _channel = Channel.CreateUnbounded<Http2StreamWriteAwaitable?>();

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

                        while (_channel.Reader.TryRead(out Http2StreamWriteAwaitable? stream) ||
                            // If we have any connection window left, we should check for any pending writes.
                            (_connectionWindow != 0 && _waitingForMoreConnectionWindow.TryDequeue(out stream)))
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
                // This could technically give a false negative answer every ~4 billion writes to the network on the same connection, but that seems fine.
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
                    _shouldFlush = true;
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
                _shouldFlush = true;

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


        // Equivalent to the bytes returned from HPackEncoder.EncodeLiteralHeaderFieldWithoutIndexingNewNameToAllocatedArray(":protocol")
        private static ReadOnlySpan<byte> ProtocolLiteralHeaderBytes => [0x0, 0x9, 0x3a, 0x70, 0x72, 0x6f, 0x74, 0x6f, 0x63, 0x6f, 0x6c];

        private static readonly TaskCompletionSourceWithCancellation<bool> s_settingsReceivedSingleton = CreateSuccessfullyCompletedTcs();

        private TaskCompletionSourceWithCancellation<bool>? _initialSettingsReceived;

        private readonly HttpConnectionPool _pool;
        private readonly Stream _stream;

        // NOTE: This is a mutable struct; do not make it readonly.
        // ProcessIncomingFramesAsync is responsible for disposing/returning this buffer.
        private ArrayBuffer _incomingBuffer;

        /// <summary>Reusable array used to get the values for each header being written to the wire.</summary>
        [ThreadStatic]
        private static string[]? t_headerValues;

        private readonly HPackDecoder _hpackDecoder;

        private readonly Dictionary<int, Http2Stream> _httpStreams;

        private RttEstimator _rttEstimator;

        private int _nextStream;
        private bool _receivedSettingsAck;
        private int _initialServerStreamWindowSize;
        private int _pendingWindowUpdate;

        private uint _maxConcurrentStreams;
        private uint _streamsInUse;
        private TaskCompletionSource<bool>? _availableStreamsWaiter;

        private readonly Http2FrameWriter _frameWriter;

        // Server-advertised SETTINGS_MAX_HEADER_LIST_SIZE
        // https://www.rfc-editor.org/rfc/rfc9113.html#section-6.5.2-2.12.1
        private uint _maxHeaderListSize = uint.MaxValue; // Defaults to infinite

        // This flag indicates that the connection is shutting down and cannot accept new requests, because of one of the following conditions:
        // (1) We received a GOAWAY frame from the server
        // (2) We have exhaustead StreamIds (i.e. _nextStream == MaxStreamId)
        // (3) A connection-level error occurred, in which case _abortException below is set.
        // (4) The connection is being disposed.
        // Requests currently in flight will continue to be processed.
        // When all requests have completed, the connection will be torn down.
        private bool _shutdown;

        // If this is set, the connection is aborting due to an IO failure (IOException) or a protocol violation (Http2ProtocolException).
        // _shutdown above is true, and requests in flight have been (or are being) failed.
        private Exception? _abortException;

        private const int MaxStreamId = int.MaxValue;

        // Temporary workaround for request burst handling on connection start.
        private const int InitialMaxConcurrentStreams = 100;

        private static ReadOnlySpan<byte> Http2ConnectionPreface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;

#if DEBUG
        // In debug builds, start with a very small buffer to induce buffer growing logic.
        private const int InitialConnectionBufferSize = FrameHeader.Size;
#else
        // Rent enough space to receive a full data frame in one read call.
        private const int InitialConnectionBufferSize = FrameHeader.Size + FrameHeader.MaxPayloadLength;
#endif

        // The default initial window size for streams and connections according to the RFC:
        // https://datatracker.ietf.org/doc/html/rfc7540#section-5.2.1
        // Unlike HttpHandlerDefaults.DefaultInitialHttp2StreamWindowSize, this value should never be changed.
        internal const int DefaultInitialWindowSize = 65535;

        // We don't really care about limiting control flow at the connection level.
        // We limit it per stream, and the user controls how many streams are created.
        // So set the connection window size to a large value.
        private const int ConnectionWindowSize = 64 * 1024 * 1024;

        // We hold off on sending WINDOW_UPDATE until we hit the minimum threshold.
        // This value is somewhat arbitrary; the intent is to ensure it is much smaller than
        // the window size itself, or we risk stalling the server because it runs out of window space.
        // If we want to further reduce the frequency of WINDOW_UPDATEs, it's probably better to
        // increase the window size (and thus increase the threshold proportionally)
        // rather than just increase the threshold.
        private const int ConnectionWindowUpdateRatio = 8;
        private const int ConnectionWindowThreshold = ConnectionWindowSize / ConnectionWindowUpdateRatio;

        internal enum KeepAliveState
        {
            None,
            PingSent
        }

        private readonly long _keepAlivePingDelay;
        private readonly long _keepAlivePingTimeout;
        private readonly HttpKeepAlivePingPolicy _keepAlivePingPolicy;
        private long _keepAlivePingPayload;
        private long _nextPingRequestTimestamp;
        private long _keepAlivePingTimeoutTimestamp;
        private volatile KeepAliveState _keepAliveState;

        public Http2Connection(HttpConnectionPool pool, Stream stream, IPEndPoint? remoteEndPoint)
            : base(pool, remoteEndPoint)
        {
            _pool = pool;
            _stream = stream;

            _incomingBuffer = new ArrayBuffer(initialSize: 0, usePool: true);

            _hpackDecoder = new HPackDecoder(maxHeadersLength: pool.Settings.MaxResponseHeadersByteLength);

            _httpStreams = new Dictionary<int, Http2Stream>();

            _frameWriter = new Http2FrameWriter(this, DefaultInitialWindowSize);

            _rttEstimator = RttEstimator.Create();

            _nextStream = 1;
            _initialServerStreamWindowSize = DefaultInitialWindowSize;

            _maxConcurrentStreams = InitialMaxConcurrentStreams;
            _streamsInUse = 0;

            _pendingWindowUpdate = 0;

            _keepAlivePingDelay = TimeSpanToMs(_pool.Settings._keepAlivePingDelay);
            _keepAlivePingTimeout = TimeSpanToMs(_pool.Settings._keepAlivePingTimeout);
            _nextPingRequestTimestamp = Environment.TickCount64 + _keepAlivePingDelay;
            _keepAlivePingPolicy = _pool.Settings._keepAlivePingPolicy;

            uint maxHeaderListSize = _pool._lastSeenHttp2MaxHeaderListSize;
            if (maxHeaderListSize > 0)
            {
                // Previous connections to the same host advertised a limit.
                // Use this as an initial value before we receive the SETTINGS frame.
                _maxHeaderListSize = maxHeaderListSize;
            }

            if (NetEventSource.Log.IsEnabled()) TraceConnection(_stream);

            static long TimeSpanToMs(TimeSpan value)
            {
                double milliseconds = value.TotalMilliseconds;
                return (long)(milliseconds > int.MaxValue ? int.MaxValue : milliseconds);
            }
        }

        ~Http2Connection() => Dispose();

        private object SyncObject => _httpStreams;

        internal TaskCompletionSourceWithCancellation<bool> InitialSettingsReceived =>
            _initialSettingsReceived ??
            Interlocked.CompareExchange(ref _initialSettingsReceived, new(), null) ??
            _initialSettingsReceived;

        internal bool IsConnectEnabled { get; private set; }

        public async ValueTask SetupAsync(CancellationToken cancellationToken)
        {
            ArrayBuffer initialFramesBuffer = new(
                initialSize: Http2ConnectionPreface.Length +
                    FrameHeader.Size + FrameHeader.SettingLength +
                    FrameHeader.Size + FrameHeader.WindowUpdateLength,
                usePool: true);

            try
            {
                // Send connection preface
                Http2ConnectionPreface.CopyTo(initialFramesBuffer.AvailableSpan);
                initialFramesBuffer.Commit(Http2ConnectionPreface.Length);

                // Send SETTINGS frame.  Disable push promise & set initial window size.
                FrameHeader.WriteTo(initialFramesBuffer.AvailableSpan, 2 * FrameHeader.SettingLength, FrameType.Settings, FrameFlags.None, streamId: 0);
                initialFramesBuffer.Commit(FrameHeader.Size);
                BinaryPrimitives.WriteUInt16BigEndian(initialFramesBuffer.AvailableSpan, (ushort)SettingId.EnablePush);
                initialFramesBuffer.Commit(2);
                BinaryPrimitives.WriteUInt32BigEndian(initialFramesBuffer.AvailableSpan, 0);
                initialFramesBuffer.Commit(4);
                BinaryPrimitives.WriteUInt16BigEndian(initialFramesBuffer.AvailableSpan, (ushort)SettingId.InitialWindowSize);
                initialFramesBuffer.Commit(2);
                BinaryPrimitives.WriteUInt32BigEndian(initialFramesBuffer.AvailableSpan, (uint)_pool.Settings._initialHttp2StreamWindowSize);
                initialFramesBuffer.Commit(4);

                // The connection-level window size can not be initialized by SETTINGS frames:
                // https://datatracker.ietf.org/doc/html/rfc7540#section-6.9.2
                // Send an initial connection-level WINDOW_UPDATE to setup the desired ConnectionWindowSize:
                uint windowUpdateAmount = ConnectionWindowSize - DefaultInitialWindowSize;
                if (NetEventSource.Log.IsEnabled()) Trace($"Initial connection-level WINDOW_UPDATE, windowUpdateAmount={windowUpdateAmount}");
                FrameHeader.WriteTo(initialFramesBuffer.AvailableSpan, FrameHeader.WindowUpdateLength, FrameType.WindowUpdate, FrameFlags.None, streamId: 0);
                initialFramesBuffer.Commit(FrameHeader.Size);
                BinaryPrimitives.WriteUInt32BigEndian(initialFramesBuffer.AvailableSpan, windowUpdateAmount);
                initialFramesBuffer.Commit(4);

                // Processing the incoming frames before sending the client preface and SETTINGS is necessary when using a NamedPipe as a transport.
                // If the preface and SETTINGS coming from the server are not read first the below WriteAsync and the ProcessIncomingFramesAsync fall into a deadlock.
                _ = ProcessIncomingFramesAsync();

                await _stream.WriteAsync(initialFramesBuffer.ActiveMemory, cancellationToken).ConfigureAwait(false);

                _rttEstimator.OnInitialSettingsSent();
            }
            catch (Exception e)
            {
                Dispose();

                if (e is OperationCanceledException oce && oce.CancellationToken == cancellationToken)
                {
                    // Note, AddHttp2ConnectionAsync handles this OCE separately so don't wrap it.
                    throw;
                }

                // TODO: Review this case!
                throw new IOException(SR.net_http_http2_connection_not_established, e);
            }
            finally
            {
                initialFramesBuffer.Dispose();
            }

            _frameWriter.StartWriteLoop();
        }

        private void Shutdown()
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(_shutdown)}={_shutdown}, {nameof(_abortException)}={_abortException}");

            Debug.Assert(Monitor.IsEntered(SyncObject));

            if (!_shutdown)
            {
                // InvalidateHttp2Connection may call back into Shutdown,
                // so we set the flag early to prevent executing FinalTeardown twice.
                _shutdown = true;

                _pool.InvalidateHttp2Connection(this);
                SignalAvailableStreamsWaiter(false);

                if (_streamsInUse == 0)
                {
                    FinalTeardown();
                }
            }
        }

        public bool TryReserveStream()
        {
            lock (SyncObject)
            {
                if (_shutdown)
                {
                    return false;
                }

                if (_streamsInUse < _maxConcurrentStreams)
                {
                    if (_streamsInUse == 0)
                    {
                        MarkConnectionAsNotIdle();
                    }

                    _streamsInUse++;
                    return true;
                }
            }

            return false;
        }

        // Can be called by the HttpConnectionPool after TryReserveStream if the stream doesn't end up being used.
        // Otherwise, will be called when the request is complete and stream is closed.
        public void ReleaseStream()
        {
            lock (SyncObject)
            {
                if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(_streamsInUse)}={_streamsInUse}");

                Debug.Assert(_availableStreamsWaiter is null || _streamsInUse >= _maxConcurrentStreams);

                _streamsInUse--;

                Debug.Assert(_streamsInUse >= _httpStreams.Count);

                if (_streamsInUse < _maxConcurrentStreams)
                {
                    SignalAvailableStreamsWaiter(true);
                }

                if (_streamsInUse == 0)
                {
                    MarkConnectionAsIdle();

                    if (_shutdown)
                    {
                        FinalTeardown();
                    }
                }
            }
        }

        // Returns true to indicate at least one stream is available
        // Returns false to indicate that the connection is shutting down and cannot be used anymore
        public Task<bool> WaitForAvailableStreamsAsync()
        {
            lock (SyncObject)
            {
                Debug.Assert(_availableStreamsWaiter is null, "As used currently, shouldn't already have a waiter");

                if (_shutdown)
                {
                    return Task.FromResult(false);
                }

                if (_streamsInUse < _maxConcurrentStreams)
                {
                    return Task.FromResult(true);
                }

                // Need to wait for streams to become available.
                _availableStreamsWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _availableStreamsWaiter.Task;
            }
        }

        private void SignalAvailableStreamsWaiter(bool result)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(result)}={result}, {nameof(_availableStreamsWaiter)}?={_availableStreamsWaiter is not null}");

            Debug.Assert(Monitor.IsEntered(SyncObject));

            if (_availableStreamsWaiter is not null)
            {
                Debug.Assert(_shutdown != result);
                _availableStreamsWaiter.SetResult(result);
                _availableStreamsWaiter = null;
            }
        }

        private async ValueTask<FrameHeader> ReadFrameAsync(bool initialFrame = false)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(initialFrame)}={initialFrame}");

            // Ensure we've read enough data for the frame header.
            if (_incomingBuffer.ActiveLength < FrameHeader.Size)
            {
                do
                {
                    // Issue a zero-byte read to avoid potentially pinning the buffer while waiting for more data.
                    await _stream.ReadAsync(Memory<byte>.Empty).ConfigureAwait(false);

                    _incomingBuffer.EnsureAvailableSpace(FrameHeader.Size);

                    int bytesRead = await _stream.ReadAsync(_incomingBuffer.AvailableMemory).ConfigureAwait(false);
                    _incomingBuffer.Commit(bytesRead);
                    if (bytesRead == 0)
                    {
                        if (_incomingBuffer.ActiveLength == 0)
                        {
                            ThrowMissingFrame();
                        }
                        else
                        {
                            ThrowPrematureEOF(FrameHeader.Size);
                        }
                    }
                }
                while (_incomingBuffer.ActiveLength < FrameHeader.Size);
            }

            // Parse the frame header from our read buffer and validate it.
            FrameHeader frameHeader = FrameHeader.ReadFrom(_incomingBuffer.ActiveSpan);
            if (frameHeader.PayloadLength > FrameHeader.MaxPayloadLength)
            {
                if (initialFrame && NetEventSource.Log.IsEnabled())
                {
                    string response = Encoding.ASCII.GetString(_incomingBuffer.ActiveSpan.Slice(0, Math.Min(20, _incomingBuffer.ActiveLength)));
                    Trace($"HTTP/2 handshake failed. Server returned {response}");
                }

                _incomingBuffer.Discard(FrameHeader.Size);
                ThrowProtocolError(initialFrame ? Http2ProtocolErrorCode.ProtocolError : Http2ProtocolErrorCode.FrameSizeError);
            }
            _incomingBuffer.Discard(FrameHeader.Size);

            // Ensure we've read the frame contents into our buffer.
            if (_incomingBuffer.ActiveLength < frameHeader.PayloadLength)
            {
                _incomingBuffer.EnsureAvailableSpace(frameHeader.PayloadLength - _incomingBuffer.ActiveLength);
                do
                {
                    // Issue a zero-byte read to avoid potentially pinning the buffer while waiting for more data.
                    await _stream.ReadAsync(Memory<byte>.Empty).ConfigureAwait(false);

                    int bytesRead = await _stream.ReadAsync(_incomingBuffer.AvailableMemory).ConfigureAwait(false);
                    _incomingBuffer.Commit(bytesRead);
                    if (bytesRead == 0) ThrowPrematureEOF(frameHeader.PayloadLength);
                }
                while (_incomingBuffer.ActiveLength < frameHeader.PayloadLength);
            }

            // Return the read frame header.
            return frameHeader;

            void ThrowPrematureEOF(int requiredBytes) =>
                throw new HttpIOException(HttpRequestError.ResponseEnded, SR.Format(SR.net_http_invalid_response_premature_eof_bytecount, requiredBytes - _incomingBuffer.ActiveLength));

            void ThrowMissingFrame() =>
                throw new HttpIOException(HttpRequestError.ResponseEnded, SR.net_http_invalid_response_missing_frame);
        }

        private async Task ProcessIncomingFramesAsync()
        {
            try
            {
                FrameHeader frameHeader;
                try
                {
                    // Read the initial settings frame.
                    frameHeader = await ReadFrameAsync(initialFrame: true).ConfigureAwait(false);
                    if (frameHeader.Type != FrameType.Settings || frameHeader.AckFlag)
                    {
                        if (frameHeader.Type == FrameType.GoAway)
                        {
                            var (_, errorCode) = ReadGoAwayFrame(frameHeader);
                            ThrowProtocolError(errorCode, SR.net_http_http2_connection_close);
                        }
                        else
                        {
                            ThrowProtocolError();
                        }
                    }

                    if (NetEventSource.Log.IsEnabled()) Trace($"Frame 0: {frameHeader}.");

                    // Process the initial SETTINGS frame. This will send an ACK.
                    ProcessSettingsFrame(frameHeader, initialFrame: true);

                    Debug.Assert(InitialSettingsReceived.Task.IsCompleted);
                }
                catch (HttpProtocolException e)
                {
                    InitialSettingsReceived.TrySetException(e);
                    throw;
                }
                catch (Exception e)
                {
                    InitialSettingsReceived.TrySetException(new HttpIOException(HttpRequestError.InvalidResponse, SR.net_http_http2_connection_not_established, e));
                    throw new HttpIOException(HttpRequestError.InvalidResponse, SR.net_http_http2_connection_not_established, e);
                }

                // Keep processing frames as they arrive.
                for (long frameNum = 1; ; frameNum++)
                {
                    // We could just call ReadFrameAsync here, but we add this code
                    // to avoid another state machine allocation in the relatively common case where we
                    // currently don't have enough data buffered and issuing a read for the frame header
                    // completes asynchronously, but that read ends up also reading enough data to fulfill
                    // the entire frame's needs (not just the header).
                    if (_incomingBuffer.ActiveLength < FrameHeader.Size)
                    {
                        do
                        {
                            // Issue a zero-byte read to avoid potentially pinning the buffer while waiting for more data.
                            ValueTask<int> zeroByteReadTask = _stream.ReadAsync(Memory<byte>.Empty);
                            if (!zeroByteReadTask.IsCompletedSuccessfully && _incomingBuffer.ActiveLength == 0)
                            {
                                // No data is available yet. Return the receive buffer back to the pool while we wait.
                                _incomingBuffer.ClearAndReturnBuffer();
                            }
                            await zeroByteReadTask.ConfigureAwait(false);

                            // While we only need FrameHeader.Size bytes to complete this read, it's better if we rent more
                            // to avoid multiple ReadAsync calls and resizes once we start copying the content.
                            _incomingBuffer.EnsureAvailableSpace(InitialConnectionBufferSize);

                            int bytesRead = await _stream.ReadAsync(_incomingBuffer.AvailableMemory).ConfigureAwait(false);
                            Debug.Assert(bytesRead >= 0);
                            _incomingBuffer.Commit(bytesRead);
                            if (bytesRead == 0)
                            {
                                // ReadFrameAsync below will detect that the frame is incomplete and throw the appropriate error.
                                break;
                            }
                        }
                        while (_incomingBuffer.ActiveLength < FrameHeader.Size);
                    }

                    // Read the frame.
                    frameHeader = await ReadFrameAsync().ConfigureAwait(false);
                    if (NetEventSource.Log.IsEnabled()) Trace($"Frame {frameNum}: {frameHeader}.");

                    RefreshPingTimestamp();

                    // Process the frame.
                    switch (frameHeader.Type)
                    {
                        case FrameType.Headers:
                            await ProcessHeadersFrame(frameHeader).ConfigureAwait(false);
                            break;

                        case FrameType.Data:
                            ProcessDataFrame(frameHeader);
                            break;

                        case FrameType.Settings:
                            ProcessSettingsFrame(frameHeader);
                            break;

                        case FrameType.Priority:
                            ProcessPriorityFrame(frameHeader);
                            break;

                        case FrameType.Ping:
                            ProcessPingFrame(frameHeader);
                            break;

                        case FrameType.WindowUpdate:
                            ProcessWindowUpdateFrame(frameHeader);
                            break;

                        case FrameType.RstStream:
                            ProcessRstStreamFrame(frameHeader);
                            break;

                        case FrameType.GoAway:
                            ProcessGoAwayFrame(frameHeader);
                            break;

                        case FrameType.AltSvc:
                            ProcessAltSvcFrame(frameHeader);
                            break;

                        case FrameType.PushPromise:     // Should not happen, since we disable this in our initial SETTINGS
                        case FrameType.Continuation:    // Should only be received while processing headers in ProcessHeadersFrame
                        default:
                            ThrowProtocolError();
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(ProcessIncomingFramesAsync)}: {e.Message}");

                Abort(e);
            }
            finally
            {
                _incomingBuffer.Dispose();
            }
        }

        // Note, this will return null for a streamId that's no longer in use.
        // Callers must check for this and send a RST_STREAM or ignore as appropriate.
        // If the streamId is invalid or the stream is idle, calling this function
        // will result in a connection level error.
        private Http2Stream? GetStream(int streamId)
        {
            if (streamId <= 0 || streamId >= _nextStream)
            {
                ThrowProtocolError();
            }

            lock (SyncObject)
            {
                if (!_httpStreams.TryGetValue(streamId, out Http2Stream? http2Stream))
                {
                    return null;
                }

                return http2Stream;
            }
        }

        private async ValueTask ProcessHeadersFrame(FrameHeader frameHeader)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{frameHeader}");
            Debug.Assert(frameHeader.Type == FrameType.Headers);

            bool endStream = frameHeader.EndStreamFlag;

            int streamId = frameHeader.StreamId;
            Http2Stream? http2Stream = GetStream(streamId);

            IHttpStreamHeadersHandler headersHandler;
            if (http2Stream != null)
            {
                http2Stream.OnHeadersStart();
                _rttEstimator.OnDataOrHeadersReceived(this, sendWindowUpdateBeforePing: true);
                headersHandler = http2Stream;
            }
            else
            {
                // http2Stream will be null if this is a closed stream. We will still process the headers,
                // to ensure the header decoding state is up-to-date, but we will discard the decoded headers.
                headersHandler = NopHeadersHandler.Instance;
            }

            _hpackDecoder.Decode(
                GetFrameData(_incomingBuffer.ActiveSpan.Slice(0, frameHeader.PayloadLength), frameHeader.PaddedFlag, frameHeader.PriorityFlag),
                frameHeader.EndHeadersFlag,
                headersHandler);
            _incomingBuffer.Discard(frameHeader.PayloadLength);

            while (!frameHeader.EndHeadersFlag)
            {
                frameHeader = await ReadFrameAsync().ConfigureAwait(false);

                if (frameHeader.Type != FrameType.Continuation ||
                    frameHeader.StreamId != streamId)
                {
                    ThrowProtocolError();
                }

                _hpackDecoder.Decode(
                    _incomingBuffer.ActiveSpan.Slice(0, frameHeader.PayloadLength),
                    frameHeader.EndHeadersFlag,
                    headersHandler);
                _incomingBuffer.Discard(frameHeader.PayloadLength);
            }

            _hpackDecoder.CompleteDecode();

            http2Stream?.OnHeadersComplete(endStream);
        }

        /// <summary>Nop implementation of <see cref="IHttpStreamHeadersHandler"/> used by <see cref="ProcessHeadersFrame"/>.</summary>
        private sealed class NopHeadersHandler : IHttpStreamHeadersHandler
        {
            public static readonly NopHeadersHandler Instance = new NopHeadersHandler();
            void IHttpStreamHeadersHandler.OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) { }
            void IHttpStreamHeadersHandler.OnHeadersComplete(bool endStream) { }
            void IHttpStreamHeadersHandler.OnStaticIndexedHeader(int index) { }
            void IHttpStreamHeadersHandler.OnStaticIndexedHeader(int index, ReadOnlySpan<byte> value) { }
            void IHttpStreamHeadersHandler.OnDynamicIndexedHeader(int? index, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) { }
        }

        private static ReadOnlySpan<byte> GetFrameData(ReadOnlySpan<byte> frameData, bool hasPad, bool hasPriority)
        {
            if (hasPad)
            {
                if (frameData.Length == 0)
                {
                    ThrowProtocolError();
                }

                int padLength = frameData[0];
                frameData = frameData.Slice(1);

                if (frameData.Length < padLength)
                {
                    ThrowProtocolError();
                }

                frameData = frameData.Slice(0, frameData.Length - padLength);
            }

            if (hasPriority)
            {
                if (frameData.Length < FrameHeader.PriorityInfoLength)
                {
                    ThrowProtocolError();
                }

                // We ignore priority info.
                frameData = frameData.Slice(FrameHeader.PriorityInfoLength);
            }

            return frameData;
        }

        /// <summary>
        /// Parses an ALTSVC frame, defined by RFC 7838 Section 4.
        /// </summary>
        /// <remarks>
        /// The RFC states that any parse errors should result in ignoring the frame.
        /// </remarks>
        private void ProcessAltSvcFrame(FrameHeader frameHeader)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{frameHeader}");
            Debug.Assert(frameHeader.Type == FrameType.AltSvc);

            ReadOnlySpan<byte> span = _incomingBuffer.ActiveSpan.Slice(0, frameHeader.PayloadLength);

            if (BinaryPrimitives.TryReadUInt16BigEndian(span, out ushort originLength))
            {
                span = span.Slice(2);

                // Check that this ALTSVC frame is valid for our pool's origin. ALTSVC frames can come in one of two ways:
                //  - On stream 0, the origin will be specified. HTTP/2 can service multiple origins per connection, and so the server can report origins other than what our pool is using.
                //  - Otherwise, the origin is implicitly defined by the request stream and must be of length 0.

                if ((frameHeader.StreamId != 0 && originLength == 0) || (frameHeader.StreamId == 0 && span.Length >= originLength && span.Slice(0, originLength).SequenceEqual(_pool.Http2AltSvcOriginUri)))
                {
                    span = span.Slice(originLength);

                    // The span now contains a string with the same format as Alt-Svc headers.

                    string altSvcHeaderValue = Encoding.ASCII.GetString(span);
                    _pool.HandleAltSvc(new[] { altSvcHeaderValue }, null);
                }
            }

            _incomingBuffer.Discard(frameHeader.PayloadLength);
        }

        private void ProcessDataFrame(FrameHeader frameHeader)
        {
            Debug.Assert(frameHeader.Type == FrameType.Data);

            Http2Stream? http2Stream = GetStream(frameHeader.StreamId);

            // Note, http2Stream will be null if this is a closed stream.
            // Just ignore the frame in this case.

            ReadOnlySpan<byte> frameData = GetFrameData(_incomingBuffer.ActiveSpan.Slice(0, frameHeader.PayloadLength), hasPad: frameHeader.PaddedFlag, hasPriority: false);

            bool endStream = frameHeader.EndStreamFlag;
            http2Stream?.OnResponseData(frameData, endStream);

            if (frameData.Length > 0)
            {
                bool windowUpdateSent = ExtendWindow(frameData.Length);
                if (http2Stream is not null && !endStream)
                {
                    _rttEstimator.OnDataOrHeadersReceived(this, sendWindowUpdateBeforePing: !windowUpdateSent);
                }
            }

            _incomingBuffer.Discard(frameHeader.PayloadLength);
        }

        private void ProcessSettingsFrame(FrameHeader frameHeader, bool initialFrame = false)
        {
            Debug.Assert(frameHeader.Type == FrameType.Settings);

            if (frameHeader.StreamId != 0)
            {
                ThrowProtocolError();
            }

            if (frameHeader.AckFlag)
            {
                if (frameHeader.PayloadLength != 0)
                {
                    ThrowProtocolError(Http2ProtocolErrorCode.FrameSizeError);
                }

                if (_receivedSettingsAck)
                {
                    ThrowProtocolError();
                }

                // We only send SETTINGS once initially, so we don't need to do anything in response to the ACK.
                // Just remember that we received one and we won't be expecting any more.
                _receivedSettingsAck = true;
                _rttEstimator.OnInitialSettingsAckReceived(this);
            }
            else
            {
                if ((frameHeader.PayloadLength % 6) != 0)
                {
                    ThrowProtocolError(Http2ProtocolErrorCode.FrameSizeError);
                }

                // Parse settings and process the ones we care about.
                ReadOnlySpan<byte> settings = _incomingBuffer.ActiveSpan.Slice(0, frameHeader.PayloadLength);
                bool maxConcurrentStreamsReceived = false;
                while (settings.Length > 0)
                {
                    Debug.Assert((settings.Length % 6) == 0);

                    ushort settingId = BinaryPrimitives.ReadUInt16BigEndian(settings);
                    settings = settings.Slice(2);
                    uint settingValue = BinaryPrimitives.ReadUInt32BigEndian(settings);
                    settings = settings.Slice(4);

                    if (NetEventSource.Log.IsEnabled()) Trace($"Applying setting {(SettingId)settingId}={settingValue}");

                    switch ((SettingId)settingId)
                    {
                        case SettingId.MaxConcurrentStreams:
                            ChangeMaxConcurrentStreams(settingValue);
                            maxConcurrentStreamsReceived = true;
                            break;

                        case SettingId.InitialWindowSize:
                            if (settingValue > 0x7FFFFFFF)
                            {
                                ThrowProtocolError(Http2ProtocolErrorCode.FlowControlError);
                            }

                            ChangeInitialWindowSize((int)settingValue);
                            break;

                        case SettingId.MaxFrameSize:
                            if (settingValue < 16384 || settingValue > 16777215)
                            {
                                ThrowProtocolError();
                            }

                            // We don't actually store this value; we always send frames of the minimum size (16K).
                            break;

                        case SettingId.EnableConnect:
                            if (settingValue == 1)
                            {
                                IsConnectEnabled = true;
                            }
                            else if (settingValue == 0 && IsConnectEnabled)
                            {
                                // Accroding to RFC: a sender MUST NOT send a SETTINGS_ENABLE_CONNECT_PROTOCOL parameter
                                // with the value of 0 after previously sending a value of 1.
                                // https://datatracker.ietf.org/doc/html/rfc8441#section-3
                                ThrowProtocolError();
                            }
                            break;

                        case SettingId.MaxHeaderListSize:
                            _maxHeaderListSize = settingValue;
                            _pool._lastSeenHttp2MaxHeaderListSize = _maxHeaderListSize;
                            break;

                        default:
                            // All others are ignored because we don't care about them.
                            // Note, per RFC, unknown settings IDs should be ignored.
                            break;
                    }
                }

                if (initialFrame)
                {
                    if (!maxConcurrentStreamsReceived)
                    {
                        // Set to 'infinite' because MaxConcurrentStreams was not set on the initial SETTINGS frame.
                        ChangeMaxConcurrentStreams(int.MaxValue);
                    }

                    if (_initialSettingsReceived is null)
                    {
                        Interlocked.CompareExchange(ref _initialSettingsReceived, s_settingsReceivedSingleton, null);
                    }
                    // Set result in case if CompareExchange lost the race
                    InitialSettingsReceived.TrySetResult(true);
                }

                _incomingBuffer.Discard(frameHeader.PayloadLength);

                // Send acknowledgement
                // Don't wait for completion, which could happen asynchronously.
                _frameWriter.SendSettingsAck();
            }
        }

        private void ChangeMaxConcurrentStreams(uint newValue)
        {
            lock (SyncObject)
            {
                if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(newValue)}={newValue}, {nameof(_streamsInUse)}={_streamsInUse}, {nameof(_availableStreamsWaiter)}?={_availableStreamsWaiter is not null}");

                Debug.Assert(_availableStreamsWaiter is null || _streamsInUse >= _maxConcurrentStreams);

                _maxConcurrentStreams = newValue;
                if (_streamsInUse < _maxConcurrentStreams)
                {
                    SignalAvailableStreamsWaiter(true);
                }
            }
        }

        private void ChangeInitialWindowSize(int newSize)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(newSize)}={newSize}");
            Debug.Assert(newSize >= 0);

            lock (SyncObject)
            {
                int delta = newSize - _initialServerStreamWindowSize;
                _initialServerStreamWindowSize = newSize;

                // Adjust existing streams
                foreach (KeyValuePair<int, Http2Stream> kvp in _httpStreams)
                {
                    kvp.Value.OnWindowUpdate(delta);
                }
            }
        }

        private void ProcessPriorityFrame(FrameHeader frameHeader)
        {
            Debug.Assert(frameHeader.Type == FrameType.Priority);

            if (frameHeader.StreamId == 0 || frameHeader.PayloadLength != FrameHeader.PriorityInfoLength)
            {
                ThrowProtocolError();
            }

            // Ignore priority info.

            _incomingBuffer.Discard(frameHeader.PayloadLength);
        }

        private void ProcessPingFrame(FrameHeader frameHeader)
        {
            Debug.Assert(frameHeader.Type == FrameType.Ping);

            if (frameHeader.StreamId != 0)
            {
                ThrowProtocolError();
            }

            if (frameHeader.PayloadLength != FrameHeader.PingLength)
            {
                ThrowProtocolError(Http2ProtocolErrorCode.FrameSizeError);
            }

            // We don't wait for SendPing acknowledgement before discarding
            // the incoming buffer, so we need to take a copy of the data. Read
            // it as a big-endian integer here to avoid allocating an array.
            Debug.Assert(sizeof(long) == FrameHeader.PingLength);
            ReadOnlySpan<byte> pingContent = _incomingBuffer.ActiveSpan.Slice(0, FrameHeader.PingLength);
            long pingContentLong = BinaryPrimitives.ReadInt64BigEndian(pingContent);

            if (NetEventSource.Log.IsEnabled()) Trace($"Received PING frame, content:{pingContentLong} ack: {frameHeader.AckFlag}");

            if (frameHeader.AckFlag)
            {
                ProcessPingAck(pingContentLong);
            }
            else
            {
                _frameWriter.SendPing(pingContentLong, isAck: true);
            }
            _incomingBuffer.Discard(frameHeader.PayloadLength);
        }

        private void ProcessWindowUpdateFrame(FrameHeader frameHeader)
        {
            Debug.Assert(frameHeader.Type == FrameType.WindowUpdate);

            if (frameHeader.PayloadLength != FrameHeader.WindowUpdateLength)
            {
                ThrowProtocolError(Http2ProtocolErrorCode.FrameSizeError);
            }

            int amount = BinaryPrimitives.ReadInt32BigEndian(_incomingBuffer.ActiveSpan) & 0x7FFFFFFF;
            if (NetEventSource.Log.IsEnabled()) Trace($"{frameHeader}. {nameof(amount)}={amount}");

            Debug.Assert(amount >= 0);
            if (amount <= 0)
            {
                ThrowProtocolError();
            }

            _incomingBuffer.Discard(frameHeader.PayloadLength);

            if (frameHeader.StreamId == 0)
            {
                _frameWriter.AddConnectionWindow(amount);
            }
            else
            {
                Http2Stream? http2Stream = GetStream(frameHeader.StreamId);
                if (http2Stream == null)
                {
                    // Ignore invalid stream ID, as per RFC
                    return;
                }

                http2Stream.OnWindowUpdate(amount);
            }
        }

        private void ProcessRstStreamFrame(FrameHeader frameHeader)
        {
            Debug.Assert(frameHeader.Type == FrameType.RstStream);

            if (frameHeader.PayloadLength != FrameHeader.RstStreamLength)
            {
                ThrowProtocolError(Http2ProtocolErrorCode.FrameSizeError);
            }

            if (frameHeader.StreamId == 0)
            {
                ThrowProtocolError();
            }

            Http2Stream? http2Stream = GetStream(frameHeader.StreamId);
            if (http2Stream == null)
            {
                // Ignore invalid stream ID, as per RFC
                _incomingBuffer.Discard(frameHeader.PayloadLength);
                return;
            }

            var protocolError = (Http2ProtocolErrorCode)BinaryPrimitives.ReadInt32BigEndian(_incomingBuffer.ActiveSpan);
            if (NetEventSource.Log.IsEnabled()) Trace(frameHeader.StreamId, $"{nameof(protocolError)}={protocolError}");

            _incomingBuffer.Discard(frameHeader.PayloadLength);

            bool canRetry = protocolError == Http2ProtocolErrorCode.RefusedStream;
            http2Stream.OnReset(HttpProtocolException.CreateHttp2StreamException(protocolError), resetStreamErrorCode: protocolError, canRetry: canRetry);
        }

        private void ProcessGoAwayFrame(FrameHeader frameHeader)
        {
            var (lastStreamId, errorCode) = ReadGoAwayFrame(frameHeader);

            Debug.Assert(lastStreamId >= 0);
            Exception resetException = HttpProtocolException.CreateHttp2ConnectionException(errorCode, SR.net_http_http2_connection_close);

            // There is no point sending more PING frames for RTT estimation:
            _rttEstimator.OnGoAwayReceived();

            List<Http2Stream> streamsToAbort = new List<Http2Stream>();
            lock (SyncObject)
            {
                Shutdown();

                foreach (KeyValuePair<int, Http2Stream> kvp in _httpStreams)
                {
                    int streamId = kvp.Key;
                    Debug.Assert(streamId == kvp.Value.StreamId);

                    if (streamId > lastStreamId)
                    {
                        streamsToAbort.Add(kvp.Value);
                    }
                }
            }

            // Avoid calling OnReset under the lock, as it may cause the Http2Stream to call back in to RemoveStream
            foreach (Http2Stream s in streamsToAbort)
            {
                s.OnReset(resetException, canRetry: true);
            }
        }

        private (int lastStreamId, Http2ProtocolErrorCode errorCode) ReadGoAwayFrame(FrameHeader frameHeader)
        {
            Debug.Assert(frameHeader.Type == FrameType.GoAway);

            if (frameHeader.PayloadLength < FrameHeader.GoAwayMinLength)
            {
                ThrowProtocolError(Http2ProtocolErrorCode.FrameSizeError);
            }

            if (frameHeader.StreamId != 0)
            {
                ThrowProtocolError();
            }

            int lastStreamId = (int)(BinaryPrimitives.ReadUInt32BigEndian(_incomingBuffer.ActiveSpan) & 0x7FFFFFFF);
            Http2ProtocolErrorCode errorCode = (Http2ProtocolErrorCode)BinaryPrimitives.ReadInt32BigEndian(_incomingBuffer.ActiveSpan.Slice(sizeof(int)));
            if (NetEventSource.Log.IsEnabled()) Trace(frameHeader.StreamId, $"{nameof(lastStreamId)}={lastStreamId}, {nameof(errorCode)}={errorCode}");

            _incomingBuffer.Discard(frameHeader.PayloadLength);

            return (lastStreamId, errorCode);
        }


        internal void HeartBeat()
        {
            if (_shutdown)
                return;

            try
            {
                VerifyKeepAlive();
            }
            catch (Exception e)
            {
                if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(HeartBeat)}: {e.Message}");

                Abort(e);
            }
        }

        private static (ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> rest) SplitBuffer(ReadOnlyMemory<byte> buffer, int maxSize) =>
            buffer.Length > maxSize ?
                (buffer.Slice(0, maxSize), buffer.Slice(maxSize)) :
                (buffer, Memory<byte>.Empty);

        private void WriteIndexedHeader(int index, ref ArrayBuffer headerBuffer)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(index)}={index}");

            int bytesWritten;
            while (!HPackEncoder.EncodeIndexedHeaderField(index, headerBuffer.AvailableSpan, out bytesWritten))
            {
                headerBuffer.Grow();
            }

            headerBuffer.Commit(bytesWritten);
        }

        private void WriteIndexedHeader(int index, string value, ref ArrayBuffer headerBuffer)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(index)}={index}, {nameof(value)}={value}");

            int bytesWritten;
            while (!HPackEncoder.EncodeLiteralHeaderFieldWithoutIndexing(index, value, valueEncoding: null, headerBuffer.AvailableSpan, out bytesWritten))
            {
                headerBuffer.Grow();
            }

            headerBuffer.Commit(bytesWritten);
        }

        private void WriteLiteralHeader(string name, ReadOnlySpan<string> values, Encoding? valueEncoding, ref ArrayBuffer headerBuffer)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(name)}={name}, {nameof(values)}={string.Join(", ", values.ToArray())}");

            int bytesWritten;
            while (!HPackEncoder.EncodeLiteralHeaderFieldWithoutIndexingNewName(name, values, HttpHeaderParser.DefaultSeparator, valueEncoding, headerBuffer.AvailableSpan, out bytesWritten))
            {
                headerBuffer.Grow();
            }

            headerBuffer.Commit(bytesWritten);
        }

        private void WriteLiteralHeaderValues(ReadOnlySpan<string> values, string? separator, Encoding? valueEncoding, ref ArrayBuffer headerBuffer)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(values)}={string.Join(separator, values.ToArray())}");

            int bytesWritten;
            while (!HPackEncoder.EncodeStringLiterals(values, separator, valueEncoding, headerBuffer.AvailableSpan, out bytesWritten))
            {
                headerBuffer.Grow();
            }

            headerBuffer.Commit(bytesWritten);
        }

        private void WriteLiteralHeaderValue(string value, Encoding? valueEncoding, ref ArrayBuffer headerBuffer)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(value)}={value}");

            int bytesWritten;
            while (!HPackEncoder.EncodeStringLiteral(value, valueEncoding, headerBuffer.AvailableSpan, out bytesWritten))
            {
                headerBuffer.Grow();
            }

            headerBuffer.Commit(bytesWritten);
        }

        private void WriteBytes(ReadOnlySpan<byte> bytes, ref ArrayBuffer headerBuffer)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(bytes.Length)}={bytes.Length}");

            headerBuffer.EnsureAvailableSpace(bytes.Length);
            bytes.CopyTo(headerBuffer.AvailableSpan);
            headerBuffer.Commit(bytes.Length);
        }

        private int WriteHeaderCollection(HttpRequestMessage request, HttpHeaders headers, ref ArrayBuffer headerBuffer)
        {
            if (NetEventSource.Log.IsEnabled()) Trace("");

            HeaderEncodingSelector<HttpRequestMessage>? encodingSelector = _pool.Settings._requestHeaderEncodingSelector;

            ref string[]? tmpHeaderValuesArray = ref t_headerValues;

            ReadOnlySpan<HeaderEntry> entries = headers.GetEntries();
            int headerListSize = entries.Length * HeaderField.RfcOverhead;

            foreach (HeaderEntry header in entries)
            {
                int headerValuesCount = HttpHeaders.GetStoreValuesIntoStringArray(header.Key, header.Value, ref tmpHeaderValuesArray);
                Debug.Assert(headerValuesCount > 0, "No values for header??");
                ReadOnlySpan<string> headerValues = tmpHeaderValuesArray.AsSpan(0, headerValuesCount);

                Encoding? valueEncoding = encodingSelector?.Invoke(header.Key.Name, request);

                KnownHeader? knownHeader = header.Key.KnownHeader;
                if (knownHeader != null)
                {
                    // The Host header is not sent for HTTP2 because we send the ":authority" pseudo-header instead
                    // (see pseudo-header handling below in WriteHeaders).
                    // The Connection, Upgrade and ProxyConnection headers are also not supported in HTTP2.
                    if (knownHeader != KnownHeaders.Host && knownHeader != KnownHeaders.Connection && knownHeader != KnownHeaders.Upgrade && knownHeader != KnownHeaders.ProxyConnection)
                    {
                        // The length of the encoded name may be shorter than the actual name.
                        // Ensure that headerListSize is always >= of the actual size.
                        headerListSize += knownHeader.Name.Length;

                        if (knownHeader == KnownHeaders.TE)
                        {
                            // HTTP/2 allows only 'trailers' TE header. rfc7540 8.1.2.2
                            foreach (string value in headerValues)
                            {
                                if (string.Equals(value, "trailers", StringComparison.OrdinalIgnoreCase))
                                {
                                    WriteBytes(knownHeader.Http2EncodedName, ref headerBuffer);
                                    WriteLiteralHeaderValue(value, valueEncoding, ref headerBuffer);
                                    break;
                                }
                            }
                            continue;
                        }

                        // For all other known headers, send them via their pre-encoded name and the associated value.
                        WriteBytes(knownHeader.Http2EncodedName, ref headerBuffer);
                        string? separator = null;
                        if (headerValues.Length > 1)
                        {
                            HttpHeaderParser? parser = header.Key.Parser;
                            if (parser != null && parser.SupportsMultipleValues)
                            {
                                separator = parser.Separator;
                            }
                            else
                            {
                                separator = HttpHeaderParser.DefaultSeparator;
                            }
                        }

                        WriteLiteralHeaderValues(headerValues, separator, valueEncoding, ref headerBuffer);
                    }
                }
                else
                {
                    // The header is not known: fall back to just encoding the header name and value(s).
                    WriteLiteralHeader(header.Key.Name, headerValues, valueEncoding, ref headerBuffer);
                }
            }

            return headerListSize;
        }

        private void WriteHeaders(HttpRequestMessage request, ref ArrayBuffer headerBuffer)
        {
            if (NetEventSource.Log.IsEnabled()) Trace("");

            // HTTP2 does not support Transfer-Encoding: chunked, so disable this on the request.
            if (request.HasHeaders && request.Headers.TransferEncodingChunked == true)
            {
                request.Headers.TransferEncodingChunked = false;
            }

            HttpMethod normalizedMethod = HttpMethod.Normalize(request.Method);

            // Method is normalized so we can do reference equality here.
            if (ReferenceEquals(normalizedMethod, HttpMethod.Get))
            {
                WriteIndexedHeader(H2StaticTable.MethodGet, ref headerBuffer);
            }
            else if (ReferenceEquals(normalizedMethod, HttpMethod.Post))
            {
                WriteIndexedHeader(H2StaticTable.MethodPost, ref headerBuffer);
            }
            else
            {
                WriteIndexedHeader(H2StaticTable.MethodGet, normalizedMethod.Method, ref headerBuffer);
            }

            WriteIndexedHeader(_pool.IsSecure ? H2StaticTable.SchemeHttps : H2StaticTable.SchemeHttp, ref headerBuffer);

            if (request.HasHeaders && request.Headers.Host is string host)
            {
                WriteIndexedHeader(H2StaticTable.Authority, host, ref headerBuffer);
            }
            else
            {
                WriteBytes(_pool._http2EncodedAuthorityHostHeader, ref headerBuffer);
            }

            Debug.Assert(request.RequestUri != null);
            string pathAndQuery = request.RequestUri.PathAndQuery;
            if (pathAndQuery == "/")
            {
                WriteIndexedHeader(H2StaticTable.PathSlash, ref headerBuffer);
            }
            else
            {
                WriteIndexedHeader(H2StaticTable.PathSlash, pathAndQuery, ref headerBuffer);
            }

            int headerListSize = 3 * HeaderField.RfcOverhead; // Method, Authority, Path

            if (request.HasHeaders)
            {
                if (request.Headers.Protocol is string protocol)
                {
                    WriteBytes(ProtocolLiteralHeaderBytes, ref headerBuffer);
                    Encoding? protocolEncoding = _pool.Settings._requestHeaderEncodingSelector?.Invoke(":protocol", request);
                    WriteLiteralHeaderValue(protocol, protocolEncoding, ref headerBuffer);
                    headerListSize += HeaderField.RfcOverhead;
                }

                headerListSize += WriteHeaderCollection(request, request.Headers, ref headerBuffer);
            }

            // Determine cookies to send.
            if (_pool.Settings._useCookies)
            {
                string cookiesFromContainer = _pool.Settings._cookieContainer!.GetCookieHeader(request.RequestUri);
                if (cookiesFromContainer != string.Empty)
                {
                    WriteBytes(KnownHeaders.Cookie.Http2EncodedName, ref headerBuffer);
                    Encoding? cookieEncoding = _pool.Settings._requestHeaderEncodingSelector?.Invoke(KnownHeaders.Cookie.Name, request);
                    WriteLiteralHeaderValue(cookiesFromContainer, cookieEncoding, ref headerBuffer);
                    headerListSize += HttpKnownHeaderNames.Cookie.Length + HeaderField.RfcOverhead;
                }
            }

            if (request.Content == null)
            {
                // Write out Content-Length: 0 header to indicate no body,
                // unless this is a method that never has a body.
                if (normalizedMethod.MustHaveRequestBody)
                {
                    WriteBytes(KnownHeaders.ContentLength.Http2EncodedName, ref headerBuffer);
                    WriteLiteralHeaderValue("0", valueEncoding: null, ref headerBuffer);
                    headerListSize += HttpKnownHeaderNames.ContentLength.Length + HeaderField.RfcOverhead;
                }
            }
            else
            {
                headerListSize += WriteHeaderCollection(request, request.Content.Headers, ref headerBuffer);
            }

            // The headerListSize is an approximation of the total header length.
            // This is acceptable as long as the value is always >= the actual length.
            // We must avoid ever sending more than the server allowed.
            // This approach must be revisted if we ever support the dynamic table or compression when sending requests.
            headerListSize += headerBuffer.ActiveLength;

            uint maxHeaderListSize = _maxHeaderListSize;
            if ((uint)headerListSize > maxHeaderListSize)
            {
                throw new HttpRequestException(SR.Format(SR.net_http_request_headers_exceeded_length, maxHeaderListSize));
            }
        }

        private void AddStream(Http2Stream http2Stream)
        {
            lock (SyncObject)
            {
                if (_nextStream == MaxStreamId)
                {
                    // We have exhausted StreamIds. Shut down the connection.
                    Shutdown();
                }

                if (_abortException is not null)
                {
                    throw GetRequestAbortedException(_abortException);
                }

                if (_shutdown)
                {
                    // The connection has shut down. Throw a retryable exception so that this request will be handled on another connection.
                    ThrowRetry(SR.net_http_server_shutdown);
                }

                if (_streamsInUse > _maxConcurrentStreams)
                {
                    // The server must have sent a downward adjustment to SETTINGS_MAX_CONCURRENT_STREAMS, so our previous stream reservation is no longer valid.
                    // We might want a better exception message here, but in general the user shouldn't see this anyway since it will be retried.
                    ThrowRetry(SR.net_http_request_aborted);
                }

                // Now that we're holding the lock, configure the stream.  The lock must be held while
                // assigning the stream ID to ensure only one stream gets an ID, and it must be held
                // across setting the initial window size (available credit) and storing the stream into
                // collection such that window size updates are able to atomically affect all known streams.
                http2Stream.Initialize(_nextStream, _initialServerStreamWindowSize);

                // Client-initiated streams are always odd-numbered, so increase by 2.
                _nextStream += 2;

                _httpStreams.Add(http2Stream.StreamId, http2Stream);
            }
        }

        private async ValueTask<Http2Stream> SendHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArrayBuffer headerBuffer = default;
            try
            {
                if (HttpTelemetry.Log.IsEnabled()) HttpTelemetry.Log.RequestHeadersStart(Id);

                // Serialize headers to a temporary buffer, and do as much work to prepare to send the headers as we can
                // before taking the write lock.
                headerBuffer = new ArrayBuffer(InitialConnectionBufferSize, usePool: true);
                WriteHeaders(request, ref headerBuffer);
                ReadOnlyMemory<byte> headerBytes = headerBuffer.ActiveMemory;
                Debug.Assert(headerBytes.Length > 0);

                // Calculate the total number of bytes we're going to use (content + headers).
                int frameCount = ((headerBytes.Length - 1) / FrameHeader.MaxPayloadLength) + 1;
                int totalSize = headerBytes.Length + (frameCount * FrameHeader.Size);

                // Construct and initialize the new Http2Stream instance.  It's stream ID must be set below
                // before the instance is used and stored into the dictionary.  However, we construct it here
                // so as to avoid the allocation and initialization expense while holding multiple locks.
                var http2Stream = new Http2Stream(request, this);

                await http2Stream.SendHeadersAsync(headerBytes, cancellationToken).ConfigureAwait(false);

                if (HttpTelemetry.Log.IsEnabled()) HttpTelemetry.Log.RequestHeadersStop();

                return http2Stream;
            }
            catch
            {
                ReleaseStream();
                throw;
            }
            finally
            {
                headerBuffer.Dispose();
            }
        }

        private bool ExtendWindow(int amount)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(amount)}={amount}");
            Debug.Assert(amount > 0);
            Debug.Assert(_pendingWindowUpdate < ConnectionWindowThreshold);

            _pendingWindowUpdate += amount;
            if (_pendingWindowUpdate < ConnectionWindowThreshold)
            {
                if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(_pendingWindowUpdate)} {_pendingWindowUpdate} < {ConnectionWindowThreshold}.");
                return false;
            }

            _frameWriter.SendWindowUpdate(0, _pendingWindowUpdate);
            _pendingWindowUpdate = 0;
            return true;
        }

        private bool ForceSendConnectionWindowUpdate()
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(_pendingWindowUpdate)}={_pendingWindowUpdate}");
            if (_pendingWindowUpdate == 0) return false;

            _frameWriter.SendWindowUpdate(0, _pendingWindowUpdate);
            _pendingWindowUpdate = 0;
            return true;
        }

        public override long GetIdleTicks(long nowTicks)
        {
            lock (SyncObject)
            {
                return _streamsInUse == 0 ? base.GetIdleTicks(nowTicks) : 0;
            }
        }

        /// <summary>Abort all streams and cause further processing to fail.</summary>
        /// <param name="abortException">Exception causing Abort to be called.</param>
        private void Abort(Exception abortException)
        {
            if (NetEventSource.Log.IsEnabled()) Trace($"{nameof(abortException)}=={abortException}");

            // The connection has failed, e.g. failed IO or a connection-level protocol error.
            List<Http2Stream> streamsToAbort = new List<Http2Stream>();
            lock (SyncObject)
            {
                if (_abortException is not null)
                {
                    if (NetEventSource.Log.IsEnabled()) Trace($"Abort called while already aborting. {nameof(abortException)}={abortException}");
                    return;
                }

                _abortException = abortException;

                Shutdown();

                foreach (KeyValuePair<int, Http2Stream> kvp in _httpStreams)
                {
                    int streamId = kvp.Key;
                    Debug.Assert(streamId == kvp.Value.StreamId);

                    streamsToAbort.Add(kvp.Value);
                }
            }

            // Avoid calling OnReset under the lock, as it may cause the Http2Stream to call back in to RemoveStream
            foreach (Http2Stream s in streamsToAbort)
            {
                s.OnReset(_abortException);
            }
        }

        private void FinalTeardown()
        {
            if (NetEventSource.Log.IsEnabled()) Trace("");

            Debug.Assert(_shutdown);
            Debug.Assert(_streamsInUse == 0);

            GC.SuppressFinalize(this);

            _stream.Dispose();

            _frameWriter.Complete();

            // We're not disposing the _incomingBuffer here as it may still be in use by
            // ProcessIncomingFramesAsync, and that method is responsible for returning the buffer.

            MarkConnectionAsClosed();
        }

        public override void Dispose()
        {
            lock (SyncObject)
            {
                Shutdown();
            }
        }

        private enum FrameType : byte
        {
            Data = 0,
            Headers = 1,
            Priority = 2,
            RstStream = 3,
            Settings = 4,
            PushPromise = 5,
            Ping = 6,
            GoAway = 7,
            WindowUpdate = 8,
            Continuation = 9,
            AltSvc = 10,

            Last = 10
        }

        private readonly struct FrameHeader
        {
            public readonly int PayloadLength;
            public readonly FrameType Type;
            public readonly FrameFlags Flags;
            public readonly int StreamId;

            public const int Size = 9;
            public const int MaxPayloadLength = 16384;

            public const int SettingLength = 6;            // per setting (total SETTINGS length must be a multiple of this)
            public const int PriorityInfoLength = 5;       // for both PRIORITY frame and priority info within HEADERS
            public const int PingLength = 8;
            public const int WindowUpdateLength = 4;
            public const int RstStreamLength = 4;
            public const int GoAwayMinLength = 8;

            public FrameHeader(int payloadLength, FrameType type, FrameFlags flags, int streamId)
            {
                Debug.Assert(streamId >= 0);

                PayloadLength = payloadLength;
                Type = type;
                Flags = flags;
                StreamId = streamId;
            }

            public bool PaddedFlag => (Flags & FrameFlags.Padded) != 0;
            public bool AckFlag => (Flags & FrameFlags.Ack) != 0;
            public bool EndHeadersFlag => (Flags & FrameFlags.EndHeaders) != 0;
            public bool EndStreamFlag => (Flags & FrameFlags.EndStream) != 0;
            public bool PriorityFlag => (Flags & FrameFlags.Priority) != 0;

            public static FrameHeader ReadFrom(ReadOnlySpan<byte> buffer)
            {
                Debug.Assert(buffer.Length >= Size);

                FrameFlags flags = (FrameFlags)buffer[4]; // do first to avoid some bounds checks
                int payloadLength = (buffer[0] << 16) | (buffer[1] << 8) | buffer[2];
                FrameType type = (FrameType)buffer[3];
                int streamId = (int)(BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(5)) & 0x7FFFFFFF);

                return new FrameHeader(payloadLength, type, flags, streamId);
            }

            public static void WriteTo(Span<byte> destination, int payloadLength, FrameType type, FrameFlags flags, int streamId)
            {
                Debug.Assert(destination.Length >= Size);
                Debug.Assert(type <= FrameType.Last);
                Debug.Assert((flags & FrameFlags.ValidBits) == flags);
                Debug.Assert((uint)payloadLength <= MaxPayloadLength);

                // This ordering helps eliminate bounds checks.
                BinaryPrimitives.WriteInt32BigEndian(destination.Slice(5), streamId);
                destination[4] = (byte)flags;
                destination[0] = (byte)((payloadLength & 0x00FF0000) >> 16);
                destination[1] = (byte)((payloadLength & 0x0000FF00) >> 8);
                destination[2] = (byte)(payloadLength & 0x000000FF);
                destination[3] = (byte)type;
            }

            public override string ToString() => $"StreamId={StreamId}; Type={Type}; Flags={Flags}; PayloadLength={PayloadLength}"; // Description for diagnostic purposes
        }

        [Flags]
        private enum FrameFlags : byte
        {
            None = 0,

            // Some frame types define bits differently.  Define them all here for simplicity.

            EndStream =     0b00000001,
            Ack =           0b00000001,
            EndHeaders =    0b00000100,
            Padded =        0b00001000,
            Priority =      0b00100000,

            ValidBits =     0b00101101
        }

        private enum SettingId : ushort
        {
            HeaderTableSize = 0x1,
            EnablePush = 0x2,
            MaxConcurrentStreams = 0x3,
            InitialWindowSize = 0x4,
            MaxFrameSize = 0x5,
            MaxHeaderListSize = 0x6,
            EnableConnect = 0x8
        }

        private static TaskCompletionSourceWithCancellation<bool> CreateSuccessfullyCompletedTcs()
        {
            var tcs = new TaskCompletionSourceWithCancellation<bool>();
            tcs.TrySetResult(true);
            return tcs;
        }

        // Note that this is safe to be called concurrently by multiple threads.

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool async, CancellationToken cancellationToken)
        {
            Debug.Assert(async);
            if (NetEventSource.Log.IsEnabled()) Trace($"Sending request: {request}");

            try
            {
                Http2Stream http2Stream = await SendHeadersAsync(request, cancellationToken).ConfigureAwait(false);

                bool duplex = request.Content != null && request.Content.AllowDuplex;

                // If we have duplex content, then don't propagate the cancellation to the request body task.
                // If cancellation occurs before we receive the response headers, then we will cancel the request body anyway.
                CancellationToken requestBodyCancellationToken = duplex ? CancellationToken.None : cancellationToken;

                // Start sending request body, if any.
                Task requestBodyTask = http2Stream.SendRequestBodyAsync(requestBodyCancellationToken);

                // Start receiving the response headers.
                Task responseHeadersTask = http2Stream.ReadResponseHeadersAsync(cancellationToken);

                // Wait for either task to complete.  The best and most common case is when the request body completes
                // before the response headers, in which case we can fully process the sending of the request and then
                // fully process the sending of the response.  WhenAny is not free, so we do a fast-path check to see
                // if the request body completed synchronously, only progressing to do the WhenAny if it didn't. Then
                // if the WhenAny completes and either the WhenAny indicated that the request body completed or
                // both tasks completed, we can proceed to handle the request body as if it completed first.  We also
                // check whether the request content even allows for duplex communication; if it doesn't (none of
                // our built-in content types do), then we can just proceed to wait for the request body content to
                // complete before worrying about response headers completing.
                if (requestBodyTask.IsCompleted ||
                    duplex == false ||
                    await Task.WhenAny(requestBodyTask, responseHeadersTask).ConfigureAwait(false) == requestBodyTask ||
                    requestBodyTask.IsCompleted ||
                    http2Stream.SendRequestFinished)
                {
                    // The sending of the request body completed before receiving all of the request headers (or we're
                    // ok waiting for the request body even if it hasn't completed, e.g. because we're not doing duplex).
                    // This is the common and desirable case.
                    try
                    {
                        await requestBodyTask.ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        if (NetEventSource.Log.IsEnabled()) Trace($"Sending request content failed: {e}");
                        LogExceptions(responseHeadersTask); // Observe exception (if any) on responseHeadersTask.
                        throw;
                    }
                }
                else
                {
                    // We received the response headers but the request body hasn't yet finished; this most commonly happens
                    // when the protocol is being used to enable duplex communication. If the connection is aborted or if we
                    // get RST or GOAWAY from server, exception will be stored in stream._abortException and propagated up
                    // to caller if possible while processing response, but make sure that we log any exceptions from this task
                    // completing asynchronously).
                    LogExceptions(requestBodyTask);
                }

                // Wait for the response headers to complete if they haven't already, propagating any exceptions.
                await responseHeadersTask.ConfigureAwait(false);

                return http2Stream.GetAndClearResponse();
            }
            catch (HttpIOException e)
            {
                throw new HttpRequestException(e.HttpRequestError, e.Message, e);
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                throw new HttpRequestException(HttpRequestError.Unknown, SR.net_http_client_execution_error, e);
            }
        }

        private void RemoveStream(Http2Stream http2Stream)
        {
            if (NetEventSource.Log.IsEnabled()) Trace(http2Stream.StreamId, "");

            lock (SyncObject)
            {
                if (!_httpStreams.Remove(http2Stream.StreamId))
                {
                    Debug.Fail($"Stream {http2Stream.StreamId} not found in dictionary during RemoveStream???");
                    return;
                }
            }

            ReleaseStream();
        }

        private void RefreshPingTimestamp()
        {
            _nextPingRequestTimestamp = Environment.TickCount64 + _keepAlivePingDelay;
        }

        private void ProcessPingAck(long payload)
        {
            // RttEstimator is using negative values in PING payloads.
            // _keepAlivePingPayload is always non-negative.
            if (payload < 0) // RTT ping
            {
                _rttEstimator.OnPingAckReceived(payload, this);
            }
            else // Keepalive ping
            {
                if (_keepAliveState != KeepAliveState.PingSent)
                    ThrowProtocolError();
                if (Interlocked.Read(ref _keepAlivePingPayload) != payload)
                    ThrowProtocolError();
                _keepAliveState = KeepAliveState.None;
            }
        }

        private void VerifyKeepAlive()
        {
            if (_keepAlivePingPolicy == HttpKeepAlivePingPolicy.WithActiveRequests)
            {
                lock (SyncObject)
                {
                    if (_streamsInUse == 0)
                    {
                        return;
                    }
                }
            }

            long now = Environment.TickCount64;
            switch (_keepAliveState)
            {
                case KeepAliveState.None:
                    // Check whether keep alive delay has passed since last frame received
                    if (now > _nextPingRequestTimestamp)
                    {
                        // Set the status directly to ping sent and set the timestamp
                        _keepAliveState = KeepAliveState.PingSent;
                        _keepAlivePingTimeoutTimestamp = now + _keepAlivePingTimeout;

                        long pingPayload = Interlocked.Increment(ref _keepAlivePingPayload);
                        _frameWriter.SendPing(pingPayload, isAck: false);
                        return;
                    }
                    break;
                case KeepAliveState.PingSent:
                    if (now > _keepAlivePingTimeoutTimestamp)
                        ThrowProtocolError();
                    break;
                default:
                    Debug.Fail($"Unexpected keep alive state ({_keepAliveState})");
                    break;
            }
        }

        public sealed override string ToString() => $"{nameof(Http2Connection)}({_pool})"; // Description for diagnostic purposes

        public override void Trace(string message, [CallerMemberName] string? memberName = null) =>
            Trace(0, message, memberName);

        internal void Trace(int streamId, string message, [CallerMemberName] string? memberName = null) =>
            NetEventSource.Log.HandlerMessage(
                _pool?.GetHashCode() ?? 0,    // pool ID
                GetHashCode(),                // connection ID
                streamId,                     // stream ID
                memberName,                   // method name
                message);                     // message

        [DoesNotReturn]
        private static void ThrowRetry(string message, Exception? innerException = null) =>
            throw CreateRetryException(message, innerException);

        private static HttpRequestException CreateRetryException(string message, Exception? innerException = null) =>
            new HttpRequestException((innerException as HttpIOException)?.HttpRequestError ?? HttpRequestError.Unknown, message, innerException, RequestRetryType.RetryOnConnectionFailure);

        private static Exception GetRequestAbortedException(Exception? innerException = null) =>
            innerException as HttpIOException ?? new IOException(SR.net_http_request_aborted, innerException);

        [DoesNotReturn]
        private static void ThrowRequestAborted(Exception? innerException = null) =>
            throw GetRequestAbortedException(innerException);

        [DoesNotReturn]
        private static void ThrowProtocolError() =>
            ThrowProtocolError(Http2ProtocolErrorCode.ProtocolError);

        [DoesNotReturn]
        private static void ThrowProtocolError(Http2ProtocolErrorCode errorCode, string? message = null) =>
            throw HttpProtocolException.CreateHttp2ConnectionException(errorCode, message);
    }
}
