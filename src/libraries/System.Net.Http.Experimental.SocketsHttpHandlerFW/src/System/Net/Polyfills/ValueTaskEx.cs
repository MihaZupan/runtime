// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading.Tasks
{
    internal static class ValueTaskEx
    {
        public static ValueTask FromException(Exception exception)
        {
            return new ValueTask(Task.FromException(exception));
        }

        public static ValueTask<T> FromException<T>(Exception exception)
        {
            return new ValueTask<T>(Task.FromException<T>(exception));
        }

        public static ValueTask FromCanceled(CancellationToken cancellationToken)
        {
            return new ValueTask(Task.FromCanceled(cancellationToken));
        }

        public static ValueTask<T> FromCanceled<T>(CancellationToken cancellationToken)
        {
            return new ValueTask<T>(Task.FromCanceled<T>(cancellationToken));
        }
    }
}
