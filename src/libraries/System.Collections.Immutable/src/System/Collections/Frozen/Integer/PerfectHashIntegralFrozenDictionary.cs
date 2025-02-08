// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace System.Collections.Frozen
{
    /// <summary>Provides a <see cref="FrozenDictionary{TKey, TValue}"/> for integral keys where we can find a perfect hash relatively efficiently.</summary>
    internal static class PerfectHashIntegralFrozenDictionary
    {
        public static FrozenDictionary<TKey, TValue>? CreateIfValid<TKey, TValue>(Dictionary<TKey, TValue> source)
            where TKey : notnull
        {
            Debug.Assert(source.Count > 0);
            Debug.Assert(typeof(TKey) != typeof(byte) && typeof(TKey) != typeof(sbyte), "This source should have been handled by DenseIntegralFrozenDictionary.");

            return
                typeof(TKey) == typeof(ushort) || (typeof(TKey).IsEnum && typeof(TKey).GetEnumUnderlyingType() == typeof(ushort)) ? CreateIfValid<TKey, ushort, TValue>(source) :
                typeof(TKey) == typeof(short) || (typeof(TKey).IsEnum && typeof(TKey).GetEnumUnderlyingType() == typeof(short)) ? CreateIfValid<TKey, short, TValue>(source) :
                typeof(TKey) == typeof(char) ? CreateIfValid<TKey, char, TValue>(source) :
                typeof(TKey) == typeof(uint) || (typeof(TKey).IsEnum && typeof(TKey).GetEnumUnderlyingType() == typeof(uint)) ? CreateIfValid<TKey, uint, TValue>(source) :
                typeof(TKey) == typeof(int) || (typeof(TKey).IsEnum && typeof(TKey).GetEnumUnderlyingType() == typeof(int)) ? CreateIfValid<TKey, int, TValue>(source) :
                typeof(TKey) == typeof(ulong) || (typeof(TKey).IsEnum && typeof(TKey).GetEnumUnderlyingType() == typeof(ulong)) ? CreateIfValid<TKey, ulong, TValue>(source) :
                typeof(TKey) == typeof(long) || (typeof(TKey).IsEnum && typeof(TKey).GetEnumUnderlyingType() == typeof(long)) ? CreateIfValid<TKey, long, TValue>(source) :
                null;
        }

        private static FrozenDictionary<TKey, TValue>? CreateIfValid<TKey, TKeyUnderlying, TValue>(Dictionary<TKey, TValue> source)
            where TKey : notnull
            where TKeyUnderlying : unmanaged, IBinaryInteger<TKeyUnderlying>
        {
            char[] charKeys = ArrayPool<char>.Shared.Rent(source.Count);
            int count = 0;
            int maxKey = int.MinValue;

            foreach ((TKey key, _) in source)
            {
                if (!PerfectHashIntegralFrozenSet.IsInUInt16Range<TKey, TKeyUnderlying>(key))
                {
                    ArrayPool<char>.Shared.Return(charKeys);
                    return null;
                }

                char c = PerfectHashIntegralFrozenSet.ToChar<TKey, TKeyUnderlying>(key);
                charKeys[count++] = c;
                maxKey = Math.Max(maxKey, c);
            }

            Debug.Assert(count == source.Count);

            PerfectHashCharLookup.Initialize(charKeys.AsSpan(0, count), maxKey, out uint multiplier, out char[] hashEntries);

            ArrayPool<char>.Shared.Return(charKeys);

            return new UInt16PerfectHashDictionary<TKey, TKeyUnderlying, TValue>(source, multiplier, hashEntries);
        }

        [DebuggerTypeProxy(typeof(DebuggerProxy<,,>))]
        private sealed class UInt16PerfectHashDictionary<TKey, TKeyUnderlying, TValue> : FrozenDictionary<TKey, TValue>
            where TKey : notnull
            where TKeyUnderlying : unmanaged, IBinaryInteger<TKeyUnderlying>
        {
            private readonly uint _multiplier;
            private readonly char[] _hashEntries;
            private readonly TValue[] _valueEntries;
            private readonly TKey[] _keys;

            public UInt16PerfectHashDictionary(Dictionary<TKey, TValue> source, uint multiplier, char[] hashEntries) : base(EqualityComparer<TKey>.Default)
            {
                Debug.Assert(ReferenceEquals(source.Comparer, EqualityComparer<TKey>.Default));

                _multiplier = multiplier;
                _hashEntries = hashEntries;

                _keys = new TKey[source.Count];
                _valueEntries = new TValue[_hashEntries.Length];

                int count = 0;

                foreach ((TKey key, TValue value) in source)
                {
                    _keys[count++] = key;
                    Unsafe.AsRef(in GetValueRefOrNullRefCore(key)) = value;
                }
            }

            private protected override TKey[] KeysCore => _keys;

            [field: MaybeNull]
            private protected override TValue[] ValuesCore
            {
                get
                {
                    return field ?? AllocateValuesArray();

                    TValue[] AllocateValuesArray()
                    {
                        TValue[] values = new TValue[_keys.Length];

                        for (int i = 0; i < values.Length; i++)
                        {
                            values[i] = GetValueRefOrNullRefCore(_keys[i]);
                        }

                        return field ??= values;
                    }
                }
            }

            private protected override Enumerator GetEnumeratorCore() => new Enumerator(KeysCore, ValuesCore);

            private protected override int CountCore => _keys.Length;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private protected override ref readonly TValue GetValueRefOrNullRefCore(TKey key)
            {
                if (PerfectHashIntegralFrozenSet.IsInUInt16Range<TKey, TKeyUnderlying>(key))
                {
                    return ref PerfectHashCharLookup.GetValueRefOrNullRef(_hashEntries, _multiplier, _valueEntries, PerfectHashIntegralFrozenSet.ToChar<TKey, TKeyUnderlying>(key));
                }

                return ref Unsafe.NullRef<TValue>();
            }
        }

        private sealed class DebuggerProxy<TKey, TKeyUnderlying, TValue>(IReadOnlyDictionary<TKey, TValue> dictionary) :
            ImmutableDictionaryDebuggerProxy<TKey, TValue>(dictionary)
            where TKey : notnull;
    }
}
