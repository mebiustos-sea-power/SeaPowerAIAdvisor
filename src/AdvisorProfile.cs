using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Local advisor preferences are independent of credentials, knowledge capture and core answer rules.
internal sealed class AdvisorProfile
{
    internal const string DefaultName = "Michael Reed";
    internal const string DefaultPortraitFile = "portrait.png";
    internal const int MaxPortraitBytes = PortraitImage.MaxBytes;
    internal const int MaxPortraitDimension = PortraitImage.MaxDimension;
    internal const int MaxPersonalityLength = 2000;
    internal bool UseDefault { get; }
    internal string CustomName { get; }
    internal string DisplayName => UseDefault ? DefaultName : CustomName;
    internal string PortraitFile { get; }
    internal string DefaultPersonality { get; }
    internal string CustomPersonality { get; }
    internal string Personality => UseDefault ? DefaultPersonality : CustomPersonality;
    internal AdvisorProfile(string name, string portraitFile, bool useDefault = false,
        string defaultPersonality = "", string customPersonality = "")
    {
        name = name.Trim(); portraitFile = portraitFile.Trim();
        defaultPersonality = defaultPersonality.Trim(); customPersonality = customPersonality.Trim();
        if (new[] { defaultPersonality, customPersonality }.Any(value => value.Length > MaxPersonalityLength ||
            value.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')))
            throw new AdvisorProfileException("personality_invalid");
        if ((!useDefault && name.Length == 0) || name.Length > 80 || name.Any(char.IsControl) ||
            portraitFile.Length == 0 || portraitFile.Length > 1024 || portraitFile.Any(char.IsControl))
            throw new AdvisorProfileException("profile_invalid");
        try
        {
            if (!PortraitImage.SupportsPath(portraitFile))
                throw new AdvisorProfileException("profile_invalid");
        }
        catch (ArgumentException) { throw new AdvisorProfileException("profile_invalid"); }
        try { _ = new UTF8Encoding(false, true).GetByteCount(name + portraitFile + defaultPersonality + customPersonality); }
        catch (EncoderFallbackException) { throw new AdvisorProfileException("profile_invalid"); }
        UseDefault = useDefault; CustomName = name; PortraitFile = portraitFile;
        DefaultPersonality = defaultPersonality; CustomPersonality = customPersonality;
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
            WriteNew(SettingsPath(directory), Encoding.UTF8.GetBytes(new AdvisorProfile(DefaultName, DefaultPortraitFile, true).Json()));
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
            if (reader.Read() || value.Properties().Any(p => p.Name != "mode" && p.Name != "display_name" &&
                p.Name != "portrait_file" && p.Name != "personality" && p.Name != "default_personality" && p.Name != "custom_personality") ||
                value["display_name"]?.Type != JTokenType.String || value["portrait_file"]?.Type != JTokenType.String ||
                (value["mode"] != null && (value["mode"]!.Type != JTokenType.String ||
                    ((string?)value["mode"] != "default" && (string?)value["mode"] != "custom"))) ||
                new[] { "personality", "default_personality", "custom_personality" }.Any(key =>
                    value[key] != null && value[key]!.Type != JTokenType.String))
                throw new AdvisorProfileException("profile_invalid");
            // Legacy profiles remain custom; the former shared personality supplies missing per-mode values.
            var legacyPersonality = (string?)value["personality"] ?? "";
            return new AdvisorProfile((string)value["display_name"]!, (string)value["portrait_file"]!,
                (string?)value["mode"] == "default",
                (string?)value["default_personality"] ?? legacyPersonality,
                (string?)value["custom_personality"] ?? legacyPersonality);
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
    internal byte[] ReadActivePortrait(string directory, Func<byte[]> defaultPortrait) =>
        UseDefault ? defaultPortrait() : ReadPortrait(directory);

    internal void AddAnswerPreferences(JObject input) => input["advisor_preferences"] = new JObject
    {
        ["name"] = DisplayName, ["personality_and_tone"] = Personality
    };

    internal byte[] ReadPortrait(string directory)
    {
        var path = ResolvePortrait(directory);
        if (!File.Exists(path)) throw new AdvisorProfileException("portrait_missing");
        try
        {
            if (new FileInfo(path).Length > MaxPortraitBytes) throw new AdvisorProfileException("portrait_invalid");
            var bytes = File.ReadAllBytes(path);
            PortraitImage.Extension(bytes);
            return bytes;
        }
        catch (IOException) { throw new AdvisorProfileException("portrait_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("portrait_unreadable"); }
    }
    // Store a separate copy before changing profile.json, so a failed save cannot replace the active image.
    internal AdvisorProfile ImportPortrait(string directory, byte[] image)
    {
        var extension = PortraitImage.Extension(image);
        var imported = new AdvisorProfile(CustomName, "portrait-import-" + Guid.NewGuid().ToString("N") + extension, UseDefault, DefaultPersonality, CustomPersonality);
        var path = imported.ResolvePortrait(directory);
        var saved = false;
        try
        {
            Directory.CreateDirectory(directory);
            WriteNew(path, image);
            imported.Save(directory);
            saved = true;
            return imported;
        }
        catch (IOException) { throw new AdvisorProfileException("profile_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_write_failed"); }
        finally { if (!saved && File.Exists(path)) File.Delete(path); }
    }

    internal void DeleteImportedPortrait(string directory, Action<string> warn)
    {
        const string prefix = "portrait-import-";
        // Only remove our own previous imported copy; never delete user-selected source files or custom paths.
        var stem = Path.GetFileNameWithoutExtension(PortraitFile);
        if (Path.GetFileName(PortraitFile) != PortraitFile || !PortraitImage.SupportsPath(PortraitFile) ||
            stem.Length != prefix.Length + 32 || !stem.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(stem.Substring(prefix.Length), "N", out _)) return;
        try { File.Delete(ResolvePortrait(directory)); }
        catch (IOException error) { warn("Cannot remove the previous imported portrait: " + error.Message); }
        catch (UnauthorizedAccessException error) { warn("Cannot remove the previous imported portrait: " + error.Message); }
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
    private string Json() => new JObject
    {
        ["mode"] = UseDefault ? "default" : "custom", ["display_name"] = CustomName,
        ["portrait_file"] = PortraitFile,
        ["default_personality"] = DefaultPersonality, ["custom_personality"] = CustomPersonality
    }.ToString(Formatting.Indented) + Environment.NewLine;
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

// Unsaved edits belong to the settings preview; only Save persists them for later consultations.
internal sealed class AdvisorProfileDraft
{
    internal bool UseDefault { get; set; }
    internal string CustomName { get; set; }
    internal string Personality
    {
        get => UseDefault ? defaultPersonality : customPersonality;
        set { if (UseDefault) defaultPersonality = value; else customPersonality = value; }
    }
    private string defaultPersonality;
    private string customPersonality;
    private readonly string portraitFile;
    private byte[]? selectedPortrait;

    internal AdvisorProfileDraft(AdvisorProfile profile)
    {
        UseDefault = profile.UseDefault; CustomName = profile.CustomName;
        defaultPersonality = profile.DefaultPersonality; customPersonality = profile.CustomPersonality;
        portraitFile = profile.PortraitFile;
    }

    internal void SelectPortrait(byte[] image) => selectedPortrait = image;
    internal byte[] ReadActivePortrait(string directory, Func<byte[]> defaultPortrait) =>
        UseDefault ? defaultPortrait() : selectedPortrait ??
            new AdvisorProfile(AdvisorProfile.DefaultName, portraitFile).ReadPortrait(directory);

    internal AdvisorProfile Save(string directory)
    {
        var profile = new AdvisorProfile(CustomName, portraitFile, UseDefault, defaultPersonality, customPersonality);
        if (selectedPortrait != null) return profile.ImportPortrait(directory, selectedPortrait);
        profile.Save(directory);
        return profile;
    }
}
