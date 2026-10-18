using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// One registered custom adviser. Identity survives renames and does not depend on the image filename.
internal sealed class AdvisorPersona
{
    internal string Id { get; }
    internal string Name { get; }
    internal string Role { get; }
    internal string PortraitFile { get; }
    internal string Personality { get; }
    internal AdvisorPersona(string id, string name, string role, string portraitFile, string personality)
    {
        id = id.Trim(); name = name.Trim(); role = role.Trim(); portraitFile = portraitFile.Trim();
        if (id.Length == 0 || id.Length > 80 || id == AdvisorProfile.DefaultId || id.Any(char.IsControl) ||
            name.Length > 80 || name.Any(char.IsControl) || portraitFile.Length == 0 || portraitFile.Length > 1024 || portraitFile.Any(char.IsControl))
            throw new AdvisorProfileException("profile_invalid");
        if (role.Length > AdvisorProfile.MaxRoleLength || role.Any(char.IsControl)) throw new AdvisorProfileException("role_invalid");
        try { if (!PortraitImage.SupportsPath(portraitFile)) throw new AdvisorProfileException("profile_invalid"); }
        catch (ArgumentException) { throw new AdvisorProfileException("profile_invalid"); }
        personality = Style(personality);
        try { _ = new UTF8Encoding(false, true).GetByteCount(id + name + role + portraitFile); }
        catch (EncoderFallbackException) { throw new AdvisorProfileException("profile_invalid"); }
        Id = id; Name = name; Role = role; PortraitFile = portraitFile; Personality = personality;
    }
    internal static string Style(string value)
    {
        value = value.Trim();
        if (value.Length > AdvisorProfile.MaxPersonalityLength || value.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
            throw new AdvisorProfileException("personality_invalid");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new AdvisorProfileException("profile_invalid"); }
        return value;
    }
    internal JObject Json() => new() { ["id"] = Id, ["display_name"] = Name, ["role"] = Role, ["portrait_file"] = PortraitFile, ["personality"] = Personality };
    internal static AdvisorPersona Parse(JObject value)
    {
        var fields = new[] { "id", "display_name", "role", "portrait_file", "personality" };
        if (value.Properties().Any(p => !fields.Contains(p.Name)) || fields.Any(f => value[f]?.Type != JTokenType.String))
            throw new AdvisorProfileException("profile_invalid");
        return new AdvisorPersona((string)value["id"]!, (string)value["display_name"]!, (string)value["role"]!, (string)value["portrait_file"]!, (string)value["personality"]!);
    }
}

internal sealed class AdvisorPersonaDraft
{
    internal string Id { get; }
    internal string Name { get; set; }
    internal string Role { get; set; }
    internal string PortraitFile { get; }
    internal string Personality { get; set; }
    internal AdvisorPersonaDraft(AdvisorPersona persona)
    { Id = persona.Id; Name = persona.Name; Role = persona.Role; PortraitFile = persona.PortraitFile; Personality = persona.Personality; }
    internal AdvisorPersona Build() => new(Id, Name, Role, PortraitFile, Personality);
}
