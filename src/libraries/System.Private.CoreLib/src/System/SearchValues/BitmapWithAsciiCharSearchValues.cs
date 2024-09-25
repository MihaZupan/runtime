// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace System.Buffers
{
    internal sealed class BitmapWithAsciiCharSearchValues<TOptimizations> : SearchValues<char>
        where TOptimizations : struct, IndexOfAnyAsciiSearcher.IOptimizations
    {
        private IndexOfAnyAsciiSearcher.AsciiState _asciiState;
        private IndexOfAnyAsciiSearcher.AsciiState _inverseAsciiState;
        private readonly uint[] _bitmap;

        public BitmapWithAsciiCharSearchValues(ReadOnlySpan<char> values, int maxInclusive)
        {
            Debug.Assert(IndexOfAnyAsciiSearcher.IsVectorizationSupported);
            Debug.Assert(values.ContainsAnyInRange((char)0, (char)127));

            IndexOfAnyAsciiSearcher.ComputeAsciiState(values, out _asciiState);
            _inverseAsciiState = _asciiState.CreateInverse();

            _bitmap = new uint[maxInclusive / 32 + 1];

            foreach (char c in values)
            {
                _bitmap[c >> 5] |= 1u << c;
            }
        }

        internal override char[] GetValues()
        {
            var chars = new List<char>();
            uint[] bitmap = _bitmap;

            for (int i = 0; i < _bitmap.Length * 32; i++)
            {
                if (Contains(bitmap, i))
                {
                    chars.Add((char)i);
                }
            }

            return chars.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(char value) =>
            Contains(_bitmap, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Contains(uint[] bitmap, int value)
        {
            uint offset = (uint)(value >> 5);
            return offset < (uint)bitmap.Length && (bitmap[offset] & (1u << value)) != 0;
        }

        internal override int IndexOfAny(ReadOnlySpan<char> span)
        {
            int offset = 0;

            // We check whether the first character is ASCII before calling into IndexOfAnyAsciiSearcher
            // in order to minimize the overhead this fast-path has on non-ASCII texts.
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported && span.Length >= Vector128<short>.Count && char.IsAscii(span[0]))
            {
                // We are using IndexOfAnyAsciiSearcher to search for the first ASCII character in the set, or any non-ASCII character.
                // We do this by inverting the bitmap and using the opposite search function (Negate instead of DontNegate).

                // If the bitmap we're using contains a 0, we have to use 'Ssse3AndWasmHandleZeroInNeedle' when running on X86 and WASM.
                // Everything else should use 'Default'. 'TOptimizations' specifies whether '_asciiState' contains a 0.
                // Since we're using the inverse bitmap in this case, we have to use 'Ssse3AndWasmHandleZeroInNeedle' iff we're
                // running on X86/WASM and 'TOptimizations' is 'Default' (as that means that the inverse bitmap definitely has a 0).
                Debug.Assert(_asciiState.Lookup.Contains(0) != _inverseAsciiState.Lookup.Contains(0));

                if ((Ssse3.IsSupported || PackedSimd.IsSupported) && typeof(TOptimizations) == typeof(IndexOfAnyAsciiSearcher.Default))
                {
                    Debug.Assert(_inverseAsciiState.Lookup.Contains(0), "The inverse bitmap did not contain a 0.");

                    offset = IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Ssse3AndWasmHandleZeroInNeedle, SearchValues.FalseConst>(
                        ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)),
                        span.Length,
                        ref _inverseAsciiState);
                }
                else
                {
                    Debug.Assert(!(Ssse3.IsSupported || PackedSimd.IsSupported) || !_inverseAsciiState.Lookup.Contains(0),
                        "The inverse bitmap contained a 0, but we're not using Ssse3AndWasmHandleZeroInNeedle.");

                    offset = IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Default, SearchValues.FalseConst>(
                        ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)),
                        span.Length,
                        ref _inverseAsciiState);
                }

                // If we've reached the end of the span or stopped at an ASCII character, we've found the result.
                if ((uint)offset >= (uint)span.Length || char.IsAscii(span[offset]))
                {
                    return offset;
                }

                // Fall back to using the ProbabilisticMap.
                span = span.Slice(offset);
            }

            int index = IndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(ref MemoryMarshal.GetReference(span), span.Length);

            if (index >= 0)
            {
                // We found a match. Account for the number of ASCII characters we've skipped previously.
                index += offset;
            }

            return index;
        }

        internal override int IndexOfAnyExcept(ReadOnlySpan<char> span)
        {
            int offset = 0;

            // We check whether the first character is ASCII before calling into IndexOfAnyAsciiSearcher
            // in order to minimize the overhead this fast-path has on non-ASCII texts.
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported && span.Length >= Vector128<short>.Count && char.IsAscii(span[0]))
            {
                // Do a regular IndexOfAnyExcept for the ASCII characters. The search will stop if we encounter a non-ASCII char.
                offset = IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.Negate, TOptimizations, SearchValues.FalseConst>(
                    ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)),
                    span.Length,
                    ref _asciiState);

                // If we've reached the end of the span or stopped at an ASCII character, we've found the result.
                if ((uint)offset >= (uint)span.Length || char.IsAscii(span[offset]))
                {
                    return offset;
                }

                // Fall back to a simple char-by-char search.
                span = span.Slice(offset);
            }

            int index = IndexOfAny<IndexOfAnyAsciiSearcher.Negate>(ref MemoryMarshal.GetReference(span), span.Length);

            if (index >= 0)
            {
                // We found a match. Account for the number of ASCII characters we've skipped previously.
                index += offset;
            }

            return index;
        }

        internal override int LastIndexOfAny(ReadOnlySpan<char> span)
        {
            // We check whether the last character is ASCII before calling into IndexOfAnyAsciiSearcher
            // in order to minimize the overhead this fast-path has on non-ASCII texts.
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported && span.Length >= Vector128<short>.Count && char.IsAscii(span[^1]))
            {
                // We are using IndexOfAnyAsciiSearcher to search for the last ASCII character in the set, or any non-ASCII character.
                // We do this by inverting the bitmap and using the opposite search function (Negate instead of DontNegate).

                // If the bitmap we're using contains a 0, we have to use 'Ssse3AndWasmHandleZeroInNeedle' when running on X86 and WASM.
                // Everything else should use 'Default'. 'TOptimizations' specifies whether '_asciiState' contains a 0.
                // Since we're using the inverse bitmap in this case, we have to use 'Ssse3AndWasmHandleZeroInNeedle' iff we're
                // running on X86/WASM and 'TOptimizations' is 'Default' (as that means that the inverse bitmap definitely has a 0).
                Debug.Assert(_asciiState.Lookup.Contains(0) != _inverseAsciiState.Lookup.Contains(0));

                int offset;

                if ((Ssse3.IsSupported || PackedSimd.IsSupported) && typeof(TOptimizations) == typeof(IndexOfAnyAsciiSearcher.Default))
                {
                    Debug.Assert(_inverseAsciiState.Lookup.Contains(0), "The inverse bitmap did not contain a 0.");

                    offset = IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Ssse3AndWasmHandleZeroInNeedle, SearchValues.FalseConst>(
                        ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)),
                        span.Length,
                        ref _inverseAsciiState);
                }
                else
                {
                    Debug.Assert(!(Ssse3.IsSupported || PackedSimd.IsSupported) || !_inverseAsciiState.Lookup.Contains(0),
                        "The inverse bitmap contained a 0, but we're not using Ssse3AndWasmHandleZeroInNeedle.");

                    offset = IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Default, SearchValues.FalseConst>(
                        ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)),
                        span.Length,
                        ref _inverseAsciiState);
                }

                // If we've reached the end of the span or stopped at an ASCII character, we've found the result.
                if ((uint)offset >= (uint)span.Length || char.IsAscii(span[offset]))
                {
                    return offset;
                }

                // Fall back to using the ProbabilisticMap.
                span = span.Slice(0, offset + 1);
            }

            return LastIndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(ref MemoryMarshal.GetReference(span), span.Length);
        }

        internal override int LastIndexOfAnyExcept(ReadOnlySpan<char> span)
        {
            // We check whether the last character is ASCII before calling into IndexOfAnyAsciiSearcher
            // in order to minimize the overhead this fast-path has on non-ASCII texts.
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported && span.Length >= Vector128<short>.Count && char.IsAscii(span[^1]))
            {
                // Do a regular LastIndexOfAnyExcept for the ASCII characters. The search will stop if we encounter a non-ASCII char.
                int offset = IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate, TOptimizations, SearchValues.FalseConst>(
                    ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)),
                    span.Length,
                    ref _asciiState);

                // If we've reached the end of the span or stopped at an ASCII character, we've found the result.
                if ((uint)offset >= (uint)span.Length || char.IsAscii(span[offset]))
                {
                    return offset;
                }

                // Fall back to a simple char-by-char search.
                span = span.Slice(0, offset + 1);
            }

            return LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate>(ref MemoryMarshal.GetReference(span), span.Length);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int IndexOfAny<TNegator>(ref char searchSpace, int searchSpaceLength)
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            ref char searchSpaceEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength);
            ref char cur = ref searchSpace;
            uint[] bitmap = _bitmap;

            while (!Unsafe.AreSame(ref cur, ref searchSpaceEnd))
            {
                char c = cur;
                if (TNegator.NegateIfNeeded(Contains(bitmap, c)))
                {
                    return (int)((nuint)Unsafe.ByteOffset(ref searchSpace, ref cur) / sizeof(char));
                }

                cur = ref Unsafe.Add(ref cur, 1);
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int LastIndexOfAny<TNegator>(ref char searchSpace, int searchSpaceLength)
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            uint[] bitmap = _bitmap;

            while (--searchSpaceLength >= 0)
            {
                char c = Unsafe.Add(ref searchSpace, searchSpaceLength);
                if (TNegator.NegateIfNeeded(Contains(bitmap, c)))
                {
                    break;
                }
            }

            return searchSpaceLength;
        }
    }
}
