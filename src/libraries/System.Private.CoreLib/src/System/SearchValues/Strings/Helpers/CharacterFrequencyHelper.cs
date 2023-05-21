// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Buffers
{
    internal static class CharacterFrequencyHelper
    {
        public static int IndexOfAsciiCharWithLowestFrequency(ReadOnlySpan<char> span, bool ignoreCase, int excludeIndex = -1)
        {
            float minFrequency = float.MaxValue;
            int minIndex = -1;

            for (int i = 0; i < span.Length; i++)
            {
                if (i == excludeIndex)
                {
                    continue;
                }

                char c = span[i];

                if (char.IsAscii(c))
                {
                    float frequency = AsciiFrequency[c];

                    if (ignoreCase)
                    {
                        // Include the alternative character that will also match.
                        frequency += AsciiFrequency[c ^ 0x20];
                    }

                    if (frequency < minFrequency)
                    {
                        minFrequency = frequency;
                        minIndex = i;
                    }
                }
            }

            return minIndex;
        }

        // Same as RegexPrefixAnalyzer.Frequency.
        public static ReadOnlySpan<float> AsciiFrequency => new float[]
        {
            0.000f /* '\x00' */, 0.000f /* '\x01' */, 0.000f /* '\x02' */, 0.000f /* '\x03' */, 0.000f /* '\x04' */, 0.000f /* '\x05' */, 0.000f /* '\x06' */, 0.000f /* '\x07' */,
            0.000f /* '\x08' */, 0.001f /* '\x09' */, 0.000f /* '\x0A' */, 0.000f /* '\x0B' */, 0.000f /* '\x0C' */, 0.000f /* '\x0D' */, 0.000f /* '\x0E' */, 0.000f /* '\x0F' */,
            0.000f /* '\x10' */, 0.000f /* '\x11' */, 0.000f /* '\x12' */, 0.000f /* '\x13' */, 0.003f /* '\x14' */, 0.000f /* '\x15' */, 0.000f /* '\x16' */, 0.000f /* '\x17' */,
            0.000f /* '\x18' */, 0.004f /* '\x19' */, 0.000f /* '\x1A' */, 0.000f /* '\x1B' */, 0.006f /* '\x1C' */, 0.006f /* '\x1D' */, 0.000f /* '\x1E' */, 0.000f /* '\x1F' */,
            8.952f /* '    ' */, 0.065f /* '   !' */, 0.420f /* '   "' */, 0.010f /* '   #' */, 0.011f /* '   $' */, 0.005f /* '   %' */, 0.070f /* '   &' */, 0.050f /* '   '' */,
            3.911f /* '   (' */, 3.910f /* '   )' */, 0.356f /* '   *' */, 2.775f /* '   +' */, 1.411f /* '   ,' */, 0.173f /* '   -' */, 2.054f /* '   .' */, 0.677f /* '   /' */,
            1.199f /* '   0' */, 0.870f /* '   1' */, 0.729f /* '   2' */, 0.491f /* '   3' */, 0.335f /* '   4' */, 0.269f /* '   5' */, 0.435f /* '   6' */, 0.240f /* '   7' */,
            0.234f /* '   8' */, 0.196f /* '   9' */, 0.144f /* '   :' */, 0.983f /* '   ;' */, 0.357f /* '   <' */, 0.661f /* '   =' */, 0.371f /* '   >' */, 0.088f /* '   ?' */,
            0.007f /* '   @' */, 0.763f /* '   A' */, 0.229f /* '   B' */, 0.551f /* '   C' */, 0.306f /* '   D' */, 0.449f /* '   E' */, 0.337f /* '   F' */, 0.162f /* '   G' */,
            0.131f /* '   H' */, 0.489f /* '   I' */, 0.031f /* '   J' */, 0.035f /* '   K' */, 0.301f /* '   L' */, 0.205f /* '   M' */, 0.253f /* '   N' */, 0.228f /* '   O' */,
            0.288f /* '   P' */, 0.034f /* '   Q' */, 0.380f /* '   R' */, 0.730f /* '   S' */, 0.675f /* '   T' */, 0.265f /* '   U' */, 0.309f /* '   V' */, 0.137f /* '   W' */,
            0.084f /* '   X' */, 0.023f /* '   Y' */, 0.023f /* '   Z' */, 0.591f /* '   [' */, 0.085f /* '   \' */, 0.590f /* '   ]' */, 0.013f /* '   ^' */, 0.797f /* '   _' */,
            0.001f /* '   `' */, 4.596f /* '   a' */, 1.296f /* '   b' */, 2.081f /* '   c' */, 2.005f /* '   d' */, 6.903f /* '   e' */, 1.494f /* '   f' */, 1.019f /* '   g' */,
            1.024f /* '   h' */, 3.750f /* '   i' */, 0.286f /* '   j' */, 0.439f /* '   k' */, 2.913f /* '   l' */, 1.459f /* '   m' */, 3.908f /* '   n' */, 3.230f /* '   o' */,
            1.444f /* '   p' */, 0.231f /* '   q' */, 4.220f /* '   r' */, 3.924f /* '   s' */, 5.312f /* '   t' */, 2.112f /* '   u' */, 0.737f /* '   v' */, 0.573f /* '   w' */,
            0.992f /* '   x' */, 1.067f /* '   y' */, 0.181f /* '   z' */, 0.391f /* '   {' */, 0.056f /* '   |' */, 0.391f /* '   }' */, 0.002f /* '   ~' */, 0.000f /* '\x7F' */,
        };

    }
}
