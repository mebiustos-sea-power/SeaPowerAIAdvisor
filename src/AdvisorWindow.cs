using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Text;
using Noesis;
using SeapowerUI;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorWindow
{
    private readonly FrameworkElement view;
    private readonly WindowHost host;
    private readonly Action<Action> guard;
    private readonly IAdvisorSource source;
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
    private readonly ScrollViewer transcriptScroll;
    private readonly AdvisorTranscriptView transcript;
    private readonly TextBox diagnosticOutput, profileName, profilePersonality, modelName;
    private readonly PasswordBox apiKey;
    private AdvisorSettingsDraft? connectionDraft;
    private readonly RadioButton defaultAdvisor, customAdvisor;
    private readonly TextBlock advisorName, activity, portraitError, profilePreviewError, profileStatus;
    private readonly Image portraitImage, profilePreview;
    private readonly FrameworkElement conversationPane, settingsPane;
    private readonly TabControl settingsTabs;
    private readonly CheckBox logAiJson;
    private readonly string settingsPath;
    private readonly Button saveSettings;
    private readonly string profileDirectory;
    private AdvisorPortrait? portrait, previewPortrait;
    private AdvisorProfileDraft profileDraft = new(new AdvisorProfile(AdvisorProfile.DefaultName, AdvisorProfile.DefaultPortraitFile, true));
    private bool updatingProfile;
    private AdvisorProfile? appliedProfile;
    private Task<string?>? portraitSelection;
    private readonly Button choosePortrait;
    private string speakerName;
    private bool profileLoaded;
    private readonly TextBlock status, diagnosticStatus, settingsStatus;
    private readonly Button send, captureAction, inputAction;
    private readonly (AdvisorShortcut Shortcut, Button Button)[] shortcuts;
    private AdvisorJob? active;
    private AdvisorQuestion? pendingQuestion;
    private string? pendingModel;
    private bool scrollPending;
    private bool diagnosticScrollPending;

    internal AdvisorWindow(FrameworkElement view, WindowHost host, Action<Action> guard, IAdvisorSource source, Func<string> replyLanguage, Action<string> warn)
    {
        this.view = view;
        this.host = host;
        this.guard = guard;
        this.source = source;
        this.replyLanguage = replyLanguage;
        this.warn = warn;
        uiLanguage = replyLanguage();
        text = AdvisorText.Load(uiLanguage);
        speakerName = text["speaker.advisor"];
        using var viewSource = typeof(AdvisorWindow).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Advisor.xaml")
            ?? throw new FileNotFoundException("The embedded advisor view is missing.");
        using var reader = new StreamReader(viewSource, Encoding.UTF8);
        content = GUI.ParseXaml(reader.ReadToEnd()) as FrameworkElement
            ?? throw new InvalidOperationException("The advisor view is not a FrameworkElement.");
        ApplyTextResources();
        question = Named<TextBox>(content, "Question");
        transcriptScroll = Named<ScrollViewer>(content, "Transcript");
        transcript = new AdvisorTranscriptView(Named<StackPanel>(content, "TranscriptMessages"), guard);
        status = Named<TextBlock>(content, "Status");
        diagnosticStatus = Named<TextBlock>(content, "DiagnosticStatus");
        diagnosticOutput = Named<TextBox>(content, "DiagnosticOutput");
        profileName = Named<TextBox>(content, "ProfileName");
        profilePersonality = Named<TextBox>(content, "ProfilePersonality");
        profilePersonality.MaxLength = AdvisorProfile.MaxPersonalityLength;
        defaultAdvisor = Named<RadioButton>(content, "DefaultAdvisor");
        customAdvisor = Named<RadioButton>(content, "CustomAdvisor");
        choosePortrait = Named<Button>(content, "ChoosePortrait");
        choosePortrait.Click += (_, _) => guard(ChoosePortrait);
        advisorName = Named<TextBlock>(content, "AdvisorName");
        activity = Named<TextBlock>(content, "Activity");
        portraitError = Named<TextBlock>(content, "PortraitError");
        profilePreviewError = Named<TextBlock>(content, "ProfilePreviewError");
        profileStatus = Named<TextBlock>(content, "ProfileStatus");
        portraitImage = Named<Image>(content, "Portrait");
        profilePreview = Named<Image>(content, "ProfilePreview");
        conversationPane = Named<FrameworkElement>(content, "ConversationPane");
        settingsPane = Named<FrameworkElement>(content, "SettingsPane");
        settingsTabs = Named<TabControl>(content, "SettingsTabs");
        Named<Button>(content, "Settings").Click += (_, _) => guard(OpenSettings);
        saveSettings = Named<Button>(content, "SaveSettings");
        saveSettings.Click += (_, _) => guard(SaveSettings);
        Named<Button>(content, "CancelSettings").Click += (_, _) => guard(CancelSettings);
        settingsStatus = Named<TextBlock>(content, "SettingsStatus");
        apiKey = Named<PasswordBox>(content, "ApiKey");
        modelName = Named<TextBox>(content, "ModelName");
        profileDirectory = AdvisorProfile.DirectoryIn(UnityEngine.Application.persistentDataPath);
        settingsPath = AdvisorSettings.PathIn(UnityEngine.Application.persistentDataPath);
        logAiJson = Named<CheckBox>(content, "LogAiJson");
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
            (AdvisorShortcut.KeyRisks, Named<Button>(content, "KeyRisks")),
            (AdvisorShortcut.MoreDetail, Named<Button>(content, "MoreDetail"))
        };
        foreach (var item in shortcuts)
            item.Button.Click += (_, _) => guard(() => ConsultShortcut(item.Shortcut, item.Button));

        questionInput = new QuestionInputController(question, () => active != null, StartConsultation, guard);
        question.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(question));
        question.LostKeyboardFocus += (_, _) => guard(() => typing.Release(question));
        foreach (var input in new[] { profileName, profilePersonality, modelName, diagnosticOutput, Named<TextBox>(content, "ProfilePath") })
        {
            input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input));
            input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
        }
        apiKey.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(apiKey));
        apiKey.LostKeyboardFocus += (_, _) => guard(() => typing.Release(apiKey));
        var width = Math.Min(680, view.ActualWidth - 40);
        var height = Math.Min(720, view.ActualHeight - 120);
        if (width < 360 || height < 360) throw new InvalidOperationException("The game view is too small for the advisor prototype.");
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
        window.OnClosed += (_, _) => guard(() => { CancelActive(); ReleaseInput(); ReleasePortrait(); portraitSelection = null; DiscardConnectionChanges(); });
        settingsTabs.SelectionChanged += (_, _) => guard(() =>
        {
            if (!IsOpen || settingsPane.Visibility != Visibility.Visible) return;
            ReleaseInput();
            FocusSettings();
        });
        defaultAdvisor.Checked += (_, _) => guard(ChangeProfileMode);
        customAdvisor.Checked += (_, _) => guard(ChangeProfileMode);
        try { AdvisorProfile.EnsureCreated(profileDirectory, DefaultPortrait); }
        catch (AdvisorProfileException failure) { ProfileError(failure); }
        ShowConversation();
    }

    internal bool IsOpen => host.Windows.Contains(window);

    internal void Toggle()
    {
        if (IsOpen) { Close(); return; }
        RefreshLanguage();
        LoadProfile();
        if (settingsPane.Visibility == Visibility.Visible) BeginConnectionEdit();
        WindowHostCommands.OpenWindowCommand.Execute(window, view);
        if (!IsOpen) throw new InvalidOperationException("The game window host did not open the advisor.");
        // Keep conversation and message text; reopened settings reload their saved values.
        choosePortrait.IsEnabled = portraitSelection == null;
        if (settingsPane.Visibility == Visibility.Visible) FocusSettings();
        else question.Focus();
    }

    internal void Tick()
    {
        if (!IsOpen) { CancelActive(); questionInput.Reset(); typing.Release(); ReleasePortrait(); return; }
        CompletePortraitSelection();
        questionInput.Tick();
        CompleteActive();
        if (diagnosticScrollPending)
        {
            diagnosticOutput.ScrollToHome();
            diagnosticScrollPending = false;
        }
        if (scrollPending)
        {
            transcriptScroll.ScrollToEnd();
            scrollPending = false;
        }
    }

    internal void Close()
    {
        CancelActive();
        ReleaseInput();
        portraitSelection = null;
        DiscardConnectionChanges();
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
        if (conversationPane.Visibility != Visibility.Visible || !shortcut.IsAvailable(active != null, conversation.Exchanges.Count)) return;
        questionInput.Reset();
        StartConsultation(shortcut.Prepare(button.Content as string
            ?? throw new InvalidOperationException("The shortcut caption is unavailable.")));
    }

    private void StartConsultation() => StartConsultation(new AdvisorQuestion(question.Text ?? string.Empty));

    private void StartConsultation(AdvisorQuestion request)
    {
        if (active != null || conversationPane.Visibility != Visibility.Visible) return;
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
            var settings = AdvisorSettings.Load(settingsPath);
            var session = source.CreateSession(replyLanguage(), request.Text);
            var history = conversation.Exchanges.ToArray();
            pendingQuestion = request;
            pendingModel = settings.Model;
            var logDirectory = AdvisorTrace.DirectoryIn(UnityEngine.Application.persistentDataPath);
            var advisorProfile = appliedProfile;
            active = new AdvisorJob(cancellation =>
                AdvisorConsultation.RunAsync(logDirectory, session, settings, request, history, cancellation, profile: advisorProfile), warn);
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
            diagnosticStatus.Text = text["diagnostics.ime"];
            return;
        }
        if (structured && (question.Text?.Length ?? 0) > AdvisorConversation.MaxQuestionLength)
        {
            diagnosticStatus.Text = ValidationMessage("validation.question_too_long");
            return;
        }
        try
        {
            if (structured)
            {
                var language = replyLanguage();
                var input = source.CreateSession(language, question.Text).InitialInput();
                appliedProfile?.AddAnswerPreferences(input);
                diagnosticOutput.Text = input.ToString(Newtonsoft.Json.Formatting.Indented);
                diagnosticStatus.Text = text.Format("diagnostics.input_generated", language);
            }
            else
            {
                diagnosticOutput.Text = source.Preview(text);
                diagnosticStatus.Text = text["diagnostics.situation_ready"];
            }
            diagnosticScrollPending = true;
        }
        catch (SituationUnavailableException error)
        {
            diagnosticOutput.Text = text["situation.error." + error.Code];
            diagnosticStatus.Text = text["diagnostics.situation_refused"];
            warn("Situation preview refused: " + error.Message);
        }
    }

    private void ShowConversation(bool updateStatus = true)
    {
        scrollPending = false;
        diagnosticScrollPending = false;
        transcript.Show(conversation.Exchanges, text, speakerName, text["transcript.empty"]);
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
        transcript.Empty(text["transcript.cleared"]);
        UpdateShortcuts();
        status.Text = text["status.new_question"];
        question.Focus();
    }


    private void OpenSettings()
    {
        if (settingsPane.Visibility == Visibility.Visible) return;
        ReleaseInput();
        DiscardProfileChanges();
        BeginConnectionEdit();
        conversationPane.Visibility = Visibility.Collapsed;
        settingsPane.Visibility = Visibility.Visible;
        FocusSettings();
    }

    private void CancelSettings()
    {
        ReleaseInput();
        DiscardProfileChanges();
        ReturnToConversation();
    }

    private void ReturnToConversation()
    {
        DiscardConnectionChanges();
        settingsPane.Visibility = Visibility.Collapsed;
        conversationPane.Visibility = Visibility.Visible;
        scrollPending = true;
        question.Focus();
    }

    private void BeginConnectionEdit()
    {
        DiscardConnectionChanges();
        SetSettingsStatus(string.Empty);
        try
        {
            connectionDraft = AdvisorSettings.Edit(settingsPath);
            apiKey.Password = connectionDraft.ApiKey;
            modelName.Text = connectionDraft.Model;
            logAiJson.IsChecked = connectionDraft.LogAiJson;
            apiKey.IsEnabled = modelName.IsEnabled = logAiJson.IsEnabled = saveSettings.IsEnabled = true;
        }
        catch (AdvisorConnectionException failure)
        {
            apiKey.IsEnabled = modelName.IsEnabled = logAiJson.IsEnabled = saveSettings.IsEnabled = false;
            SettingsError(failure);
        }
    }

    private void DiscardConnectionChanges()
    {
        connectionDraft = null;
        apiKey.Password = string.Empty;
        modelName.Text = string.Empty;
        logAiJson.IsChecked = false;
    }

    private void SettingsError(AdvisorConnectionException failure)
    {
        var key = text.Contains("settings.error." + failure.Code) ? failure.Code : "settings_invalid";
        SetSettingsStatus(text["settings.error." + key] + text.Format("settings.error_suffix", failure.Code));
        warn("Advisor settings failed: " + failure.Code);
    }

    private void SetSettingsStatus(string message)
    {
        settingsStatus.Text = message;
        settingsStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FocusSettings()
    {
        if (settingsTabs.SelectedIndex == 1)
        {
            if (apiKey.IsEnabled) apiKey.Focus();
            else Named<Button>(content, "CancelSettings").Focus();
        }
        else if (settingsTabs.SelectedIndex == 2) captureAction.Focus();
        else if (profileDraft.UseDefault) profilePersonality.Focus();
        else profileName.Focus();
    }

    private void SetProfileStatus(string message)
    {
        profileStatus.Text = message;
        profileStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateProfileControls()
    {
        profileName.Text = profileDraft.UseDefault ? AdvisorProfile.DefaultName : profileDraft.CustomName;
        profileName.IsReadOnly = profileDraft.UseDefault;
        choosePortrait.Visibility = profileDraft.UseDefault ? Visibility.Collapsed : Visibility.Visible;
        choosePortrait.IsEnabled = portraitSelection == null;
    }

    private void ChangeProfileMode()
    {
        if (updatingProfile) return;
        if (!profileName.IsReadOnly) profileDraft.CustomName = profileName.Text ?? "";
        profileDraft.Personality = profilePersonality.Text ?? "";
        profileDraft.UseDefault = defaultAdvisor.IsChecked == true;
        profilePersonality.Text = profileDraft.Personality;
        UpdateProfileControls();
        RefreshProfilePreview();
        ReleaseInput();
        (profileDraft.UseDefault ? defaultAdvisor : customAdvisor).Focus();
    }

    private void RefreshProfilePreview()
    {
        try
        {
            SetProfilePreview(new AdvisorPortrait(profileDraft.ReadActivePortrait(profileDirectory, DefaultPortrait)));
            SetProfileStatus(string.Empty);
        }
        catch (AdvisorProfileException failure)
        {
            ReleasePreview();
            profilePreviewError.Visibility = Visibility.Visible;
            profilePreviewError.Text = text["portrait.settings_error"];
            ProfileError(failure, true);
        }
    }

    private void SetProfilePreview(AdvisorPortrait next)
    {
        ReleasePreview();
        previewPortrait = next;
        profilePreview.Source = next.Source;
        profilePreviewError.Visibility = Visibility.Collapsed;
    }

    private void ChoosePortrait()
    {
        if (profileDraft.UseDefault || portraitSelection != null) return;
        ReleaseInput();
        portraitSelection = PortraitFileDialog.OpenAsync(text["profile.choose_title"], text["profile.image_filter"]);
        choosePortrait.IsEnabled = false;
    }

    private void CompletePortraitSelection()
    {
        if (portraitSelection == null || !portraitSelection.IsCompleted) return;
        var selection = portraitSelection;
        portraitSelection = null;
        choosePortrait.IsEnabled = true;
        try
        {
            var path = selection.GetAwaiter().GetResult();
            if (path == null) return;
            var selected = new AdvisorProfile(AdvisorProfile.DefaultName, path);
            var image = selected.ReadPortrait(profileDirectory);
            var next = new AdvisorPortrait(image);
            profileDraft.SelectPortrait(image);
            if (profileDraft.UseDefault) { next.Dispose(); RefreshProfilePreview(); }
            else SetProfilePreview(next);
            SetProfileStatus(text["profile.image_selected"]);
        }
        catch (Win32Exception error)
        {
            SetProfileStatus(text["profile.error.portrait_dialog_failed"]);
            warn("Advisor portrait selection failed: " + error);
        }
        catch (AdvisorProfileException failure) { ProfileError(failure, true); }
        finally
        {
            if (settingsPane.Visibility == Visibility.Visible && settingsTabs.SelectedIndex == 0) FocusSettings();
        }
    }

    private void LoadProfile()
    {
        try
        {
            var profile = AdvisorProfile.Load(profileDirectory);
            ApplyProfile(profile, new AdvisorPortrait(profile.ReadActivePortrait(profileDirectory, DefaultPortrait)));
            SetProfileStatus(string.Empty);
        }
        catch (AdvisorProfileException failure) { ProfileError(failure); }
    }

    private void SaveSettings()
    {
        if (QuestionInputController.IsComposing)
        {
            SetSettingsStatus(text["profile.ime"]);
            return;
        }
        if (portraitSelection != null || connectionDraft == null) return;
        SetSettingsStatus(string.Empty);
        if (!profileName.IsReadOnly) profileDraft.CustomName = profileName.Text ?? "";
        profileDraft.Personality = profilePersonality.Text ?? "";
        connectionDraft.ApiKey = apiKey.Password;
        connectionDraft.Model = modelName.Text ?? "";
        connectionDraft.LogAiJson = logAiJson.IsChecked == true;
        try
        {
            _ = connectionDraft.Data();
            var next = new AdvisorPortrait(profileDraft.ReadActivePortrait(profileDirectory, DefaultPortrait));
            AdvisorProfile profile;
            try { profile = AdvisorPreferences.Save(profileDraft, connectionDraft, profileDirectory, settingsPath); }
            catch { next.Dispose(); throw; }
            var previous = appliedProfile;
            ApplyProfile(profile, next);
            if (previous != null && previous.PortraitFile != profile.PortraitFile)
                previous.DeleteImportedPortrait(profileDirectory, warn);
            ReturnToConversation();
        }
        catch (AdvisorProfileException failure)
        {
            settingsTabs.SelectedIndex = 0;
            ProfileError(failure, true);
        }
        catch (AdvisorConnectionException failure)
        {
            settingsTabs.SelectedIndex = 1;
            SettingsError(failure);
        }
    }

    private void ApplyProfile(AdvisorProfile profile, AdvisorPortrait next)
    {
        ReleasePortrait();
        portrait = next;
        portraitImage.Source = next.Source;
        profilePreview.Source = next.Source;
        portraitError.Visibility = Visibility.Collapsed;
        profilePreviewError.Visibility = Visibility.Collapsed;
        profileLoaded = true;
        appliedProfile = profile;
        speakerName = profile.DisplayName;
        advisorName.Text = profile.DisplayName;
        ResetProfileEditor(profile);
        ShowConversation(false);
    }

    private void DiscardProfileChanges()
    {
        portraitSelection = null;
        ResetProfileEditor(appliedProfile ?? new AdvisorProfile(AdvisorProfile.DefaultName, AdvisorProfile.DefaultPortraitFile, true));
        ReleasePreview();
        profilePreview.Source = portrait?.Source;
        profilePreviewError.Visibility = portrait == null ? Visibility.Visible : Visibility.Collapsed;
        if (appliedProfile != null && portrait != null) SetProfileStatus(string.Empty);
    }

    private void ResetProfileEditor(AdvisorProfile profile)
    {
        profileDraft = new AdvisorProfileDraft(profile);
        choosePortrait.IsEnabled = portraitSelection == null;
        updatingProfile = true;
        try
        {
            defaultAdvisor.IsChecked = profile.UseDefault;
            customAdvisor.IsChecked = !profile.UseDefault;
            UpdateProfileControls();
            profilePersonality.Text = profile.Personality;
        }
        finally { updatingProfile = false; }
    }

    private void ReleasePreview()
    {
        profilePreview.Source = null;
        var previous = previewPortrait;
        previewPortrait = null;
        previous?.Dispose();
    }

    private void ReleasePortrait()
    {
        ReleasePreview();
        portraitImage.Source = null;
        var previous = portrait;
        portrait = null;
        previous?.Dispose();
    }

    private void ProfileError(AdvisorProfileException failure, bool preservePortrait = false)
    {
        if (!preservePortrait || portrait == null)
        {
            ReleasePortrait();
            if (!profileLoaded) advisorName.Text = text["speaker.advisor"];
            portraitError.Visibility = Visibility.Visible;
            portraitError.Text = text["portrait.settings_error"];
            profilePreviewError.Visibility = Visibility.Visible;
            profilePreviewError.Text = text["portrait.settings_error"];
        }
        var key = text.Contains("profile.error." + failure.Code) ? failure.Code : "profile_invalid";
        SetProfileStatus(text["profile.error." + key] + text.Format("profile.error_suffix", failure.Code));
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
        if (!profileLoaded) speakerName = text["speaker.advisor"];
        SetBusy(active != null);
        ShowConversation();
    }

    private static byte[] DefaultPortrait()
    {
        using var viewSource = typeof(AdvisorWindow).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.DefaultPortrait.png")
            ?? throw new FileNotFoundException("The default advisor portrait is missing.");
        using var buffer = new MemoryStream();
        viewSource.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static T Named<T>(FrameworkElement view, string name) where T : class =>
        view.FindName(name) as T ?? throw new InvalidOperationException("The advisor control is missing: " + name);
}
