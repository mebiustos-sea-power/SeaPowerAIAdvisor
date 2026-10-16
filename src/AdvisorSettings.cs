using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorSettings
{
    internal const string DefaultModel = "gemini-3.5-flash-lite";
    internal string ApiKey { get; }
    internal string Model { get; }
    internal int RequestTimeoutSeconds { get; }
    internal int ConsultationTimeoutSeconds { get; }
    internal int MaxOutputTokens { get; }
    internal bool LogAiJson { get; }
    internal static string PathIn(string gameDataPath) => Path.Combine(gameDataPath, "SeaPowerAIAdvisor", "gemini.json");

    private AdvisorSettings(string key, string model, int requestTimeout, int totalTimeout, int outputTokens, bool logAiJson)
    { ApiKey = key; Model = model; RequestTimeoutSeconds = requestTimeout; ConsultationTimeoutSeconds = totalTimeout; MaxOutputTokens = outputTokens; LogAiJson = logAiJson; }

    internal static AdvisorSettings Load(string path)
    {
        if (!File.Exists(path)) throw new AdvisorConnectionException("settings_missing");
        string text;
        try
        {
            if (new FileInfo(path).Length > 16_384) throw new AdvisorConnectionException("settings_invalid");
            text = File.ReadAllText(path);
        }
        catch (IOException) { throw new AdvisorConnectionException("settings_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AdvisorConnectionException("settings_unreadable"); }
        return Parse(text);
    }

    internal static AdvisorSettings Parse(string json)
    {
        JObject value;
        try
        {
            using var source = new StringReader(json);
            using var reader = new JsonTextReader(source) { MaxDepth = 4, DateParseHandling = DateParseHandling.None };
            value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new JsonReaderException();
        }
        catch (JsonException) { throw new AdvisorConnectionException("settings_invalid"); }
        if (value.Properties().Any(p => !new[] { "api_key", "model", "request_timeout_seconds", "consultation_timeout_seconds", "max_output_tokens", "log_ai_json" }.Contains(p.Name)))
            throw new AdvisorConnectionException("settings_invalid");
        if (value["api_key"]?.Type != JTokenType.String) throw new AdvisorConnectionException("api_key_missing");
        var key = ((string)value["api_key"]!).Trim();
        if (key.Length == 0) throw new AdvisorConnectionException("api_key_missing");
        if (!Regex.IsMatch(key, @"\A[!-~]{1,512}\z")) throw new AdvisorConnectionException("settings_invalid");
        var model = value["model"] == null ? DefaultModel : value["model"]?.Type == JTokenType.String ? (string)value["model"]! : "";
        if (!Regex.IsMatch(model, @"\Agemini-[0-9]+(?:\.[0-9]+)?-flash-lite(?:-[a-z0-9-]+)?\z") || model.Length > 100)
            throw new AdvisorConnectionException("model_invalid");
        var request = Number(value, "request_timeout_seconds", 60, 5, 120);
        var total = Number(value, "consultation_timeout_seconds", 180, 10, 600);
        if (total < request) throw new AdvisorConnectionException("settings_invalid");
        if (value["log_ai_json"] != null && value["log_ai_json"]!.Type != JTokenType.Boolean)
            throw new AdvisorConnectionException("settings_invalid");
        return new AdvisorSettings(key, model, request, total, Number(value, "max_output_tokens", 4096, 256, 8192),
            (bool?)value["log_ai_json"] ?? true);
    }

    private static int Number(JObject value, string name, int fallback, int min, int max)
    {
        if (value[name] == null) return fallback;
        if (value[name]!.Type != JTokenType.Integer || !int.TryParse(value[name]!.ToString(), out var number) || number < min || number > max)
            throw new AdvisorConnectionException("settings_invalid");
        return number;
    }
}

// Only fixed error codes and HTTP statuses cross the provider/UI boundary; never a raw response or key.
internal sealed class AdvisorConnectionException : Exception
{
    internal string Code { get; }
    internal int? HttpStatus { get; }
    internal AdvisorConnectionException(string code, int? httpStatus = null) : base(code + (httpStatus.HasValue ? " HTTP " + httpStatus : ""))
    { Code = code; HttpStatus = httpStatus; }
}
