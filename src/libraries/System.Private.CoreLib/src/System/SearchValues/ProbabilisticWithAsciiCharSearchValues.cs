// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace System.Buffers
{
    internal sealed class ProbabilisticWithAsciiCharSearchValues<TOptimizations> : SearchValues<char>
        where TOptimizations : struct, IndexOfAnyAsciiSearcher.IOptimizations
    {
        private IndexOfAnyAsciiSearcher.AsciiState _asciiState;
        private IndexOfAnyAsciiSearcher.AsciiState _inverseAsciiState;
        private ProbabilisticMapState _map;
        private readonly string _values;

        public ProbabilisticWithAsciiCharSearchValues(ReadOnlySpan<char> values)
        {
            Debug.Assert(IndexOfAnyAsciiSearcher.IsVectorizationSupported);
            Debug.Assert(values.ContainsAnyInRange((char)0, (char)127));

            IndexOfAnyAsciiSearcher.ComputeAsciiState(values, out _asciiState);
            _inverseAsciiState = _asciiState.CreateInverse();

            _values = new string(values);
            _map = new ProbabilisticMapState(_values);
        }

        internal override char[] GetValues() => _values.ToCharArray();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(char value) =>
            _map.FastContains(value);

        internal override int IndexOfAny(ReadOnlySpan<char> span) =>
            IndexOfAny(ref MemoryMarshal.GetReference(span), span.Length);

        private int IndexOfAny(ref char searchSpace, int searchSpaceLength)
        {
            int offset = 0;

            // We check whether the first character is ASCII before calling into IndexOfAnyAsciiSearcher
            // in order to minimize the overhead this fast-path has on non-ASCII texts.
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported &&
                searchSpaceLength >= Vector128<short>.Count &&
                char.IsAscii(searchSpace))
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

                    offset = IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Ssse3AndWasmHandleZeroInNeedle>(
                        ref Unsafe.As<char, short>(ref searchSpace),
                        searchSpaceLength,
                        ref _inverseAsciiState);
                }
                else
                {
                    Debug.Assert(!(Ssse3.IsSupported || PackedSimd.IsSupported) || !_inverseAsciiState.Lookup.Contains(0),
                        "The inverse bitmap contained a 0, but we're not using Ssse3AndWasmHandleZeroInNeedle.");

                    offset = IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Default>(
                        ref Unsafe.As<char, short>(ref searchSpace),
                        searchSpaceLength,
                        ref _inverseAsciiState);
                }

                // If we've reached the end of the span or stopped at an ASCII character, we've found the result.
                if ((uint)offset >= (uint)searchSpaceLength || char.IsAscii(Unsafe.Add(ref searchSpace, offset)))
                {
                    return offset;
                }

                // Fall back to using the ProbabilisticMap.
            }

            int index = ProbabilisticMap.IndexOfAny<SearchValues.TrueConst>(
                ref Unsafe.Add(ref searchSpace, offset),
                searchSpaceLength - offset,
                ref _map);

            if (index >= 0)
            {
                // We found a match. Account for the number of ASCII characters we've skipped previously.
                index += offset;
            }

            return index;
        }

        internal override int IndexOfAnyExcept(ReadOnlySpan<char> span) =>
            IndexOfAnyExcept(ref MemoryMarshal.GetReference(span), span.Length);

        private int IndexOfAnyExcept(ref char searchSpace, int searchSpaceLength)
        {
            int offset = 0;

            // We check whether the first character is ASCII before calling into IndexOfAnyAsciiSearcher
            // in order to minimize the overhead this fast-path has on non-ASCII texts.
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported &&
                searchSpaceLength >= Vector128<short>.Count &&
                char.IsAscii(searchSpace))
            {
                // Do a regular IndexOfAnyExcept for the ASCII characters. The search will stop if we encounter a non-ASCII char.
                offset = IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.Negate, TOptimizations>(
                    ref Unsafe.As<char, short>(ref searchSpace),
                    searchSpaceLength,
                    ref _asciiState);

                // If we've reached the end of the span or stopped at an ASCII character, we've found the result.
                if ((uint)offset >= (uint)searchSpaceLength || char.IsAscii(Unsafe.Add(ref searchSpace, offset)))
                {
                    return offset;
                }

                // Fall back to a simple char-by-char search.
            }

            uint multiplier = _map._multiplier;
            char[] hashEntries = _map._hashEntries!;

            ref char searchSpaceEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength);
            ref char cur = ref Unsafe.Add(ref searchSpace, offset);

            while (!Unsafe.AreSame(ref cur, ref searchSpaceEnd))
            {
                char c = cur;
                if (!ProbabilisticMapState.FastContains(hashEntries, multiplier, c))
                {
                    return (int)((nuint)Unsafe.ByteOffset(ref searchSpace, ref cur) / sizeof(char));
                }

                cur = ref Unsafe.Add(ref cur, 1);
            }

            return -1;
        }

        internal override int LastIndexOfAny(ReadOnlySpan<char> span) =>
            LastIndexOfAny(ref MemoryMarshal.GetReference(span), span.Length);

        private int LastIndexOfAny(ref char searchSpace, int searchSpaceLength)
        {
            // We check whether the last character is ASCII before calling into IndexOfAnyAsciiSearcher
            // in order to minimize the overhead this fast-path has on non-ASCII texts.
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported &&
                searchSpaceLength >= Vector128<short>.Count &&
                char.IsAscii(Unsafe.Add(ref searchSpace, searchSpaceLength - 1)))
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

                    offset = IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Ssse3AndWasmHandleZeroInNeedle>(
                        ref Unsafe.As<char, short>(ref searchSpace),
                        searchSpaceLength,
                        ref _inverseAsciiState);
                }
                else
                {
                    Debug.Assert(!(Ssse3.IsSupported || PackedSimd.IsSupported) || !_inverseAsciiState.Lookup.Contains(0),
                        "The inverse bitmap contained a 0, but we're not using Ssse3AndWasmHandleZeroInNeedle.");

                    offset = IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Default>(
                        ref Unsafe.As<char, short>(ref searchSpace),
                        searchSpaceLength,
                        ref _inverseAsciiState);
                }

                // If we've reached the end of the span or stopped at an ASCII character, we've found the result.
                if ((uint)offset >= (uint)searchSpaceLength || char.IsAscii(Unsafe.Add(ref searchSpace, offset)))
                {
                    return offset;
                }

                // Fall back to using the ProbabilisticMap.
                searchSpaceLength = offset + 1;
            }

            return ProbabilisticMap.LastIndexOfAny<SearchValues.TrueConst>(
                ref searchSpace,
                searchSpaceLength,
                ref _map);
        }

        internal override int LastIndexOfAnyExcept(ReadOnlySpan<char> span) =>
            LastIndexOfAnyExcept(ref MemoryMarshal.GetReference(span), span.Length);

        private int LastIndexOfAnyExcept(ref char searchSpace, int searchSpaceLength)
        {
            // We check whether the last character is ASCII before calling into IndexOfAnyAsciiSearcher
            // in order to minimize the overhead this fast-path has on non-ASCII texts.
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported &&
                searchSpaceLength >= Vector128<short>.Count &&
                char.IsAscii(Unsafe.Add(ref searchSpace, searchSpaceLength - 1)))
            {
                // Do a regular LastIndexOfAnyExcept for the ASCII characters. The search will stop if we encounter a non-ASCII char.
                int offset = IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate, TOptimizations>(
                    ref Unsafe.As<char, short>(ref searchSpace),
                    searchSpaceLength,
                    ref _asciiState);

                // If we've reached the end of the span or stopped at an ASCII character, we've found the result.
                if ((uint)offset >= (uint)searchSpaceLength || char.IsAscii(Unsafe.Add(ref searchSpace, offset)))
                {
                    return offset;
                }

                // Fall back to a simple char-by-char search.
                searchSpaceLength = offset + 1;
            }

            return ProbabilisticMap.LastIndexOfAnySimpleLoop<SearchValues.TrueConst, IndexOfAnyAsciiSearcher.Negate>(
                ref searchSpace,
                searchSpaceLength,
                ref _map);
        }
    }
}
