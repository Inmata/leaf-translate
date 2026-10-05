using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Leaf
{
    public static class Json
    {
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 33554432, RecursionLimit = 64 }; }
        public static string Write(object value) { return Serializer().Serialize(value); }
        public static T Read<T>(string value) { return Serializer().Deserialize<T>(value); }
        public static object Read(string value) { return Serializer().DeserializeObject(value); }
        public static T Copy<T>(T value) { return Read<T>(Write(value)); }
    }
    public sealed class ProviderProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public string ThinkingMode { get; set; }
        public string ReasoningEffort { get; set; }
        public int MaxOutputTokens { get; set; }
        public override string ToString() { return Name; }
    }
    public sealed class LearningOption
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Instruction { get; set; }
        public bool Enabled { get; set; }
        public bool Custom { get; set; }
    }
    public sealed class PointSetting
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
    public sealed class WindowPlacement
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Screen { get; set; }
    }
    public sealed class LearningPreset
    {
        public string Name { get; set; }
        public string TargetLanguage { get; set; }
        public string Scene { get; set; }
        public string SceneDetail { get; set; }
        public string GameTextType { get; set; }
        public string Style { get; set; }
        public List<LearningOption> Learning { get; set; }
        public override string ToString() { return Name; }
    }
    public sealed class Settings
    {
        public int Version { get; set; }
        public string ProviderId { get; set; }
        public List<ProviderProfile> Providers { get; set; }
        public string TargetLanguage { get; set; }
        public bool ClipboardMode { get; set; }
        public string Shortcut { get; set; }
        public string Scene { get; set; }
        public string SceneDetail { get; set; }
        public Dictionary<string, string> SceneDetails { get; set; }
        public string GameTextType { get; set; }
        public string Style { get; set; }
        public List<LearningOption> Learning { get; set; }
        public List<LearningPreset> Presets { get; set; }
        public bool HistoryEnabled { get; set; }
        public int HistoryLimit { get; set; }
        public bool AutoStart { get; set; }
        public bool FocusInputOnShortcut { get; set; }
        public WindowPlacement Placement { get; set; }
        public ProviderProfile Provider { get { return Providers.FirstOrDefault(p => p.Id == ProviderId) ?? Providers[0]; } }

        public static Settings Defaults()
        {
            return new Settings {
                Version = 3, ProviderId = "zhipu", TargetLanguage = "中文", ClipboardMode = false,
                Shortcut = "Ctrl+Alt+T", Scene = "通用", SceneDetail = "", GameTextType = "自动判断",
                Style = "自然准确", HistoryEnabled = true, HistoryLimit = 200,
                Presets = new List<LearningPreset>(),
                SceneDetails = new Dictionary<string, string>(),
                Providers = new List<ProviderProfile> {
                    new ProviderProfile { Id = "zhipu", Name = "智谱", BaseUrl = "https://open.bigmodel.cn/api/paas/v4", Model = "" },
                    new ProviderProfile { Id = "qwen", Name = "千问", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1", Model = "" },
                    new ProviderProfile { Id = "deepseek", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com", Model = "" },
                    new ProviderProfile { Id = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "" },
                    new ProviderProfile { Id = "custom", Name = "自定义兼容服务", BaseUrl = "", Model = "" }
                },
                Learning = new List<LearningOption> {
                    new LearningOption { Id = "lemma", Name = "原形与词形", Instruction = "解释原形、词性、当前形式与原形的关系。", Enabled = true },
                    new LearningOption { Id = "synonyms", Name = "近义词比较", Instruction = "选一两个容易混淆的近义词，说明语义和使用场景的区别。", Enabled = true },
                    new LearningOption { Id = "roots", Name = "构词与词根", Instruction = "有依据才讲词源；分清构词、词源与记忆联想，不能确认就明确说明。", Enabled = false },
                    new LearningOption { Id = "collocations", Name = "常见搭配", Instruction = "给出两三个符合当前语境的搭配与翻译。", Enabled = false },
                    new LearningOption { Id = "examples", Name = "短例句", Instruction = "提供一个贴近当前场景的短例句与译文。", Enabled = false }
                }
            };
        }
        public void Normalize()
        {
            var defaults = Defaults();
            if (Providers == null || Providers.Count == 0) Providers = defaults.Providers;
            if (Providers.Any(p => p.Id == "zhipu" || p.Id == "qwen" || p.Id == "deepseek")) {
                foreach (var profile in defaults.Providers.Where(p => p.Id == "openai" || p.Id == "custom"))
                    if (!Providers.Any(p => p.Id == profile.Id)) Providers.Add(profile);
            }
            if (Learning == null) Learning = defaults.Learning;
            if (Presets == null) Presets = new List<LearningPreset>();
            if (Version < 2) {
                var oldDefault = Providers.FirstOrDefault(p => p.Id == "deepseek" && p.Model == "deepseek-chat" &&
                    (p.BaseUrl ?? "").TrimEnd('/') == "https://api.deepseek.com");
                if (oldDefault != null) oldDefault.Model = "deepseek-flash";
            }
            if (SceneDetails == null) SceneDetails = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(Scene) && Scene != "通用" && !SceneDetails.ContainsKey(Scene)) SceneDetails[Scene] = SceneDetail ?? "";
            Version = 3;
            if (string.IsNullOrWhiteSpace(TargetLanguage)) TargetLanguage = "中文";
            if (string.IsNullOrWhiteSpace(Scene)) Scene = "通用";
            if (string.IsNullOrWhiteSpace(Style)) Style = "自然准确";
            if (string.IsNullOrWhiteSpace(Shortcut)) Shortcut = defaults.Shortcut;
            HistoryLimit = Math.Max(20, Math.Min(1000, HistoryLimit == 0 ? 200 : HistoryLimit));
        }
    }
    public sealed class LearningSection
    {
        public string title { get; set; }
        public string content { get; set; }
    }
    public sealed class WordCard
    {
        public string word { get; set; }
        public string lemma { get; set; }
        public string part_of_speech { get; set; }
        public string meaning { get; set; }
        public string target_phrase { get; set; }
        public List<LearningSection> sections { get; set; }
        public static WordCard Parse(string text, string word, string translation)
        {
            text = text.Trim();
            string fence = new string((char)96, 3);
            if (text.StartsWith(fence)) {
                int line = text.IndexOf('\n'), end = text.LastIndexOf(fence, StringComparison.Ordinal);
                if (line >= 0 && end > line) text = text.Substring(line + 1, end - line - 1);
            }
            WordCard result;
            try { result = Json.Read<WordCard>(text); }
            catch { throw new UserError("format", "模型返回的词卡无法解析，请重试。"); }
            if (result == null || string.IsNullOrWhiteSpace(result.meaning) || result.meaning.Length > 6000)
                throw new UserError("format", "模型没有返回有效的词义，请重试。");
            result.word = word;
            result.lemma = Safe(result.lemma, 250);
            result.part_of_speech = Safe(result.part_of_speech, 100);
            result.target_phrase = Safe(result.target_phrase, 500);
            if (!string.IsNullOrEmpty(result.target_phrase) && translation.IndexOf(result.target_phrase, StringComparison.Ordinal) < 0)
                result.target_phrase = "";
            result.sections = (result.sections ?? new List<LearningSection>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.content)).Take(8)
                .Select(s => new LearningSection { title = Safe(s.title, 100), content = Safe(s.content, 6000) }).ToList();
            return result;
        }
        private static string Safe(string value, int limit) {
            value = (value ?? "").Trim(); return value.Length <= limit ? value : value.Substring(0, limit);
        }
    }
    public sealed class ChatTurn
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public string Topic { get; set; }
    }
    public sealed class TranslationRecord
    {
        public string Id { get; set; }
        public string CacheKey { get; set; }
        public string Source { get; set; }
        public string SourceKind { get; set; }
        public string Translation { get; set; }
        public long UpdatedUtcTicks { get; set; }
        public Settings Context { get; set; }
        public Dictionary<string, WordCard> Cards { get; set; }
        public List<ChatTurn> Chat { get; set; }
        public string Draft { get; set; }
        public bool Completed { get; set; }
        public string Preview { get { return Source == null ? "" : Source.Replace("\r", " ").Replace("\n", " "); } }
        public string TimeLabel { get { return new DateTime(UpdatedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("MM/dd HH:mm"); } }
        public static TranslationRecord Create(string text, string sourceKind, Settings context)
        {
            return new TranslationRecord {
                Id = Guid.NewGuid().ToString("N"), CacheKey = CacheKeys.For(text, context),
                Source = text, SourceKind = sourceKind, Translation = "", Context = Json.Copy(context),
                UpdatedUtcTicks = DateTime.UtcNow.Ticks, Cards = new Dictionary<string, WordCard>(),
                Chat = new List<ChatTurn>(), Draft = ""
            };
        }
    }
    public static class CacheKeys
    {
        public static string ForWord(TranslationRecord record, TextPiece word, ProviderProfile provider)
        {
            var context = Json.Copy(record.Context);
            context.Providers = new List<ProviderProfile> { Json.Copy(provider) };
            context.ProviderId = provider.Id;
            return word.Key + ":" + For(Json.Write(new { record.Source, record.Translation, word.Start, word.Length }), context);
        }
        public static string For(string text, Settings settings)
        {
            var state = new {
                text = text.Trim(), provider = settings.Provider.Id,
                endpoint = settings.Provider.BaseUrl.TrimEnd('/'), model = settings.Provider.Model,
                thinking = settings.Provider.ThinkingMode ?? "auto", effort = settings.Provider.ReasoningEffort ?? "low",
                output_limit = settings.Provider.MaxOutputTokens,
                target = settings.TargetLanguage, scene = settings.Scene,
                detail = settings.Scene == "通用" ? "" : settings.SceneDetail,
                gameType = settings.Scene == "游戏" ? settings.GameTextType : "", style = settings.Style,
                learning = settings.Learning.Where(x => x.Enabled).Select(x => new { x.Id, x.Instruction }).ToArray()
            };
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(state)))).Replace("-", "");
        }
    }
    public sealed class TextPiece
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public string Text { get; set; }
        public bool IsWord { get; set; }
        public string Key { get { return Start + ":" + Length; } }
    }
    public static class TextTools
    {
        private static readonly Regex Words = new Regex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*|\p{N}+(?:[.,]\p{N}+)*", RegexOptions.Compiled);
        public static List<TextPiece> Pieces(string source)
        {
            var pieces = new List<TextPiece>(); int cursor = 0;
            foreach (Match match in Words.Matches(source)) {
                if (match.Index > cursor) pieces.Add(new TextPiece { Start = cursor, Length = match.Index - cursor, Text = source.Substring(cursor, match.Index - cursor) });
                pieces.Add(new TextPiece { Start = match.Index, Length = match.Length, Text = match.Value, IsWord = true });
                cursor = match.Index + match.Length;
            }
            if (cursor < source.Length) pieces.Add(new TextPiece { Start = cursor, Length = source.Length - cursor, Text = source.Substring(cursor) });
            return pieces;
        }
        public static bool IsWordInput(string source) { return source.Length <= 80 && Pieces(source).Count(p => p.IsWord) == 1; }
        public static string ValidateInput(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) throw new UserError("empty", "没有取得文字。请先选中或复制，再按快捷键。");
            if (text.Length > 6000) throw new UserError("length", "这段文字超过 6000 字符。请缩小选取范围后再试。");
            return text;
        }
    }
    public sealed class UserError : Exception
    {
        public string Code { get; private set; }
        public UserError(string code, string message) : base(message) { Code = code; }
    }
    public sealed class RequestGate
    {
        private long version;
        public long Next() { return System.Threading.Interlocked.Increment(ref version); }
        public bool IsCurrent(long requestVersion) { return System.Threading.Interlocked.Read(ref version) == requestVersion; }
    }
}
