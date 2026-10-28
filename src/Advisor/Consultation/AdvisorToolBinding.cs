using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// A definition and its owner travel together. Read bindings normalize into the shared read batch;
// proposal bindings retain their separate approval path.
internal sealed class AdvisorToolBinding
{
    internal JObject Definition { get; }
    internal string Name => (string)Definition["name"]!;
    internal Func<JObject, JArray?>? Read { get; }
    internal Func<JObject, CancellationToken, Task<JObject>>? Execute { get; }
    internal string ProgressQuery { get; }
    internal AdvisorToolBinding(JObject definition, Func<JObject, JArray?> read)
    { Definition = (JObject)definition.DeepClone(); Read = read; ProgressQuery = ""; }
    internal AdvisorToolBinding(JObject definition, string progressQuery, Func<JObject, CancellationToken, Task<JObject>> execute)
    { Definition = (JObject)definition.DeepClone(); ProgressQuery = progressQuery; Execute = execute; }
}
