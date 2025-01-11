// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace System.Threading
{
    public unsafe class Overlapped
    {
        private IAsyncResult? _asyncResult;
        internal object? _callback; // IOCompletionCallback or IOCompletionCallbackHelper
        private NativeOverlapped* _pNativeOverlapped;
        private IntPtr _eventHandle;
        private int _offsetLow;
        private int _offsetHigh;

        public Overlapped()
        {
        }

        public Overlapped(int offsetLo, int offsetHi, IntPtr hEvent, IAsyncResult? ar)
        {
            _offsetLow = offsetLo;
            _offsetHigh = offsetHi;
            _eventHandle = hEvent;
            _asyncResult = ar;
        }

        [Obsolete("This constructor is not 64-bit compatible and has been deprecated. Use the constructor that accepts an IntPtr for the event handle instead.")]
        public Overlapped(int offsetLo, int offsetHi, int hEvent, IAsyncResult? ar)
            : this(offsetLo, offsetHi, new IntPtr(hEvent), ar)
        {
        }

        public IAsyncResult AsyncResult
        {
            get => _asyncResult!;
            set => _asyncResult = value;
        }

        public int OffsetLow
        {
            get => (_pNativeOverlapped != null) ? _pNativeOverlapped->OffsetLow : _offsetLow;
            set => ((_pNativeOverlapped != null) ? ref _pNativeOverlapped->OffsetLow : ref _offsetLow) = value;
        }

        public int OffsetHigh
        {
            get => (_pNativeOverlapped != null) ? _pNativeOverlapped->OffsetHigh : _offsetHigh;
            set => ((_pNativeOverlapped != null) ? ref _pNativeOverlapped->OffsetHigh : ref _offsetHigh) = value;
        }

        [Obsolete("Overlapped.EventHandle is not 64-bit compatible and has been deprecated. Use EventHandleIntPtr instead.")]
        public int EventHandle
        {
            get => EventHandleIntPtr.ToInt32();
            set => EventHandleIntPtr = new IntPtr(value);
        }

        public IntPtr EventHandleIntPtr
        {
            get => (_pNativeOverlapped != null) ? _pNativeOverlapped->EventHandle : _eventHandle;
            set => ((_pNativeOverlapped != null) ? ref _pNativeOverlapped->EventHandle : ref _eventHandle) = value;
        }

        [Obsolete("This overload is not safe and has been deprecated. Use Pack(IOCompletionCallback?, object?) instead.")]
        [CLSCompliant(false)]
        public NativeOverlapped* Pack(IOCompletionCallback? iocb)
            => Pack(iocb, null);

        [CLSCompliant(false)]
        public NativeOverlapped* Pack(IOCompletionCallback? iocb, object? userData)
        {
            if (_pNativeOverlapped != null)
            {
                throw new InvalidOperationException(SR.InvalidOperation_Overlapped_Pack);
            }

            if (iocb != null)
            {
                ExecutionContext? ec = ExecutionContext.Capture();
                _callback = (ec != null && !ec.IsDefault) ? new IOCompletionCallbackHelper(iocb, ec) : (object)iocb;
            }
            else
            {
                _callback = null;
            }
            return AllocateNativeOverlapped(userData);
        }

        [Obsolete("This overload is not safe and has been deprecated. Use UnsafePack(IOCompletionCallback?, object?) instead.")]
        [CLSCompliant(false)]
        public NativeOverlapped* UnsafePack(IOCompletionCallback? iocb)
            => UnsafePack(iocb, null);

        [CLSCompliant(false)]
        public NativeOverlapped* UnsafePack(IOCompletionCallback? iocb, object? userData)
        {
            if (_pNativeOverlapped != null)
            {
                throw new InvalidOperationException(SR.InvalidOperation_Overlapped_Pack);
            }
            _callback = iocb;
            return AllocateNativeOverlapped(userData);
        }

        /*====================================================================
        *  Unpacks an unmanaged native Overlapped struct.
        *  Unpins the native Overlapped struct
        ====================================================================*/
        [CLSCompliant(false)]
        public static Overlapped Unpack(NativeOverlapped* nativeOverlappedPtr)
        {
            ArgumentNullException.ThrowIfNull(nativeOverlappedPtr);

            return GetOverlappedFromNative(nativeOverlappedPtr);
        }

        [CLSCompliant(false)]
        public static void Free(NativeOverlapped* nativeOverlappedPtr)
        {
            ArgumentNullException.ThrowIfNull(nativeOverlappedPtr);

            GetOverlappedFromNative(nativeOverlappedPtr)._pNativeOverlapped = null;
            FreeNativeOverlapped(nativeOverlappedPtr);
        }

        private NativeOverlapped* AllocateNativeOverlapped(object? userData)
        {
            NativeOverlapped* pNativeOverlapped = null;
            try
            {
                nuint handleCount = 1;

                if (userData != null)
                {
                    if (userData.GetType() == typeof(object[]))
                    {
                        handleCount += (nuint)((object[])userData).Length;
                    }
                    else
                    {
                        handleCount++;
                    }
                }

                pNativeOverlapped = (NativeOverlapped*)NativeMemory.Alloc(
                    (nuint)(sizeof(NativeOverlapped) + sizeof(nuint)) + handleCount * (nuint)sizeof(GCHandle));

                GCHandleCountRef(pNativeOverlapped) = 0;

                pNativeOverlapped->InternalLow = default;
                pNativeOverlapped->InternalHigh = default;
                pNativeOverlapped->OffsetLow = _offsetLow;
                pNativeOverlapped->OffsetHigh = _offsetHigh;
                pNativeOverlapped->EventHandle = _eventHandle;

                GCHandleRef(pNativeOverlapped, 0) = GCHandle<Overlapped>.ToIntPtr(new GCHandle<Overlapped>(this));
                GCHandleCountRef(pNativeOverlapped)++;

                if (userData != null)
                {
                    if (userData.GetType() == typeof(object[]))
                    {
                        object[] objArray = (object[])userData;
                        for (int i = 0; i < objArray.Length; i++)
                        {
                            GCHandleRef(pNativeOverlapped, (nuint)(i + 1)) = PinnedGCHandle<object>.ToIntPtr(new PinnedGCHandle<object>(objArray[i]));
                            GCHandleCountRef(pNativeOverlapped)++;
                        }
                    }
                    else
                    {
                        GCHandleRef(pNativeOverlapped, 1) = PinnedGCHandle<object>.ToIntPtr(new PinnedGCHandle<object>(userData));
                        GCHandleCountRef(pNativeOverlapped)++;
                    }
                }

                Debug.Assert(GCHandleCountRef(pNativeOverlapped) == handleCount);

                // Tracing needs _pNativeOverlapped to be initialized
                _pNativeOverlapped = pNativeOverlapped;

#if FEATURE_PERFTRACING
                if (NativeRuntimeEventSource.Log.IsEnabled())
                    NativeRuntimeEventSource.Log.ThreadPoolIOPack(pNativeOverlapped);
#endif

                NativeOverlapped* pRet = pNativeOverlapped;
                pNativeOverlapped = null;
                return pRet;
            }
            finally
            {
                if (pNativeOverlapped != null)
                {
                    _pNativeOverlapped = null;
                    FreeNativeOverlapped(pNativeOverlapped);
                }
            }
        }

        internal static void FreeNativeOverlapped(NativeOverlapped* pNativeOverlapped)
        {
            nuint handleCount = GCHandleCountRef(pNativeOverlapped);

            // Review -- this is assuming that GCHandle<T> and PinnedGCHandle<T> are freed in the same way.
            for (nuint i = 0; i < handleCount; i++)
                GCHandle<object>.FromIntPtr(GCHandleRef(pNativeOverlapped, i)).Dispose();

            NativeMemory.Free(pNativeOverlapped);
        }

        //
        // The NativeOverlapped structure is followed by GC handle count and inline array of GC handles
        //
        private static ref nuint GCHandleCountRef(NativeOverlapped* pNativeOverlapped)
            => ref *(nuint*)(pNativeOverlapped + 1);

        private static ref IntPtr GCHandleRef(NativeOverlapped* pNativeOverlapped, nuint index)
            => ref *((IntPtr*)((nuint*)(pNativeOverlapped + 1) + 1) + index);

        internal static Overlapped GetOverlappedFromNative(NativeOverlapped* pNativeOverlapped)
        {
            IntPtr handle = GCHandleRef(pNativeOverlapped, 0);

            Debug.Assert(GCHandle<object>.FromIntPtr(handle).Target is Overlapped);
            Overlapped overlapped = GCHandle<Overlapped>.FromIntPtr(handle).Target;

            Debug.Assert(overlapped._pNativeOverlapped == pNativeOverlapped);

            return overlapped;
        }
    }
}
