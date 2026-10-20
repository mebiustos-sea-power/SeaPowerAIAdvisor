using System.Collections.Generic;

namespace SeaPowerAIAdvisor;

internal interface IAdvisorUnitNavigation
{
    IReadOnlyList<(int Id, string Name)> OwnUnits();
    bool Contains(int unitId);
    void Focus(int unitId);
    void OpenMenu(int unitId);
}
