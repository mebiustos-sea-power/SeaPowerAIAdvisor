using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Form edits and selected images stay local until the registration operation saves them.
internal sealed class AdvisorProfileDraft
{
    private readonly List<AdvisorPersonaDraft> advisers;
    private readonly Dictionary<string, byte[]> selectedPortraits = new(StringComparer.Ordinal);
    private string defaultPersonality;
    private bool useDefault;
    internal string? SelectedCustomId { get; private set; }
    internal string SelectedId => UseDefault ? AdvisorProfile.DefaultId : SelectedCustomId!;
    internal IReadOnlyList<AdvisorPersonaDraft> Advisers => advisers.AsReadOnly();
    private AdvisorPersonaDraft Custom => advisers.SingleOrDefault(a => a.Id == SelectedCustomId) ?? throw new AdvisorProfileException("profile_invalid");
    internal bool UseDefault
    {
        get => useDefault;
        set { if (!value && SelectedCustomId == null) throw new AdvisorProfileException("profile_invalid"); useDefault = value; }
    }
    internal string CustomName { get => Custom.Name; set => Custom.Name = value; }
    internal string CustomRole { get => Custom.Role; set => Custom.Role = value; }
    internal string Personality
    {
        get => UseDefault ? defaultPersonality : Custom.Personality;
        set { if (UseDefault) defaultPersonality = value; else Custom.Personality = value; }
    }
    internal AdvisorProfileDraft(AdvisorProfile profile)
    {
        advisers = profile.Advisers.Select(a => new AdvisorPersonaDraft(a)).ToList();
        SelectedCustomId = profile.SelectedCustomId; useDefault = profile.UseDefault; defaultPersonality = profile.DefaultPersonality;
    }
    internal void Select(string id)
    {
        if (id == AdvisorProfile.DefaultId) { UseDefault = true; return; }
        if (!advisers.Any(a => a.Id == id)) throw new AdvisorProfileException("profile_invalid");
        SelectedCustomId = id; UseDefault = false;
    }
    internal string Add(string name, string role)
    {
        var id = Guid.NewGuid().ToString("N");
        advisers.Add(new AdvisorPersonaDraft(new AdvisorPersona(id, name, role, AdvisorProfile.DefaultPortraitFile, "")));
        Select(id); return id;
    }
    internal void Remove(string id)
    {
        var found = advisers.SingleOrDefault(a => a.Id == id) ?? throw new AdvisorProfileException("profile_invalid");
        advisers.Remove(found); selectedPortraits.Remove(id);
        if (SelectedCustomId == id) { useDefault = true; SelectedCustomId = advisers.FirstOrDefault()?.Id; }
    }
    internal void SelectPortrait(byte[] image) => SelectPortrait(SelectedCustomId ?? throw new AdvisorProfileException("profile_invalid"), image);
    internal void SelectPortrait(string id, byte[] image)
    {
        if (!advisers.Any(a => a.Id == id)) throw new AdvisorProfileException("profile_invalid");
        selectedPortraits[id] = (byte[])image.Clone();
    }
    internal byte[] ReadActivePortrait(string directory, Func<byte[]> defaultPortrait) => UseDefault ? defaultPortrait() :
        selectedPortraits.TryGetValue(Custom.Id, out var image) ? image :
        new AdvisorProfile(Custom.Name, Custom.PortraitFile).ReadPortrait(directory);
    internal bool HasSelectedPortrait => selectedPortraits.ContainsKey(SelectedId);
    internal byte[]? SelectedPortrait(string id) => selectedPortraits.TryGetValue(id, out var bytes) ? (byte[])bytes.Clone() : null;
    internal AdvisorProfile Build() => new(advisers.Select(a => a.Build()), SelectedCustomId, UseDefault, defaultPersonality);
    internal AdvisorProfile Save(string directory)
    {
        var profile = Build();
        var created = new List<string>();
        var saved = false;
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var pair in selectedPortraits)
            {
                var name = "portrait-import-" + Guid.NewGuid().ToString("N") + PortraitImage.Extension(pair.Value);
                var path = Path.Combine(directory, name);
                AdvisorProfile.WriteNew(path, pair.Value); created.Add(path);
                profile = profile.WithPortrait(pair.Key, name);
            }
            profile.Save(directory); saved = true; return profile;
        }
        catch (IOException) { throw new AdvisorProfileException("profile_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorProfileException("profile_write_failed"); }
        finally { if (!saved) foreach (var path in created) File.Delete(path); }
    }
}
