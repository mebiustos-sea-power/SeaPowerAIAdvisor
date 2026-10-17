using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// One local append-only stream per consultation. Receives bodies, never HTTP headers or settings JSON.
internal sealed class AdvisorTrace : IDisposable
{
    private readonly StreamWriter writer;
    private readonly string[] secrets;
    private readonly string id;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int sequence;
    internal string FilePath { get; }
    internal static string DirectoryIn(string gameDataPath) => Path.Combine(gameDataPath, "SeaPowerAIAdvisor", "logs");

    private AdvisorTrace(string directory, string id, AdvisorSettings settings, AdvisorQuestion question)
    {
        this.id = id;
        secrets = new[] { settings.ApiKey, settings.FallbackApiKey }.Where(k => k.Length > 0)
            .SelectMany(KeyVariants).Distinct().OrderByDescending(k => k.Length).ToArray();
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "consultation-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss.fff'Z'", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N") + ".jsonl");
        writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        try
        {
            Write("consultation_started", new JObject
            {
                ["mod_version"] = typeof(AdvisorTrace).Assembly.GetName().Version!.ToString(), ["api"] = "interactions",
                ["model"] = settings.RequestModel, ["fallback_model"] = settings.FallbackModel, ["display_question"] = question.DisplayText,
                ["question_source"] = question.IsShortcut ? "shortcut" : "typed"
            });
        }
        catch { writer.Dispose(); throw; }
    }

    internal static AdvisorTrace? Start(string directory, string id, AdvisorSettings settings, AdvisorQuestion question)
    {
        if (!settings.LogAiJson) return null;
        try { return new AdvisorTrace(directory, id, settings, question); }
        catch (IOException) { throw new AdvisorConnectionException("diagnostic_log_error"); }
        catch (UnauthorizedAccessException) { throw new AdvisorConnectionException("diagnostic_log_error"); }
    }

    internal void Request(int request, string json, bool fallback = false, string? model = null) => Write("request", new JObject
    {
        ["api_request"] = request, ["direction"] = "mod_to_ai", ["key_slot"] = fallback ? "fallback" : "primary", ["model"] = model, ["body_json"] = RedactBody(json)
    });

    internal void Fallback(AdvisorConnectionException error) => Write("key_changed", new JObject
    {
        ["from_key"] = "primary", ["to_key"] = "fallback", ["error_code"] = error.Code, ["http_status"] = error.HttpStatus
    });

    internal void Response(int request, int status, string body, bool complete, long receivedBytes) => Write("response", new JObject
    {
        ["api_request"] = request, ["direction"] = "ai_to_mod", ["http_status"] = status,
        ["body_complete"] = complete, ["received_bytes"] = receivedBytes, ["body_json"] = RedactBody(body)
    });

    internal void RequestFailed(int request, string code, int? status = null) => Write("request_failed", new JObject
    { ["api_request"] = request, ["error_code"] = code, ["http_status"] = status });

    internal void Finish(string outcome, AdviceResult? result = null, string? errorCode = null, int? status = null, string? errorType = null) =>
        Write("consultation_finished", new JObject
        {
            ["outcome"] = outcome, ["error_code"] = errorCode, ["http_status"] = status, ["error_type"] = errorType,
            ["model"] = result?.Model, ["api_requests"] = result?.ApiRequests, ["query_rounds"] = result?.QueryRounds, ["total_tokens"] = result?.TotalTokens
        });

    private void Write(string kind, JObject record)
    {
        record["schema_version"] = 1;
        record["consultation_id"] = id;
        record["sequence"] = ++sequence;
        record["timestamp_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        record["elapsed_ms"] = clock.ElapsedMilliseconds;
        record["event"] = kind;
        RedactToken(record);
        try
        {
            writer.WriteLine(record.ToString(Formatting.None));
            writer.Flush();
        }
        catch (IOException) { throw new AdvisorConnectionException("diagnostic_log_error"); }
        catch (UnauthorizedAccessException) { throw new AdvisorConnectionException("diagnostic_log_error"); }
    }

    // Preserve the original JSON text unless a secret must be removed. Parsing also
    // catches an echoed key encoded as JSON escapes; malformed bodies remain useful text.
    private string RedactBody(string body)
    {
        try
        {
            using var source = new StringReader(body);
            using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) return Scrub(body);
            return RedactToken(token) ? token.ToString(Formatting.None) : body;
        }
        catch (JsonException) { return Scrub(body); }
    }

    private bool RedactToken(JToken token)
    {
        var changed = false;
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties().ToArray())
            {
                changed |= RedactToken(property.Value);
                var name = Scrub(property.Name);
                if (name != property.Name)
                {
                    property.Replace(new JProperty(name, property.Value.DeepClone()));
                    changed = true;
                }
            }
        }
        else if (token is JArray array)
            foreach (var item in array) changed |= RedactToken(item);
        else if (token is JValue value && value.Type == JTokenType.String && value.Value is string original)
        {
            var safe = Scrub(original);
            if (safe != original) { value.Value = safe; changed = true; }
        }
        return changed;
    }

    private static string[] KeyVariants(string key)
    {
        var variants = new string[5];
        variants[0] = key;
        for (var i = 1; i < 4; i++)
        {
            var quoted = JsonConvert.ToString(variants[i - 1]);
            variants[i] = quoted.Substring(1, quoted.Length - 2);
        }
        variants[4] = Uri.EscapeDataString(key);
        return variants;
    }

    private string Scrub(string value)
    {
        foreach (var secret in secrets)
            value = value.Replace(secret, "<api-key-removed>");
        return value;
    }

    public void Dispose()
    {
        try { writer.Dispose(); }
        catch (IOException) { throw new AdvisorConnectionException("diagnostic_log_error"); }
    }
}
