using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Local display preferences are independent of provider credentials and model instructions.
internal sealed class AdvisorProfile
{
    internal const string DefaultName = "Michael Reed";
    internal const string DefaultPortraitFile = "portrait.png";
    internal const int MaxPortraitBytes = 8 * 1024 * 1024;
    internal const int MaxPortraitDimension = 4096;
    internal string DisplayName { get; }
    internal string PortraitFile { get; }
    internal AdvisorProfile(string name, string portraitFile)
    {
        name = name.Trim(); portraitFile = portraitFile.Trim();
        if (name.Length == 0 || name.Length > 80 || name.Any(char.IsControl) ||
            portraitFile.Length == 0 || portraitFile.Length > 1024 || portraitFile.Any(char.IsControl))
            throw new AdvisorProfileException("profile_invalid");
        try
        {
            if (!string.Equals(Path.GetExtension(portraitFile), ".png", StringComparison.OrdinalIgnoreCase))
                throw new AdvisorProfileException("profile_invalid");
        }
        catch (ArgumentException) { throw new AdvisorProfileException("profile_invalid"); }
        try { _ = new UTF8Encoding(false, true).GetByteCount(name + portraitFile); }
        catch (EncoderFallbackException) { throw new AdvisorProfileException("profile_invalid"); }
        DisplayName = name; PortraitFile = portraitFile;
    }
    internal static string DirectoryIn(string gameDataPath) => Path.Combine(gameDataPath, "SeaPowerAIAdvisor");
    internal static string SettingsPath(string directory) => Path.Combine(directory, "profile.json");

    internal static void EnsureCreated(string directory, Func<byte[]> defaultPortrait)
    {
        try { Directory.CreateDirectory(directory); }
        catch (IOException) { throw new AdvisorProfileException("profile_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_write_failed"); }
        if (File.Exists(SettingsPath(directory))) return;
        var portrait = Path.Combine(directory, DefaultPortraitFile);
        // A missing bundled resource is a deployment error, not a recoverable user-profile error.
        var defaults = File.Exists(portrait) ? null : defaultPortrait();
        try
        {
            if (defaults != null) WriteNew(portrait, defaults);
            WriteNew(SettingsPath(directory), Encoding.UTF8.GetBytes(new AdvisorProfile(DefaultName, DefaultPortraitFile).Json()));
        }
        catch (IOException) { throw new AdvisorProfileException("profile_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_write_failed"); }
    }

    internal static AdvisorProfile Load(string directory)
    {
        var path = SettingsPath(directory);
        if (!File.Exists(path)) throw new AdvisorProfileException("profile_missing");
        try
        {
            if (new FileInfo(path).Length > 16_384) throw new AdvisorProfileException("profile_invalid");
            return Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
        }
        catch (DecoderFallbackException) { throw new AdvisorProfileException("profile_invalid"); }
        catch (IOException) { throw new AdvisorProfileException("profile_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_unreadable"); }
    }
    internal static AdvisorProfile Parse(string json)
    {
        try
        {
            using var source = new StringReader(json);
            using var reader = new JsonTextReader(source) { MaxDepth = 4, DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || value.Properties().Any(p => p.Name != "display_name" && p.Name != "portrait_file") ||
                value["display_name"]?.Type != JTokenType.String || value["portrait_file"]?.Type != JTokenType.String)
                throw new AdvisorProfileException("profile_invalid");
            return new AdvisorProfile((string)value["display_name"]!, (string)value["portrait_file"]!);
        }
        catch (JsonException) { throw new AdvisorProfileException("profile_invalid"); }
        catch (ArgumentException) { throw new AdvisorProfileException("profile_invalid"); }
    }
    internal string ResolvePortrait(string directory)
    {
        try { return Path.GetFullPath(Path.Combine(directory, PortraitFile)); }
        catch (ArgumentException) { throw new AdvisorProfileException("profile_invalid"); }
        catch (NotSupportedException) { throw new AdvisorProfileException("profile_invalid"); }
        catch (PathTooLongException) { throw new AdvisorProfileException("profile_invalid"); }
    }
    internal byte[] ReadPortrait(string directory)
    {
        var path = ResolvePortrait(directory);
        if (!File.Exists(path)) throw new AdvisorProfileException("portrait_missing");
        try
        {
            if (new FileInfo(path).Length > MaxPortraitBytes) throw new AdvisorProfileException("portrait_invalid");
            var bytes = File.ReadAllBytes(path);
            // Validate dimensions before Unity allocates the decoded texture. Unity validates the PNG stream itself.
            if (bytes.Length < 33 || bytes.Length > MaxPortraitBytes ||
                !bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
                throw new AdvisorProfileException("portrait_invalid");
            uint Number(int offset) => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
            var width = Number(16); var height = Number(20);
            if (width == 0 || height == 0 || width > MaxPortraitDimension || height > MaxPortraitDimension)
                throw new AdvisorProfileException("portrait_invalid");
            return bytes;
        }
        catch (IOException) { throw new AdvisorProfileException("portrait_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("portrait_unreadable"); }
    }
    internal void Save(string directory)
    {
        var path = SettingsPath(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            WriteNew(temporary, Encoding.UTF8.GetBytes(Json()));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        catch (IOException) { throw new AdvisorProfileException("profile_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_write_failed"); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { throw new AdvisorProfileException("profile_write_failed"); }
            catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_write_failed"); }
        }
    }
    private string Json() => new JObject { ["display_name"] = DisplayName, ["portrait_file"] = PortraitFile }.ToString(Formatting.Indented) + Environment.NewLine;
    private static void WriteNew(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes, 0, bytes.Length);
    }
}

internal sealed class AdvisorProfileException : Exception
{
    internal string Code { get; }
    internal AdvisorProfileException(string code) : base(code) { Code = code; }
}
