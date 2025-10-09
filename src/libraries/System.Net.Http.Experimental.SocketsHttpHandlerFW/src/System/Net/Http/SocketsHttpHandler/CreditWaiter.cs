// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace System.Net.Http
{
    /// <summary>Represents a waiter for credit.</summary>
    internal sealed class CreditWaiter : IValueTaskSource<int>
    {
        // State for the implementation of the CreditWaiter. Note that neither _cancellationToken nor
        // _registration are zero'd out upon completion, because they're used for synchronization
        // between successful completion and cancellation.  This means an instance may end up
        // referencing the underlying CancellationTokenSource even after the await operation has completed.

        /// <summary>Cancellation token for the current wait operation.</summary>
        private CancellationToken _cancellationToken;
        /// <summary>Cancellation registration for the current wait operation.</summary>
        private CancellationTokenRegistration _registration;
        /// <summary><see cref="IValueTaskSource"/> implementation.</summary>
        private ManualResetValueTaskSourceCore<int> _source;

        // State carried with the waiter for the consumer to use; these aren't used at all in the implementation.

        /// <summary>Amount of credit desired by this waiter.</summary>
        public int Amount;
        /// <summary>Next waiter in a list of waiters.</summary>
        public CreditWaiter? Next;

        /// <summary>Initializes a waiter for a credit wait operation.</summary>
        /// <param name="cancellationToken">The cancellation token for this wait operation.</param>
        public CreditWaiter(CancellationToken cancellationToken)
        {
            _source.RunContinuationsAsynchronously = true;
            RegisterCancellation(cancellationToken);
        }

        /// <summary>Re-initializes a waiter for a credit wait operation.</summary>
        /// <param name="cancellationToken">The cancellation token for this wait operation.</param>
        public void ResetForAwait(CancellationToken cancellationToken)
        {
            _source.Reset();
            RegisterCancellation(cancellationToken);
        }

        /// <summary>Registers with the cancellation token to transition the source to a canceled state.</summary>
        /// <param name="cancellationToken">The cancellation token with which to register.</param>
        private void RegisterCancellation(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _registration = cancellationToken.UnsafeRegister(static (s, cancellationToken) =>
            {
                // The callback will only fire if cancellation owns the right to complete the instance.
                ((CreditWaiter)s!)._source.SetException(ExceptionDispatchInfo.SetCurrentStackTrace(new OperationCanceledException(cancellationToken)));
            }, this);
        }

        /// <summary>Wraps the instance as a <see cref="ValueTask{TResult}"/> to make it awaitable.</summary>
        public ValueTask<int> AsValueTask() => new ValueTask<int>(this, _source.Version);

        /// <summary>Completes the instance with the specified result.</summary>
        /// <param name="result">The result value.</param>
        /// <returns>true if the instance was successfully completed; false if it was or is being canceled.</returns>
        public bool TrySetResult(int result)
        {
            if (UnregisterAndOwnCompletion())
            {
                _source.SetResult(result);
                return true;
            }

            return false;
        }

        /// <summary>Disposes the instance, failing any outstanding wait.</summary>
        public void Dispose()
        {
            if (UnregisterAndOwnCompletion())
            {
                _source.SetException(ExceptionDispatchInfo.SetCurrentStackTrace(new ObjectDisposedException(nameof(CreditManager), SR.net_http_disposed_while_in_use)));
            }
        }

        /// <summary>Unregisters the cancellation callback.</summary>
        /// <returns>true if the non-cancellation caller has the right to complete the instance; false if the instance was or is being completed by cancellation.</returns>
        private bool UnregisterAndOwnCompletion()
        {
            if (!_cancellationToken.CanBeCanceled)
            {
                return true;
            }

            _registration.Dispose();
            return !_cancellationToken.IsCancellationRequested;
        }

        int IValueTaskSource<int>.GetResult(short token) =>
            _source.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) =>
            _source.GetStatus(token);
        void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _source.OnCompleted(continuation, state, token, flags);
    }
}
