// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace System
{
    internal static class IriHelper
    {
        //
        // Checks if provided non surrogate char lies in iri range
        //
        internal static bool CheckIriUnicodeRange(char unicode, bool isQuery)
        {
            return ((unicode >= '\u00A0' && unicode <= '\uD7FF') ||
               (unicode >= '\uF900' && unicode <= '\uFDCF') ||
               (unicode >= '\uFDF0' && unicode <= '\uFFEF') ||
               (isQuery && unicode >= '\uE000' && unicode <= '\uF8FF'));
        }

        //
        // Check if highSurr and lowSurr are a surrogate pair then
        // it checks if the combined char is in the range
        // Takes in isQuery because iri restrictions for query are different
        //
        internal static bool CheckIriUnicodeRange(char highSurr, char lowSurr, ref bool surrogatePair, bool isQuery)
        {
            bool inRange = false;
            surrogatePair = false;

            Debug.Assert(char.IsHighSurrogate(highSurr));

            if (char.IsSurrogatePair(highSurr, lowSurr))
            {
                surrogatePair = true;
                ReadOnlySpan<char> chars = stackalloc char[2] { highSurr, lowSurr };
                string surrPair = new string(chars);
                if (((string.CompareOrdinal(surrPair, "\U00010000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U0001FFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U00020000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U0002FFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U00030000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U0003FFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U00040000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U0004FFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U00050000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U0005FFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U00060000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U0006FFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U00070000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U0007FFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U00080000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U0008FFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U00090000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U0009FFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U000A0000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U000AFFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U000B0000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U000BFFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U000C0000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U000CFFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U000D0000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U000DFFFD") <= 0)) ||
                    ((string.CompareOrdinal(surrPair, "\U000E1000") >= 0)
                        && (string.CompareOrdinal(surrPair, "\U000EFFFD") <= 0)) ||
                    (isQuery &&
                        (((string.CompareOrdinal(surrPair, "\U000F0000") >= 0)
                            && (string.CompareOrdinal(surrPair, "\U000FFFFD") <= 0)) ||
                            ((string.CompareOrdinal(surrPair, "\U00100000") >= 0)
                            && (string.CompareOrdinal(surrPair, "\U0010FFFD") <= 0)))))
                {
                    inRange = true;
                }
            }

            return inRange;
        }

        internal static bool CheckIriUnicodeRange(uint value, bool isQuery)
        {
            Debug.Assert(value >= 0xFFFF);

            return ((value & 0xFFFF) < 0xFFFE)
                    && (value - 0xE0000 >= (0xE1000 - 0xE0000))
                    && (isQuery || value < 0xF0000);
        }

        //
        // Check reserved chars according to RFC 3987 in a specific component
        //
        internal static bool CheckIsReserved(char ch, UriComponents component)
        {
            if ((component != UriComponents.Scheme) &&
                    (component != UriComponents.UserInfo) &&
                    (component != UriComponents.Host) &&
                    (component != UriComponents.Port) &&
                    (component != UriComponents.Path) &&
                    (component != UriComponents.Query) &&
                    (component != UriComponents.Fragment)
                )
            {
                return (component == (UriComponents)0) ? UriHelper.IsGenDelim(ch) : false;
            }

            return UriHelper.RFC3986ReservedMarks.IndexOf(ch) >= 0;
        }

        //
        // IRI normalization for strings containing characters that are not allowed or
        // escaped characters that should be unescaped in the context of the specified Uri component.
        //
        internal static unsafe string EscapeUnescapeIri(char* pInput, int start, int end, UriComponents component)
        {
            int size = end - start;
            ValueStringBuilder dest = size < 256
                ? new ValueStringBuilder(stackalloc char[256])
                : new ValueStringBuilder(size);

            for (int i = start; i < end; ++i)
            {
                char ch = pInput[i];
                if (ch == '%')
                {
                    if (i + 2 < end)
                    {
                        ch = UriHelper.EscapedAscii(pInput[i + 1], pInput[i + 2]);

                        // Do not unescape a reserved char
                        if (ch == Uri.c_DummyChar || ch == '%' || CheckIsReserved(ch, component) || UriHelper.IsNotSafeForUnescape(ch))
                        {
                            // keep as is
                            dest.Append(pInput[i++]);
                            dest.Append(pInput[i++]);
                            dest.Append(pInput[i]);
                            continue;
                        }
                        else if (ch <= '\x7F')
                        {
                            Debug.Assert(ch < 0xFF, "Expecting ASCII character.");
                            //ASCII
                            dest.Append(ch);
                            i += 2;
                            continue;
                        }
                        else
                        {
                            // possibly utf8 encoded sequence of unicode

                            int charactersRead = PercentEncodingHelper.UnescapePercentEncodedUTF8Sequence(
                                new ReadOnlySpan<char>(pInput + i, end - i),
                                ref dest,
                                component == UriComponents.Query,
                                iriParsing: true);

                            Debug.Assert(charactersRead > 0);

                            i += charactersRead - 1; // -1 as i will be incremented in the loop
                        }
                    }
                    else
                    {
                        dest.Append(pInput[i]);
                    }
                }
                else if (ch > '\x7f')
                {
                    // unicode

                    bool escape;
                    bool surrogatePair = false;

                    char ch2 = '\0';

                    if ((char.IsHighSurrogate(ch)) && (i + 1 < end))
                    {
                        ch2 = pInput[i + 1];
                        escape = !CheckIriUnicodeRange(ch, ch2, ref surrogatePair, component == UriComponents.Query);
                    }
                    else
                    {
                        escape = !CheckIriUnicodeRange(ch, component == UriComponents.Query);
                    }

                    if (escape)
                    {
                        Span<byte> encodedBytes = stackalloc byte[4];

                        Rune rune;
                        if (surrogatePair)
                        {
                            rune = new Rune(ch, ch2);
                        }
                        else if (!Rune.TryCreate(ch, out rune))
                        {
                            rune = Rune.ReplacementChar;
                        }

                        int bytesWritten = rune.EncodeToUtf8(encodedBytes);
                        encodedBytes = encodedBytes.Slice(0, bytesWritten);

                        foreach (byte b in encodedBytes)
                        {
                            UriHelper.EscapeAsciiChar(b, ref dest);
                        }
                    }
                    else
                    {
                        dest.Append(ch);
                        if (surrogatePair)
                        {
                            dest.Append(ch2);
                        }
                    }

                    if (surrogatePair)
                    {
                        i++;
                    }
                }
                else
                {
                    // just copy the character
                    dest.Append(pInput[i]);
                }
            }

            return dest.ToString();
        }
    }
}
