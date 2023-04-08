// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct EightPackedReferences<T> where T : class
    {
        private readonly T? _ref0;
        private readonly T? _ref1;
        private readonly T? _ref2;
        private readonly T? _ref3;
        private readonly T? _ref4;
        private readonly T? _ref5;
        private readonly T? _ref6;
        private readonly T? _ref7;

        public EightPackedReferences(ReadOnlySpan<T> values)
        {
            Debug.Assert(values.Length <= 8, $"Got {values.Length} values");

            for (int i = 0; i < values.Length; i++)
            {
                this[i] = values[i];
            }
        }

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert(index is >= 0 and < 8, $"Should be [0, 7], was {index}");
                Debug.Assert(Unsafe.Add(ref Unsafe.AsRef(in _ref0), index) is not null);

                return Unsafe.Add(ref Unsafe.AsRef(in _ref0), index)!;
            }
            private set
            {
                Unsafe.Add(ref Unsafe.AsRef(in _ref0), index) = value;
            }
        }
    }
}
