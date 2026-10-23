using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Noesis;

namespace SeaPowerAIAdvisor;

// Navigation owns no global draft. Each page saves through its own persistence boundary.
internal sealed class AdvisorSettingsController
{
    private enum Category { General, Profile, Templates, Api, Consultation, Information, Developer, Wip, Commands }
    private readonly FrameworkElement content, pane, home, page, chat, confirmation;
    private readonly FrameworkElement[] pages;
    private readonly Func<AdvisorText> text;
    private readonly AdvisorOptionsStore options;
    private readonly AdvisorApiSettingsStore apiStore;
    private readonly AdvisorApiSettingsView api;
    private readonly AdvisorProfileSettingsView profiles;
    private readonly AdvisorTemplateSettingsView templates;
    private readonly AdvisorCommandSettingsView commands;
    private readonly AdvisorInformationView information;
    private readonly AdvisorLocalConnectionsView localConnections;
    private readonly AdvisorPromptSettingsView promptEditor;
    private readonly AdvisorErrorHistoryView errorHistory;
    private readonly Action<Action> guard;
    private readonly Action releaseInput, changed, back;
    private readonly Func<bool> consultationBusy;
    private readonly Action<string> warn;
    private readonly TextBox rounds, history, prompt, userName;
    private readonly ComboBox size;
    private readonly CheckBox campaign, log, localApi, linkOwnUnits, linkContacts, unitOperations;
    private readonly TextBlock status, title, testStatus, testError, confirmationText;
    private readonly Button test, accept, cancelConfirmation, throwException;
    private Category? current;
    private string baseline = "", testingName = "";
    private Action? confirmed;
    private AdvisorJob? testing;
    private bool available;
    private readonly Stopwatch savedNotice = new();

    internal AdvisorSettingsController(FrameworkElement content, Func<AdvisorText> text, AdvisorOptionsStore options,
        string profileDirectory, string settingsPath, AdvisorTextTemplateStore templateStore, Func<byte[]> defaultPortrait, Action<Action> guard, TypingScope typing,
        Action releaseInput, Func<bool> consultationBusy, Action<AdvisorProfile> profileSaved, Action changed,
        Action back, Action<string> warn, Func<AdvisorOperations?> operations)
    {
        this.content = content; this.text = text; this.options = options; this.guard = guard;
        this.releaseInput = releaseInput; this.consultationBusy = consultationBusy; this.changed = changed; this.back = back; this.warn = warn;
        pane = Named<FrameworkElement>("SettingsPane"); home = Named<FrameworkElement>("SettingsHome");
        page = Named<FrameworkElement>("SettingsCategoryPage"); chat = Named<FrameworkElement>("ConversationPane");
        confirmation = Named<FrameworkElement>("SettingsConfirmation"); confirmationText = Named<TextBlock>("SettingsConfirmationText");
        accept = Named<Button>("AcceptConfirmation"); cancelConfirmation = Named<Button>("CancelConfirmation");
        status = Named<TextBlock>("SettingsStatus"); title = Named<TextBlock>("SettingsCategoryTitle");
        rounds = Named<TextBox>("MaxQueryRounds"); history = Named<TextBox>("HistoryExchanges"); prompt = Named<TextBox>("CommonPrompt");
        prompt.MaxLength = AdvisorOptions.MaxCommonPromptLength;
        userName = Named<TextBox>("UserName"); userName.MaxLength = AdvisorOptions.MaxUserNameLength;
        size = Named<ComboBox>("ConversationTextSize"); campaign = Named<CheckBox>("EnableCampaignAdvisor"); log = Named<CheckBox>("LogAiJson");
        localApi = Named<CheckBox>("EnableLocalApi");
        unitOperations = Named<CheckBox>("EnableUnitOperations");
        linkOwnUnits = Named<CheckBox>("LinkOwnUnits"); linkContacts = Named<CheckBox>("LinkContacts");
        localConnections = new AdvisorLocalConnectionsView(Named<StackPanel>("LocalApiConnections"), Named<ComboBox>("LocalApiDefaultConnection"), Named<CheckBox>("AllowLocalApiModelOverride"), text, guard);
        information = new AdvisorInformationView(Named<StackPanel>("InformationChoices"), text(), guard);
        throwException = Named<Button>("ThrowTestException");
        test = Named<Button>("TestConnection"); testStatus = Named<TextBlock>("ConnectionTestStatus"); testError = Named<TextBlock>("ConnectionTestError");
        promptEditor = new AdvisorPromptSettingsView(content, text, guard, typing, changed, warn);
        errorHistory = new AdvisorErrorHistoryView(content, text, guard, typing);
        commands = new AdvisorCommandSettingsView(content, operations, text, typing, guard);
        apiStore = new AdvisorApiSettingsStore(settingsPath, () => text()["connection.configuration_name_pattern"]);
        api = new AdvisorApiSettingsView(content, apiStore, text, guard, typing, ApiChanged, Saved, () => Notice(text()["profile.ime"], true), ApiError, ConfirmDelete, ConfirmDuplicate);
        profiles = new AdvisorProfileSettingsView(content, profileDirectory, text, defaultPortrait, guard, typing,
            profile => { profileSaved(profile); Saved(); }, () => Notice(""), ProfileError, ConfirmDelete, warn);
        templates = new AdvisorTemplateSettingsView(content, templateStore, text, guard, typing,
            Saved, () => Notice(""), () => Notice(text()["profile.ime"], true), TemplateError, ConfirmDelete);
        pages = new[] { "GeneralCategory", "ProfileCategory", "TemplatesCategory", "ApiCategory", "ConsultationCategory", "InformationCategory", "DeveloperCategory", "WipCategory", "CommandsCategory" }
            .Select(Named<FrameworkElement>).ToArray();
        var buttons = new[] { "OpenGeneralCategory", "OpenProfileCategory", "OpenTemplatesCategory", "OpenApiCategory", "OpenConsultationCategory", "OpenInformationCategory", "OpenDeveloperCategory", "OpenWipCategory", "OpenCommandsCategory" };
        for (var i = 0; i < buttons.Length; i++) { var category = (Category)i; Bind(buttons[i], () => OpenCategory(category)); }
        Bind("BackToSettings", BackToHome); Bind("BackToConversation", () => { Close(); back(); });
        Bind("SaveGeneral", () => Save(() => options.SaveGeneral((AdvisorTextSize)size.SelectedIndex, userName.Text ?? "", linkOwnUnits.IsChecked == true, linkContacts.IsChecked == true)));
        Bind("SaveConsultation", () => Save(() => options.SaveConsultation(Number(rounds, "query_rounds_invalid"), Number(history, "history_exchanges_invalid"), prompt.Text ?? "")));
        Bind("ResetInformation", () => { if (available) { information.Reset(AdvisorInformation.Default); Notice(""); } });
        Bind("SaveInformation", () => Save(() => options.SaveInformation(information.Value)));
        Bind("SaveCommands", () => Save(() => options.SaveUnitOperations(unitOperations.IsChecked == true)));
        Bind("SaveWip", () => Save(() => options.SaveCampaign(campaign.IsChecked == true)));
        void SaveDeveloper() => Save(() => options.SaveDeveloper(localApi.IsChecked == true,
            () => apiStore.SaveLogging(log.IsChecked == true), localConnections.Value));
        Bind("SaveDeveloper", SaveDeveloper); Bind("SaveLocalApi", SaveDeveloper);
        Bind("TestConnection", TestConnection);
        Bind("ThrowTestException", () =>
        {
            if (testing == null && !consultationBusy()) AdvisorRuntime.ThrowTestException();
        });
        Bind("CancelConfirmation", HideConfirmation);
        Bind("AcceptConfirmation", () => { var action = confirmed; HideConfirmation(); action?.Invoke(); });
        foreach (var input in new[] { rounds, history, prompt, userName, Named<TextBox>("ProfilePath") })
        { input.GotKeyboardFocus += (_, _) => guard(() => typing.Acquire(input)); input.LostKeyboardFocus += (_, _) => guard(() => typing.Release(input)); }
        Named<TextBox>("ProfilePath").Text = AdvisorProfile.SettingsPath(profileDirectory);
    }
    private T Named<T>(string name) where T : class => content.FindName(name) as T ?? throw new InvalidOperationException("Missing settings control: " + name);
    private void Bind(string name, Action action) => Named<Button>(name).Click += (_, _) => guard(action);
    internal void Open()
    {
        Close(); releaseInput(); chat.Visibility = Visibility.Collapsed; pane.Visibility = Visibility.Visible;
        home.Visibility = Visibility.Visible; page.Visibility = Visibility.Collapsed; Named<Button>("OpenGeneralCategory").Focus();
    }
    internal void Close()
    {
        CancelTest(); HideConfirmation(); profiles.Close(); api.Close(); promptEditor.Close(); errorHistory.Close(); templates.Close(); commands.Close(); current = null; available = false;
        Notice(""); pane.Visibility = Visibility.Collapsed; chat.Visibility = Visibility.Visible;
    }
    private void OpenCategory(Category category)
    {
        releaseInput(); current = category; available = false; Notice(""); ClearTest();
        home.Visibility = Visibility.Collapsed; page.Visibility = Visibility.Visible;
        for (var i = 0; i < pages.Length; i++) { pages[i].Visibility = i == (int)category ? Visibility.Visible : Visibility.Collapsed; pages[i].IsEnabled = true; }
        title.Text = text()[new[] { "general.tab", "profile.tab", "templates.category", "settings.api_category", "ai.consultation", "information.tab", "diagnostics.heading", "wip.category", "commands.category" }[(int)category]];
        try
        {
            if (category == Category.Profile) { Named<TabControl>("ProfileCategory").SelectedIndex = 0; profiles.Open(); profiles.Focus(); }
            else if (category == Category.Templates) { templates.Open(); templates.Focus(); }
            else if (category == Category.Api)
            {
                Named<TabControl>("ApiCategory").SelectedIndex = 0;
                Named<TabItem>("ApiUseTab").IsEnabled = Named<TabItem>("ApiEditTab").IsEnabled = true;
                api.Open(); api.Focus();
            }
            else if (category == Category.Commands) { unitOperations.IsChecked = options.Read().UnitOperationsEnabled; commands.Open(); commands.Focus(); }
            else if (category == Category.Wip) { campaign.IsChecked = options.Read().CampaignEnabled; campaign.Focus(); }
            else if (category == Category.Developer)
            {
                Named<TabControl>("DeveloperCategory").SelectedIndex = 0;
                var developer = options.Read(); var connections = apiStore.Read();
                log.IsChecked = connections.LogAiJson; localApi.IsChecked = developer.DeveloperApiEnabled;
                localConnections.Open(developer.LocalConnections, connections); promptEditor.Open(); log.Focus();
            }
            else
            {
                var saved = options.Read();
                if (category == Category.General) { userName.Text = saved.UserName ?? text()["general.default_user_name"]; size.SelectedIndex = (int)saved.TextSize; linkOwnUnits.IsChecked = saved.LinkOwnUnits; linkContacts.IsChecked = saved.LinkContacts; userName.Focus(); }
                if (category == Category.Consultation) { rounds.Text = saved.MaxQueryRounds.ToString(CultureInfo.InvariantCulture); history.Text = saved.HistoryExchanges.ToString(CultureInfo.InvariantCulture); prompt.Text = saved.CommonPrompt; prompt.Focus(); }
                if (category == Category.Information) { information.Reset(saved.Information); information.Focus(); }
            }
            available = true; baseline = Snapshot(); UpdateBusy();
        }
        catch (AdvisorOptionsException error) { OptionsError(error); DisablePage(); }
        catch (AdvisorConnectionException error) { ApiError(error); api.Close(); DisablePage(); }
        catch (AdvisorProfileException error) { ProfileError(error); profiles.Close(); DisablePage(); }
        catch (AdvisorTemplateException error) { TemplateError(error); templates.Close(); DisablePage(); }
    }
    private void DisablePage()
    {
        if (current == Category.Api)
        {
            // Error history must remain reachable when the saved connection settings are unreadable.
            Named<TabItem>("ApiUseTab").IsEnabled = Named<TabItem>("ApiEditTab").IsEnabled = false;
            Named<TabControl>("ApiCategory").SelectedIndex = 2;
        }
        else if (current.HasValue) pages[(int)current.Value].IsEnabled = false;
        Named<Button>("BackToSettings").Focus();
    }
    private string Snapshot() => current switch
    {
        Category.General => userName.Text + "\0" + size.SelectedIndex + ":" + linkOwnUnits.IsChecked + ":" + linkContacts.IsChecked,
        Category.Consultation => rounds.Text + "\0" + history.Text + "\0" + prompt.Text,
        Category.Information => information.Value.Key,
        Category.Commands => unitOperations.IsChecked.ToString(),
        Category.Wip => campaign.IsChecked.ToString(),
        Category.Developer => log.IsChecked + ":" + localApi.IsChecked + ":" + localConnections.Value.Json().ToString(),
        _ => ""
    };
    private bool HasChanges => available && (current switch
    {
        Category.Profile => profiles.HasChanges,
        Category.Api => api.HasChanges,
        Category.Templates => templates.HasChanges,
        Category.Developer => promptEditor.HasChanges || Snapshot() != baseline,
        _ => Snapshot() != baseline
    });
    private void BackToHome()
    {
        void Go() { CancelTest(); profiles.Close(); api.Close(); promptEditor.Close(); errorHistory.Close(); templates.Close(); commands.Close(); current = null; available = false; releaseInput(); page.Visibility = Visibility.Collapsed; home.Visibility = Visibility.Visible; Named<Button>("OpenGeneralCategory").Focus(); }
        if (HasChanges) Confirm(text()["settings.discard_question"], text()["settings.discard_action"], Go, "settings.continue_editing"); else Go();
    }
    private void ConfirmDuplicate(Action action) => Confirm(text()["connection.duplicate_discard_question"], text()["settings.duplicate"], action, "settings.continue_editing");
    private void ConfirmDelete(string name, Action action) => Confirm(text().Format("settings.delete_question", name), text()["profile.remove"], action);
    private void Confirm(string message, string caption, Action action, string cancelKey = "settings.cancel")
    {
        releaseInput(); confirmed = action; confirmationText.Text = message; accept.Content = caption;
        cancelConfirmation.Content = text()[cancelKey];
        pane.IsEnabled = false; confirmation.Visibility = Visibility.Visible; cancelConfirmation.Focus();
    }
    private void HideConfirmation() { confirmed = null; confirmation.Visibility = Visibility.Collapsed; pane.IsEnabled = true; }
    private void Save(Action action)
    {
        if (!available) return;
        if (QuestionInputController.IsComposing) { Notice(text()["profile.ime"], true); return; }
        try { action(); baseline = Snapshot(); Saved(); }
        catch (AdvisorOptionsException error) { OptionsError(error); }
        catch (AdvisorConnectionException error) { ApiError(error); }
    }
    private static int Number(TextBox input, string code) => int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : throw new AdvisorOptionsException(code);
    private void Saved()
    {
        changed(); Notice(text()["settings.saved"]); status.Opacity = 0; savedNotice.Restart();
    }
    private void Notice(string message, bool error = false)
    {
        savedNotice.Reset(); status.Opacity = 1;
        status.Text = message; status.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        status.Foreground = (Brush)content.FindResource(error ? "AdvisorErrorBrush" : "AdvisorNoticeBrush");
    }
    private void ApiError(AdvisorConnectionException error)
    {
        var key = "settings.error." + error.Code;
        Notice(text().Contains(key) ? text()[key] + text().Format("settings.error_suffix", error.Code) : AdvisorNotice.Connection(error).Message(text()), true);
        warn("Advisor API settings failed: " + error.Code);
    }
    private void ProfileError(AdvisorProfileException error)
    {
        var key = text().Contains("profile.error." + error.Code) ? error.Code : "profile_invalid";
        Notice(text()["profile.error." + key] + text().Format("profile.error_suffix", error.Code), true); warn("Advisor profile settings failed: " + error.Code);
    }
    private void TemplateError(AdvisorTemplateException error)
    {
        Notice(text()["settings.error." + error.Code] + text().Format("settings.error_suffix", error.Code), true);
        warn("Advisor text templates failed: " + error.Code);
    }
    private void OptionsError(AdvisorOptionsException error) { Notice(text()["settings.error." + error.Code] + text().Format("settings.error_suffix", error.Code), true); warn("Advisor options failed: " + error.Code); }
    private void ApiChanged() { if (!api.Editing) CancelTest(); ClearTest(); Notice(""); UpdateBusy(); }
    internal void UpdateBusy()
    {
        api.SetBusy(testing != null); test.IsEnabled = api.Editing && !consultationBusy();
        throwException.IsEnabled = testing == null && !consultationBusy();
        test.Content = text()[testing != null ? "action.cancel" : "connection.test"];
    }
    private void TestConnection()
    {
        if (testing != null) { CancelTest(); return; }
        if (!api.Editing || consultationBusy()) return;
        Notice(""); testingName = api.TestName;
        if (QuestionInputController.IsComposing) { TestError(text()["profile.ime"]); return; }
        try
        {
            var configuration = api.TestConfiguration(); var connectionOptions = api.TestOptions();
            var requestText = AdvisorPrompts.ConnectionTestRequest(); ClearTest();
            var directory = AdvisorTrace.DirectoryIn(UnityEngine.Application.persistentDataPath);
            testing = new AdvisorJob(cancellation => AdvisorServices.TestAsync(directory, configuration, connectionOptions, requestText, cancellation), warn);
            testStatus.Text = TestMessage(text()["connection.test_running"]); UpdateBusy();
        }
        catch (AdvisorConnectionException error) { TestError(AdvisorNotice.Connection(error).Message(text(), true)); }
        catch (AdvisorPromptException error) { TestError(AdvisorNotice.Prompts(error).Message(text())); warn("Connection test prompt unavailable: " + error.Message); }
    }
    private string TestMessage(string message) => text().Format("connection.test_result", testingName, message);
    private void ClearTest() { if (testing != null) return; testStatus.Text = testError.Text = ""; testError.Visibility = Visibility.Collapsed; }
    private void TestError(string message) { testStatus.Text = ""; testError.Text = TestMessage(message); testError.Visibility = Visibility.Visible; }
    private void CancelTest()
    {
        if (testing == null) return;
        testing.Dispose(); testing = null; UpdateBusy(); testStatus.Text = TestMessage(text()["connection.test_cancelled"]);
    }
    internal void Tick()
    {
        profiles.Tick();
        Named<TextBlock>("LocalApiStatus").Text = AdvisorLocalApiDriver.Status;
        errorHistory.Tick(current == Category.Api && Named<TabControl>("ApiCategory").SelectedIndex == 2);
        if (savedNotice.IsRunning)
        {
            var elapsed = savedNotice.Elapsed.TotalSeconds;
            if (elapsed >= 3) Notice("");
            else status.Opacity = (float)Math.Min(1, elapsed / 0.2);
        }
        var job = testing; if (job == null || !job.Completion.IsCompleted) return;
        testing = null; var result = job.TakeCompleted(); UpdateBusy();
        if (result.UnexpectedError != null) { TestError(AdvisorNotice.Unexpected().Message(text())); throw result.UnexpectedError; }
        if (result.IsCancelled) testStatus.Text = TestMessage(text()["connection.test_cancelled"]);
        else if (result.Error != null) { TestError(AdvisorNotice.Connection(result.Error).Message(text(), true)); warn("Advisor connection test failed: " + result.Error.Code); }
        else { _ = result.Result ?? throw new InvalidOperationException("The connection test has no result."); testStatus.Text = TestMessage(text()["connection.test_succeeded"]); }
    }
}
