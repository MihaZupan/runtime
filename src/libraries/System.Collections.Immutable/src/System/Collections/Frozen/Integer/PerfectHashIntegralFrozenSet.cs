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
    /// <summary>Provides a <see cref="FrozenSet{T}"/> for integral keys where we can find a perfect hash relatively efficiently.</summary>
    internal sealed class PerfectHashIntegralFrozenSet
    {
        public static FrozenSet<T>? CreateIfValid<T>(ReadOnlySpan<T> values)
        {
            Debug.Assert(!values.IsEmpty);
            Debug.Assert(typeof(T) != typeof(byte) && typeof(T) != typeof(sbyte), "These values should have been handled by BitmapIntegralFrozenSet.");

            return
                typeof(T) == typeof(ushort) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(ushort)) ? CreateIfValid<T, ushort>(values) :
                typeof(T) == typeof(short) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(short)) ? CreateIfValid<T, short>(values) :
                typeof(T) == typeof(char) ? CreateIfValid<T, char>(values) :
                typeof(T) == typeof(uint) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(uint)) ? CreateIfValid<T, uint>(values) :
                typeof(T) == typeof(int) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(int)) ? CreateIfValid<T, int>(values) :
                typeof(T) == typeof(ulong) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(ulong)) ? CreateIfValid<T, ulong>(values) :
                typeof(T) == typeof(long) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(long)) ? CreateIfValid<T, long>(values) :
                null;
        }

        private static FrozenSet<T>? CreateIfValid<T, TUnderlying>(ReadOnlySpan<T> values)
            where TUnderlying : unmanaged, IBinaryInteger<TUnderlying>
        {
            char[] charKeys = ArrayPool<char>.Shared.Rent(values.Length);
            int maxKey = int.MinValue;

            for (int i = 0; i < values.Length; i++)
            {
                T value = values[i];

                if (!IsInUInt16Range<T, TUnderlying>(value))
                {
                    ArrayPool<char>.Shared.Return(charKeys);
                    return null;
                }

                char c = ToChar<T, TUnderlying>(value);
                charKeys[i] = c;
                maxKey = Math.Max(maxKey, c);
            }

            PerfectHashCharLookup.Initialize(charKeys.AsSpan(0, values.Length), maxKey, out uint multiplier, out char[] hashEntries);

            ArrayPool<char>.Shared.Return(charKeys);

            return new UInt16PerfectHashSet<T, TUnderlying>(multiplier, hashEntries, values.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsInUInt16Range<T, TUnderlying>(T value)
            where TUnderlying : unmanaged, IBinaryInteger<TUnderlying>
        {
            if (typeof(TUnderlying) == typeof(uint)) return (uint)(object)value! <= ushort.MaxValue;
            if (typeof(TUnderlying) == typeof(int)) return (uint)(int)(object)value! <= ushort.MaxValue;
            if (typeof(TUnderlying) == typeof(ulong)) return (ulong)(object)value! <= ushort.MaxValue;
            if (typeof(TUnderlying) == typeof(long)) return (ulong)(long)(object)value! <= ushort.MaxValue;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static char ToChar<T, TUnderlying>(T value)
            where TUnderlying : unmanaged, IBinaryInteger<TUnderlying>
        {
            Debug.Assert(IsInUInt16Range<T, TUnderlying>(value));

            if (typeof(TUnderlying) == typeof(ushort)) return (char)(ushort)(object)value!;
            if (typeof(TUnderlying) == typeof(short)) return (char)(ushort)(short)(object)value!;
            if (typeof(TUnderlying) == typeof(uint)) return (char)(uint)(object)value!;
            if (typeof(TUnderlying) == typeof(int)) return (char)(uint)(int)(object)value!;
            if (typeof(TUnderlying) == typeof(ulong)) return (char)(ulong)(object)value!;
            if (typeof(TUnderlying) == typeof(long)) return (char)(ulong)(long)(object)value!;

            Debug.Assert(typeof(T) == typeof(char));
            return (char)(object)value!;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T FromChar<T, TUnderlying>(char value)
            where TUnderlying : unmanaged, IBinaryInteger<TUnderlying>
        {
            if (typeof(TUnderlying) == typeof(ushort)) return (T)(object)(ushort)value;
            if (typeof(TUnderlying) == typeof(short)) return (T)(object)(short)value;
            if (typeof(TUnderlying) == typeof(uint)) return (T)(object)(uint)value;
            if (typeof(TUnderlying) == typeof(int)) return (T)(object)(int)value;
            if (typeof(TUnderlying) == typeof(ulong)) return (T)(object)(ulong)value;
            if (typeof(TUnderlying) == typeof(long)) return (T)(object)(long)value;

            Debug.Assert(typeof(T) == typeof(char));
            return (T)(object)value;
        }

        [DebuggerTypeProxy(typeof(DebuggerProxy<,>))]
        private sealed class UInt16PerfectHashSet<T, TUnderlying>(uint multiplier, char[] hashEntries, int count) :
            FrozenSetInternalBase<T, UInt16PerfectHashSet<T, TUnderlying>.GSW>(EqualityComparer<T>.Default)
            where TUnderlying : unmanaged, IBinaryInteger<TUnderlying>
        {
            private readonly uint _multiplier = multiplier;
            private readonly char[] _hashEntries = hashEntries;
            private readonly int _count = count;

            [field: MaybeNull]
            private protected override T[] ItemsCore
            {
                get
                {
                    return field ?? AllocateItemsArray();

                    T[] AllocateItemsArray()
                    {
                        var set = new HashSet<char>(_hashEntries);

                        T[] items = new T[set.Count];
                        int count = 0;

                        foreach (char c in set)
                        {
                            items[count++] = FromChar<T, TUnderlying>(c);
                        }

                        Debug.Assert(count == items.Length);
                        return field ??= items;
                    }
                }
            }

            private protected override Enumerator GetEnumeratorCore() => new Enumerator(ItemsCore);

            private protected override int CountCore => _count;

            private protected override bool ContainsCore(T item) =>
                IsInUInt16Range<T, TUnderlying>(item) &&
                PerfectHashCharLookup.Contains(_hashEntries, _multiplier, ToChar<T, TUnderlying>(item));

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

            /// <inheritdoc />
            /// <remarks>
            /// This is an internal helper where results are not exposed to the user.
            /// The returned index does not have to correspond to the value in the <see cref="ItemsCore"/> array.
            /// In this case, calculating the real index would be costly, so we return the offset into <see cref="_hashEntries"/> instead.
            /// </remarks>
            private protected override int FindItemIndex(T item) =>
                !IsInUInt16Range<T, TUnderlying>(item) ? -1 :
                PerfectHashCharLookup.IndexOf(_hashEntries, _multiplier, ToChar<T, TUnderlying>(item));

            /// <inheritdoc />
            /// <remarks>
            /// We're overriding this method to account for the fact that the indexes returned by <see cref="FindItemIndex(T)"/>
            /// are based on <see cref="_hashEntries"/> instead of <see cref="ItemsCore"/>.
            /// </remarks>
            private protected override KeyValuePair<int, int> CheckUniqueAndUnfoundElements(IEnumerable<T> other, bool returnIfUnfound) =>
                CheckUniqueAndUnfoundElements(other, returnIfUnfound, _hashEntries.Length);

            internal struct GSW : IGenericSpecializedWrapper
            {
                private UInt16PerfectHashSet<T, TUnderlying> _set;
                public void Store(FrozenSet<T> set) => _set = (UInt16PerfectHashSet<T, TUnderlying>)set;

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
