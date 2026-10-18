using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Consultation identity and redaction are separate from the shared game-run log file.
// Receives bodies, never HTTP headers or settings JSON.
internal sealed class AdvisorTrace : IDisposable
{
    private static readonly AdvisorLogRun Log = new();
    private bool disposed;
    private readonly string[] secrets;
    private readonly string id;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int sequence;
    internal string FilePath { get; }
    internal static string DirectoryIn(string gameDataPath) => Path.Combine(gameDataPath, "SeaPowerAIAdvisor", "logs");

    private AdvisorTrace(string directory, string id, AdvisorTraceOptions settings, AdvisorQuestion question)
    {
        this.id = id;
        secrets = settings.Secrets.Where(k => k.Length > 0)
            .SelectMany(KeyVariants).Distinct().OrderByDescending(k => k.Length).ToArray();
        FilePath = Path.GetFullPath(Path.Combine(directory, "ai-communication.jsonl"));
        Write("consultation_started", new JObject
        {
            ["mod_version"] = typeof(AdvisorTrace).Assembly.GetName().Version!.ToString(), ["api"] = settings.Api,
            ["model"] = settings.Model, ["fallback_model"] = settings.FallbackModel, ["display_question"] = question.DisplayText,
            ["question_source"] = question.IsShortcut ? "shortcut" : "typed"
        });
    }

    internal static AdvisorTrace? Start(string directory, string id, AdvisorTraceOptions settings, AdvisorQuestion question)
    {
        if (!settings.Enabled) return null;
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
        if (disposed) throw new ObjectDisposedException(nameof(AdvisorTrace));
        record["schema_version"] = 1;
        record["consultation_id"] = id;
        record["sequence"] = ++sequence;
        record["timestamp_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        record["elapsed_ms"] = clock.ElapsedMilliseconds;
        record["event"] = kind;
        RedactToken(record);
        try
        {
            Log.Write(FilePath, record.ToString(Formatting.None));
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

    public void Dispose() => disposed = true;
}

// One instance lives for the game run; it touches a file only when an enabled trace writes.
internal sealed class AdvisorLogRun
{
    private readonly object gate = new();
    private readonly HashSet<string> initialized = new(StringComparer.OrdinalIgnoreCase);

    internal void Write(string path, string line)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var mode = initialized.Contains(path) ? FileMode.Append : FileMode.Create;
            // Close each record's writer so ending a consultation cannot close another one's log.
            using (var writer = new StreamWriter(new FileStream(path, mode, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                writer.WriteLine(line);
            initialized.Add(path);
        }
    }
}

// Backend-supplied diagnostic metadata and redaction values; no provider settings or credentials enter AI input.
internal sealed class AdvisorTraceOptions
{
    internal bool Enabled { get; }
    internal string Api { get; }
    internal string Model { get; }
    internal string FallbackModel { get; }
    internal IReadOnlyList<string> Secrets { get; }
    internal AdvisorTraceOptions(bool enabled, string api, string model, string fallbackModel = "", IEnumerable<string>? secrets = null)
    {
        Enabled = enabled; Api = api; Model = model; FallbackModel = fallbackModel;
        Secrets = Array.AsReadOnly((secrets ?? Array.Empty<string>()).ToArray());
    }
}
