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
    public static class ConversationContext
    {
        // Keeps the learning language/context and the single related provider, dropping
        // presets, placement, startup and other unrelated runtime preferences.
        public static Settings Snapshot(Settings source)
        {
            source = source ?? Settings.Defaults();
            ProviderProfile provider = null;
            if (source.Providers != null) provider = source.Providers.FirstOrDefault(p => p.Id == source.ProviderId);
            if (provider == null && source.Providers != null && source.Providers.Count > 0) provider = source.Providers[0];
            if (provider == null) provider = Settings.Defaults().Provider;
            return new Settings {
                Version = 3,
                ProviderId = provider.Id,
                Providers = new List<ProviderProfile> { Json.Copy(provider) },
                TargetLanguage = source.TargetLanguage,
                Scene = source.Scene, SceneDetail = source.SceneDetail,
                SceneDetails = new Dictionary<string, string>(),
                GameTextType = source.GameTextType, Style = source.Style,
                Learning = (source.Learning ?? new List<LearningOption>())
                    .Where(option => option != null).Select(Json.Copy).ToList(),
                Presets = new List<LearningPreset>(),
                Placement = null
            };
        }
    }
}
