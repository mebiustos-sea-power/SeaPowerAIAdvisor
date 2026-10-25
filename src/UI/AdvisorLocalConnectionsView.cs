using System;
using System.Collections.Generic;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// The developer page owns a draft; only its existing Save action publishes these choices.
internal sealed class AdvisorLocalConnectionsView
{
    private readonly StackPanel host;
    private readonly ComboBox defaultConnection;
    private readonly CheckBox modelOverride;
    private readonly Func<AdvisorText> text;
    private readonly Action<Action> guard;
    private readonly List<(string Id, string Name, CheckBox Box)> choices = new();
    private string[] defaultIds = Array.Empty<string>();
    internal AdvisorLocalConnectionsView(StackPanel host, ComboBox defaultConnection, CheckBox modelOverride, Func<AdvisorText> text, Action<Action> guard)
    { this.host = host; this.defaultConnection = defaultConnection; this.modelOverride = modelOverride; this.text = text; this.guard = guard; }
    private string? SelectedDefaultId => defaultConnection.SelectedIndex <= 0 ? null : defaultIds[defaultConnection.SelectedIndex - 1];
    internal AdvisorLocalConnections Value => new(choices.Where(c => c.Box.IsChecked == true).Select(c => c.Id),
        SelectedDefaultId, modelOverride.IsChecked == true);
    internal void Open(AdvisorLocalConnections saved, AdvisorSettingsDraft settings)
    {
        modelOverride.IsChecked = saved.AllowModelOverride;
        host.Children.Clear(); choices.Clear();
        foreach (var entry in settings.ConfigurationsByName)
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = entry.Name + " (" + entry.Provider + " / " + entry.Model + ")", TextWrapping = TextWrapping.Wrap },
                IsChecked = saved.AllowedIds.Contains(entry.Id), Margin = new Thickness(0, 4, 0, 4)
            };
            choices.Add((entry.Id, entry.Name, box)); host.Children.Add(box);
            box.Click += (_, _) => guard(() => RefreshDefault(SelectedDefaultId));
        }
        RefreshDefault(saved.DefaultId);
    }
    private void RefreshDefault(string? selected)
    {
        var allowed = choices.Where(c => c.Box.IsChecked == true).ToArray();
        defaultIds = allowed.Select(c => c.Id).ToArray();
        defaultConnection.Items.Clear();
        defaultConnection.Items.Add(new ComboBoxItem { Content = text()["connection.choose_configuration"] });
        foreach (var item in allowed) defaultConnection.Items.Add(new ComboBoxItem { Content = item.Name });
        defaultConnection.SelectedIndex = Array.IndexOf(defaultIds, selected) + 1;
    }
}
