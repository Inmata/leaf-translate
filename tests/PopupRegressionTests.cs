using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
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
        await WordCardRefreshKeepsSelection(Path.Combine(folder, "card-refresh"));
        await InteractiveResizeHoldsReadingPage(Path.Combine(folder, "interactive-resize"));
        await ExpandEntryRestoresAfterCollapse(Path.Combine(folder, "expand-entry"));
        await FailedFollowupRetriesOriginalQuestion(Path.Combine(folder, "failed-followup"));
        await RecoveryBarrierBlocksFollowupRetry(Path.Combine(folder, "barrier-retry"));
        await FollowupRetryWorksAfterRecovery(Path.Combine(folder, "barrier-cleared"));
        await SettingsPageRoundTrip(Path.Combine(folder, "settings-page"));
        await ConcurrentSettingsLeave(Path.Combine(folder, "settings-leave"));
        await FailedSettingsLeaveKeepsPage(Path.Combine(folder, "settings-leave-failed"));
        await ExplicitApplyIsFlushed(Path.Combine(folder, "settings-apply-flush"));
        await ExplicitApplySurvivesPageLeave(Path.Combine(folder, "settings-apply-leave"));
        await InternalSelectionReturn(Path.Combine(folder, "internal-return"));
        await InternalChildChain(Path.Combine(folder, "internal-chain"));
        await RootRetryReanchorsAfterReturn(Path.Combine(folder, "root-retry-reanchor"));
        await HighlightPendingClearsOnDetach(Path.Combine(folder, "highlight-detach"));
        await HighlightBandsUnderRealLayout();
        await HighlightSuspendsDuringInteractiveResize();
        await TranslationDisplayDropsMarkers(Path.Combine(folder, "marker-display"));
        SourceOffsetMapsStayTotalAndMonotone();
        EmbeddedPaperFont();
        await SameSourceInternalSelectionReuse(Path.Combine(folder, "internal-reuse"));
        await RootEntriesClearAnchor(Path.Combine(folder, "root-entries"));
        await LateChildCannotTouchRoot(Path.Combine(folder, "late-child"));
        await ReturnAfterClearDoesNotResurrect(Path.Combine(folder, "return-cleared"));
        await OpenRecordDuringClearDoesNotResurrect(Path.Combine(folder, "open-record-cleared"));
        await LearningContentSelectable(Path.Combine(folder, "selectable"));
        await SettingsEntryUsesPopupPage(Path.Combine(folder, "settings-entry"));
        await InternalSelectionShortcutRouting(Path.Combine(folder, "internal-shortcut"));
        await InternalSelectionInSettingsIsNotRead(Path.Combine(folder, "settings-focus"));
        await ShortcutRecordingEndsWhenHostDeactivates(Path.Combine(folder, "shortcut-deactivated"));
        await ExternalShortcutLeavesSettingsPage(Path.Combine(folder, "external-settings-leave"));
        await FailedSettingsLeaveBlocksCapture(Path.Combine(folder, "settings-leave-blocks"));
        await InconclusiveCaptureKeepsSettingsPage(Path.Combine(folder, "inconclusive-settings"));
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
            Check(Reading(shell.Popup, "WordCard").Contains("新的词义"),
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
                CardHeadword(shell.Popup) == word.Text,
                "The word card is on screen before recovery");
            Check(shell.WordHighlighted,
                "The selected word is highlighted before recovery");
            shell.BeginSourceEdit(false);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Clear();
            int calls = handler.Calls;
            await shell.TranslateAsync(record.Source, "选中文字", false);
            Check(handler.Calls == calls && ReferenceEquals(shell.Current, record),
                "Recovering the same text makes no API call");
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                CardHeadword(shell.Popup) == word.Text &&
                Reading(shell.Popup, "WordCard").Contains("夹具词义"),
                "Recovery keeps the word card instead of rebuilding the record");
            Check(shell.WordHighlighted,
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
            Check(Ui.Get<TextBlock>(shell.Popup, "SourceBadge").Text == "选中文本",
                "The reading badge keeps its source label over the settings page");
            shell.ShowSettingsPage();
            Check(shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record),
                "Opening settings twice keeps one page over the live session");
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen &&
                Ui.Get<Grid>(shell.Popup, "TranslatePage").Visibility == Visibility.Visible,
                "Leaving settings returns to the reading page");
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                CardHeadword(shell.Popup) == word.Text &&
                Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "draft across settings",
                "The round trip keeps the word card, the conversation and the draft");
            Check(Ui.Get<TextBlock>(shell.Popup, "SourceBadge").Text == "选中文本",
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
    // retried once the field is fixed. That entry is the top bar's real toggle, so the click
    // that opened the page is also the click that leaves it through this exact flush.
    private static async Task FailedSettingsLeaveKeepsPage(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A failed settings leave sentence.", "选中文字", false);
            var record = shell.Current;
            Click(shell.Popup, "SettingsButton");
            Check(shell.IsSettingsPageOpen &&
                Ui.Get<Grid>(shell.Popup, "SettingsPage").Visibility == Visibility.Visible,
                "The top-bar entry opens the settings page over the live session");
            var panel = SettingsPanel(shell);
            Ui.Get<TextBox>(panel, "TargetInput").Text = "   ";
            // The toggle leaves on a second click instead of only ever opening; its flush is
            // started without awaiting, so the fixture settles it before checking.
            Click(shell.Popup, "SettingsButton");
            await Settle();
            Check(shell.IsSettingsPageOpen &&
                Ui.Get<Grid>(shell.Popup, "SettingsPage").Visibility == Visibility.Visible,
                "A failed flush keeps the settings page over the live session");
            Check(Ui.Get<TextBlock>(panel, "SettingsStatus").Text.Contains("目标语言"),
                "The failed flush keeps its reason visible instead of closing silently");
            Check(ReferenceEquals(shell.Current, record),
                "A failed flush does not touch the session behind the page");
            Ui.Get<TextBox>(panel, "TargetInput").Text = "中文";
            Click(shell.Popup, "SettingsButton");
            await Until(() => !shell.IsSettingsPageOpen);
            Check(Ui.Get<Grid>(shell.Popup, "TranslatePage").Visibility == Visibility.Visible,
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

    // Deeper internal selections stay anchored to the first root: A→B→C returns to A,
    // never to the intermediate B, and leaving the root again keeps that anchor.
    private static async Task InternalChildChain(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("The root conversation.", "选中文字", false);
            var root = shell.Current;
            await shell.TranslateInternalSelectionAsync("The first child conversation.");
            var firstChild = shell.Current;
            await shell.TranslateInternalSelectionAsync("The second child conversation.");
            Check(!ReferenceEquals(shell.Current, firstChild) && !ReferenceEquals(shell.Current, root),
                "A deeper internal selection opens yet another conversation");
            Check(shell.CanReturnToPreviousSession &&
                Ui.Get<Button>(shell.Popup, "BackToPreviousButton").Visibility == Visibility.Visible,
                "The deeper child still offers the return entry");
            shell.ReturnToPreviousSession();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReferenceEquals(shell.Current, root) && handler.Calls == 3,
                "Returning from the deeper child goes back to the root, never to the first child");
            await shell.TranslateInternalSelectionAsync("A fresh child after the return.");
            shell.ReturnToPreviousSession();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReferenceEquals(shell.Current, root),
                "Leaving the root again stays anchored to the same root");
        }
    }

    // Returning to a root and then retrying it replaces that root: the stale return entry
    // dies with the old record, and a child opened over the retried root comes back to it.
    // Regenerating shares the same inherit path, so the retry covers both entries.
    private static async Task RootRetryReanchorsAfterReturn(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var firstRoot = shell.TranslateAsync("The root that will be retried.", "选中文字", false);
            handler.ReplyError(0, HttpStatusCode.InternalServerError);
            await firstRoot;
            var root = shell.Current;
            Check(Ui.Get<Button>(shell.Popup, "RetryButton").Visibility == Visibility.Visible,
                "The failed root offers its retry entry");
            var child = shell.TranslateInternalSelectionAsync("A child over the failed root.");
            handler.ReplyStream(1, "child 译文");
            await child;
            Check(shell.CanReturnToPreviousSession, "The child over the failed root keeps the return entry");
            shell.ReturnToPreviousSession();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReferenceEquals(shell.Current, root) &&
                Ui.Get<Button>(shell.Popup, "RetryButton").Visibility == Visibility.Visible,
                "Returning to the failed root restores its error and its retry entry");
            Click(shell.Popup, "RetryButton");
            handler.ReplyStream(2, "retried 译文");
            await Until(() => shell.Current != null && shell.Current.Completed);
            var retriedRoot = shell.Current;
            Check(!ReferenceEquals(retriedRoot, root) && !shell.CanReturnToPreviousSession &&
                Ui.Get<Button>(shell.Popup, "BackToPreviousButton").Visibility == Visibility.Collapsed,
                "Retrying the root after returning replaces it and drops the stale return entry");
            var deeper = shell.TranslateInternalSelectionAsync("A child over the retried root.");
            handler.ReplyStream(3, "child 译文");
            await deeper;
            Check(shell.CanReturnToPreviousSession &&
                Ui.Get<Button>(shell.Popup, "BackToPreviousButton").Visibility == Visibility.Visible,
                "A child opened over the retried root anchors to it");
            shell.ReturnToPreviousSession();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReferenceEquals(shell.Current, retriedRoot) && !ReferenceEquals(shell.Current, root),
                "Returning from that child comes back to the retried root, never the replaced one");
        }
    }

    // The pending highlight is state the reading box paints from, so every detach path
    // clears it: a collapsed word card and a refilled source must leave nothing behind.
    private static async Task HighlightPendingClearsOnDetach(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A highlight detach sentence.", "选中文字", false);
            var word = TextTools.Pieces(shell.Current.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            Check(shell.WordHighlighted, "Selecting a word records its highlight");
            Click(shell.Popup, "BackToSentence");
            Check(!shell.WordHighlighted, "Collapsing the word card detaches the pending highlight");
            await shell.SelectWordAsync(word, false);
            Check(shell.WordHighlighted, "The same word can be explained again after collapsing");
            shell.OpenRecord(TranslationRecord.Create("Another source for the detach check.", "选中文字", shell.Store.Settings));
            Check(!shell.WordHighlighted, "Filling a new source detaches the previous highlight");
        }
    }

    // Reuse the existing hidden host to give the TextBoxes valid line geometry. Inspect the
    // queued state, not pixels or a detached visual's drawing: clearing before the deferred
    // callback must leave the layer empty, while a subsequent selection must work again.
    private static async Task HighlightBandsUnderRealLayout()
    {
        var parameters = new HwndSourceParameters("highlight-band-check");
        parameters.SetPosition(-32000, -32000); parameters.SetSize(340, 260);
        parameters.WindowStyle = unchecked((int)0x80000000); // WS_POPUP: never shown
        using (var host = new HwndSource(parameters)) {
            var canvas = new Canvas();
            var single = new TextBox { Text = "alpha beta gamma", Width = 300 };
            var multiline = new TextBox { Width = 300 };
            multiline.Text = "one\ntwo\nthree";
            Canvas.SetTop(multiline, 40);
            canvas.Children.Add(single); canvas.Children.Add(multiline);
            host.RootVisual = canvas;
            canvas.UpdateLayout();
            Check(single.LineCount == 1 && multiline.LineCount == 3,
                "The hidden host gives the real TextBox lines to highlight");
            int beta = single.Text.IndexOf("beta");
            var singleLayer = new SourceHighlightLayer();
            singleLayer.Bind(single);
            singleLayer.Show(beta, beta + 4);
            int last = multiline.Text.IndexOf("three");
            var multilineLayer = new SourceHighlightLayer();
            multilineLayer.Bind(multiline);
            multilineLayer.Show(last, last + 5);
            var bandsField = typeof(SourceHighlightLayer).GetField("bands", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var queuedField = typeof(SourceHighlightLayer).GetField("recomputeQueued", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(((System.Collections.ICollection)bandsField.GetValue(singleLayer)).Count > 0 &&
                ((System.Collections.ICollection)bandsField.GetValue(multilineLayer)).Count > 0,
                "Deferred highlights compute nonempty geometry under valid layout");
            singleLayer.Show(beta, beta + 4); multilineLayer.Show(last, last + 5);
            Check((bool)queuedField.GetValue(singleLayer) && (bool)queuedField.GetValue(multilineLayer),
                "The clear regression has pending highlight work before clearing");
            singleLayer.Clear(); multilineLayer.Clear();
            canvas.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(((System.Collections.ICollection)bandsField.GetValue(singleLayer)).Count == 0 &&
                ((System.Collections.ICollection)bandsField.GetValue(multilineLayer)).Count == 0 &&
                !(bool)queuedField.GetValue(singleLayer) && !(bool)queuedField.GetValue(multilineLayer),
                "Queued work finishes without restoring a cleared highlight");
            singleLayer.Show(beta, beta + 4);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(((System.Collections.ICollection)bandsField.GetValue(singleLayer)).Count > 0,
                "A new selection computes its highlight after a clear");
        }
    }

    // The same hidden host drives the pause a hand-dragged window edge puts the layer in: the
    // drawing is hidden, no geometry is asked for, and the range that is current when the drag
    // ends is the one that comes back. A clear during the pause owns the state, and a popup
    // that goes away drops the pass it had queued instead of painting it.
    private static async Task HighlightSuspendsDuringInteractiveResize()
    {
        var parameters = new HwndSourceParameters("highlight-suspend-check");
        parameters.SetPosition(-32000, -32000); parameters.SetSize(340, 260);
        parameters.WindowStyle = unchecked((int)0x80000000); // WS_POPUP: never shown
        using (var host = new HwndSource(parameters)) {
            var canvas = new Canvas();
            var box = new TextBox { Text = "alpha beta gamma", Width = 300 };
            canvas.Children.Add(box);
            host.RootVisual = canvas;
            canvas.UpdateLayout();
            var layer = new SourceHighlightLayer();
            layer.Bind(box);
            int beta = box.Text.IndexOf("beta");
            int gamma = box.Text.IndexOf("gamma");
            layer.Show(beta, beta + 4);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(HighlightBands(layer) > 0, "The highlight is drawn before the window edge is dragged");
            PauseHighlight(layer, "Suspend");
            Check(HighlightBands(layer) == 0, "A dragged edge hides the drawing");
            layer.Show(gamma, gamma + 5);
            Check(!HighlightQueued(layer), "A suspended layer queues no geometry pass of its own");
            canvas.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(HighlightBands(layer) == 0 && !HighlightQueued(layer),
                "Size, layout and highlight ticks compute no geometry while the drag runs");
            PauseHighlight(layer, "Resume");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(HighlightBands(layer) > 0 && !HighlightSuspended(layer),
                "The ended drag refreshes the highlight from the range that is current");
            PauseHighlight(layer, "Suspend");
            layer.Show(beta, beta + 4);
            layer.Clear();
            PauseHighlight(layer, "Resume");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(HighlightBands(layer) == 0,
                "A highlight cleared while the drag ran is not revived when it ends");
            layer.Show(beta, beta + 4);
            PauseHighlight(layer, "Suspend");
            layer.Show(gamma, gamma + 5);
            PauseHighlight(layer, "CancelPending");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(HighlightBands(layer) == 0 && !HighlightQueued(layer) && !HighlightSuspended(layer),
                "A popup hidden mid-drag drops its queued pass instead of repainting");
            layer.Show(beta, beta + 4);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(HighlightBands(layer) > 0, "The next selection draws again after that drop");
            layer.Show(gamma, gamma + 5);
            Check(HighlightQueued(layer), "The stop regression starts with pending work");
            PauseHighlight(layer, "Stop");
            layer.Show(beta, beta + 4);
            box.Width = 280;
            canvas.UpdateLayout();
            Check(!HighlightQueued(layer), "A stopped layer refuses subsequent selection and size requests");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(HighlightBands(layer) == 0 && !HighlightQueued(layer),
                "Stopping discards the pending callback without restoring a highlight");
        }
    }

    private const int WmSizing = 0x0214;
    private const int WmExitSizeMove = 0x0232;
    // The layer's state, read where a person could not see it: the drawing is inspected
    // through its own bands rather than through pixels.
    private static int HighlightBands(SourceHighlightLayer layer)
    {
        return ((System.Collections.ICollection)HighlightField("bands").GetValue(layer)).Count;
    }
    private static bool HighlightQueued(SourceHighlightLayer layer)
    {
        return (bool)HighlightField("recomputeQueued").GetValue(layer);
    }
    private static bool HighlightSuspended(SourceHighlightLayer layer)
    {
        return (bool)HighlightField("suspended").GetValue(layer);
    }
    private static FieldInfo HighlightField(string name)
    {
        return typeof(SourceHighlightLayer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
    private static void PauseHighlight(SourceHighlightLayer layer, string name)
    {
        typeof(SourceHighlightLayer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(layer, null);
    }
    // The real message entry, driven the way the system drives it. The answer is whether the
    // handler swallowed the message: a sizing message that was absorbed would be a real
    // regression, so the check reads the same flag the window procedure would.
    private static bool SendMessage(AppShell shell, int message)
    {
        var messages = typeof(AppShell).GetMethod("Messages", BindingFlags.Instance | BindingFlags.NonPublic);
        object[] arguments = { IntPtr.Zero, message, IntPtr.Zero, IntPtr.Zero, false };
        messages.Invoke(shell, arguments);
        return (bool)arguments[4];
    }
    private static SourceHighlightLayer HighlightLayerOf(AppShell shell)
    {
        return (SourceHighlightLayer)typeof(AppShell)
            .GetField("highlightLayer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
    }

    // A hand-dragged window edge, followed through the popup's own message entry: the reading
    // page holds the scale the drag started from - the card keeps its document, its selection
    // and its ladder - and the size the drag ends at is applied once, in place. Moving the
    // window raises only the end of the modal loop and must leave the page alone.
    private static async Task InteractiveResizeHoldsReadingPage(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A drag resize sentence.", "选中文字", false);
            var word = TextTools.Pieces(shell.Current.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            var source = Ui.Get<TextBox>(shell.Popup, "SourceText");
            var card = Ui.Get<RichTextBox>(shell.Popup, "WordCard");
            var document = card.Document;
            var entry = document.Blocks.OfType<Paragraph>().First();
            card.Selection.Select(document.ContentStart, document.ContentEnd);
            shell.Popup.Width = 456; shell.Popup.Height = 620; shell.UpdatePopupTypography();
            Check(Math.Abs(source.FontSize - 19) < 0.01 && Math.Abs(entry.FontSize - 22) < 0.01,
                "The full-size window lays the reading page out at its full ladder");

            Check(!SendMessage(shell, WmSizing), "A sizing message is observed without being swallowed");
            Check(HighlightSuspended(HighlightLayerOf(shell)), "Starting the drag pauses the highlight layer");
            shell.Popup.Width = 380; shell.Popup.Height = 420; shell.UpdatePopupTypography();
            Check(Math.Abs(source.FontSize - 19) < 0.01 && Math.Abs(Ui.Get<RichTextBox>(shell.Popup, "TranslationText").FontSize - 22) < 0.01,
                "The drag keeps the scale the reading page started from");
            Check(ReferenceEquals(card.Document, document) && !card.Selection.IsEmpty &&
                Math.Abs(entry.FontSize - 22) < 0.01 && Math.Abs(document.FontSize - 16) < 0.01,
                "The drag keeps the card's document, the reader's selection and the card's own ladder");

            Check(!SendMessage(shell, WmExitSizeMove), "The end of the drag is observed without being swallowed");
            double small = AppShell.TypographyScale(380, 420);
            Check(!HighlightSuspended(HighlightLayerOf(shell)), "The ended drag releases the highlight layer");
            Check(Math.Abs(source.FontSize - Math.Max(13, 19 * small)) < 0.01 &&
                Math.Abs(Ui.Get<RichTextBox>(shell.Popup, "TranslationText").FontSize - Math.Max(14, 22 * small)) < 0.01 &&
                Math.Abs(entry.FontSize - Math.Max(14, 22 * small)) < 0.01 &&
                ReferenceEquals(card.Document, document) && !card.Selection.IsEmpty,
                "The size the drag ended at is applied once, to the same card document");

            // A move raises the same end of the modal loop with no sizing message before it.
            shell.Popup.Width = 456; shell.Popup.Height = 620; shell.UpdatePopupTypography();
            shell.Popup.Width = 400; shell.Popup.Height = 500;
            SendMessage(shell, WmExitSizeMove);
            Check(Math.Abs(source.FontSize - 19) < 0.01 && Math.Abs(entry.FontSize - 22) < 0.01,
                "Moving the window ends the loop without pausing or re-applying the reading page");
            shell.UpdatePopupTypography();
            Check(Math.Abs(source.FontSize - Math.Max(13, 19 * AppShell.TypographyScale(400, 500))) < 0.01,
                "A size set without a drag still applies the window's own scale");
            var record = shell.Current;
            shell.Popup.Width = 456; shell.Popup.Height = 620; shell.UpdatePopupTypography();
            SendMessage(shell, WmSizing);
            shell.Popup.Width = 380; shell.Popup.Height = 420;
            shell.HidePopup();
            Check(!HighlightSuspended(HighlightLayerOf(shell)), "Hiding releases an interrupted drag");
            shell.ShowPopup();
            Check(Math.Abs(source.FontSize - Math.Max(13, 19 * small)) < 0.01 &&
                ReferenceEquals(shell.Current, record) && ReferenceEquals(card.Document, document),
                "Reopening applies the interrupted drag's final scale without replacing the session or card");
            SendMessage(shell, WmSizing);
            shell.Dispose();
            SendMessage(shell, WmSizing);
            shell.UpdatePopupTypography();
            var layer = HighlightLayerOf(shell);
            layer.Show(0, 1);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!HighlightSuspended(layer) && !HighlightQueued(layer) && HighlightBands(layer) == 0,
                "Disposal ends the drag and prevents late messages or selections restarting highlight work");
        }
    }

    // The reading view renders the parsed presentation of the model answer: paired markers
    // become emphasis and never leak into the displayed text, while the record itself keeps
    // the raw answer for copy, history and the LLM context.
    private static async Task TranslationDisplayDropsMarkers(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var translation = shell.TranslateAsync("A marker display sentence.", "选中文字", false);
            handler.ReplyStream(0, "前缀 **加粗** 后缀 \\* 转义");
            await translation;
            Check(Reading(shell.Popup, "TranslationText") == "前缀 加粗 后缀 * 转义",
                "The translation view shows the parsed emphasis instead of the raw markers");
            Check(shell.Current.Translation == "前缀 **加粗** 后缀 \\* 转义",
                "The record keeps the raw answer for copy, history and the LLM context");
        }
    }

    // The box normalizes line breaks to "\r\n", so both offset maps have to stay total and
    // monotone across every boundary: a hole would read as the default 0 and silently
    // explain, highlight or copy the wrong text.
    private static void SourceOffsetMapsStayTotalAndMonotone()
    {
        var box = new TextBox();
        var sources = new[] {
            new { Tag = "plain", Text = "A plain sentence without breaks." },
            new { Tag = "crlf-pair", Text = "First line\r\nsecond line" },
            new { Tag = "lf", Text = "First line\nsecond line" },
            new { Tag = "cr", Text = "Odd\rbreak inside" },
            new { Tag = "mixed", Text = "Mixed\nbreaks\r\nand\rlast" },
            new { Tag = "leading-lf", Text = "\nleading break" },
            new { Tag = "trailing-lf", Text = "trailing break\n" },
            new { Tag = "blank-line", Text = "a\n\nb" },
            new { Tag = "many", Text = "one\ntwo\r\nthree\nfour" }
        };
        foreach (var fixture in sources) {
            box.Text = fixture.Text;
            string display = box.Text;
            var displayToSource = new int[display.Length + 1];
            var sourceToDisplay = new int[fixture.Text.Length + 1];
            AppShell.BuildOffsetMaps(fixture.Text, display, displayToSource, sourceToDisplay);
            Check(displayToSource[0] == 0 && sourceToDisplay[0] == 0 &&
                displayToSource[display.Length] == fixture.Text.Length &&
                sourceToDisplay[fixture.Text.Length] == display.Length,
                "The offset maps of [" + fixture.Tag + "] anchor both ends of both texts");
            bool monotone = true;
            for (int i = 1; i < displayToSource.Length; i++) monotone &= displayToSource[i] >= displayToSource[i - 1];
            for (int i = 1; i < sourceToDisplay.Length; i++) monotone &= sourceToDisplay[i] >= sourceToDisplay[i - 1];
            Check(monotone, "The offset maps of [" + fixture.Tag + "] never step back, so no boundary reads a default 0");
        }
    }

    // The paper's typeface travels inside Leaf.exe instead of being asked of the machine: both
    // bundled faces resolve out of the application's own resource container, cover the Latin
    // and Chinese the reading page shows, and bring the agreement the licence requires.
    private static void EmbeddedPaperFont()
    {
        var family = (FontFamily)Application.Current.Resources["PaperFontFamily"];
        foreach (var face in new[] {
            new { Weight = FontWeights.Normal, Resource = "sourcehansanssc-regular" },
            new { Weight = FontWeights.Bold, Resource = "sourcehansanssc-bold" } }) {
            GlyphTypeface glyph;
            bool resolved = new Typeface(family, FontStyles.Normal, face.Weight, FontStretches.Normal)
                .TryGetGlyphTypeface(out glyph);
            Check(resolved, "The bundled " + face.Weight + " face resolves without a system install");
            // A family that had fallen back to an installed face would report a file:// URI here.
            Console.WriteLine("paper font " + face.Weight + ": " + glyph.FontUri + " simulations=" + glyph.StyleSimulations);
            Check(glyph.FontUri.IsAbsoluteUri && glyph.FontUri.Scheme == "pack" &&
                glyph.FontUri.OriginalString.ToLowerInvariant().Contains(face.Resource),
                "The bundled " + face.Weight + " face loads from the application's own resources");
            Check(glyph.CharacterToGlyphMap.ContainsKey('A') &&
                glyph.CharacterToGlyphMap.ContainsKey('中') && glyph.CharacterToGlyphMap.ContainsKey('文'),
                "The bundled " + face.Weight + " face covers the Latin and Chinese the reading page shows");
        }
        string license;
        using (var stream = typeof(AppShell).Assembly.GetManifestResourceStream("Leaf.Assets.Fonts.LICENSE.txt"))
            license = stream == null ? "" : new StreamReader(stream, new UTF8Encoding(false)).ReadToEnd();
        Check(license.Contains("SIL OPEN FONT LICENSE Version 1.1") &&
            license.Contains("Copyright 2014-2025 Adobe") && license.Length > 1000,
            "The font agreement travels inside the executable and is readable");
    }

    // The learning card is one document the reader selects in: a refresh that does not change
    // its typography - a chat message, a repeated layout pass - hands back the same document
    // with the selection still in it, and a real scale change rewrites that document's ladder
    // in place rather than rebuilding it, so a window drag keeps the selection too.
    private static async Task WordCardRefreshKeepsSelection(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A card refresh sentence.", "选中文字", false);
            var word = TextTools.Pieces(shell.Current.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            var card = Ui.Get<RichTextBox>(shell.Popup, "WordCard");
            var document = card.Document;
            card.Selection.Select(document.ContentStart, document.ContentEnd);
            shell.UpdatePopupTypography();
            Check(ReferenceEquals(card.Document, document) && !card.Selection.IsEmpty,
                "A layout pass at the same scale keeps the card document and the reader's selection");
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "这个短语怎么用？";
            await shell.SendChatAsync();
            Check(shell.Current.Chat.Count == 2 && ReferenceEquals(card.Document, document) && !card.Selection.IsEmpty,
                "A chat refresh at the same scale does not rebuild the card or clear the selection");
            // A genuinely different window size is a real typography change: the card's own
            // ladder is written again in place, so the document and the selection stay.
            shell.Popup.Width = 360; shell.Popup.Height = 380; shell.UpdatePopupTypography();
            double small = AppShell.TypographyScale(360, 380);
            var entry = card.Document.Blocks.OfType<Paragraph>().First();
            Check(ReferenceEquals(card.Document, document) && !card.Selection.IsEmpty,
                "A smaller window keeps the card document and the reader's selection");
            Check(Math.Abs(entry.FontSize - Math.Max(14, 22 * small)) < 0.01 &&
                Math.Abs(card.Document.FontSize - Math.Max(13, 16 * small)) < 0.01 &&
                card.Document.Blocks.OfType<Paragraph>().All(p => p.FontSize >= 11) &&
                CardHeadword(shell.Popup) == word.Text,
                "A smaller window rewrites the card's ladder in place, still under the selected word");
            shell.Popup.Width = 456; shell.Popup.Height = 620; shell.UpdatePopupTypography();
            Check(ReferenceEquals(card.Document, document) && Math.Abs(entry.FontSize - 22) < 0.01,
                "Restoring the window restores the card's full-size ladder without rebuilding it");
        }
    }

    // Collapsing a word card puts the expansion entry back; expanding again reuses the card
    // the session already holds, and the translation, the session and the follow-up draft all
    // stay where they were.
    private static async Task ExpandEntryRestoresAfterCollapse(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("prowess", "选中文字", false);
            var record = shell.Current;
            var entry = Ui.Get<Button>(shell.Popup, "ExpandWordButton");
            Check(entry.Visibility == Visibility.Visible,
                "A finished single-word session offers the expansion entry");
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "a kept draft";
            Click(shell.Popup, "ExpandWordButton");
            await Until(() => record.Cards.Count > 0);
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                entry.Visibility == Visibility.Collapsed,
                "Expanding shows the card and steps the entry aside");
            int looked = handler.Calls;
            Click(shell.Popup, "BackToSentence");
            Check(entry.Visibility == Visibility.Visible,
                "Collapsing the card restores the expansion entry");
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Collapsed,
                "Collapsing hides the card");
            Check(ReferenceEquals(shell.Current, record) && record.Completed &&
                Reading(shell.Popup, "TranslationText") == "夹具译文",
                "Collapsing keeps the session and its translation");
            Check(Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "a kept draft",
                "Collapsing keeps the follow-up draft");
            Click(shell.Popup, "ExpandWordButton");
            await Until(() => Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                Reading(shell.Popup, "WordCard").Contains("夹具词义"));
            Check(handler.Calls == looked,
                "Expanding again reuses the session's cached card instead of calling the API");
            Check(ReferenceEquals(shell.Current, record) && CardHeadword(shell.Popup) == "prowess",
                "The re-expanded card still belongs to the same session and word");
        }
    }

    // An internal selection equal to the text already on screen reuses the open session
    // with no request and no history entry, and an uncommitted draft survives the reuse;
    // the root's own text comes back through the return entry, not a new child.
    private static async Task SameSourceInternalSelectionReuse(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("The reuse fixture sentence.", "选中文字", false);
            var record = shell.Current;
            int before = handler.Calls;
            await shell.TranslateInternalSelectionAsync(record.Source);
            Check(ReferenceEquals(shell.Current, record) && handler.Calls == before,
                "An internal selection equal to the open session reuses it without a request");
            Check(shell.Store.History("").Count == 1,
                "The reuse writes no extra history entry");
            shell.BeginSourceEdit(true);
            var input = Ui.Get<TextBox>(shell.Popup, "SourceInput");
            input.Text = "An uncommitted draft.";
            await shell.TranslateInternalSelectionAsync(record.Source);
            Check(ReferenceEquals(shell.Current, record),
                "The same-text selection still reuses the open session with a draft open");
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible &&
                input.Text == "An uncommitted draft.",
                "The uncommitted draft is restored instead of being discarded");
            await shell.TranslateInternalSelectionAsync("A child over the reuse root.");
            var child = shell.Current;
            Check(!ReferenceEquals(shell.Current, record), "The child over the root opens its own conversation");
            await shell.TranslateInternalSelectionAsync(record.Source);
            Check(ReferenceEquals(shell.Current, record) && !ReferenceEquals(shell.Current, child),
                "Selecting the root's own text returns to the root session");
            Check(handler.Calls == before + 1,
                "Returning to the root through the selection costs no request");
        }
    }

    // Every root entry - an external capture, a manual submit, an opened history record,
    // a forget - replaces the return relation, while a retry inside a child keeps it.
    private static async Task RootEntriesClearAnchor(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var root = shell.TranslateAsync("The anchor root.", "选中文字", false);
            handler.ReplyStream(0, "root 译文");
            await root;
            var rootRecord = shell.Current;
            // The child's first request fails, and its retry inherits the anchor.
            var child = shell.TranslateInternalSelectionAsync("A child that fails once.");
            handler.ReplyError(1, HttpStatusCode.InternalServerError);
            await child;
            Check(shell.CanReturnToPreviousSession, "The failed child still anchors to its root");
            Click(shell.Popup, "RetryButton");
            handler.ReplyStream(2, "child 译文");
            await Until(() => shell.Current != null && shell.Current.Completed);
            Check(shell.CanReturnToPreviousSession && shell.Current.Source == "A child that fails once.",
                "Retrying a failed child keeps the anchor");
            // An external capture starts a new root: the return relation is gone.
            var external = shell.TranslateAsync("An external root sentence.", "剪贴板", false);
            handler.ReplyStream(3, "external 译文");
            await external;
            Check(!shell.CanReturnToPreviousSession &&
                Ui.Get<Button>(shell.Popup, "BackToPreviousButton").Visibility == Visibility.Collapsed,
                "An external capture clears the return entry");
            // A manual submit is a root of its own as well.
            var childOverExternal = shell.TranslateInternalSelectionAsync("A child over the external root.");
            handler.ReplyStream(4, "child 译文");
            await childOverExternal;
            Check(shell.CanReturnToPreviousSession, "The child over the external root anchors to it");
            shell.BeginSourceEdit(true);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Text = "A manually submitted root.";
            var submit = shell.SubmitSourceAsync();
            handler.ReplyStream(5, "submitted 译文");
            await submit;
            Check(!shell.CanReturnToPreviousSession,
                "A manual submit clears the return entry");
            // Opening a history record is a root; forgetting clears the anchor with it.
            var historyRecord = rootRecord;
            shell.OpenRecord(historyRecord);
            await Until(() => ReferenceEquals(shell.Current, historyRecord));
            Check(!shell.CanReturnToPreviousSession,
                "Opening a history record clears the return entry");
            shell.Forget(historyRecord.Id);
            Check(shell.Current == null && !shell.CanReturnToPreviousSession,
                "Forgetting the open record leaves nothing to return to");
        }
    }

    // A child answered after its session was left must not write over the root: the
    // cancelled request's late result changes neither the root's text nor its state.
    private static async Task LateChildCannotTouchRoot(string folder)
    {
        var handler = new ControlledHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            var root = shell.TranslateAsync("The root that stays.", "选中文字", false);
            handler.ReplyStream(0, "root 译文");
            await root;
            var rootRecord = shell.Current;
            var ignored = shell.TranslateInternalSelectionAsync("A child answered too late.");
            await Until(() => handler.Requests.Count >= 2);
            shell.ReturnToPreviousSession();
            Check(ReferenceEquals(shell.Current, rootRecord), "The root is back while the child is still in flight");
            handler.ReplyStream(1, "迟到的子会话译文");
            await Settle();
            Check(ReferenceEquals(shell.Current, rootRecord) && rootRecord.Completed && rootRecord.Translation == "root 译文",
                "The late child result does not touch the restored root");
            Check(Ui.Get<Border>(shell.Popup, "ErrorPanel").Visibility == Visibility.Collapsed,
                "The restored root shows no error after the late result");
        }
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

    // Opening a record while the settings page is still flushing keeps the epoch it was opened
    // under: a clear that lands during that wait must not let the delayed entry write the record
    // back under the newer one. The record itself stays on screen from memory.
    private static async Task OpenRecordDuringClearDoesNotResurrect(string folder)
    {
        var store = new LocalStore(folder, new DelayedWrites());
        var seed = Json.Copy(store.Settings); seed.Provider.Model = "fixture-model"; store.SaveSettings(seed);
        using (var shell = new AppShell(store, false, new LlmClient(new FixtureHandler()))) {
            await shell.TranslateAsync("A record from before the clear.", "选中文字", false);
            var record = TranslationRecord.Create("A record opened while the page was still flushing.", "选中文字", store.Settings);
            record.Translation = "清空后不应重新写入。"; record.Completed = true;
            await store.SaveAsync(record, store.HistoryEpoch);
            Check(store.History("").Any(r => r.Id == record.Id), "The record is in the history that is about to be cleared");
            shell.ShowSettingsPage();
            var panel = SettingsPanel(shell);
            // A real pending preference makes the leave before the open take its delayed write.
            Ui.Get<TextBox>(panel, "TargetInput").Text = "英语";
            shell.OpenRecord(record);
            // The clear lands while that leave is still writing, so the epoch moves under the
            // entry that was already queued.
            await store.ClearAsync();
            await Until(() => ReferenceEquals(shell.Current, record));
            Check(ReferenceEquals(shell.Current, record) && !shell.IsSettingsPageOpen &&
                Ui.Get<TextBox>(shell.Popup, "SourceInput").Text == record.Source,
                "The record opened before the clear still shows on the reading page from memory");
            await shell.SaveCurrentAsync();
            await store.FlushAsync();
            Check(store.History("").Count == 0,
                "A record opened before the clear cannot be written back under the newer epoch");
            // This store's writes are slowed on purpose; take the exit snapshot before the scope ends.
            shell.Dispose();
            try { await store.FlushAsync(); } catch { }
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
            // The child looks up a word of its own, so the card on screen belongs to the child
            // when the return entry is used: what comes back must be the root's word, not this.
            var childWord = TextTools.Pieces(shell.Current.Source).First(p => p.IsWord);
            Check(childWord.Text != word.Text, "The child looks up a different word than the root's card");
            await shell.SelectWordAsync(childWord, false);
            Check(CardHeadword(shell.Popup) == childWord.Text,
                "The child's own lookup shows the child's word on its card");
            Check(Ui.Get<TextBlock>(shell.Popup, "SourceBadge").Text == "选中文本",
                "The badge labels an internal selection as 选中文本");
            Check(shell.CanReturnToPreviousSession &&
                Ui.Get<Button>(shell.Popup, "BackToPreviousButton").Visibility == Visibility.Visible,
                "The replaced conversation offers its return entry");
            shell.ReturnToPreviousSession();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReferenceEquals(shell.Current, record) && handler.Calls == 4,
                "Returning to the previous session restores it without any API call");
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                CardHeadword(shell.Popup) == word.Text &&
                Reading(shell.Popup, "WordCard").Contains("夹具词义"),
                "The return restores the root's own word card, never the child's last lookup");
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

    // Learning content is one read-only selectable document, marked for the selection source.
    private static async Task LearningContentSelectable(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A selectable learning sentence.", "选中文字", false);
            var record = shell.Current;
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            var card = Ui.Get<RichTextBox>(shell.Popup, "WordCard");
            Check(Reading(shell.Popup, "WordCard").Contains("夹具词义"),
                "The word meaning renders in the selectable reading control");
            Check(card.IsReadOnly && (string)card.Tag == "LeafSelectableText",
                "The word card is read-only and marked for selection");
            // The entry, the meaning and every section head and body are paragraphs of that
            // one document, so a drag from a heading runs on into the content under it.
            var paragraphs = card.Document.Blocks.OfType<Paragraph>()
                .Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim()).ToList();
            Check(paragraphs.Contains(word.Text) && paragraphs.Contains("词形") && paragraphs.Contains("仅供隔离测试"),
                "The entry, the section heading and its content are paragraphs of one document");
            Check(shell.Popup.FindName("WordTitle") == null && shell.Popup.FindName("WordMeaning") == null &&
                shell.Popup.FindName("LearningSections") == null,
                "The learning card is no longer split across separate controls");
            Check((string)Ui.Get<RichTextBox>(shell.Popup, "TranslationText").Tag == "LeafSelectableText" &&
                (string)Ui.Get<TextBox>(shell.Popup, "SourceText").Tag == "LeafSelectableText",
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
                    SelectRange(Ui.Get<RichTextBox>(shell.Popup, "WordCard"), "learning section text") },
                new ReadingSelection { Label = "the read-only original", Expected = "read-only original text",
                    Select = () => SelectOriginalRange(Ui.Get<TextBox>(shell.Popup, "SourceText"), "read-only original text") },
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
    // The hidden reading page is left exactly as it was too - the card, the open follow-up and
    // the draft it holds must survive a shortcut pressed while the settings page is in front.
    private static async Task InternalSelectionInSettingsIsNotRead(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Target.IsLeaf = true;
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            shell.Selection = new SelectionAcquirer(probe);
            // The direct-input preference is the one that would switch the hidden page into its
            // editor, so it is on for this scenario.
            var direct = Json.Copy(shell.Store.Settings); direct.FocusInputOnShortcut = true; shell.Store.SaveSettings(direct);
            await shell.TranslateAsync("A settings focus sentence.", "选中文字", false);
            var record = shell.Current;
            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            await shell.SelectWordAsync(word, false);
            Click(shell.Popup, "AskButton");
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft behind the settings page";
            Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
                Ui.Get<Border>(shell.Popup, "InputPanel").Visibility == Visibility.Visible &&
                Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "The reading page behind the settings page has a word card, an open follow-up and no editor");
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
            Check(HiddenReadingPageUntouched(shell, record),
                "A shortcut in settings leaves the hidden page's card, follow-up, draft and editor untouched");
            // The guard is about the page, not about one status: an empty typed answer is routed
            // the same way as the unavailable one above.
            shell.InternalSelection = new FixedPageReader(InternalSelectionResult.Empty("SourceText"));
            await shell.InvokeShortcutAsync();
            Check(shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record) && handler.Calls == calls &&
                HiddenReadingPageUntouched(shell, record),
                "An empty internal answer in settings also leaves the hidden page untouched");
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen &&
                shell.Store.Settings.TargetLanguage == "a settings value",
                "Leaving the page afterwards applies the edit it was holding");
            Check(HiddenReadingPageUntouched(shell, record) &&
                Ui.Get<TextBlock>(shell.Popup, "TopicLabel").Text.Contains(word.Text),
                "Returning from settings shows the same reading page the shortcut left alone");
        }
    }

    // The reading page's own state while the settings page is in front of it.
    private static bool HiddenReadingPageUntouched(AppShell shell, TranslationRecord record)
    {
        return Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible &&
            Ui.Get<Border>(shell.Popup, "InputPanel").Visibility == Visibility.Visible &&
            Ui.Get<TextBlock>(shell.Popup, "TranslationCaption").Text == "译文" &&
            Ui.Get<TextBox>(shell.Popup, "SourceText").Visibility == Visibility.Visible &&
            Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed &&
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Text == record.Source &&
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "draft behind the settings page";
    }

    // The popup is the settings host now, so its own window Deactivated event is the entry the
    // tray window's Deactivated used to be: a shortcut recording started on the page is ended
    // when the window loses activation, instead of staying open behind a topmost popup that an
    // outside click does not leave. Both events are the real WPF ones: the recorder's own
    // GotKeyboardFocus, and the window's own Deactivated. That a real activation change reaches
    // it - an outside click, a pinned window, the global registration itself - still needs the
    // manual desktop pass.
    private static async Task ShortcutRecordingEndsWhenHostDeactivates(string folder)
    {
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            await shell.TranslateAsync("A shortcut recording sentence.", "选中文字", false);
            shell.ShowSettingsPage();
            var panel = SettingsPanel(shell);
            var recorder = Ui.Get<TextBox>(panel, "ShortcutInput");
            var hint = Ui.Get<TextBlock>(panel, "ShortcutHint");
            string saved = recorder.Text, idleHint = hint.Text;
            FocusRecorder(recorder);
            Check(recorder.Text.Contains("请按下") && hint.Text.Contains("按下 Ctrl"),
                "Focusing the recorder on the popup settings page starts a shortcut recording");
            Deactivate(shell.Popup);
            Check(recorder.Text == saved && hint.Text == idleHint,
                "The popup losing activation ends the recording and restores the previous shortcut");
            FocusRecorder(recorder);
            Check(recorder.Text.Contains("请按下"),
                "A recording can be started again after the window lost activation");
            Check(await shell.TryLeaveSettingsPageAsync() && !shell.IsSettingsPageOpen,
                "The page can still be left after deactivation ended the recording");
        }
    }

    // Starts the recorder the way the user's click does: the control's own focus event.
    private static void FocusRecorder(TextBox recorder)
    {
        recorder.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice, 0, null, recorder) {
            RoutedEvent = System.Windows.Input.Keyboard.GotKeyboardFocusEvent
        });
    }

    // Raises the window's own Deactivated event, which is what WPF does when the window loses
    // activation. It is backed by a private key rather than a routed event, so its protected
    // raiser is the only headless entry that goes through the real handler list.
    private static void Deactivate(Window window)
    {
        var method = typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(window, new object[] { EventArgs.Empty });
    }

    // A reader that answers one fixed typed result, so a rule that must not depend on the
    // status can be checked for a status this page cannot produce with real controls.
    private sealed class FixedPageReader : IInternalSelectionReader
    {
        private readonly InternalSelectionResult result;
        public FixedPageReader(InternalSelectionResult result) { this.result = result; }
        public InternalSelectionResult Read() { return result; }
    }

    // An external capture runs first; only a text result leaves the open settings page and
    // enters the reading page, so the pending preference is applied at that leave, and the
    // session keeps its draft instead of losing it to a page that was never saved.
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

    // An inconclusive capture (empty or unavailable) never touches an open settings page:
    // the page, its pending edit, the session and its draft all stay exactly as they were,
    // and the outcome is stated on the source badge instead of a generic error.
    private static async Task InconclusiveCaptureKeepsSettingsPage(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Automation = SelectionCaptureResult.NoText(CaptureStatus.Empty, CaptureReason.EmptySelection);
        var handler = new FixtureHandler();
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(handler))) {
            shell.Selection = new SelectionAcquirer(probe);
            await shell.TranslateAsync("An inconclusive capture sentence.", "选中文字", false);
            var record = shell.Current;
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "draft kept through the empty capture";
            shell.ShowSettingsPage();
            var panel = SettingsPanel(shell);
            Ui.Get<TextBox>(panel, "TargetInput").Text = "英语";
            int calls = handler.Calls;
            await shell.InvokeShortcutAsync();
            Check(shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record) && handler.Calls == calls,
                "An empty capture keeps the settings page and the conversation untouched");
            Check(shell.Store.Settings.TargetLanguage != "英语",
                "An empty capture never flushes the pending preference");
            Check(Ui.Get<TextBox>(panel, "TargetInput").Text == "英语" &&
                Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "draft kept through the empty capture",
                "An empty capture keeps the page edit and the follow-up draft");
            Check(Ui.Get<Border>(shell.Popup, "ErrorPanel").Visibility == Visibility.Collapsed,
                "An empty capture shows no generic error over the kept page");

            // Unavailable behaves the same: nothing on the page moves and the badge explains why.
            probe.Automation = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern);
            await shell.InvokeShortcutAsync();
            Check(shell.IsSettingsPageOpen && ReferenceEquals(shell.Current, record),
                "An unavailable capture keeps the settings page and the conversation too");
            Check(Convert.ToString(Ui.Get<TextBlock>(shell.Popup, "SourceBadge").ToolTip).Contains("未取得选区"),
                "An unavailable capture is described by the source badge");
            Check(Ui.Get<TextBox>(panel, "TargetInput").Text == "英语",
                "An unavailable capture keeps the page edit as well");
        }
    }

    // A settings page that cannot be saved is not left: the captured text is discarded
    // instead of shown over the page, which keeps its reason and its pending edit.
    private static async Task FailedSettingsLeaveBlocksCapture(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Automation = SelectionCaptureResult.Success("capture that must not be shown");
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
                "A settings page that cannot be saved keeps the page and never shows the capture");
            Check(probe.AutomationCalls == 1 && probe.CopyCalls == 0 && probe.ClipboardCalls == 0,
                "The capture still reads the selection once before the page refuses to be left");
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

    // The merged word card's headword is its first paragraph, the same place the entry was
    // read from when the card was split across separate controls.
    private static string CardHeadword(Window popup)
    {
        var box = Ui.Get<RichTextBox>(popup, "WordCard");
        var first = box.Document.Blocks.FirstBlock;
        return first == null ? "" : new TextRange(first.ContentStart, first.ContentEnd).Text.Trim();
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
            Check(Ui.Get<TextBox>(shell.Popup, "SourceText").Visibility == Visibility.Visible &&
                Ui.Get<Button>(shell.Popup, "EditSourceButton").Visibility == Visibility.Visible,
                "Recovered reading page shows the readable original and its edit entry");
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

            var direct = Json.Copy(shell.Store.Settings); direct.FocusInputOnShortcut = true; shell.Store.SaveSettings(direct);
            await shell.InvokeShortcutAsync();
            Check(ReferenceEquals(shell.Current, record) && Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "An internal shortcut without a selection keeps the result even with direct input enabled");

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

    // Leaf itself in front with nothing to restore: the shortcut reveals the empty state
    // quietly and keeps whatever page and draft the user has. The input is entered through
    // the explicit edit control, never opened automatically — not even by the direct-input
    // preference, which only applies when a real text result was obtained.
    private static async Task LeafForegroundWithoutResult(string folder)
    {
        var probe = new FakeSelectionProbe();
        probe.Target.IsLeaf = true;
        using (var shell = new AppShell(ConfiguredStore(folder), false, new LlmClient(new FixtureHandler()))) {
            shell.Selection = new SelectionAcquirer(probe);
            Check(shell.Current == null, "A fresh shell has no result to recover");
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "The first empty page starts in reading mode");
            await shell.InvokeShortcutAsync();
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "A shortcut with no result keeps the empty reading page instead of opening the editor");
            Check(Ui.Get<TextBlock>(shell.Popup, "SourceBadge").Text == "选中模式",
                "The no-result empty state is stated on the source badge");
            Check(probe.AutomationCalls == 0 && probe.CopyCalls == 0 && probe.ClipboardCalls == 0,
                "The empty state in front of Leaf reads no selection at all");

            // The same shortcut keeps whatever the user has already typed.
            Ui.Visible(Ui.Get<Grid>(shell.Popup, "SourceEditor"), true);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Text = "an uncommitted draft";
            await shell.InvokeShortcutAsync();
            Check(Ui.Get<TextBox>(shell.Popup, "SourceInput").Text == "an uncommitted draft" &&
                shell.Current == null,
                "A shortcut with no result never discards an uncommitted draft");

            // Even the direct-input preference does not switch the empty page into its editor.
            var direct = Json.Copy(shell.Store.Settings); direct.FocusInputOnShortcut = true; shell.Store.SaveSettings(direct);
            Ui.Visible(Ui.Get<TextBox>(shell.Popup, "SourceText"), true);
            Ui.Visible(Ui.Get<Grid>(shell.Popup, "SourceEditor"), false);
            Ui.Get<TextBox>(shell.Popup, "SourceInput").Clear();
            await shell.InvokeShortcutAsync();
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "The direct-input preference does not open the editor when there is no result");
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
            probe.AutomationPending.TrySetResult(SelectionCaptureResult.Success("snapshot preference text"));
            await pending;
            // The captured text is translated, but the editor is not opened: the invocation
            // snapshot said quiet, and a preference changed mid-capture waits for the next one.
            Check(shell.Current.Source == "snapshot preference text" && !ReferenceEquals(shell.Current, record),
                "A captured text starts its own conversation");
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "A text capture uses the direct-input preference it snapshotted, not one changed while it ran");
            Check(handler.Calls == 2, "A text capture adds exactly one API call");

            // A later invocation reads the saved preference and takes the direct-input path.
            probe.AutomationPending = new TaskCompletionSource<SelectionCaptureResult>();
            var second = shell.InvokeShortcutAsync();
            probe.AutomationPending.TrySetResult(SelectionCaptureResult.Success("direct input text"));
            await second;
            Check(shell.Current.Source == "direct input text" &&
                Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible,
                "A text capture with the saved direct-input preference enters the editor");
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

        // With no actual selection, the direct-input preference keeps the recovered reading page.
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
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed &&
                input.Text == record.Source,
                "Without a selection, the direct-input preference preserves the original in reading mode");
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

    // The read-only original is a plain TextBox now; a selection in it is the box's own.
    private static TextBox SelectOriginalRange(TextBox box, string text)
    {
        box.Text = text;
        box.Select(0, text.Length);
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
