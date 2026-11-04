using System;
using System.Linq;
using System.Reflection;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor;

// Native dialogs handle input and dismissal; this view remembers only the last reported load result.
internal sealed class AdvisorSaveDataNotice
{
    // The native model is internal; reflection is confined to this presentation boundary.
    private static readonly Lazy<Type> MessageBox = new(() => typeof(MainGameViewModel).Assembly.GetType("SeapowerUI.ViewModels.MessageBoxViewModel", true)!);
    private AdvisorSaveDataIssue? shown;
    internal void Tick(AdvisorSaveData data, MainGameViewModel current, Action<Action> guard)
    {
        var issue = data.UnsupportedData;
        if (issue == null || ReferenceEquals(issue, shown) || current.RootControl == null) return;
        var view = GameUi.FindVisual<MainGameView>(current.RootControl);
        if (view == null || !ReferenceEquals(view, MainGameView.Instance) || view.ActualWidth <= 0 || view.ActualHeight <= 0) return;
        var host = GameUi.FindWindowHost(current.RootControl);
        if (host == null || host.Windows.Any(window => MessageBox.Value.IsInstanceOfType(window) || window is Modal)) return;
        var text = AdvisorText.Load(GameLanguage.Read());
        var notice = Activator.CreateInstance(MessageBox.Value, true)
            ?? throw new InvalidOperationException("The game's message-box model could not be created.");
        Set(notice, "Header", text["save_data.notice_title"]);
        Set(notice, "Message", text.Format("save_data.unsupported_old", text["save_data.part." + issue.Part]));
        Set(notice, "MessageBoxType", MessageBoxType.Warning);
        Set(notice, "AcknowledgeCommandLabel", text["save_data.continue"]);
        Set(notice, "AcknowledgeCommand", new DelegateCommand(_ => guard(() => WindowHostCommands.CloseWindowCommand.Execute(notice, view))));
        WindowHostCommands.OpenWindowCommand.Execute(notice, view);
        if (!host.Windows.Contains(notice)) throw new InvalidOperationException("The game window host did not open the save-data notice.");
        shown = issue;
    }
    private static void Set(object target, string name, object value) =>
        (MessageBox.Value.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMemberException(MessageBox.Value.FullName, name)).SetValue(target, value);
}
