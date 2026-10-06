using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Leaf;

// Popup behaviour that only exists once a session is on screen: recovering an unchanged
// result and keeping an uncommitted draft. Runs inside ApplicationTests' WPF application.
internal static class PopupRegressionTests
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++; Console.WriteLine("PASS " + label);
    }
    private static void Click(Window window, string name)
    {
        Ui.Get<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    public static async Task<int> Run(string folder)
    {
        assertions = 0;
        await SameSourceRecovery(Path.Combine(folder, "restore"));
        await InFlightReuse(Path.Combine(folder, "inflight"));
        await LeafForegroundShortcut(Path.Combine(folder, "leaf-front"));
        await LeafForegroundWithoutResult(Path.Combine(folder, "leaf-front-empty"));
        await InvocationSnapshotPreference(Path.Combine(folder, "snapshot-preference"));
        await DraftRetention(Path.Combine(folder, "draft"));
        await StopAndRetryWord(Path.Combine(folder, "stop-word"));
        await StopAndRetryFollowup(Path.Combine(folder, "stop-chat"));
        await StopAndRetryParallel(Path.Combine(folder, "stop-both"));
        await StopAndRetryTranslation(Path.Combine(folder, "stop-translation"));
        await RetryUsesLatestProvider(Path.Combine(folder, "retry-provider"));
        await CachedRestoreKeepsWordCard(Path.Combine(folder, "cached-card"));
        await FailedFollowupRetriesOriginalQuestion(Path.Combine(folder, "failed-followup"));
        await RecoveryBarrierBlocksFollowupRetry(Path.Combine(folder, "barrier-retry"));
        await FollowupRetryWorksAfterRecovery(Path.Combine(folder, "barrier-cleared"));
        await SettingsPageRoundTrip(Path.Combine(folder, "settings-page"));
        await ConcurrentSettingsLeave(Path.Combine(folder, "settings-leave"));
        await FailedSettingsLeaveKeepsPage(Path.Combine(folder, "settings-leave-failed"));
        await ExplicitApplyIsFlushed(Path.Combine(folder, "settings-apply-flush"));
        await ExplicitApplySurvivesPageLeave(Path.Combine(folder, "settings-apply-leave"));
        await InternalSelectionReturn(Path.Combine(folder, "internal-return"));
        await ReturnAfterClearDoesNotResurrect(Path.Combine(folder, "return-cleared"));
        await LearningContentSelectable(Path.Combine(folder, "selectable"));
        await SettingsEntryUsesPopupPage(Path.Combine(folder, "settings-entry"));
        await InternalSelectionShortcutRouting(Path.Combine(folder, "internal-shortcut"));
        await InternalSelectionInSettingsIsNotRead(Path.Combine(folder, "settings-focus"));
        await ExternalShortcutLeavesSettingsPage(Path.Combine(folder, "external-settings-leave"));
        await FailedSettingsLeaveBlocksCapture(Path.Combine(folder, "settings-leave-blocks"));
        await ExitKeepsAppOnFailedSettingsLeave(Path.Combine(folder, "exit-settings-leave"));
        return assertions;
    }

    // Stopping a word lookup and retrying must repeat that lookup, not the translation.
    private static async Task StopAndRetryWord(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var translation = shell.TranslateAsync("A retry fixture sentence.", "选中文字", false);
            handler.ReplyStream(0, "夹具译文");
            await translation;
            var record = shell.Current;
            record.Chat.Add(new ChatTurn { Role = "user", Content = "older fixture", Topic = "原句" });
            record.Chat.Add(new ChatTurn { Role = "assistant", Content = "older answer", Topic = "原句" });
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            var lookup = shell.SelectWordAsync(word, false);
            Check(handler.Requests.Count == 2 && !handler.Requests[1].Cancellation.IsCancellationRequested,
                "The word lookup is in flight with its own cancellation");
            Click(shell.Popup, "CancelButton");
            Check(handler.Requests[1].Cancellation.IsCancellationRequested, "Stopping cancels the word lookup");
            Check(Ui.Get<Grid>(shell.Popup, "BusyPanel").Visibility == Visibility.Collapsed,
                "Stopping clears the word lookup's busy state");
            Click(shell.Popup, "RetryButton");
            Check(handler.Requests.Count == 3, "Retrying a stopped word lookup issues exactly one new request");
            Check(handler.Requests[2].Body.Contains("selected_text") && !handler.Requests[2].Body.Contains("source_text"),
                "Retry repeats the word lookup rather than the whole translation [request kind=" +
                (handler.Requests[2].Body.Contains("selected_text") ? "word" : "translation") + "]");
            Check(ReferenceEquals(shell.Current, record) && record.Chat.Count == 2,
                "Retrying a stopped word lookup keeps the conversation");
            // The abandoned request still answers; it must not attach itself afterwards.
            handler.ReplyJson(1, new { meaning = "过期词义", sections = new object[0] });
            await lookup;
            handler.ReplyJson(2, new { meaning = "新的词义", sections = new object[0] });
            await Until(() => record.Cards.Count > 0);
            Check(record.Cards.Count == 1 && record.Cards.Values.All(c => c.meaning == "新的词义"),
                "Only the retried word lookup lands in the conversation");
            Check(Reading(shell.Popup, "WordMeaning") == "新的词义",
                "A retried word lookup settles on its card instead of staying busy");

            // A retry stops mattering once another conversation replaces the stopped one.
            var pending = shell.SelectWordAsync(TextTools.Pieces(record.Source).Last(p => p.IsWord), false);
            Click(shell.Popup, "CancelButton");
            shell.OpenRecord(TranslationRecord.Create("Another source", "选中文字", shell.Store.Settings));
            int before = handler.Requests.Count;
            Check(Ui.Get<Border>(shell.Popup, "ErrorPanel").Visibility == Visibility.Collapsed,
                "Replacing the conversation clears the stopped state");
            Click(shell.Popup, "RetryButton");
            Check(handler.Requests.Count == before, "A retry tied to a replaced conversation does nothing");
            handler.ReplyJson(3, new { meaning = "无关词义", sections = new object[0] });
            await pending;
        }
    }

    // Stopping a follow-up keeps the conversation and repeats the original question.
    private static async Task StopAndRetryFollowup(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var translation = shell.TranslateAsync("A follow-up retry sentence.", "选中文字", false);
            handler.ReplyStream(0, "夹具译文");
            await translation;
            var record = shell.Current;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "original question";
            var chat = shell.SendChatAsync();
            Check(handler.Requests.Count == 2 && !handler.Requests[1].Cancellation.IsCancellationRequested,
                "The follow-up is in flight with its own cancellation");
            Click(shell.Popup, "CancelButton");
            Check(handler.Requests[1].Cancellation.IsCancellationRequested, "Stopping cancels the follow-up");
            Check(Ui.Get<Border>(shell.Popup, "ChatPanel").Visibility == Visibility.Collapsed,
                "Stopping a follow-up removes its transient question and answer");
            Check(record.Chat.Count == 0, "Nothing half-finished is added to the conversation");
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "a later draft";
            Click(shell.Popup, "RetryButton");
            Check(handler.Requests.Count == 3 && handler.Requests[2].Body.Contains("original question") &&
                !handler.Requests[2].Body.Contains("a later draft"),
                "Retry repeats the original question instead of what was typed afterwards");
            Check(Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "a later draft",
                "An unsent draft survives the retry");
            handler.ReplyStream(1, "迟到的回答");
            await chat;
            Check(record.Chat.Count == 0, "A late answer to a stopped follow-up is discarded");
            handler.ReplyStream(2, "重试的回答");
            await Until(() => record.Chat.Count == 2);
            Check(record.Chat.Count == 2 && record.Chat[1].Content == "重试的回答",
                "The retried follow-up lands in the existing conversation");
            Check(Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "a later draft",
                "The retried question does not clear a draft written afterwards");
            // The input is cleared only when it still holds the question that was answered.
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "second question";
            var second = shell.SendChatAsync();
            handler.ReplyStream(3, "第二个回答");
            await second;
            Check(Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text.Length == 0 && record.Chat.Count == 4,
                "A finished follow-up clears the input only when it still holds its question");
        }
    }

    // Stopping both at once offers one retry that restores both, and never a translation.
    private static async Task StopAndRetryParallel(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var translation = shell.TranslateAsync("A parallel fixture sentence.", "选中文字", false);
            handler.ReplyStream(0, "夹具译文");
            await translation;
            var record = shell.Current;
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            var lookup = shell.SelectWordAsync(word, false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "parallel question";
            var chat = shell.SendChatAsync();
            Check(handler.Requests.Count == 3, "A word lookup and a follow-up run together");
            Click(shell.Popup, "CancelButton");
            Check(handler.Requests[1].Cancellation.IsCancellationRequested && handler.Requests[2].Cancellation.IsCancellationRequested,
                "Stopping cancels both active operations");
            Click(shell.Popup, "RetryButton");
            Check(handler.Requests.Count == 5, "One retry entry restores both stopped operations");
            Check(handler.Requests.Skip(3).Count(r => r.Body.Contains("selected_text")) == 1 &&
                handler.Requests.Skip(3).Count(r => r.Body.Contains("parallel question")) == 1,
                "The retry keeps each operation's own kind");
            Check(!handler.Requests.Skip(3).Any(r => r.Body.Contains("source_text")),
                "Retrying stopped work never re-translates the whole sentence");
            handler.ReplyJson(1, new { meaning = "过期词义", sections = new object[0] });
            await lookup;
            handler.ReplyStream(2, "迟到的回答");
            await chat;
            handler.ReplyJson(3, new { meaning = "并行词义", sections = new object[0] });
            handler.ReplyStream(4, "并行回答");
            await Until(() => record.Cards.Count > 0 && record.Chat.Count == 2);
            Check(record.Cards.Values.Any(c => c.meaning == "并行词义") && record.Chat[1].Content == "并行回答",
                "Both retried operations complete in the same conversation");
        }
    }
    // Stopping a translation retries that translation and nothing else.
    private static async Task StopAndRetryTranslation(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var translation = shell.TranslateAsync("A stopped translation sentence.", "选中文字", false);
            Check(handler.Requests.Count == 1 && !handler.Requests[0].Cancellation.IsCancellationRequested,
                "The translation is in flight with its own cancellation");
            Click(shell.Popup, "CancelButton");
            Check(handler.Requests[0].Cancellation.IsCancellationRequested, "Stopping cancels the translation");
            Check(Ui.Get<Grid>(shell.Popup, "BusyPanel").Visibility == Visibility.Collapsed,
                "Stopping clears the translation's busy state");
            Click(shell.Popup, "RetryButton");
            Check(handler.Requests.Count == 2, "Retrying a stopped translation issues exactly one new request");
            Check(handler.Requests[1].Body.Contains("source_text") && !handler.Requests[1].Body.Contains("selected_text"),
                "Retry repeats the translation instead of becoming a word lookup");
            handler.ReplyStream(0, "迟到的译文");
            await translation;
            handler.ReplyStream(1, "重试的译文");
            await Until(() => shell.Current != null && shell.Current.Completed);
            Check(shell.Current.Source == "A stopped translation sentence." && shell.Current.Translation == "重试的译文",
                "The retried translation completes the same sentence");
        }
    }

    // A retry reads the configuration saved when it runs, not one captured earlier.
    private static async Task RetryUsesLatestProvider(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var translation = shell.TranslateAsync("A provider retry sentence.", "选中文字", false);
            handler.ReplyStream(0, "夹具译文");
            await translation;
            var record = shell.Current;
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            var lookup = shell.SelectWordAsync(word, false);
            Click(shell.Popup, "CancelButton");
            Check(handler.Requests[1].Cancellation.IsCancellationRequested, "Stopping cancels the word lookup");
            var moved = Json.Copy(shell.Store.Settings);
            moved.Provider.BaseUrl = "https://retry.example/v1/chat/completions";
            moved.Provider.Model = "retry-model";
            shell.Store.SaveSettings(moved);
            Click(shell.Popup, "RetryButton");
            Check(handler.Requests.Count == 3, "The retry is sent after the saved configuration changed");
            Check(handler.Requests[2].Uri.Host == "retry.example" && handler.Requests[2].Body.Contains("retry-model"),
                "A retry reads the newest saved endpoint and model instead of a captured one");
            handler.ReplyJson(1, new { meaning = "过期词义", sections = new object[0] });
            await lookup;
            handler.ReplyJson(2, new { meaning = "新词义", sections = new object[0] });
            await Until(() => record.Cards.Count > 0);
            Check(record.Cards.Values.Any(c => c.meaning == "新词义"), "The retried lookup lands in the conversation");
        }
    }

    // Recovering a cached result keeps the word card and its highlight, not only the text.
    private static async Task CachedRestoreKeepsWordCard(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A cached card sentence.", "选中文字", false);
            var record = shell.Current;
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                Ui.Get<TextBlock>(shell.Popup, "WordTitle").Text == word.Text,
                "The word card is on screen before recovery");
            Check(WordLink(shell.Popup, word.Text) != null && WordLink(shell.Popup, word.Text).Background != null,
                "The selected word is highlighted before recovery");
            shell.BeginSourceEdit(false);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Clear();
            int calls = handler.Calls;
            await shell.TranslateAsync(record.Source, "选中文字", false);
            Check(handler.Calls == calls && ReferenceEquals(shell.Current, record),
                "Recovering the same text makes no API call");
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                Ui.Get<TextBlock>(shell.Popup, "WordTitle").Text == word.Text &&
                Reading(shell.Popup, "WordMeaning") == "夹具词义",
                "Recovery keeps the word card instead of rebuilding the record");
            Check(WordLink(shell.Popup, word.Text) != null && WordLink(shell.Popup, word.Text).Background != null,
                "Recovery keeps the highlight on the selected word");
            Check(Ui.Get<TextBlock>(shell.Popup, "TopicLabel").Text.Contains(word.Text),
                "Recovery keeps the follow-up topic on the selected word");
        }
    }

    // A follow-up that failed is retried with its own question, not with a later draft.
    private static async Task FailedFollowupRetriesOriginalQuestion(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var translation = shell.TranslateAsync("A failing follow-up sentence.", "选中文字", false);
            handler.ReplyStream(0, "夹具译文");
            await translation;
            var record = shell.Current;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "original failing question";
            var chat = shell.SendChatAsync();
            handler.ReplyError(1, HttpStatusCode.InternalServerError);
            await chat;
            Check(Ui.Get<Border>(shell.Popup, "ErrorPanel").Visibility == Visibility.Visible &&
                Ui.Get<Button>(shell.Popup, "RetryButton").Visibility == Visibility.Visible,
                "A failed follow-up offers its retry entry");
            Check(record.Chat.Count == 0, "A failed follow-up adds nothing to the conversation");
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "edited afterwards";
            Click(shell.Popup, "RetryButton");
            Check(handler.Requests.Count == 3 && handler.Requests[2].Body.Contains("original failing question") &&
                !handler.Requests[2].Body.Contains("edited afterwards"),
                "Retrying a failed follow-up repeats the original question instead of the edited input");
            Check(Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "edited afterwards",
                "The edited draft survives the retry");
            handler.ReplyStream(2, "重试的回答");
            await Until(() => record.Chat.Count == 2);
            Check(record.Chat[1].Content == "重试的回答", "The retried follow-up lands in the conversation");
        }
    }

    // A retry closure captured before the barrier is irrelevant once an external rollback
    // still needs an explicit reapply; it must not start a new request.
    private static async Task RecoveryBarrierBlocksFollowupRetry(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var translation = shell.TranslateAsync("A recovery barrier sentence.", "选中文字", false);
            handler.ReplyStream(0, "夹具译文");
            await translation;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "original barrier question";
            var chat = shell.SendChatAsync();
            handler.ReplyError(1, HttpStatusCode.InternalServerError);
            await chat;
            Check(Ui.Get<Button>(shell.Popup, "RetryButton").Visibility == Visibility.Visible,
                "A failed follow-up leaves its original-question retry before the barrier");
            await ManufactureRecovery(shell.Store);
            int before = handler.Requests.Count;
            Click(shell.Popup, "RetryButton");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
            Check(handler.Requests.Count == before,
                "A follow-up retry cannot bypass the recovery barrier");
        }
    }

    // Clearing the barrier with a normal explicit reapply must not disable the retry entry
    // forever: the original question is still sent once the barrier is gone.
    private static async Task FollowupRetryWorksAfterRecovery(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var translation = shell.TranslateAsync("A recovery clears sentence.", "选中文字", false);
            handler.ReplyStream(0, "夹具译文");
            await translation;
            var record = shell.Current;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "original cleared question";
            var chat = shell.SendChatAsync();
            handler.ReplyError(1, HttpStatusCode.InternalServerError);
            await chat;
            await ManufactureRecovery(shell.Store);
            var resume = Json.Copy(shell.Store.Settings); resume.TargetLanguage = "中文";
            await shell.ApplySettingsAsync(SettingsUpdate.Preferences(resume),
                new Dictionary<string, string>(), new HashSet<string>(), true);
            Check(!shell.Store.NeedsExplicitRecovery, "An explicit reapply clears the fixture recovery barrier");
            int before = handler.Requests.Count;
            Click(shell.Popup, "RetryButton");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
            Check(handler.Requests.Count == before + 1 && handler.Requests[before].Body.Contains("original cleared question"),
                "After recovery the original-question retry still sends the original question");
            handler.ReplyStream(before, "恢复后的回答");
            await Until(() => record.Chat.Count == 2);
            Check(record.Chat[1].Content == "恢复后的回答", "The retried follow-up lands after recovery");
        }
    }

    // The settings page is a view over the live session: entering, leaving and re-entering
    // keep the conversation, its word card and the follow-up draft untouched.
    private static async Task SettingsPageRoundTrip(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A settings round-trip sentence.", "选中文字", false);
            var record = shell.Current;
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft across settings";
            shell.ShowSettingsPage();
            Check(shell.IsSettingsPageOpen &&
                Ui.Get<Grid>(shell.Popup, "TranslatePage").Visibility == Visibility.Collapsed &&
                Ui.Get<Grid>(shell.Popup, "SettingsPage").Visibility == Visibility.Visible,
                "Opening settings shows the settings page over the reading page");
            Check(Ui.Get<TextBlock>(shell.Popup, "SourceBadge").Text.Length == 0,
                "The reading badge does not show through the settings page");
            shell.ShowSettingsPage();
            Check(shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record),
                "Opening settings twice keeps one page over the live session");
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen &&
                Ui.Get<Grid>(shell.Popup, "TranslatePage").Visibility == Visibility.Visible,
                "Leaving settings returns to the reading page");
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                Ui.Get<TextBlock>(shell.Popup, "WordTitle").Text == word.Text &&
                Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "draft across settings",
                "The round trip keeps the word card, the conversation and the draft");
            Check(Ui.Get<TextBlock>(shell.Popup, "SourceBadge").Text == "来自选中文本",
                "The badge labels an internal selection as 选中文本 after leaving settings");

            // An empty preset list has its own empty state instead of a blank dropdown.
            shell.ShowSettingsPage();
            var panel = (FrameworkElement)Ui.Get<ContentControl>(shell.Popup, "SettingsPageHost").Content;
            var hint = Ui.Get<TextBlock>(panel, "PresetPlaceholder");
            Check(hint.Visibility == Visibility.Visible && hint.Text == "暂无偏好预设" &&
                !Ui.Get<ComboBox>(panel, "PresetCombo").IsEnabled,
                "An empty preset list shows its empty hint and disables the dropdown");
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen,
                "Leaving the freshly built settings page also succeeds");
        }
    }

    // Two entry points (the back button and the window close) can ask to leave the same page
    // while the first flush is still running; the second shares that attempt instead of
    // flushing the same page twice or detaching whatever page is there when it resumes.
    private static async Task ConcurrentSettingsLeave(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A shared settings leave sentence.", "选中文字", false);
            var record = shell.Current;
            shell.ShowSettingsPage();
            var panel = SettingsPanel(shell);
            Ui.Get<CheckBox>(panel, "ClipboardModeCheck").IsChecked = true;
            var back = shell.TryLeaveSettingsPageAsync();
            var close = shell.TryLeaveSettingsPageAsync();
            Check(ReferenceEquals(back, close),
                "A second leave entry reuses the attempt already running for this page");
            Check(await back && await close,
                "Both leave entries settle once instead of detaching the same page twice");
            Check(!shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record) &&
                Ui.Get<Grid>(shell.Popup, "TranslatePage").Visibility == Visibility.Visible,
                "The shared leave returns to the live reading page");
            Check(shell.Store.Settings.ClipboardMode,
                "The pending preference edit is flushed by the shared leave");
        }
    }

    // A flush that cannot apply keeps the page open with its reason, and the same entry can be
    // retried once the field is fixed.
    private static async Task FailedSettingsLeaveKeepsPage(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A failed settings leave sentence.", "选中文字", false);
            var record = shell.Current;
            shell.ShowSettingsPage();
            var panel = SettingsPanel(shell);
            Ui.Get<TextBox>(panel, "TargetInput").Text = "   ";
            Check(!await shell.TryLeaveSettingsPageAsync() && shell.IsSettingsPageOpen &&
                Ui.Get<Grid>(shell.Popup, "SettingsPage").Visibility == Visibility.Visible,
                "A failed flush keeps the settings page over the live session");
            Check(Ui.Get<TextBlock>(panel, "SettingsStatus").Text.Contains("目标语言"),
                "The failed flush keeps its reason visible instead of closing silently");
            Check(ReferenceEquals(shell.Current, record),
                "A failed flush does not touch the session behind the page");
            Ui.Get<TextBox>(panel, "TargetInput").Text = "中文";
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen,
                "Retrying the leave after fixing the field returns to the reading page");
        }
    }

    // Flushing settings must not report success while a clicked service apply is still
    // committing: the exit path shares this flush and would otherwise leave early.
    private static async Task ExplicitApplyIsFlushed(string folder)
    {
        var store = new LocalStore(folder, new DelayedWrites());
        var seed = Json.Copy(store.Settings); seed.Provider.Model = "fixture-model"; store.SaveSettings(seed);
        var credentials = new RecordingCredentials();
        using (var shell = new AppShell(store, false, new LlmClient(new FixtureHandler()), credentials)) {
            var settings = new SettingsWindow(shell);
            var providers = Ui.Get<ComboBox>(settings.Panel, "ProviderCombo");
            var deepseek = providers.Items.Cast<ProviderProfile>().First(p => p.Id == "deepseek");
            providers.SelectedItem = deepseek;
            Ui.Get<ComboBox>(settings.Panel, "ModelInput").Text = "flushed-model";
            Ui.Get<PasswordBox>(settings.Panel, "ApiKeyInput").Password = "flushed-key";
            Press(settings.Panel, "SaveSettingsButton");
            await Task.Delay(60);
            bool flushed = await settings.FlushPendingAsync();
            Check(flushed && store.Settings.Provider.Model == "flushed-model",
                "Flushing settings waits for the clicked apply instead of stopping after its first phase");
            Check(credentials.Read(deepseek) == "flushed-key",
                "The key captured by the clicked apply is saved before the flush reports success");
        }
    }

    // Leaving the popup settings page while the clicked apply is still committing waits for it,
    // so the key it captured is not dropped by the page detach.
    private static async Task ExplicitApplySurvivesPageLeave(string folder)
    {
        var store = new LocalStore(folder, new DelayedWrites());
        var seed = Json.Copy(store.Settings); seed.Provider.Model = "fixture-model"; store.SaveSettings(seed);
        var credentials = new RecordingCredentials();
        var shell = new AppShell(store, false, new LlmClient(new FixtureHandler()), credentials);
        await shell.TranslateAsync("An applied settings sentence.", "选中文字", false);
        shell.ShowSettingsPage();
        var panel = SettingsPanel(shell);
        var providers = Ui.Get<ComboBox>(panel, "ProviderCombo");
        var deepseek = providers.Items.Cast<ProviderProfile>().First(p => p.Id == "deepseek");
        providers.SelectedItem = deepseek;
        Ui.Get<ComboBox>(panel, "ModelInput").Text = "page-model";
        Ui.Get<PasswordBox>(panel, "ApiKeyInput").Password = "page-key";
        Press(panel, "SaveSettingsButton");
        await Task.Delay(60);
        Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen,
            "Leaving the page waits for the service apply it started");
        Check(credentials.Read(deepseek) == "page-key" && store.Settings.Provider.Model == "page-model",
            "The page leave keeps the key and model that the clicked apply captured");
        // The disposing shell writes its fallback exit snapshot in the background, and this
        // store's writes are slowed on purpose; wait for it so nothing outlives the scenario.
        shell.Dispose();
        try { await store.FlushAsync(); } catch { }
    }

    // Clearing history while a sub-session is on screen must not let the previous session come
    // back: the restored session keeps the epoch it was captured under and its save is skipped.
    private static async Task ReturnAfterClearDoesNotResurrect(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A previous session sentence.", "选中文字", false);
            var record = shell.Current;
            await shell.TranslateInternalSelectionAsync("A sub-session sentence.");
            Check(!ReferenceEquals(shell.Current, record), "The sub-session replaced the earlier conversation");
            await shell.Store.ClearAsync();
            shell.ReturnToPreviousSession();
            Check(ReferenceEquals(shell.Current, record) && !shell.CanReturnToPreviousSession,
                "The earlier conversation still returns from memory without another request");
            await shell.SaveCurrentAsync();
            await shell.Store.FlushAsync();
            Check(shell.Store.History("").Count == 0,
                "A session captured before the clear cannot be written back under the newer epoch");
        }
    }

    // Selecting text inside Leaf starts its own conversation, and the light back entry
    // returns to the previous one without any request.
    private static async Task InternalSelectionReturn(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A return fixture sentence.", "选中文字", false);
            var record = shell.Current;
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "return draft";
            await shell.TranslateInternalSelectionAsync("Words picked inside Leaf itself.");
            Check(!ReferenceEquals(shell.Current, record) && shell.Current.Source == "Words picked inside Leaf itself.",
                "An internal selection starts its own conversation");
            Check(Ui.Get<TextBlock>(shell.Popup, "SourceBadge").Text == "来自选中文本",
                "The badge labels an internal selection as 选中文本");
            Check(shell.CanReturnToPreviousSession &&
                Ui.Get<Button>(shell.Popup, "BackToPreviousButton").Visibility == Visibility.Visible,
                "The replaced conversation offers its return entry");
            shell.ReturnToPreviousSession();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReferenceEquals(shell.Current, record) && handler.Calls == 3,
                "Returning to the previous session restores it without any API call");
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                Ui.Get<TextBlock>(shell.Popup, "WordTitle").Text == word.Text,
                "The return restores the previous session's word card");
            Check(Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "return draft",
                "The return restores the previous session's follow-up draft");
            Check(!shell.CanReturnToPreviousSession &&
                Ui.Get<Button>(shell.Popup, "BackToPreviousButton").Visibility == Visibility.Collapsed,
                "The return entry hides once the previous session is back");
            await shell.TranslateInternalSelectionAsync("   ");
            Check(Ui.Get<Border>(shell.Popup, "ErrorPanel").Visibility == Visibility.Visible &&
                ReferenceEquals(shell.Current, record),
                "A blank internal selection shows its validation error and keeps the session");
        }
    }

    // Learning content is read-only selectable text, marked for the selection source.
    private static async Task LearningContentSelectable(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A selectable learning sentence.", "选中文字", false);
            var record = shell.Current;
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            var meaning = Ui.Get<RichTextBox>(shell.Popup, "WordMeaning");
            Check(Reading(shell.Popup, "WordMeaning") == "夹具词义",
                "The word meaning renders in the selectable reading control");
            Check(meaning.IsReadOnly && (string)meaning.Tag == "LeafSelectableText",
                "The word meaning is read-only and marked for selection");
            var sections = Ui.Get<StackPanel>(shell.Popup, "LearningSections").Children.OfType<RichTextBox>().ToList();
            Check(sections.Count > 0 && sections.All(b => b.IsReadOnly && (string)b.Tag == "LeafSelectableText"),
                "Every learning section is a read-only selectable reading control");
            Check((string)Ui.Get<RichTextBox>(shell.Popup, "TranslationText").Tag == "LeafSelectableText" &&
                (string)Ui.Get<RichTextBox>(shell.Popup, "SourceText").Tag == "LeafSelectableText",
                "The translation and the original are marked for selection too");
        }
    }

    // The tray, first-run and error entries all reach the settings page the shell already hosts:
    // one window, one page over the live session, and no focus taken from what the user read.
    private static async Task SettingsEntryUsesPopupPage(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A settings entry sentence.", "选中文字", false);
            var record = shell.Current;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft across the settings entry";
            int windows = Application.Current.Windows.Count;
            shell.OpenSettings();
            Check(shell.IsSettingsPageOpen && Ui.Get<ContentControl>(shell.Popup, "SettingsPageHost").Content != null,
                "The settings entry opens the popup's own settings page");
            Check(Application.Current.Windows.Count == windows,
                "The settings entry opens no second window");
            Check(!shell.Popup.ShowActivated && !shell.Pinned && ReferenceEquals(shell.Current, record) &&
                Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "draft across the settings entry",
                "Opening settings stays passive and keeps the session and its draft");
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen &&
                Ui.Get<Grid>(shell.Popup, "TranslatePage").Visibility == Visibility.Visible,
                "The page the entry opened can be left back to the reading page");
        }
    }

    // A selection the user makes inside Leaf is read from the page itself: the real reader rules
    // decide which control answers, and the shortcut never asks the desktop, simulates a copy or
    // touches the clipboard. Every reading control the user can select in is covered, and each
    // one starts its own conversation while the previous session stays reachable.
    private static async Task InternalSelectionShortcutRouting(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Target.IsLeaf = true;
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            shell.Selection = new SelectionAcquirer(probe);
            await shell.TranslateAsync("A shortcut routing sentence.", "选中文字", false);
            var record = shell.Current;
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft kept across internal selections";

            // Each case is the state the page really is in when that control can be selected:
            // the word card is open first, the read-only original and the editor follow the
            // restore of the session before them, and the translation stays on screen in all
            // of them.
            var cases = new List<ReadingSelection> {
                new ReadingSelection { Label = "a learning control", Expected = "learning section text", Select = () =>
                    SelectRange(Ui.Get<StackPanel>(shell.Popup, "LearningSections").Children.OfType<RichTextBox>().First(), "learning section text") },
                new ReadingSelection { Label = "the read-only original", Expected = "read-only original text",
                    Select = () => SelectRange(Ui.Get<RichTextBox>(shell.Popup, "SourceText"), "read-only original text") },
                new ReadingSelection { Label = "the original editor", Expected = "editor", Select = () => {
                    // The editor is closed on the restored reading page; it has to be open, as it
                    // is while the user is typing in it, for its selection to be the one on screen.
                    shell.BeginSourceEdit(false);
                    var box = Ui.Get<TextBox>(shell.Popup, "SourceInput");
                    box.Text = "editor selection text"; box.Select(0, 6); return box;
                } },
                new ReadingSelection { Label = "the translation", Expected = "translated selection",
                    Select = () => SelectRange(Ui.Get<RichTextBox>(shell.Popup, "TranslationText"), "translated selection") }
            };
            foreach (var item in cases) {
                var control = item.Select();
                shell.InternalSelection = new FocusedPageReader((FrameworkElement)shell.Popup.Content, () => control);
                var read = shell.InternalSelection.Read();
                Check(read.Status == InternalSelectionStatus.Text && read.Text == item.Expected && read.Source.Length > 0,
                    "The reader takes the selection made in " + item.Label + " (was " + read.Status + "/" + read.Reason +
                    " source \"" + read.Source + "\" text \"" + read.Text + "\")");
                int calls = handler.Calls;
                await shell.InvokeShortcutAsync();
                Check(shell.Current.Source == item.Expected && !ReferenceEquals(shell.Current, record) && handler.Calls == calls + 1,
                    "A selection in " + item.Label + " starts its own conversation with exactly one request (source \"" +
                    shell.Current.Source + "\", calls " + handler.Calls + " of " + (calls + 1) + ")");
                Check(shell.Current.SourceKind == "选中文字",
                    "A selection in " + item.Label + " is labeled as selected text");
                Check(probe.AutomationCalls == 0 && probe.CopyCalls == 0 && probe.ClipboardCalls == 0,
                    "A selection in " + item.Label + " never reads the desktop, simulates a copy or touches the clipboard");
                Check(shell.CanReturnToPreviousSession &&
                    Ui.Get<Button>(shell.Popup, "BackToPreviousButton").Visibility == Visibility.Visible,
                    "A selection in " + item.Label + " keeps the previous session reachable");
                shell.ReturnToPreviousSession();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(ReferenceEquals(shell.Current, record) && handler.Calls == calls + 1 &&
                    Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "draft kept across internal selections",
                    "Returning after a selection in " + item.Label + " restores the session, its draft and makes no request");
            }
        }
    }

    // The settings page is not a selection source: a real selected settings field stays a
    // settings value, the page and the session behind it survive, and nothing is read outside.
    private static async Task InternalSelectionInSettingsIsNotRead(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Target.IsLeaf = true;
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            shell.Selection = new SelectionAcquirer(probe);
            await shell.TranslateAsync("A settings focus sentence.", "选中文字", false);
            var record = shell.Current;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft behind the settings page";
            shell.ShowSettingsPage();
            var panel = SettingsPanel(shell);
            var target = Ui.Get<TextBox>(panel, "TargetInput");
            target.Text = "a settings value"; target.SelectAll();
            var reader = new FocusedPageReader((FrameworkElement)shell.Popup.Content, () => target);
            shell.InternalSelection = reader;
            var read = reader.Read();
            Check(read.Status == InternalSelectionStatus.Unavailable && read.Reason == InternalSelectionReason.SensitiveControl &&
                read.Text.Length == 0,
                "The reader refuses a selected settings field even though it is a selectable control on screen");
            int calls = handler.Calls;
            await shell.InvokeShortcutAsync();
            Check(shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record) && handler.Calls == calls,
                "A shortcut with the focus in settings never starts a session from a settings value");
            Check(probe.AutomationCalls == 0 && probe.CopyCalls == 0 && probe.ClipboardCalls == 0,
                "The settings page is never scanned as an external selection either");
            Check(target.Text == "a settings value" &&
                Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "draft behind the settings page",
                "The settings field and the draft behind the page are untouched");
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen &&
                shell.Store.Settings.TargetLanguage == "a settings value",
                "Leaving the page afterwards applies the edit it was holding");
        }
    }

    // An external capture shows the reading page, so it leaves an open settings page first: the
    // pending preference is applied, the capture still uses the old pipeline, and the session
    // keeps its draft instead of losing it to a page that was never saved.
    private static async Task ExternalShortcutLeavesSettingsPage(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Automation = SelectionCaptureResult.Success("external capture text");
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            shell.Selection = new SelectionAcquirer(probe);
            await shell.TranslateAsync("An external capture sentence.", "选中文字", false);
            var record = shell.Current;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft kept through the external capture";
            shell.ShowSettingsPage();
            var panel = SettingsPanel(shell);
            Ui.Get<TextBox>(panel, "TargetInput").Text = "英语";
            Check(shell.Store.Settings.TargetLanguage != "英语", "The settings edit is still pending before the shortcut");
            await shell.InvokeShortcutAsync();
            Check(!shell.IsSettingsPageOpen && Ui.Get<Grid>(shell.Popup, "TranslatePage").Visibility == Visibility.Visible,
                "An external shortcut leaves the settings page and shows the reading page");
            Check(shell.Store.Settings.TargetLanguage == "英语",
                "Leaving the page for a capture applies the pending preference first");
            Check(probe.AutomationCalls == 1 && probe.ClipboardCalls == 0 && handler.Calls == 2,
                "The external capture still goes through the selection pipeline once");
            Check(shell.Current.Source == "external capture text" && !ReferenceEquals(shell.Current, record) &&
                record.Draft == "draft kept through the external capture",
                "The external capture starts its own conversation while the previous draft survives");
        }
    }

    // A settings page that cannot be saved is not left: the capture never happens, the page keeps
    // its reason and its pending edit, and the session stays where it was.
    private static async Task FailedSettingsLeaveBlocksCapture(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Automation = SelectionCaptureResult.Success("capture that must not happen");
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            shell.Selection = new SelectionAcquirer(probe);
            await shell.TranslateAsync("A blocked capture sentence.", "选中文字", false);
            var record = shell.Current;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft kept through the blocked capture";
            shell.ShowSettingsPage();
            var panel = SettingsPanel(shell);
            Ui.Get<TextBox>(panel, "TargetInput").Text = "   ";
            int calls = handler.Calls;
            await shell.InvokeShortcutAsync();
            Check(shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record) && handler.Calls == calls,
                "A settings page that cannot be saved keeps the page and never starts the capture");
            Check(probe.AutomationCalls == 0 && probe.CopyCalls == 0 && probe.ClipboardCalls == 0,
                "The blocked capture never reads the desktop");
            Check(Ui.Get<TextBlock>(panel, "SettingsStatus").Text.Contains("目标语言") &&
                Ui.Get<TextBox>(panel, "TargetInput").Text == "   ",
                "The kept page shows its reason and still holds the pending edit");
            // The other entries that switch away from the page are gated the same way: with a
            // fresh edit that cannot be applied, neither the history window nor another record
            // opens, and the page keeps its reason. Each entry needs its own pending edit: a
            // leave with nothing pending has nothing to fail on.
            int windows = Application.Current.Windows.Count;
            var other = TranslationRecord.Create("A record that must not open", "选中文字", shell.Store.Settings);
            Ui.Get<TextBox>(panel, "TargetInput").Text = "    ";
            shell.OpenHistory();
            await Settle();
            Check(shell.IsSettingsPageOpen && Application.Current.Windows.Count == windows && ReferenceEquals(shell.Current, record),
                "A settings page that cannot be saved blocks the history entry too");
            Ui.Get<TextBox>(panel, "TargetInput").Text = "     ";
            shell.OpenRecord(other);
            await Settle();
            Check(shell.IsSettingsPageOpen && !ReferenceEquals(shell.Current, other),
                "A settings page that cannot be saved blocks opening a record behind it");
            Ui.Get<TextBox>(panel, "TargetInput").Text = "中文";
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record),
                "Fixing the field lets the same page be left afterwards");
        }
    }

    // The exit waits for the popup's settings page: a page that cannot be saved keeps the app
    // alive with the page and the session, and the retried exit leaves once the field is fixed.
    private static async Task ExitKeepsAppOnFailedSettingsLeave(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("An exit leave sentence.", "选中文字", false);
            var record = shell.Current;
            string target = shell.Store.Settings.TargetLanguage;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft kept through the failed exit";
            shell.ShowSettingsPage();
            var panel = SettingsPanel(shell);
            Ui.Get<TextBox>(panel, "TargetInput").Text = "   ";
            await shell.ExitAsync();
            Check(!shell.IsExiting && shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record) &&
                shell.Store.Settings.TargetLanguage == target,
                "An exit whose settings page cannot be saved stays alive with the page and the session");
            Check(Ui.Get<TextBlock>(panel, "SettingsStatus").Text.Contains("目标语言") &&
                Ui.Get<Button>(shell.Popup, "RetryButton").Visibility == Visibility.Visible,
                "The failed exit leaves its reason visible and offers a real retry");
            Check(Ui.Get<TextBox>(panel, "TargetInput").Text == "   " && record.Draft == "draft kept through the failed exit",
                "The failed exit loses neither the pending edit nor the draft");
            Ui.Get<TextBox>(panel, "TargetInput").Text = "中文";
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen,
                "The page can be left once the field is fixed after the failed exit");
        }
    }

    // Files are rolled back but an applied external effect could not be, which is exactly the
    // state that requires an explicit reapply before requests may run again.
    private static async Task ManufactureRecovery(LocalStore store)
    {
        bool failed = false;
        try {
            var next = Json.Copy(store.Settings); next.TargetLanguage = "日本語";
            await store.CommitSettingsAsync(SettingsUpdate.Preferences(next),
                (committed, candidate) => { throw new Exception("fixture external apply failure"); }, null);
        } catch (UserError) { failed = true; }
        Check(failed && store.NeedsExplicitRecovery, "The fixture leaves an explicit recovery barrier");
    }

    // Learning content and the translation render in read-only RichTextBoxes now.
    private static string Reading(Window popup, string name)
    {
        var box = Ui.Get<RichTextBox>(popup, name);
        return new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.Trim();
    }

    private static System.Windows.Documents.Hyperlink WordLink(Window popup, string text)
    {
        foreach (var block in Ui.Get<RichTextBox>(popup, "SourceText").Document.Blocks.OfType<Paragraph>())
            foreach (var inline in block.Inlines.OfType<System.Windows.Documents.Hyperlink>())
                if (new TextRange(inline.ContentStart, inline.ContentEnd).Text == text) return inline;
        return null;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        if (!condition()) throw new Exception("Timed out waiting for the fixture to settle.");
    }

    // Lets a path that was started without awaiting it finish, so a check about what it must not
    // do is decided after it had the chance to do it.
    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(60);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    // Clearing the editor is a draft action, not a request to forget the result.
    private static async Task SameSourceRecovery(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A fixture sentence.", "选中文字", false);
            var record = shell.Current;
            Check(record.Completed && record.Translation == "夹具译文", "The fixture translation is on screen before recovery");
            record.Chat.Add(new ChatTurn { Role = "user", Content = "older fixture", Topic = "原句" });
            record.Chat.Add(new ChatTurn { Role = "assistant", Content = "older answer", Topic = "原句" });
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "unsent question";
            shell.BeginSourceEdit(false);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Clear();
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible, "Clearing the draft leaves the editor open");
            int calls = handler.Calls;
            await shell.TranslateAsync(record.Source, "选中文字", false);
            Check(ReferenceEquals(shell.Current, record) && handler.Calls == calls,
                "Same-source recovery keeps the record and makes no API call");
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "Same-source recovery returns to the reading page");
            Check(Ui.Get<RichTextBox>(shell.Popup, "SourceText").Visibility == Visibility.Visible &&
                Ui.Get<Button>(shell.Popup, "EditSourceButton").Visibility == Visibility.Visible,
                "Recovered reading page shows the clickable original and its edit entry");
            Check(Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "unsent question" && record.Chat.Count == 2,
                "Recovery keeps the conversation and the follow-up draft");
            Check(Ui.Get<Border>(shell.Popup, "ChatPanel").Visibility == Visibility.Visible &&
                Ui.Get<TextBox>(shell.Popup, "SourceInput").Text == record.Source,
                "Recovery restores the conversation view and refills the original text");
            var translation = Ui.Get<RichTextBox>(shell.Popup, "TranslationText");
            Check(new TextRange(translation.Document.ContentStart, translation.Document.ContentEnd).Text.Trim() == "夹具译文",
                "Recovery keeps the existing translation without redrawing it away");
        }
    }

    // The same text while it is still translating restores the progress view.
    private static async Task InFlightReuse(string folder)
    {
        var handler = new FixtureHandler { Holding = true };
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var first = shell.TranslateAsync("An in-flight sentence.", "选中文字", false);
            await Task.Delay(30);
            var record = shell.Current;
            try {
                await shell.TranslateAsync("An in-flight sentence.", "选中文字", false);
                Check(handler.Calls == 1 && ReferenceEquals(shell.Current, record),
                    "Repeating the same text while it translates reuses the request");
                Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                    "Reusing an in-flight request keeps the reading view");
            } finally { handler.Release(); }
            await first;
            Check(record.Completed && record.Translation == "夹具译文", "The reused in-flight request still completes");
        }
    }

    // Leaf itself in front: an untouched or cleared editor recovers the result, a real draft stays.
    private static async Task LeafForegroundShortcut(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Target.IsLeaf = true;
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            shell.Selection = new SelectionAcquirer(probe);
            await shell.TranslateAsync("A leaf foreground sentence.", "选中文字", false);
            var record = shell.Current;
            shell.BeginSourceEdit(false);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Clear();
            await shell.InvokeShortcutAsync();
            Check(ReferenceEquals(shell.Current, record) && Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "A shortcut while Leaf is in front recovers the result after a cleared draft");
            Check(probe.AutomationCalls == 0 && probe.CopyCalls == 0 && handler.Calls == 1,
                "Recovering in front of Leaf never re-reads a selection or calls the API");

            // A real, uncommitted draft is not what the shortcut asked to replace.
            shell.BeginSourceEdit(false);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Text = "a different uncommitted draft";
            await shell.InvokeShortcutAsync();
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible &&
                Ui.Get<TextBox>(shell.Popup, "SourceInput").Text == "a different uncommitted draft",
                "A non-empty different draft stays in the editor");
            Check(ReferenceEquals(shell.Current, record), "Keeping a draft does not discard the conversation");
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Clear();
            await shell.InvokeShortcutAsync();
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "Clearing that draft lets the next shortcut recover the reading page again");

            // Clipboard mode is never skipped by the in-front branch.
            var clipboard = Json.Copy(shell.Store.Settings); clipboard.ClipboardMode = true; shell.Store.SaveSettings(clipboard);
            probe.Clipboard = SelectionCaptureResult.Success("fresh clipboard fixture");
            await shell.InvokeShortcutAsync();
            Check(probe.ClipboardCalls == 1 && probe.AutomationCalls == 0 && shell.Current.Source == "fresh clipboard fixture",
                "Clipboard mode in front of Leaf still reads this invocation's clipboard");
            Check(!ReferenceEquals(shell.Current, record), "Clipboard mode starts its own conversation instead of recovering");
        }
    }

    // Leaf itself in front with nothing to restore: the shortcut must still prepare the
    // manual input the user asked for instead of doing nothing at all.
    private static async Task LeafForegroundWithoutResult(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Target.IsLeaf = true;
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(new FixtureHandler()))) {
            shell.Selection = new SelectionAcquirer(probe);
            Check(shell.Current == null, "A fresh shell has no result to recover");
            // A reading page without a result has its editor collapsed.
            Ui.Visible(Ui.Get<RichTextBox>(shell.Popup, "SourceText"), true);
            Ui.Visible(Ui.Get<Grid>(shell.Popup, "SourceEditor"), false);
            await shell.InvokeShortcutAsync();
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible &&
                Ui.Get<TextBox>(shell.Popup, "SourceInput").Text.Length == 0,
                "A shortcut in front of Leaf with no result prepares the empty manual input");
            Check(probe.AutomationCalls == 0 && probe.CopyCalls == 0 && probe.ClipboardCalls == 0,
                "Preparing manual input in front of Leaf reads no selection at all");

            // The same shortcut keeps whatever the user has already typed.
            Ui.Visible(Ui.Get<Grid>(shell.Popup, "SourceEditor"), true);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Text = "an uncommitted draft";
            await shell.InvokeShortcutAsync();
            Check(Ui.Get<TextBox>(shell.Popup, "SourceInput").Text == "an uncommitted draft" &&
                shell.Current == null,
                "A shortcut with no result never discards an uncommitted draft");

            // The direct-input preference decides whether that prepared editor takes focus.
            var direct = Json.Copy(shell.Store.Settings); direct.FocusInputOnShortcut = true; shell.Store.SaveSettings(direct);
            Ui.Visible(Ui.Get<RichTextBox>(shell.Popup, "SourceText"), true);
            Ui.Visible(Ui.Get<Grid>(shell.Popup, "SourceEditor"), false);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Clear();
            await shell.InvokeShortcutAsync();
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible &&
                Ui.Get<TextBox>(shell.Popup, "SourceInput").SelectedText.Length == 0,
                "The direct-input preference still prepares the editor when there is no result");
        }
    }

    // A capture applies the preferences it snapshotted when it started, not the ones that
    // happen to be saved by the time it returns.
    private static async Task InvocationSnapshotPreference(string folder)
    {
        var probe = new FakeSelectionProbe();
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            shell.Selection = new SelectionAcquirer(probe);
            await shell.TranslateAsync("A snapshot preference sentence.", "选中文字", false);
            var record = shell.Current;
            var quiet = Json.Copy(shell.Store.Settings); quiet.FocusInputOnShortcut = false; shell.Store.SaveSettings(quiet);
            probe.AutomationPending = new TaskCompletionSource<SelectionCaptureResult>();
            var pending = shell.InvokeShortcutAsync();
            // The user changes the preference while the shortcut is still acquiring.
            var changed = Json.Copy(shell.Store.Settings); changed.FocusInputOnShortcut = true; shell.Store.SaveSettings(changed);
            probe.AutomationPending.TrySetResult(SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern));
            await pending;
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible &&
                Ui.Get<TextBox>(shell.Popup, "SourceInput").SelectedText.Length == 0,
                "A capture uses the direct-input preference it snapshotted, not one changed while it ran");
            Check(ReferenceEquals(shell.Current, record),
                "Preparing input for an inconclusive capture keeps the conversation");
            Check(handler.Calls == 1, "An inconclusive capture adds no API call");
        }
    }

    // A draft is not a session; explicit submission still starts a new one.
    private static async Task DraftRetention(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A submitted sentence.", "选中文字", false);
            var record = shell.Current; string previousId = record.Id;
            shell.BeginSourceEdit(false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft kept through resubmission";
            await shell.SubmitSourceAsync();
            Check(shell.Current.Id != previousId && shell.Current.SourceKind == "输入" && shell.Current.Chat.Count == 0,
                "An explicit submission still starts a new conversation with the same text");
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "Submitting returns to the reading page");
        }

        // The direct-input preference refills and selects the recovered original without calling again.
        var directProbe = new FakeSelectionProbe();
        directProbe.Target.IsLeaf = true;
        var directHandler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(Path.Combine(folder, "direct")), false, new LlmClient(directHandler))) {
            shell.Selection = new SelectionAcquirer(directProbe);
            var settings = Json.Copy(shell.Store.Settings); settings.FocusInputOnShortcut = true; shell.Store.SaveSettings(settings);
            await shell.TranslateAsync("A direct input sentence.", "选中文字", false);
            var record = shell.Current;
            await shell.InvokeShortcutAsync();
            var input = Ui.Get<TextBox>(shell.Popup, "SourceInput");
            Check(ReferenceEquals(shell.Current, record) && directHandler.Calls == 1,
                "The direct-input preference recovers the existing result without a new request");
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible &&
                input.Text == record.Source && input.SelectedText == record.Source,
                "The direct-input preference refills the whole original and selects it for replacement");
        }
    }

    // The production read rules with an explicit focus and the popup's own content as the page
    // root, so the shortcut's routing can be checked against real popup controls. Those two are
    // the parts a headless check cannot produce: the host window is never shown, and its window
    // needs a real keyboard focus. Which control may answer is still decided by the same reader:
    // the control chain, the visible page, the sensitive names, the allowed sources and the
    // control's own selection.
    private sealed class FocusedPageReader : IInternalSelectionReader
    {
        private readonly InternalSelectionReader reader;
        private readonly Func<object> focus;
        public FocusedPageReader(FrameworkElement page, Func<object> focus)
        {
            reader = new InternalSelectionReader(page); this.focus = focus;
        }
        public InternalSelectionResult Read() { return reader.ReadFrom(focus()); }
    }

    // One real reading control, the selection a user would make in it, and the text the shortcut
    // must therefore translate.
    private sealed class ReadingSelection
    {
        public string Label;
        public string Expected;
        public Func<FrameworkElement> Select;
    }

    // Selects the whole content of a real reading control, as dragging over it would.
    private static RichTextBox SelectRange(RichTextBox box, string text)
    {
        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph(new Run(text)));
        box.Selection.Select(box.Document.ContentStart, box.Document.ContentEnd);
        return box;
    }

    private static LocalStore ConfiguredStore(string folder)
    {
        var store = new LocalStore(folder); var settings = Json.Copy(store.Settings);
        settings.Provider.Model = "fixture-model"; store.SaveSettings(settings);
        return store;
    }
    private static FrameworkElement SettingsPanel(AppShell shell)
    {
        return (FrameworkElement)Ui.Get<ContentControl>(shell.Popup, "SettingsPageHost").Content;
    }
    private static void Press(FrameworkElement scope, string name)
    {
        Ui.Get<Button>(scope, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    // Writes pause, so a test can observe a settings commit while it is still in flight.
    private sealed class DelayedWrites : IStoreFiles
    {
        private const int DelayMs = 300;
        private readonly IStoreFiles inner = new PhysicalStoreFiles();
        public bool Exists(string path) { return inner.Exists(path); }
        public long Length(string path) { return inner.Length(path); }
        public string Read(string path) { return inner.Read(path); }
        public void Write(string path, string payload) { Thread.Sleep(DelayMs); inner.Write(path, payload); }
        public void Copy(string source, string destination, bool overwrite) { inner.Copy(source, destination, overwrite); }
        public void Move(string source, string destination) { inner.Move(source, destination); }
        public void Replace(string source, string destination) { inner.Replace(source, destination); }
        public void Delete(string path) { inner.Delete(path); }
    }
    private sealed class RecordingCredentials : ICredentialProfiles
    {
        private readonly Dictionary<string, string> keys = new Dictionary<string, string>();
        public string Read(ProviderProfile profile)
        {
            string key; string id = Credentials.ScopedId(profile);
            return keys.TryGetValue(id, out key) ? key : "";
        }
        public void Save(ProviderProfile profile, string key) { keys[Credentials.ScopedId(profile)] = key; }
        public void Delete(ProviderProfile profile) { keys.Remove(Credentials.ScopedId(profile)); }
    }
    private static HttpResponseMessage StreamReply(string text)
    {
        string payload = "data: " + Json.Write(new { choices = new[] {
            new { delta = new { content = text } }
        } }) + "\n\ndata: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }
    private static HttpResponseMessage JsonReply(object card)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(Json.Write(new { choices = new[] {
                new { message = new { content = Json.Write(card) }, finish_reason = "stop" }
            } }), Encoding.UTF8, "application/json")
        };
    }
    private sealed class PendingResponse
    {
        public Uri Uri; public string Body; public CancellationToken Cancellation;
        public readonly TaskCompletionSource<HttpResponseMessage> Completion =
            new TaskCompletionSource<HttpResponseMessage>();
    }
    // Keeps every answer open so a stopped request can be answered late on purpose.
    private sealed class ControlledHandler : HttpMessageHandler
    {
        public readonly List<PendingResponse> Requests = new List<PendingResponse>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var pending = new PendingResponse {
                Uri = request.RequestUri,
                Body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                Cancellation = cancellation
            };
            Requests.Add(pending); return pending.Completion.Task;
        }
        public void ReplyStream(int index, string text) { Requests[index].Completion.TrySetResult(StreamReply(text)); }
        public void ReplyJson(int index, object card) { Requests[index].Completion.TrySetResult(JsonReply(card)); }
        public void ReplyError(int index, HttpStatusCode status)
        {
            Requests[index].Completion.TrySetResult(new HttpResponseMessage(status) {
                Content = new StringContent("{\"error\":{\"message\":\"fixture failure\"}}", Encoding.UTF8, "application/json")
            });
        }
    }
    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int Calls;
        public bool Holding;
        private readonly TaskCompletionSource<bool> gate = new TaskCompletionSource<bool>();
        public void Release() { gate.TrySetResult(true); }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Calls++;
            if (!Holding) return Task.FromResult(Reply(request));
            return Held(request);
        }
        private async Task<HttpResponseMessage> Held(HttpRequestMessage request)
        {
            await gate.Task; return Reply(request);
        }
        private static HttpResponseMessage Reply(HttpRequestMessage request)
        {
            var body = (Dictionary<string, object>)Json.Read(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            bool stream = body.ContainsKey("stream") && Convert.ToBoolean(body["stream"]);
            return stream ? StreamReply("夹具译文") : JsonReply(new {
                lemma = "fixture", part_of_speech = "noun", meaning = "夹具词义",
                target_phrase = "夹具", sections = new[] {
                    new { title = "词形", content = "仅供隔离测试" }
                }
            });
        }
    }
}
