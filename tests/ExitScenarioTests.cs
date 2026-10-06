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
using System.Windows.Threading;
using Leaf;

// Runs in a child process with its own WPF Application so Application.Shutdown and a real
// process exit are part of the check. The parent asserts the reloaded files afterwards.
public static class ExitScenarioTests
{
    public static int Run(string mode, string folder)
    {
        Directory.CreateDirectory(folder);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.InitializeTheme();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        int exit = 0;
        try {
            var task = Scenario(mode, folder);
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            timer.Tick += (s, e) => frame.Continue = false;
            timer.Start();
            task.ContinueWith(finished => {
                if (finished.IsFaulted) {
                    exit = 1;
                    try { File.WriteAllText(Path.Combine(folder, "error.txt"), finished.Exception.ToString()); } catch { }
                }
                try { application.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)); }
                catch { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
            timer.Stop();
            if (!task.IsCompleted) throw new Exception("Exit scenario timed out.");
            task.GetAwaiter().GetResult();
        } catch (Exception error) {
            exit = 1;
            try { File.WriteAllText(Path.Combine(folder, "error.txt"), error.ToString()); } catch { }
        } finally {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        return exit;
    }

    private static Task Scenario(string mode, string folder)
    {
        string data = Path.Combine(folder, "data");
        if (mode == "normal") return Normal(data, folder);
        if (mode == "late") return Late(data, folder);
        if (mode == "fail") return Failing(data, folder);
        if (mode == "placement") return Placement(data, folder);
        if (mode == "retry") return RetryBarrier(data, folder);
        if (mode == "settings") return SettingsPage(data, folder);
        throw new ArgumentException("Unknown exit scenario: " + mode);
    }

    // The popup's own settings page is part of the exit: a pending edit made there is flushed and
    // the page is left before anything is disposed. The parent asserts the reloaded files.
    private static async Task SettingsPage(string data, string folder)
    {
        var store = await ConfiguredStore(data);
        using (var shell = new AppShell(store, false, new LlmClient(new ExitHandler()), new ExitCredentials())) {
            await shell.TranslateAsync("exit settings source", "剪贴板", false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "exit settings draft";
            shell.ShowSettingsPage();
            var panel = (FrameworkElement)Ui.Get<ContentControl>(shell.Popup, "SettingsPageHost").Content;
            Ui.Get<TextBox>(panel, "TargetInput").Text = "英语";
            bool open = shell.IsSettingsPageOpen;
            await shell.ExitAsync();
            var history = store.History("");
            File.WriteAllText(Path.Combine(folder, "exit-settings.json"), Json.Write(new {
                opened = open,
                left = !shell.IsSettingsPageOpen,
                target_saved = store.Settings.TargetLanguage == "英语",
                history = history.Count,
                draft_saved = history.Count == 1 && history[0].Draft == "exit settings draft"
            }));
        }
    }

    private static Task<LocalStore> ConfiguredStore(string data)
    {
        var store = new LocalStore(data);
        var settings = Json.Copy(store.Settings);
        settings.Provider.Model = "exit-fixture-model";
        settings.Provider.BaseUrl = "https://exit.invalid/v1";
        store.SaveSettings(settings);
        return Task.FromResult(store);
    }

    private static async Task Normal(string data, string folder)
    {
        var store = await ConfiguredStore(data);
        using (var shell = new AppShell(store, false, new LlmClient(new ExitHandler()), new ExitCredentials())) {
            await shell.TranslateAsync("exit normal source", "剪贴板", false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "exit normal draft";
            var pendingPreferences = Json.Copy(store.Settings);
            pendingPreferences.Scene = "书籍"; pendingPreferences.SceneDetail = "Exit pending patch";
            var patch = shell.ApplySettingsAsync(SettingsUpdate.Preferences(pendingPreferences),
                new Dictionary<string, string>(), new HashSet<string>());
            var placement = store.SavePlacementAsync(new WindowPlacement {
                X = 77, Y = 88, Width = 456, Height = 620, Screen = "exit"
            });
            await shell.ExitAsync();
            // The queued work must have completed before shutdown; these tasks are already done.
            await patch; await placement;
        }
    }

    private static async Task Late(string data, string folder)
    {
        var files = new ExitFaultingFiles { DelayWritesMs = 1500 };
        var store = new LocalStore(data, files);
        var settings = Json.Copy(store.Settings);
        settings.Provider.Model = "exit-fixture-model";
        settings.Provider.BaseUrl = "https://exit.invalid/v1";
        await store.SaveSettingsAsync(SettingsUpdate.Full(settings));
        using (var shell = new AppShell(store, false, new LlmClient(new ExitDelayedHandler()), new ExitCredentials())) {
            await shell.TranslateAsync("exit late source", "剪贴板", false);
            var word = TextTools.Pieces(shell.Current.Source).First(p => p.IsWord);
            var lookup = shell.SelectWordAsync(word, false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "exit late question";
            var chat = shell.SendChatAsync();
            var exit = shell.ExitAsync();
            await Task.WhenAll(lookup, chat);
            await exit;
        }
    }

    private static async Task Failing(string data, string folder)
    {
        var files = new ExitFaultingFiles();
        var store = new LocalStore(data, files);
        var settings = Json.Copy(store.Settings);
        settings.Provider.Model = "exit-fixture-model";
        settings.Provider.BaseUrl = "https://exit.invalid/v1";
        await store.SaveSettingsAsync(SettingsUpdate.Full(settings));
        using (var shell = new AppShell(store, false, new LlmClient(new ExitHandler()), new ExitCredentials())) {
            await shell.TranslateAsync("exit fail source", "剪贴板", false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "exit fail draft";
            files.MatchingCalls = 0; files.FailOperation = "Replace"; files.FailAt = 1;
            await shell.ExitAsync();
            bool alive = !shell.IsExiting;
            bool retryVisible = Ui.Get<Button>(shell.Popup, "RetryButton").Visibility == Visibility.Visible;
            File.WriteAllText(Path.Combine(folder, "alive.json"),
                Json.Write(new { alive = alive, retry_visible = retryVisible }));
            files.FailAt = -1; files.MatchingCalls = 0;
            await store.RetryFailedWritesAsync();
            await shell.ExitAsync();
        }
    }

    // A fully flushed exit must not start another placement save while it disposes; the
    // native read is replaced by a seam so no real desktop is involved.
    private static async Task Placement(string data, string folder)
    {
        var store = await ConfiguredStore(data);
        using (var shell = new AppShell(store, false, new LlmClient(new ExitHandler()), new ExitCredentials())) {
            int captures = 0;
            var first = new WindowPlacement { X = 111, Y = 112, Width = 456, Height = 620, Screen = "exit" };
            var second = new WindowPlacement { X = 222, Y = 223, Width = 456, Height = 620, Screen = "exit" };
            shell.PlacementReader = () => ++captures == 1 ? first : second;
            await shell.ExitAsync();
            await store.FlushAsync();
            var saved = store.Settings.Placement;
            File.WriteAllText(Path.Combine(folder, "placement.json"),
                Json.Write(new { captures = captures, x = saved == null ? -1 : (int)saved.X }));
        }
    }

    // Once the exit has started, the retry closure captured before it must not turn into a
    // new API request; the child process owns the real shutdown.
    private static async Task RetryBarrier(string data, string folder)
    {
        var store = await ConfiguredStore(data);
        var handler = new ExitRetryHandler();
        using (var shell = new AppShell(store, false, new LlmClient(handler), new ExitCredentials())) {
            await shell.TranslateAsync("exit retry source", "剪贴板", false);
            Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "exit retry question";
            await shell.SendChatAsync();
            bool retryVisible = Ui.Get<Button>(shell.Popup, "RetryButton").Visibility == Visibility.Visible;
            int before = handler.Calls;
            var exit = shell.ExitAsync();
            Ui.Get<Button>(shell.Popup, "RetryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            File.WriteAllText(Path.Combine(folder, "retry.json"),
                Json.Write(new { retry_visible = retryVisible, before = before, after = handler.Calls }));
            await exit;
        }
    }

    private sealed class ExitCredentials : ICredentialProfiles
    {
        public string Read(ProviderProfile profile) { return ""; }
        public void Save(ProviderProfile profile, string key) { }
        public void Delete(ProviderProfile profile) { }
    }

    private sealed class ExitHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            string payload = "data: " + Json.Write(new { choices = new[] { new { delta = new { content = "退出夹具译文" } } } }) +
                "\n\ndata: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
            });
        }
    }

    // First call is an immediate streamed translation; word/chat calls complete after a
    // delay so they finish while the exit path is still flushing the store.
    private sealed class ExitDelayedHandler : HttpMessageHandler
    {
        private int calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) {
                string stream = "data: " + Json.Write(new { choices = new[] { new { delta = new { content = "迟到退出译文" } } } }) +
                    "\n\ndata: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            }
            await Task.Delay(250);
            string json = Json.Write(new { choices = new[] { new { message = new { content = Json.Write(new { meaning = "迟到结果", sections = new object[0] }) }, finish_reason = "stop" } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    // First call is an immediate streamed translation; the follow-up fails so the original
    // question keeps a retry entry that the exit must refuse.
    private sealed class ExitRetryHandler : HttpMessageHandler
    {
        private int calls;
        public int Calls { get { return calls; } }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) {
                string stream = "data: " + Json.Write(new { choices = new[] { new { delta = new { content = "退出重试译文" } } } }) +
                    "\n\ndata: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
                Content = new StringContent("{\"error\":{\"message\":\"fixture failure\"}}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ExitFaultingFiles : IStoreFiles
    {
        private readonly IStoreFiles inner = new PhysicalStoreFiles();
        public string FailOperation;
        public int FailAt = -1;
        public int MatchingCalls;
        public int DelayWritesMs;
        private void Before(string operation)
        {
            if (operation == FailOperation && ++MatchingCalls == FailAt) throw new IOException("Exit fixture failure");
        }
        public bool Exists(string path) { return inner.Exists(path); }
        public long Length(string path) { return inner.Length(path); }
        public string Read(string path) { Before("Read"); return inner.Read(path); }
        public void Write(string path, string payload)
        {
            Before("Write");
            if (DelayWritesMs > 0) Thread.Sleep(DelayWritesMs);
            inner.Write(path, payload);
        }
        public void Copy(string source, string destination, bool overwrite) { Before("Copy"); inner.Copy(source, destination, overwrite); }
        public void Move(string source, string destination) { Before("Move"); inner.Move(source, destination); }
        public void Replace(string source, string destination) { Before("Replace"); inner.Replace(source, destination); }
        public void Delete(string path) { Before("Delete"); inner.Delete(path); }
    }
}
