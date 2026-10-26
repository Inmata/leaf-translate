using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Leaf
{
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

    public sealed class Settings
    {
        public int Version { get; set; }
        public string ProviderId { get; set; }
        public List<ProviderProfile> Providers { get; set; }
        public string TargetLanguage { get; set; }
        public string FontFamilyName { get; set; }
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
                Version = 3, ProviderId = "zhipu", TargetLanguage = "中文", FontFamilyName = "", ClipboardMode = false,
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
            if (FontFamilyName == null) FontFamilyName = "";
            if (string.IsNullOrWhiteSpace(Scene)) Scene = "通用";
            if (string.IsNullOrWhiteSpace(Style)) Style = "自然准确";
            if (string.IsNullOrWhiteSpace(Shortcut)) Shortcut = defaults.Shortcut;
            HistoryLimit = Math.Max(20, Math.Min(1000, HistoryLimit == 0 ? 200 : HistoryLimit));
        }
        // Validates the structure of freshly loaded JSON before Normalize runs. Throws for
        // structurally invalid data so the caller can back it up and restore defaults.
        public void NormalizeLoaded()
        {
            if (Providers != null && Providers.Count > 0) {
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var provider in Providers) {
                    if (provider == null) throw new InvalidDataException("A provider entry is null.");
                    if (string.IsNullOrWhiteSpace(provider.Id)) throw new InvalidDataException("A provider id is empty.");
                    if (!ids.Add(provider.Id)) throw new InvalidDataException("A provider id is duplicated.");
                    provider.Name = provider.Name ?? "";
                    provider.Model = provider.Model ?? "";
                    provider.BaseUrl = provider.BaseUrl ?? "";
                    if (!string.IsNullOrWhiteSpace(provider.BaseUrl)) LlmClient.Endpoint(provider.BaseUrl);
                }
            }
            if (Learning != null) foreach (var option in Learning)
                if (option == null) throw new InvalidDataException("A learning entry is null.");
            if (Presets != null) foreach (var preset in Presets) {
                if (preset == null) throw new InvalidDataException("A preset entry is null.");
                if (preset.Learning == null) throw new InvalidDataException("A preset has no learning list.");
                foreach (var option in preset.Learning)
                    if (option == null) throw new InvalidDataException("A preset learning entry is null.");
            }
            Normalize();
            if (Providers.All(p => p.Id != ProviderId)) ProviderId = Providers[0].Id;
        }
    }
}
