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
                typeof(TKey) == typeof(ushort) || (typeof(TKey).IsEnum && typeof(TKey).GetEnumUnderlyingType() == typeof(ushort)) ? new UInt16PerfectHashDictionary<TKey, ushort, TValue>(source) :
                typeof(TKey) == typeof(short) || (typeof(TKey).IsEnum && typeof(TKey).GetEnumUnderlyingType() == typeof(short)) ? new UInt16PerfectHashDictionary<TKey, short, TValue>(source) :
                typeof(TKey) == typeof(char) ? new UInt16PerfectHashDictionary<TKey, char, TValue>(source) :
                null;
        }

        [DebuggerTypeProxy(typeof(DebuggerProxy<,,>))]
        private sealed class UInt16PerfectHashDictionary<TKey, TKeyUnderlying, TValue> : FrozenDictionary<TKey, TValue>
            where TKey : notnull
            where TKeyUnderlying : IBinaryInteger<TKeyUnderlying>
        {
            private readonly uint _multiplier;
            private readonly char[] _hashEntries;
            private readonly TValue[] _valueEntries;
            private readonly TKey[] _keys;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static char ToChar(TKey value)
            {
                if (typeof(TKeyUnderlying) == typeof(ushort)) return (char)(ushort)(object)value;
                if (typeof(TKeyUnderlying) == typeof(short)) return (char)(ushort)(short)(object)value;

                Debug.Assert(typeof(TKey) == typeof(char));
                return (char)(object)value;
            }

            public UInt16PerfectHashDictionary(Dictionary<TKey, TValue> source) : base(EqualityComparer<TKey>.Default)
            {
                Debug.Assert(ReferenceEquals(source.Comparer, EqualityComparer<TKey>.Default));
                Debug.Assert(source.Count != 0);

                _keys = new TKey[source.Count];

                char[] charKeys = ArrayPool<char>.Shared.Rent(_keys.Length);
                int index = 0;
                int maxKey = int.MinValue;

                foreach ((TKey key, _) in source)
                {
                    _keys[index] = key;
                    charKeys[index++] = ToChar(key);
                    maxKey = Math.Max(maxKey, ToChar(key));
                }

                PerfectHashCharLookup.Initialize(charKeys.AsSpan(0, _keys.Length), maxKey, out _multiplier, out _hashEntries);

                ArrayPool<char>.Shared.Return(charKeys);

                _valueEntries = new TValue[_hashEntries.Length];

                foreach ((TKey key, TValue value) in source)
                {
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
            private protected override ref readonly TValue GetValueRefOrNullRefCore(TKey key) =>
                ref PerfectHashCharLookup.GetValueRefOrNullRef(_hashEntries, _multiplier, _valueEntries, ToChar(key));
        }

        private sealed class DebuggerProxy<TKey, TKeyUnderlying, TValue>(IReadOnlyDictionary<TKey, TValue> dictionary) :
            ImmutableDictionaryDebuggerProxy<TKey, TValue>(dictionary)
            where TKey : notnull;
    }
}
