// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Text
{
    internal static class EncodingExtensions
    {
        private static readonly Encoding s_latin1 = Encoding.GetEncoding(28591);

        extension(Encoding encoding)
        {
            public static Encoding Latin1 => s_latin1;

            public unsafe string GetString(ReadOnlySpan<byte> bytes)
            {
                fixed (byte* pBytes = bytes)
                {
                    return encoding.GetString(pBytes, bytes.Length);
                }
            }

            public unsafe int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
            {
                fixed (char* pChars = chars)
                fixed (byte* pBytes = bytes)
                {
                    return encoding.GetBytes(pChars, chars.Length, pBytes, bytes.Length);
                }
            }
        }
    }
}
