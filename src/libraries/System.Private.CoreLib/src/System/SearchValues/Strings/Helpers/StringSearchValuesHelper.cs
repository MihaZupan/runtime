// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace System.Buffers
{
    // Provides implementations for helpers shared across multiple SearchValues<string> implementations,
    // such as normalizing and matching values under different case sensitivity rules.
    internal static class StringSearchValuesHelper
    {
        [Conditional("DEBUG")]
        public static void ValidateReadPosition(ref char searchSpaceStart, int searchSpaceLength, ref char searchSpace, int offset = 0)
        {
            Debug.Assert(searchSpaceLength >= 0);

            ValidateReadPosition(MemoryMarshal.CreateReadOnlySpan(ref searchSpaceStart, searchSpaceLength), ref searchSpace, offset);
        }

        [Conditional("DEBUG")]
        public static void ValidateReadPosition(ReadOnlySpan<char> span, ref char searchSpace, int offset = 0)
        {
            Debug.Assert(offset >= 0);

            nint currentByteOffset = Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref searchSpace);
            Debug.Assert(currentByteOffset >= 0);
            Debug.Assert((currentByteOffset & 1) == 0);

            int currentOffset = (int)(currentByteOffset / 2);
            int availableLength = span.Length - currentOffset;
            Debug.Assert(offset <= availableLength);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool StartsWith<TCaseSensitivity>(ref char matchStart, int lengthRemaining, string[] candidates)
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            foreach (string candidate in candidates)
            {
                if (StartsWith<TCaseSensitivity>(ref matchStart, lengthRemaining, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool StartsWith<TCaseSensitivity>(ref char matchStart, int lengthRemaining, string candidate)
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            Debug.Assert(lengthRemaining > 0);

            if (lengthRemaining < candidate.Length)
            {
                return false;
            }

            return TCaseSensitivity.Equals(ref matchStart, candidate);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ScalarEquals<TCaseSensitivity>(ref char matchStart, string candidate)
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            for (int i = 0; i < candidate.Length; i++)
            {
                if (TCaseSensitivity.TransformInput(Unsafe.Add(ref matchStart, i)) != candidate[i])
                {
                    return false;
                }
            }

            return true;
        }

        public interface ICaseSensitivity
        {
            static abstract char TransformInput(char input);
            static abstract TVector TransformInput<TVector>(TVector input) where TVector : struct, ISimdVector<TVector, byte>;
            static abstract bool Equals(ref char matchStart, string candidate);
        }

        // Performs no case transformations.
        public readonly struct CaseSensitive : ICaseSensitivity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static char TransformInput(char input) => input;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static TVector TransformInput<TVector>(TVector input)
                where TVector : struct, ISimdVector<TVector, byte> =>
                input;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Equals(ref char matchStart, string candidate) =>
                ScalarEquals<CaseSensitive>(ref matchStart, candidate);
        }

        // Transforms inputs to their uppercase variants with the assumption that all input characters are ASCII letters.
        // These helpers may produce wrong results for other characters, and the callers must account for that.
        public readonly struct CaseInsensitiveAsciiLetters : ICaseSensitivity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static char TransformInput(char input) => (char)(input & ~0x20);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static TVector TransformInput<TVector>(TVector input)
                where TVector : struct, ISimdVector<TVector, byte> =>
                input & TVector.Create(unchecked((byte)~0x20));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Equals(ref char matchStart, string candidate) =>
                ScalarEquals<CaseInsensitiveAsciiLetters>(ref matchStart, candidate);
        }

        // Transforms inputs to their uppercase variants with the assumption that all input characters are ASCII.
        // These helpers may produce wrong results for non-ASCII inputs, and the callers must account for that.
        public readonly struct CaseInsensitiveAscii : ICaseSensitivity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static char TransformInput(char input) => TextInfo.ToUpperAsciiInvariant(input);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static TVector TransformInput<TVector>(TVector input)
                where TVector : struct, ISimdVector<TVector, byte>
            {
                TVector subtraction = TVector.Create(128 + 'a');
                TVector comparison = TVector.Create(128 + 26);
                TVector caseConversion = TVector.Create(0x20);

                TVector offsetInput = input - subtraction;
                TVector matches;

                if (typeof(TVector) == typeof(Vector128<byte>))
                {
                    matches = Unsafe.BitCast<Vector128<sbyte>, TVector>(Vector128.LessThan(
                        Unsafe.BitCast<TVector, Vector128<sbyte>>(offsetInput),
                        Unsafe.BitCast<TVector, Vector128<sbyte>>(subtraction)));
                }
                else if (typeof(TVector) == typeof(Vector256<byte>))
                {
                    matches = Unsafe.BitCast<Vector256<sbyte>, TVector>(Vector256.LessThan(
                        Unsafe.BitCast<TVector, Vector256<sbyte>>(offsetInput),
                        Unsafe.BitCast<TVector, Vector256<sbyte>>(subtraction)));
                }
                else
                {
                    Debug.Assert(typeof(TVector) == typeof(Vector512<byte>));

                    matches = Unsafe.BitCast<Vector512<sbyte>, TVector>(Vector512.LessThan(
                        Unsafe.BitCast<TVector, Vector512<sbyte>>(offsetInput),
                        Unsafe.BitCast<TVector, Vector512<sbyte>>(subtraction)));
                }

                return input ^ (matches & caseConversion);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Equals(ref char matchStart, string candidate) =>
                ScalarEquals<CaseInsensitiveAscii>(ref matchStart, candidate);
        }

        // We can't efficiently map non-ASCII inputs to their Ordinal uppercase variants,
        // so this helper is only used for the verification of the whole input.
        public readonly struct CaseInsensitiveUnicode : ICaseSensitivity
        {
            public static char TransformInput(char input) => throw new UnreachableException();

            public static TVector TransformInput<TVector>(TVector input)
                where TVector : struct, ISimdVector<TVector, byte> =>
                throw new UnreachableException();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Equals(ref char matchStart, string candidate) =>
                Ordinal.EqualsIgnoreCase(ref matchStart, ref candidate.GetRawStringData(), candidate.Length);
        }
    }
}
