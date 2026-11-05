using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// Owns imported copies only. Saves and people hold references, never image bytes.
internal sealed class CrewPortraitFiles
{
    private readonly string directory;
    private readonly Action<Exception> log;
    private string Imports => Path.Combine(directory, "Crew", "portraits");
    internal CrewPortraitFiles(string directory, Action<Exception> log) { this.directory = directory; this.log = log; }
    internal byte[]? Read(string file)
    {
        var path = Path.Combine(directory, file);
        return File.Exists(path) ? PortraitData.Read(path) : null;
    }
    internal string Import(byte[] bytes)
    {
        var extension = PortraitData.Extension(bytes);
        var name = "portrait-" + Guid.NewGuid().ToString("N") + extension;
        try
        {
            Directory.CreateDirectory(Imports);
            using var stream = new FileStream(Path.Combine(Imports, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(Path.Combine(Imports, name));
            throw new PortraitDataException("portrait_write_failed", e);
        }
        return Path.Combine("Crew", "portraits", name);
    }
    internal void Prune(IEnumerable<string?> references)
    {
        try
        {
            if (!Directory.Exists(Imports)) return;
            var retained = references.Where(f => f != null).Select(f => Path.GetFullPath(Path.Combine(directory, f!))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(Imports))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                if (stem.StartsWith("portrait-", StringComparison.Ordinal) && Guid.TryParseExact(stem.Substring(9), "N", out _) &&
                    PortraitData.SupportsPath(path) && !retained.Contains(Path.GetFullPath(path))) TryDelete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log(e); }
    }
    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log(e); }
    }
}
