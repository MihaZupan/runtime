namespace System.Text
{
    internal ref partial struct ValueStringBuilder
    {
        public void Replace(int start, char oldChar, char newChar)
        {
            Span<char> span = _chars.Slice(start, _pos - start);

            int index = span.IndexOf(oldChar);

            if (index == -1)
            {
                return;
            }

            span = span.Slice(index);

            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] == oldChar)
                {
                    span[i] = newChar;
                }
            }
        }
    }
}
