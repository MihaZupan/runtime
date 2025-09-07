// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Text
{
    /// <summary>Provides downlevel polyfills for Ascii helper APIs.</summary>
    internal static class Ascii
    {
        public static bool IsValid(string value)
        {
            return IsValid(value.AsSpan());
        }

        public static bool IsValid(ReadOnlySpan<char> value)
        {
            foreach (char c in value)
            {
                if (c > 127)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
