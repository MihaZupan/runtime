// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System
{
    internal static class EnvironmentEx
    {
        public static long TickCount64
        {
            get
            {
                return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            }
        }
    }
}
