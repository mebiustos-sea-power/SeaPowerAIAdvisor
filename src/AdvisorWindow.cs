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
    private const int GeneralTabIndex = 0, AdvisorTabIndex = 1, ConnectionTabIndex = 2,
        InformationTabIndex = 3, DiagnosticsTabIndex = 4;
    private readonly FrameworkElement view;
    private readonly WindowHost host;
    private readonly Action<Action> guard;
    private readonly IAdvisorSource source;
    private readonly AdvisorOptionsStore options;
    private AdvisorOptions? optionsDraft;
    private readonly TextBox maxQueryRounds, commonPrompt;
    private readonly CheckBox enableCampaign;
    private readonly AdvisorInformationView informationChoices;
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
    private readonly TextBox diagnosticOutput, profileName, profileRole, profilePersonality, modelName, fallbackModelName;
    private readonly PasswordBox apiKey, fallbackApiKey;
    private AdvisorSettingsDraft? connectionDraft;
    private readonly ComboBox advisorSelector;
    private readonly Button addAdvisor, removeAdvisor;
    private string[] advisorChoices = Array.Empty<string>();
    private string? portraitSelectionAdvisorId;
    private readonly TextBlock advisorName, advisorRole, activity, portraitError, profilePreviewError, profileStatus;
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
    private readonly TextBlock status, diagnosticStatus, settingsStatus, mainConnectionTestStatus, mainConnectionTestError;
    private readonly TextBlock subConnectionTestStatus, subConnectionTestError;
    private TextBlock TestStatus => testingFallback ? subConnectionTestStatus : mainConnectionTestStatus;
    private TextBlock TestError => testingFallback ? subConnectionTestError : mainConnectionTestError;
    private bool settingsInputError;
    private readonly Button send, captureAction, inputAction, testConnection, testFallbackConnection;
    private readonly (AdvisorShortcut Shortcut, Button Button)[] shortcuts;
    private AdvisorJob? active;
    private AdvisorTurn? pendingTurn;
    private AdvisorNotice? configurationNotice;
    private AdvisorJob? connectionTest;
    private bool testingFallback;
    private string? pendingModel;
    private bool scrollPending;
    private bool diagnosticScrollPending;

    internal AdvisorWindow(FrameworkElement view, WindowHost host, Action<Action> guard, IAdvisorSource source, Func<string> replyLanguage, Action<string> warn, AdvisorOptionsStore options)
    {
        this.view = view;
        this.host = host;
        this.guard = guard;
        this.source = source;
        this.options = options;
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
        transcript = new AdvisorTranscriptView(Named<StackPanel>(content, "TranscriptMessages"), guard, RetryConsultation);
        status = Named<TextBlock>(content, "Status");
        diagnosticStatus = Named<TextBlock>(content, "DiagnosticStatus");
        diagnosticOutput = Named<TextBox>(content, "DiagnosticOutput");
        profileName = Named<TextBox>(content, "ProfileName");
        profileRole = Named<TextBox>(content, "ProfileRole");
        profileRole.MaxLength = AdvisorProfile.MaxRoleLength;
        profilePersonality = Named<TextBox>(content, "ProfilePersonality");
        profilePersonality.MaxLength = AdvisorProfile.MaxPersonalityLength;
        advisorSelector = Named<ComboBox>(content, "AdvisorSelector");
        addAdvisor = Named<Button>(content, "AddAdvisor");
        removeAdvisor = Named<Button>(content, "RemoveAdvisor");
        addAdvisor.Click += (_, _) => guard(AddAdvisor);
        removeAdvisor.Click += (_, _) => guard(RemoveAdvisor);
        choosePortrait = Named<Button>(content, "ChoosePortrait");
        choosePortrait.Click += (_, _) => guard(ChoosePortrait);
        advisorName = Named<TextBlock>(content, "AdvisorName");
        advisorRole = Named<TextBlock>(content, "AdvisorRole");
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
        fallbackApiKey = Named<PasswordBox>(content, "FallbackApiKey");
        modelName = Named<TextBox>(content, "ModelName");
        fallbackModelName = Named<TextBox>(content, "FallbackModelName");
        maxQueryRounds = Named<TextBox>(content, "MaxQueryRounds");
        commonPrompt = Named<TextBox>(content, "CommonPrompt");
        commonPrompt.MaxLength = AdvisorOptions.MaxCommonPromptLength;
        enableCampaign = Named<CheckBox>(content, "EnableCampaignAdvisor");
        informationChoices = new AdvisorInformationView(Named<StackPanel>(content, "InformationChoices"), guard);
        testConnection = Named<Button>(content, "TestConnection");
        testFallbackConnection = Named<Button>(content, "TestFallbackConnection");
        mainConnectionTestStatus = Named<TextBlock>(content, "ConnectionTestStatus");
        mainConnectionTestError = Named<TextBlock>(content, "ConnectionTestError");
        subConnectionTestStatus = Named<TextBlock>(content, "SubConnectionTestStatus");
        subConnectionTestError = Named<TextBlock>(content, "SubConnectionTestError");
        testConnection.Click += (_, _) => guard(() => TestConnection(false));
        testFallbackConnection.Click += (_, _) => guard(() => TestConnection(true));
        apiKey.PasswordChanged += (_, _) => guard(ClearConnectionTestResult);
        fallbackApiKey.PasswordChanged += (_, _) => guard(ClearConnectionTestResult);
        modelName.TextChanged += (_, _) => guard(ClearConnectionTestResult);
        fallbackModelName.TextChanged += (_, _) => guard(ClearConnectionTestResult);
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
        foreach (var input in new[] { profileName, profileRole, profilePersonality, modelName, fallbackModelName, maxQueryRounds, commonPrompt, diagnosticOutput, Named<TextBox>(content, "ProfilePath") })
        {
            input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input));
            input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
        }
        foreach (var input in new[] { apiKey, fallbackApiKey })
        {
            input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input));
            input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
        }
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
        window.OnClosed += (_, _) => guard(OnWindowClosed);
        settingsTabs.SelectionChanged += (_, _) => guard(() =>
        {
            if (!IsOpen || settingsPane.Visibility != Visibility.Visible) return;
            ReleaseInput();
            FocusSettings();
        });
        advisorSelector.SelectionChanged += (_, _) => guard(ChangeAdvisor);
        profileName.TextChanged += (_, _) => guard(UpdateAdvisorCaption);
        try { AdvisorProfile.EnsureCreated(profileDirectory, DefaultPortrait, text["advisor.role"]); }
        catch (AdvisorProfileException failure) { ProfileError(failure); }
        ShowConversation();
    }

    internal bool IsOpen => host.Windows.Contains(window);

    internal void Toggle()
    {
        if (IsOpen) { Close(); return; }
        RefreshLanguage();
        LoadProfile();
        RefreshConfigurationNotice();
        ShowConversation(false);
        WindowHostCommands.OpenWindowCommand.Execute(window, view);
        if (!IsOpen) throw new InvalidOperationException("The game window host did not open the advisor.");
        // Reopen the conversation with its history and message text intact.
        choosePortrait.IsEnabled = portraitSelection == null;
        question.Focus();
    }

    internal void Tick()
    {
        if (!IsOpen) { CancelActive(); CancelConnectionTest(); questionInput.Reset(); typing.Release(); ReleasePortrait(); return; }
        CompletePortraitSelection();
        questionInput.Tick();
        CompleteActive();
        UpdateActiveProgress();
        CompleteConnectionTest();
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
        if (IsOpen) window.CloseWindowNow();
        else OnWindowClosed();
    }

    private void OnWindowClosed()
    {
        CancelActive();
        ReleaseInput();
        DiscardProfileChanges();
        DiscardConnectionChanges();
        settingsPane.Visibility = Visibility.Collapsed;
        conversationPane.Visibility = Visibility.Visible;
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

    private void RetryConsultation(AdvisorTurn turn)
    {
        if (!conversation.CanRetry(turn)) return;
        questionInput.Reset();
        StartConsultation(turn.Request, turn);
    }

    private void StartConsultation(AdvisorQuestion request, AdvisorTurn? retry = null)
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
        conversation.UseInformationPolicy(options.Current.Information.Key);
        var answeringName = appliedProfile?.DisplayName ?? speakerName;
        var answeringId = appliedProfile?.ActiveId ?? AdvisorProfile.DefaultId;
        var turn = retry ?? conversation.Begin(request, answeringName, answeringId);
        if (retry != null) conversation.Retry(turn, answeringName, answeringId);
        else question.Text = request.DraftAfterSend(question.Text ?? string.Empty);
        configurationNotice = null;
        ShowConversation(false);
        scrollPending = true;
        try
        {
            var settings = AdvisorSettings.Load(settingsPath);
            var session = source.CreateSession(replyLanguage(), request.Text, options.Current.MaxQueryRounds, options.Current.Information);
            var history = conversation.Exchanges.ToArray();
            pendingTurn = turn;
            pendingModel = settings.Model;
            var logDirectory = AdvisorTrace.DirectoryIn(UnityEngine.Application.persistentDataPath);
            var advisorProfile = appliedProfile;
            var sharedPrompt = options.Current.CommonPrompt;
            active = new AdvisorJob((cancellation, progress) =>
                AdvisorConsultation.RunAsync(logDirectory, session, settings, request, history, cancellation, profile: advisorProfile, commonPrompt: sharedPrompt, progress: progress), warn);
            SetBusy(true);
            status.Text = text["status.consulting"];
        }
        catch (AdvisorConnectionException failure)
        {
            FailTurn(turn, AdvisorNotice.Connection(failure));
            warn("Gemini consultation failed: " + failure.Message);
        }
        catch (AdvisorPromptException failure)
        {
            FailTurn(turn, AdvisorNotice.Prompts(failure));
            warn("System prompts unavailable: " + failure.Message);
        }
        catch (SituationUnavailableException failure)
        {
            FailTurn(turn, AdvisorNotice.Situation(failure.Code));
            warn("Consultation refused: " + failure.Message);
        }
        catch
        {
            FailTurn(turn, AdvisorNotice.Unexpected());
            throw;
        }
    }

    private void UpdateActiveProgress()
    {
        var progress = active?.TakeProgress();
        if (progress == null) return;
        var turn = pendingTurn ?? throw new InvalidOperationException("The pending turn is missing.");
        var follow = transcriptScroll.VerticalOffset >= transcriptScroll.ScrollableHeight - 4;
        turn.UpdateProgress(progress);
        transcript.UpdateProgress(turn, text);
        if (follow) scrollPending = true;
    }

    private void CompleteActive()
    {
        var job = active;
        if (job == null || !job.Completion.IsCompleted) return;
        var turn = pendingTurn ?? throw new InvalidOperationException("The pending turn is missing.");
        var model = pendingModel;
        active = null;
        pendingTurn = null;
        var completion = job.TakeCompleted();
        SetBusy(false);
        if (completion.UnexpectedError != null)
        {
            FailTurn(turn, AdvisorNotice.Unexpected());
            throw completion.UnexpectedError;
        }
        if (completion.IsCancelled)
        {
            turn.Cancel();
            ShowConversation(false);
            status.Text = text["status.cancelled"];
        }
        else if (completion.Error != null)
        {
            FailTurn(turn, AdvisorNotice.Connection(completion.Error));
            warn("Gemini consultation failed: " + completion.Error.Message);
        }
        else
        {
            var result = completion.Result ?? throw new InvalidOperationException("The completed consultation has no result.");
            turn.Complete(result.Text);
            ShowConversation(false);
            status.Text = text.Format("status.completed", result.Model.Length > 0 ? result.Model : model ?? string.Empty, result.QueryRounds, result.ApiRequests,
                result.TotalTokens.HasValue ? text.Format("status.token_count", result.TotalTokens.Value) : text["status.tokens_unknown"]);
        }
        scrollPending = true;
    }

    private void FailTurn(AdvisorTurn turn, AdvisorNotice error)
    {
        turn.Fail(error);
        pendingTurn = null;
        ShowConversation(false);
        status.Text = text["status.failed"];
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
        UpdateConnectionTestControls();
    }

    private void CancelActive()
    {
        if (active == null) return;
        var job = active; active = null;
        var turn = pendingTurn ?? throw new InvalidOperationException("The pending turn is missing.");
        pendingTurn = null;
        job.Dispose();
        turn.Cancel();
        SetBusy(false);
        ShowConversation(false);
        status.Text = text["status.cancelled"];
        scrollPending = true;
    }

    private void RefreshConfigurationNotice()
    {
        try { _ = AdvisorSettings.Load(settingsPath); _ = AdvisorPrompts.Current; configurationNotice = null; }
        catch (AdvisorConnectionException error) { configurationNotice = AdvisorNotice.Connection(error); }
        catch (AdvisorPromptException error) { configurationNotice = AdvisorNotice.Prompts(error); }
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
                var input = source.CreateSession(language, question.Text, options.Current.MaxQueryRounds, options.Current.Information).InitialInput();
                appliedProfile?.AddAnswerPreferences(input);
                input["common_prompt"] = options.Current.CommonPrompt;
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
        catch (AdvisorPromptException error)
        {
            diagnosticOutput.Text = AdvisorNotice.Prompts(error).Message(text);
            diagnosticStatus.Text = text["diagnostics.prompt_failed"];
            warn("System prompts unavailable: " + error.Message);
        }
        catch (SituationUnavailableException error)
        {
            diagnosticOutput.Text = text["situation.error." + error.Code];
            diagnosticStatus.Text = text["diagnostics.situation_refused"];
            warn("Situation preview refused: " + error.Message);
        }
    }

    private void ShowConversation(bool updateStatus = true, string? empty = null)
    {
        scrollPending = false;
        diagnosticScrollPending = false;
        transcript.Show(conversation, text, speakerName, empty ?? text["transcript.empty"], configurationNotice);
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
        RefreshConfigurationNotice();
        ShowConversation(false, text["transcript.cleared"]);
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
        RefreshConfigurationNotice();
        ShowConversation(false);
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
            fallbackApiKey.Password = connectionDraft.FallbackApiKey;
            modelName.Text = connectionDraft.Model;
            fallbackModelName.Text = connectionDraft.FallbackModel;
            logAiJson.IsChecked = connectionDraft.LogAiJson;
        }
        catch (AdvisorConnectionException failure) { connectionDraft = null; SettingsError(failure); }
        try
        {
            optionsDraft = options.Read();
            maxQueryRounds.Text = optionsDraft.MaxQueryRounds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            enableCampaign.IsChecked = optionsDraft.CampaignEnabled;
            informationChoices.Reset(optionsDraft.Information);
            commonPrompt.Text = optionsDraft.CommonPrompt;
        }
        catch (AdvisorOptionsException failure) { optionsDraft = null; OptionsError(failure); }
        maxQueryRounds.IsEnabled = commonPrompt.IsEnabled = enableCampaign.IsEnabled = optionsDraft != null;
        logAiJson.IsEnabled = connectionDraft != null;
        saveSettings.IsEnabled = connectionDraft != null && optionsDraft != null;
        UpdateConnectionTestControls();
    }

    private void DiscardConnectionChanges()
    {
        CancelConnectionTest();
        connectionDraft = null;
        optionsDraft = null;
        informationChoices.Reset(null);
        maxQueryRounds.Text = string.Empty;
        commonPrompt.Text = string.Empty;
        enableCampaign.IsChecked = false;
        apiKey.Password = string.Empty;
        fallbackApiKey.Password = string.Empty;
        modelName.Text = string.Empty;
        fallbackModelName.Text = string.Empty;
        logAiJson.IsChecked = false;
        ClearConnectionTestResult();
        UpdateConnectionTestControls();
    }

    private void TestConnection(bool fallback)
    {
        if (connectionTest != null) { CancelConnectionTest(); return; }
        if (active != null || connectionDraft == null || settingsPane.Visibility != Visibility.Visible) return;
        if (settingsInputError) SetSettingsStatus(string.Empty);
        testingFallback = fallback;
        if (QuestionInputController.IsComposing) { ShowConnectionTestError(text["profile.ime"]); return; }
        try
        {
            ReadConnectionDraft();
            var settings = connectionDraft.TestSettings(fallback);
            var directory = AdvisorTrace.DirectoryIn(UnityEngine.Application.persistentDataPath);
            ClearConnectionTestResult();
            connectionTest = new AdvisorJob(cancellation => GeminiConnectionTest.RunAsync(directory, settings, cancellation), warn);
            TestStatus.Text = ConnectionTestMessage(text["connection.test_running"]);
            UpdateConnectionTestControls();
        }
        catch (AdvisorConnectionException error) { ShowConnectionTestError(AdvisorNotice.Connection(error).Message(text)); }
    }

    private void CompleteConnectionTest()
    {
        var job = connectionTest;
        if (job == null || !job.Completion.IsCompleted) return;
        connectionTest = null;
        var completion = job.TakeCompleted();
        UpdateConnectionTestControls();
        if (completion.UnexpectedError != null)
        {
            ShowConnectionTestError(AdvisorNotice.Unexpected().Message(text));
            throw completion.UnexpectedError;
        }
        if (completion.IsCancelled) TestStatus.Text = ConnectionTestMessage(text["connection.test_cancelled"]);
        else if (completion.Error != null)
        {
            ShowConnectionTestError(AdvisorNotice.Connection(completion.Error).Message(text));
            warn("Gemini connection test failed: " + completion.Error.Message);
        }
        else
        {
            _ = completion.Result ?? throw new InvalidOperationException("The connection test has no result.");
            TestStatus.Text = ConnectionTestMessage(text["connection.test_succeeded"]);
        }
    }

    private void CancelConnectionTest()
    {
        if (connectionTest == null) return;
        var job = connectionTest; connectionTest = null;
        job.Dispose();
        UpdateConnectionTestControls();
        TestStatus.Text = ConnectionTestMessage(text["connection.test_cancelled"]);
    }

    private void UpdateConnectionTestControls()
    {
        apiKey.IsEnabled = fallbackApiKey.IsEnabled = modelName.IsEnabled = fallbackModelName.IsEnabled = connectionDraft != null && connectionTest == null;
        var canTest = connectionDraft != null && active == null;
        testConnection.IsEnabled = canTest && (connectionTest == null || !testingFallback);
        testFallbackConnection.IsEnabled = canTest && (connectionTest == null || testingFallback);
        testConnection.Content = text[connectionTest != null && !testingFallback ? "action.cancel" : "connection.test"];
        testFallbackConnection.Content = text[connectionTest != null && testingFallback ? "action.cancel" : "connection.test"];
    }

    private string ConnectionTestMessage(string message) => text.Format("connection.test_result",
        text[testingFallback ? "connection.fallback_key_name" : "connection.primary_key_name"], message);

    private void ClearConnectionTestResult()
    {
        if (connectionTest != null) return;
        foreach (var label in new[] { mainConnectionTestStatus, subConnectionTestStatus }) label.Text = string.Empty;
        foreach (var label in new[] { mainConnectionTestError, subConnectionTestError })
        { label.Text = string.Empty; label.Visibility = Visibility.Collapsed; }
    }

    private void ShowConnectionTestError(string message)
    {
        TestStatus.Text = string.Empty;
        TestError.Text = ConnectionTestMessage(message);
        TestError.Visibility = Visibility.Visible;
    }

    private void ReadConnectionDraft()
    {
        if (connectionDraft == null) throw new InvalidOperationException("The connection draft is missing.");
        connectionDraft.ApiKey = apiKey.Password;
        connectionDraft.FallbackApiKey = fallbackApiKey.Password;
        connectionDraft.Model = modelName.Text ?? string.Empty;
        connectionDraft.FallbackModel = fallbackModelName.Text ?? string.Empty;
        connectionDraft.LogAiJson = logAiJson.IsChecked == true;
    }

    private AdvisorOptions ReadOptionsDraft()
    {
        if (!int.TryParse(maxQueryRounds.Text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var rounds)) throw new AdvisorOptionsException("query_rounds_invalid");
        return new AdvisorOptions(rounds, enableCampaign.IsChecked == true, informationChoices.Value, commonPrompt.Text ?? "");
    }

    private void OptionsError(AdvisorOptionsException failure)
    {
        SetSettingsStatus(text["settings.error." + failure.Code] + text.Format("settings.error_suffix", failure.Code));
        warn("Advisor options failed: " + failure.Code);
    }

    private void SettingsError(AdvisorConnectionException failure)
    {
        var key = text.Contains("settings.error." + failure.Code) ? failure.Code : "settings_invalid";
        SetSettingsStatus(text["settings.error." + key] + text.Format("settings.error_suffix", failure.Code));
        settingsInputError = failure.Code is "api_key_missing" or "model_invalid" or "fallback_model_invalid" or "settings_invalid";
        warn("Advisor settings failed: " + failure.Code);
    }

    private void SetSettingsStatus(string message)
    {
        settingsInputError = false;
        settingsStatus.Text = message;
        settingsStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FocusSettings()
    {
        if (settingsTabs.SelectedIndex == ConnectionTabIndex)
        {
            if (apiKey.IsEnabled) apiKey.Focus();
            else Named<Button>(content, "CancelSettings").Focus();
        }
        else if (settingsTabs.SelectedIndex == InformationTabIndex) informationChoices.Focus();
        else if (settingsTabs.SelectedIndex == DiagnosticsTabIndex) captureAction.Focus();
        else if (settingsTabs.SelectedIndex == AdvisorTabIndex)
        {
            if (profileDraft.UseDefault) profilePersonality.Focus();
            else profileName.Focus();
        }
        else if (enableCampaign.IsEnabled) enableCampaign.Focus();
        else Named<Button>(content, "CancelSettings").Focus();
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
        profileRole.Text = profileDraft.UseDefault ? text["advisor.role"] : profileDraft.CustomRole;
        profileRole.IsReadOnly = profileDraft.UseDefault;
        choosePortrait.Visibility = profileDraft.UseDefault ? Visibility.Collapsed : Visibility.Visible;
        UpdateProfileActions();
    }

    private void UpdateProfileActions()
    {
        advisorSelector.IsEnabled = addAdvisor.IsEnabled = portraitSelection == null;
        removeAdvisor.IsEnabled = !profileDraft.UseDefault && portraitSelection == null;
        choosePortrait.IsEnabled = portraitSelection == null;
    }

    private void ReadProfileDraft()
    {
        if (!profileName.IsReadOnly)
        {
            profileDraft.CustomName = profileName.Text ?? "";
            profileDraft.CustomRole = profileRole.Text ?? "";
        }
        profileDraft.Personality = profilePersonality.Text ?? "";
    }

    private void RefreshAdvisorSelector()
    {
        var wasUpdating = updatingProfile; updatingProfile = true;
        try
        {
            advisorChoices = new[] { AdvisorProfile.DefaultId }.Concat(profileDraft.Advisers.Select(a => a.Id)).ToArray();
            advisorSelector.Items.Clear();
            advisorSelector.Items.Add(new ComboBoxItem { Content = text.Format("profile.default_choice", AdvisorProfile.DefaultName) });
            foreach (var adviser in profileDraft.Advisers)
                advisorSelector.Items.Add(new ComboBoxItem { Content = string.IsNullOrWhiteSpace(adviser.Name) ? text["profile.new_name"] : adviser.Name });
            advisorSelector.SelectedIndex = Array.IndexOf(advisorChoices, profileDraft.SelectedId);
        }
        finally { updatingProfile = wasUpdating; }
    }

    private void UpdateAdvisorCaption()
    {
        if (updatingProfile || profileName.IsReadOnly || advisorSelector.SelectedIndex < 1) return;
        ((ComboBoxItem)advisorSelector.Items[advisorSelector.SelectedIndex]).Content =
            string.IsNullOrWhiteSpace(profileName.Text) ? text["profile.new_name"] : profileName.Text;
    }

    private void ChangeAdvisor()
    {
        if (updatingProfile || advisorSelector.SelectedIndex < 0) return;
        ReadProfileDraft();
        profileDraft.Select(advisorChoices[advisorSelector.SelectedIndex]);
        UpdateProfileControls();
        profilePersonality.Text = profileDraft.Personality;
        RefreshProfilePreview();
        ReleaseInput(); advisorSelector.Focus();
    }

    private void AddAdvisor()
    {
        if (portraitSelection != null) return;
        ReadProfileDraft();
        profileDraft.Add(text["profile.new_name"], text["advisor.role"]);
        RefreshAdvisorSelector(); UpdateProfileControls();
        profilePersonality.Text = profileDraft.Personality;
        RefreshProfilePreview(); ReleaseInput(); profileName.Focus();
    }

    private void RemoveAdvisor()
    {
        if (profileDraft.UseDefault || portraitSelection != null) return;
        ReadProfileDraft(); profileDraft.Remove(profileDraft.SelectedId);
        RefreshAdvisorSelector(); UpdateProfileControls();
        profilePersonality.Text = profileDraft.Personality;
        RefreshProfilePreview(); ReleaseInput(); advisorSelector.Focus();
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
        portraitSelectionAdvisorId = profileDraft.SelectedCustomId;
        portraitSelection = PortraitFileDialog.OpenAsync(text["profile.choose_title"], text["profile.image_filter"]);
        UpdateProfileActions();
    }

    private void CompletePortraitSelection()
    {
        if (portraitSelection == null || !portraitSelection.IsCompleted) return;
        var selection = portraitSelection;
        var targetId = portraitSelectionAdvisorId ?? throw new InvalidOperationException("The portrait selection adviser is missing.");
        portraitSelection = null; portraitSelectionAdvisorId = null;
        UpdateProfileActions();
        try
        {
            var path = selection.GetAwaiter().GetResult();
            if (path == null) return;
            var selected = new AdvisorProfile(AdvisorProfile.DefaultName, path);
            var image = selected.ReadPortrait(profileDirectory);
            var next = new AdvisorPortrait(image);
            profileDraft.SelectPortrait(targetId, image);
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
            if (settingsPane.Visibility == Visibility.Visible && settingsTabs.SelectedIndex == AdvisorTabIndex) FocusSettings();
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
        if (portraitSelection != null || connectionDraft == null || optionsDraft == null) return;
        CancelConnectionTest();
        SetSettingsStatus(string.Empty);
        ReadProfileDraft();
        ReadConnectionDraft();
        try
        {
            _ = connectionDraft.Data();
            var nextOptions = ReadOptionsDraft();
            var next = new AdvisorPortrait(profileDraft.ReadActivePortrait(profileDirectory, DefaultPortrait));
            AdvisorProfile profile;
            try { profile = AdvisorPreferences.Save(profileDraft, connectionDraft, options, nextOptions, profileDirectory, settingsPath); }
            catch { next.Dispose(); throw; }
            conversation.UseInformationPolicy(nextOptions.Information.Key);
            var previous = appliedProfile;
            ApplyProfile(profile, next);
            previous?.DeleteUnusedPortraits(profileDirectory, profile, warn);
            ReturnToConversation();
        }
        catch (AdvisorProfileException failure)
        {
            settingsTabs.SelectedIndex = AdvisorTabIndex;
            ProfileError(failure, true);
        }
        catch (AdvisorConnectionException failure)
        {
            settingsTabs.SelectedIndex = ConnectionTabIndex;
            SettingsError(failure);
        }
        catch (AdvisorOptionsException failure)
        {
            settingsTabs.SelectedIndex = failure.Code is "query_rounds_invalid" or "common_prompt_invalid" ? ConnectionTabIndex : failure.Code == "information_invalid" ? InformationTabIndex : GeneralTabIndex;
            OptionsError(failure);
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
        advisorRole.Text = profile.UseDefault ? text["advisor.role"] : profile.CustomRole;
        advisorRole.Visibility = advisorRole.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ResetProfileEditor(profile);
        ShowConversation(false);
    }

    private void DiscardProfileChanges()
    {
        portraitSelection = null; portraitSelectionAdvisorId = null;
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
            RefreshAdvisorSelector();
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
