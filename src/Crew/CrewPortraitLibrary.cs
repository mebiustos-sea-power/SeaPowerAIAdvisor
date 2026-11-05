using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Crew;

// The library owns persistent images. Assignment, history and saved games only borrow image IDs.
internal sealed class CrewPortraitLibrary
{
    internal const int MaximumDimension = 256;
    private readonly string directory;
    internal int Version { get; private set; }
    internal CrewPortraitLibrary(string settingsDirectory) => directory = Path.Combine(settingsDirectory, "Crew", "PortraitLibrary");
    internal IReadOnlyList<string> Ids()
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                if (File.Exists(directory)) throw new IOException("The portrait library path is not a directory.");
                return Array.Empty<string>();
            }
            return Directory.EnumerateFiles(directory).Select(Path.GetFileName).Where(id => id != null && IsId(id))
                .Select(id => id!).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new CrewPortraitLibraryException("portrait_library_read_failed", e); }
    }
    internal bool Contains(string id) => File.Exists(Resolve(id));
    internal byte[]? Read(string id)
    {
        var path = Resolve(id);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = PortraitData.Read(path); Validate(bytes); return bytes;
        }
        // A file can disappear after the existence check; other read/decode failures remain errors.
        catch (PortraitDataException e) when (e.Code == "portrait_missing") { return null; }
    }
    internal string Add(byte[] preparedImage)
    {
        var extension = Validate(preparedImage);
        var id = Guid.NewGuid().ToString("N") + extension;
        var path = Resolve(id); var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(preparedImage, 0, preparedImage.Length);
            // Publish a new asset only after writing it completely; never replace an existing image ID.
            File.Move(temporary, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new CrewPortraitLibraryException("portrait_library_write_failed", new AggregateException(e, cleanup)); }
            throw new CrewPortraitLibraryException("portrait_library_write_failed", e);
        }
        Version++; return id;
    }
    internal void Delete(string id)
    {
        try { File.Delete(Resolve(id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new CrewPortraitLibraryException("portrait_library_delete_failed", e); }
        Version++;
    }
    private string Resolve(string id)
    {
        if (!IsId(id)) throw new CrewPortraitLibraryException("portrait_id_invalid");
        return Path.Combine(directory, id);
    }
    internal static bool IsId(string id) => id.Length == 36 &&
        (id.EndsWith(".png", StringComparison.Ordinal) || id.EndsWith(".jpg", StringComparison.Ordinal)) &&
        Guid.TryParseExact(id.Substring(0, 32), "N", out _);
    private static string Validate(byte[] bytes)
    {
        var image = PortraitData.Inspect(bytes);
        if (image.Width > MaximumDimension || image.Height > MaximumDimension) throw new PortraitDataException("portrait_invalid");
        return image.Extension;
    }
}

internal sealed class CrewPortraitLibraryException : Exception
{
    internal string Code { get; }
    internal CrewPortraitLibraryException(string code, Exception? inner = null) : base(code, inner) => Code = code;
}
