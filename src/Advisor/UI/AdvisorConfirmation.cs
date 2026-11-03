using System;
using Noesis;

namespace SeaPowerAIAdvisor;

// The dialog owns modality and the pending action; callers own the changes being confirmed.
internal sealed class AdvisorConfirmation
{
    private readonly FrameworkElement dialog;
    private readonly TextBlock message;
    private readonly Button accept, cancel;
    private readonly Action releaseInput;
    private FrameworkElement? disabledPane;
    private Action? confirmed;
    internal bool IsOpen => disabledPane != null;

    internal AdvisorConfirmation(FrameworkElement content, Action releaseInput, Action<Action> guard)
    {
        T Named<T>(string name) where T : class => content.FindName(name) as T
            ?? throw new InvalidOperationException("The confirmation element is missing: " + name);
        this.releaseInput = releaseInput;
        dialog = Named<FrameworkElement>("Confirmation"); message = Named<TextBlock>("ConfirmationText");
        accept = Named<Button>("AcceptConfirmation"); cancel = Named<Button>("CancelConfirmation");
        cancel.Click += (_, _) => guard(Close);
        accept.Click += (_, _) => guard(() => { var action = confirmed; Close(); action?.Invoke(); });
    }

    internal void Show(FrameworkElement pane, string text, string acceptCaption, string cancelCaption, Action action)
    {
        if (IsOpen) throw new InvalidOperationException("A confirmation is already open.");
        releaseInput();
        confirmed = action; disabledPane = pane;
        message.Text = text; accept.Content = acceptCaption; cancel.Content = cancelCaption;
        pane.IsEnabled = false; dialog.Visibility = Visibility.Visible; cancel.Focus();
    }

    internal void Close()
    {
        confirmed = null; dialog.Visibility = Visibility.Collapsed;
        if (disabledPane != null) { disabledPane.IsEnabled = true; disabledPane = null; }
    }
}
