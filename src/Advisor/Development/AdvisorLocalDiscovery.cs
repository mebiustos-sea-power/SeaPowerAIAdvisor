using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// A discovery file is written on saved changes, never by polling the provider or exposing its settings.
internal sealed class AdvisorLocalDiscovery
{
    internal static string DefaultPath => Path.Combine(Path.GetTempPath(), "SeaPowerAIAdvisor", "developer-api.json");
    internal string FilePath { get; }
    private readonly int processId;
    internal AdvisorLocalDiscovery(string path, int processId) { FilePath = path; this.processId = processId; }
    internal AdvisorLocalDiscovery() : this(DefaultPath, Process.GetCurrentProcess().Id) { }
    internal void Write(JObject connections)
    {
        var value = (JObject)connections.DeepClone();
        value["endpoint"] = AdvisorLocalServer.Endpoint;
        value["process_id"] = processId;
        ConfigFile.Write(FilePath, Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + "\n"));
    }
    internal void Remove() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}
