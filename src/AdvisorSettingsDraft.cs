using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Editing is local to the settings screen; generating data validates but does not save it.
internal sealed class AdvisorSettingsDraft
{
    private readonly JObject source;
    internal string ApiKey { get; set; }
    internal string FallbackApiKey { get; set; }
    internal string Model { get; set; }
    internal string FallbackModel { get; set; }
    internal bool LogAiJson { get; set; }

    internal AdvisorSettingsDraft(JObject source)
    {
        this.source = (JObject)source.DeepClone();
        ApiKey = String("api_key", "");
        FallbackApiKey = String("fallback_api_key", "");
        Model = String("model", AdvisorSettings.DefaultModel);
        FallbackModel = String("fallback_model", Model);
        LogAiJson = (bool?)source["log_ai_json"] ?? false;
    }

    private string String(string key, string fallback) => source[key] == null ? fallback :
        source[key]!.Type == JTokenType.String ? (string)source[key]! : throw new AdvisorConnectionException("settings_invalid");

    // Testing uses pending credentials, but the log switch only takes effect after Save.
    internal AdvisorSettings TestSettings(bool fallback = false)
    {
        var value = (JObject)source.DeepClone();
        value["api_key"] = ApiKey.Trim();
        value["fallback_api_key"] = FallbackApiKey.Trim();
        value["model"] = Model.Trim();
        value["fallback_model"] = FallbackModel.Trim();
        var settings = AdvisorSettings.Parse(value.ToString(Formatting.None));
        return fallback ? settings.ForFallback() : settings;
    }

    internal byte[] Data()
    {
        var value = (JObject)source.DeepClone();
        value["api_key"] = ApiKey.Trim();
        value["fallback_api_key"] = FallbackApiKey.Trim();
        value["model"] = Model.Trim();
        value["fallback_model"] = FallbackModel.Trim();
        value["log_ai_json"] = LogAiJson;
        AdvisorSettings.ValidateDraft(value);
        return Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + Environment.NewLine);
    }
}
