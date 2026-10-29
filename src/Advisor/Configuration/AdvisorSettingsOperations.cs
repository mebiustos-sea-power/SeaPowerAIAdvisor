using System;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Each operation reads current storage and changes one registration or one form, never another category.
internal sealed class AdvisorApiSettingsStore : IAiConnectionStore
{
    System.Collections.Generic.IReadOnlyList<AiConnectionProfile> IAiConnectionStore.ReadConnections() => Read().ConfigurationsByName.Select(c => c.Build()).ToArray();
    AiConnectionDraft IAiConnectionStore.Create(string pattern) => Read().Add(pattern);
    AiConnectionDraft IAiConnectionStore.Edit(string id) => new AdvisorApiConfigurationDraft((Read().Find(id) ?? throw new AiConnectionException("api_selection_invalid")).Build());
    void IAiConnectionStore.Save(AiConnectionDraft entry, bool adding) => SaveEntry((AdvisorApiConfigurationDraft)entry, adding);
    void IAiConnectionStore.Duplicate(string id, string suffix) => Duplicate(id, suffix);
    void IAiConnectionStore.Remove(string id) => Remove(id);
    private readonly string path;
    private readonly Func<string> namePattern;
    internal AdvisorApiSettingsStore(string path, Func<string> namePattern)
    { this.path = path; this.namePattern = namePattern; }
    internal AdvisorSettingsDraft Read() => AdvisorSettings.Edit(path, namePattern());
    internal AdvisorSettingsDraft SaveEntry(AdvisorApiConfigurationDraft entry, bool adding)
    {
        var current = Read();
        if (adding == (current.Find(entry.Id) != null)) throw new AiConnectionException("api_selection_invalid");
        current.Put(entry); Save(current); return Read();
    }
    internal AdvisorSettingsDraft Duplicate(string id, string nameSuffix)
    { var current = Read(); current.Duplicate(id, nameSuffix); Save(current); return Read(); }
    internal AdvisorSettingsDraft Remove(string id)
    { var current = Read(); current.Remove(id); Save(current); return Read(); }
    internal AdvisorSettingsDraft Assign(string? main, string? sub, string? translation = null)
    {
        if (main == null) throw new AiConnectionException("api_main_missing");
        var current = Read(); current.MainId = main; current.SubId = sub; current.TranslationId = translation; Save(current); return Read();
    }
    internal AdvisorSettings Test(AdvisorApiConfigurationDraft entry)
    { var current = Read(); current.Put(entry); return current.TestConfiguration(entry.Id); }
    internal AdvisorConnectionOptions TestOptions() => Read().Options;
    internal void SaveLogging(bool enabled)
    { var current = Read(); current.LogAiJson = enabled; Save(current); }
    private void Save(AdvisorSettingsDraft value) => AdvisorSettings.WriteBytes(path, value.Data());
}

internal sealed class AdvisorProfileSettingsStore
{
    private readonly string directory;
    private readonly Action<string> warn;
    internal AdvisorProfileSettingsStore(string directory, Action<string> warn)
    { this.directory = directory; this.warn = warn; }
    internal AdvisorProfile Read() => AdvisorProfile.Load(directory);
    internal AdvisorProfile Select(string id)
    { var draft = new AdvisorProfileDraft(Read()); draft.Select(id); return draft.Save(directory); }
    internal AdvisorProfile Remove(string id)
    {
        var previous = Read(); var draft = new AdvisorProfileDraft(previous); draft.Remove(id);
        var next = draft.Save(directory); previous.DeleteUnusedPortraits(directory, next, warn); return next;
    }
    internal AdvisorProfile SaveEntry(AdvisorProfileDraft form, string id, bool adding)
    {
        var edited = form.Build(); var current = Read();
        AdvisorProfile merged;
        if (id == AdvisorProfile.DefaultId)
            merged = new AdvisorProfile(current.Advisers, current.SelectedCustomId, current.UseDefault, edited.DefaultPersonality);
        else
        {
            var entry = edited.Advisers.SingleOrDefault(a => a.Id == id) ?? throw new AdvisorProfileException("profile_invalid");
            if (entry.Name.Length == 0 || adding == current.Advisers.Any(a => a.Id == id)) throw new AdvisorProfileException("profile_invalid");
            merged = new AdvisorProfile(current.Advisers.Where(a => a.Id != id).Concat(new[] { entry }),
                current.SelectedCustomId, current.UseDefault, current.DefaultPersonality);
        }
        var output = new AdvisorProfileDraft(merged);
        var image = form.SelectedPortrait(id);
        if (image != null) output.SelectPortrait(id, image);
        var saved = output.Save(directory); current.DeleteUnusedPortraits(directory, saved, warn); return saved;
    }
}
