// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Text;

namespace System.Buffers
{
    /// <summary>
    /// Separated out of <see cref="AhoCorasick"/> to allow us to defer some computation costs in case we decide not to build the full thing.
    /// </summary>
    internal ref struct AhoCorasickBuilder
    {
        private readonly ReadOnlySpan<string> _values;
        private readonly bool _ignoreCase;
        private ValueListBuilder<AhoCorasickNode> _nodes;
        private ValueListBuilder<int> _parents;
        private IndexOfAnyAsciiSearcher.AsciiState _startingAsciiChars;

        private AhoCorasickBuilder(ReadOnlySpan<string> values, bool ignoreCase)
        {
            Debug.Assert(!values.IsEmpty);
            Debug.Assert(!string.IsNullOrEmpty(values[0]));

            _values = values;
            _ignoreCase = ignoreCase;
        }

        public static AhoCorasick Build(ReadOnlySpan<string> values, bool ignoreCase) =>
            new AhoCorasickBuilder(values, ignoreCase).Build();

        private AhoCorasick Build()
        {
            BuildTrie();

            AddSuffixLinks();

            Debug.Assert(_nodes[0].Match is null, "The root node shouldn't have a match.");

            for (int i = 0; i < _nodes.Length; i++)
            {
                _nodes[i].OptimizeChildren();
            }

            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported)
            {
                GenerateStartingAsciiCharsBitmap();
            }

            var result = new AhoCorasick(_nodes.AsSpan().ToArray(), _startingAsciiChars);

            _nodes.Dispose();
            _parents.Dispose();

            return result;
        }

        private void BuildTrie()
        {
            _nodes.Append(new AhoCorasickNode());
            _parents.Append(0);

            foreach (string value in _values)
            {
                int nodeIndex = 0;
                ref AhoCorasickNode node = ref _nodes[nodeIndex];

                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];

                    if (!node.TryGetChild(c, out int childIndex))
                    {
                        childIndex = _nodes.Length;
                        node.AddChild(c, childIndex);
                        _nodes.Append(new AhoCorasickNode());
                        _parents.Append(nodeIndex);
                    }

                    node = ref _nodes[childIndex];
                    nodeIndex = childIndex;
                }

                node.Match = value;
                node.SuffixLink = -1;
            }
        }

        private void AddSuffixLinks()
        {
            // Besides the list of children which continue the current value, each node also contains a suffix link
            // which points to the node with the longest suffix of the current node.
            // When we're searching and can't find a child to extend the current string with, we will follow
            // suffix links to find the longest string that does match up until the current point.
            //
            // For example if we have strings "DOTNET" and "OTTER", we want
            // the 'O' and 'T' in "dotnet" to point into 'O' and 'T' in "OTTER".
            // If our text contains the word "dotter", we will walk it character by character.
            // Once we get to "DOT" and read the next character 'T', we can no longer continue with "DOTNET",
            // and will instead follow the suffix link to "OT" in "OTTER" where we can continue the search.
            //
            // We also remember when a node's suffix link points to the end of a different value, such that it is itself a match.
            // If we also had the word "POTTERY", the 'R' would contain a suffix link to the 'R' in "OTTER",
            // but also mark that it is already a length=5 match.
            //
            //       +---> D  O  T  N  E  T
            //       |        |  |
            //       |     +--+  |
            // root--+     |     |
            //       |     |  +--+
            //       |     v  v
            //       +---> O  T  T  E  R
            //       |     ^  ^  ^  ^  ^
            //       |     |  |  |  |  | -- this is also a length=5 match
            //       |     |  |  |  |  |
            //       +> P  O  T  T  E  R  Y

            var queue = new Queue<(char Char, int Index)>();
            queue.Enqueue(((char)0, 0));

            while (queue.TryDequeue(out (char Char, int Index) trieNode))
            {
                ref AhoCorasickNode node = ref _nodes[trieNode.Index];
                int parent = _parents[trieNode.Index];
                int suffixLink = _nodes[parent].SuffixLink;

                // If this node doesn't represent the first character of a value (doesn't immediately follow the root node),
                // it may have a have a non-zero suffix link.
                if (node.Match is null && parent != 0)
                {
                    while (suffixLink >= 0)
                    {
                        ref AhoCorasickNode suffixNode = ref _nodes[suffixLink];

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

                    node.SuffixLink = suffixLink;

                    if (suffixLink >= 0)
                    {
                        // Remember if this node's suffix link points to a node that is itself a match.
                        node.Match = _nodes[suffixLink].Match;
                    }
                }

                node.AddChildrenToQueue(queue);
            }
        }

        // If all the values start with ASCII characters, we can use IndexOfAnyAsciiSearcher
        // to quickly skip to the next possible starting location in the input.
        private unsafe void GenerateStartingAsciiCharsBitmap()
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
                IndexOfAnyAsciiSearcher.ComputeAsciiState(startingChars.AsSpan(), out _startingAsciiChars);
            }

            startingChars.Dispose();
        }
    }
}
