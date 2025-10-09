// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Runtime.ExceptionServices
{
    internal static class ExceptionDispatchInfoEx
    {
        extension (ExceptionDispatchInfo)
        {
            public static Exception SetCurrentStackTrace(Exception ex)
            {
                return ex;
            }
        }
    }
}
