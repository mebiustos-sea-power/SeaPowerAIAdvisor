using System;
using System.Reflection;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPowerAIAdvisor;

internal sealed class BattleWeaponNavigation : IAdvisorWeaponNavigation
{
    private readonly Func<MainGameView?> view;
    private readonly Action<Action> guard;
    internal BattleWeaponNavigation(Func<MainGameView?> view, Action<Action> guard)
    { this.view = view; this.guard = guard; }

    public bool Contains(string referenceKey) => Find(referenceKey) != null;

    public void OpenMenu(string referenceKey)
    {
        var current = view();
        if (current == null || Find(referenceKey) == null) return;
        var menu = current.ContextMenuCanvas.ContextMenu
            ?? throw new InvalidOperationException("The game's context menu is unavailable.");
        menu.IsOpen = false;
        menu.ItemsSource = new[]
        {
            new ContextMenuItem("ContextMenu", "WeaponInformation",
                command: new DelegateCommand(_ => guard(() => OpenReference(referenceKey))))
        };
        menu.IsOpen = true;
    }

    private void OpenReference(string key)
    {
        var current = view();
        var entry = Find(key);
        if (current == null || entry == null) return;
        WindowHostCommands.OpenWindowCommand.Execute(new ObjectWeaponInformationViewModel(current, entry), current);
    }

    private static MuseumWeaponEntry? Find(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var type = typeof(Globals).Assembly.GetType("SeaPower.EncyclopedicDataManager", true)!;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        var exists = type.GetMethod("InstanceExists", flags) ?? throw new MissingMethodException(type.FullName, "InstanceExists");
        if (!(bool)exists.Invoke(null, new object[] { false })!) return null;
        var manager = type.GetProperty("Instance", flags)?.GetValue(null)
            ?? throw new InvalidOperationException("The encyclopedia instance is unavailable.");
        if (!(bool)BattleValue.Required(manager, "UnitsLoaded")) return null;
        return (MuseumWeaponEntry?)BattleReferenceCatalog.FindPublicWeapon(
            BattleValue.Rows(BattleValue.Required(manager, "WeaponMuseumEntries")), key);
    }
}
