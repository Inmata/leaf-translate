using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Leaf;

public static class CoreTests
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++; Console.WriteLine("PASS " + label);
    }
    private static void Throws(Action action, string code, string label)
    {
        try { action(); } catch (UserError error) { Check(error.Code == code, label); return; }
        throw new Exception("FAILED: " + label);
    }
    private static async Task ThrowsAsync(Func<Task> action, string code, string label)
    {
        try { await action(); } catch (UserError error) { Check(error.Code == code, label); return; }
        throw new Exception("FAILED: " + label);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        string folder = Path.Combine(Path.GetTempPath(), "leaf-tests-" + Guid.NewGuid().ToString("N"));
        try {
            if (args.Contains("--native")) return WindowsNativeTests.Run(folder);
            Domain(); Storage(folder); Transport().GetAwaiter().GetResult();
            assertions += ApplicationTests.Run(Path.Combine(folder, "app"));
            Console.WriteLine("SUCCESS: " + assertions + " assertions"); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            // The target is a freshly generated directory under TEMP, never a user/project path.
            if (Directory.Exists(folder) && Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(folder, true);
        }
    }
    private static void Domain()
    {
        var settings = Settings.Defaults();
        string key = CacheKeys.For("prowess", settings);
        var other = Json.Copy(settings); other.Scene = "游戏"; other.SceneDetail = "Example";
        Check(key != CacheKeys.For("prowess", other), "Context changes invalidate cache");
        other = Json.Copy(settings); other.Providers[0].Model = "different-model";
        Check(key != CacheKeys.For("prowess", other), "Model changes invalidate cache");
        other = Json.Copy(settings); other.TargetLanguage = "日本語";
        Check(key != CacheKeys.For("prowess", other), "Target language changes invalidate cache");
        other = Json.Copy(settings); other.Learning[2].Enabled = true;
        Check(key != CacheKeys.For("prowess", other), "Learning changes invalidate cache");
        Check(key == CacheKeys.For("  prowess  ", settings), "Boundary whitespace does not duplicate cache");
        Check(CacheKeys.For("His prowess", settings) != CacheKeys.For("Her prowess", settings), "Sentence context is retained");
        string source = "L'homme dit : Grüße, Grüße! 😀";
        var pieces = TextTools.Pieces(source);
        Check(string.Concat(pieces.Select(p => p.Text)) == source, "Unicode tokenization preserves every character");
        var repeated = pieces.Where(p => p.Text == "Grüße").ToList();
        Check(repeated.Count == 2 && repeated[0].Key != repeated[1].Key, "Repeated words have independent offsets");
        Check(pieces.Any(p => p.Text == "L'homme" && p.IsWord), "French apostrophes stay in the word");
        Check(TextTools.Pieces("e\u0301lan").Count == 1, "Combining marks are preserved");
        Throws(() => TextTools.ValidateInput(" "), "empty", "Empty input fails explicitly");
        Throws(() => TextTools.ValidateInput(new string('x', 6001)), "length", "Long input is bounded");
        var card = WordCard.Parse(Json.Write(new {
            meaning = "本领", lemma = "prowess", part_of_speech = "名词",
            target_phrase = "不存在的译文", sections = new[] { new { title = "比较", content = "power 用法更广。" } }
        }), "prowess", "他有本领。");
        Check(card.target_phrase == "", "Unverified target phrase is discarded");
        Check(card.sections.Count == 1 && card.word == "prowess", "Word card preserves validated explanations");
        string fence = new string((char)96, 3);
        Check(WordCard.Parse(fence + "json\n{\"meaning\":\"本领\",\"target_phrase\":\"本领\"}\n" + fence, "prowess", "本领").target_phrase == "本领", "Fenced JSON remains supported");
        Throws(() => WordCard.Parse("{\"lemma\":\"x\"}", "x", "x"), "format", "Missing meaning is rejected");
        Throws(() => WordCard.Parse("not JSON", "x", "x"), "format", "Malformed word card is rejected");
        Check((HotkeySpec.Parse("Ctrl+Alt+T").Modifiers & 3) == 3, "Global hotkey modifiers parse correctly");
        Check(HotkeySpec.Parse(" alt + space ").VirtualKey == 32 && (HotkeySpec.Parse("alt+space").Modifiers & 15) == 1, "Alt+Space accepts spaces and case differences");
        Check(HotkeySpec.Parse("Alt+空格").VirtualKey == 32, "The Chinese space-key name is accepted");
        Check(HotkeySpec.Parse("Ctrl+1").VirtualKey == 49, "Digit shortcuts use the digit key rather than numeric enum values");
        Throws(() => HotkeySpec.Parse("Shift+A"), "shortcut", "Typing-only shortcuts are rejected");
        Throws(() => HotkeySpec.Parse("Ctrl+NoSuchKey"), "shortcut", "Invalid shortcut key is rejected");
        var gate = new RequestGate(); long old = gate.Next(), latest = gate.Next();
        Check(!gate.IsCurrent(old) && gate.IsCurrent(latest), "Late responses cannot pass the current generation gate");
        Throws(() => LlmClient.Endpoint("http://example.com/v1"), "endpoint", "Remote plaintext endpoints are rejected");
        Throws(() => LlmClient.Endpoint("https://user:password@example.com/v1"), "endpoint", "Endpoint-embedded credentials are rejected");
        Check(LlmClient.Endpoint("https://api.deepseek.com/").AbsoluteUri == "https://api.deepseek.com/chat/completions", "Completion path is appended exactly once");
        Check(LlmClient.Endpoint("https://api.deepseek.com/chat/completions").AbsoluteUri == "https://api.deepseek.com/chat/completions", "Full completion URL remains usable");
        Check(settings.Providers.All(p => p.Model == ""), "New settings do not assume any fixed provider model");
        var migrated = Json.Copy(settings); migrated.Version = 1; migrated.Providers[2].Model = "deepseek-chat"; migrated.Normalize();
        Check(migrated.Providers[2].Model == "deepseek-flash", "The old official DeepSeek default is migrated once");
        migrated.Version = 1; migrated.Providers[2].Model = "private-model"; migrated.Normalize();
        Check(migrated.Providers[2].Model == "private-model", "Custom model choices survive migration");
        var placement = new WindowPlacement { X = -1800, Y = 180, Width = 520, Height = 680, Screen = "left" };
        var fit = PopupLayout.Fit(placement, -1920, 0, 1920, 1040, 1, "left");
        Check(fit.X == -1800 && fit.Y == 180 && fit.Width == 520 && fit.Height == 680, "Saved geometry is restored on a monitor with negative coordinates");
        fit = PopupLayout.Fit(placement, 0, 0, 1920, 1040, 1.5, "primary");
        Check(fit.X > 0 && fit.X + fit.Width * 1.5 <= 1908 && fit.Y + fit.Height * 1.5 <= 1028, "An unplugged monitor falls back inside the available work area");
        fit = PopupLayout.Fit(new WindowPlacement { X = 5000, Y = -900, Width = 1200, Height = 2000, Screen = "primary" }, 0, 0, 1280, 720, 1.5, "primary");
        Check(fit.X >= 12 && fit.Y >= 12 && fit.X + fit.Width * 1.5 <= 1268 && fit.Y + fit.Height * 1.5 <= 708, "Oversized or off-screen saved windows stay accessible after DPI or resolution changes");
        fit = PopupLayout.Fit(new WindowPlacement { X = 300, Y = 100, Width = 480, Height = 500, Screen = "primary" }, 0, 0, 2560, 1400, 2, "primary");
        Check(fit.Width == 480 && fit.Height == 500, "Saved window size uses device-independent units across DPI settings");
        Check(LlmClient.ModelsEndpoint(Settings.Defaults().Providers[2], 1).AbsoluteUri == "https://api.deepseek.com/models", "DeepSeek discovery uses its official models endpoint");
        var qwenModels = LlmClient.ModelsEndpoint(Settings.Defaults().Providers[1], 2);
        Check(qwenModels.Host == "dashscope.aliyuncs.com" && qwenModels.AbsolutePath == "/api/v1/models" && qwenModels.Query.Contains("page_no=2"), "Bailian discovery uses its own paginated text-model catalog on the configured host");
        Check(LlmClient.ModelsEndpoint(new ProviderProfile { Id = "qwen", BaseUrl = "https://relay.example/v1/chat/completions" }, 1).AbsoluteUri == "https://relay.example/v1/models", "Custom endpoints keep credentials on the configured host");
        var record = TranslationRecord.Create("Original sentence", "剪贴板", settings);
        record.Translation = "译文";
        for (int i = 0; i < 20; i++) {
            record.Chat.Add(new ChatTurn { Role = i % 2 == 0 ? "user" : "assistant", Content = "turn-" + i, Topic = "original" });
        }
        var messages = Prompts.Followup(record, "prowess", null, "Compare power");
        Check(messages.Count == 14 && messages[1].Role == "user", "Follow-up sends a bounded suffix of complete pairs");
        Check(messages[0].Content.Contains("Original sentence") && messages.Last().Content == "Compare power", "Follow-up keeps original text and current question");
    }
    private static void Storage(string folder)
    {
        var store = new LocalStore(folder);
        var settings = Json.Copy(store.Settings); settings.HistoryLimit = 20; store.SaveSettings(settings);
        for (int i = 0; i < 25; i++) {
            var record = TranslationRecord.Create("original-" + i, "剪贴板", settings);
            record.Translation = "译文-" + i; record.Completed = true; store.Save(record);
        }
        Check(store.History("").Count == 20, "History obeys the configured retention limit");
        var found = store.History("original-24").Single(); string id = found.Id;
        store.Save(found);
        Check(store.History("").Count == 20 && store.History("original-24").Single().Id == id, "Saving again does not duplicate a history entry");
        Check(store.History("译文-24").Count == 1, "Translated text is searchable");
        var reloaded = new LocalStore(folder);
        Check(reloaded.Find(found.CacheKey) != null, "History survives restarting the store");
        var incomplete = TranslationRecord.Create("incomplete", "剪贴板", settings); store.Save(incomplete);
        Check(store.History("incomplete").Count == 0, "Incomplete translations are never persisted");
        store.Delete(id);
        Check(store.Find(found.CacheKey) == null, "Deleting history removes its cached result");
        settings.HistoryEnabled = false; store.SaveSettings(settings); store.Save(found);
        Check(store.History("").Count == 0 && new LocalStore(folder).History("").Count == 0, "Disabling history clears existing and prevents new records");
        string payload = File.ReadAllText(Path.Combine(folder, "settings.json"));
        Check(!payload.Contains("api_key") && !payload.Contains("test-key"), "Settings contain no API key field");
        var geometry = new WindowPlacement { X = 420, Y = 170, Width = 540, Height = 720, Screen = "test-display" };
        store.SavePlacement(geometry);
        Check(new LocalStore(folder).Settings.Placement.Width == 540 && new LocalStore(folder).Settings.Placement.Y == 170, "Popup position and resized dimensions survive a store restart");
        string legacyFolder = Path.Combine(folder, "legacy"); Directory.CreateDirectory(legacyFolder);
        File.WriteAllText(Path.Combine(legacyFolder, "settings.json"), Json.Write(new {
            Version = 1, Monitor = "old-display", Positions = new Dictionary<string, PointSetting> {
                { "old-display", new PointSetting { X = -900, Y = 200 } }
            }
        }));
        var legacyStore = new LocalStore(legacyFolder);
        Check(legacyStore.Settings.Placement.Screen == "old-display" && legacyStore.Settings.Placement.X == -900, "Previous per-monitor positions migrate to one remembered popup position");
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{broken");
        var repaired = new LocalStore(folder);
        Check(repaired.Warning != null && Directory.GetFiles(folder, "settings.json.corrupt-*").Length == 1, "Corrupt settings are backed up before defaults");
    }
    private static async Task Transport()
    {
        var messages = new List<ChatTurn> { new ChatTurn { Role = "user", Content = "Hello" } };
        var profiles = Settings.Defaults().Providers;
        profiles[0].Model = "glm-fixture"; profiles[1].Model = "qwen-fixture"; profiles[2].Model = "deepseek-flash";
        string zhipu = LlmClient.RequestBody(profiles[0], messages, true, 1600, false);
        string qwen = LlmClient.RequestBody(profiles[1], messages, true, 1600, false);
        string deepseek = LlmClient.RequestBody(profiles[2], messages, true, 1600, false);
        Check(zhipu.Contains("\"thinking\"") && zhipu.Contains("disabled"), "Zhipu request disables reasoning");
        Check(qwen.Contains("\"enable_thinking\":false"), "Qwen request disables reasoning");
        Check(deepseek.Contains("\"thinking\"") && deepseek.Contains("disabled"), "Current DeepSeek translation requests disable unnecessary reasoning");

        string sse = ": keepalive\n\ndata: {\"choices\":[{\"delta\":{\"reasoning_content\":\"ignored\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"😀\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        string latest = "";
        using (var handler = new FixtureHandler(sse, "text/event-stream", 200))
        using (var client = new LlmClient(handler)) {
            string text = await client.CompleteAsync(profiles[0], "test-only", messages, true, false, value => latest = value, CancellationToken.None);
            Check(text == "你好😀" && latest == text, "SSE tolerates fragmented UTF-8 and ignores reasoning tokens");
            Check(handler.Body.Contains("\"stream\":true") && handler.Body.Contains("Hello"), "Transport sends the serialized chat request");
            Check(handler.Authorization == "Bearer", "Transport supplies bearer authentication without logging the key");
        }
        string ended = "data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\n";
        Check(await LlmClient.ReadSseAsync(new StringReader(ended), null, CancellationToken.None) == "done", "Explicit finish works without DONE sentinel");
        await ThrowsAsync(() => LlmClient.ReadSseAsync(new StringReader("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"), null, CancellationToken.None), "network", "An interrupted stream cannot become a saved success");
        await ThrowsAsync(() => LlmClient.ReadSseAsync(new StringReader("data: invalid-json\n\n"), null, CancellationToken.None), "format", "Malformed SSE is reported clearly");
        await ThrowsAsync(() => LlmClient.ReadSseAsync(new StringReader("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n"), null, CancellationToken.None), "length", "Truncated model output is rejected");
        using (var handler = new FixtureHandler("{\"choices\":[{\"message\":{\"content\":\"A reply\"},\"finish_reason\":\"stop\"}]}", "application/json", 200))
        using (var client = new LlmClient(handler)) {
            Check(await client.CompleteAsync(profiles[0], "test-only", messages, true, false, null, CancellationToken.None) == "A reply", "Non-streaming compatible replies are accepted");
        }
        Check(LlmClient.Classify(401, "").Code == "key", "Invalid credentials get a dedicated error");
        Check(LlmClient.Classify(429, "insufficient_quota").Code == "quota", "Quota exhaustion is distinguished from rate limiting");
        Check(LlmClient.Classify(429, "").Code == "rate", "Rate limiting gets a dedicated error");
        Check(LlmClient.Classify(503, "").Code == "server", "Provider failures get a dedicated error");
        using (var handler = new FixtureHandler("{}", "application/json", 401))
        using (var client = new LlmClient(handler))
            await ThrowsAsync(() => client.CompleteAsync(profiles[0], "test-only", messages, false, false, null, CancellationToken.None), "key", "HTTP failures use visible user errors");
        using (var handler = new WaitingHandler())
        using (var client = new LlmClient(handler))
        using (var cancellation = new CancellationTokenSource()) {
            cancellation.CancelAfter(30);
            bool cancelled = false;
            try { await client.CompleteAsync(profiles[0], "test-only", messages, true, false, null, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Explicit cancellation reaches the HTTP request");
        }
        await Loopback(messages);
        using (var handler = new FixtureHandler("{\"data\":[{\"id\":\"deepseek-flash\"},{\"id\":\"deepseek-v4-pro\"},{\"id\":\"deepseek-flash\"}]}", "application/json", 200))
        using (var client = new LlmClient(handler)) {
            var models = await client.ListModelsAsync(profiles[2], "test-only", CancellationToken.None);
            Check(models.SequenceEqual(new[] { "deepseek-flash", "deepseek-v4-pro" }) && handler.Authorization == "Bearer", "The fetched model list is authenticated and deduplicated without fixed presets");
        }
        using (var handler = new FixtureHandler("{}", "application/json", 404))
        using (var client = new LlmClient(handler))
            await ThrowsAsync(() => client.ListModelsAsync(profiles[0], "test-only", CancellationToken.None), "models", "Unsupported discovery offers manual model entry");
        using (var handler = new FixtureHandler("not-json", "application/json", 200))
        using (var client = new LlmClient(handler))
            await ThrowsAsync(() => client.ListModelsAsync(profiles[2], "test-only", CancellationToken.None), "format", "Malformed model catalogs fail clearly");
        using (var handler = new PagedModelsHandler())
        using (var client = new LlmClient(handler)) {
            var models = await client.ListModelsAsync(profiles[1], "test-only", CancellationToken.None);
            Check(models.Count == 101 && handler.Pages.SequenceEqual(new[] { 1, 2 }), "Bailian catalog pagination includes models beyond the first page");
        }
    }
    private static async Task Loopback(List<ChatTurn> messages)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; string requestBody = "";
        var server = Task.Run(async () => {
            using (var socket = await listener.AcceptTcpClientAsync())
            using (var stream = socket.GetStream()) {
                var headerBytes = new List<byte>();
                while (headerBytes.Count < 16384) {
                    int b = stream.ReadByte(); if (b < 0) throw new IOException(); headerBytes.Add((byte)b);
                    int n = headerBytes.Count;
                    if (n >= 4 && headerBytes[n - 4] == 13 && headerBytes[n - 3] == 10 && headerBytes[n - 2] == 13 && headerBytes[n - 1] == 10) break;
                }
                string headers = Encoding.ASCII.GetString(headerBytes.ToArray());
                int length = int.Parse(headers.Split(new[] { "\r\n" }, StringSplitOptions.None).First(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                byte[] body = new byte[length]; int offset = 0;
                while (offset < length) { int read = await stream.ReadAsync(body, offset, length - offset); if (read == 0) throw new IOException(); offset += read; }
                requestBody = Encoding.UTF8.GetString(body);
                string data = "data: {\"choices\":[{\"delta\":{\"content\":\"本机链路通过\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                byte[] payload = Encoding.UTF8.GetBytes(data);
                byte[] responseHeaders = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeaders, 0, responseHeaders.Length);
                await stream.WriteAsync(payload, 0, payload.Length);
            }
        });
        try {
            using (var client = new LlmClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })) {
                string text = await client.CompleteAsync(new ProviderProfile { Id = "local", Model = "fixture", BaseUrl = "http://127.0.0.1:" + port }, "test-only", messages, true, false, null, CancellationToken.None);
                await server;
                Check(text == "本机链路通过" && requestBody.Contains("fixture"), "Real loopback HTTP covers serialization, transport and streaming");
            }
        } finally { listener.Stop(); }
    }
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string response, media; private readonly int status;
        public string Body, Authorization;
        public FixtureHandler(string payload, string type, int code) { response = payload; media = type; status = code; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(); Authorization = request.Headers.Authorization.Scheme;
            var message = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(response))) };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(media); return message;
        }
    }
    private sealed class PagedModelsHandler : HttpMessageHandler
    {
        public List<int> Pages = new List<int>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            int page = request.RequestUri.Query.Contains("page_no=2") ? 2 : 1; Pages.Add(page);
            var models = Enumerable.Range(page == 1 ? 0 : 100, page == 1 ? 100 : 1).Select(i => new { model = "model-" + i }).ToArray();
            string payload = Json.Write(new { output = new { total = 101, models = models } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class FragmentedStream : MemoryStream
    {
        public FragmentedStream(byte[] bytes) : base(bytes) { }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
        {
            return base.ReadAsync(buffer, offset, Math.Min(count, 3), cancellation);
        }
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            await Task.Delay(10000, cancellation); return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
