using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Only counters stay in memory between visits to the error tab.
internal sealed class AdvisorErrorHistory
{
    internal static AdvisorErrorHistory? Active { get; private set; }
    internal static void StartRun(Action<string> warn) => Active ??= new AdvisorErrorHistory(Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor"), warn);
    private readonly object gate = new();
    private readonly Action<string>? warn;
    private int count, revision;
    private string storageError = "";
    internal string FilePath { get; }
    internal int Revision { get { lock (gate) return revision; } }
    internal string StorageError { get { lock (gate) return storageError; } }
    internal AdvisorErrorHistory(string directory, Action<string>? warn = null)
    {
        FilePath = Path.GetFullPath(Path.Combine(directory, "provider-errors.log")); this.warn = warn;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using (File.Create(FilePath)) { }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { WriteFailed(failure.ToString()); }
    }
    internal void Append(JObject safeRecord)
    {
        lock (gate)
        {
            safeRecord["id"] = count + 1;
            safeRecord["timestamp_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var text = new StringBuilder();
            text.Append("===== Provider error #").Append(count + 1).AppendLine(" =====");
            foreach (var field in safeRecord.Properties())
            {
                if (field.Name is "body_json" or "exception") continue;
                var value = field.Value.Type == JTokenType.Null ? "(none)" : field.Value.Type == JTokenType.String
                    ? (string)field.Value! : field.Value.ToString(Formatting.None);
                text.Append(field.Name).Append(": ").AppendLine(value);
            }
            text.AppendLine().AppendLine("Exception:").AppendLine((string?)safeRecord["exception"] ?? "(none)");
            // Write the received, redacted body directly; do not quote or reserialize it.
            text.AppendLine().AppendLine("Response body:").Append((string?)safeRecord["body_json"] ?? "(no response body received)");
            text.AppendLine().AppendLine("===== End provider error =====").AppendLine();
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); }
            count++; revision++;
        }
    }
    internal void WriteFailed(string safeDetail)
    {
        lock (gate) { storageError = safeDetail; revision++; }
        warn?.Invoke("Advisor error history could not be saved: " + safeDetail);
    }
    internal string ReadAll()
    {
        lock (gate) return File.ReadAllText(FilePath, Encoding.UTF8);
    }
}
