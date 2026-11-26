// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Numerics;

namespace System
{
    public static class AdditionOperatorsHelper<TSelf, TOther, TResult>
        where TSelf : IAdditionOperators<TSelf, TOther, TResult>
    {
        public static TResult op_Addition(TSelf left, TOther right) => left + right;

        public static TResult op_CheckedAddition(TSelf left, TOther right) => checked(left + right);
    }

    public static class AdditiveIdentityHelper<TSelf, TResult>
        where TSelf : IAdditiveIdentity<TSelf, TResult>
    {
        public static TResult AdditiveIdentity => TSelf.AdditiveIdentity;
    }

    public static class BinaryIntegerHelper<TSelf>
        where TSelf : IBinaryInteger<TSelf>
    {
        public static (TSelf Quotient, TSelf Remainder) DivRem(TSelf left, TSelf right) => TSelf.DivRem(left, right);

        public static (TSelf Quotient, TSelf Remainder) DivRem(TSelf left, TSelf right, DivisionRounding mode) => TSelf.DivRem(left, right, mode);

        public static TSelf Divide(TSelf left, TSelf right, DivisionRounding mode) => TSelf.Divide(left, right, mode);

        public static TSelf Remainder(TSelf left, TSelf right, DivisionRounding mode) => TSelf.Remainder(left, right, mode);

        public static TSelf DivideRoundingCorrection(TSelf left, TSelf right, DivisionRounding mode) => mode switch
        {
            _ when ((left % right) == TSelf.Zero) => TSelf.Zero,
            DivisionRounding.Truncate => TSelf.Zero,
            DivisionRounding.Floor when ((right > TSelf.Zero) ^ (left > TSelf.Zero)) => -TSelf.One,
            DivisionRounding.Ceiling when ((right > TSelf.Zero) ^ (left < TSelf.Zero)) => TSelf.One,
            DivisionRounding.AwayFromZero => ((right > TSelf.Zero) ^ (left > TSelf.Zero)) ? -TSelf.One : TSelf.One,
            DivisionRounding.Euclidean when (left < TSelf.Zero) => (right > TSelf.Zero) ? -TSelf.One : TSelf.One,
            _ => TSelf.Zero,
        };

        public static TSelf RemainderRoundingCorrection(TSelf left, TSelf right, DivisionRounding mode) => mode switch
        {
            _ when ((left % right) == TSelf.Zero) => TSelf.Zero,
            DivisionRounding.Truncate => TSelf.Zero,
            DivisionRounding.Floor when ((right > TSelf.Zero) ^ (left > TSelf.Zero)) => right,
            DivisionRounding.Ceiling when ((right > TSelf.Zero) ^ (left < TSelf.Zero)) => -right,
            DivisionRounding.AwayFromZero => ((right > TSelf.Zero) ^ (left > TSelf.Zero)) ? right : -right,
            DivisionRounding.Euclidean when (left < TSelf.Zero) => (right > TSelf.Zero) ? right : -right,
            _ => TSelf.Zero,
        };

        public static (TSelf Quotient, TSelf Remainder) DivRemExpected(TSelf left, TSelf right, DivisionRounding mode) =>
            ((left / right) + DivideRoundingCorrection(left, right, mode), (left % right) + RemainderRoundingCorrection(left, right, mode));

        public static TSelf DivideExpected(TSelf left, TSelf right, DivisionRounding mode)
            => (left / right) + DivideRoundingCorrection(left, right, mode);

        public static TSelf RemainderExpected(TSelf left, TSelf right, DivisionRounding mode)
            => (left % right) + RemainderRoundingCorrection(left, right, mode);

        public static TSelf LeadingZeroCount(TSelf value) => TSelf.LeadingZeroCount(value);

        public static TSelf Log10(TSelf value) => TSelf.Log10(value);

        public static TSelf PopCount(TSelf value) => TSelf.PopCount(value);

        public static TSelf ReadBigEndian(byte[] source, bool isUnsigned) => TSelf.ReadBigEndian(source, isUnsigned);

        public static TSelf ReadBigEndian(byte[] source, int startIndex, bool isUnsigned) => TSelf.ReadBigEndian(source, startIndex, isUnsigned);

        public static TSelf ReadBigEndian(ReadOnlySpan<byte> source, bool isUnsigned) => TSelf.ReadBigEndian(source, isUnsigned);

        public static TSelf ReadLittleEndian(byte[] source, bool isUnsigned) => TSelf.ReadLittleEndian(source, isUnsigned);

        public static TSelf ReadLittleEndian(byte[] source, int startIndex, bool isUnsigned) => TSelf.ReadLittleEndian(source, startIndex, isUnsigned);

        public static TSelf ReadLittleEndian(ReadOnlySpan<byte> source, bool isUnsigned) => TSelf.ReadLittleEndian(source, isUnsigned);

        public static TSelf RotateLeft(TSelf value, int rotateAmount) => TSelf.RotateLeft(value, rotateAmount);

        public static TSelf RotateRight(TSelf value, int rotateAmount) => TSelf.RotateRight(value, rotateAmount);

        public static TSelf TrailingZeroCount(TSelf value) => TSelf.TrailingZeroCount(value);

        public static bool TryReadBigEndian(ReadOnlySpan<byte> source, bool isUnsigned, out TSelf value) => TSelf.TryReadBigEndian(source, isUnsigned, out value);

        public static bool TryReadLittleEndian(ReadOnlySpan<byte> source, bool isUnsigned, out TSelf value) => TSelf.TryReadLittleEndian(source, isUnsigned, out value);

        public static int GetByteCount(TSelf value) => value.GetByteCount();

        public static int GetShortestBitLength(TSelf value) => value.GetShortestBitLength();

        public static bool TryWriteBigEndian(TSelf value, Span<byte> destination, out int bytesWritten) => value.TryWriteBigEndian(destination, out bytesWritten);

        public static bool TryWriteLittleEndian(TSelf value, Span<byte> destination, out int bytesWritten) => value.TryWriteLittleEndian(destination, out bytesWritten);

        public static int WriteBigEndian(TSelf value, byte[] destination) => value.WriteBigEndian(destination);

        public static int WriteBigEndian(TSelf value, byte[] destination, int startIndex) => value.WriteBigEndian(destination, startIndex);

        public static int WriteBigEndian(TSelf value, Span<byte> destination) => value.WriteBigEndian(destination);

        public static int WriteLittleEndian(TSelf value, byte[] destination) => value.WriteLittleEndian(destination);

        public static int WriteLittleEndian(TSelf value, byte[] destination, int startIndex) => value.WriteLittleEndian(destination, startIndex);

        public static int WriteLittleEndian(TSelf value, Span<byte> destination) => value.WriteLittleEndian(destination);
    }

    public static class BinaryNumberHelper<TSelf>
        where TSelf : IBinaryNumber<TSelf>
    {
        public static TSelf AllBitsSet => TSelf.AllBitsSet;

        public static bool IsPow2(TSelf value) => TSelf.IsPow2(value);

        public static TSelf Log2(TSelf value) => TSelf.Log2(value);
    }

    public static class BitwiseOperatorsHelper<TSelf, TOther, TResult>
        where TSelf : IBitwiseOperators<TSelf, TOther, TResult>
    {
        public static TResult op_BitwiseAnd(TSelf left, TOther right) => left & right;

        public static TResult op_BitwiseOr(TSelf left, TOther right) => left | right;

        public static TResult op_ExclusiveOr(TSelf left, TOther right) => left ^ right;

        public static TResult op_OnesComplement(TSelf value) => ~value;
    }

    public static class ComparisonOperatorsHelper<TSelf, TOther, TResult>
        where TSelf : IComparisonOperators<TSelf, TOther, TResult>
    {
        public static TResult op_GreaterThan(TSelf left, TOther right) => left > right;

        public static TResult op_GreaterThanOrEqual(TSelf left, TOther right) => left >= right;

        public static TResult op_LessThan(TSelf left, TOther right) => left < right;

        public static TResult op_LessThanOrEqual(TSelf left, TOther right) => left <= right;
    }

    public static class DecrementOperatorsHelper<TSelf>
        where TSelf : IDecrementOperators<TSelf>
{
        public static TSelf op_Decrement(TSelf value) => --value;

        public static TSelf op_CheckedDecrement(TSelf value) => checked(--value);
    }

    public static class DivisionOperatorsHelper<TSelf, TOther, TResult>
        where TSelf : IDivisionOperators<TSelf, TOther, TResult>
    {
        public static TResult op_Division(TSelf left, TOther right) => left / right;

        public static TResult op_CheckedDivision(TSelf left, TOther right) => checked(left / right);
    }

    public static class EqualityOperatorsHelper<TSelf, TOther, TResult>
        where TSelf : IEqualityOperators<TSelf, TOther, TResult>
    {
        public static TResult op_Equality(TSelf left, TOther right) => left == right;

        public static TResult op_Inequality(TSelf left, TOther right) => left != right;
    }

    public static class ExponentialFunctionsHelper<TSelf>
        where TSelf : IExponentialFunctions<TSelf>
    {
        public static TSelf Exp(TSelf x) => TSelf.Exp(x);

        public static TSelf ExpM1(TSelf x) => TSelf.ExpM1(x);

        public static TSelf Exp2(TSelf x) => TSelf.Exp2(x);

        public static TSelf Exp2M1(TSelf x) => TSelf.Exp2M1(x);

        public static TSelf Exp10(TSelf x) => TSelf.Exp10(x);

        public static TSelf Exp10M1(TSelf x) => TSelf.Exp10M1(x);
    }

    public static class FloatingPointHelper<TSelf>
        where TSelf : IFloatingPoint<TSelf>
    {
        public static TSelf Ceiling(TSelf x) => TSelf.Ceiling(x);

        public static TInteger ConvertToInteger<TInteger>(TSelf x)
            where TInteger : IBinaryInteger<TInteger> => TSelf.ConvertToInteger<TInteger>(x);

        public static TInteger ConvertToIntegerNative<TInteger>(TSelf x)
            where TInteger : IBinaryInteger<TInteger> => TSelf.ConvertToIntegerNative<TInteger>(x);

        public static TSelf Floor(TSelf x) => TSelf.Floor(x);

        public static TSelf Round(TSelf x) => TSelf.Round(x);

        public static TSelf Round(TSelf x, int digits) => TSelf.Round(x, digits);

        public static TSelf Round(TSelf x, MidpointRounding mode) => TSelf.Round(x, mode);

        public static TSelf Round(TSelf x, int digits, MidpointRounding mode) => TSelf.Round(x, digits, mode);

        public static TSelf Truncate(TSelf x) => TSelf.Truncate(x);

        public static int GetExponentByteCount(TSelf value) => value.GetExponentByteCount();

        public static int GetExponentShortestBitLength(TSelf value) => value.GetExponentShortestBitLength();

        public static int GetSignificandByteCount(TSelf value) => value.GetSignificandByteCount();

        public static int GetSignificandBitLength(TSelf value) => value.GetSignificandBitLength();

        public static bool TryWriteExponentBigEndian(TSelf value, Span<byte> destination, out int bytesWritten) => value.TryWriteExponentBigEndian(destination, out bytesWritten);

        public static bool TryWriteExponentLittleEndian(TSelf value, Span<byte> destination, out int bytesWritten) => value.TryWriteExponentLittleEndian(destination, out bytesWritten);

        public static bool TryWriteSignificandBigEndian(TSelf value, Span<byte> destination, out int bytesWritten) => value.TryWriteSignificandBigEndian(destination, out bytesWritten);

        public static bool TryWriteSignificandLittleEndian(TSelf value, Span<byte> destination, out int bytesWritten) => value.TryWriteSignificandLittleEndian(destination, out bytesWritten);

        public static int WriteExponentBigEndian(TSelf value, byte[] destination) => value.WriteExponentBigEndian(destination);

        public static int WriteExponentBigEndian(TSelf value, byte[] destination, int startIndex) => value.WriteExponentBigEndian(destination, startIndex);

        public static int WriteExponentBigEndian(TSelf value, Span<byte> destination) => value.WriteExponentBigEndian(destination);

        public static int WriteExponentLittleEndian(TSelf value, byte[] destination) => value.WriteExponentLittleEndian(destination);

        public static int WriteExponentLittleEndian(TSelf value, byte[] destination, int startIndex) => value.WriteExponentLittleEndian(destination, startIndex);

        public static int WriteExponentLittleEndian(TSelf value, Span<byte> destination) => value.WriteExponentLittleEndian(destination);

        public static int WriteSignificandBigEndian(TSelf value, byte[] destination) => value.WriteSignificandBigEndian(destination);

        public static int WriteSignificandBigEndian(TSelf value, byte[] destination, int startIndex) => value.WriteSignificandBigEndian(destination, startIndex);

        public static int WriteSignificandBigEndian(TSelf value, Span<byte> destination) => value.WriteSignificandBigEndian(destination);

        public static int WriteSignificandLittleEndian(TSelf value, byte[] destination) => value.WriteSignificandLittleEndian(destination);

        public static int WriteSignificandLittleEndian(TSelf value, byte[] destination, int startIndex) => value.WriteSignificandLittleEndian(destination, startIndex);

        public static int WriteSignificandLittleEndian(TSelf value, Span<byte> destination) => value.WriteSignificandLittleEndian(destination);
    }

    public static class IncrementOperatorsHelper<TSelf>
        where TSelf : IIncrementOperators<TSelf>
    {
        public static TSelf op_Increment(TSelf value) => ++value;

        public static TSelf op_CheckedIncrement(TSelf value) => checked(++value);
    }

    public static class MinMaxValueHelper<TSelf>
        where TSelf : IMinMaxValue<TSelf>
    {
        public static TSelf MaxValue => TSelf.MaxValue;

        public static TSelf MinValue => TSelf.MinValue;
    }

    public static class ModulusOperatorsHelper<TSelf, TOther, TResult>
        where TSelf : IModulusOperators<TSelf, TOther, TResult>
    {
        public static TResult op_Modulus(TSelf left, TOther right) => left % right;
    }

    public static class MultiplicativeIdentityHelper<TSelf, TResult>
        where TSelf : IMultiplicativeIdentity<TSelf, TResult>
    {
        public static TResult MultiplicativeIdentity => TSelf.MultiplicativeIdentity;
    }

    public static class MultiplyOperatorsHelper<TSelf, TOther, TResult>
        where TSelf : IMultiplyOperators<TSelf, TOther, TResult>
    {
        public static TResult op_Multiply(TSelf left, TOther right) => left * right;

        public static TResult op_CheckedMultiply(TSelf left, TOther right) => checked(left * right);
    }

    public static class NumberBaseHelper<TSelf>
        where TSelf : INumberBase<TSelf>
    {
        public static TSelf One => TSelf.One;

        public static int Radix => TSelf.Radix;

        public static TSelf Zero => TSelf.Zero;

        public static TSelf Abs(TSelf value) => TSelf.Abs(value);

        public static TSelf CreateChecked<TOther>(TOther value)
            where TOther : INumberBase<TOther> => TSelf.CreateChecked(value);

        public static TSelf CreateSaturating<TOther>(TOther value)
            where TOther : INumberBase<TOther> => TSelf.CreateSaturating(value);

        public static TSelf CreateTruncating<TOther>(TOther value)
            where TOther : INumberBase<TOther> => TSelf.CreateTruncating(value);

        public static bool IsCanonical(TSelf value) => TSelf.IsCanonical(value);

        public static bool IsComplexNumber(TSelf value) => TSelf.IsComplexNumber(value);

        public static bool IsEvenInteger(TSelf value) => TSelf.IsEvenInteger(value);

        public static bool IsFinite(TSelf value) => TSelf.IsFinite(value);

        public static bool IsImaginaryNumber(TSelf value) => TSelf.IsImaginaryNumber(value);

        public static bool IsInfinity(TSelf value) => TSelf.IsInfinity(value);

        public static bool IsInteger(TSelf value) => TSelf.IsInteger(value);

        public static bool IsNaN(TSelf value) => TSelf.IsNaN(value);

        public static bool IsNegative(TSelf value) => TSelf.IsNegative(value);

        public static bool IsNegativeInfinity(TSelf value) => TSelf.IsNegativeInfinity(value);

        public static bool IsNormal(TSelf value) => TSelf.IsNormal(value);

        public static bool IsOddInteger(TSelf value) => TSelf.IsOddInteger(value);

        public static bool IsPositive(TSelf value) => TSelf.IsPositive(value);

        public static bool IsPositiveInfinity(TSelf value) => TSelf.IsPositiveInfinity(value);

        public static bool IsRealNumber(TSelf value) => TSelf.IsRealNumber(value);

        public static bool IsSubnormal(TSelf value) => TSelf.IsSubnormal(value);

        public static bool IsZero(TSelf value) => TSelf.IsZero(value);

        public static TSelf MaxMagnitude(TSelf x, TSelf y) => TSelf.MaxMagnitude(x, y);

        public static TSelf MaxMagnitudeNumber(TSelf x, TSelf y) => TSelf.MaxMagnitudeNumber(x, y);

        public static TSelf MinMagnitude(TSelf x, TSelf y) => TSelf.MinMagnitude(x, y);

        public static TSelf MinMagnitudeNumber(TSelf x, TSelf y) => TSelf.MinMagnitudeNumber(x, y);

        public static TSelf Parse(string s, NumberStyles style, IFormatProvider provider) => TSelf.Parse(s, style, provider);

        public static TSelf Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider) => TSelf.Parse(s, style, provider);

        public static bool TryParse(string s, NumberStyles style, IFormatProvider provider, out TSelf result) => TSelf.TryParse(s, style, provider, out result);

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out TSelf result) => TSelf.TryParse(s, style, provider, out result);

        public static bool TryParsePartial(string s, NumberStyles style, IFormatProvider provider, out TSelf result, out int charsConsumed) => TSelf.TryParsePartial(s, style, provider, out result, out charsConsumed);

        public static bool TryParsePartial(ReadOnlySpan<byte> utf8Text, NumberStyles style, IFormatProvider provider, out TSelf result, out int bytesConsumed) => TSelf.TryParsePartial(utf8Text, style, provider, out result, out bytesConsumed);

        public static bool TryParsePartial(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider provider, out TSelf result, out int charsConsumed) => TSelf.TryParsePartial(s, style, provider, out result, out charsConsumed);
    }

    public static class NumberHelper<TSelf>
        where TSelf : INumber<TSelf>
    {
        public static TSelf Clamp(TSelf value, TSelf min, TSelf max) => TSelf.Clamp(value, min, max);

        public static TSelf CopySign(TSelf value, TSelf sign) => TSelf.CopySign(value, sign);

        public static TSelf Max(TSelf x, TSelf y) => TSelf.Max(x, y);

        public static TSelf MaxNumber(TSelf x, TSelf y) => TSelf.MaxNumber(x, y);

        public static TSelf Min(TSelf x, TSelf y) => TSelf.Min(x, y);

        public static TSelf MinNumber(TSelf x, TSelf y) => TSelf.MinNumber(x, y);

        public static int Sign(TSelf value) => TSelf.Sign(value);
    }

    public static class ParsableHelper<TSelf>
        where TSelf : IParsable<TSelf>
    {
        public static TSelf Parse(string s, IFormatProvider provider) => TSelf.Parse(s, provider);

        public static bool TryParse(string s, IFormatProvider provider, out TSelf result) => TSelf.TryParse(s, provider, out result);
    }

    public static class ShiftOperatorsHelper<TSelf, TOther, TResult>
        where TSelf : IShiftOperators<TSelf, TOther, TResult>
    {
        public static TResult op_LeftShift(TSelf value, TOther shiftAmount) => value << shiftAmount;

        public static TResult op_RightShift(TSelf value, TOther shiftAmount) => value >> shiftAmount;

        public static TResult op_UnsignedRightShift(TSelf value, TOther shiftAmount) => value >>> shiftAmount;
    }

    public static class SignedNumberHelper<TSelf>
        where TSelf : ISignedNumber<TSelf>
    {
        public static TSelf NegativeOne => TSelf.NegativeOne;
    }

    public static class SpanParsableHelper<TSelf>
        where TSelf : ISpanParsable<TSelf>
    {
        public static TSelf Parse(ReadOnlySpan<char> s, IFormatProvider provider) => TSelf.Parse(s, provider);

        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider provider, out TSelf result) => TSelf.TryParse(s, provider, out result);
    }

    public static class Utf8SpanParsableHelper<TSelf>
        where TSelf : IUtf8SpanParsable<TSelf>
    {
        public static TSelf Parse(ReadOnlySpan<byte> s, IFormatProvider provider) => TSelf.Parse(s, provider);

        public static bool TryParse(ReadOnlySpan<byte> s, IFormatProvider provider, out TSelf result) => TSelf.TryParse(s, provider, out result);
    }

    public static class SubtractionOperatorsHelper<TSelf, TOther, TResult>
        where TSelf : ISubtractionOperators<TSelf, TOther, TResult>
    {
        public static TResult op_Subtraction(TSelf left, TOther right) => left - right;

        public static TResult op_CheckedSubtraction(TSelf left, TOther right) => checked(left - right);
    }

    public static class UnaryNegationOperatorsHelper<TSelf, TResult>
        where TSelf : IUnaryNegationOperators<TSelf, TResult>
    {
        public static TResult op_UnaryNegation(TSelf value) => -value;

        public static TResult op_CheckedUnaryNegation(TSelf value) => checked(-value);
    }

    public static class UnaryPlusOperatorsHelper<TSelf, TResult>
        where TSelf : IUnaryPlusOperators<TSelf, TResult>
    {
        public static TResult op_UnaryPlus(TSelf value) => +value;
    }
}
