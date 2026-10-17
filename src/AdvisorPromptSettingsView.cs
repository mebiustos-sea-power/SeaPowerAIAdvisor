using System;
using System.Collections.Generic;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// Text boxes own a draft. Applying and resetting change memory only, independently of saved options.
internal sealed class AdvisorPromptSettingsView
{
    private readonly StackPanel host;
    private readonly TextBlock status;
    private readonly Button apply, reset;
    private readonly FrameworkElement content;
    private readonly Func<AdvisorText> text;
    private readonly Action changed;
    private readonly Action<string> warn;
    private readonly TypingScope typing;
    private readonly Action<Action> guard;
    private readonly List<(AdvisorPromptField Field, TextBox Input)> fields = new();
    private AdvisorPromptOverrides? runtime;
    private bool updating;
    internal bool HasChanges => fields.Any(f => (f.Input.Text ?? "") != f.Field.Value);
    internal AdvisorPromptSettingsView(FrameworkElement content, Func<AdvisorText> text, Action<Action> guard, TypingScope typing, Action changed, Action<string> warn)
    {
        this.content = content; this.text = text; this.guard = guard; this.typing = typing; this.changed = changed; this.warn = warn;
        T Named<T>(string name) where T : class => content.FindName(name) as T ?? throw new InvalidOperationException("Missing prompt control: " + name);
        host = Named<StackPanel>("PromptFields"); status = Named<TextBlock>("PromptStatus");
        apply = Named<Button>("ApplyPrompts"); reset = Named<Button>("ResetPrompts");
        apply.Click += (_, _) => guard(Apply); reset.Click += (_, _) => guard(Reset);
    }
    internal void Open()
    {
        Close();
        try { runtime = AdvisorPromptOverrides.Session; Populate(); }
        catch (AdvisorPromptException error)
        {
            Message(AdvisorNotice.Prompts(error).Message(text()), true);
            warn("Prompt editor unavailable: " + error.Code + ": " + error.FileName);
        }
    }
    internal void Close()
    {
        fields.Clear(); host.Children.Clear(); runtime = null; apply.IsEnabled = reset.IsEnabled = false; status.Text = "";
    }
    private void Populate()
    {
        if (runtime == null) return;
        updating = true;
        try
        {
            // Keep the controls when applying: only their comparison baseline changes.
            if (fields.Count == 0)
            {
                string? file = null;
                foreach (var field in runtime.Current.Fields)
                {
                    if (file != field.FileName)
                    {
                        host.Children.Add(new TextBlock { Text = field.FileName, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, file == null ? 0 : 16, 0, 10) });
                        file = field.FileName;
                    }
                    host.Children.Add(new TextBlock { Text = field.Key, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                    var input = new TextBox
                    {
                        Text = field.Value, Height = 120, FontSize = 14, Padding = new Thickness(7), Margin = new Thickness(0, 4, 0, 14),
                        AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                    };
                    fields.Add((field, input)); host.Children.Add(input);
                    input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input));
                    input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
                    input.TextChanged += (_, _) => guard(() => { if (!updating) ShowState(); });
                }
            }
            else
            {
                var current = runtime.Current.Fields.ToDictionary(f => f.Id);
                for (var i = 0; i < fields.Count; i++)
                {
                    var field = current[fields[i].Field.Id]; var input = fields[i].Input;
                    input.Text = field.Value; fields[i] = (field, input);
                }
            }
            apply.IsEnabled = reset.IsEnabled = true;
        }
        finally { updating = false; }
        ShowState();
    }
    private void ShowState() => Message(text()[HasChanges ? "developer.prompts.pending" : runtime?.HasOverrides == true ? "developer.prompts.active" : "developer.prompts.original"]);
    private void Apply()
    {
        if (runtime == null) return;
        if (QuestionInputController.IsComposing) { Message(text()["profile.ime"], true); return; }
        try
        {
            runtime.Apply(fields.ToDictionary(f => f.Field.Id, f => f.Input.Text ?? ""));
            Populate(); changed(); Message(text()["developer.prompts.applied"]);
        }
        catch (AdvisorPromptException error)
        {
            Message(text().Format("developer.prompts.invalid", error.FileName), true);
            warn("Temporary prompt edits rejected: " + error.Code + ": " + error.FileName);
        }
    }
    private void Reset()
    {
        if (runtime == null) return;
        if (QuestionInputController.IsComposing) { Message(text()["profile.ime"], true); return; }
        runtime.Reset(); Populate(); changed(); Message(text()["developer.prompts.restored"]);
    }
    private void Message(string message, bool error = false)
    {
        status.Text = message;
        status.Foreground = error ? (Brush)content.FindResource("AdvisorErrorBrush") : new SolidColorBrush(Color.FromArgb(255, 232, 211, 154));
    }
}
