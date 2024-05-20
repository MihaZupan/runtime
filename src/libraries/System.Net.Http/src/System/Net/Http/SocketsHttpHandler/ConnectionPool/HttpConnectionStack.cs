// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Threading;
using System.Collections.Concurrent;

namespace System.Net.Http
{
    internal struct HttpConnectionStack
    {
#if DEBUG
        private readonly ConcurrentDictionary<HttpConnection, bool> _debugContents = new();

        public readonly int DebugCount =>
            _debugContents.Count;

        public readonly bool DebugContains(HttpConnection item) =>
            _debugContents.ContainsKey(item);
#endif

        /// <summary>
        /// Contains the current head index in the lower half and the next index in the upper half.
        /// -1 for "empty".
        /// </summary>
        private ulong _head;

        // List of all connections on the pool.
        private readonly Queue<int> _freeQueue;
        private HttpConnection?[] _connections;

        public HttpConnectionStack()
        {
            _head = unchecked((uint)-1);
            _freeQueue = new Queue<int>();
            _connections = [];
        }

        public void Register(HttpConnection connection)
        {
            lock (_freeQueue)
            {
                if (_freeQueue.Count == 0)
                {
                    int count = _connections.Length;
                    Array.Resize(ref _connections, Math.Max(4, count * 2));
                    for (int i = count; i < _connections.Length; i++)
                    {
                        _freeQueue.Enqueue(i);
                    }
                }

                connection.ConnectionIndex = _freeQueue.Dequeue();
            }
        }

        public readonly void Unregister(HttpConnection connection)
        {
            lock (_freeQueue)
            {
                Debug.Assert(!_freeQueue.Contains(connection.ConnectionIndex));
                _freeQueue.Enqueue(connection.ConnectionIndex);
            }
        }

        public void Push(HttpConnection connection)
        {
#if DEBUG
            Debug.Assert((uint)connection.ConnectionIndex < (uint)_connections.Length);
            Debug.Assert(_debugContents.TryAdd(connection, true));
            Debug.Assert(_connections[connection.ConnectionIndex] is null);
#endif

            _connections[connection.ConnectionIndex] = connection;

            SpinWait spin = default;

            while (true)
            {
                ulong head = Volatile.Read(ref _head);
                connection.NextConnectionIndex = (int)head;
                ulong newHead = (head << 32) | (uint)connection.ConnectionIndex;

                if (Interlocked.CompareExchange(ref _head, newHead, head) == head)
                {
                    break;
                }

                spin.SpinOnce();
            }
        }

        public bool TryPop([NotNullWhen(true)] out HttpConnection? connection)
        {
            SpinWait spin = default;

            while (true)
            {
                ulong head = Volatile.Read(ref _head);

                int connectionIndex = (int)head;
                HttpConnection?[] connections = Volatile.Read(ref _connections);

                if ((uint)connectionIndex >= connections.Length)
                {
                    connection = null;
                    return false;
                }

                HttpConnection? result = connections[connectionIndex];
                if (result is null)
                {
                    goto Retry;
                }

                int next = result.NextConnectionIndex;
                ulong newHead = (uint)next;

                if ((uint)next < (uint)connections.Length)
                {
                    HttpConnection? newNext = connections[next];
                    if (newNext is null)
                    {
                        goto Retry;
                    }

                    newHead |= (ulong)(uint)newNext.NextConnectionIndex << 32;
                }

                if (Interlocked.CompareExchange(ref _head, newHead, head) == head)
                {
#if DEBUG
                    Debug.Assert(_debugContents.Remove(result, out _));
#endif

                    // We null out the references to avoid rooting connections.
                    connections[connectionIndex] = null;
                    connection = result;
                    return true;
                }

            Retry:
                spin.SpinOnce();
            }
        }
    }
}
