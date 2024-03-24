// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Win32.SafeHandles;

namespace System.Net
{
#if DEBUG
    //
    // This is a helper class for debugging GC-ed handles that we define.
    // As a general rule normal code path should always destroy handles explicitly
    //
    internal abstract class DebugSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        protected DebugSafeHandle(bool ownsHandle) : base(ownsHandle)
        {
        }

        protected DebugSafeHandle(IntPtr invalidValue, bool ownsHandle) : base(ownsHandle)
        {
            SetHandle(invalidValue);
        }
    }
#endif // DEBUG
}
