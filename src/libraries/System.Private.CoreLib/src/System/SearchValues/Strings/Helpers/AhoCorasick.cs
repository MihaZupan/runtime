// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace System.Buffers
{
    internal ref struct AhoCorasickBuilder
    {
        private readonly ReadOnlySpan<string> _values;
        private readonly bool _ignoreCase;
        private ValueListBuilder<AhoCorasick.Node> _nodes;
        private ValueListBuilder<int> _parents;
        private Vector256<byte> _startingCharsAsciiBitmap;
        private int _maxValueLength; // Only used by the NLS fallback

        public AhoCorasickBuilder(ReadOnlySpan<string> values, bool ignoreCase, ref List<string>? unreachableValues)
        {
            Debug.Assert(!values.IsEmpty);
            Debug.Assert(!string.IsNullOrEmpty(values[0]));

#if DEBUG
            // The input should have been sorted by length
            for (int i = 1; i < values.Length; i++)
            {
                Debug.Assert(values[i - 1].Length <= values[i].Length);
            }
#endif

            _values = values;
            _ignoreCase = ignoreCase;
            BuildTrie(ref unreachableValues);
        }

        public AhoCorasick Build()
        {
            AddSuffixLinks();

            Debug.Assert(_nodes[0].MatchLength == 0, "The root node shouldn't have a match.");

            for (int i = 0; i < _nodes.Length; i++)
            {
                _nodes[i].OptimizeChildren();
            }

            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported)
            {
                GenerateStartingAsciiCharsBitmap();
            }

            return new AhoCorasick(_nodes.AsSpan().ToArray(), _startingCharsAsciiBitmap, _maxValueLength);
        }

        public void Dispose()
        {
            _nodes.Dispose();
            _parents.Dispose();
        }

        private void BuildTrie(ref List<string>? unreachableValues)
        {
            _nodes.Append(new AhoCorasick.Node());
            _parents.Append(0);

            foreach (string value in _values)
            {
                int nodeIndex = 0;
                ref AhoCorasick.Node node = ref _nodes[nodeIndex];

                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];

                    if (!node.TryGetChild(c, out int childIndex))
                    {
                        childIndex = _nodes.Length;
                        node.AddChild(c, childIndex);
                        _nodes.Append(new AhoCorasick.Node());
                        _parents.Append(nodeIndex);
                    }

                    node = ref _nodes[childIndex];
                    nodeIndex = childIndex;

                    if (node.MatchLength != 0)
                    {
                        // A previous value is an exact prefix of this one.
                        // We're looking for the index of the first match, not necessarily the longest one, we can skip this value.
                        unreachableValues ??= new List<string>();
                        unreachableValues.Add(value);
                        break;
                    }

                    if (i == value.Length - 1)
                    {
                        node.MatchLength = value.Length;
                        _maxValueLength = Math.Max(_maxValueLength, value.Length);
                        break;
                    }
                }
            }
        }

        private void AddSuffixLinks()
        {
            var queue = new Queue<(char Char, int Index)>();
            queue.Enqueue(((char)0, 0));

            while (queue.TryDequeue(out (char Char, int Index) trieNode))
            {
                ref AhoCorasick.Node node = ref _nodes[trieNode.Index];
                int parent = _parents[trieNode.Index];
                int suffixLink = _nodes[parent].SuffixLink;

                if (parent != 0)
                {
                    while (suffixLink >= 0)
                    {
                        ref AhoCorasick.Node suffixNode = ref _nodes[suffixLink];

                        if (suffixNode.TryGetChild(trieNode.Char, out int childSuffixLink))
                        {
                            suffixLink = childSuffixLink;
                            break;
                        }

                        if (suffixLink == 0)
                        {
                            break;
                        }

                        suffixLink = suffixNode.SuffixLink;
                    }
                }

                if (node.MatchLength != 0)
                {
                    node.SuffixLink = -1;

                    // If a node is a match, there's no need to assign suffix links to its children.
                    // If a child does not match, such that we would look at its suffix link, we already saw an earlier match node.
                }
                else
                {
                    node.SuffixLink = suffixLink;

                    if (suffixLink >= 0)
                    {
                        node.MatchLength = _nodes[suffixLink].MatchLength;
                    }

                    node.AddChildrenToQueue(queue);
                }
            }
        }

        private void GenerateStartingAsciiCharsBitmap()
        {
            scoped ValueListBuilder<char> startingChars = new ValueListBuilder<char>(stackalloc char[128]);

            foreach (string value in _values)
            {
                char c = value[0];

                if (_ignoreCase)
                {
                    startingChars.Append(char.ToLowerInvariant(c));
                    startingChars.Append(char.ToUpperInvariant(c));
                }
                else
                {
                    startingChars.Append(c);
                }
            }

            if (Ascii.IsValid(startingChars.AsSpan()))
            {
                IndexOfAnyAsciiSearcher.ComputeBitmap(startingChars.AsSpan(), out _startingCharsAsciiBitmap, out _);

                // TODO: Should we avoid using the fast scan if there are too many starting values?
                //int uniqueStartingChars =
                //    BitOperations.PopCount(bitmap.AsUInt64()[0]) +
                //    BitOperations.PopCount(bitmap.AsUInt64()[1]);
            }

            startingChars.Dispose();
        }
    }

    internal struct AhoCorasick
    {
        private readonly Node[] _nodes;
        private Vector256<byte> _startingCharsAsciiBitmap;
        private readonly int _maxValueLength; // Only used by the NLS fallback

        public AhoCorasick(Node[] nodes, Vector256<byte> startingAsciiBitmap, int maxValueLength)
        {
            _nodes = nodes;
            _startingCharsAsciiBitmap = startingAsciiBitmap;
            _maxValueLength = maxValueLength;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int IndexOfAny<TCaseSensitivity, TFastScanVariant>(ReadOnlySpan<char> span)
            where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
            where TFastScanVariant : struct, IFastScan
        {
            if (typeof(TCaseSensitivity) == typeof(TeddyHelper.CaseInsensitiveUnicode))
            {
                return GlobalizationMode.UseNls
                    ? IndexOfAnyCaseInsensitiveUnicodeNls<TFastScanVariant>(span)
                    : IndexOfAnyCaseInsensitiveUnicodeIcuOrInvariant<TFastScanVariant>(span);
            }

            return IndexOfAnyCore<TCaseSensitivity, TFastScanVariant>(span);
        }

        private readonly int IndexOfAnyCore<TCaseSensitivity, TFastScanVariant>(ReadOnlySpan<char> span)
            where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
            where TFastScanVariant : struct, IFastScan
        {
            Debug.Assert(typeof(TCaseSensitivity) != typeof(TeddyHelper.CaseInsensitiveUnicode));

            ref Node nodes = ref MemoryMarshal.GetArrayDataReference(_nodes);
            int nodeIndex = 0;
            int result = -1;
            int i = 0;

        FastScan:
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported && typeof(TFastScanVariant) == typeof(IndexOfAnyAsciiFastScan))
            {
                int remainingLength = span.Length - i;

                if (remainingLength >= Vector128<ushort>.Count)
                {
                    // If '\0' is one of the starting chars and we're running on Ssse3 hardware, this may return false-positives.
                    // False-positives here are okay, we'll just rule them out below. While we could flow the Ssse3AndWasmHandleZeroInNeedle
                    // generic through, we expect such values to be rare enough that introducing more code is not worth it.
                    int offset = IndexOfAnyAsciiSearcher.IndexOfAnyVectorized<IndexOfAnyAsciiSearcher.DontNegate, IndexOfAnyAsciiSearcher.Default>(
                        ref Unsafe.As<char, short>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), i)),
                        remainingLength,
                        ref Unsafe.AsRef(_startingCharsAsciiBitmap));

                    if (offset < 0)
                    {
                        goto Return;
                    }

                    i += offset;
                    goto LoopWithoutRangeCheck;
                }
            }

        Loop:
            if ((uint)i >= (uint)span.Length)
            {
                goto Return;
            }

        LoopWithoutRangeCheck:
            Debug.Assert(i < span.Length);
            char c = TCaseSensitivity.TransformInput(Unsafe.Add(ref MemoryMarshal.GetReference(span), i));

            while (true)
            {
                Debug.Assert((uint)nodeIndex < (uint)_nodes.Length);
                ref Node node = ref Unsafe.Add(ref nodes, (uint)nodeIndex);

                if (node.TryGetChild(c, out int childIndex))
                {
                    nodeIndex = childIndex;

                    int matchLength = Unsafe.Add(ref nodes, (uint)nodeIndex).MatchLength;
                    if (matchLength != 0)
                    {
                        result = i + 1 - matchLength;
                    }

                    i++;
                    goto Loop;
                }

                if (nodeIndex == 0)
                {
                    if (result >= 0)
                    {
                        goto Return;
                    }

                    i++;
                    goto FastScan;
                }

                nodeIndex = node.SuffixLink;

                if (nodeIndex < 0)
                {
                    Debug.Assert(nodeIndex == -1);
                    Debug.Assert(result >= 0);
                    goto Return;
                }
            }

        Return:
            return result;
        }

        // Mostly a copy of IndexOfAnyCore, but we may read two characters at a time in the case of surrogate pairs.
        private readonly int IndexOfAnyCaseInsensitiveUnicodeIcuOrInvariant<TFastScanVariant>(ReadOnlySpan<char> span)
            where TFastScanVariant : struct, IFastScan
        {
            Debug.Assert(!GlobalizationMode.UseNls);

            const char LowSurrogateNotSet = '\0';

            ref Node nodes = ref MemoryMarshal.GetArrayDataReference(_nodes);
            int nodeIndex = 0;
            int result = -1;
            int i = 0;
            char lowSurrogateUpper = LowSurrogateNotSet;

        FastScan:
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported && typeof(TFastScanVariant) == typeof(IndexOfAnyAsciiFastScan))
            {
                if (lowSurrogateUpper != LowSurrogateNotSet)
                {
                    goto LoopWithoutRangeCheck;
                }

                int remainingLength = span.Length - i;

                if (remainingLength >= Vector128<ushort>.Count)
                {
                    int offset = IndexOfAnyAsciiSearcher.IndexOfAnyVectorized<IndexOfAnyAsciiSearcher.DontNegate, IndexOfAnyAsciiSearcher.Default>(
                        ref Unsafe.As<char, short>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), i)),
                        remainingLength,
                        ref Unsafe.AsRef(_startingCharsAsciiBitmap));

                    if (offset < 0)
                    {
                        goto Return;
                    }

                    i += offset;
                    goto LoopWithoutRangeCheck;
                }
            }

        Loop:
            if ((uint)i >= (uint)span.Length)
            {
                goto Return;
            }

        LoopWithoutRangeCheck:
            Debug.Assert(i < span.Length);
            char c;
            if (lowSurrogateUpper != LowSurrogateNotSet)
            {
                c = lowSurrogateUpper;
                lowSurrogateUpper = LowSurrogateNotSet;
            }
            else
            {
                c = Unsafe.Add(ref MemoryMarshal.GetReference(span), i);
                char lowSurrogate;

                if (char.IsHighSurrogate(c) &&
                    (uint)(i + 1) < (uint)span.Length &&
                    char.IsLowSurrogate(lowSurrogate = Unsafe.Add(ref MemoryMarshal.GetReference(span), i + 1)))
                {
                    SurrogateCasing.ToUpper(c, lowSurrogate, out c, out lowSurrogateUpper);
                    Debug.Assert(lowSurrogateUpper != LowSurrogateNotSet);
                }
                else
                {
                    c = GlobalizationMode.Invariant
                        ? InvariantModeCasing.ToUpper(c)
                        : OrdinalCasing.ToUpper(c);
                }

#if DEBUG
                // This logic must match Ordinal.ToUpperOrdinal exactly.
                Span<char> destination = new char[2]; // Avoid stackalloc in a loop
                Ordinal.ToUpperOrdinal(span.Slice(i, i + 1 == span.Length ? 1 : 2), destination);
                Debug.Assert(c == destination[0]);
                Debug.Assert(lowSurrogateUpper == LowSurrogateNotSet || lowSurrogateUpper == destination[1]);
#endif
            }

            while (true)
            {
                Debug.Assert((uint)nodeIndex < (uint)_nodes.Length);
                ref Node node = ref Unsafe.Add(ref nodes, (uint)nodeIndex);

                if (node.TryGetChild(c, out int childIndex))
                {
                    nodeIndex = childIndex;

                    int matchLength = Unsafe.Add(ref nodes, (uint)nodeIndex).MatchLength;
                    if (matchLength != 0)
                    {
                        result = i + 1 - matchLength;
                    }

                    i++;
                    goto Loop;
                }

                if (nodeIndex == 0)
                {
                    if (result >= 0)
                    {
                        goto Return;
                    }

                    i++;
                    goto FastScan;
                }

                nodeIndex = node.SuffixLink;

                if (nodeIndex < 0)
                {
                    Debug.Assert(nodeIndex == -1);
                    Debug.Assert(result >= 0);
                    goto Return;
                }
            }

        Return:
            return result;
        }

        private readonly int IndexOfAnyCaseInsensitiveUnicodeNls<TFastScanVariant>(ReadOnlySpan<char> span)
            where TFastScanVariant : struct, IFastScan
        {
            Debug.Assert(GlobalizationMode.UseNls);

            if (span.IsEmpty)
            {
                return -1;
            }

            // If the input is large, we avoid uppercasing all of it upfront.
            // We may find a match at position 0, so we want to behave closer to O(match offset) than O(input length).
#if DEBUG
            // Make it easier to test with shorter inputs
            const int StackallocThreshold = 32;
#else
            // This limit isn't just about how much we allocate on the stack, but also how we chunk the input span.
            // A larger value would improve throughput for rare matches, while a lower number reduces the overhead
            // when matches are found close to the start.
            const int StackallocThreshold = 64;
#endif

            int minBufferSize = (int)Math.Clamp(_maxValueLength * 4L, StackallocThreshold, string.MaxLength + 1);

            char[]? pooledArray = null;
            Span<char> buffer = minBufferSize <= StackallocThreshold
                ? stackalloc char[StackallocThreshold]
                : (pooledArray = ArrayPool<char>.Shared.Rent(minBufferSize));

            int leftoverFromPreviousIteration = 0;
            int offsetFromStart = 0;
            int result;

            while (true)
            {
                Span<char> newSpaceAvailable = buffer.Slice(leftoverFromPreviousIteration);
                int toConvert = Math.Min(span.Length, newSpaceAvailable.Length);

                int charsWritten = Ordinal.ToUpperOrdinal(span.Slice(0, toConvert), newSpaceAvailable);
                Debug.Assert(charsWritten == toConvert);
                span = span.Slice(toConvert);

                Span<char> upperCaseBuffer = buffer.Slice(0, leftoverFromPreviousIteration + toConvert);
                result = IndexOfAny<TeddyHelper.CaseSensitive, TFastScanVariant>(upperCaseBuffer);

                if (result >= 0 && (span.IsEmpty || result <= buffer.Length - _maxValueLength))
                {
                    result += offsetFromStart;
                    break;
                }

                if (span.IsEmpty)
                {
                    result = -1;
                    break;
                }

                leftoverFromPreviousIteration = _maxValueLength - 1;
                buffer.Slice(buffer.Length - leftoverFromPreviousIteration).CopyTo(buffer);
                offsetFromStart += buffer.Length - leftoverFromPreviousIteration;
            }

            if (pooledArray is not null)
            {
                ArrayPool<char>.Shared.Return(pooledArray);
            }

            return result;
        }

        public interface IFastScan { }

        public readonly struct IndexOfAnyAsciiFastScan : IFastScan { }

        public readonly struct NoFastScan : IFastScan { }

        [DebuggerDisplay("MatchLength={MatchLength} SuffixLink={SuffixLink} ChildrenCount={(_children?.Count ?? 0) + (_firstChildChar < 0 ? 0 : 1)}")]
        public struct Node
        {
            public int SuffixLink;
            public int MatchLength;

            private int _firstChildChar;
            private int _firstChildIndex;
            private Dictionary<char, int>? _children;

            public Node()
            {
                _firstChildChar = -1;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly bool TryGetChild(char c, out int index)
            {
                if (_firstChildChar == c)
                {
                    index = _firstChildIndex;
                    return true;
                }

                Dictionary<char, int>? children = _children;

                if (children is null)
                {
                    index = default;
                    return false;
                }

                return children.TryGetValue(c, out index);
            }

            public void AddChild(char c, int index)
            {
                if (_firstChildChar < 0)
                {
                    _firstChildChar = c;
                    _firstChildIndex = index;
                }
                else
                {
                    _children ??= new Dictionary<char, int>();
                    _children.Add(c, index);
                }
            }

            public void AddChildrenToQueue(Queue<(char Char, int Index)> queue)
            {
                if (_firstChildChar >= 0)
                {
                    queue.Enqueue(((char)_firstChildChar, _firstChildIndex));

                    if (_children is not null)
                    {
                        foreach ((char childChar, int childIndex) in _children)
                        {
                            queue.Enqueue((childChar, childIndex));
                        }
                    }
                }
            }

            public void OptimizeChildren()
            {
                // TODO: This is okay for sparse nodes, but should we have an array-based lookup for dense (at least starting) nodes?
                if (_children is not null)
                {
                    _children.Add((char)_firstChildChar, _firstChildIndex);

                    int frequency = -2;

                    foreach ((char childChar, int childIndex) in _children)
                    {
                        int newFrequency = char.IsAscii(childChar) ? s_asciiFrequency[childChar] : -1;

                        if (newFrequency > frequency)
                        {
                            frequency = newFrequency;
                            _firstChildChar = childChar;
                            _firstChildIndex = childIndex;
                        }
                    }

                    _children.Remove((char)_firstChildChar);
                }
            }

            // Same as RegexPrefixAnalyzer.Frequency.
            private static ReadOnlySpan<byte> s_asciiFrequency => new byte[]
            {
                 0,  0,  0,  0,  0,  0,  0,  0,  0,  1,  0,  0,  0,  0,  0,  0,
                 0,  0,  0,  0,  3,  0,  0,  0,  0,  4,  0,  0,  6,  6,  0,  0,
                96, 17, 50,  8,  9,  5, 18, 15, 90, 89, 45, 84, 76, 26, 81, 62,
                74, 68, 63, 55, 43, 37, 51, 34, 33, 28, 24, 69, 46, 60, 47, 21,
                 7, 66, 31, 56, 41, 53, 44, 25, 22, 54, 12, 14, 40, 29, 35, 30,
                39, 13, 48, 64, 61, 36, 42, 23, 19, 11, 11, 59, 20, 58, 10, 67,
                 1, 93, 75, 82, 80, 95, 79, 71, 72, 87, 38, 52, 85, 78, 88, 86,
                77, 32, 92, 91, 94, 83, 65, 57, 70, 73, 27, 49, 16, 49,  2,  0
            };
        }
    }
}
