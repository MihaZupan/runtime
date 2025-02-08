// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace System.Collections.Frozen
{
    /// <summary>Provides a <see cref="FrozenSet{T}"/> for densely-packed integral keys.</summary>
    internal static class BitmapIntegralFrozenSet
    {
        public static FrozenSet<T>? CreateIfValid<T>(ReadOnlySpan<T> values)
        {
            Debug.Assert(!values.IsEmpty);

            // Int32 and integer types that fit within Int32. This is to minimize difficulty later validating that
            // inputs are in range of int: we can always cast everything here to Int32 without loss of information.
            return
                typeof(T) == typeof(byte) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(byte)) ? CreateIfValid<T, byte>(values) :
                typeof(T) == typeof(sbyte) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(sbyte)) ? CreateIfValid<T, sbyte>(values) :
                typeof(T) == typeof(ushort) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(ushort)) ? CreateIfValid<T, ushort>(values) :
                typeof(T) == typeof(short) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(short)) ? CreateIfValid<T, short>(values) :
                typeof(T) == typeof(char) ? CreateIfValid<T, char>(values) :
                typeof(T) == typeof(int) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(int)) ? CreateIfValid<T, int>(values) :
                null;
        }

        private static FrozenSet<T>? CreateIfValid<T, TUnderlying>(ReadOnlySpan<T> values)
            where TUnderlying : unmanaged, IBinaryInteger<TUnderlying>
        {
            int min = int.MaxValue;
            int max = int.MinValue;

            foreach (T t in values)
            {
                int value = int.CreateTruncating((TUnderlying)(object)t!);
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            // If the set is small enough, use a bitmap. We allow at least 256 bits to ensure byte/sbyte sets always use this implementation.
            long maxAllowedLength = Math.Clamp((long)values.Length * DenseIntegralFrozenDictionary.LengthToCountFactor, 256, Array.MaxLength);
            long length = (long)max - min + 1;

            return length <= maxAllowedLength ? new BitmapImpl<T, TUnderlying>(values, min, max) : null;
        }

        [DebuggerTypeProxy(typeof(DebuggerProxy<,>))]
        private sealed class BitmapImpl<T, TUnderlying> : FrozenSetInternalBase<T, BitmapImpl<T, TUnderlying>.GSW>
            where TUnderlying : unmanaged, IBinaryInteger<TUnderlying>
        {
            private readonly uint[] _bitmap;
            private readonly int _minInclusive;
            private readonly int _count;

            public BitmapImpl(ReadOnlySpan<T> values, int min, int max) : base(EqualityComparer<T>.Default)
            {
                Debug.Assert(!values.IsEmpty);

                _minInclusive = min;
                _count = values.Length;

                long length = (long)max - min + 1;

                _bitmap = new uint[length / 32 + 1];

                foreach (T t in values)
                {
                    int value = int.CreateTruncating((TUnderlying)(object)t!) - min;
                    _bitmap[value >> 5] |= 1u << value;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool Contains(uint[] bitmap, int value)
            {
                uint offset = (uint)(value >> 5);
                return offset < (uint)bitmap.Length && (bitmap[offset] & (1u << value)) != 0;
            }

            /// <summary>Lazily-allocated set to support <see cref="FindItemIndex(T)"/> queries.</summary>
            [field: MaybeNull]
            private Int32FrozenSet HashTable
            {
                get
                {
                    return field ?? AllocateHashTable();

                    Int32FrozenSet AllocateHashTable()
                    {
                        T[] items = ItemsCore;

                        var set = new HashSet<int>(items.Length);

                        foreach (T item in items)
                        {
                            set.Add(int.CreateTruncating((TUnderlying)(object)item!));
                        }

                        Debug.Assert(set.Count == _count);
                        return field ??= new Int32FrozenSet(set);
                    }
                }
            }

            [field: MaybeNull]
            private protected override T[] ItemsCore
            {
                get
                {
                    return field ?? AllocateItemsArray();

                    T[] AllocateItemsArray()
                    {
                        T[] items = new T[_count];
                        int count = 0;

                        uint[] bitmap = _bitmap;

                        for (int i = 0; i < bitmap.Length * 32; i++)
                        {
                            if (Contains(bitmap, i))
                            {
                                items[count++] = (T)(object)TUnderlying.CreateTruncating(i + _minInclusive);
                            }
                        }

                        Debug.Assert(count == _count);
                        return field ??= items;
                    }
                }
            }

            private protected override Enumerator GetEnumeratorCore() => new Enumerator(ItemsCore);

            private protected override int CountCore => _count;

            private protected override bool ContainsCore(T item) => Contains(_bitmap, int.CreateTruncating((TUnderlying)(object)item!) - _minInclusive);

            private protected override bool TryGetValueCore(T equalValue, [MaybeNullWhen(false)] out T actualValue)
            {
                if (ContainsCore(equalValue))
                {
                    actualValue = equalValue;
                    return true;
                }

                actualValue = default;
                return false;
            }

            private protected override int FindItemIndex(T item) => HashTable.FindIndexInternal(int.CreateTruncating((TUnderlying)(object)item!));

            internal struct GSW : IGenericSpecializedWrapper
            {
                private BitmapImpl<T, TUnderlying> _set;
                public void Store(FrozenSet<T> set) => _set = (BitmapImpl<T, TUnderlying>)set;

                public int Count => _set.Count;
                public IEqualityComparer<T> Comparer => _set.Comparer;
                public int FindItemIndex(T item) => _set.FindItemIndex(item);
                public Enumerator GetEnumerator() => _set.GetEnumerator();
            }
        }

        private sealed class DebuggerProxy<T, TUnderlying>(IEnumerable<T> enumerable) :
            ImmutableEnumerableDebuggerProxy<T>(enumerable)
        { }
    }
}
