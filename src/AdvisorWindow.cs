using System;
using System.IO;
using System.Text;
using Noesis;
using SeapowerUI;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorWindow
{
    private readonly CampaignView view;
    private readonly WindowHost host;
    private readonly Action<Action> guard;
    private readonly Func<SituationSnapshot> capture;
    private readonly Action<string> warn;
    private readonly PrototypeConversation conversation = new();
    private readonly TypingScope typing = new();
    private readonly Window window;
    private readonly TextBox question;
    private readonly TextBox transcript;
    private readonly TextBlock status;
    private bool scrollPending;

    internal AdvisorWindow(CampaignView view, WindowHost host, Action<Action> guard, Func<SituationSnapshot> capture, Action<string> warn)
    {
        this.view = view;
        this.host = host;
        this.guard = guard;
        this.capture = capture;
        this.warn = warn;
        using var source = typeof(AdvisorWindow).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Advisor.xaml")
            ?? throw new FileNotFoundException("The embedded advisor view is missing.");
        using var reader = new StreamReader(source, Encoding.UTF8);
        var content = GUI.ParseXaml(reader.ReadToEnd()) as FrameworkElement
            ?? throw new InvalidOperationException("The advisor view is not a FrameworkElement.");
        question = Named<TextBox>(content, "Question");
        transcript = Named<TextBox>(content, "Transcript");
        status = Named<TextBlock>(content, "Status");
        Named<Button>(content, "Send").Click += (_, _) => guard(Send);
        Named<Button>(content, "Clear").Click += (_, _) => guard(Clear);
        Named<Button>(content, "Capture").Click += (_, _) => guard(Preview);
        Named<Button>(content, "Conversation").Click += (_, _) => guard(ShowConversation);
        question.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(question));
        question.LostKeyboardFocus += (_, _) => guard(() => typing.Release(question));
        transcript.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(transcript));
        transcript.LostKeyboardFocus += (_, _) => guard(() => typing.Release(transcript));
        var width = Math.Min(680, view.ActualWidth - 40);
        var height = Math.Min(720, view.ActualHeight - 120);
        if (width < 360 || height < 360) throw new InvalidOperationException("The campaign view is too small for the advisor prototype.");
        window = new Window
        {
            Header = "AIアドバイザー（試作）", Content = content,
            Width = width, Height = height, MinWidth = Math.Min(480, width), MinHeight = Math.Min(540, height),
            MaxWidth = view.ActualWidth - 20, MaxHeight = view.ActualHeight - 40,
            X = (float)Math.Max(10, view.ActualWidth - width - 30), Y = 90,
            Draggable = true, CanResize = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        window.DataContext = window;
        window.Template = view.TryFindResource("WindowLayoutNoScroll") as ControlTemplate
            ?? throw new InvalidOperationException("The game's WindowLayoutNoScroll template is unavailable.");
        window.OnClosed += (_, _) => guard(ReleaseInput);
        ShowConversation();
    }

    internal bool IsOpen => host.Windows.Contains(window);

    internal void Toggle()
    {
        if (IsOpen) { Close(); return; }
        WindowHostCommands.OpenWindowCommand.Execute(window, view);
        if (!IsOpen) throw new InvalidOperationException("The campaign window host did not open the advisor.");
        // Keep the draft and history when closing and reopening in the same campaign.
        question.Focus();
    }

    internal void Tick()
    {
        if (!IsOpen) { typing.Release(); return; }
        if (scrollPending)
        {
            transcript.ScrollToEnd();
            scrollPending = false;
        }
    }

    internal void Close()
    {
        ReleaseInput();
        if (IsOpen) window.CloseWindowNow();
    }

    private void ReleaseInput()
    {
        typing.Release();
        if (window.IsKeyboardFocusWithin) window.Keyboard.ClearFocus();
    }

    private void Send()
    {
        if (!string.IsNullOrEmpty(UnityEngine.Input.compositionString))
        {
            status.Text = "日本語の変換を確定してから送信してください。";
            question.Focus();
            return;
        }
        if (!conversation.TrySend(question.Text, out var error))
        {
            status.Text = error;
            question.Focus();
            return;
        }
        ShowConversation();
        question.Text = string.Empty;
        status.Text = "仮回答を表示しました。状況への助言はまだ行いません。";
        scrollPending = true;
        question.Focus();
    }

    private void Preview()
    {
        // Clear the previous preview before capture, including a refused or failed capture.
        scrollPending = false;
        transcript.Text = string.Empty;
        try
        {
            var snapshot = capture();
            transcript.Text = SituationFormatter.Format(snapshot);
            transcript.ScrollToHome();
            status.Text = "取得時点の状況です。更新するには再度取得してください。コピーできます。";
        }
        catch (SituationUnavailableException error)
        {
            transcript.Text = error.Message;
            status.Text = "状況は取得していません。";
            warn("Situation preview refused: " + error.Message);
        }
    }

    private void ShowConversation()
    {
        scrollPending = false;
        var text = new StringBuilder();
        foreach (var exchange in conversation.Exchanges)
        {
            if (text.Length > 0) text.Append("\n\n────────────\n\n");
            text.Append("あなた\n").Append(exchange.Question).Append("\n\nアドバイザー（仮回答）\n").Append(exchange.Answer);
        }
        transcript.Text = text.Length == 0
            ? "ここに質問と仮の回答が表示されます。\n\n「状況を取得・確認」で、アドバイザーが参照する情報を確認できます。"
            : text.ToString();
        status.Text = "外部送信なし。質問への回答は仮回答です。会話は保存されません。";
    }

    private void Clear()
    {
        conversation.Clear();
        scrollPending = false;
        transcript.Text = "会話を消去しました。";
        status.Text = "新しい質問を入力してください。";
        question.Focus();
    }

    private static T Named<T>(FrameworkElement view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException("The advisor control is missing: " + name);
}
