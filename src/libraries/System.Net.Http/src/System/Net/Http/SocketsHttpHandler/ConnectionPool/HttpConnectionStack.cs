// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Threading;
using System.Collections.Concurrent;

namespace System.Net.Http
{
    /// <summary>
    /// A <see cref="ConcurrentStack{T}"/>-like collection, specialized for entries that can store
    /// extra state, allowing us to avoid allocating on every <see cref="Push(HttpConnection)"/>.
    /// </summary>
    internal struct HttpConnectionStack
    {
        /// <summary>
        /// Each entry is assigned a fixed index when it is created. It is bound to a single connection
        /// for its lifetime, and that connection stores a reference to it (<see cref="HttpConnection.ConnectionStackEntry"/>).
        /// <para>The type serves as a reusable node in the linked list that forms the stack.</para>
        /// <para>It is explicitly a separate type (instead of just inline data on the connection object)
        /// to allow for thread-safe modifications to its <see cref="NextIndex"/>/<see cref="StrongRef"/>
        /// even while the <see cref="_entries"/> array is being resized.</para>
        /// </summary>
        internal sealed class Entry(int index)
        {
            public ulong PackedId => (uint)Index | ((ulong)PushCount << 32);

            /// <summary>The index into the <see cref="_entries"/> array where this <see cref="Entry"/> is stored.</summary>
            public readonly int Index = index;

            public uint PushCount;

            /// <summary>The index of the next <see cref="Entry"/> in the linked list.</summary>
            public int NextIndex;

            /// <summary>This is set on Push and cleared after Pop to avoid rooting active connections on the pool.</summary>
            public HttpConnection? StrongRef;
        }

#if DEBUG
        private readonly ConcurrentDictionary<HttpConnection, bool> _debugContents = new();
        public readonly int DebugCount => _debugContents.Count;
        public readonly bool DebugContains(HttpConnection item) => _debugContents.ContainsKey(item);
#endif

        /// <summary>
        /// Contains the current head index in the lower half and the push count the upper half. -1 for "empty".
        /// <para>This is working around the lack of a 16-byte CAS by storing a 32-bit offset
        /// into the <see cref="_entries"/> array instead of a full 64-bit pointer.</para>
        /// </summary>
        private ulong _head;

        /// <summary>Stores the list of indexes that are free to be assigned to new connections.</summary>
        private readonly Queue<int> _freeQueue;
        private Entry?[] _entries;

        public HttpConnectionStack()
        {
            _head = unchecked((uint)-1);
            _freeQueue = new Queue<int>();
            _entries = [];
        }

        public void Register(HttpConnection connection)
        {
            Debug.Assert(connection.ConnectionStackEntry is null);

            lock (_freeQueue)
            {
                if (_freeQueue.Count == 0)
                {
                    int count = _entries.Length;
                    Array.Resize(ref _entries, Math.Max(4, count * 2));
                    for (int i = count; i < _entries.Length; i++)
                    {
                        _freeQueue.Enqueue(i);
                    }
                }

                int index = _freeQueue.Dequeue();
                connection.ConnectionStackEntry = _entries[index] ??= new Entry(index);
            }
        }

        public readonly void Unregister(HttpConnection connection)
        {
            lock (_freeQueue)
            {
                Debug.Assert(connection.ConnectionStackEntry is not null);
                Debug.Assert(!_freeQueue.Contains(connection.ConnectionStackEntry.Index));

                _freeQueue.Enqueue(connection.ConnectionStackEntry.Index);
            }
        }

        public void Push(HttpConnection connection)
        {
            Debug.Assert(connection.ConnectionStackEntry is not null);
            Debug.Assert(connection.ConnectionStackEntry.StrongRef is null);
#if DEBUG
            Debug.Assert(_debugContents.TryAdd(connection, true));
#endif

            Entry entry = connection.ConnectionStackEntry;
            entry.StrongRef = connection;
            entry.PushCount++;

            ulong id = entry.PackedId;

            while (true)
            {
                ulong head = Volatile.Read(ref _head);
                entry.NextIndex = (int)head;

                if (Interlocked.CompareExchange(ref _head, id, head) == head)
                {
                    break;
                }
            }
        }

        public bool TryPop([NotNullWhen(true)] out HttpConnection? connection)
        {
            Entry?[] entries = _entries;

            while (true)
            {
                ulong head = Volatile.Read(ref _head);
                int index = (int)head;

                if ((uint)index >= entries.Length)
                {
                    connection = null;
                    return false;
                }

                Entry? result = entries[index];
                Debug.Assert(result is not null);

                int nextIndex = result.NextIndex;
                Debug.Assert(nextIndex == -1 || entries[nextIndex] is not null);

                ulong newHead = (uint)nextIndex < (uint)entries.Length
                    ? entries[nextIndex]!.PackedId
                    : unchecked((uint)-1);

                if (Interlocked.CompareExchange(ref _head, newHead, head) == head)
                {
                    Debug.Assert(result.StrongRef is not null);
#if DEBUG
                    Debug.Assert(_debugContents.Remove(result.StrongRef, out _));
#endif

                    connection = result.StrongRef;
                    result.StrongRef = null;
                    return true;
                }
            }
        }
    }
}
