// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CS8500 // Takes the address of a managed type

namespace System.Buffers
{
    internal unsafe struct ProbabilisticMapState
    {
        public ProbabilisticMap Map;
        private readonly uint _multiplier;
        private readonly char[]? _hashEntries;
        private readonly ReadOnlySpan<char>* _slowContainsValuesPtr;

        public ProbabilisticMapState(ReadOnlySpan<char> values)
        {
            Map = new ProbabilisticMap(values);

            int modulus = FindModulus(values);
            Debug.Assert(modulus <= char.MaxValue);

            _multiplier = GetFastModMultiplier((ushort)modulus);
            _hashEntries = new char[modulus];

            foreach (char c in values)
            {
                _hashEntries[FastMod(c, (uint)modulus, _multiplier)] = c;
            }
        }

        // valuesPtr must remain valid for as long as this ProbabilisticMapState is used.
        public unsafe ProbabilisticMapState(ReadOnlySpan<char>* valuesPtr)
        {
            Debug.Assert((IntPtr)valuesPtr != IntPtr.Zero);

            Map = new ProbabilisticMap(*valuesPtr);
            _slowContainsValuesPtr = valuesPtr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool FastContains(char value)
        {
            Debug.Assert(_hashEntries is not null);
            Debug.Assert((IntPtr)_slowContainsValuesPtr == IntPtr.Zero);

            return FastContains(_hashEntries, _multiplier, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool FastContains(char[] hashEntries, uint multiplier, char value)
        {
            ulong offset = FastMod(value, (uint)hashEntries.Length, multiplier);
            Debug.Assert(offset < (ulong)hashEntries.Length);

            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(hashEntries), (nuint)offset) == value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool SlowProbabilisticContains(char value)
        {
            Debug.Assert(_hashEntries is null);
            Debug.Assert((IntPtr)_slowContainsValuesPtr != IntPtr.Zero);

            return ProbabilisticMap.Contains(
                ref Unsafe.As<ProbabilisticMap, uint>(ref Map),
                *_slowContainsValuesPtr,
                value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool SlowContains(char value)
        {
            Debug.Assert(_hashEntries is null);
            Debug.Assert((IntPtr)_slowContainsValuesPtr != IntPtr.Zero);

            return ProbabilisticMap.Contains(*_slowContainsValuesPtr, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfirmProbabilisticMatch<TUseFastContains>(char value)
            where TUseFastContains : struct, SearchValues.IRuntimeConst
        {
            if (TUseFastContains.Value)
            {
                return FastContains(value);
            }
            else
            {
                return SlowContains(value);
            }
        }

        private static int FindModulus(ReadOnlySpan<char> chars)
        {
            bool[] seen = ArrayPool<bool>.Shared.Rent(char.MaxValue + 1);

            // Doesn't technically have to be prime.
            int modulus = HashHelpers.GetPrime(chars.Length);
            int numbersTested = 0;

            while (true)
            {
                if (TestModulus(chars, seen, modulus))
                {
                    ArrayPool<bool>.Shared.Return(seen);
                    return modulus;
                }

                modulus = HashHelpers.GetPrime(modulus + 1);
                numbersTested++;

                if (modulus >= char.MaxValue)
                {
                    return char.MaxValue;
                }

                // We optimize for the common case of sets not containing duplicates.
                // If we were unable to find a modulus after 10 attempts, it's likely that the set
                // does contain duplicates. We must remove them or we won't find a valid modulus.
                if (numbersTested == 10)
                {
                    chars = RemoveDuplicates(chars, seen);
                    modulus = HashHelpers.GetPrime(chars.Length);
                }
            }

            static bool TestModulus(ReadOnlySpan<char> chars, bool[] seen, int modulus)
            {
                seen.AsSpan(0, modulus).Clear();

                foreach (char c in chars)
                {
                    int index = c % modulus;

                    if (seen[index])
                    {
                        return false;
                    }

                    seen[index] = true;
                }

                // Saw no duplicates.
                return true;
            }

            static ReadOnlySpan<char> RemoveDuplicates(ReadOnlySpan<char> values, bool[] seen)
            {
                seen.AsSpan().Clear();

                int duplicates = 0;

                foreach (char c in values)
                {
                    if (seen[c])
                    {
                        duplicates++;
                    }
                    else
                    {
                        seen[c] = true;
                    }
                }

                if (duplicates == 0)
                {
                    return values;
                }

                char[] deduped = new char[values.Length - duplicates];
                int count = 0;

                for (int i = 0; i < seen.Length; i++)
                {
                    if (seen[i])
                    {
                        deduped[count++] = (char)i;
                    }
                }

                Debug.Assert(count == deduped.Length);
                return deduped;
            }
        }

        private static uint GetFastModMultiplier(ushort divisor) =>
            uint.MaxValue / divisor + 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong FastMod(char value, uint divisor, uint multiplier)
        {
            Debug.Assert(divisor <= char.MaxValue);
            Debug.Assert(multiplier == GetFastModMultiplier((ushort)divisor));

            ulong result = ((ulong)(multiplier * value) * divisor) >> 32;

            Debug.Assert(result == (value % divisor));
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOfAnySimpleLoop<TUseFastContains, TNegator>(ref char searchSpace, int searchSpaceLength, ref ProbabilisticMapState state)
            where TUseFastContains : struct, SearchValues.IRuntimeConst
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            ref char searchSpaceEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength);
            ref char cur = ref searchSpace;

            if (TUseFastContains.Value)
            {
                Debug.Assert(state._hashEntries is not null);

                char[] hashEntries = state._hashEntries;
                uint multiplier = state._multiplier;

                while (!Unsafe.AreSame(ref cur, ref searchSpaceEnd))
                {
                    char c = cur;
                    if (TNegator.NegateIfNeeded(FastContains(hashEntries, multiplier, c)))
                    {
                        return (int)((nuint)Unsafe.ByteOffset(ref searchSpace, ref cur) / sizeof(char));
                    }

                    cur = ref Unsafe.Add(ref cur, 1);
                }
            }
            else
            {
                while (!Unsafe.AreSame(ref cur, ref searchSpaceEnd))
                {
                    char c = cur;
                    if (TNegator.NegateIfNeeded(state.SlowProbabilisticContains(c)))
                    {
                        return (int)((nuint)Unsafe.ByteOffset(ref searchSpace, ref cur) / sizeof(char));
                    }

                    cur = ref Unsafe.Add(ref cur, 1);
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastIndexOfAnySimpleLoop<TUseFastContains, TNegator>(ref char searchSpace, int searchSpaceLength, ref ProbabilisticMapState state)
            where TUseFastContains : struct, SearchValues.IRuntimeConst
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            if (TUseFastContains.Value)
            {
                Debug.Assert(state._hashEntries is not null);

                char[] hashEntries = state._hashEntries;
                uint multiplier = state._multiplier;

                while (--searchSpaceLength >= 0)
                {
                    char c = Unsafe.Add(ref searchSpace, searchSpaceLength);
                    if (TNegator.NegateIfNeeded(FastContains(hashEntries, multiplier, c)))
                    {
                        break;
                    }
                }
            }
            else
            {
                while (--searchSpaceLength >= 0)
                {
                    char c = Unsafe.Add(ref searchSpace, searchSpaceLength);
                    if (TNegator.NegateIfNeeded(state.SlowProbabilisticContains(c)))
                    {
                        break;
                    }
                }
            }

            return searchSpaceLength;
        }
    }
}
