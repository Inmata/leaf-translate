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
        await DraftRetention(Path.Combine(folder, "draft"));
        return assertions;
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
