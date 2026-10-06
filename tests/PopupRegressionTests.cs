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
            Check(Ui.Get<TextBlock>(shell.Popup, "WordMeaning").Text == "新的词义",
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
                Ui.Get<TextBlock>(shell.Popup, "WordMeaning").Text == "夹具词义",
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

    private static LocalStore ConfiguredStore(string folder)
    {
        var store = new LocalStore(folder); var settings = Json.Copy(store.Settings);
        settings.Provider.Model = "fixture-model"; store.SaveSettings(settings);
        return store;
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
