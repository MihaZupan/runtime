// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System
{
    internal static class OperatingSystemPolyfills
    {
        extension(OperatingSystem)
        {
            public static bool IsAndroid() => false;
            public static bool IsLinux() => false;
            public static bool IsMacOS() => false;
            public static bool IsWindows() => true;
        }
    }
}
