using System;
using System.IO;
using Noesis;

namespace SeaPowerAIAdvisor;

// Display text is released when the tab closes; the game-run history stays file-backed.
internal sealed class AdvisorErrorHistoryView
{
    private readonly TextBox details;
    private readonly TextBlock status;
    private readonly Func<AdvisorText> text;
    private int loaded = -1;
    private bool visible;
    internal AdvisorErrorHistoryView(FrameworkElement content, Func<AdvisorText> text, Action<Action> guard, TypingScope typing)
    {
        this.text = text;
        T Named<T>(string name) where T : class => content.FindName(name) as T ?? throw new InvalidOperationException("Missing error history control: " + name);
        details = Named<TextBox>("ApiErrorHistoryDetails"); status = Named<TextBlock>("ApiErrorHistoryStatus");
        details.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(details));
        details.LostKeyboardFocus += (_, _) => guard(() => typing.Release(details));
    }
    internal void Close()
    {
        visible = false; loaded = -1; details.Text = "";
    }
    internal void Tick(bool active)
    {
        if (!active) { if (visible) Close(); return; }
        visible = true;
        var history = AdvisorErrorHistory.Active;
        var revision = history?.Revision ?? 0;
        if (revision == loaded) return;
        loaded = revision;
        status.Text = history?.StorageError.Length > 0 ? text().Format("errorlog.storage_failed", history.StorageError) : "";
        details.Text = "";
        try
        {
            details.Text = history?.ReadAll() ?? "";
            if (status.Text.Length == 0 && details.Text.Length == 0) status.Text = text()["errorlog.empty"];
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { status.Text = text().Format("errorlog.read_failed", failure.Message); }
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
