using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Leaf;

public static class ApplicationTests
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++; Console.WriteLine("PASS " + label);
    }
    public static int Run(string folder)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.InitializeTheme();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        try {
            var frame = new DispatcherFrame();
            var task = Scenarios(folder);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += (s, e) => frame.Continue = false;
            timer.Start();
            task.ContinueWith(t => application.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame); timer.Stop();
            if (!task.IsCompleted) throw new Exception("Application request tests timed out.");
            task.GetAwaiter().GetResult(); return assertions;
        } finally { SynchronizationContext.SetSynchronizationContext(previous); application.Shutdown(); }
    }
    private static async Task Scenarios(string folder)
    {
        var immediate = new ImmediateHandler();
        using (var shell = new AppShell(ConfiguredStore(Path.Combine(folder, "fast")), false, new LlmClient(immediate))) {
            await shell.TranslateAsync("A fast sentence", "剪贴板", false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(shell.Current.Completed && shell.Current.Translation == "完整译文", "Queued partial output cannot overwrite a finished translation");
            Check(shell.Store.History("").Single().Translation == "完整译文", "History retains the complete streamed result");
        }

        var handler = new ControlledHandler();
        var store = ConfiguredStore(Path.Combine(folder, "requests"));
        using (var shell = new AppShell(store, false, new LlmClient(handler))) {
            var first = shell.TranslateAsync("Old sentence", "剪贴板", false);
            await shell.TranslateAsync("Old sentence", "剪贴板", false);
            Check(handler.Requests.Count == 1, "Repeated shortcuts share the same in-flight request");
            var second = shell.TranslateAsync("New sentence", "剪贴板", false);
            Check(handler.Requests.Count == 2 && handler.Requests[0].Cancellation.IsCancellationRequested, "A new shortcut cancels the previous translation");
            handler.ReplyStream(1, "新的完整译文"); await second;
            handler.ReplyStream(0, "旧的迟到结果"); await first;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(shell.Current.Source == "New sentence" && shell.Current.Translation == "新的完整译文", "A late old request cannot replace the active conversation");
            Check(store.History("").Count == 1, "Cancelled translations are not added to history");
            var record = shell.Current;
            shell.OpenRecord(record);
            Check(handler.Requests.Count == 2, "Reopening history makes no API call");

            var word = TextTools.Pieces(record.Source).First(p => p.IsWord);
            var lookup = shell.SelectWordAsync(word, false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "这个词为什么这样翻译？";
            var chat = shell.SendChatAsync();
            Check(handler.Requests.Count == 4 && !handler.Requests[2].Cancellation.IsCancellationRequested, "A follow-up leaves the independent word request running");
            handler.ReplyStream(3, "这取决于原句语境。"); await chat;
            handler.ReplyJson(2, new { meaning = "新的", lemma = "new", part_of_speech = "形容词", target_phrase = "新的", sections = new object[0] });
            await lookup;
            Check(record.Cards.Count == 1 && record.Chat.Count == 2, "Word cards and follow-ups both finish in one conversation");
            await shell.SelectWordAsync(word, false);
            Check(handler.Requests.Count == 4, "Repeated word lookup reuses the contextual card");

            var changed = Json.Copy(store.Settings);
            changed.Provider.BaseUrl = "https://current-api.example/v1";
            changed.Provider.Model = "current-model";
            store.SaveSettings(changed);
            shell.OpenRecord(record);
            lookup = shell.SelectWordAsync(word, false);
            Check(handler.Requests.Count == 5 && handler.Requests[4].Uri.Host == "current-api.example" && handler.Requests[4].Body.Contains("current-model"), "Old history uses the currently saved endpoint and model for new word calls");
            Check(handler.Requests[4].Body.Contains("New sentence") && record.Context.Provider.BaseUrl != changed.Provider.BaseUrl, "Changing API preserves the historical language and source context");
            handler.ReplyJson(4, new { meaning = "新的解释", lemma = "new", target_phrase = "", sections = new object[0] }); await lookup;
            Check(record.Cards.Count == 2, "An endpoint/model change invalidates the old word cache");
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "再解释一下";
            chat = shell.SendChatAsync();
            Check(handler.Requests[5].Uri.Host == "current-api.example", "Historical follow-ups also use the current endpoint");
            handler.ReplyStream(5, "进一步的说明。"); await chat;

            var cardRequest = shell.SelectWordAsync(word, true);
            shell.OpenRecord(TranslationRecord.Create("Another source", "选中文字", store.Settings));
            handler.ReplyJson(6, new { meaning = "过期词卡", sections = new object[0] }); await cardRequest;
            Check(shell.Current.Source == "Another source" && shell.Current.Cards.Count == 0, "Switching conversation cannot attach an old word card to the new source");
        }
        var providerStore = ConfiguredStore(Path.Combine(folder, "provider"));
        using (var shell = new AppShell(providerStore, false)) {
            var settings = new SettingsWindow(shell);
            Ui.Get<ComboBox>(settings.Window, "ProviderCombo").SelectedIndex = 2;
            Ui.Get<ComboBox>(settings.Window, "ModelInput").Text = "user-chosen-model";
            Ui.Get<PasswordBox>(settings.Window, "ApiKeyInput").Password = "fake-key-fixture";
            Ui.Get<Button>(settings.Window, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reloaded = new LocalStore(Path.Combine(folder, "provider"));
            Check(reloaded.Settings.ProviderId == "deepseek" && reloaded.Settings.Provider.Model == "user-chosen-model", "Saving a provider selection makes it the active provider after restart");
            settings = new SettingsWindow(shell);
            Check(((ProviderProfile)Ui.Get<ComboBox>(settings.Window, "ProviderCombo").SelectedItem).Id == "deepseek", "Reopening settings selects the saved provider instead of looking for another provider's key");
            settings.Window.Close();
        }
        using (var catalogHandler = new CatalogHandler())
        using (var shell = new AppShell(ConfiguredStore(Path.Combine(folder, "catalog")), false, new LlmClient(catalogHandler))) {
            var settings = new SettingsWindow(shell);
            Ui.Get<ComboBox>(settings.Window, "ProviderCombo").SelectedIndex = 2;
            Ui.Get<PasswordBox>(settings.Window, "ApiKeyInput").Password = "fake-key-fixture";
            Ui.Get<Button>(settings.Window, "FetchModels").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 100 && !Ui.Get<Button>(settings.Window, "FetchModels").IsEnabled; i++) await Task.Delay(5);
            var input = Ui.Get<ComboBox>(settings.Window, "ModelInput");
            Check(input.Items.Count == 2 && input.Text == "", "Model discovery fills choices and leaves the user's model selection explicit");
            input.SelectedIndex = 1;
            Check(input.Text == "deepseek-v4-pro", "Selecting a fetched model supplies the exact model ID");
            input.Text = "custom-model";
            Ui.Get<Button>(settings.Window, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(shell.Store.Settings.Provider.Model == "custom-model", "Manual model IDs remain supported after fetching a catalog");
        }
    }
    private static LocalStore ConfiguredStore(string directory)
    {
        var store = new LocalStore(directory); var settings = Json.Copy(store.Settings);
        settings.Provider.Model = "fixture-model"; store.SaveSettings(settings); return store;
    }
    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"data\":[{\"id\":\"deepseek-flash\"},{\"id\":\"deepseek-v4-pro\"}]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
    private static HttpResponseMessage StreamReply(params string[] chunks)
    {
        string payload = string.Concat(chunks.Select(c => "data: " + Json.Write(new { choices = new[] { new { delta = new { content = c } } } }) + "\n\n")) + "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/event-stream") };
    }
    private sealed class ImmediateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            return Task.FromResult(StreamReply("完整", "译文"));
        }
    }
    private sealed class PendingRequest
    {
        public Uri Uri; public string Body; public CancellationToken Cancellation;
        public TaskCompletionSource<HttpResponseMessage> Completion = new TaskCompletionSource<HttpResponseMessage>();
    }
    private sealed class ControlledHandler : HttpMessageHandler
    {
        public List<PendingRequest> Requests = new List<PendingRequest>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var pending = new PendingRequest { Uri = request.RequestUri, Body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Cancellation = cancellation };
            Requests.Add(pending); return pending.Completion.Task;
        }
        public void ReplyStream(int index, string text) { Requests[index].Completion.SetResult(StreamReply(text)); }
        public void ReplyJson(int index, object card)
        {
            string payload = Json.Write(new { choices = new[] { new { message = new { content = Json.Write(card) }, finish_reason = "stop" } } });
            Requests[index].Completion.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
