using System;
using System.IO;

namespace SeaPowerAIAdvisor.Crew;

// Samples become ordinary user data on first use; subsequent opens do not merge or restore them.
internal static class CrewSampleCharacters
{
    internal static CrewCharacterLibrary Open(string settingsDirectory, CrewPortraitLibrary portraits)
    {
        var library = new CrewCharacterLibrary(settingsDirectory);
        library.Initialize(() =>
        {
            var samples = CrewCharacterLibrary.Read(Read("characters.json"));
            foreach (var character in samples)
                if (character.PortraitId is string id) portraits.Import(id, Read(id));
            // The registry is committed once, after all portrait imports succeed.
            return samples;
        });
        return library;
    }
    private static byte[] Read(string name)
    {
        using var source = typeof(CrewSampleCharacters).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Crew.Samples." + name)
            ?? throw new InvalidOperationException("The bundled crew sample is missing: " + name);
        using var bytes = new MemoryStream(); source.CopyTo(bytes); return bytes.ToArray();
    }
}
