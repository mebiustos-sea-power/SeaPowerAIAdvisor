namespace SeaPowerAIAdvisor;

// Only the token at the caret is replaced; the rest of the draft remains ordinary text.
internal readonly struct AdvisorMentionQuery
{
    internal int Start { get; }
    internal int Length { get; }
    internal string Query { get; }
    private AdvisorMentionQuery(int start, int length, string query)
    { Start = start; Length = length; Query = query; }

    internal static AdvisorMentionQuery? At(string text, int caret, int selectionLength)
    {
        if (selectionLength != 0 || caret < 0 || caret > text.Length) return null;
        for (var i = caret - 1; i >= 0; i--)
        {
            if (text[i] == '@')
            {
                var end = caret;
                while (end < text.Length && !Boundary(text[end]) && text[end] != '@') end++;
                return new AdvisorMentionQuery(i, end - i, text.Substring(i + 1, caret - i - 1));
            }
            if (Boundary(text[i])) break;
        }
        return null;
    }

    private static bool Boundary(char c) => char.IsWhiteSpace(c) || ",.;:!?()[]{}、，。；：！？（）［］｛｝「」『』【】".IndexOf(c) >= 0;
}
