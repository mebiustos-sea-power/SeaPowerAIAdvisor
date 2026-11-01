using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

internal sealed partial class BattleOperationSupport
{
    private IEnumerable<ObjectBase> VisibleUnits() => units().Where(unit =>
    {
        // Id is -1 when the object has no corresponding contact on the player's map.
        if (unit.isWeapon() || unit.Id < 0) return false;
        var kind = BattleValue.Code(BattleValue.Required(unit, "_type"));
        var subtype = kind == "landunit" ? BattleValue.Code(BattleValue.Required(unit._obp, "_landUnitSubType")) : null;
        return BattleInformation.Permitted(BattleInformation.UnitCategory(subtype), information());
    });
    private ObjectBase Unit(string target)
    {
        Require("battle_units");
        return VisibleUnits().FirstOrDefault(u => "unit:" + u.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == target)
            ?? throw new OperationException(409, "target_unavailable");
    }
}
