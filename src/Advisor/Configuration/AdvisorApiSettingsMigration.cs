using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal static class AdvisorApiSettingsMigration
{
    internal const int CurrentVersion = 4;
    internal static JObject Upgrade(JObject source, string namePattern = "API setting {0}") =>
        ConfigMigration.Upgrade(source, CurrentVersion, (value, version) =>
        {
            switch (version)
            {
                case 0: ImportSingleConnection(value, namePattern); break;
                case 2: ImportGeminiLibrary(value); break;
                case 3: NormalizeConnectionMetadata(value); break;
                default: throw new AiConnectionException("settings_invalid");
            }
        });

    private static void ImportSingleConnection(JObject value, string namePattern)
    {
        if (value.Properties().Any(p => p.Name is not ("api_key" or "fallback_api_key" or "model" or "fallback_model" or
            "request_timeout_seconds" or "fallback_request_timeout_seconds" or "consultation_timeout_seconds" or "max_output_tokens" or "log_ai_json")))
            throw new AiConnectionException("settings_invalid");
        string Text(string key, string fallback) => value[key] == null ? fallback : value[key]!.Type == JTokenType.String
            ? (string)value[key]! : throw new AiConnectionException(key == "fallback_model" ? "fallback_model_invalid" : "settings_invalid");
        var timeout = AiRequestTimeout.Read(value);
        var outputTokens = AiOutputTokens.Read(value);
        var model = Text("model", AdvisorSettings.DefaultModel);
        var main = new AdvisorApiConfiguration("api-1", string.Format(CultureInfo.InvariantCulture, namePattern, 1), Text("api_key", ""), model, timeout, outputTokens);
        var subTimeout = value["fallback_request_timeout_seconds"] == null ? timeout :
            AiRequestTimeout.Parse(value["fallback_request_timeout_seconds"]!.Type == JTokenType.Integer ? value["fallback_request_timeout_seconds"]!.ToString() : "");
        var sub = new AdvisorApiConfiguration("api-2", string.Format(CultureInfo.InvariantCulture, namePattern, 2),
            Text("fallback_api_key", ""), Text("fallback_model", model), subTimeout, outputTokens);
        if (sub.ApiKey.Length > 0 && sub.Model.Length == 0) throw new AiConnectionException("fallback_model_invalid");
        var entries = new JArray();
        if (main.ApiKey.Length > 0) entries.Add(main.Json());
        if (sub.ApiKey.Length > 0 && sub.Model.Length > 0) entries.Add(sub.Json());
        foreach (var key in new[] { "api_key", "fallback_api_key", "model", "fallback_model", "fallback_request_timeout_seconds", "max_output_tokens" }) value.Remove(key);
        value["api_configurations"] = entries;
        value["main_api_id"] = main.ApiKey.Length > 0 ? new JValue(main.Id) : JValue.CreateNull();
        value["sub_api_id"] = sub.ApiKey.Length > 0 && sub.Model.Length > 0 ? new JValue(sub.Id) : JValue.CreateNull();
        value["schema_version"] = 3;
    }

    private static void ImportGeminiLibrary(JObject value)
    {
        if (value["api_configurations"] is not JArray entries) throw new AiConnectionException("settings_invalid");
        var timeout = AiRequestTimeout.Read(value);
        foreach (var token in entries)
        {
            if (token is not JObject item || item.Properties().Any(p => p.Name is not ("id" or "name" or "api_key" or "model")) ||
                new[] { "id", "name", "api_key", "model" }.Any(k => item[k]?.Type != JTokenType.String))
                throw new AiConnectionException("settings_invalid");
            var settings = new JObject { ["api_key"] = item["api_key"]!.DeepClone(), ["model"] = item["model"]!.DeepClone() };
            item.Remove("api_key"); item.Remove("model");
            item["provider"] = "gemini"; item["settings"] = settings; item["request_timeout_seconds"] = timeout;
        }
        value["schema_version"] = 3;
    }

    private static void NormalizeConnectionMetadata(JObject value)
    {
        if (value["api_configurations"] is not JArray entries ||
            new[] { "api_key", "fallback_api_key", "model", "fallback_model" }.Any(k => value[k] != null))
            throw new AiConnectionException("settings_invalid");
        var timeout = AiRequestTimeout.Read(value);
        foreach (var token in entries)
        {
            if (token is not JObject item || item["settings"] is not JObject settings) throw new AiConnectionException("settings_invalid");
            if ((string?)item["provider"] == "nanogpt" && settings.TryGetValue("max_output_tokens", out var old))
            {
                if (item["max_output_tokens"] == null)
                {
                    if (old.Type != JTokenType.String) throw new AiConnectionException("output_tokens_invalid");
                    item["max_output_tokens"] = AiOutputTokens.Parse(old.ToString());
                }
                settings.Remove("max_output_tokens");
            }
            if (item["request_timeout_seconds"] == null) item["request_timeout_seconds"] = timeout;
        }
        value.Remove("max_output_tokens"); value.Remove("fallback_request_timeout_seconds"); value.Remove("consultation_timeout_seconds");
        value["schema_version"] = CurrentVersion;
    }

    // File location changes precede document migration and never overwrite an existing destination.
    internal static string ResolvePath(string gameDataPath)
    {
        var path = AdvisorSettings.PathIn(gameDataPath);
        static bool Exists(string candidate)
        {
            try
            {
                if ((File.GetAttributes(candidate) & FileAttributes.Directory) != 0) throw new IOException();
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        try
        {
            if (Exists(path)) return path;
            var old = Path.Combine(gameDataPath, "SeaPowerAIAdvisor", "gemini.json");
            if (Exists(old)) File.Move(old, path);
            return path;
        }
        catch (IOException) { throw new AiConnectionException("settings_migration_failed"); }
        catch (UnauthorizedAccessException) { throw new AiConnectionException("settings_migration_failed"); }
    }
}
