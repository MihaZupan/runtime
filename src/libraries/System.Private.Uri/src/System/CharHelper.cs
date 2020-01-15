using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System
{
    internal static class CharHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiDigit(char c) =>
            (uint)(c - '0') < 10;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiLowercaseLetter(char c) =>
            (uint)(c - 'a') < 26;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiUppercaseLetter(char c) =>
            (uint)(c - 'A') < 26;

        public static bool IsAsciiLetter(char c) =>
            (((uint)c - 'A') & ~0x20) < 26;

        public static bool IsAsciiLetterOrDigit(char c) =>
            ((((uint)c - 'A') & ~0x20) < 26) ||
            (((uint)c - '0') < 10);

        public static bool IsHexDigit(char c) =>
            (((uint)c - '0') < 10) ||
            ((((uint)c - 'A') & ~0x20) < 6);

        public static int HexToInt(char c)
        {
            Debug.Assert(IsHexDigit(c));

            return c <= '9'
                ? c - '0'
                : (c | 0x20) - 'a' + 10;
        }
    }
}
