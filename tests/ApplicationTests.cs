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
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
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
            string previousId = shell.Current.Id;
            shell.BeginSourceEdit(false); Ui.Get<TextBox>(shell.Popup, "SourceInput").Text = "A fast sentence";
            await shell.SubmitSourceAsync();
            Check(shell.Current.Id != previousId && shell.Current.SourceKind == "输入" && shell.Current.Chat.Count == 0,
                "Manual submit starts a new conversation even when the text matches history");
            Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                "Submitting returns to the clickable original text");
            var translated = Ui.Get<RichTextBox>(shell.Popup, "TranslationText");
            Check(new System.Windows.Documents.TextRange(translated.Document.ContentStart, translated.Document.ContentEnd).Text.Trim() == "完整译文",
                "Selectable translation document retains the complete response");
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
            bool closed = false; settings.Window.Closed += (s, e) => closed = true;
            Ui.Get<Button>(settings.Window, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!closed && Ui.Get<TextBlock>(settings.Window, "SettingsStatus").Text.Contains("已应用"), "Applying a service confirms success without closing settings");
            var scene = Ui.Get<ComboBox>(settings.Window, "SceneCombo"); var detail = Ui.Get<TextBox>(settings.Window, "SceneDetailInput");
            scene.SelectedItem = "游戏"; detail.Text = "Baldur's Gate 3";
            scene.SelectedItem = "影视"; detail.Text = "Arrival"; scene.SelectedItem = "游戏";
            Check(detail.Text == "Baldur's Gate 3", "Each scene remembers its own detail while switching");
            Ui.Get<CheckBox>(settings.Window, "FocusInputCheck").IsChecked = true;
            await Task.Delay(650);
            reloaded = new LocalStore(Path.Combine(folder, "provider"));
            Check(reloaded.Settings.SceneDetail == "Baldur's Gate 3" && reloaded.Settings.SceneDetails["影视"] == "Arrival" && reloaded.Settings.FocusInputOnShortcut,
                "Scene names and focus preference automatically persist without applying a service");
            Ui.Get<ComboBox>(settings.Window, "ModelInput").Text = "second-model"; await Task.Delay(650);
            Check(shell.Store.Settings.Provider.Model == "second-model", "Switching a configured model applies without closing settings");
            settings.Window.Close();
            settings = new SettingsWindow(shell);
            Check(((ProviderProfile)Ui.Get<ComboBox>(settings.Window, "ProviderCombo").SelectedItem).Id == "deepseek", "Reopening settings selects the saved provider instead of looking for another provider's key");
            settings.Window.Close();
        }
        using (var catalogHandler = new CatalogHandler())
        using (var shell = new AppShell(ConfiguredStore(Path.Combine(folder, "catalog")), false, new LlmClient(catalogHandler))) {
            var settings = new SettingsWindow(shell);
            Ui.Get<ComboBox>(settings.Window, "ProviderCombo").SelectedIndex = 2;
            Ui.Get<PasswordBox>(settings.Window, "ApiKeyInput").Password = "fake-key-fixture";
            Check(Ui.Get<TextBlock>(settings.Window, "ModelStatus").Text.Contains("准备获取"), "Entering a key immediately provides model-discovery feedback");
            await Task.Delay(850);
            var input = Ui.Get<ComboBox>(settings.Window, "ModelInput");
            Check(catalogHandler.Calls == 1 && input.Items.Count == 2 && input.Text == "", "Key entry automatically fetches models without silently choosing one");
            Check(Ui.Get<TextBlock>(settings.Window, "ModelStatus").Text.Contains("2 个模型"), "Automatic model discovery reports the result count");
            input.SelectedIndex = 1;
            Check(input.Text == "deepseek-v4-pro", "Selecting a fetched model supplies the exact model ID");
            Check(Ui.Get<TextBlock>(settings.Window, "ModelVendorHint").Text.Contains("catalog-owner"), "Model ownership is displayed only from catalog metadata");
            input.Text = "custom-model";
            Ui.Get<Button>(settings.Window, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(shell.Store.Settings.Provider.Model == "custom-model", "Manual model IDs remain supported after fetching a catalog");
        }
        using (var catalogHandler = new CatalogHandler())
        using (var shell = new AppShell(ConfiguredStore(Path.Combine(folder, "debounce")), false, new LlmClient(catalogHandler))) {
            var settings = new SettingsWindow(shell);
            var key = Ui.Get<PasswordBox>(settings.Window, "ApiKeyInput");
            key.Password = "part"; await Task.Delay(120); key.Password = "complete-fixture";
            await Task.Delay(850);
            Check(catalogHandler.Calls == 1, "Debouncing avoids a request for every character of a typed key");
            Ui.Get<TextBox>(settings.Window, "EndpointInput").Text = "https://example.invalid/v1";
            await Task.Delay(700);
            Check(catalogHandler.Calls == 1 && key.Password.Length == 0, "Changing the endpoint clears the edited key and stops automatic discovery");
            key.Password = "temporary-fixture"; settings.Window.Close(); await Task.Delay(750);
            Check(catalogHandler.Calls == 1, "Closing settings cancels a scheduled model lookup");
        }
        using (var catalogHandler = new DelayedCatalogHandler())
        using (var shell = new AppShell(ConfiguredStore(Path.Combine(folder, "stale-catalog")), false, new LlmClient(catalogHandler))) {
            var settings = new SettingsWindow(shell);
            var key = Ui.Get<PasswordBox>(settings.Window, "ApiKeyInput"); key.Password = "old-fixture";
            await Task.Delay(750);
            Check(catalogHandler.Calls == 1 && Ui.Get<TextBlock>(settings.Window, "ModelStatus").Text.Contains("正在获取"), "Slow catalog requests display a loading state");
            Ui.Get<ComboBox>(settings.Window, "ProviderCombo").SelectedIndex = 2;
            Check(catalogHandler.Cancellation.IsCancellationRequested, "Switching providers cancels the previous catalog request");
            catalogHandler.Reply(); await Task.Delay(60);
            Check(Ui.Get<ComboBox>(settings.Window, "ModelInput").Items.Count == 0, "A late provider catalog cannot replace the new provider's choices");
            key.Password = "new-fixture"; key.Clear(); await Task.Delay(750);
            Check(catalogHandler.Calls == 1, "Clearing an unsaved key never reuses its pending password");
            settings.Window.Close();
        }
        using (var shell = new AppShell(ConfiguredStore(Path.Combine(folder, "advanced")), false)) {
            var settings = new SettingsWindow(shell);
            Ui.Get<ComboBox>(settings.Window, "ModelInput").Text = "glm-5.3-flash";
            var thinking = Ui.Get<ComboBox>(settings.Window, "ThinkingModeCombo"); thinking.SelectedIndex = 1;
            Ui.Get<ComboBox>(settings.Window, "ReasoningCombo").SelectedIndex = 1;
            Ui.Get<TextBox>(settings.Window, "OutputLimitInput").Text = "4096";
            Ui.Get<Button>(settings.Window, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var profile = new LocalStore(Path.Combine(folder, "advanced")).Settings.Provider;
            Check(profile.ThinkingMode == "enabled" && profile.ReasoningEffort == "high" && profile.MaxOutputTokens == 4096,
                "Advanced model settings survive saving and restarting");
            settings = new SettingsWindow(shell);
            Check(Ui.Get<ComboBox>(settings.Window, "ThinkingModeCombo").SelectedIndex == 1 && Ui.Get<TextBox>(settings.Window, "OutputLimitInput").Text == "4096",
                "Reopening settings restores the selected model's advanced options");
            settings.Window.Close();
        }
        using (var catalogHandler = new CatalogHandler())
        using (var shell = new AppShell(new LocalStore(Path.Combine(folder, "key-first")), false, new LlmClient(catalogHandler))) {
            var settings = new SettingsWindow(shell);
            var key = Ui.Get<PasswordBox>(settings.Window, "ApiKeyInput"); key.Password = "unassigned-fixture";
            await Task.Delay(750);
            Check(catalogHandler.Calls == 0, "An unassigned API key is never probed against guessed providers");
            var providers = Ui.Get<ComboBox>(settings.Window, "ProviderCombo");
            providers.SelectedItem = providers.Items.Cast<ProviderProfile>().First(p => p.Id == "openai");
            await Task.Delay(850);
            Check(catalogHandler.Calls == 1 && catalogHandler.Host == "api.openai.com" && key.Password == "unassigned-fixture",
                "Key-first setup retains the key and discovers models only at the explicitly selected OpenAI endpoint");
            settings.Window.Close();
        }
        using (var catalogHandler = new CatalogHandler())
        using (var shell = new AppShell(new LocalStore(Path.Combine(folder, "custom-first")), false, new LlmClient(catalogHandler))) {
            var settings = new SettingsWindow(shell);
            Ui.Get<PasswordBox>(settings.Window, "ApiKeyInput").Password = "custom-key-fixture";
            Ui.Get<TextBox>(settings.Window, "EndpointInput").Text = "https://example.invalid/v1";
            await Task.Delay(850);
            Check(catalogHandler.Calls == 1 && catalogHandler.Host == "example.invalid" &&
                ((ProviderProfile)Ui.Get<ComboBox>(settings.Window, "ProviderCombo").SelectedItem).Id == "custom",
                "Entering a custom endpoint after the key automatically loads its catalog without losing the key");
            Ui.Get<ComboBox>(settings.Window, "ModelInput").Text = "custom-model";
            Ui.Get<Button>(settings.Window, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(shell.Store.Settings.Provider.Id == "custom" && shell.Store.Settings.Provider.BaseUrl == "https://example.invalid/v1",
                "Custom service configuration is retained for the next launch");
        }
    }
    private static LocalStore ConfiguredStore(string directory)
    {
        var store = new LocalStore(directory); var settings = Json.Copy(store.Settings);
        settings.Provider.Model = "fixture-model"; store.SaveSettings(settings); return store;
    }
    private sealed class CatalogHandler : HttpMessageHandler
    {
        public int Calls; public string Host;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++;
            Host = request.RequestUri.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"data\":[{\"id\":\"deepseek-flash\"},{\"id\":\"deepseek-v4-pro\",\"owned_by\":\"catalog-owner\"}]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
    private sealed class DelayedCatalogHandler : HttpMessageHandler
    {
        public int Calls; public CancellationToken Cancellation;
        private readonly TaskCompletionSource<HttpResponseMessage> completion = new TaskCompletionSource<HttpResponseMessage>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++; Cancellation = cancellation; return completion.Task;
        }
        public void Reply()
        {
            completion.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"old-model\"}]}", System.Text.Encoding.UTF8, "application/json") });
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
