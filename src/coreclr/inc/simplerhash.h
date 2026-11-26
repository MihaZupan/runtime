// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifndef _SIMPLERHASHTABLE_H_
#define _SIMPLERHASHTABLE_H_

#include "contract.h"
#include "iallocator.h"

// SimplerHashTable implements a mapping from a Key type to a Value type,
// via a hash table.

// Synchronization is the responsibility of the caller: if a
// SimplerHash is used in a multithreaded context, the table should be
// associated with a lock.

// SimplerHashTable actually takes four template arguments: Key,
// KeyFuncs, Value, and Behavior.  We don't assume that Key has hash or equality
// functions specific names; rather, we assume that KeyFuncs has
// statics methods
//    int GetHashCode(Key)
// and
//    bool Equals(Key, Key)
// and use those.  An
// instantiator can thus make a small "adaptor class" to invoke
// existing instance method hash and/or equality functions.  If the
// implementor of a candidate Key class K understands this convention,
// these static methods can be implemented by K, so that K can be used
// as the actual arguments for the both Key and KeyTrait classes.
//
// The "Behavior" argument provides the following static members:
//
// s_growth_factor_numerator
// s_growth_factor_denominator                  Factor to grow allocation (numerator/denominator).
//
// s_density_factor_numerator
// s_density_factor_denominator                 Maximum occupied density of table before growth
//                                              occurs (num/denom).
//
// s_minimum_allocation                         Minimum table allocation count (size on first growth.)  It is
//                                              probably preferable to call Reallocate on initialization.
//
// NoMemory()                                   Called when the hash table is unable to grow due to potential
//                                              overflow or the lack of a sufficiently large prime.

// Stores info about primes, including the magic number and shift amount needed
// to implement a divide without using the divide instruction
class PrimeInfo
{
public:
    constexpr PrimeInfo() : prime(0), magic(0), shift(0) {}
    constexpr PrimeInfo(unsigned p, unsigned m, unsigned s) : prime(p), magic(m), shift(s) {}
    unsigned prime;
    unsigned magic;
    unsigned shift;
};


// Hash table class definition

template <typename Key, typename KeyFuncs, typename Value, typename Behavior>
class SimplerHashTable
{
public:
    // Forward declaration.
    class KeyIterator;

    // Constructor/destructor.  SHash tables always start out empty, with no
    // allocation overhead.  Call Reallocate to prime with an initial size if
    // desired. Pass NULL as the IAllocator* if you want to use DefaultAllocator
    // (basically, operator new/delete).

    SimplerHashTable(IAllocator* alloc);
    ~SimplerHashTable();

    // operators new/delete when an IAllocator is to be used.
    void * operator new(size_t sz, IAllocator * alloc);
    void * operator new[](size_t sz, IAllocator * alloc);
    void   operator delete(void * p, IAllocator * alloc);
    void   operator delete[](void * p, IAllocator * alloc);

    // If the table contains a mapping for "key", returns "true" and
    // sets "*pVal" to the value to which "key" maps.  Otherwise,
    // returns false, and does not modify "*pVal".
    bool Lookup(Key k, Value* pVal = NULL) const;

    Value *LookupPointer(Key k) const;

    // Causes the table to map "key" to "val".  Returns "true" if
    // "key" had already been mapped by the table, "false" otherwise.
    bool Set(Key k, Value val);

    // Ensures that "key" is not mapped to a value by the "table."
    // Returns "true" iff it had been mapped.
    bool Remove(Key k);

    // Remove all mappings in the table.
    void RemoveAll();

    // Begin and End pointers for iteration over entire table.
    KeyIterator Begin() const;
    KeyIterator End() const;

    // Return the number of elements currently stored in the table
    unsigned GetCount() const;

private:
    // Forward declaration of the linked-list node class.
    struct Node;

    unsigned GetIndexForKey(Key k) const;

    // If the table has a mapping for "k", return the node containing
    // that mapping, else "NULL".
    Node* FindNode(Key k) const;

    // Resizes a hash table for growth.  The new size is computed based
    // on the current population, growth factor, and maximum density factor.
    void Grow();

    // See if it is OK to grow the hash table by one element.  If not, reallocate
    // the hash table.
    void CheckGrowth();

public:
    // Reallocates a hash table to a specific size.  The size must be big enough
    // to hold all elements in the table appropriately.
    //
    // Note that the actual table size must always be a prime number; the number
    // passed in will be upward adjusted if necessary.
    void Reallocate(unsigned newTableSize);

    // For iteration, we use a pattern similar to the STL "forward
    // iterator" pattern.  It basically consists of wrapping an
    // "iteration variable" in an object, and providing pointer-like
    // operators on the iterator. Example usage:
    //
    // for (SimplerHashTable::KeyIterator iter = foo->Begin(), end = foo->End(); !iter.Equal(end); iter++)
    // {
    //      // use foo, iter.
    // }
    // iter.Get() will yield (a reference to) the
    // current key.  It will assert the equivalent of "iter != end."
    class KeyIterator
    {
    private:
        friend class SimplerHashTable;

        // The method implementations have to be here for portability.
        // Some compilers won't compile the separate implementation in shash.inl

        Node**      m_table;
        Node*       m_node;
        unsigned    m_tableSize;
        unsigned    m_index;

    public:
        KeyIterator(const SimplerHashTable *hash, BOOL begin)
        : m_table(hash->m_table),
          m_node(NULL),
          m_tableSize(hash->m_tableSizeInfo.prime),
          m_index(begin ? 0 : m_tableSize)
        {
            if (begin && hash->m_tableCount > 0)
            {
                assert(m_table != NULL);
                while (m_index < m_tableSize && m_table[m_index] == NULL)
                    m_index++;

                if (m_index >= m_tableSize)
                {
                    return;
                }
                else
                {
                    m_node = m_table[m_index];
                }
                assert(m_node != NULL);
            }
        }

        const Key& Get() const
        {
            assert(m_node != NULL);

            return m_node->m_key;
        }

        const Value& GetValue() const
        {
            assert(m_node != NULL);

            return m_node->m_val;
        }

        void SetValue(const Value & value) const
        {
            assert(m_node != NULL);

            m_node->m_val = value;
        }

        void Next()
        {
            if (m_node != NULL)
            {
                m_node = m_node->m_next;
                if (m_node != NULL)
                {
                    return;
                }

                // Otherwise...
                m_index++;
            }
            while (m_index < m_tableSize && m_table[m_index] == NULL)
                m_index++;

            if (m_index >= m_tableSize)
            {
                m_node = NULL;
                return;
            }
            else
            {
                m_node = m_table[m_index];
            }
            assert(m_node != NULL);
        }

        bool Equal(const KeyIterator &i) const
        {
            return i.m_node == m_node;
        }

        void operator++() {
            Next();
        }

        void operator++(int) {
            Next();
        }
    };

    // HashTableRef only exists to support operator[]
    // operator[] returns a HashTableRef which enables operator[] to support reading and writing
    // in a normal array, it just returns a ref an actual element, which is not possible here.
    class HashTableRef
    {
    public:
        // this is really the getter for the array.
        operator Value()
        {

            Value result;
            table->Lookup(key, &result);
            return result;
        }

        void operator =(const Value v)
        {
            table->Set(key, v);
        }

        friend class SimplerHashTable;

    protected:
        HashTableRef(SimplerHashTable *t, Key k)
        {
            table = t;
            key = k;
        }

        SimplerHashTable *table;
        Key key;
    };

    Value &operator[](Key k) const
    {
        Value* p = LookupPointer(k);
        assert(p);
        return *p;
    }

  private:
    // Find the next prime number >= the given value.
    static PrimeInfo NextPrime(unsigned number);

    // Instance members
    IAllocator*   m_alloc;                // IAllocator to use in this
                                          // table.
    // The node type.
    struct Node {
        Node* m_next;   // Assume that the alignment requirement of Key and Value are no greater than Node*, so put m_next to avoid unnecessary padding.
        Key   m_key;
        Value m_val;

        Node(Key k, Value v, Node* next) : m_next(next), m_key(k), m_val(v) {}

        void* operator new(size_t sz, IAllocator* alloc)
        {
            return alloc->Alloc(sz);
        }

        void operator delete(void* p, IAllocator* alloc)
        {
            alloc->Free(p);
        }
    };

    Node**        m_table;                // pointer to table
    PrimeInfo     m_tableSizeInfo;        // size of table (a prime) and information about it
    unsigned      m_tableCount;           // number of elements in table
    unsigned      m_tableMax;             // maximum occupied count
};

#include "simplerhash.inl"

#endif // _SIMPLERHASHTABLE_H_
