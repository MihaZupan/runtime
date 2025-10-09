// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System
{
    internal static class ArgumentOutOfRangeExceptionEx
    {
        public static void ThrowIfNegativeOrZero(int value)
        {
            if (value <= 0)
            {
                Throw();
            }

            static void Throw() => throw new ArgumentOutOfRangeException("Value must be greater than zero.");
        }

        public static void ThrowIfNegative(int value)
        {
            if (value < 0)
            {
                Throw();
            }

            static void Throw() => throw new ArgumentOutOfRangeException("Value must be non-negative.");
        }
    }

    internal static class ObjectDisposedExceptionEx
    {
        public static void ThrowIf(bool condition, object obj)
        {
            if (condition)
            {
                Throw(obj);
            }

            static void Throw(object obj) => throw new ObjectDisposedException(obj.GetType().FullName);
        }
    }
}
