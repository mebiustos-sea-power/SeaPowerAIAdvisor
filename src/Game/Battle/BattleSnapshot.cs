using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// The reader builds JSON explicitly from allowed primitive values; no game object is serialized.
internal sealed class BattleSnapshot
{
    private readonly JObject data;
    internal BattleSnapshot(JObject data) { this.data = (JObject)data.DeepClone(); }
    internal JObject Copy() => (JObject)data.DeepClone();
}
