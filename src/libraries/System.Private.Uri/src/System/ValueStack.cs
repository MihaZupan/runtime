using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System
{
    internal ref struct ValueStack<T>
    {
        private T[]? _arrayToReturnToPool;
        private Span<T> _stack;

        public int Count { get; private set; }

        public ValueStack(Span<T> initialBuffer)
        {
            _stack = initialBuffer;
            _arrayToReturnToPool = null;
            Count = 0;
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert((uint)index < (uint)_stack.Length);
                return ref _stack[index];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T Peek()
        {
            Debug.Assert(Count > 0);
            return _stack[Count - 1];
        }

        public readonly bool TryPeek(out T item)
        {
            int index = Count - 1;
            Span<T> stack = _stack;

            if ((uint)index < (uint)stack.Length)
            {
                item = stack[index];
                return true;
            }
            else
            {
                item = default!;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Pop()
        {
            Debug.Assert(Count > 0);
            return _stack[--Count];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TryPop()
        {
            int count = Count;
            if (count != 0)
            {
                Debug.Assert(count > 0);
                Count = count - 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear() => Count = 0;

        public readonly Span<T> AsSpan() => _stack.Slice(0, Count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(T item)
        {
            int count = Count;
            Span<T> stack = _stack;
            if ((uint)count < (uint)stack.Length)
            {
                stack[count] = item;
                Count = count + 1;
            }
            else
            {
                PushSlow(item);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PushSlow(T segment)
        {
            Debug.Assert(Count == _stack.Length);

            T[] poolArray = ArrayPool<T>.Shared.Rent(Count * 2);

            _stack.CopyTo(poolArray);

            T[]? toReturn = _arrayToReturnToPool;
            _stack = _arrayToReturnToPool = poolArray;
            if (toReturn != null)
            {
                ArrayPool<T>.Shared.Return(toReturn);
            }

            _stack[Count++] = segment;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            T[]? toReturn = _arrayToReturnToPool;
            this = default;
            if (toReturn != null)
            {
                ArrayPool<T>.Shared.Return(toReturn);
            }
        }
    }
}
