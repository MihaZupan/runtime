// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

namespace System.Collections.Generic
{
    /*============================================================
    **
    ** Class:  LowLevelDictionary<TKey, TValue>
    **
    ** Private version of Dictionary<> for internal System.Private.CoreLib use. This
    ** permits sharing more source between BCL and System.Private.CoreLib (as well as the
    ** fact that Dictionary<> is just a useful class in general.)
    **
    ** This does not strive to implement the full api surface area
    ** (but any portion it does implement should match the real Dictionary<>'s
    ** behavior.)
    **
    ===========================================================*/
    internal class LowLevelDictionary<TKey, TValue> where TKey : IEquatable<TKey>
    {
        private const int DefaultSize = 17;

        public LowLevelDictionary()
            : this(DefaultSize)
        {
        }

        public LowLevelDictionary(int capacity)
        {
            Clear(capacity);
        }

        public int Count
        {
            get
            {
                return _numEntries;
            }
        }

        public TValue this[TKey key]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(key);

                Entry entry = Find(key);
                if (entry == null)
                    throw new KeyNotFoundException();
                return entry.m_value;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(key);

                Entry entry = Find(key);
                if (entry != null)
                    entry.m_value = value;
                else
                    UncheckedAdd(key, value);
            }
        }

        public bool TryGetValue(TKey key, out TValue? value)
        {
            ArgumentNullException.ThrowIfNull(key);

            value = default(TValue);
            Entry entry = Find(key);
            if (entry != null)
            {
                value = entry.m_value;
                return true;
            }
            return false;
        }

        public void Add(TKey key, TValue value)
        {
            ArgumentNullException.ThrowIfNull(key);

            Entry entry = Find(key);
            if (entry != null)
                throw new ArgumentException(SR.Format(SR.Argument_AddingDuplicateWithKey, key));
            UncheckedAdd(key, value);
        }

        public void Clear(int capacity = DefaultSize)
        {
            _buckets = new Entry[capacity];
            _numEntries = 0;
        }

        public bool Remove(TKey key)
        {
            ArgumentNullException.ThrowIfNull(key);

            int bucket = GetBucket(key);
            Entry? prev = null;
            Entry? entry = _buckets[bucket];
            while (entry != null)
            {
                if (key.Equals(entry.m_key))
                {
                    if (prev == null)
                    {
                        _buckets[bucket] = entry.m_next;
                    }
                    else
                    {
                        prev.m_next = entry.m_next;
                    }
                    _numEntries--;
                    return true;
                }

                prev = entry;
                entry = entry.m_next;
            }
            return false;
        }

        internal TValue LookupOrAdd(TKey key, TValue value)
        {
            Entry entry = Find(key);
            if (entry != null)
                return entry.m_value;
            UncheckedAdd(key, value);
            return value;
        }

        private Entry Find(TKey key)
        {
            int bucket = GetBucket(key);
            Entry? entry = _buckets[bucket];
            while (entry != null)
            {
                if (key.Equals(entry.m_key))
                    return entry;

                entry = entry.m_next;
            }
            return null;
        }

        private Entry UncheckedAdd(TKey key, TValue value)
        {
            Entry entry = new Entry();
            entry.m_key = key;
            entry.m_value = value;

            int bucket = GetBucket(key);
            entry.m_next = _buckets[bucket];
            _buckets[bucket] = entry;

            _numEntries++;
            if (_numEntries > (_buckets.Length * 2))
                ExpandBuckets();

            return entry;
        }


        private void ExpandBuckets()
        {
            try
            {
                int newNumBuckets = _buckets.Length * 2 + 1;
                Entry[] newBuckets = new Entry[newNumBuckets];
                for (int i = 0; i < _buckets.Length; i++)
                {
                    Entry? entry = _buckets[i];
                    while (entry != null)
                    {
                        Entry? nextEntry = entry.m_next;

                        int bucket = GetBucket(entry.m_key, newNumBuckets);
                        entry.m_next = newBuckets[bucket];
                        newBuckets[bucket] = entry;

                        entry = nextEntry;
                    }
                }
                _buckets = newBuckets;
            }
            catch (OutOfMemoryException)
            {
            }
        }

        private int GetBucket(TKey key, int numBuckets = 0)
        {
            int h = key.GetHashCode();
            h &= 0x7fffffff;
            return (h % (numBuckets == 0 ? _buckets.Length : numBuckets));
        }

        private sealed class Entry
        {
            public TKey m_key;
            public TValue m_value;
            public Entry? m_next;
        }

        private Entry?[] _buckets;
        private int _numEntries;
    }
}
