namespace SeaPowerAIAdvisor.Translation;

// Keep the first source through repeated translations, but never reuse it after the game changes the text.
internal sealed class TranslationTextChange
{
    internal string Original { get; private set; }
    internal string Translated { get; private set; }
    internal TranslationTextChange(string original, string translated) { Original = original; Translated = translated; }
    internal void Update(string source, string translated)
    {
        if (source != Translated) Original = source;
        Translated = translated;
    }
    internal string? OriginalFor(string? current) => current == Translated && Original != Translated ? Original : null;
}
