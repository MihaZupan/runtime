// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace System.Text
{
    internal static class Ascii
    {
        public static unsafe bool IsValid(ReadOnlySpan<char> span)
        {
            fixed (char* src = span)
            {
                uint* ptrUInt32 = (uint*)src;
                int length = span.Length;

                while (length >= 4)
                {
                    if (!AllCharsInUInt32AreAscii(ptrUInt32[0] | ptrUInt32[1]))
                    {
                        return false;
                    }

                    ptrUInt32 += 2;
                    length -= 4;
                }

                char* ptrChar = (char*)ptrUInt32;
                while (length-- > 0)
                {
                    char ch = *ptrChar++;
                    if (ch >= 0x80)
                    {
                        return false;
                    }
                }
            }

            return true;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static bool AllCharsInUInt32AreAscii(uint value) => (value & ~0x007F_007Fu) == 0;
        }
    }
}
