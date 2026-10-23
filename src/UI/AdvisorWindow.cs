using System;
using System.IO;
using System.Linq;
using System.Text;
using Noesis;
using Newtonsoft.Json.Linq;
using SeapowerUI;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorWindow : IAdvisorLocalTarget
{
    private readonly FrameworkElement view, content, conversationPane;
    private readonly WindowHost host;
    private readonly Action<Action> guard;
    private readonly IAdvisorSource source;
    private readonly AdvisorOptionsStore options;
    private readonly AdvisorBackendRegistry backends;
    private readonly Func<string> replyLanguage;
    private readonly Action<string> warn;
    private readonly AdvisorConversation conversation = new();
    private readonly TypingScope typing = new();
    private readonly QuestionInputController questionInput;
    private readonly AdvisorUnitDraft unitDraft = new();
    private readonly AdvisorUnitBadgeView unitBadges;
    private readonly Window window;
    private readonly AdvisorSettingsController settings;
    private AdvisorText text;
    private string uiLanguage;
    private readonly TextBox question, diagnosticOutput;
    private readonly ScrollViewer transcriptScroll;
    private readonly AdvisorTranscriptView transcript;
    private readonly AdvisorOperationReview operationReview;
    private AdvisorOperations? OperationService => (source as IAdvisorOperationSource)?.Operations;
    private readonly TextBlock advisorName, advisorRole, activity, portraitError, status, diagnosticStatus, unitNavigationHint;
    private readonly IAdvisorUnitNavigation? navigation;
    private readonly Image portraitImage;
    private readonly string settingsPath, profileDirectory;
    private AdvisorPortrait? portrait;
    private AdvisorProfile? appliedProfile;
    private string speakerName;
    private bool profileLoaded;
    private readonly Button send, captureAction, inputAction, insertSelectedUnit, clearInput;
    private readonly (AdvisorShortcut Shortcut, Button Button)[] shortcuts;
    private AdvisorJob? active;
    private AdvisorLocalRun? localRun;
    private AdvisorTurn? pendingTurn;
    private AdvisorNotice? configurationNotice;
    private string pendingConnectionName = "";
    private Action? pendingScroll;
    private bool diagnosticScrollPending;

    internal AdvisorWindow(FrameworkElement view, WindowHost host, Action<Action> guard, IAdvisorSource source, Func<string> replyLanguage, Action<string> warn, AdvisorOptionsStore options, AdvisorBackendRegistry backends,
        IAdvisorUnitNavigation? navigation = null, IAdvisorWeaponNavigation? weapons = null, IAdvisorMapNavigation? map = null)
    {
        this.view = view;
        this.host = host;
        this.guard = guard;
        this.source = source;
        this.options = options;
        this.backends = backends;
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
        unitNavigationHint = Named<TextBlock>(content, "UnitNavigationHint");
        this.navigation = navigation;
        transcript = new AdvisorTranscriptView(Named<StackPanel>(content, "TranscriptMessages"), transcriptScroll, guard, RetryConsultation, ReassessContact, InsertUnit, navigation, weapons, map, ReviewProposal);
        status = Named<TextBlock>(content, "Status");
        diagnosticStatus = Named<TextBlock>(content, "DiagnosticStatus");
        diagnosticOutput = Named<TextBox>(content, "DiagnosticOutput");
        advisorName = Named<TextBlock>(content, "AdvisorName");
        advisorRole = Named<TextBlock>(content, "AdvisorRole");
        activity = Named<TextBlock>(content, "Activity");
        portraitError = Named<TextBlock>(content, "PortraitError");
        portraitImage = Named<Image>(content, "Portrait");
        conversationPane = Named<FrameworkElement>(content, "ConversationPane");
        profileDirectory = AdvisorProfile.DirectoryIn(UnityEngine.Application.persistentDataPath);
        settingsPath = AdvisorSettings.PathIn(UnityEngine.Application.persistentDataPath);
        send = Named<Button>(content, "Send");
        send.Click += (_, _) => guard(Send);
        insertSelectedUnit = Named<Button>(content, "InsertSelectedUnit");
        insertSelectedUnit.Visibility = navigation == null ? Visibility.Collapsed : Visibility.Visible;
        insertSelectedUnit.Click += (_, _) => guard(InsertSelectedUnit);
        Named<Button>(content, "Clear").Click += (_, _) => guard(Clear);
        clearInput = Named<Button>(content, "ClearInput");
        clearInput.Click += (_, _) => guard(ClearInput);
        question.TextChanged += (_, _) => guard(UpdateInputClear);
        var conversationBackground = content.Resources["AdvisorConversationBackgroundBrush"] as Brush
            ?? throw new InvalidOperationException("The advisor conversation background brush is missing.");
        unitBadges = new AdvisorUnitBadgeView(question, Named<ScrollViewer>(content, "UnitBadgeArea"),
            Named<WrapPanel>(content, "UnitBadges"), conversationBackground, RemoveUnit, guard);
        captureAction = Named<Button>(content, "Capture");
        captureAction.Click += (_, _) => guard(() => Preview(false));
        inputAction = Named<Button>(content, "Input");
        inputAction.Click += (_, _) => guard(() => Preview(true));

        shortcuts = new[]
        {
            (AdvisorShortcut.NextSteps, Named<Button>(content, "NextSteps")),
            (AdvisorShortcut.KeyRisks, Named<Button>(content, "KeyRisks")),
            (AdvisorShortcut.RadarOperations, Named<Button>(content, "RadarOperations")),
            (AdvisorShortcut.OwnUnits, Named<Button>(content, "OwnUnits")),
            (AdvisorShortcut.Contacts, Named<Button>(content, "Contacts"))
        };
        foreach (var item in shortcuts)
            item.Button.Click += (_, _) => guard(() => ConsultShortcut(item.Shortcut, item.Button));

        questionInput = new QuestionInputController(question, () => active != null, StartConsultation, guard);
        question.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(question));
        question.LostKeyboardFocus += (_, _) => guard(() => typing.Release(question));
        foreach (var input in new[] { diagnosticOutput })
        {
            input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input));
            input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input));
        }
        var width = Math.Min(680, view.ActualWidth - 40);
        var height = Math.Min(720, view.ActualHeight - 120);
        if (width < 360 || height < 360) throw new InvalidOperationException("The game view is too small for the advisor prototype.");
        window = new Window
        {
            Header = WindowTitle, Content = content,
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
        settings = new AdvisorSettingsController(content, () => text, options, profileDirectory, settingsPath, DefaultPortrait,
            guard, typing, ReleaseInput, () => active != null, ApplyProfile, () =>
            {
                conversation.UseInformationPolicy(options.Current.Information.Key);
                RefreshConfigurationNotice(); ShowConversation(false);
            }, ReturnToConversation, warn);
        operationReview = new AdvisorOperationReview(content, conversationPane, () => OperationService,
            () => text, guard, () => ShowConversation(false), map, navigation);
        Named<Button>(content, "Settings").Click += (_, _) => guard(settings.Open);
        try
        {
            AdvisorProfile.EnsureCreated(profileDirectory, DefaultPortrait, text["advisor.role"]);
            appliedProfile = AdvisorProfile.Load(profileDirectory);
        }
        catch (AdvisorProfileException failure) { ProfileError(failure); }
        ShowConversation();
    }

    internal bool IsOpen => host.Windows.Contains(window);
    internal string DisplayName => appliedProfile?.DisplayName ?? speakerName;
    internal bool CanConsult => active == null && !operationReview.IsOpen && conversationPane.Visibility == Visibility.Visible;

    internal void ConsultContext(string promptKey, string caption, Newtonsoft.Json.Linq.JObject context)
    {
        if (!CanConsult) return;
        if (!IsOpen) Toggle();
        questionInput.Reset();
        try
        {
            StartConsultation(new AdvisorQuestion(AdvisorPrompts.Current.Text("requests.json", promptKey), caption, true, context, allowOperations: false));
        }
        catch (AdvisorPromptException error)
        {
            configurationNotice = AdvisorNotice.Prompts(error); ShowConversation(false);
            warn("Contextual consultation prompt unavailable: " + error.Message);
        }
    }

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
        question.Focus();
    }

    internal void Tick()
    {
        if (!IsOpen) { operationReview.Close(); CancelActive(); questionInput.Reset(); typing.Release(); ReleasePortrait(); return; }
        settings.Tick();
        operationReview.Tick();
        UpdateUnitInsertion();
        questionInput.Tick();
        CompleteActive();
        UpdateActiveProgress();
        transcript.Tick();
        if (diagnosticScrollPending)
        {
            diagnosticOutput.ScrollToHome();
            diagnosticScrollPending = false;
        }
        if (pendingScroll != null && conversationPane.Visibility == Visibility.Visible)
        {
            var scroll = pendingScroll; pendingScroll = null;
            transcriptScroll.UpdateLayout();
            scroll();
            transcriptScroll.UpdateLayout();
        }
    }

    // Fault teardown must cancel work and release input without rendering the broken conversation again.
    internal void Stop()
    {
        var job = active; active = null;
        if (pendingTurn?.State == AdvisorTurnState.Waiting)
        { FinishProposal(pendingTurn, false); pendingTurn.Cancel(); }
        OperationService?.DiscardProposalsExcept(Array.Empty<string>());
        operationReview.Close();
        pendingTurn = null; FinishRecording();
        try { job?.Dispose(); }
        finally
        {
            try { ReleaseInput(); }
            finally
            {
                try { settings.Close(); }
                finally
                {
                    try { ReleasePortrait(); }
                    finally { if (IsOpen) window.CloseWindowNow(); }
                }
            }
        }
    }

    internal void Close()
    {
        if (IsOpen) window.CloseWindowNow();
        else OnWindowClosed();
    }

    private void OnWindowClosed()
    {
        operationReview.Close();
        CancelActive();
        ReleaseInput();
        settings.Close();
        ReleasePortrait();
    }

    private void ReleaseInput()
    {
        questionInput.Reset();
        typing.Release();
        if (window.IsKeyboardFocusWithin) window.Keyboard.ClearFocus();
    }

    private void UpdateUnitInsertion() => insertSelectedUnit.IsEnabled = active == null && navigation?.SelectedUnit() != null;

    private void InsertSelectedUnit() => InsertUnit(navigation?.SelectedUnit());
    private void InsertUnit(int unitId) => InsertUnit(navigation?.GetUnit(unitId));

    private bool CanEditDraft()
    {
        if (!CanConsult || question.IsReadOnly) return false;
        if (!QuestionInputController.IsComposing) return true;
        status.Text = text["status.ime"];
        return false;
    }

    private void InsertUnit((int Id, string Name)? selected)
    {
        if (!CanEditDraft() || !selected.HasValue) return;
        questionInput.Reset();
        if (unitDraft.Add(selected.Value.Id, selected.Value.Name)) RefreshUnitBadges();
        question.Focus();
    }

    private void RemoveUnit(int unitId)
    {
        if (!CanEditDraft()) return;
        questionInput.Reset();
        unitDraft.Remove(unitId);
        RefreshUnitBadges();
        question.Focus();
    }

    private void RefreshUnitBadges() { unitBadges.Show(unitDraft.Units); UpdateInputClear(); }
    private void UpdateInputClear() => clearInput.IsEnabled = active == null &&
        (!string.IsNullOrEmpty(question.Text) || unitDraft.Units.Count > 0);

    private void ClearDraft()
    {
        question.Text = string.Empty;
        unitDraft.Clear();
        RefreshUnitBadges();
    }

    private void ClearInput()
    {
        if (!CanEditDraft()) return;
        questionInput.Reset();
        ClearDraft();
        question.SelectionStart = 0;
        question.SelectionLength = 0;
        question.Focus();
    }

    private void Send()
    {
        questionInput.Reset();
        if (active != null) { CancelActive(); return; }
        StartConsultation();
    }

    private void ConsultShortcut(AdvisorShortcut shortcut, Button button)
    {
        if (conversationPane.Visibility != Visibility.Visible || !shortcut.IsAvailable(active != null, conversation.Exchanges.Count, options.Current.HistoryExchanges)) return;
        questionInput.Reset();
        try
        {
            StartConsultation(shortcut.Prepare(button.Content as string
                ?? throw new InvalidOperationException("The shortcut caption is unavailable.")));
        }
        catch (AdvisorPromptException error)
        {
            configurationNotice = AdvisorNotice.Prompts(error); ShowConversation(false);
            warn("Shortcut prompt unavailable: " + error.Message);
        }
    }

    JObject IAdvisorLocalTarget.LocalStatus() => new()
    {
        ["proposal_review_open"] = operationReview.IsOpen, ["busy"] = active != null, ["settings_open"] = conversationPane.Visibility != Visibility.Visible,
        ["advisor_name"] = DisplayName, ["answered_exchanges"] = conversation.Exchanges.Count
    };
    AdvisorLocalRun IAdvisorLocalTarget.LocalConsult(string? message, string? shortcut, bool allowFallback, Func<AdvisorSituationDelivery?, AdvisorBackendSetup> prepare, bool plainAdvisor, bool? allowOperations, AdvisorSituationDelivery? situationDelivery)
    {
        RequireLocalReady();
        if (QuestionInputController.IsComposing) throw new AdvisorLocalApiException(409, "input_composing");
        AdvisorQuestion request;
        if (shortcut != null)
        {
            var preset = AdvisorShortcut.Find(shortcut) ?? throw new AdvisorLocalApiException(400, "unknown_shortcut");
            if (!preset.IsAvailable(false, conversation.Exchanges.Count, options.Current.HistoryExchanges))
                throw new AdvisorLocalApiException(409, "shortcut_unavailable");
            request = preset.Prepare(text[preset.LabelKey]);
        }
        else
        {
            if (!AdvisorConversation.Validate(message, out var error)) throw new AdvisorLocalApiException(400, error);
            request = new AdvisorQuestion(message!);
        }
        if (allowOperations.HasValue) request = request.WithOperations(allowOperations.Value);
        if (situationDelivery.HasValue) request = request.WithSituationDelivery(situationDelivery);
        var execution = prepare(request.SituationDelivery);
        var run = new AdvisorLocalRun(allowFallback, plainAdvisor, request.AllowOperations, execution);
        guard(() => { if (!IsOpen) Toggle(); StartConsultation(request, recording: run, prepared: execution); });
        if (!run.Attached) throw new AdvisorLocalApiException(409, "advisor_unavailable");
        return run;
    }
    void IAdvisorLocalTarget.LocalClear() { RequireLocalReady(); guard(Clear); }
    void IAdvisorLocalTarget.LocalCancel() { guard(CancelActive); }
    private void RequireLocalReady()
    {
        if (operationReview.IsOpen) throw new AdvisorLocalApiException(409, "proposal_review_open");
        if (active != null) throw new AdvisorLocalApiException(409, "consultation_in_progress");
        if (conversationPane.Visibility != Visibility.Visible) throw new AdvisorLocalApiException(409, "settings_open");
    }

    private void StartConsultation() => StartConsultation(unitDraft.Prepare(question.Text ?? string.Empty));

    private void RetryConsultation(AdvisorTurn turn)
    {
        if (!conversation.CanRetry(turn)) return;
        questionInput.Reset();
        StartConsultation(turn.Request, turn);
    }

    private void ReassessContact(AdvisorTurn turn, ContactAssumption assumption)
    {
        if (!CanConsult || !conversation.Turns.Contains(turn) || !BattleContactAssessment.CanReassess(turn)) return;
        questionInput.Reset();
        try
        {
            StartConsultation(BattleContactAssessment.Reassess(turn, assumption,
                text["contact.reassess_" + BattleContactAssessment.AffiliationCode(assumption)]));
        }
        catch (AdvisorPromptException error)
        {
            configurationNotice = AdvisorNotice.Prompts(error); ShowConversation(false);
            warn("Contact reassessment prompt unavailable: " + error.Message);
        }
    }

    private void StartConsultation(AdvisorQuestion request, AdvisorTurn? retry = null, AdvisorLocalRun? recording = null, AdvisorBackendSetup? prepared = null)
    {
        if (active != null || operationReview.IsOpen || conversationPane.Visibility != Visibility.Visible) return;
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
        var history = conversation.Exchanges.ToArray();
        var advisorProfile = recording?.ProfileFor(appliedProfile) ?? appliedProfile;
        var answeringName = advisorProfile?.DisplayName ?? speakerName;
        var answeringId = advisorProfile?.ActiveId ?? AdvisorProfile.DefaultId;
        var turn = retry ?? conversation.Begin(request, answeringName, answeringId);
        recording?.Attach(turn); localRun = recording;
        if (retry != null) conversation.Retry(turn, answeringName, answeringId);
        else if (recording == null && request.ConsumesDraft(question.Text ?? string.Empty)) ClearDraft();
        configurationNotice = null;
        ShowConversation(false);
        ScrollToEnd();
        try
        {
            options.EnsureUserName(text["general.default_user_name"]);
            var execution = prepared ?? backends.Prepare(recording?.AllowFallback ?? true, request.SituationDelivery);
            IAdvisorQuerySession session = source.CreateSession(replyLanguage(), request.Text, options.Current.MaxQueryRounds, options.Current.Information, request.Context);
            turn.UpdateProgress(new AdvisorProgress(AdvisorStage.Waiting, 0, session.RoundsUsed, session.MaxRounds,
                model: execution.Model, connectionName: execution.ConnectionName));
            turn.SetUnitReferences(session is IAdvisorUnitReferenceSource references ? references.UnitReferences : AdvisorUnitReferences.Empty);
            var operationReferences = session is IAdvisorOperationReferenceSource operationSource
                ? operationSource.OperationReferences : new AdvisorOperationReferences(turn.UnitReferences);
            if (recording != null) session = recording.Observe(session);
            var operationSession = AdvisorConsultation.CreateOperations(request, source, turn.OperationId, session.Prompts, operationReferences);
            if (recording != null && operationSession != null) operationSession = recording.Observe(operationSession);
            var historyLimit = options.Current.HistoryExchanges;
            pendingTurn = turn;
            pendingConnectionName = execution.ConnectionName;
            var logDirectory = AdvisorTrace.DirectoryIn(UnityEngine.Application.persistentDataPath);
            var sharedPrompt = recording?.CommonPromptFor(options.Current.CommonPrompt) ?? options.Current.CommonPrompt;
            var userName = options.Current.UserName ?? throw new InvalidOperationException("The user name has not been initialized.");
            active = new AdvisorJob(async (cancellation, progress) =>
            {
                using var operationScope = operationSession;
                var connectionName = execution.ConnectionName;
                var context = AdvisorContext.Create(session, history, advisorProfile, sharedPrompt, update =>
                {
                    if (update.UsingFallback) connectionName = execution.SubConnectionName;
                    progress(new AdvisorProgress(update.Stage, update.ApiRequest, update.Rounds, update.MaxRounds,
                        update.Queries, update.UsingFallback, update.Model, connectionName));
                }, historyLimit, userName, operationScope, recording == null ? null : recording.CaptureInput);
                recording?.CaptureInput(context.Input);
                return await AdvisorConsultation.RunAsync(execution, context, logDirectory, request, cancellation).ConfigureAwait(false);
            }, warn);
            SetBusy(true);
            status.Text = text["status.consulting"];
        }
        catch (AdvisorOptionsException failure)
        {
            FailTurn(turn, AdvisorNotice.Options(failure));
            warn("Advisor options unavailable: " + failure.Code);
        }
        catch (AdvisorConnectionException failure)
        {
            FailTurn(turn, AdvisorNotice.Connection(failure));
            warn("Advisor consultation failed: " + failure.Message);
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
        var follow = FollowingLatest;
        turn.UpdateProgress(progress);
        transcript.UpdateProgress(turn, text);
        if (follow) ScrollToEnd();
    }

    private void CompleteActive()
    {
        var job = active;
        if (job == null || !job.Completion.IsCompleted) return;
        UpdateActiveProgress();
        var turn = pendingTurn ?? throw new InvalidOperationException("The pending turn is missing.");
        var follow = FollowingLatest;
        var readingOffset = transcriptScroll.TargetVerticalOffset;
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
            FinishProposal(turn, false);
            turn.Cancel();
            ShowConversation(false);
            status.Text = text["status.cancelled"];
        }
        else if (completion.Error != null)
        {
            FailTurn(turn, AdvisorNotice.Connection(completion.Error));
            warn("Advisor consultation failed: " + completion.Error.Message);
        }
        else
        {
            var result = completion.Result ?? throw new InvalidOperationException("The completed consultation has no result.");
            localRun?.Complete(result, turn.Progress?.ConnectionName ?? pendingConnectionName);
            turn.Finish(result, OperationService);
            ShowConversation(false);
            var connection = AdvisorProgress.FormatConnection(turn.Progress?.ConnectionName ?? pendingConnectionName,
                result.Model.Length > 0 ? result.Model : turn.Progress?.Model ?? "", text);
            status.Text = text.Format(result.Completed ? "status.completed" : "status.incomplete", connection, result.QueryRounds, result.ApiRequests,
                result.TotalTokens.HasValue ? text.Format("status.token_count", result.TotalTokens.Value) : text["status.tokens_unknown"]);
        }
        FinishRecording();
        if (!follow) pendingScroll = () => transcriptScroll.ScrollToVerticalOffset(readingOffset, false);
        else if (turn.State == AdvisorTurnState.Answered) pendingScroll = () => transcript.ScrollToAnswer(turn, transcriptScroll);
        else ScrollToEnd();
    }

    private bool FollowingLatest => transcriptScroll.TargetVerticalOffset >= transcriptScroll.ScrollableHeight - 4;
    private void ScrollToEnd() => pendingScroll = () => transcriptScroll.ScrollToVerticalOffset(transcriptScroll.ScrollableHeight, false);

    private void FailTurn(AdvisorTurn turn, AdvisorNotice error)
    {
        FinishProposal(turn, false);
        turn.Fail(error);
        FinishRecording();
        pendingTurn = null;
        ShowConversation(false);
        status.Text = text["status.failed"];
        ScrollToEnd();
    }

    private void FinishRecording() { localRun?.Finish(); localRun = null; }

    private void SetBusy(bool busy)
    {
        send.Content = text[busy ? "action.cancel" : "action.send"];
        question.IsReadOnly = busy;
        unitBadges.SetBusy(busy);
        UpdateUnitInsertion();
        UpdateInputClear();
        captureAction.IsEnabled = !busy;
        inputAction.IsEnabled = !busy;
        activity.Text = text[busy ? "activity.busy" : "activity.idle"];
        UpdateShortcuts();
        settings.UpdateBusy();
        transcript.UpdateBusy(busy);
    }

    private void CancelActive()
    {
        if (active == null) return;
        var job = active; active = null;
        var turn = pendingTurn ?? throw new InvalidOperationException("The pending turn is missing.");
        pendingTurn = null;
        job.Dispose();
        FinishProposal(turn, false);
        turn.Cancel();
        FinishRecording();
        SetBusy(false);
        ShowConversation(false);
        status.Text = text["status.cancelled"];
        ScrollToEnd();
    }

    private void RefreshConfigurationNotice()
    {
        try
        {
            options.EnsureUserName(text["general.default_user_name"]);
            _ = backends.Prepare(); _ = AdvisorPrompts.Current; configurationNotice = null;
        }
        catch (AdvisorOptionsException error) { configurationNotice = AdvisorNotice.Options(error); }
        catch (AdvisorConnectionException error) { configurationNotice = AdvisorNotice.Connection(error); }
        catch (AdvisorPromptException error) { configurationNotice = AdvisorNotice.Prompts(error); }
    }

    private void Preview(bool structured)
    {
        // Never leave a stale request on screen after a refused or failed capture.
        pendingScroll = null;
        diagnosticScrollPending = false;
        diagnosticOutput.Text = string.Empty;
        if (structured && !string.IsNullOrEmpty(UnityEngine.Input.compositionString))
        {
            diagnosticStatus.Text = text["diagnostics.ime"];
            return;
        }
        var draftQuestion = structured ? unitDraft.Prepare(question.Text ?? string.Empty).Text : string.Empty;
        if (structured && draftQuestion.Length > AdvisorConversation.MaxQuestionLength)
        {
            diagnosticStatus.Text = ValidationMessage("validation.question_too_long");
            return;
        }
        try
        {
            if (structured)
            {
                options.EnsureUserName(text["general.default_user_name"]);
                var language = replyLanguage();
                var input = source.CreateSession(language, draftQuestion,
                    options.Current.MaxQueryRounds, options.Current.Information).InitialInput();
                appliedProfile?.AddAnswerPreferences(input);
                input["common_prompt"] = options.Current.CommonPrompt;
                input["user_name"] = options.Current.UserName;
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
        catch (AdvisorOptionsException error)
        {
            diagnosticOutput.Text = AdvisorNotice.Options(error).Message(text);
            diagnosticStatus.Text = text["status.failed"];
            warn("Advisor options unavailable: " + error.Code);
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

    private void FinishProposal(AdvisorTurn turn, bool success) =>
        turn.Proposal = OperationService?.FinishProposal(turn.OperationId, success);

    private void ReviewProposal(AdvisorTurn turn, string? planId)
    {
        if (!CanConsult || turn.Proposal == null) return;
        operationReview.Show(turn.Proposal, planId);
    }

    private void ShowConversation(bool updateStatus = true, string? empty = null)
    {
        OperationService?.DiscardProposalsExcept(conversation.Turns.Select(turn => turn.OperationId));
        pendingScroll = null;
        diagnosticScrollPending = false;
        question.FontSize = AdvisorTypography.FontSize(options.Current.TextSize, 15);
        UpdateUnitInsertion();
        UpdateInputClear();
        unitNavigationHint.Visibility = navigation != null && (options.Current.LinkOwnUnits || options.Current.LinkContacts)
            ? Visibility.Visible : Visibility.Collapsed;
        transcript.Show(conversation, text, speakerName, empty ?? text["transcript.empty"], configurationNotice, options.Current.TextSize, options.Current.LinkOwnUnits, options.Current.LinkContacts);
        UpdateShortcuts();
        if (updateStatus) status.Text = text["status.ready"];
    }

    private void Clear()
    {
        questionInput.Reset();
        CancelActive();
        operationReview.Close();
        conversation.Clear();
        pendingScroll = null;
        diagnosticScrollPending = false;
        RefreshConfigurationNotice();
        ShowConversation(false, text["transcript.cleared"]);
        UpdateShortcuts();
        status.Text = text["status.new_question"];
        question.Focus();
    }


    private void ReturnToConversation()
    {
        ReleaseInput(); RefreshConfigurationNotice(); ShowConversation(false); ScrollToEnd(); question.Focus();
    }

    private void LoadProfile()
    {
        try { ApplyProfile(AdvisorProfile.Load(profileDirectory)); }
        catch (AdvisorProfileException failure) { ProfileError(failure); }
    }

    private void ApplyProfile(AdvisorProfile profile)
    {
        var next = new AdvisorPortrait(profile.ReadActivePortrait(profileDirectory, DefaultPortrait));
        ReleasePortrait(); portrait = next; portraitImage.Source = next.Source;
        portraitError.Visibility = Visibility.Collapsed; profileLoaded = true; appliedProfile = profile;
        speakerName = profile.DisplayName; advisorName.Text = profile.DisplayName;
        advisorRole.Text = profile.UseDefault ? text["advisor.role"] : profile.CustomRole;
        advisorRole.Visibility = advisorRole.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowConversation(false);
    }

    private void ReleasePortrait()
    {
        portraitImage.Source = null; var previous = portrait; portrait = null; previous?.Dispose();
    }

    private void ProfileError(AdvisorProfileException failure)
    {
        ReleasePortrait();
        if (!profileLoaded) advisorName.Text = text["speaker.advisor"];
        portraitError.Visibility = Visibility.Visible; portraitError.Text = text["portrait.settings_error"];
        warn("Advisor profile failed: " + failure.Code);
    }

    private string ValidationMessage(string code) => code == "validation.question_too_long"
        ? text.Format(code, AdvisorConversation.MaxQuestionLength) : text[code];

    private void UpdateShortcuts()
    {
        foreach (var item in shortcuts)
            item.Button.IsEnabled = item.Shortcut.IsAvailable(active != null, conversation.Exchanges.Count, options.Current.HistoryExchanges);
    }

    private string WindowTitle => text["window.title"] + " v" +
        (typeof(AdvisorWindow).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The advisor assembly version is missing.")).ToString(3);

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
        window.Header = WindowTitle;
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
