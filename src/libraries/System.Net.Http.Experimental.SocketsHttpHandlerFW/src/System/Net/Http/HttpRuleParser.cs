// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace System.Net.Http
{
    internal static class HttpRuleParser
    {
        private static readonly bool[] s_tokenBytes = new bool[256];

        static HttpRuleParser()
        {
            foreach (char c in "!#$%&'*+-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ^_`abcdefghijklmnopqrstuvwxyz|~")
            {
                s_tokenBytes[c] = true;
            }
        }

        internal static readonly Encoding DefaultHttpEncoding = Encoding.GetEncoding(28591);

        internal static bool IsToken(ReadOnlySpan<char> input)
        {
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c >= 127 || !s_tokenBytes[c])
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsToken(ReadOnlySpan<byte> input)
        {
            for (int i = 0; i < input.Length; i++)
            {
                if (!s_tokenBytes[input[i]])
                {
                    return false;
                }
            }

            return true;
        }

        internal static string GetTokenString(ReadOnlySpan<byte> input)
        {
            Debug.Assert(IsToken(input));

            return Encoding.ASCII.GetString(input);
        }
    }
}
