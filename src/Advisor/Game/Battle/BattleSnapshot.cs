using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// The reader builds JSON explicitly from allowed primitive values; no game object is serialized.
internal sealed class BattleSnapshot
{
    private readonly JObject data;
    internal AdvisorUnitReferences UnitReferences { get; }
    internal BattleSnapshot(JObject data, AdvisorUnitReferences? unitReferences = null)
    { this.data = (JObject)data.DeepClone(); UnitReferences = unitReferences ?? AdvisorUnitReferences.Empty; }
    internal JObject Copy() => (JObject)data.DeepClone();
}
