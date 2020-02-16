using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace System
{
    internal static class PercentEncodingHelper
    {
        /*
        public static unsafe int UnescapePercentEncodedUTF8Sequence2(ReadOnlySpan<char> input, ref ValueStringBuilder dest, bool isQuery, bool iriParsing)
        {
            // As an optimization, this method should only be called after the first character is known to be a part of a non-ascii UTF8 sequence
            Debug.Assert(input.Length >= 3);
            Debug.Assert(input[0] == '%');
            Debug.Assert(UriHelper.EscapedAscii(input[1], input[2]) != Uri.c_DummyChar);
            Debug.Assert(UriHelper.EscapedAscii(input[1], input[2]) >= 128);

            // Will only be used if the input contains a very long contiguous percent-encoded utf8 sequence
            byte[]? byteArrayToReturnToPool = null;

            Span<byte> bytes = stackalloc byte[256];
            int byteCount = 0; // same as i / 3

            int i = 0;
            do
            {
                if ((uint)(i + 2) >= input.Length || input[i] != '%')
                    break;

                char c = UriHelper.EscapedAscii(input[i + 1], input[i + 2]);

                if (c <= 127 || c == Uri.c_DummyChar)
                    break;

                if ((uint)byteCount < (uint)bytes.Length)
                {
                    bytes[byteCount++] = (byte)c;
                }
                else
                {
                    Debug.Assert(byteArrayToReturnToPool is null);
                    Debug.Assert(input.Length / 3 + 1 > 256);

                    byteArrayToReturnToPool = ArrayPool<byte>.Shared.Rent(input.Length / 3 + 1);
                    bytes.CopyTo(byteArrayToReturnToPool);
                    bytes = byteArrayToReturnToPool;

                    bytes[byteCount++] = (byte)c;
                }

                i += 3;
            }
            while (true);

            Debug.Assert(i >= 3);

            bytes = bytes.Slice(0, byteCount);

            char[]? charArrayToReturnToPool = byteCount > 256 ? ArrayPool<char>.Shared.Rent(byteCount) : null;
            Span<char> chars = charArrayToReturnToPool ?? stackalloc char[256];

            int charsWritten = UriHelper.s_noFallbackCharUTF8.GetChars(bytes, chars);
            chars = chars.Slice(charsWritten);



            if (byteArrayToReturnToPool != null)
            {
                ArrayPool<byte>.Shared.Return(byteArrayToReturnToPool);
            }
            if (charArrayToReturnToPool != null)
            {
                ArrayPool<char>.Shared.Return(charArrayToReturnToPool);
            }

            // return i;

            if (iriParsing)
            {
                i = 0;
                while (true)
                {
                    if ((uint)(i + 5) >= input.Length || input[i] != '%' || input[i + 3] != '%')
                        return i;

                    char c = UriHelper.EscapedAscii(input[i + 1], input[i + 2]);

                    if (c <= 127 || c == Uri.c_DummyChar)
                        return i;

                    char c2 = UriHelper.EscapedAscii(input[i + 4], input[i + 5]);

                    if (c2 <= 127 || c2 == Uri.c_DummyChar)
                        return i;

                    if (IriHelper.CheckIriUnicodeRange(c, isQuery))
                    {
                        Debug.Assert(!char.IsSurrogate(c));
                        dest.Append(c);
                        i += 3;
                    }
                    else
                    {
                        char lowSurrogate;

                        if (char.IsHighSurrogate(c)
                          && (uint)(i + 5) < input.Length
                          && input[i + 3] == '%'
                          && Rune.TryCreate(c, lowSurrogate = UriHelper.EscapedAscii(input[i + 4], input[i + 5]), out Rune rune)
                          && IriHelper.CheckIriUnicodeRange(rune, isQuery))
                        {
                            dest.Append(c);
                            dest.Append(lowSurrogate);
                            i += 6;
                        }
                        else
                        {
                            dest.Append('%');
                            dest.Append(input[i + 1]);
                            dest.Append(input[i + 2]);
                            i += 3;
                        }
                    }
                }
            }
            else
            {
                // Copy the input for as long as it contains valid non-ascii percent encoded bytes

                dest.Append(input.Slice(0, i));
                return i;
            }
        }
        */

        public static unsafe int UnescapePercentEncodedUTF8Sequence(ReadOnlySpan<char> input, ref ValueStringBuilder dest, bool isQuery, bool iriParsing)
        {
            // As an optimization, this method should only be called after the first character is known to be a part of a non-ascii UTF8 sequence
            Debug.Assert(input.Length >= 3);
            Debug.Assert(input[0] == '%');
            Debug.Assert(UriHelper.EscapedAscii(input[1], input[2]) != Uri.c_DummyChar);
            Debug.Assert(UriHelper.EscapedAscii(input[1], input[2]) >= 128);

            int i = 0;

            do
            {
                if ((uint)(i + 2) >= input.Length || input[i] != '%')
                    break;

                uint value = EscapedAscii_ZeroFallback(input[i + 1], input[i + 2]);

                if (value <= 127)
                    break;

                int length = BitOperations.LeadingZeroCount(~(value << 24));

                if (length - 2 > 2 || (uint)(i + 5) >= input.Length || input[i + 3] != '%')
                    goto CopyThreeCharsAndReturn;

                uint next = EscapedAscii_ZeroFallback(input[i + 4], input[i + 5]);

                if (next <= 127)
                    goto CopyThreeCharsAndReturn;

                value = value << 8 | next;

                if (Utf8Helpers.IsTwoByteSequence(value))
                {
                    uint character = Utf8Helpers.ExtractCharFromTwoBytes(value);

                    if (!iriParsing || IriHelper.CheckIriUnicodeRange((char)character, isQuery))
                    {
                        dest.Append((char)character);
                        i += 6;
                        continue;
                    }
                    else
                    {
                        dest.Append('%');
                        dest.Append(input[i + 1]);
                        dest.Append(input[i + 2]);
                        dest.Append('%');
                        dest.Append(input[i + 4]);
                        dest.Append(input[i + 5]);
                        i += 6;
                        continue;
                    }
                }
                else if (length == 2)
                {
                    dest.Append('%');
                    dest.Append(input[i + 1]);
                    dest.Append(input[i + 2]);
                    i += 3;
                    continue;
                }
                else
                {
                    if ((uint)(i + 8) >= input.Length || input[i + 6] != '%')
                        goto CopySixCharsAndReturn;

                    next = EscapedAscii_ZeroFallback(input[i + 7], input[i + 8]);

                    if (next <= 127)
                        goto CopySixCharsAndReturn;

                    value = value << 8 | next;

                    if (Utf8Helpers.IsThreeByteSequence(value))
                    {
                        uint character = Utf8Helpers.ExtractCharFromThreeBytes(value);

                        if (!iriParsing || IriHelper.CheckIriUnicodeRange((char)character, isQuery))
                        {
                            dest.Append((char)character);
                            i += 9;
                            continue;
                        }
                        else
                        {
                            dest.Append('%');
                            dest.Append(input[i + 1]);
                            dest.Append(input[i + 2]);
                            dest.Append('%');
                            dest.Append(input[i + 4]);
                            dest.Append(input[i + 5]);
                            dest.Append('%');
                            dest.Append(input[i + 7]);
                            dest.Append(input[i + 8]);
                            i += 9;
                            continue;
                        }
                    }
                    else if (length != 4)
                    {
                        dest.Append('%');
                        dest.Append(input[i + 1]);
                        dest.Append(input[i + 2]);
                        dest.Append('%');
                        dest.Append(input[i + 4]);
                        dest.Append(input[i + 5]);
                        i += 6;
                        continue;
                    }
                    else
                    {
                        if ((uint)(i + 11) >= input.Length || input[i + 9] != '%')
                            goto CopyNineCharsAndReturn;

                        next = EscapedAscii_ZeroFallback(input[i + 10], input[i + 11]);

                        if (next <= 127)
                            goto CopyNineCharsAndReturn;

                        value = value << 8 | next;

                        if (Utf8Helpers.IsFourByteSequence(value))
                        {
                            uint character = Utf8Helpers.ExtractCharFromFourBytes(value);

                            if (!iriParsing || IriHelper.CheckIriUnicodeRange(value, isQuery))
                            {
                                dest.Append((char)((character + ((0xD800u - 0x40u) << 10)) >> 10));
                                dest.Append((char)((character & 0x3FFu) + 0xDC00u));
                                i += 12;
                                continue;
                            }
                        }

                        dest.Append('%');
                        dest.Append(input[i + 1]);
                        dest.Append(input[i + 2]);
                        dest.Append('%');
                        dest.Append(input[i + 4]);
                        dest.Append(input[i + 5]);
                        dest.Append('%');
                        dest.Append(input[i + 7]);
                        dest.Append(input[i + 8]);
                        dest.Append('%');
                        dest.Append(input[i + 10]);
                        dest.Append(input[i + 11]);
                        i += 12;
                    }
                }

            CopyNineCharsAndReturn:
                dest.Append('%');
                dest.Append(input[i + 1]);
                dest.Append(input[i + 2]);
                i += 3;

            CopySixCharsAndReturn:
                dest.Append('%');
                dest.Append(input[i + 1]);
                dest.Append(input[i + 2]);
                i += 3;

            CopyThreeCharsAndReturn:
                dest.Append('%');
                dest.Append(input[i + 1]);
                dest.Append(input[i + 2]);
                i += 3;
                break;
            }
            while (true);

            return i;
        }

        private static char EscapedAscii_ZeroFallback(char digit, char next)
        {
            if (!(((digit >= '0') && (digit <= '9'))
                || ((digit >= 'A') && (digit <= 'F'))
                || ((digit >= 'a') && (digit <= 'f'))))
            {
                return (char)0;
            }

            int res = (digit <= '9')
                ? ((int)digit - (int)'0')
                : (((digit <= 'F')
                ? ((int)digit - (int)'A')
                : ((int)digit - (int)'a'))
                   + 10);

            if (!(((next >= '0') && (next <= '9'))
                || ((next >= 'A') && (next <= 'F'))
                || ((next >= 'a') && (next <= 'f'))))
            {
                return (char)0;
            }

            return (char)((res << 4) + ((next <= '9')
                    ? ((int)next - (int)'0')
                    : (((next <= 'F')
                        ? ((int)next - (int)'A')
                        : ((int)next - (int)'a'))
                       + 10)));
        }

        private static class Utf8Helpers
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsTwoByteSequence(uint value)
            {
                return (value & 0b11100000_11000000) == 0b11000000_10000000;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static uint ExtractCharFromTwoBytes(uint value)
            {
                return ((value & 0x1F00) >> 2) | (value & 0x3F);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsThreeByteSequence(uint value)
            {
                return (value & 0b11110000_11000000_11000000) == 0b11100000_10000000_10000000;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static uint ExtractCharFromThreeBytes(uint value)
            {
                return ((value & 0x0F0000) >> 4) | ((value & 0x3F00) >> 2) | (value & 0x3F);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool IsFourByteSequence(uint value)
            {
                return (value & 0b11111000_11000000_11000000_11000000) == 0b11110000_10000000_10000000_10000000;
            }

            public static uint ExtractCharFromFourBytes(uint value)
            {
                return ((value & 0x0F000000) >> 6) | ((value & 0x3F0000) >> 4) | ((value & 0x3F00) >> 2) | (value & 0x3F);
            }
        }
    }
}
