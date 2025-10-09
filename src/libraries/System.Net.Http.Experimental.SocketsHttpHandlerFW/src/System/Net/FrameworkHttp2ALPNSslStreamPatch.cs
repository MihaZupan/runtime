// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using HarmonyLib;
using System.Reflection;
using System.Threading;

namespace System.Net.Http
{
    // Adds HTTP/2 ALPN support to SslStream on .NET Framework.
    internal static class FrameworkHttp2ALPNSslStreamPatch
    {
        private static readonly Type s_securityBufferType = AccessTools.TypeByName("System.Net.SecurityBuffer");
        private static readonly Type s_bufferTypeType = AccessTools.TypeByName("System.Net.BufferType");
#pragma warning disable IL2080 // 'this' argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The source field does not have matching annotations.
        private static readonly ConstructorInfo s_securityBufferCtor = s_securityBufferType.GetConstructor(new Type[] { typeof(byte[]), s_bufferTypeType });
#pragma warning restore IL2080
        private static readonly AsyncLocal<bool> s_usePatch = new();

        public static void Apply()
        {
            var harmony = new Harmony(nameof(FrameworkHttp2ALPNSslStreamPatch));
            var original = AccessTools.Method("System.Net.SafeDeleteContext:InitializeSecurityContext");
            var prefix = SymbolExtensions.GetMethodInfo((object[] args) => Prefix(args));
            harmony.Patch(original, new HarmonyMethod(prefix));
        }

        public static void UseInCurrentContext()
        {
            s_usePatch.Value = true;
        }

        private static void Prefix(object[] __args)
        {
            if (s_usePatch.Value && __args[2] is null && __args[6] is null && __args[7] is null)
            {
                __args[6] = s_securityBufferCtor.Invoke(new object[]
                {
                    new byte[] { 9, 0, 0, 0, 2, 0, 0, 0, 3, 0, 2, (byte)'h', (byte)'2' },
                    Enum.ToObject(s_bufferTypeType, 18)
                });
            }
        }
    }
}
