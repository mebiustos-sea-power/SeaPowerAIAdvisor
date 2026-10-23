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
    internal const int MaxRoleLength = 80;
    internal const string DefaultId = "default";
    internal const int MaxProfileBytes = 1_048_576;
    internal bool UseDefault { get; }
    internal System.Collections.Generic.IReadOnlyList<AdvisorPersona> Advisers { get; }
    internal string? SelectedCustomId { get; }
    internal string ActiveId => UseDefault ? DefaultId : SelectedCustomId!;
    private AdvisorPersona? Custom => Advisers.SingleOrDefault(a => a.Id == SelectedCustomId);
    internal string CustomName => Custom?.Name ?? "";
    internal string CustomRole => Custom?.Role ?? "";
    internal string DisplayName => UseDefault ? DefaultName : CustomName;
    internal string PortraitFile => Custom?.PortraitFile ?? DefaultPortraitFile;
    internal string DefaultPersonality { get; }
    internal string CustomPersonality => Custom?.Personality ?? "";
    internal string Personality => UseDefault ? DefaultPersonality : CustomPersonality;
    // Retains the former single-custom constructor for value snapshots and legacy imports.
    internal AdvisorProfile(string name, string portraitFile, bool useDefault = false,
        string defaultPersonality = "", string customPersonality = "", string customRole = "")
        : this(new[] { new AdvisorPersona("custom", name, customRole, portraitFile, customPersonality) }, "custom", useDefault, defaultPersonality) { }
    internal AdvisorProfile(System.Collections.Generic.IEnumerable<AdvisorPersona> advisers, string? selectedCustomId,
        bool useDefault, string defaultPersonality)
    {
        var entries = advisers.ToArray();
        if (entries.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length ||
            (selectedCustomId != null && !entries.Any(a => a.Id == selectedCustomId)) ||
            (!useDefault && (selectedCustomId == null || entries.Single(a => a.Id == selectedCustomId).Name.Length == 0)))
            throw new AdvisorProfileException("profile_invalid");
        Advisers = Array.AsReadOnly(entries); SelectedCustomId = selectedCustomId ?? entries.FirstOrDefault()?.Id;
        UseDefault = useDefault; DefaultPersonality = AdvisorPersona.Style(defaultPersonality);
    }
    internal static string DirectoryIn(string gameDataPath) => Path.Combine(gameDataPath, "SeaPowerAIAdvisor");
    internal static string SettingsPath(string directory) => Path.Combine(directory, "profile.json");

    internal static void EnsureCreated(string directory, Func<byte[]> defaultPortrait, string? initialCustomRole = null)
    {
        try { Directory.CreateDirectory(directory); }
        catch (IOException) { throw new AdvisorProfileException("profile_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_write_failed"); }
        if (File.Exists(SettingsPath(directory)))
        {
            if (initialCustomRole != null)
            {
                var existing = Read(directory, initialCustomRole, out var roleMissing);
                // Initialize older profiles once; an explicit empty role is a saved user choice.
                if (roleMissing) existing.Save(directory);
            }
            return;
        }
        var portrait = Path.Combine(directory, DefaultPortraitFile);
        // A missing bundled resource is a deployment error, not a recoverable user-profile error.
        var defaults = File.Exists(portrait) ? null : defaultPortrait();
        try
        {
            if (defaults != null) WriteNew(portrait, defaults);
            WriteNew(SettingsPath(directory), Encoding.UTF8.GetBytes(new AdvisorProfile(DefaultName, DefaultPortraitFile, true,
                defaultPersonality: "Calm, professional and courteous.", customRole: initialCustomRole ?? "").Json()));
        }
        catch (IOException) { throw new AdvisorProfileException("profile_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_write_failed"); }
    }

    internal static AdvisorProfile Load(string directory) => Read(directory, "", out _);

    private static AdvisorProfile Read(string directory, string initialCustomRole, out bool roleMissing)
    {
        var path = SettingsPath(directory);
        if (!File.Exists(path)) throw new AdvisorProfileException("profile_missing");
        try
        {
            if (new FileInfo(path).Length > MaxProfileBytes) throw new AdvisorProfileException("profile_invalid");
            return Parse(File.ReadAllText(path, new UTF8Encoding(false, true)), initialCustomRole, out roleMissing);
        }
        catch (DecoderFallbackException) { throw new AdvisorProfileException("profile_invalid"); }
        catch (IOException) { throw new AdvisorProfileException("profile_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_unreadable"); }
    }
    internal static AdvisorProfile Parse(string json) => Parse(json, "", out _);

    private static AdvisorProfile Parse(string json, string initialCustomRole, out bool roleMissing)
    {
        try
        {
            using var source = new StringReader(json);
            using var reader = new JsonTextReader(source) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new AdvisorProfileException("profile_invalid");
            if (value["schema_version"] != null)
            {
                roleMissing = false;
                var fields = new[] { "schema_version", "mode", "selected_custom_id", "default_personality", "custom_advisors" };
                if (value["schema_version"]!.Type != JTokenType.Integer || value["schema_version"]!.ToString(Formatting.None) != "2" ||
                    value.Properties().Any(p => !fields.Contains(p.Name)) || value["mode"]?.Type != JTokenType.String ||
                    ((string?)value["mode"] != "default" && (string?)value["mode"] != "custom") ||
                    value["default_personality"]?.Type != JTokenType.String ||
                    (value["selected_custom_id"]?.Type != JTokenType.String && value["selected_custom_id"]?.Type != JTokenType.Null) ||
                    !(value["custom_advisors"] is JArray advisers) || advisers.Any(a => !(a is JObject)))
                    throw new AdvisorProfileException("profile_invalid");
                return new AdvisorProfile(advisers.Cast<JObject>().Select(AdvisorPersona.Parse), (string?)value["selected_custom_id"],
                    (string?)value["mode"] == "default", (string)value["default_personality"]!);
            }
            if (value.Properties().Any(p => p.Name != "mode" && p.Name != "display_name" &&
                p.Name != "portrait_file" && p.Name != "personality" && p.Name != "default_personality" && p.Name != "custom_personality" && p.Name != "custom_role") ||
                value["display_name"]?.Type != JTokenType.String || value["portrait_file"]?.Type != JTokenType.String ||
                (value["mode"] != null && (value["mode"]!.Type != JTokenType.String ||
                    ((string?)value["mode"] != "default" && (string?)value["mode"] != "custom"))) ||
                new[] { "personality", "default_personality", "custom_personality", "custom_role" }.Any(key =>
                    value[key] != null && value[key]!.Type != JTokenType.String))
                throw new AdvisorProfileException("profile_invalid");
            // Legacy profiles remain custom; the former shared personality supplies missing per-mode values.
            var legacyPersonality = (string?)value["personality"] ?? "";
            roleMissing = value["custom_role"] == null;
            return new AdvisorProfile((string)value["display_name"]!, (string)value["portrait_file"]!,
                (string?)value["mode"] == "default",
                (string?)value["default_personality"] ?? legacyPersonality,
                (string?)value["custom_personality"] ?? legacyPersonality,
                (string?)value["custom_role"] ?? initialCustomRole);
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
        var imported = WithPortrait(SelectedCustomId ?? throw new AdvisorProfileException("profile_invalid"),
            "portrait-import-" + Guid.NewGuid().ToString("N") + extension);
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

    internal AdvisorProfile WithPortrait(string id, string file)
    {
        if (!Advisers.Any(a => a.Id == id)) throw new AdvisorProfileException("profile_invalid");
        return new AdvisorProfile(Advisers.Select(a => a.Id == id ? new AdvisorPersona(a.Id, a.Name, a.Role, file, a.Personality) : a),
            SelectedCustomId, UseDefault, DefaultPersonality);
    }
    internal void DeleteUnusedPortraits(string directory, AdvisorProfile next, Action<string> warn)
    {
        var retained = next.Advisers.Select(a => a.PortraitFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Advisers.Select(a => a.PortraitFile).Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !retained.Contains(f)))
            DeletePortraitFile(directory, file, warn);
    }
    internal void DeleteImportedPortrait(string directory, Action<string> warn) => DeletePortraitFile(directory, PortraitFile, warn);
    private static void DeletePortraitFile(string directory, string portraitFile, Action<string> warn)
    {
        const string prefix = "portrait-import-";
        // Only remove our own previous imported copy; never delete user-selected source files or custom paths.
        var stem = Path.GetFileNameWithoutExtension(portraitFile);
        if (Path.GetFileName(portraitFile) != portraitFile || !PortraitImage.SupportsPath(portraitFile) ||
            stem.Length != prefix.Length + 32 || !stem.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(stem.Substring(prefix.Length), "N", out _)) return;
        try { File.Delete(Path.Combine(directory, portraitFile)); }
        catch (IOException error) { warn("Cannot remove the previous imported portrait: " + error.Message); }
        catch (UnauthorizedAccessException error) { warn("Cannot remove the previous imported portrait: " + error.Message); }
    }

    internal void Save(string directory)
    {
        var data = Encoding.UTF8.GetBytes(Json());
        if (data.Length > MaxProfileBytes) throw new AdvisorProfileException("profile_invalid");
        var path = SettingsPath(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            WriteNew(temporary, data);
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
        ["schema_version"] = 2, ["mode"] = UseDefault ? "default" : "custom",
        ["selected_custom_id"] = SelectedCustomId, ["default_personality"] = DefaultPersonality,
        ["custom_advisors"] = new JArray(Advisers.Select(a => a.Json()))
    }.ToString(Formatting.Indented) + Environment.NewLine;
    internal static void WriteNew(string path, byte[] bytes)
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
