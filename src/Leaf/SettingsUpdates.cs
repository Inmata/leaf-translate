using System;
using System.Collections.Generic;
using System.Linq;

namespace Leaf
{
    // Captures one settings patch. Build runs on the serial store queue against the latest
    // committed settings, so two automatic saves cannot overwrite each other with a stale copy.
    public sealed class SettingsUpdate
    {
        private readonly Func<Settings, Settings> apply;
        private SettingsUpdate(Func<Settings, Settings> update) { apply = update; }

        public Settings Build(Settings latest)
        {
            var result = apply(Json.Copy(latest));
            result.Normalize();
            return result;
        }

        public static SettingsUpdate Preferences(Settings value)
        {
            var captured = Json.Copy(value);
            return new SettingsUpdate(s => {
                var p = Json.Copy(captured);
                s.TargetLanguage = p.TargetLanguage;
                s.Scene = p.Scene; s.SceneDetail = p.SceneDetail; s.SceneDetails = p.SceneDetails;
                s.GameTextType = p.GameTextType; s.Style = p.Style;
                s.Learning = p.Learning; s.Presets = p.Presets;
                s.ClipboardMode = p.ClipboardMode; s.FocusInputOnShortcut = p.FocusInputOnShortcut;
                s.Shortcut = p.Shortcut; s.AutoStart = p.AutoStart;
                s.HistoryEnabled = p.HistoryEnabled; s.HistoryLimit = p.HistoryLimit;
                return s;
            });
        }

        public static SettingsUpdate Provider(ProviderProfile value)
        {
            var captured = Json.Copy(value);
            return new SettingsUpdate(s => {
                var p = Json.Copy(captured); LlmClient.ValidateProfile(p);
                int index = s.Providers.FindIndex(x => x.Id == p.Id);
                if (index < 0) s.Providers.Add(p); else s.Providers[index] = p;
                s.ProviderId = p.Id; return s;
            });
        }

        public static SettingsUpdate Model(string id, string model)
        {
            return new SettingsUpdate(s => {
                var p = s.Providers.First(x => x.Id == id);
                p.Model = model; s.ProviderId = id;
                if (LlmClient.IsGlm53(p) && p.ThinkingMode == "disabled")
                    p.ThinkingMode = "auto";
                LlmClient.ValidateProfile(p);
                return s;
            });
        }

        public static SettingsUpdate Placement(WindowPlacement value)
        {
            var captured = Json.Copy(value);
            return new SettingsUpdate(s => { s.Placement = Json.Copy(captured); return s; });
        }

        // Compatibility entry for imperative tests and the synchronous wrapper. UI flows
        // always use the scoped factories above.
        public static SettingsUpdate Full(Settings value)
        {
            var captured = Json.Copy(value);
            return new SettingsUpdate(s => Json.Copy(captured));
        }
    }
}
