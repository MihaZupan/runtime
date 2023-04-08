// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace System.Buffers
{
    internal readonly struct AhoCorasick
    {
        private readonly Node[] _nodes;
        private readonly Vector128<byte> _startingCharsAsciiBitmap;

        public AhoCorasick(ReadOnlySpan<string> values, bool ignoreCase, ref List<string>? unreachableValues, out bool asciiStartChars)
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

            scoped ValueListBuilder<Node> nodesBuilder = default;
            scoped ValueListBuilder<int> parents = new(stackalloc int[64]);

            BuildTrie(ref nodesBuilder, ref parents, values, ref unreachableValues);

            Span<Node> nodes = nodesBuilder.RawSpan();

            AddSuffixLinks(nodes, parents.AsSpan());

            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i].OptimizeChildren();
            }

            Debug.Assert(nodes[0].MatchLength == 0, "The root node shouldn't have a match.");

            _nodes = nodes.ToArray();

            nodesBuilder.Dispose();
            parents.Dispose();

            asciiStartChars = false;

            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported)
            {
                GenerateStartingAsciiCharsBitmap(values, ignoreCase, out _startingCharsAsciiBitmap, out asciiStartChars);
            }
        }

        private static void BuildTrie(ref ValueListBuilder<Node> nodes, ref ValueListBuilder<int> parents, ReadOnlySpan<string> values, ref List<string>? unreachableValues)
        {
            nodes.Append(new Node());
            parents.Append(0);

            foreach (string value in values)
            {
                int nodeIndex = 0;
                ref Node node = ref nodes[nodeIndex];

                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];

                    if (!node.TryGetChild(c, out int childIndex))
                    {
                        childIndex = nodes.Length;
                        node.AddChild(c, childIndex);
                        nodes.Append(new Node());
                        parents.Append(nodeIndex);
                    }

                    node = ref nodes[childIndex];
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
                        break;
                    }
                }
            }
        }

        private static void AddSuffixLinks(Span<Node> nodes, ReadOnlySpan<int> parents)
        {
            var queue = new Queue<(char Char, int Index)>();
            queue.Enqueue(((char)0, 0));

            while (queue.TryDequeue(out (char Char, int Index) trieNode))
            {
                ref Node node = ref nodes[trieNode.Index];
                int parent = parents[trieNode.Index];
                int suffixLink = nodes[parent].SuffixLink;

                if (parent != 0)
                {
                    while (suffixLink >= 0)
                    {
                        ref Node suffixNode = ref nodes[suffixLink];

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
                        node.MatchLength = nodes[suffixLink].MatchLength;
                    }

                    node.AddChildrenToQueue(queue);
                }
            }
        }

        private static void GenerateStartingAsciiCharsBitmap(ReadOnlySpan<string> values, bool ignoreCase, out Vector128<byte> bitmap, out bool asciiStartChars)
        {
            scoped ValueListBuilder<char> startingChars = new ValueListBuilder<char>(stackalloc char[128]);
            foreach (string value in values)
            {
                char c = value[0];

                if (ignoreCase)
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
                IndexOfAnyAsciiSearcher.ComputeBitmap(startingChars.AsSpan(), out bitmap, out _);

                // TODO: Should we avoid using the fast scan if there are too many starting values?
                //int uniqueStartingChars =
                //    BitOperations.PopCount(bitmap.AsUInt64()[0]) +
                //    BitOperations.PopCount(bitmap.AsUInt64()[1]);

                asciiStartChars = true;
            }
            else
            {
                bitmap = default;
                asciiStartChars = false;
            }

            startingChars.Dispose();
        }

        public readonly int IndexOfAny<TCaseSensitivity, TFastScanVariant>(ReadOnlySpan<char> span)
            where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
            where TFastScanVariant : struct, IFastScan
        {
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
                    int offset = IndexOfAnyAsciiSearcher.IndexOfAnyVectorized<IndexOfAnyAsciiSearcher.DontNegate, IndexOfAnyAsciiSearcher.Default>(
                        ref Unsafe.As<char, short>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), i)),
                        remainingLength,
                        _startingCharsAsciiBitmap);

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

        [DebuggerDisplay("MatchLength={MatchLength} SuffixLink={SuffixLink} ChildrenCount={(_children?.Count ?? 0) + (_firstChildChar < 0 ? 0 : 1)}")]
        private struct Node
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

        // TODO: Do we care about 1-5 chars fast paths for non-ASCII values that don't go through Teddy?
        public interface IFastScan { }

        public readonly struct IndexOfAnyAsciiFastScan : IFastScan { }

        public readonly struct NoFastScan : IFastScan { }
    }
}
