// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using Xunit;

public class ModToZero
{
    [Fact]
    public static void TestEntryPoint()
    {
        for (int value = -1024; value <= 1024; value++)
        {
            TestValues(unchecked((ulong)value));
        }

        ulong[] divisors =
        {
            2, 3, 4, 6, 7, 10, 12, 42, 255, 256, 65535, 65536,
            0x40000000, 0x40000001, 0x60000000, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xC0000000, uint.MaxValue,
            0x4000000000000000, 0x4000000000000001, 0x6000000000000000, 0x7FFFFFFFFFFFFFFF,
            0x8000000000000000, 0x8000000000000001, 0xC000000000000000, ulong.MaxValue
        };

        foreach (ulong divisor in divisors)
        {
            foreach (ulong boundary in new ulong[] { 0, int.MaxValue, 0x80000000, uint.MaxValue, long.MaxValue, 0x8000000000000000, ulong.MaxValue })
            {
                ulong multiple = boundary / divisor * divisor;
                for (int offset = -2; offset <= 2; offset++)
                {
                    TestValues(unchecked(multiple + (ulong)offset));
                    TestValues(unchecked(0 - multiple + (ulong)offset));
                }
            }
        }

        Random random = new Random(42);
        for (int i = 0; i < 1000; i++)
        {
            TestValues(unchecked((ulong)random.NextInt64() << 1) | (uint)random.Next(2));
        }

        int calls = 0;
        Assert.True(SideEffect(ref calls));
        Assert.Equal(1, calls);
        Assert.Throws<DivideByZeroException>(() => ModZero(42));
        Assert.Throws<DivideByZeroException>(() => ModZeroUInt(42));
        Assert.Throws<DivideByZeroException>(() => ModZeroLong(42));
        Assert.Throws<DivideByZeroException>(() => ModZeroULong(42));
        Assert.Throws<OverflowException>(() => ModMinusOne(int.MinValue));
        Assert.Throws<OverflowException>(() => ModMinusOneLong(long.MinValue));
    }

    private static void TestValues(ulong value)
    {
        TestInt(unchecked((int)value));
        TestUInt(unchecked((uint)value));
        TestLong(unchecked((long)value));
        TestULong(value);
        Assert.Equal(unchecked((int)value) % RemainderDivisor(42) == 0, Int42(unchecked((int)value)));
        Assert.Equal(unchecked((uint)value) % (uint)RemainderDivisor(42) != 0, UInt42(unchecked((uint)value)));
        Assert.Equal(unchecked((long)value) % RemainderDivisor(42) == 0, Long42(unchecked((long)value)));
        Assert.Equal(value % (ulong)RemainderDivisor(42) != 0, ULong42(value));
        Assert.Equal(unchecked((uint)value) % (uint)RemainderDivisor(42) != 0 ? 1 : 2, UInt42Branch(unchecked((uint)value)));
        Assert.Equal(value % (ulong)RemainderDivisor(42) == 0 ? 1 : 2, ULong42Branch(value));
        Assert.Equal(unchecked((nint)value) % RemainderDivisor(42) == 0, Native42(unchecked((nint)value)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestInt(int value)
    {
        CheckInt(value, 1);
        CheckInt(value, 2);
        CheckInt(value, 3);
        CheckInt(value, 4);
        CheckInt(value, 6);
        CheckInt(value, 7);
        CheckInt(value, 10);
        CheckInt(value, 12);
        CheckInt(value, 42);
        CheckInt(value, 255);
        CheckInt(value, 256);
        CheckInt(value, 65535);
        CheckInt(value, 65536);
        CheckInt(value, 0x40000000);
        CheckInt(value, 0x40000001);
        CheckInt(value, 0x60000000);
        CheckInt(value, int.MaxValue);
        CheckInt(value, -2);
        CheckInt(value, -3);
        CheckInt(value, -4);
        CheckInt(value, -6);
        CheckInt(value, -7);
        CheckInt(value, -42);
        CheckInt(value, -0x40000001);
        CheckInt(value, -0x60000000);
        CheckInt(value, -int.MaxValue);
        CheckInt(value, int.MinValue);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestUInt(uint value)
    {
        CheckUInt(value, 1);
        CheckUInt(value, 2);
        CheckUInt(value, 3);
        CheckUInt(value, 4);
        CheckUInt(value, 6);
        CheckUInt(value, 7);
        CheckUInt(value, 10);
        CheckUInt(value, 12);
        CheckUInt(value, 42);
        CheckUInt(value, 255);
        CheckUInt(value, 256);
        CheckUInt(value, 65535);
        CheckUInt(value, 65536);
        CheckUInt(value, 0x40000000);
        CheckUInt(value, 0x40000001);
        CheckUInt(value, 0x60000000);
        CheckUInt(value, int.MaxValue);
        CheckUInt(value, 0x80000000);
        CheckUInt(value, 0x80000001);
        CheckUInt(value, 0xC0000000);
        CheckUInt(value, uint.MaxValue);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestLong(long value)
    {
        CheckLong(value, 1);
        CheckLong(value, 2);
        CheckLong(value, 3);
        CheckLong(value, 4);
        CheckLong(value, 6);
        CheckLong(value, 7);
        CheckLong(value, 10);
        CheckLong(value, 12);
        CheckLong(value, 42);
        CheckLong(value, 255);
        CheckLong(value, 256);
        CheckLong(value, 65535);
        CheckLong(value, 65536);
        CheckLong(value, 0x4000000000000000);
        CheckLong(value, 0x4000000000000001);
        CheckLong(value, 0x6000000000000000);
        CheckLong(value, long.MaxValue);
        CheckLong(value, -2);
        CheckLong(value, -3);
        CheckLong(value, -4);
        CheckLong(value, -6);
        CheckLong(value, -7);
        CheckLong(value, -42);
        CheckLong(value, -0x4000000000000001);
        CheckLong(value, -0x6000000000000000);
        CheckLong(value, -long.MaxValue);
        CheckLong(value, long.MinValue);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestULong(ulong value)
    {
        CheckULong(value, 1);
        CheckULong(value, 2);
        CheckULong(value, 3);
        CheckULong(value, 4);
        CheckULong(value, 6);
        CheckULong(value, 7);
        CheckULong(value, 10);
        CheckULong(value, 12);
        CheckULong(value, 42);
        CheckULong(value, 255);
        CheckULong(value, 256);
        CheckULong(value, 65535);
        CheckULong(value, 65536);
        CheckULong(value, 0x4000000000000000);
        CheckULong(value, 0x4000000000000001);
        CheckULong(value, 0x6000000000000000);
        CheckULong(value, long.MaxValue);
        CheckULong(value, 0x8000000000000000);
        CheckULong(value, 0x8000000000000001);
        CheckULong(value, 0xC000000000000000);
        CheckULong(value, ulong.MaxValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckInt(int value, int divisor)
    {
        int remainder = RemainderInt(value, divisor);
        Assert.Equal(remainder == 0, value % divisor == 0);
        Assert.Equal(remainder != 0, 0 != value % divisor);
        if (value % divisor == 0)
        {
            Assert.Equal(0, remainder);
        }
        else
        {
            Assert.NotEqual(0, remainder);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckUInt(uint value, uint divisor)
    {
        uint remainder = RemainderUInt(value, divisor);
        Assert.Equal(remainder == 0, 0 == value % divisor);
        Assert.Equal(remainder != 0, value % divisor != 0);
        if (value % divisor == 0)
        {
            Assert.Equal(0u, remainder);
        }
        else
        {
            Assert.NotEqual(0u, remainder);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckLong(long value, long divisor)
    {
        long remainder = RemainderLong(value, divisor);
        Assert.Equal(remainder == 0, value % divisor == 0);
        Assert.Equal(remainder != 0, 0 != value % divisor);
        if (value % divisor != 0)
        {
            Assert.NotEqual(0L, remainder);
        }
        else
        {
            Assert.Equal(0L, remainder);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckULong(ulong value, ulong divisor)
    {
        ulong remainder = RemainderULong(value, divisor);
        Assert.Equal(remainder == 0, 0 == value % divisor);
        Assert.Equal(remainder != 0, value % divisor != 0);
        if (value % divisor != 0)
        {
            Assert.NotEqual(0ul, remainder);
        }
        else
        {
            Assert.Equal(0ul, remainder);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RemainderInt(int value, int divisor) => value % divisor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint RemainderUInt(uint value, uint divisor) => value % divisor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RemainderLong(long value, long divisor) => value % divisor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong RemainderULong(ulong value, ulong divisor) => value % divisor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RemainderDivisor(int divisor) => divisor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Int42(int value)
    {
        // X64: imul
        // X64-NOT: imul
        // X64: ror
        // X64: cmp
        return value % 42 == 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool UInt42(uint value)
    {
        // X64: imul
        // X64-NOT: imul
        // X64: ror
        // X64: cmp
        return value % 42 != 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Long42(long value)
    {
        // X64: imul
        // X64-NOT: imul
        // X64: ror
        // X64: cmp
        return value % 42 == 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ULong42(ulong value)
    {
        // X64: imul
        // X64-NOT: imul
        // X64: ror
        // X64: cmp
        return value % 42 != 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int UInt42Branch(uint value)
    {
        // X64: imul
        // X64-NOT: imul
        // X64: ror
        // X64: cmp
        if (value % 42 != 0)
        {
            return RemainderDivisor(1);
        }

        return RemainderDivisor(2);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ULong42Branch(ulong value)
    {
        // X64: imul
        // X64-NOT: imul
        // X64: ror
        // X64: cmp
        if (value % 42 == 0)
        {
            return RemainderDivisor(1);
        }

        return RemainderDivisor(2);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Native42(nint value) => value % 42 == 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SideEffect(ref int calls) => GetValue(ref calls) % 42 == 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int GetValue(ref int calls)
    {
        calls++;
        return 42;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ModZero(int value) => value % 0 == 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ModZeroUInt(uint value) => value % 0 != 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ModZeroLong(long value) => value % 0 != 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ModZeroULong(ulong value) => value % 0 == 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ModMinusOne(int value) => value % -1 == 0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ModMinusOneLong(long value) => value % -1 != 0;
}
