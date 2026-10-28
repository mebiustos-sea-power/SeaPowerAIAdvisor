using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SeaPowerAIAdvisor.Shared;

internal static class EmbeddedDependencies
{
    // Load our pinned parser before any UI method that references it is JIT-compiled.
    internal static void LoadMarkdown(Assembly owner)
    {
        using var stream = owner.GetManifestResourceStream("SeaPowerAIAdvisor.Markdig.dll")
            ?? throw new FileNotFoundException("The embedded Markdown parser is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var loaded = Assembly.Load(buffer.ToArray());
        var expected = owner.GetReferencedAssemblies().Single(a => a.Name == "Markdig");
        if (loaded.FullName != expected.FullName)
            throw new InvalidDataException("The embedded Markdown parser version does not match its reference.");
    }
}
