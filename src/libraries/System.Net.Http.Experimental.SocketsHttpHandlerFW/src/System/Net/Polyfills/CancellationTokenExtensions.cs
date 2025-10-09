// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading
{
    internal static class CancellationTokenExtensions
    {
        public static CancellationTokenRegistration UnsafeRegister(this CancellationToken cancellationToken, Action<object?> callback, object? state)
        {
            return cancellationToken.Register(callback, state, useSynchronizationContext: false);
        }

        public static CancellationTokenRegistration UnsafeRegister(this CancellationToken cancellationToken, Action<object?, CancellationToken> callback, object? state)
        {
            return cancellationToken.Register(s => callback(s, CancellationToken.None), state, useSynchronizationContext: false);
        }
    }
}
