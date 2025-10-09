// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;

namespace System.Text
{
    internal static class Ascii
    {
        public static bool IsValid(this string value)
        {
            return IsValid(value.AsSpan());
        }

        public static bool IsValid(this ReadOnlySpan<char> value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 127)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsValid(this ReadOnlySpan<byte> value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 127)
                {
                    return false;
                }
            }

            return true;
        }

        public static OperationStatus ToLower(ReadOnlySpan<char> source, Span<byte> destination, out int bytesWritten)
        {
            if (destination.Length < source.Length)
            {
                bytesWritten = 0;
                return OperationStatus.DestinationTooSmall;
            }

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];

                if (c > 127)
                {
                    bytesWritten = i;
                    return OperationStatus.InvalidData;
                }

                if (c is >= 'A' and <= 'Z')
                {
                    c = (char)(c | 0x20);
                }

                destination[i] = (byte)c;
            }

            bytesWritten = source.Length;
            return OperationStatus.Done;
        }

        public static unsafe OperationStatus FromUtf16(ReadOnlySpan<char> source, Span<byte> destination, out int bytesWritten)
        {
            if (destination.Length < source.Length)
            {
                bytesWritten = 0;
                return OperationStatus.DestinationTooSmall;
            }

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];

                if (c > 127)
                {
                    bytesWritten = i;
                    return OperationStatus.InvalidData;
                }

                destination[i] = (byte)c;
            }

            bytesWritten = source.Length;
            return OperationStatus.Done;
        }

        public static bool Equals(ReadOnlySpan<byte> left, string right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }
            return true;
        }

        public static bool EqualsIgnoreCase(ReadOnlySpan<byte> left, string right)
        {
            Debug.Assert(IsValid(right));

            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                byte b = left[i];
                char c = right[i];

                if (b == c)
                {
                    continue;
                }

                b |= 0x20;

                if (!(b >= 'a' && b <= 'z' && b == (c | 0x20)))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
