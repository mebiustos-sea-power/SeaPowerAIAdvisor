using System;
using System.Linq;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

// Resolve against the current player map, using its displayed location and native contact menu.
internal sealed class BattleTrackNavigation : IAdvisorTrackNavigation
{
    private readonly Func<SceneMapViewModel?> map;
    private readonly Func<MainGameView?> view;

    internal BattleTrackNavigation(Func<SceneMapViewModel?> map, Func<MainGameView?> view)
    { this.map = map; this.view = view; }

    public bool Contains(int trackNumber) => Find(map(), trackNumber) != null;

    public void Focus(int trackNumber)
    {
        var current = map();
        var unit = Find(current, trackNumber);
        if (unit != null) current!.DisplayMap.Center = unit.Location;
    }

    public void OpenMenu(int trackNumber)
    {
        var unit = Find(map(), trackNumber);
        var current = view();
        if (unit == null || current == null) return;
        var menu = current.ContextMenuCanvas.ContextMenu
            ?? throw new InvalidOperationException("The game's contact context menu is unavailable.");
        menu.IsOpen = false;
        menu.ItemsSource = unit.GetContextMenuItems();
        menu.IsOpen = true;
    }

    private static MapUnitViewModel? Find(SceneMapViewModel? current, int number) =>
        current?.MapKnownUnits.AllUnits.FirstOrDefault(unit => unit.Unit.Id == number);
}
