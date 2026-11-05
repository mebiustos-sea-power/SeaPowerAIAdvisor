using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noesis;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// One battle owns decoded textures; cards and notifications borrow their sources.
internal sealed class CrewPortraits : IDisposable
{
    private readonly PortraitTexture common;
    private readonly Dictionary<string, PortraitTexture> images = new(StringComparer.OrdinalIgnoreCase);
    internal CrewPortraitLibrary Library { get; }
    internal ImageSource Common => common.Source;
    internal CrewPortraits(CrewPortraitLibrary library)
    {
        Library = library;
        using var image = typeof(CrewPortraits).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Crew.png") ?? throw new FileNotFoundException("The crew portrait is missing.");
        using var bytes = new MemoryStream(); image.CopyTo(bytes); common = new PortraitTexture(bytes.ToArray());
    }
    internal ImageSource Get(CrewPerson? person) => GetImage(person?.PortraitId);
    internal ImageSource GetImage(string? id)
    {
        if (id == null) return Common;
        if (images.TryGetValue(id, out var image)) return Library.Contains(id) ? image.Source : Common;
        var bytes = Library.Read(id);
        if (bytes == null) return Common;
        image = new PortraitTexture(bytes); images.Add(id, image); return image.Source;
    }
    internal void Retain(IEnumerable<string?> references)
    {
        var retained = references.Where(f => f != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in images.Keys.Where(f => !retained.Contains(f)).ToArray())
        { images[file].Dispose(); images.Remove(file); }
    }
    public void Dispose() { foreach (var image in images.Values) image.Dispose(); images.Clear(); common.Dispose(); }
}
