// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Text
{
    internal static class Ascii
    {
        public static bool IsValid(ReadOnlySpan<char> span)
        {
            foreach (char c in span)
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
