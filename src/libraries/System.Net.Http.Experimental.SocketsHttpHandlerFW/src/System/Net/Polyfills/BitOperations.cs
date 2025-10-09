// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Numerics
{
    internal static class BitOperations
    {
        public static int LeadingZeroCount(uint value)
        {
            value |= value >> 01;
            value |= value >> 02;
            value |= value >> 04;
            value |= value >> 08;
            value |= value >> 16;

            return 31 ^ Log2DeBruijn[(int)((value * 0x07C4ACDDu) >> 27)];
        }

        private static readonly byte[] Log2DeBruijn = new byte[] { // 32
            00, 09, 01, 10, 13, 21, 02, 29,
            11, 14, 16, 18, 22, 25, 03, 30,
            08, 12, 20, 28, 15, 17, 24, 07,
            19, 27, 23, 06, 26, 05, 04, 31
        };
    }
}
