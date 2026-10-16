using System;
using System.IO;
using System.Linq;
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
    private readonly Func<string> replyLanguage;
    private readonly Action<string> warn;
    private readonly AdvisorConversation conversation = new();
    private readonly TypingScope typing = new();
    private readonly QuestionInputController questionInput;
    private readonly Window window;
    private readonly FrameworkElement content;
    private AdvisorText text;
    private string uiLanguage;
    private readonly TextBox question;
    private readonly TextBox transcript;
    private readonly TextBox diagnosticOutput, profileName, profilePortrait;
    private readonly TextBlock advisorName, activity, portraitError, profileStatus, paneTitle;
    private readonly Image portraitImage;
    private readonly FrameworkElement settingsPane;
    private readonly Button settingsAction;
    private readonly string profileDirectory;
    private AdvisorPortrait? portrait;
    private string speakerName;
    private bool profileLoaded;
    private readonly TextBlock status;
    private readonly Button send, captureAction, inputAction;
    private readonly (AdvisorShortcut Shortcut, Button Button)[] shortcuts;
    private AdvisorJob? active;
    private AdvisorQuestion? pendingQuestion;
    private string? pendingModel;
    private bool scrollPending;
    private bool diagnosticScrollPending;

    internal AdvisorWindow(CampaignView view, WindowHost host, Action<Action> guard, Func<SituationSnapshot> capture, Func<string> replyLanguage, Action<string> warn)
    {
        this.view = view;
        this.host = host;
        this.guard = guard;
        this.capture = capture;
        this.replyLanguage = replyLanguage;
        this.warn = warn;
        uiLanguage = replyLanguage();
        text = AdvisorText.Load(uiLanguage);
        speakerName = text["speaker.advisor"];
        using var source = typeof(AdvisorWindow).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Advisor.xaml")
            ?? throw new FileNotFoundException("The embedded advisor view is missing.");
        using var reader = new StreamReader(source, Encoding.UTF8);
        content = GUI.ParseXaml(reader.ReadToEnd()) as FrameworkElement
            ?? throw new InvalidOperationException("The advisor view is not a FrameworkElement.");
        ApplyTextResources();
        question = Named<TextBox>(content, "Question");
        transcript = Named<TextBox>(content, "Transcript");
        status = Named<TextBlock>(content, "Status");
        diagnosticOutput = Named<TextBox>(content, "DiagnosticOutput");
        profileName = Named<TextBox>(content, "ProfileName");
        profilePortrait = Named<TextBox>(content, "ProfilePortrait");
        advisorName = Named<TextBlock>(content, "AdvisorName");
        activity = Named<TextBlock>(content, "Activity");
        portraitError = Named<TextBlock>(content, "PortraitError");
        profileStatus = Named<TextBlock>(content, "ProfileStatus");
        paneTitle = Named<TextBlock>(content, "PaneTitle");
        portraitImage = Named<Image>(content, "Portrait");
        settingsPane = Named<FrameworkElement>(content, "SettingsPane");
        settingsAction = Named<Button>(content, "Settings");
        settingsAction.Click += (_, _) => guard(ToggleSettings);
        Named<Button>(content, "SaveProfile").Click += (_, _) => guard(SaveProfile);
        Named<Button>(content, "ReloadProfile").Click += (_, _) => guard(ReloadProfile);
        profileDirectory = AdvisorProfile.DirectoryIn(UnityEngine.Application.persistentDataPath);
        Named<TextBox>(content, "ProfilePath").Text = AdvisorProfile.SettingsPath(profileDirectory);
        send = Named<Button>(content, "Send");
        send.Click += (_, _) => guard(Send);
        Named<Button>(content, "Clear").Click += (_, _) => guard(Clear);
        captureAction = Named<Button>(content, "Capture");
        captureAction.Click += (_, _) => guard(() => Preview(false));
        inputAction = Named<Button>(content, "Input");
        inputAction.Click += (_, _) => guard(() => Preview(true));

        shortcuts = new[]
        {
            (AdvisorShortcut.NextSteps, Named<Button>(content, "NextSteps")),
            (AdvisorShortcut.MoreDetail, Named<Button>(content, "MoreDetail")),
            (AdvisorShortcut.KeyRisks, Named<Button>(content, "KeyRisks"))
        };
        foreach (var item in shortcuts)
            item.Button.Click += (_, _) => guard(() => ConsultShortcut(item.Shortcut, item.Button));

        questionInput = new QuestionInputController(question, () => active != null, StartConsultation, guard);
        question.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(question));
        question.LostKeyboardFocus += (_, _) => guard(() => typing.Release(question));
        transcript.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(transcript));
        transcript.LostKeyboardFocus += (_, _) => guard(() => typing.Release(transcript));
        foreach (var input in new[] { profileName, profilePortrait, diagnosticOutput, Named<TextBox>(content, "ProfilePath") })
        {
            input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input));
            input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
        }
        var width = Math.Min(680, view.ActualWidth - 40);
        var height = Math.Min(720, view.ActualHeight - 120);
        if (width < 360 || height < 360) throw new InvalidOperationException("The campaign view is too small for the advisor prototype.");
        window = new Window
        {
            Header = text["window.title"], Content = content,
            Width = width, Height = height, MinWidth = Math.Min(560, width), MinHeight = Math.Min(540, height),
            MaxWidth = view.ActualWidth - 20, MaxHeight = view.ActualHeight - 40,
            X = (float)Math.Max(10, view.ActualWidth - width - 30), Y = 90,
            Draggable = true, CanResize = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        window.DataContext = window;
        window.Template = view.TryFindResource("WindowLayoutNoScroll") as ControlTemplate
            ?? throw new InvalidOperationException("The game's WindowLayoutNoScroll template is unavailable.");
        window.OnClosed += (_, _) => guard(() => { CancelActive(); ReleaseInput(); ReleasePortrait(); });
        try { AdvisorProfile.EnsureCreated(profileDirectory, DefaultPortrait); }
        catch (AdvisorProfileException failure) { ProfileError(failure); }
        ShowConversation();
    }

    internal bool IsOpen => host.Windows.Contains(window);

    internal void Toggle()
    {
        if (IsOpen) { Close(); return; }
        RefreshLanguage();
        ReloadProfile();
        WindowHostCommands.OpenWindowCommand.Execute(window, view);
        if (!IsOpen) throw new InvalidOperationException("The campaign window host did not open the advisor.");
        // Keep the draft and history when closing and reopening in the same campaign.
        question.Focus();
    }

    internal void Tick()
    {
        if (!IsOpen) { CancelActive(); questionInput.Reset(); typing.Release(); ReleasePortrait(); return; }
        questionInput.Tick();
        CompleteActive();
        if (diagnosticScrollPending)
        {
            diagnosticOutput.ScrollToHome();
            diagnosticScrollPending = false;
        }
        if (scrollPending)
        {
            transcript.ScrollToEnd();
            scrollPending = false;
        }
    }

    internal void Close()
    {
        CancelActive();
        ReleaseInput();
        if (IsOpen) window.CloseWindowNow();
        ReleasePortrait();
    }

    private void ReleaseInput()
    {
        questionInput.Reset();
        typing.Release();
        if (window.IsKeyboardFocusWithin) window.Keyboard.ClearFocus();
    }

    private void Send()
    {
        questionInput.Reset();
        if (active != null) { CancelActive(); return; }
        StartConsultation();
    }

    private void ConsultShortcut(AdvisorShortcut shortcut, Button button)
    {
        if (!shortcut.IsAvailable(active != null, conversation.Exchanges.Count)) return;
        questionInput.Reset();
        if (settingsPane.Visibility == Visibility.Visible) ToggleSettings();
        StartConsultation(shortcut.Prepare(button.Content as string
            ?? throw new InvalidOperationException("The shortcut caption is unavailable.")));
    }

    private void StartConsultation() => StartConsultation(new AdvisorQuestion(question.Text ?? string.Empty));

    private void StartConsultation(AdvisorQuestion request)
    {
        if (active != null) return;
        if (QuestionInputController.IsComposing)
        {
            status.Text = text["status.ime"];
            question.Focus();
            return;
        }
        if (!AdvisorConversation.Validate(request.Text, out var error))
        {
            status.Text = ValidationMessage(error);
            question.Focus();
            return;
        }
        try
        {
            var settings = AdvisorSettings.Load(AdvisorSettings.PathIn(UnityEngine.Application.persistentDataPath));
            var session = new AdvisorQuerySession(capture(), replyLanguage(), request.Text);
            var history = conversation.Exchanges.ToArray();
            pendingQuestion = request;
            pendingModel = settings.Model;
            var logDirectory = AdvisorTrace.DirectoryIn(UnityEngine.Application.persistentDataPath);
            active = new AdvisorJob(cancellation =>
                AdvisorConsultation.RunAsync(logDirectory, session, settings, request, history, cancellation), warn);
            SetBusy(true);
            status.Text = text["status.consulting"];
        }
        catch (AdvisorConnectionException failure) { ConnectionError(failure); }
        catch (SituationUnavailableException failure)
        {
            status.Text = text["situation.error." + failure.Code];
            warn("Consultation refused: " + failure.Message);
        }
    }

    private void CompleteActive()
    {
        var job = active;
        if (job == null || !job.Completion.IsCompleted) return;
        var sentQuestion = pendingQuestion ?? throw new InvalidOperationException("The pending question is missing.");
        var model = pendingModel;
        active = null;
        pendingQuestion = null;
        var completion = job.TakeCompleted();
        SetBusy(false);
        if (completion.UnexpectedError != null) throw completion.UnexpectedError;
        if (completion.IsCancelled) { status.Text = text["status.cancelled"]; return; }
        if (completion.Error != null) { ConnectionError(completion.Error); return; }
        var result = completion.Result ?? throw new InvalidOperationException("The completed consultation has no result.");
        if (!conversation.TryAdd(sentQuestion, result.Text, out _))
            throw new InvalidOperationException("The completed consultation is not a valid exchange.");
        question.Text = sentQuestion.DraftAfterSuccess(question.Text ?? string.Empty);
        ShowConversation();
        status.Text = text.Format("status.completed", model ?? string.Empty, result.QueryRounds, result.ApiRequests,
            result.TotalTokens.HasValue ? text.Format("status.token_count", result.TotalTokens.Value) : text["status.tokens_unknown"]);
        scrollPending = true;
    }

    private void SetBusy(bool busy)
    {
        send.Content = text[busy ? "action.cancel" : "action.send"];
        question.IsReadOnly = busy;
        captureAction.IsEnabled = !busy;
        inputAction.IsEnabled = !busy;
        activity.Text = text[busy ? "activity.busy" : "activity.idle"];
        UpdateShortcuts();
    }

    private void CancelActive()
    {
        if (active == null) return;
        var job = active; active = null; pendingQuestion = null;
        job.Dispose();
        SetBusy(false);
        status.Text = text["status.cancelled"];
    }

    private void ConnectionError(AdvisorConnectionException failure)
    {
        var key = failure.Code switch
        {
            "settings_missing" or "settings_unreadable" => "settings_missing",
            "api_key_missing" => "api_key_missing",
            "settings_invalid" or "model_invalid" => "settings_invalid",
            "api_access_denied" or "api_bad_request" => "api_access_denied",
            "model_unavailable" => "model_unavailable",
            "diagnostic_log_error" => "diagnostic_log_error",
            "rate_limited" => "rate_limited",
            "request_timeout" or "consultation_timeout" => "request_timeout",
            "network_error" => "network_error",
            "output_limit" => "output_limit",
            "response_blocked" or "generation_failed" => "response_blocked",
            "request_budget" or "request_limit" => "request_budget",
            _ => "unknown"
        };
        status.Text = text["connection." + key];
        status.Text += " [" + failure.Code + (failure.HttpStatus.HasValue ? " / HTTP " + failure.HttpStatus : "") + "]";
        warn("Gemini consultation failed: " + failure.Message);
    }

    private void Preview(bool structured)
    {
        // Never leave a stale request on screen after a refused or failed capture.
        scrollPending = false;
        diagnosticScrollPending = false;
        diagnosticOutput.Text = string.Empty;
        if (structured && !string.IsNullOrEmpty(UnityEngine.Input.compositionString))
        {
            status.Text = text["diagnostics.ime"];
            question.Focus();
            return;
        }
        if (structured && (question.Text?.Length ?? 0) > AdvisorConversation.MaxQuestionLength)
        {
            status.Text = ValidationMessage("validation.question_too_long");
            question.Focus();
            return;
        }
        try
        {
            var snapshot = capture();
            if (structured)
            {
                var language = replyLanguage();
                diagnosticOutput.Text = new AdvisorQuerySession(snapshot, language, question.Text).InitialInput().ToString(Newtonsoft.Json.Formatting.Indented);
                status.Text = text.Format("diagnostics.input_generated", language);
            }
            else
            {
                diagnosticOutput.Text = SituationFormatter.Format(snapshot, text);
                status.Text = text["diagnostics.situation_ready"];
            }
            diagnosticScrollPending = true;
        }
        catch (SituationUnavailableException error)
        {
            diagnosticOutput.Text = text["situation.error." + error.Code];
            status.Text = text["diagnostics.situation_refused"];
            warn("Situation preview refused: " + error.Message);
        }
    }

    private void ShowConversation(bool updateStatus = true)
    {
        scrollPending = false;
        diagnosticScrollPending = false;
        transcript.Text = AdvisorTranscript.Format(conversation.Exchanges, text["speaker.you"], speakerName, text["transcript.empty"]);
        UpdateShortcuts();
        if (updateStatus) status.Text = text["status.ready"];
    }

    private void Clear()
    {
        questionInput.Reset();
        CancelActive();
        conversation.Clear();
        scrollPending = false;
        diagnosticScrollPending = false;
        transcript.Text = text["transcript.cleared"];
        UpdateShortcuts();
        status.Text = text["status.new_question"];
        question.Focus();
    }


    private void ToggleSettings()
    {
        var show = settingsPane.Visibility != Visibility.Visible;
        settingsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        transcript.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        paneTitle.Text = text[show ? "pane.settings" : "pane.conversation"];
        settingsAction.Content = text[show ? "action.back" : "action.settings"];
        typing.Release();
        if (!show) { scrollPending = true; question.Focus(); }
    }

    private void ReloadProfile()
    {
        try
        {
            var profile = AdvisorProfile.Load(profileDirectory);
            ApplyProfile(profile, new AdvisorPortrait(profile.ReadPortrait(profileDirectory)));
            profileStatus.Text = text["profile.reloaded"];
        }
        catch (AdvisorProfileException failure) { ProfileError(failure); }
    }

    private void SaveProfile()
    {
        if (!string.IsNullOrEmpty(UnityEngine.Input.compositionString))
        {
            profileStatus.Text = text["profile.ime"];
            return;
        }
        try
        {
            var profile = new AdvisorProfile(profileName.Text ?? string.Empty, profilePortrait.Text ?? string.Empty);
            var next = new AdvisorPortrait(profile.ReadPortrait(profileDirectory));
            try { profile.Save(profileDirectory); }
            catch { next.Dispose(); throw; }
            ApplyProfile(profile, next);
            profileStatus.Text = text["profile.saved"];
        }
        catch (AdvisorProfileException failure) { ProfileError(failure, true); }
    }

    private void ApplyProfile(AdvisorProfile profile, AdvisorPortrait next)
    {
        ReleasePortrait();
        portrait = next;
        portraitImage.Source = next.Source;
        portraitError.Visibility = Visibility.Collapsed;
        profileLoaded = true;
        speakerName = profile.DisplayName;
        advisorName.Text = profile.DisplayName;
        profileName.Text = profile.DisplayName;
        profilePortrait.Text = profile.PortraitFile;
        ShowConversation(false);
    }

    private void ReleasePortrait()
    {
        var previous = portrait;
        if (previous == null) return;
        portraitImage.Source = null;
        portrait = null;
        previous.Dispose();
    }

    private void ProfileError(AdvisorProfileException failure, bool preservePortrait = false)
    {
        if (!preservePortrait || portrait == null)
        {
            ReleasePortrait();
            if (!profileLoaded) advisorName.Text = text["speaker.advisor"];
            portraitError.Visibility = Visibility.Visible;
            portraitError.Text = text["portrait.settings_error"];
        }
        var key = text.Contains("profile.error." + failure.Code) ? failure.Code : "profile_invalid";
        profileStatus.Text = text["profile.error." + key] + text.Format("profile.error_suffix", failure.Code);
        warn("Advisor profile failed: " + failure.Code);
    }

    private string ValidationMessage(string code) => code == "validation.question_too_long"
        ? text.Format(code, AdvisorConversation.MaxQuestionLength) : text[code];

    private void UpdateShortcuts()
    {
        foreach (var item in shortcuts)
            item.Button.IsEnabled = item.Shortcut.IsAvailable(active != null, conversation.Exchanges.Count);
    }

    private void ApplyTextResources()
    {
        foreach (var entry in text.Entries) content.Resources["Advisor." + entry.Key] = entry.Value;
    }

    private void RefreshLanguage()
    {
        var language = replyLanguage();
        if (uiLanguage == language) return;
        text = AdvisorText.Load(language);
        uiLanguage = language;
        ApplyTextResources();
        window.Header = text["window.title"];
        var settings = settingsPane.Visibility == Visibility.Visible;
        paneTitle.Text = text[settings ? "pane.settings" : "pane.conversation"];
        settingsAction.Content = text[settings ? "action.back" : "action.settings"];
        if (!profileLoaded) speakerName = text["speaker.advisor"];
        SetBusy(active != null);
        ShowConversation();
    }

    private static byte[] DefaultPortrait()
    {
        using var source = typeof(AdvisorWindow).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.DefaultPortrait.png")
            ?? throw new FileNotFoundException("The default advisor portrait is missing.");
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static T Named<T>(FrameworkElement view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException("The advisor control is missing: " + name);
}
