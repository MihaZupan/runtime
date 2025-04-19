// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System
{
    internal static class MemoryExtensionsPolyfills
    {
        public static bool Contains<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T> =>
            span.IndexOf(value) >= 0;

        public static bool ContainsAnyExcept(this ReadOnlySpan<char> span, char value)
        {
            foreach (char c in span)
            {
                if (c != value)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
