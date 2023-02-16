// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace System.Buffers
{
    internal sealed class IndexOfAnyCharValuesProbabilistic<TCharacterCheck, TValues> : IndexOfAnyValues<char>
        where TCharacterCheck : struct, IndexOfAnyCharValuesProbabilistic.ICharacterCheck<TValues>
    {
        private ProbabilisticMap _map;
        private readonly TValues _values;

        public IndexOfAnyCharValuesProbabilistic(ProbabilisticMap map, TValues values)
        {
            _map = map;
            _values = values;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(char value) =>
            TCharacterCheck.Contains(value, _values);

        internal override char[] GetValues()
        {
            List<char> chars = new();

            for (int i = 0; i <= char.MaxValue; i++)
            {
                if (ContainsCore((char)i))
                {
                    chars.Add((char)i);
                }
            }

            return chars.ToArray();
        }

        internal override int IndexOfAny(ReadOnlySpan<char> span)
        {
            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char searchSpaceEnd = ref Unsafe.Add(ref searchSpace, span.Length);
            ref char cur = ref searchSpace;

            ref uint charMap = ref Unsafe.As<ProbabilisticMap, uint>(ref _map);
            TValues values = _values;

            while (!Unsafe.AreSame(ref cur, ref searchSpaceEnd))
            {
                int ch = cur;
                if (ProbabilisticMap.Contains<TCharacterCheck, TValues>(ref charMap, values, ch))
                {
                    return (int)(Unsafe.ByteOffset(ref searchSpace, ref cur) / sizeof(char));
                }

                cur = ref Unsafe.Add(ref cur, 1);
            }

            return -1;
        }

        internal override int IndexOfAnyExcept(ReadOnlySpan<char> span)
        {
            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char searchSpaceEnd = ref Unsafe.Add(ref searchSpace, span.Length);
            ref char cur = ref searchSpace;

            TValues values = _values;

            while (!Unsafe.AreSame(ref cur, ref searchSpaceEnd))
            {
                if (!TCharacterCheck.Contains(cur, values))
                {
                    return (int)(Unsafe.ByteOffset(ref searchSpace, ref cur) / sizeof(char));
                }

                cur = ref Unsafe.Add(ref cur, 1);
            }

            return -1;
        }

        internal override int LastIndexOfAny(ReadOnlySpan<char> span)
        {
            ref uint charMap = ref Unsafe.As<ProbabilisticMap, uint>(ref _map);
            TValues values = _values;

            for (int i = span.Length - 1; i >= 0; i--)
            {
                int ch = Unsafe.Add(ref MemoryMarshal.GetReference(span), i);
                if (ProbabilisticMap.Contains<TCharacterCheck, TValues>(ref charMap, values, ch))
                {
                    return i;
                }
            }

            return -1;
        }

        internal override int LastIndexOfAnyExcept(ReadOnlySpan<char> span)
        {
            TValues values = _values;

            for (int i = span.Length - 1; i >= 0; i--)
            {
                char ch = Unsafe.Add(ref MemoryMarshal.GetReference(span), i);
                if (!TCharacterCheck.Contains(ch, values))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    internal static class IndexOfAnyCharValuesProbabilistic
    {
        public const int MaxValuesForProbabilisticMap = 64;

        public static IndexOfAnyValues<char> Create(ReadOnlySpan<char> values)
        {
            Debug.Assert(values.Length > 5 && values.Length <= MaxValuesForProbabilisticMap);

            var map = new ProbabilisticMap(values);

            if (Vector128.IsHardwareAccelerated)
            {
                if (values.Length <= Vector128<ushort>.Count)
                {
                    Vector128<ushort> valuesVector = Vector128.Create((ushort)values[0]);

                    for (int i = 0; i < values.Length; i++)
                    {
                        valuesVector.SetElementUnsafe(i, values[i]);
                    }

                    return new IndexOfAnyCharValuesProbabilistic<Vector128CharacterCheck, Vector128<ushort>>(map, valuesVector);
                }

                if (values.Length <= 2 * Vector128<ushort>.Count)
                {
                    Vector128<ushort> valuesVector0 = Vector128.Create((ushort)values[0]);
                    Vector128<ushort> valuesVector1 = valuesVector0;

                    for (int i = 0; i < Vector128<ushort>.Count; i++)
                    {
                        valuesVector0.SetElementUnsafe(i, values[i]);
                    }

                    for (int i = Vector128<ushort>.Count; i < values.Length; i++)
                    {
                        valuesVector1.SetElementUnsafe(i - Vector128<ushort>.Count, values[i]);
                    }

                    return Vector256.IsHardwareAccelerated
                        ? new IndexOfAnyCharValuesProbabilistic<Vector256CharacterCheck, Vector256<ushort>>(map, Vector256.Create(valuesVector0, valuesVector1))
                        : new IndexOfAnyCharValuesProbabilistic<Vector128x2CharacterCheck, (Vector128<ushort>, Vector128<ushort>)>(map, (valuesVector0, valuesVector1));
                }
            }

            return new IndexOfAnyCharValuesProbabilistic<StringCharacterCheck, string>(map, values.ToString());
        }

        public interface ICharacterCheck<TValues>
        {
            static abstract bool Contains(char c, TValues values);
        }

        private readonly struct Vector128CharacterCheck : ICharacterCheck<Vector128<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Contains(char c, Vector128<ushort> values)
            {
                Vector128<ushort> value = Vector128.Create((ushort)c);
                Vector128<ushort> result = Vector128.Equals(value, values);
                return result != Vector128<ushort>.Zero;
            }
        }

        private readonly struct Vector128x2CharacterCheck : ICharacterCheck<(Vector128<ushort> V0, Vector128<ushort> V1)>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Contains(char c, (Vector128<ushort> V0, Vector128<ushort> V1) values)
            {
                Vector128<ushort> value = Vector128.Create((ushort)c);
                Vector128<ushort> result = Vector128.Equals(value, values.V0) | Vector128.Equals(value, values.V1);
                return result != Vector128<ushort>.Zero;
            }
        }

        private readonly struct Vector256CharacterCheck : ICharacterCheck<Vector256<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Contains(char c, Vector256<ushort> values)
            {
                Vector256<ushort> value = Vector256.Create((ushort)c);
                Vector256<ushort> result = Vector256.Equals(value, values);
                return result != Vector256<ushort>.Zero;
            }
        }

        private readonly struct StringCharacterCheck : ICharacterCheck<string>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Contains(char c, string values)
            {
                return values.Contains(c);
            }
        }
    }
}
