// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
                typeof(T) == typeof(ushort) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(ushort)) ? new UInt16PerfectHashSet<T, ushort>(values) :
                typeof(T) == typeof(short) || (typeof(T).IsEnum && typeof(T).GetEnumUnderlyingType() == typeof(short)) ? new UInt16PerfectHashSet<T, short>(values) :
                typeof(T) == typeof(char) ? new UInt16PerfectHashSet<T, char>(values) :
                null;
        }

        [DebuggerTypeProxy(typeof(DebuggerProxy<,>))]
        private sealed class UInt16PerfectHashSet<T, TUnderlying> : FrozenSetInternalBase<T, UInt16PerfectHashSet<T, TUnderlying>.GSW>
            where TUnderlying : unmanaged, IBinaryInteger<TUnderlying>
        {
            private readonly uint _multiplier;
            private readonly char[] _hashEntries;
            private readonly int _count;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static char ToChar(T value)
            {
                if (typeof(TUnderlying) == typeof(ushort)) return (char)(ushort)(object)value!;
                if (typeof(TUnderlying) == typeof(short)) return (char)(ushort)(short)(object)value!;

                Debug.Assert(typeof(T) == typeof(char));
                return (char)(object)value!;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static T FromChar(char value)
            {
                if (typeof(TUnderlying) == typeof(ushort)) return (T)(object)(ushort)value;
                if (typeof(TUnderlying) == typeof(short)) return (T)(object)(short)value;

                Debug.Assert(typeof(T) == typeof(char));
                return (T)(object)value!;
            }

            public unsafe UInt16PerfectHashSet(ReadOnlySpan<T> values) : base(EqualityComparer<T>.Default)
            {
                Debug.Assert(!values.IsEmpty);
                Debug.Assert(sizeof(T) == sizeof(char));

                ReadOnlySpan<char> charValues = MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.As<T, char>(ref MemoryMarshal.GetReference(values)),
                    values.Length);

                int max = 0;
                foreach (char c in charValues)
                {
                    max = Math.Max(max, c);
                }

                _count = charValues.Length;

                PerfectHashCharLookup.Initialize(charValues, max, out _multiplier, out _hashEntries);
            }

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
                            items[count++] = FromChar(c);
                        }

                        Debug.Assert(count == items.Length);
                        return field ??= items;
                    }
                }
            }

            private protected override Enumerator GetEnumeratorCore() => new Enumerator(ItemsCore);

            private protected override int CountCore => _count;

            private protected override bool ContainsCore(T item) => PerfectHashCharLookup.Contains(_hashEntries, _multiplier, ToChar(item));

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
            private protected override int FindItemIndex(T item) => PerfectHashCharLookup.IndexOf(_hashEntries, _multiplier, ToChar(item));

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
