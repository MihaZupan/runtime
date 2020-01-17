using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System
{
    internal static class CharHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiDigit(char c) =>
            (uint)(c - '0') <= ('9' - '0');

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiLowercaseLetter(char c) =>
            (uint)(c - 'a') <= ('z' - 'a');

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiUppercaseLetter(char c) =>
            (uint)(c - 'A') <= ('Z' - 'A');

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiLetter(char c) =>
            (uint)((c - 'A') & ~0x20) <= ('Z' - 'A');

        public static bool IsAsciiLetterOrDigit(char c) =>
            ((uint)((c - 'A') & ~0x20) <= ('Z' - 'A')) ||
            ((uint)(c - '0') <= ('9' - '0'));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiOctalDigit(char c) =>
            (uint)(c - '0') <= ('7' - '0');

        public static bool IsHexDigit(char c) =>
            ((uint)(c - '0') <= ('9' - '0')) ||
            ((uint)((c - 'A') & ~0x20) <= ('F' - 'A'));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHexDigitLetter(char c) =>
            (uint)((c - 'A') & ~0x20) <= ('F' - 'A');

        public static int HexToInt(char c)
        {
            Debug.Assert(IsHexDigit(c));

            if (c <= '9')
                return c - '0';

            return (c | 0x20) - 'a' + 10;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsInInclusiveRange(char c, char min, char max)
            => (uint)(c - min) <= (uint)(max - min);
    }
}
