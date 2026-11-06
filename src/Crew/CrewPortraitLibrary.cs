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
    private readonly Dictionary<string, int> contentVersions = new(StringComparer.OrdinalIgnoreCase);
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
    internal int ContentVersion(string id) => contentVersions.TryGetValue(id, out var version) ? version : 0;
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
        Write(id, preparedImage, replace: false); return id;
    }
    // Initial imports keep stable references and preserve any already imported or user-replaced asset.
    internal void Import(string id, byte[] preparedImage)
    {
        Validate(preparedImage);
        if (Read(id) == null) Write(id, preparedImage, replace: false);
    }
    internal void Replace(string id, byte[] preparedImage)
    {
        Validate(preparedImage);
        // IDs remain opaque references; the current payload's signature determines PNG or JPEG decoding.
        Write(id, preparedImage, replace: true);
    }
    private void Write(string id, byte[] bytes, bool replace)
    {
        var path = Resolve(id); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!replace) Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes, 0, bytes.Length);
            // Publish a complete payload; replacement must find an existing asset.
            if (replace) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new CrewPortraitLibraryException("portrait_library_write_failed", new AggregateException(e, cleanup)); }
            throw new CrewPortraitLibraryException(replace && e is (FileNotFoundException or DirectoryNotFoundException)
                ? "portrait_selection_missing" : "portrait_library_write_failed", e);
        }
        Version++;
        if (replace) contentVersions[id] = Version;
    }
    internal void Delete(string id)
    {
        try { File.Delete(Resolve(id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new CrewPortraitLibraryException("portrait_library_delete_failed", e); }
        contentVersions.Remove(id); Version++;
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
