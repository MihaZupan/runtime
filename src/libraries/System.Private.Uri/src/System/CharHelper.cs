using System.Runtime.CompilerServices;

namespace System
{
    internal static class CharHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiDigit(char c) =>
            (uint)(c - '0') <= ('9' - '0');

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiLetter(char c) =>
            (uint)((c - 'A') & ~0x20) <= ('Z' - 'A');

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAsciiUppercaseLetter(char c) =>
            (uint)(c - 'A') <= ('Z' - 'A');
    }
}
