// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Threading
{
    internal static class ExecutionContextEx
    {
        public static AsyncFlowControlEx SuppressFlow()
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                return new AsyncFlowControlEx(false);
            }

            ExecutionContext.SuppressFlow();
            return new AsyncFlowControlEx(true);
        }

        public struct AsyncFlowControlEx : IDisposable
        {
            private readonly bool _restoreFlow;

            public AsyncFlowControlEx(bool restoreFlow)
            {
                _restoreFlow = restoreFlow;
            }

            public void Dispose()
            {
                if (_restoreFlow)
                {
                    ExecutionContext.RestoreFlow();
                }
            }
        }
    }
}
