using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Leaf;

// Language detection: the real client is driven against fixtures and one loopback socket, so the
// request the detector actually sends, the structured results it returns (identified, undetermined
// and same-language) and the way failures and cancellation surface are all covered together.
internal static class LanguageDetectionTests
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++;
        Console.WriteLine("PASS " + label);
    }
    private static async Task ThrowsAsync(Func<Task> action, string code, string label)
    {
        try { await action(); } catch (UserError error) { Check(error.Code == code, label); return; }
        throw new Exception("FAILED: " + label);
    }
    private static ProviderProfile Profile()
    {
        return new ProviderProfile { Id = "custom", BaseUrl = "https://detect.example/v1", Model = "detect-fixture" };
    }
    private static Task<SourceLanguageResult> Identify(LlmClient client, string target = "中文",
        string source = "His combat prowess gives him an edge.", string translation = "他出色的战斗本领让他占据优势。")
    {
        return new LanguageDetector(client).IdentifyAsync(source, translation, target, Profile(), "test-only", CancellationToken.None);
    }
    // The content a chat completion carries, wrapped the way the endpoint returns it.
    private static string Completion(string content)
    {
        return Json.Write(new { choices = new[] { new { message = new { content = content }, finish_reason = "stop" } } });
    }
    // The detection JSON the detector puts in the user turn, read back from the body it really sent.
    private static Dictionary<string, object> SentPayload(string body)
    {
        var root = (Dictionary<string, object>)Json.Read(body);
        var messages = (object[])root["messages"];
        var user = (Dictionary<string, object>)messages[1];
        return (Dictionary<string, object>)Json.Read((string)user["content"]);
    }

    public static async Task<int> Run()
    {
        assertions = 0;

        // A detected language comes back named in Chinese and usable as a target language, and the
        // request carries the original, the existing translation and the current target.
        using (var handler = new FixtureHandler(Completion("{\"language\":\"英语\",\"certain\":true}"), "application/json", 200))
        using (var client = new LlmClient(handler)) {
            var result = await Identify(client);
            Check(result.Determined && result.Language == "英语" && !result.SameAsTarget,
                "A detected language is returned as the new target language");
            Check(handler.Requests == 1 && handler.Authorization == "Bearer", "Detection authenticates one ordinary completion request");
            var sent = SentPayload(handler.Body);
            Check((string)sent["source_text"] == "His combat prowess gives him an edge." &&
                (string)sent["existing_translation"] == "他出色的战斗本领让他占据优势。" &&
                (string)sent["target_language"] == "中文",
                "The original, its translation and the target language travel with the request");
            var body = (Dictionary<string, object>)Json.Read(handler.Body);
            Check((string)body["model"] == "detect-fixture" && !(bool)body["stream"] && !body.ContainsKey("response_format"),
                "Detection is one non-streaming prompt-based JSON request without a forced response format");
        }

        // Same-language semantics: a Chinese variant is the language the text was already
        // translated into, and an English name for the target language is the same language too.
        using (var handler = new FixtureHandler(Completion("{\"language\":\"繁体中文\",\"certain\":true}"), "application/json", 200))
        using (var client = new LlmClient(handler)) {
            var result = await Identify(client);
            Check(result.Determined && result.Language == "中文" && result.SameAsTarget,
                "A Chinese variant is reported as Chinese and recognised as the current target");
        }
        using (var handler = new FixtureHandler(Completion("{\"language\":\"English\",\"certain\":true}"), "application/json", 200))
        using (var client = new LlmClient(handler)) {
            var result = await Identify(client, "英语");
            Check(result.Determined && result.Language == "英语" && result.SameAsTarget,
                "A language named in another language still matches the configured target");
        }

        // Fixed regressions. A qualifier folds into the same language as the target (Simplified
        // Chinese is not a different language from 中文), only a JSON string is a language name,
        // and an unknown marker matches a whole answer instead of swallowing Navajo / Nahuatl.
        var variantNames = new[] { "Simplified Chinese", "Traditional Chinese", "繁體中文", "American English", "British English" };
        var variantTargets = new[] { "中文", "中文", "中文", "英语", "英语" };
        var variantExpect = new[] { "中文", "中文", "中文", "英语", "英语" };
        for (int i = 0; i < variantNames.Length; i++) {
            var result = LanguageDetector.Parse("{\"language\":\"" + variantNames[i] + "\",\"certain\":true}", variantTargets[i]);
            Check(result.Determined && result.Language == variantExpect[i] && result.SameAsTarget,
                "A qualified language name is the same language as the target: " + variantNames[i]);
        }
        Check(LanguageDetector.Canonical("simplifiedchinese") == "中文" && LanguageDetector.Canonical("americanenglish") == "英语",
            "A qualified name written as one word still folds into its language");
        Check(LanguageDetector.Canonical("Germany") != "德语" && LanguageDetector.Canonical("Navajo") != "中文",
            "A country or unrelated word is not folded into a language by substring");
        foreach (string answer in new[] {
            "{\"language\":42,\"certain\":true}", "{\"language\":true,\"certain\":true}",
            "{\"language\":[\"英语\"],\"certain\":true}", "{\"language\":{\"name\":\"英语\"},\"certain\":true}"
        }) {
            var result = LanguageDetector.Parse(answer, "中文");
            Check(!result.Determined && result.Language == "",
                "A non-string language field is not a language: " + answer);
        }
        foreach (string answer in new[] { "{\"language\":\"n/a\"}", "{\"language\":\"N/A\"}", "{\"language\":\"unknown\"}", "{\"language\":\"none\"}" }) {
            Check(!LanguageDetector.Parse(answer, "中文").Determined, "An unknown marker is undetermined: " + answer);
        }
        foreach (string name in new[] { "Navajo", "Nahuatl" }) {
            var result = LanguageDetector.Parse("{\"language\":\"" + name + "\",\"certain\":true}", "中文");
            Check(result.Determined && result.Language == name,
                "A name that starts with the unknown marker is still a language: " + name);
        }

        // B1: an answer that is not a language name at all stays undetermined. A bare number, a
        // boolean word, punctuation or an explicit refusal is not a language; several named
        // languages are refused instead of collapsing to whichever the parser saw first.
        foreach (string answer in new[] {
            "{\"language\":\"42\",\"certain\":true}", "{\"language\":\"007\",\"certain\":true}",
            "{\"language\":\"true\",\"certain\":true}", "{\"language\":\"false\",\"certain\":true}",
            "{\"language\":\"yes\",\"certain\":true}", "{\"language\":\"no\",\"certain\":true}",
            "{\"language\":\"???\",\"certain\":true}", "{\"language\":\"...\",\"certain\":true}",
            "{\"language\":\"none of the above\",\"certain\":true}",
            "{\"language\":\"not a language\",\"certain\":true}",
            "{\"language\":\"I cannot determine\",\"certain\":true}"
        }) {
            var result = LanguageDetector.Parse(answer, "中文");
            Check(!result.Determined && result.Language == "",
                "An answer that is not a language is undetermined: " + answer);
        }
        foreach (string answer in new[] {
            "{\"language\":\"Chinese and English\",\"certain\":true}",
            "{\"language\":\"English, Spanish\",\"certain\":true}",
            "{\"language\":\"英语、日语\",\"certain\":true}"
        }) {
            var result = LanguageDetector.Parse(answer, "中文");
            Check(!result.Determined && result.Language == "",
                "Several named languages are not collapsed into one: " + answer);
        }

        // B2: common synonyms and qualified forms fold onto the language the target uses, while a
        // language word beside an unrelated word is not a language answer. Cantonese keeps its own
        // name rather than being folded into Chinese.
        var synonymNames = new[] { "Englisch", "Anglais", "英語", "中國語", "zh-hans", "en-GB", "en-CA", "French (Canada)" };
        var synonymExpect = new[] { "英语", "英语", "英语", "中文", "中文", "英语", "英语", "法语" };
        for (int i = 0; i < synonymNames.Length; i++) {
            var result = LanguageDetector.Parse("{\"language\":\"" + synonymNames[i] + "\",\"certain\":true}", "中文");
            Check(result.Determined && result.Language == synonymExpect[i],
                "A common synonym or qualified form is classified: " + synonymNames[i]);
        }
        Check(!LanguageDetector.Parse("{\"language\":\"English dictionary\",\"certain\":true}", "中文").Determined,
            "A stray word beside a language name is not a language answer");
        foreach (string name in new[] { "Cantonese", "廣東話" }) {
            var result = LanguageDetector.Parse("{\"language\":\"" + name + "\",\"certain\":true}", "中文");
            Check(result.Determined && result.Language == name && !result.SameAsTarget,
                "A language kept as its own name is not folded into Chinese: " + name);
        }
        // The languages the target drop-down offers, each in its common model output form.
        var dropdownNames = new[] { "中文", "繁體中文", "English", "日本語", "한국어", "Français", "Deutsch", "Español", "Português", "Русский", "Italiano", "العربية", "हिन्दी" };
        var dropdownCanonical = new[] { "中文", "中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "葡萄牙语", "俄语", "意大利语", "阿拉伯语", "印地语" };
        for (int i = 0; i < dropdownNames.Length; i++) {
            var result = LanguageDetector.Parse("{\"language\":\"" + dropdownNames[i] + "\",\"certain\":true}", "中文");
            Check(result.Determined && result.Language == dropdownCanonical[i],
                "A common dropdown language is classified: " + dropdownNames[i]);
        }

        // B4: the counterexamples found in review. A refusal or a non-language answer is never a
        // language, an explicit multi-language answer stays undetermined instead of collapsing,
        // and Chinese script/region qualifiers fold onto 中文 without merging Cantonese.
        foreach (string answer in new[] {
            "{\"language\":\"无法判断语言\",\"certain\":true}",
            "{\"language\":\"不确定的语言\",\"certain\":true}",
            "{\"language\":\"没有语言\",\"certain\":true}",
            "{\"language\":\"无法确定原文语言\",\"certain\":true}",
            "{\"language\":\"Mixed\",\"certain\":true}",
            "{\"language\":\"mixed language\",\"certain\":true}",
            "{\"language\":\"Multiple languages\",\"certain\":true}",
            "{\"language\":\"多种语言\",\"certain\":true}",
            "{\"language\":\"Other\",\"certain\":true}",
            "{\"language\":\"其他\",\"certain\":true}",
            "{\"language\":\"Auto\",\"certain\":true}",
            "{\"language\":\"自动检测\",\"certain\":true}"
        }) {
            var result = LanguageDetector.Parse(answer, "中文");
            Check(!result.Determined && result.Language == "",
                "A refusal or non-language answer is undetermined: " + answer);
        }
        foreach (string answer in new[] {
            "{\"language\":\"中文和英文\",\"certain\":true}",
            "{\"language\":\"汉语和英语\",\"certain\":true}",
            "{\"language\":\"中文或日语\",\"certain\":true}"
        }) {
            var result = LanguageDetector.Parse(answer, "中文");
            Check(!result.Determined && result.Language == "",
                "A written-together multi-language answer is not one language: " + answer);
        }
        foreach (string name in new[] { "Navajo", "Nahuatl", "Cantonese", "廣東話" }) {
            var result = LanguageDetector.Parse("{\"language\":\"" + name + "\",\"certain\":true}", "中文");
            Check(result.Determined && result.Language == name && !result.SameAsTarget,
                "A custom language name is still kept beside the new refusals: " + name);
        }
        foreach (string name in new[] { "中文（简体）", "中文（繁體）", "繁體中文（台灣）", "繁體中文（香港）", "简体中文（中国）" }) {
            var result = LanguageDetector.Parse("{\"language\":\"" + name + "\",\"certain\":true}", "中文");
            Check(result.Determined && result.Language == "中文" && result.SameAsTarget,
                "A Chinese qualifier folds into the target language: " + name);
        }
        foreach (string name in new[] { "华语", "華語", "台灣中文" }) {
            var result = LanguageDetector.Parse("{\"language\":\"" + name + "\",\"certain\":true}", "中文");
            Check(result.Determined && result.Language == "中文" && result.SameAsTarget,
                "A Chinese synonym does not bypass the same-language answer: " + name);
        }
        Check(LanguageDetector.Canonical("粤语") == "粤语" && LanguageDetector.Canonical("Cantonese") != "中文",
            "Cantonese is not forcibly merged into Chinese");
        Check(!LanguageDetector.Parse("{\"language\":\"英语（某种说明）\",\"certain\":true}", "中文").Determined,
            "An unrelated parenthetical is not blindly stripped from a language name");

        // certain keeps its boolean meaning (only 1 is a yes) and confidence is read on an
        // explicit scale, so an arbitrary non-zero number no longer becomes a confirmation.
        foreach (string answer in new[] {
            "{\"language\":\"英语\",\"certain\":0.2}", "{\"language\":\"英语\",\"certain\":2}",
            "{\"language\":\"英语\",\"certain\":0}", "{\"language\":\"英语\",\"certain\":\"0.2\"}"
        }) {
            Check(!LanguageDetector.Parse(answer, "中文").Determined,
                "A non-boolean number is not a certain answer: " + answer);
        }
        Check(LanguageDetector.Parse("{\"language\":\"英语\",\"certain\":1}", "中文").Determined,
            "certain=1 is a boolean confirmation");
        Check(LanguageDetector.Parse("{\"language\":\"英语\",\"certain\":\"true\"}", "中文").Determined,
            "certain=\"true\" keeps its existing confirmation");
        Check(LanguageDetector.Parse("{\"language\":\"英语\"}", "中文").Determined,
            "A missing certain keeps its existing confident default");
        foreach (string answer in new[] {
            "{\"language\":\"英语\",\"certain\":true,\"confidence\":0.2}",
            "{\"language\":\"英语\",\"certain\":true,\"confidence\":20}",
            "{\"language\":\"英语\",\"certain\":true,\"confidence\":150}",
            "{\"language\":\"英语\",\"certain\":true,\"confidence\":-1}",
            "{\"language\":\"英语\",\"certain\":true,\"confidence\":[\"low\"]}",
            "{\"language\":\"英语\",\"certain\":true,\"confidence\":{\"level\":\"low\"}}"
        }) {
            Check(!LanguageDetector.Parse(answer, "中文").Determined,
                "A low or invalid confidence is a denial: " + answer);
        }
        foreach (string answer in new[] {
            "{\"language\":\"英语\",\"certain\":true,\"confidence\":0.9}",
            "{\"language\":\"英语\",\"certain\":true,\"confidence\":90}",
            "{\"language\":\"英语\",\"certain\":true,\"confidence\":\"high\"}"
        }) {
            Check(LanguageDetector.Parse(answer, "中文").Determined,
                "A high confidence keeps the answer: " + answer);
        }

        // An answer that cannot tell the language is a structured non-success, never a guess.
        foreach (string answer in new[] {
            "{\"language\":\"\",\"certain\":false}", "{\"certain\":false,\"language\":\"英语\"}",
            "{\"language\":\"未知\"}", "{\"language\":\"无法判断\"}", "{\"certain\":true}",
            "{\"language\":\"英语\",\"confidence\":\"low\"}", "{\"language\":\"英语\",\"certain\":\"unknown\"}"
        }) {
            using (var handler = new FixtureHandler(Completion(answer), "application/json", 200))
            using (var client = new LlmClient(handler)) {
                var result = await Identify(client);
                Check(!result.Determined && result.Language == "" && !result.SameAsTarget,
                    "An undetermined answer stays a structured non-success: " + answer);
            }
        }
        using (var handler = new FixtureHandler(Completion("```json\n{\"language\":\"日语\",\"certain\":true}\n```"), "application/json", 200))
        using (var client = new LlmClient(handler)) {
            Check((await Identify(client)).Language == "日语", "A fenced JSON answer is read like the word-card contract");
        }

        // A blank original, a malformed answer, an HTTP failure and a cancellation each keep the
        // handling the rest of the application already uses.
        using (var handler = new FixtureHandler(Completion("{\"language\":\"英语\",\"certain\":true}"), "application/json", 200))
        using (var client = new LlmClient(handler)) {
            var blank = await new LanguageDetector(client).IdentifyAsync("  ", "", "中文", Profile(), "test-only", CancellationToken.None);
            Check(!blank.Determined && handler.Requests == 0, "A blank original is a non-success without a request");
        }
        using (var handler = new FixtureHandler(Completion("not JSON at all"), "application/json", 200))
        using (var client = new LlmClient(handler))
            await ThrowsAsync(() => Identify(client), "format", "A malformed detection answer is reported as a format error");
        using (var handler = new FixtureHandler(Completion("[]"), "application/json", 200))
        using (var client = new LlmClient(handler))
            await ThrowsAsync(() => Identify(client), "format", "An answer that is not an object is rejected");
        using (var handler = new FixtureHandler("{}", "application/json", 401))
        using (var client = new LlmClient(handler))
            await ThrowsAsync(() => Identify(client), "key", "An HTTP failure keeps its existing user-facing classification");
        using (var handler = new FixtureHandler("{}", "application/json", 429))
        using (var client = new LlmClient(handler))
            await ThrowsAsync(() => Identify(client), "rate", "Rate limiting is classified the same way as any other request");
        using (var handler = new WaitingHandler())
        using (var client = new LlmClient(handler))
        using (var cancellation = new CancellationTokenSource()) {
            cancellation.CancelAfter(30);
            bool cancelled = false;
            try { await new LanguageDetector(client).IdentifyAsync("Some text", "", "中文", Profile(), "test-only", cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancellation propagates out of detection");
        }

        // The detected language is remembered per original and provider, while the same-language
        // answer belongs to the target asked for now. An undetermined answer is not remembered.
        using (var handler = new FixtureHandler(Completion("{\"language\":\"英语\",\"certain\":true}"), "application/json", 200))
        using (var client = new LlmClient(handler)) {
            var detector = new LanguageDetector(client);
            var first = await detector.IdentifyAsync("A sentence to place.", "", "中文", Profile(), "test-only", CancellationToken.None);
            var second = await detector.IdentifyAsync("A sentence to place.", "", "英语", Profile(), "test-only", CancellationToken.None);
            Check(handler.Requests == 1, "A repeated original is answered from the detector's own cache");
            Check(!first.SameAsTarget && second.SameAsTarget && second.Language == "英语",
                "The cached language is compared against the target language asked for now");
            var other = await detector.IdentifyAsync("A different sentence.", "", "中文", Profile(), "test-only", CancellationToken.None);
            Check(handler.Requests == 2 && other.Language == "英语", "A different original is detected on demand");
        }
        using (var handler = new FixtureHandler(Completion("{\"language\":\"\",\"certain\":false}"), "application/json", 200))
        using (var client = new LlmClient(handler)) {
            var detector = new LanguageDetector(client);
            await detector.IdentifyAsync("A sentence to place.", "", "中文", Profile(), "test-only", CancellationToken.None);
            await detector.IdentifyAsync("A sentence to place.", "", "中文", Profile(), "test-only", CancellationToken.None);
            Check(handler.Requests == 2, "An undetermined answer is asked again instead of being cached");
        }
        using (var handler = new FixtureHandler(Completion("{\"language\":\"Chinese and English\",\"certain\":true}"), "application/json", 200))
        using (var client = new LlmClient(handler)) {
            var detector = new LanguageDetector(client);
            await detector.IdentifyAsync("A sentence to place.", "", "中文", Profile(), "test-only", CancellationToken.None);
            await detector.IdentifyAsync("A sentence to place.", "", "中文", Profile(), "test-only", CancellationToken.None);
            Check(handler.Requests == 2, "An answer that is not one language is asked again instead of being cached");
        }

        // Provider policy is untouched: a GLM-5.3 detection keeps mandatory reasoning, the light
        // effort, the 8192 output budget and the JSON-sized request through the existing client.
        var glm = Json.Copy(Settings.Defaults().Providers.First(p => p.Id == "zhipu"));
        glm.Model = "glm-5.3";
        using (var handler = new FixtureHandler(Completion("{\"language\":\"英语\",\"certain\":true}"), "application/json", 200))
        using (var client = new LlmClient(handler)) {
            await new LanguageDetector(client).IdentifyAsync("A sentence to place.", "", "中文", glm, "test-only", CancellationToken.None);
            var body = (Dictionary<string, object>)Json.Read(handler.Body);
            var thinking = (Dictionary<string, object>)body["thinking"];
            Check((string)thinking["type"] == "enabled" && (string)body["reasoning_effort"] == "low" &&
                (int)body["max_tokens"] == 8192,
                "GLM-5.3 detection keeps mandatory reasoning and its output budget");
        }

        // The prompt itself asks for uncertainty instead of a confident guess.
        string prompt = LanguageDetector.Messages("text", "译文", "中文")[0].Content;
        Check(prompt.Contains("certain=false") && prompt.Contains("不要猜测") && prompt.Contains("JSON"),
            "The detection prompt requires an honest uncertainty answer in JSON");

        await Loopback();
        return assertions;
    }

    // The same detection over a real socket, so serialization, transport and parsing are proven
    // together rather than only through an in-process handler.
    private static async Task Loopback()
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
                byte[] payload = Encoding.UTF8.GetBytes(Completion("{\"language\":\"日语\",\"certain\":true}"));
                byte[] responseHeaders = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeaders, 0, responseHeaders.Length);
                await stream.WriteAsync(payload, 0, payload.Length);
            }
        });
        try {
            using (var client = new LlmClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })) {
                var result = await new LanguageDetector(client).IdentifyAsync("彼は本を読んでいます。", "他在读书。", "中文",
                    new ProviderProfile { Id = "local", Model = "fixture", BaseUrl = "http://127.0.0.1:" + port }, "test-only", CancellationToken.None);
                await server;
                Check(result.Determined && result.Language == "日语" && !result.SameAsTarget,
                    "Real loopback HTTP carries the detection request and its answer");
                Check((string)SentPayload(requestBody)["source_text"] == "彼は本を読んでいます。",
                    "The original text reaches the local endpoint unchanged");
            }
        } finally { listener.Stop(); }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string response, media; private readonly int status;
        public int Requests; public string Body = "", Authorization = "";
        public FixtureHandler(string payload, string type, int code) { response = payload; media = type; status = code; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests++;
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
            Authorization = request.Headers.Authorization == null ? "" : request.Headers.Authorization.Scheme;
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(response, Encoding.UTF8, media) };
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
