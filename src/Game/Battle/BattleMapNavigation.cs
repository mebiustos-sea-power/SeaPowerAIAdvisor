using System;
using SeaPower;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

internal sealed class BattleMapNavigation : IAdvisorMapNavigation
{
    private readonly Func<SceneMapViewModel?> map;
    internal BattleMapNavigation(Func<SceneMapViewModel?> map) { this.map = map; }
    public void Focus(double latitude, double longitude)
    {
        var current = map();
        if (current != null) current.DisplayMap.Center = new GeoPosition(latitude, longitude, 0);
    }
}
