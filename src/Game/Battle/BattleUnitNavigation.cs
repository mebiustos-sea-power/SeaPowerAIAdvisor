using System;
using System.Collections.Generic;
using System.Linq;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

// Resolve against the player's current map; only confirmed own objects supply names or own-unit menus.
internal sealed class BattleUnitNavigation : IAdvisorUnitNavigation
{
    private readonly Func<SceneMapViewModel?> map;
    private readonly Func<MainGameView?> view;

    internal BattleUnitNavigation(Func<SceneMapViewModel?> map, Func<MainGameView?> view)
    { this.map = map; this.view = view; }

    public IReadOnlyList<(int Id, string Name)> OwnUnits()
    {
        var current = map();
        var player = Globals._playerTaskforce;
        if (current == null || player == null) return Array.Empty<(int, string)>();
        var result = new List<(int Id, string Name)>();
        foreach (var unit in current.MapKnownUnits.AllUnits)
        {
            var own = OwnObject(unit);
            if (own != null && !own.isWeapon()) result.Add((unit.Unit.Id, own.Name.Value));
        }
        return result;
    }

    public (int Id, string Name)? SelectedUnit()
    {
        var current = map();
        var selected = current?.SelectedObject.Value;
        if (selected == null) return null;
        var unit = current!.MapKnownUnits.AllUnits.FirstOrDefault(item =>
            ReferenceEquals(item.Unit, selected) || ReferenceEquals(item.Unit.Object, selected));
        if (unit == null) return null;
        return Describe(unit);
    }

    public (int Id, string Name)? GetUnit(int unitId)
    {
        var unit = Find(map(), unitId);
        return unit == null ? null : Describe(unit);
    }

    private static (int Id, string Name) Describe(MapUnitViewModel unit)
    {
        var own = OwnObject(unit);
        return (unit.Unit.Id, own != null ? own.Name.Value : BattleSituationReader.ContactDisplayName(unit));
    }

    public bool Contains(int unitId) => Find(map(), unitId) != null;

    public void Focus(int unitId)
    {
        var current = map();
        var unit = Find(current, unitId);
        if (unit == null) return;
        unit.Selected = true;
        current!.DisplayMap.Center = unit.Location;
    }

    public void OpenMenu(int unitId)
    {
        var unit = Find(map(), unitId);
        var current = view();
        if (unit == null || current == null) return;
        var own = OwnObject(unit);
        var items = own == null ? unit.GetContextMenuItems() :
            (own.ViewModel ?? throw new InvalidOperationException("The game's own-unit view model is unavailable.")).GetContextMenuItems();
        if (items == null) return;
        var menu = current.ContextMenuCanvas.ContextMenu
            ?? throw new InvalidOperationException("The game's unit context menu is unavailable.");
        menu.IsOpen = false;
        menu.ItemsSource = items;
        menu.IsOpen = true;
    }

    private static ObjectBase? OwnObject(MapUnitViewModel unit)
    {
        var own = unit.Unit.Object;
        return own != null && Globals._playerTaskforce?.TaskforceObjects.Any(item => ReferenceEquals(item, own)) == true ? own : null;
    }

    private static MapUnitViewModel? Find(SceneMapViewModel? current, int id) =>
        current?.MapKnownUnits.AllUnits.FirstOrDefault(unit => unit.Unit.Id == id);
}
