using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// A local library editor. Neither migration nor editing writes the saved document.
internal sealed class AdvisorSettingsDraft
{
    private readonly JObject source;
    internal AdvisorConnectionOptions Options => new(source);
    private readonly List<AdvisorApiConfigurationDraft> configurations;
    internal IReadOnlyList<AdvisorApiConfigurationDraft> Configurations => configurations;
    // Presentation and discovery share an order without changing the saved library or its identities.
    internal IEnumerable<AdvisorApiConfigurationDraft> ConfigurationsByName => configurations
        .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal);
    internal string? MainId { get; set; }
    internal string? SubId { get; set; }
    internal string? TranslationId { get; set; }
    internal string? TranslationConnectionId => TranslationId == AdvisorApiLibrary.FollowAdvisor ? MainId : TranslationId;
    internal AdvisorApiConfigurationDraft? Translation => TranslationId == null ? null : Find(TranslationConnectionId) ?? throw new AiConnectionException("api_main_missing");
    internal bool LogAiJson { get; set; }
    internal AdvisorApiConfigurationDraft Main => Find(MainId) ?? throw new AiConnectionException("api_main_missing");
    internal AdvisorApiConfigurationDraft? Sub => Find(SubId);
    internal AdvisorApiConfigurationDraft? Find(string? id) => id == null ? null : configurations.SingleOrDefault(c => c.Id == id);

    internal AdvisorSettingsDraft(JObject source, string namePattern = "API setting {0}")
    {
        this.source = (JObject)source.DeepClone();
        var library = AdvisorApiLibrary.Read(source, namePattern);
        configurations = library.Configurations.Select(c => new AdvisorApiConfigurationDraft(c)).ToList();
        MainId = library.MainId; SubId = library.SubId; TranslationId = library.TranslationId;
        LogAiJson = (bool?)source["log_ai_json"] ?? false;
    }

    internal AdvisorApiConfigurationDraft Add(string namePattern = "API setting {0}")
    {
        var name = AiConnectionProfile.NewName(configurations.Select(c => c.Name), namePattern);
        var added = new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration(AiConnectionProfile.NewId(), name, "", AdvisorSettings.DefaultModel, Options.RequestTimeoutSeconds));
        configurations.Add(added);
        return added;
    }

    internal AdvisorApiConfigurationDraft Duplicate(string id, string nameSuffix = " - Copy")
    {
        var original = Find(id) ?? throw new AiConnectionException("api_selection_invalid");
        var copy = new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration(
            original.Build().Duplicate(nameSuffix).Id, original.Name + nameSuffix,
            original.Provider, original.Settings, original.RequestTimeoutSeconds, original.SituationDelivery, original.MaxOutputTokens, original.Prompt));
        configurations.Add(copy);
        return copy;
    }

    internal void Put(AdvisorApiConfigurationDraft entry)
    {
        var index = configurations.FindIndex(c => c.Id == entry.Id);
        var copy = new AdvisorApiConfigurationDraft(entry.Build());
        if (index < 0) configurations.Add(copy); else configurations[index] = copy;
    }

    internal void Remove(string id)
    {
        var item = Find(id) ?? throw new AiConnectionException("api_selection_invalid");
        configurations.Remove(item);
        // Deleting an assigned entry never silently selects a different credential.
        if (MainId == id) MainId = null;
        if (SubId == id) SubId = null;
        if (TranslationId == id) TranslationId = null;
    }

    // Tests use the chosen pending entry and saved logging preference, independently of role assignments.
    internal AdvisorSettings TestConfiguration(string id) => AdvisorSettings.Parse(
        AdvisorApiLibrary.Connection(source, (Find(id) ?? throw new AiConnectionException("api_selection_invalid")).Build()).ToString(Formatting.None));
    internal AdvisorSettings TestSettings(bool fallback = false) => TestConfiguration(fallback
        ? SubId ?? throw new AiConnectionException("fallback_key_missing")
        : MainId ?? throw new AiConnectionException("api_main_missing"));

    internal byte[] Data()
    {
        var library = new AdvisorApiLibrary(configurations.Select(c => c.Build()), MainId, SubId, TranslationId);
        var value = library.Document(source, LogAiJson);
        AdvisorSettings.ValidateDraft(value);
        var data = Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + Environment.NewLine);
        if (data.Length > AdvisorSettings.MaxDocumentBytes) throw new AiConnectionException("settings_invalid");
        return data;
    }
}
