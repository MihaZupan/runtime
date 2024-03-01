// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;


namespace System.Net.Http
{
    internal sealed partial class Http2Connection
    {
        private sealed class Http2StreamWriteAwaitable : IValueTaskSource
        {
            private static readonly Action<object?, CancellationToken> s_cancelThisAwaitableCallback = static (state, cancellationToken) =>
            {
                Http2StreamWriteAwaitable thisRef = (Http2StreamWriteAwaitable)state!;

                Exception exception =
                    thisRef.Stream.ReplaceExceptionOnRequestBodyCancellationIfNeeded() ??
                    ExceptionDispatchInfo.SetCurrentStackTrace(
                        CancellationHelper.CreateOperationCanceledException(innerException: null, cancellationToken));

                thisRef._waitSource.SetException(exception);
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
    }
}
