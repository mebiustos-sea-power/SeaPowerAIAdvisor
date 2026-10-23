using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// A read-only view of registered definitions. It never opens an operation session or queries live candidates.
internal sealed class AdvisorCommandSettingsView
{
    private readonly Func<AdvisorOperations?> operations;
    private readonly Func<AdvisorText> text;
    private readonly StackPanel entries;
    private readonly FrameworkElement list, detail;
    private readonly TextBlock title, id, help;
    private readonly TextBox prompt, definition;
    private readonly TabControl tabs;
    private readonly Button tools;
    private readonly Action<Action> guard;

    internal AdvisorCommandSettingsView(FrameworkElement content, Func<AdvisorOperations?> operations, Func<AdvisorText> text,
        TypingScope typing, Action<Action> guard)
    {
        this.operations = operations; this.text = text; this.guard = guard;
        T Named<T>(string name) where T : class => content.FindName(name) as T ?? throw new InvalidOperationException("Missing command control: " + name);
        entries = Named<StackPanel>("CommandEntries"); list = Named<FrameworkElement>("CommandList"); detail = Named<FrameworkElement>("CommandDetails");
        title = Named<TextBlock>("CommandTitle"); id = Named<TextBlock>("CommandId"); help = Named<TextBlock>("CommandHelp");
        prompt = Named<TextBox>("CommandPrompt"); definition = Named<TextBox>("CommandDefinition"); tabs = Named<TabControl>("CommandDetailTabs");
        tools = Named<Button>("CommandTools"); tools.Click += (_, _) => guard(ShowTools);
        Named<Button>("BackToCommands").Click += (_, _) => guard(() => { CloseDetails(); tools.Focus(); });
        foreach (var box in new[] { prompt, definition })
        {
            box.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(box));
            box.LostKeyboardFocus += (_, _) => guard(() => typing.Release(box));
        }
    }
    internal void Open()
    {
        Close();
        var available = operations()?.Inspect(text().RequestedLanguage);
        if (available == null || available.Count == 0)
            entries.Children.Add(new TextBlock { Text = text()["commands.empty"], TextWrapping = TextWrapping.Wrap });
        if (available == null) return;
        foreach (var entry in available.OrderBy(entry => entry.Text["name"], StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new TextBlock { Text = entry.Text["name"], FontSize = 14, FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(heading);
            var button = new Button { Content = text()["commands.details"], Height = 30, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(button, 1); row.Children.Add(button);
            button.Click += (_, _) => guard(() => ShowCommand(entry.Id));
            var card = new StackPanel();
            card.Children.Add(row);
            card.Children.Add(new TextBlock { Text = entry.Id, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.75f, Margin = new Thickness(0, 3, 0, 5) });
            card.Children.Add(new TextBlock { Text = entry.Text["summary"], FontSize = 12, TextWrapping = TextWrapping.Wrap });
            entries.Children.Add(new Border { Child = card, BorderBrush = new SolidColorBrush(Color.FromArgb(255, 85, 96, 107)),
                BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 12) });
        }
    }
    private void ShowCommand(string identifier)
    {
        var current = operations() ?? throw new InvalidOperationException("The command scope is unavailable.");
        var entry = current.Inspect(text().RequestedLanguage).Single(item => item.Id == identifier);
        var summary = text()["commands.overview"] + "\n" + entry.Text["summary"] + "\n\n" + text()["commands.requirements"] + "\n" + entry.Text["requirements"] +
            "\n\n" + text()[(bool)entry.Definition["requires_approval"]! ? "commands.approval_required" : "commands.approval_not_required"];
        Show(entry.Text["name"], entry.Id, summary, (string?)entry.Definition["description"] ?? "", entry.Definition);
    }
    private void ShowTools()
    {
        var prompts = AdvisorPrompts.Current;
        Show(text()["commands.tools"], "", text()["commands.tools_help"], (string)prompts.Instructions(true)["task"]!, AdvisorOperationTools.Definitions(prompts));
    }
    private void Show(string name, string identifier, string description, string instruction, JToken schema)
    {
        title.Text = name; id.Text = identifier; id.Visibility = identifier.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        help.Text = description; prompt.Text = instruction; definition.Text = schema.ToString(Formatting.Indented);
        tabs.SelectedIndex = 0; list.Visibility = Visibility.Collapsed; detail.Visibility = Visibility.Visible;
    }
    private void CloseDetails() { detail.Visibility = Visibility.Collapsed; list.Visibility = Visibility.Visible; }
    internal void Focus() => tools.Focus();
    internal void Close() { CloseDetails(); entries.Children.Clear(); title.Text = id.Text = help.Text = prompt.Text = definition.Text = ""; }
}
