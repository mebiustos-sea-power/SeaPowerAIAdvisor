using System.Collections.Generic;

namespace SeaPowerAIAdvisor;

internal interface IAdvisorUnitNavigation
{
    IReadOnlyList<(int Id, string Name)> OwnUnits();
    (int Id, string Name)? SelectedUnit();
    (int Id, string Name)? GetUnit(int unitId);
    bool Contains(int unitId);
    void Focus(int unitId);
    void OpenMenu(int unitId);
}
