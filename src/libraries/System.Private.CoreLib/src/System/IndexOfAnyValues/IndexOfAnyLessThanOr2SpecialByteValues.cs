// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace System.Buffers
{
    internal sealed class IndexOfAnyLessThanOr2SpecialByteValues : IndexOfAnyValues<byte>
    {
        private readonly byte _lessThan;
        private readonly byte _value0;
        private readonly byte _value1;

        public IndexOfAnyLessThanOr2SpecialByteValues(byte lessThan, byte value0, byte value1) =>
            (_lessThan, _value0, _value1) = (lessThan, value0, value1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(byte value) =>
            value < _lessThan ||
            value == _value0 ||
            value == _value1;

        internal override byte[] GetValues()
        {
            List<byte> values = new();

            for (int i = 0; i < _lessThan; i++)
            {
                values.Add((byte)i);
            }

            if (_value1 >= _lessThan)
            {
                values.Add(_value1);
            }

            if (_value0 >= _lessThan)
            {
                values.Add(_value0);
            }

            return values.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAny(ReadOnlySpan<byte> span) =>
            IndexOfAny<SpanHelpers.DontNegate<byte>>(ref MemoryMarshal.GetReference(span), span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyExcept(ReadOnlySpan<byte> span) =>
            IndexOfAny<SpanHelpers.Negate<byte>>(ref MemoryMarshal.GetReference(span), span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAny(ReadOnlySpan<byte> span) =>
            LastIndexOfAny<SpanHelpers.DontNegate<byte>>(ref MemoryMarshal.GetReference(span), span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAnyExcept(ReadOnlySpan<byte> span) =>
            LastIndexOfAny<SpanHelpers.Negate<byte>>(ref MemoryMarshal.GetReference(span), span.Length);

        private int IndexOfAny<TNegator>(ref byte searchSpace, int searchSpaceLength)
            where TNegator : struct, SpanHelpers.INegator<byte>
        {
            if (!Vector128.IsHardwareAccelerated || searchSpaceLength < Vector128<byte>.Count)
            {
                nuint offset = 0;
                uint lookUp;
                uint lessThan = _lessThan;
                uint value0 = _value0;
                uint value1 = _value1;

                while (searchSpaceLength >= 4)
                {
                    searchSpaceLength -= 4;

                    ref byte current = ref Unsafe.Add(ref searchSpace, offset);
                    lookUp = current;
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto Found;
                    lookUp = Unsafe.Add(ref current, 1);
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto Found1;
                    lookUp = Unsafe.Add(ref current, 2);
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto Found2;
                    lookUp = Unsafe.Add(ref current, 3);
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto Found3;

                    offset += 4;
                }

                while (searchSpaceLength > 0)
                {
                    searchSpaceLength -= 1;

                    lookUp = Unsafe.Add(ref searchSpace, offset);
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto Found;

                    offset += 1;
                }

                return -1;
            Found3:
                return (int)(offset + 3);
            Found2:
                return (int)(offset + 2);
            Found1:
                return (int)(offset + 1);
            Found:
                return (int)(offset);
            }
            else if (Vector256.IsHardwareAccelerated && searchSpaceLength >= Vector256<byte>.Count)
            {
                Vector256<byte> lessThan = Vector256.Create(_lessThan);
                Vector256<byte> values0 = Vector256.Create(_value0);
                Vector256<byte> values1 = Vector256.Create(_value1);
                Vector256<byte> current, equals;

                ref byte currentSearchSpace = ref searchSpace;
                ref byte oneVectorAwayFromEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength - Vector256<byte>.Count);

                // Loop until either we've finished all elements or there's less than a vector's-worth remaining.
                do
                {
                    current = Vector256.LoadUnsafe(ref currentSearchSpace);
                    equals = TNegator.NegateIfNeeded(Vector256.LessThan(current, lessThan) | Vector256.Equals(values0, current) | Vector256.Equals(values1, current));

                    if (equals == Vector256<byte>.Zero)
                    {
                        currentSearchSpace = ref Unsafe.Add(ref currentSearchSpace, Vector256<byte>.Count);
                        continue;
                    }

                    return SpanHelpers.ComputeFirstIndex(ref searchSpace, ref currentSearchSpace, equals);
                }
                while (!Unsafe.IsAddressGreaterThan(ref currentSearchSpace, ref oneVectorAwayFromEnd));

                // If any elements remain, process the last vector in the search space.
                if ((uint)searchSpaceLength % Vector256<byte>.Count != 0)
                {
                    current = Vector256.LoadUnsafe(ref oneVectorAwayFromEnd);
                    equals = TNegator.NegateIfNeeded(Vector256.LessThan(current, lessThan) | Vector256.Equals(values0, current) | Vector256.Equals(values1, current));

                    if (equals != Vector256<byte>.Zero)
                    {
                        return SpanHelpers.ComputeFirstIndex(ref searchSpace, ref oneVectorAwayFromEnd, equals);
                    }
                }
            }
            else
            {
                Vector128<byte> lessThan = Vector128.Create(_lessThan);
                Vector128<byte> values0 = Vector128.Create(_value0);
                Vector128<byte> values1 = Vector128.Create(_value1);
                Vector128<byte> current, equals;

                ref byte currentSearchSpace = ref searchSpace;
                ref byte oneVectorAwayFromEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength - Vector128<byte>.Count);

                // Loop until either we've finished all elements or there's less than a vector's-worth remaining.
                do
                {
                    current = Vector128.LoadUnsafe(ref currentSearchSpace);
                    equals = TNegator.NegateIfNeeded(Vector128.LessThan(current, lessThan) | Vector128.Equals(values0, current) | Vector128.Equals(values1, current));

                    if (equals == Vector128<byte>.Zero)
                    {
                        currentSearchSpace = ref Unsafe.Add(ref currentSearchSpace, Vector128<byte>.Count);
                        continue;
                    }

                    return SpanHelpers.ComputeFirstIndex(ref searchSpace, ref currentSearchSpace, equals);
                }
                while (!Unsafe.IsAddressGreaterThan(ref currentSearchSpace, ref oneVectorAwayFromEnd));

                // If any elements remain, process the first vector in the search space.
                if ((uint)searchSpaceLength % Vector128<byte>.Count != 0)
                {
                    current = Vector128.LoadUnsafe(ref oneVectorAwayFromEnd);
                    equals = TNegator.NegateIfNeeded(Vector128.LessThan(current, lessThan) | Vector128.Equals(values0, current) | Vector128.Equals(values1, current));

                    if (equals != Vector128<byte>.Zero)
                    {
                        return SpanHelpers.ComputeFirstIndex(ref searchSpace, ref oneVectorAwayFromEnd, equals);
                    }
                }
            }

            return -1;
        }

        private int LastIndexOfAny<TNegator>(ref byte searchSpace, int searchSpaceLength)
            where TNegator : struct, SpanHelpers.INegator<byte>
        {
            if (!Vector128.IsHardwareAccelerated || searchSpaceLength < Vector128<byte>.Count)
            {
                nuint offset = (nuint)searchSpaceLength - 1;
                uint lookUp;
                uint lessThan = _lessThan;
                uint value0 = _value0;
                uint value1 = _value1;

                while (searchSpaceLength >= 4)
                {
                    searchSpaceLength -= 4;

                    ref byte current = ref Unsafe.Add(ref searchSpace, offset);
                    lookUp = current;
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto Found;
                    lookUp = Unsafe.Add(ref current, -1);
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto FoundM1;
                    lookUp = Unsafe.Add(ref current, -2);
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto FoundM2;
                    lookUp = Unsafe.Add(ref current, -3);
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto FoundM3;

                    offset -= 4;
                }

                while (searchSpaceLength > 0)
                {
                    searchSpaceLength -= 1;

                    lookUp = Unsafe.Add(ref searchSpace, offset);
                    if (TNegator.NegateIfNeeded(lookUp < lessThan || lookUp == value0 || lookUp == value1)) goto Found;

                    offset -= 1;
                }

                return -1;
            FoundM3:
                return (int)(offset - 3);
            FoundM2:
                return (int)(offset - 2);
            FoundM1:
                return (int)(offset - 1);
            Found:
                return (int)(offset);
            }
            else if (Vector256.IsHardwareAccelerated && searchSpaceLength >= Vector256<byte>.Count)
            {
                Vector256<byte> lessThan = Vector256.Create(_lessThan);
                Vector256<byte> values0 = Vector256.Create(_value0);
                Vector256<byte> values1 = Vector256.Create(_value1);
                Vector256<byte> current, equals;

                nint offset = searchSpaceLength - Vector256<byte>.Count;

                // Loop until either we've finished all elements or there's less than a vector's-worth remaining.
                while (offset > 0)
                {
                    current = Vector256.LoadUnsafe(ref searchSpace, (nuint)(offset));
                    equals = TNegator.NegateIfNeeded(Vector256.LessThan(current, lessThan) | Vector256.Equals(values0, current) | Vector256.Equals(values1, current));

                    if (equals == Vector256<byte>.Zero)
                    {
                        offset -= Vector256<byte>.Count;
                        continue;
                    }

                    return SpanHelpers.ComputeLastIndex(offset, equals);
                }

                // Process the first vector in the search space.

                current = Vector256.LoadUnsafe(ref searchSpace);
                equals = TNegator.NegateIfNeeded(Vector256.LessThan(current, lessThan) | Vector256.Equals(values0, current) | Vector256.Equals(values1, current));

                if (equals != Vector256<byte>.Zero)
                {
                    return SpanHelpers.ComputeLastIndex(offset: 0, equals);
                }
            }
            else
            {
                Vector128<byte> lessThan = Vector128.Create(_lessThan);
                Vector128<byte> values0 = Vector128.Create(_value0);
                Vector128<byte> values1 = Vector128.Create(_value1);
                Vector128<byte> current, equals;

                nint offset = searchSpaceLength - Vector128<byte>.Count;

                // Loop until either we've finished all elements or there's less than a vector's-worth remaining.
                while (offset > 0)
                {
                    current = Vector128.LoadUnsafe(ref searchSpace, (nuint)(offset));
                    equals = TNegator.NegateIfNeeded(Vector128.LessThan(current, lessThan) | Vector128.Equals(values0, current) | Vector128.Equals(values1, current));

                    if (equals == Vector128<byte>.Zero)
                    {
                        offset -= Vector128<byte>.Count;
                        continue;
                    }

                    return SpanHelpers.ComputeLastIndex(offset, equals);
                }

                // Process the first vector in the search space.

                current = Vector128.LoadUnsafe(ref searchSpace);
                equals = TNegator.NegateIfNeeded(Vector128.LessThan(current, lessThan) | Vector128.Equals(values0, current) | Vector128.Equals(values1, current));

                if (equals != Vector128<byte>.Zero)
                {
                    return SpanHelpers.ComputeLastIndex(offset: 0, equals);
                }
            }

            return -1;
        }
    }
}
