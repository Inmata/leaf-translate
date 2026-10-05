using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Leaf;

// Isolated fault-injection checks for settings/history commits. Never touches user data:
// every case uses a fresh GUID directory below the test temp folder.
public static class StorageRegressionTests
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++; Console.WriteLine("PASS " + label);
    }
    public static async Task<int> Run(string folder)
    {
        int start = assertions;
        await SettingsCommitFailures(folder);
        await FileFaultMatrix(folder);
        await TransactionRecovery(folder);
        await CredentialAndPatchScenarios(folder);
        await QueueScenarios();
        await StructureScenarios(folder);
        return assertions - start;
    }
    // WPF-side checks; must run inside the ApplicationTests application/dispatcher.
    public static async Task<int> RunUi(string folder)
    {
        int start = assertions;
        await UiRecoveryAndCredentials(folder);
        return assertions - start;
    }
    private static string NewDirectory(string folder, string name)
    {
        string directory = Path.Combine(folder, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private static Settings FixtureSettings(string id, string url, string model)
    {
        var settings = Settings.Defaults();
        settings.Providers = new List<ProviderProfile> {
            new ProviderProfile { Id = id, Name = "Fixture", BaseUrl = url, Model = model }
        };
        settings.ProviderId = id;
        return settings;
    }
    private static async Task<LocalStore> SeedStore(string directory, Settings settings)
    {
        var store = new LocalStore(directory);
        await store.SaveSettingsAsync(SettingsUpdate.Full(settings));
        var record = TranslationRecord.Create("fixture source text", "剪贴板", store.Settings);
        record.Translation = "夹具译文"; record.Completed = true;
        store.Save(record);
        await store.FlushAsync();
        return store;
    }
    private static bool DirectoryIsClean(string directory)
    {
        return Directory.GetFiles(directory).All(path =>
            !path.EndsWith(".new", StringComparison.Ordinal) &&
            !path.EndsWith(".old", StringComparison.Ordinal) &&
            Path.GetFileName(path) != AtomicFileBatch.MarkerName);
    }

    private static async Task SettingsCommitFailures(string folder)
    {
        string directory = NewDirectory(folder, "locked");
        var settings = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        settings.HistoryLimit = 180;
        var store = new LocalStore(directory);
        store.SaveSettings(settings);
        var seeded = TranslationRecord.Create("fixture history source", "剪贴板", store.Settings);
        seeded.Translation = "夹具译文"; seeded.Completed = true; store.Save(seeded);
        var next = Json.Copy(store.Settings);
        next.Provider.BaseUrl = "https://new-api.invalid/v1";
        next.HistoryLimit = 181;
        using (var locked = File.Open(Path.Combine(directory, "history.json"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            bool failed = false;
            try { await store.SaveSettingsAsync(SettingsUpdate.Full(next)); }
            catch (UserError error) { failed = error.Code == "storage"; }
            Check(failed, "Locked history rejects the grouped settings change");
        }
        Check(store.Settings.Provider.BaseUrl == "https://old-api.invalid/v1",
            "Failed settings commit leaves the in-memory endpoint unchanged");
        var reloaded = new LocalStore(directory);
        Check(reloaded.Settings.Provider.BaseUrl == "https://old-api.invalid/v1",
            "Failed commit leaves the persisted endpoint unchanged");
        Check(reloaded.Settings.HistoryLimit == 180 && reloaded.History("fixture history").Count == 1,
            "Failed commit leaves the persisted history untouched");
    }

    private static async Task FileFaultMatrix(string folder)
    {
        await FileFault(folder, "settings-replace", "Replace", 1, true);
        await FileFault(folder, "history-replace", "Replace", 2, true);
        await FileFault(folder, "prepare-marker", "Write", 3, false);
        await FileFault(folder, "completion-marker", "Write", 4, true);
        await RollbackFailureKeepsMarker(folder);
        await PersistentFaultDoesNotFakeSuccess(folder);
    }
    private static async Task FileFault(string folder, string name, string operation, int at, bool expectsApplied)
    {
        string directory = NewDirectory(folder, name);
        var settings = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        settings.HistoryLimit = 180;
        await SeedStore(directory, settings);
        var files = new FaultingFiles();
        var store = new LocalStore(directory, files);
        var next = Json.Copy(store.Settings);
        next.Provider.BaseUrl = "https://new-api.invalid/v1";
        next.TargetLanguage = "日本語";
        next.HistoryLimit = 181;
        files.MatchingCalls = 0; files.FailOperation = operation; files.FailAt = at;
        bool applied = false, rolledBack = false, failed = false;
        try {
            await store.CommitSettingsAsync(SettingsUpdate.Full(next),
                (oldSettings, candidateSettings) => { applied = true; },
                (oldSettings, candidateSettings) => { rolledBack = true; });
        } catch (UserError error) { failed = error.Code == "storage"; }
        Check(failed, name + ": the injected file failure rejects the commit");
        Check(rolledBack, name + ": external rollback runs after a failed commit");
        Check(applied == expectsApplied, name + ": external side effects apply only after staging succeeds");
        Check(store.Settings.Provider.BaseUrl == "https://old-api.invalid/v1" && store.Settings.TargetLanguage == "中文",
            name + ": failed commit leaves settings memory unchanged");
        var reloaded = new LocalStore(directory);
        Check(reloaded.Settings.Provider.BaseUrl == "https://old-api.invalid/v1" && reloaded.Settings.HistoryLimit == 180,
            name + ": failed commit leaves the persisted settings unchanged");
        Check(reloaded.History("fixture source").Count == 1, name + ": failed commit leaves the persisted history unchanged");
        Check(DirectoryIsClean(directory), name + ": rollback removes staging files and the transaction marker");
    }
    private static async Task RollbackFailureKeepsMarker(string folder)
    {
        string directory = NewDirectory(folder, "rollback-failure");
        var settings = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        await SeedStore(directory, settings);
        var files = new PersistentFaultingFiles { FailOperation = "Replace" };
        var store = new LocalStore(directory, files);
        var next = Json.Copy(store.Settings);
        next.Provider.BaseUrl = "https://new-api.invalid/v1";
        bool failed = false;
        try { await store.SaveSettingsAsync(SettingsUpdate.Full(next)); }
        catch (UserError) { failed = true; }
        Check(failed && store.RecoveryBlocked, "A failed rollback blocks further commits instead of pretending success");
        Check(files.Exists(Path.Combine(directory, AtomicFileBatch.MarkerName)),
            "A failed rollback keeps the prepared marker for the next startup");
        var recovered = new LocalStore(directory);
        Check(recovered.Settings.Provider.BaseUrl == "https://old-api.invalid/v1" && !recovered.RecoveryBlocked,
            "Restarting with healthy files recovers the prepared commit");
        Check(DirectoryIsClean(directory), "Startup recovery cleans staging files after restoring backups");
    }
    private static async Task PersistentFaultDoesNotFakeSuccess(string folder)
    {
        var queue = new StoreQueue();
        bool first = true;
        bool failed = false;
        try {
            await queue.Enqueue(() => { if (first) { first = false; throw new IOException("persistent fixture"); } });
        } catch (IOException) { failed = true; }
        Check(failed && queue.FailureCount == 1, "Persistent faults stay tracked instead of being swallowed");
    }

    private static async Task TransactionRecovery(string folder)
    {
        await PreparedTransactionRecovers(folder);
        await PreparedNewTargetRecovers(folder);
        await CommittedTransactionKeepsNewData(folder);
        await InvalidMarkerIsRefused(folder);
    }
    private static async Task PreparedTransactionRecovers(string folder)
    {
        string directory = NewDirectory(folder, "prepared");
        var settings = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        await SeedStore(directory, settings);
        var next = Json.Copy(new LocalStore(directory).Settings);
        next.TargetLanguage = "日本語";
        File.WriteAllText(Path.Combine(directory, "settings.json.new"), Json.Write(next));
        File.Copy(Path.Combine(directory, "settings.json"), Path.Combine(directory, "settings.json.old"), true);
        File.WriteAllText(Path.Combine(directory, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared",
            Targets = new[] { new { Name = "settings.json", Existed = true } }
        }));
        var recovered = new LocalStore(directory);
        Check(recovered.Settings.TargetLanguage == "中文", "A prepared commit is rolled back before settings are read");
        Check(DirectoryIsClean(directory), "Prepared recovery removes backups and the marker");
    }
    private static Task PreparedNewTargetRecovers(string folder)
    {
        string directory = NewDirectory(folder, "prepared-new");
        File.WriteAllText(Path.Combine(directory, "settings.json.new"), Json.Write(FixtureSettings("fixture", "https://new-api.invalid/v1", "m")));
        File.WriteAllText(Path.Combine(directory, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared",
            Targets = new[] { new { Name = "settings.json", Existed = false } }
        }));
        var recovered = new LocalStore(directory);
        Check(recovered.Settings.ProviderId == "zhipu", "A prepared commit for a previously missing file falls back to defaults");
        Check(!File.Exists(Path.Combine(directory, "settings.json")), "Recovery deletes targets that did not exist before the commit");
        Check(DirectoryIsClean(directory), "Prepared recovery for a new target cleans staging files");
        return Task.FromResult(0);
    }
    private static async Task CommittedTransactionKeepsNewData(string folder)
    {
        string directory = NewDirectory(folder, "committed");
        var settings = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        await SeedStore(directory, settings);
        var next = Json.Copy(new LocalStore(directory).Settings);
        next.TargetLanguage = "日本語";
        File.WriteAllText(Path.Combine(directory, "settings.json"), Json.Write(next));
        File.WriteAllText(Path.Combine(directory, "settings.json.old"), "stale backup");
        File.WriteAllText(Path.Combine(directory, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "committed",
            Targets = new[] { new { Name = "settings.json", Existed = true } }
        }));
        var recovered = new LocalStore(directory);
        Check(recovered.Settings.TargetLanguage == "日本語", "A committed commit keeps the new data through restart");
        Check(DirectoryIsClean(directory), "Committed recovery only cleans staging files");
    }
    private static Task InvalidMarkerIsRefused(string folder)
    {
        string directory = NewDirectory(folder, "invalid-marker");
        File.WriteAllText(Path.Combine(directory, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared",
            Targets = new[] { new { Name = "../outside.json", Existed = true } }
        }));
        var store = new LocalStore(directory);
        Check(store.RecoveryBlocked, "A marker outside the fixed file whitelist blocks instead of writing outside the store");
        Check(File.Exists(Path.Combine(directory, AtomicFileBatch.MarkerName)), "A refused marker is preserved for inspection");
        return Task.FromResult(0);
    }

    private static async Task CredentialAndPatchScenarios(string folder)
    {
        var profileA = new ProviderProfile { Id = "fixture", Name = "A", BaseUrl = "https://api.example.invalid/v1", Model = "m" };
        var profileB = new ProviderProfile { Id = "fixture", Name = "A", BaseUrl = "https://other.example.invalid/v2", Model = "m" };
        Check(Credentials.ScopedId(profileA) == Credentials.ScopedId(new ProviderProfile {
            Id = "fixture", BaseUrl = "https://api.example.invalid/v1/", Model = "other" }),
            "Scoped credential ids normalize the endpoint and ignore the model");
        Check(Credentials.ScopedId(profileA) != Credentials.ScopedId(profileB),
            "Different endpoints bind to different credential targets");
        Check(Credentials.ScopedId(profileA).StartsWith("fixture/endpoint/", StringComparison.Ordinal),
            "Scoped credential ids start from the provider id");

        string directory = NewDirectory(folder, "external-failure");
        var settings = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        await SeedStore(directory, settings);
        var store = new LocalStore(directory);
        var credentials = new FakeCredentialProfiles();
        var oldProfile = store.Settings.Provider;
        credentials.Save(oldProfile, "old-key-fixture");
        var newProfile = new ProviderProfile { Id = "fixture", Name = "Fixture", BaseUrl = "https://new-api.invalid/v1", Model = "fixture-model" };
        await store.CommitSettingsAsync(SettingsUpdate.Provider(newProfile),
            (committed, candidate) => credentials.Save(candidate.Provider, "new-key-fixture"),
            (committed, candidate) => { credentials.Delete(newProfile); credentials.Save(oldProfile, "old-key-fixture"); });
        Check(credentials.Read(newProfile) == "new-key-fixture" && credentials.Read(oldProfile) == "old-key-fixture",
            "Endpoint changes keep the old target and add the new one");
        Check(store.Settings.Provider.BaseUrl == "https://new-api.invalid/v1", "Endpoint patch commits the candidate");

        string failing = NewDirectory(folder, "external-rollback");
        var failingSettings = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        await SeedStore(failing, failingSettings);
        var failingStore = new LocalStore(failing);
        var failingCredentials = new FakeCredentialProfiles();
        var failingOld = failingStore.Settings.Provider;
        failingCredentials.Save(failingOld, "old-key-fixture");
        failingCredentials.FailNextSave = true;
        bool failed = false, rolled = false;
        try {
            await failingStore.CommitSettingsAsync(SettingsUpdate.Provider(newProfile),
                (committed, candidate) => failingCredentials.Save(candidate.Provider, "new-key-fixture"),
                (committed, candidate) => { rolled = true; });
        } catch (UserError error) { failed = error.Code == "credentials" || error.Code == "storage"; }
        Check(failed && rolled, "A credential failure rejects the commit and runs the external rollback");
        Check(failingStore.Settings.Provider.BaseUrl == "https://old-api.invalid/v1",
            "Credential failure leaves the committed endpoint unchanged");
        Check(failingCredentials.Read(newProfile) == "" && failingCredentials.Read(failingOld) == "old-key-fixture",
            "Credential failure never binds the old key to the new endpoint");
        Check(new LocalStore(failing).Settings.Provider.BaseUrl == "https://old-api.invalid/v1",
            "Credential failure leaves the persisted endpoint unchanged");

        string patched = NewDirectory(folder, "patches");
        var patchStore = new LocalStore(patched);
        var preferences = Json.Copy(patchStore.Settings);
        preferences.TargetLanguage = "日本語";
        preferences.Scene = "书籍"; preferences.SceneDetail = "Patch book";
        preferences.ClipboardMode = true; preferences.HistoryLimit = 180;
        var placement = new WindowPlacement { X = 12, Y = 34, Width = 456, Height = 620, Screen = "patch" };
        var first = patchStore.SaveSettingsAsync(SettingsUpdate.Preferences(preferences));
        var second = patchStore.SaveSettingsAsync(SettingsUpdate.Model("zhipu", "patch-model"));
        var third = patchStore.SaveSettingsAsync(SettingsUpdate.Placement(placement));
        await Task.WhenAll(first, second, third);
        await patchStore.FlushAsync();
        var built = new LocalStore(patched).Settings;
        Check(built.TargetLanguage == "日本語" && built.Scene == "书籍" && built.HistoryLimit == 180 && built.ClipboardMode,
            "Preference patches apply against the latest committed state");
        Check(built.Provider.Model == "patch-model" && built.ProviderId == "zhipu",
            "A queued model patch survives a later placement patch");
        Check(built.Placement.X == 12 && built.Placement.Screen == "patch",
            "A queued placement patch survives an earlier model patch");
    }

    private static async Task QueueScenarios()
    {
        var queue = new StoreQueue();
        var order = new List<int>();
        var first = queue.Enqueue(() => order.Add(1));
        Exception captured = null;
        var failing = queue.Enqueue(() => { order.Add(2); throw new IOException("fixture"); });
        var after = queue.Enqueue(() => order.Add(3));
        try { await failing; } catch (Exception error) { captured = error; }
        await first; await after;
        Check(captured is IOException, "The enqueuing caller sees the original failure");
        Check(order.SequenceEqual(new[] { 1, 2, 3 }), "A failed action does not skip later queued work");
        bool reported = false;
        try { await queue.FlushAsync(); } catch (UserError error) { reported = error.Code == "storage"; }
        Check(reported, "Flush reports unresolved background failures");
        var laterSuccess = queue.Enqueue(() => order.Add(4));
        await laterSuccess;
        reported = false;
        try { await queue.FlushAsync(); } catch (UserError) { reported = true; }
        Check(reported, "A later success does not silently clear an earlier failure");
        long through = queue.FailureWatermark;
        queue.AcknowledgeFailures(through);
        await queue.FlushAsync();
        Check(order.SequenceEqual(new[] { 1, 2, 3, 4 }) && queue.FailureCount == 0,
            "Acknowledged failures let Flush succeed without losing later work");
    }

    private static Task StructureScenarios(string folder)
    {
        string nullProvider = NewDirectory(folder, "null-provider");
        File.WriteAllText(Path.Combine(nullProvider, "settings.json"), "{\"Version\":3,\"Providers\":[null],\"HistoryEnabled\":true}");
        var recoveredNull = new LocalStore(nullProvider);
        Check(recoveredNull.Settings.Provider != null, "Providers:[null] cannot crash startup");
        Check(Directory.GetFiles(nullProvider, "settings.json.corrupt-*").Length == 1,
            "Semantically invalid settings are backed up before defaults");

        string emptyId = NewDirectory(folder, "empty-provider");
        File.WriteAllText(Path.Combine(emptyId, "settings.json"),
            "{\"Version\":3,\"ProviderId\":\"\",\"Providers\":[{\"Id\":\"\",\"Name\":\"\",\"BaseUrl\":\"\",\"Model\":\"\"}]}");
        var recoveredEmpty = new LocalStore(emptyId);
        Check(Directory.GetFiles(emptyId, "settings.json.corrupt-*").Length == 1, "An empty provider id is treated as corruption");
        Check(recoveredEmpty.Settings.Provider != null && recoveredEmpty.Settings.Provider.Id == "zhipu",
            "Empty provider ids fall back to default settings");

        string duplicate = NewDirectory(folder, "duplicate-provider");
        File.WriteAllText(Path.Combine(duplicate, "settings.json"),
            "{\"Version\":3,\"ProviderId\":\"x\",\"Providers\":[{\"Id\":\"x\",\"Name\":\"A\",\"BaseUrl\":\"\",\"Model\":\"\"},{\"Id\":\"x\",\"Name\":\"B\",\"BaseUrl\":\"\",\"Model\":\"\"}]}");
        var recoveredDuplicate = new LocalStore(duplicate);
        Check(Directory.GetFiles(duplicate, "settings.json.corrupt-*").Length == 1, "Duplicate provider ids are treated as corruption");
        Check(recoveredDuplicate.Settings.Provider != null, "Duplicate provider ids fall back to default settings");

        string nullLearning = NewDirectory(folder, "null-learning");
        File.WriteAllText(Path.Combine(nullLearning, "settings.json"), "{\"Version\":3,\"Learning\":[null]}");
        var recoveredLearning = new LocalStore(nullLearning);
        Check(Directory.GetFiles(nullLearning, "settings.json.corrupt-*").Length == 1, "A null learning entry is treated as corruption");
        Check(recoveredLearning.Settings.Learning.All(x => x != null), "Null learning entries never reach the settings UI");

        string legalCustom = NewDirectory(folder, "unconfigured-custom");
        File.WriteAllText(Path.Combine(legalCustom, "settings.json"),
            "{\"Version\":3,\"ProviderId\":\"custom\",\"Providers\":[{\"Id\":\"custom\",\"Name\":\"自定义兼容服务\",\"BaseUrl\":\"\",\"Model\":\"\"}]}");
        var legal = new LocalStore(legalCustom);
        Check(legal.Settings.Provider.Id == "custom" && legal.Settings.Provider.BaseUrl == "",
            "An unconfigured custom service is legal and not mistaken for corruption");
        Check(Directory.GetFiles(legalCustom, "settings.json.corrupt-*").Length == 0,
            "A legal unconfigured service creates no corruption backup");

        string legacy = NewDirectory(folder, "legacy-fields");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"Version\":1}");
        var migrated = new LocalStore(legacy);
        Check(migrated.Settings.Provider != null && Directory.GetFiles(legacy, "settings.json.corrupt-*").Length == 0,
            "Old settings with missing fields migrate instead of counting as corruption");

        string historyDir = NewDirectory(folder, "history-structure");
        var baseContext = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        var good = TranslationRecord.Create("good source", "剪贴板", baseContext);
        good.Translation = "好"; good.Completed = true; good.CacheKey = null;
        var badTicks = TranslationRecord.Create("bad ticks source", "剪贴板", baseContext);
        badTicks.Translation = "坏"; badTicks.Completed = true; badTicks.UpdatedUtcTicks = 0;
        var nullContext = TranslationRecord.Create("null context source", "剪贴板", baseContext);
        nullContext.Translation = "无"; nullContext.Completed = true; nullContext.Context = null;
        var filtered = TranslationRecord.Create("filtered source", "剪贴板", baseContext);
        filtered.Translation = "过滤"; filtered.Completed = true;
        filtered.Cards = new Dictionary<string, WordCard> {
            { "0:4", new WordCard { word = "good", meaning = "好的", sections = new List<LearningSection> {
                null, new LearningSection { title = "t", content = "c" } } } },
            { "bad", null }
        };
        filtered.Chat = new List<ChatTurn> {
            null, new ChatTurn { Role = "user", Content = "q" },
            new ChatTurn { Role = "assistant", Content = "a" },
            new ChatTurn { Role = "user", Content = "orphan" }
        };
        File.WriteAllText(Path.Combine(historyDir, "history.json"),
            Json.Write(new object[] { good, badTicks, nullContext, filtered }));
        var loaded = new LocalStore(historyDir);
        var kept = loaded.History("");
        Check(kept.Count == 2 && kept.All(r => r.Id == good.Id || r.Id == filtered.Id),
            "Structurally broken history records are dropped individually");
        Check(kept.First(r => r.Id == good.Id).CacheKey == CacheKeys.For("good source", baseContext),
            "A missing cache key is recomputed from the record context");
        Check(kept.First(r => r.Id == filtered.Id).Cards.Count == 1 &&
            kept.First(r => r.Id == filtered.Id).Cards.Values.Single().sections.Count == 1,
            "Null cards and null sections are filtered from loaded history");
        Check(kept.First(r => r.Id == filtered.Id).Chat.Count == 2 &&
            kept.First(r => r.Id == filtered.Id).Chat[0].Role == "user" &&
            kept.First(r => r.Id == filtered.Id).Chat[1].Role == "assistant",
            "Chat keeps only complete user/assistant pairs");
        Check(Directory.GetFiles(historyDir, "history.json.quarantine-*").Length == 1,
            "Partially bad history gets an isolated copy");
        Check(loaded.Warning != null && !loaded.Warning.Contains("good source") && !loaded.Warning.Contains("bad ticks"),
            "Recovery warnings never contain history content");
        Check(File.ReadAllText(Path.Combine(historyDir, "history.json")).Contains("bad ticks source"),
            "The original history file is preserved until the next successful save");
        var normalizeTarget = Settings.Defaults(); normalizeTarget.NormalizeLoaded();
        Check(normalizeTarget.Providers.Count > 0, "NormalizeLoaded accepts and normalizes default settings");
        var tryRecord = TranslationRecord.Create("normalizable source", "剪贴板", Settings.Defaults());
        tryRecord.Completed = true;
        Check(tryRecord.TryNormalizeLoaded(), "A completed record with a valid context normalizes");
        return Task.FromResult(0);
    }

    private static async Task UiRecoveryAndCredentials(string folder)
    {
        string blockedDirectory = NewDirectory(folder, "ui-blocked");
        var seed = new LocalStore(blockedDirectory);
        await seed.SaveSettingsAsync(SettingsUpdate.Full(FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model")));
        var next = Json.Copy(seed.Settings); next.TargetLanguage = "日本語";
        File.WriteAllText(Path.Combine(blockedDirectory, "settings.json.new"), Json.Write(next));
        File.Copy(Path.Combine(blockedDirectory, "settings.json"), Path.Combine(blockedDirectory, "settings.json.old"), true);
        File.WriteAllText(Path.Combine(blockedDirectory, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared", Targets = new[] { new { Name = "settings.json", Existed = true } }
        }));
        var blockedStore = new LocalStore(blockedDirectory, new PersistentFaultingFiles { FailOperation = "Replace" });
        Check(blockedStore.RecoveryBlocked, "UI: a failed recovery is surfaced as a blocked state");
        var handler = new CountingHandler();
        using (var shell = new AppShell(blockedStore, false, new LlmClient(handler))) {
            await shell.TranslateAsync("Blocked sentence", "剪贴板", false);
            Check(handler.Calls == 0, "UI: a blocked recovery refuses new API requests");
            Check(Ui.Get<TextBlock>(shell.Popup, "ErrorText").Text.Contains("重新应用配置"),
                "UI: the blocked state explains that settings must be reapplied");
        }

        string scopeDirectory = NewDirectory(folder, "ui-scope");
        var scopeStore = new LocalStore(scopeDirectory);
        await scopeStore.SaveSettingsAsync(SettingsUpdate.Full(FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model")));
        var fake = new FakeCredentialProfiles();
        var oldProfile = scopeStore.Settings.Provider;
        fake.Save(oldProfile, "old-scope-key");
        using (var shell = new AppShell(scopeStore, false, new LlmClient(), fake)) {
            var newProfile = new ProviderProfile { Id = "fixture", Name = "Fixture", BaseUrl = "https://new-api.invalid/v1", Model = "fixture-model" };
            await shell.ApplySettingsAsync(SettingsUpdate.Provider(newProfile),
                new Dictionary<string, string> { { "fixture", "new-scope-key" } }, new HashSet<string>());
            Check(fake.Read(newProfile) == "new-scope-key", "UI: applying a key binds it to the new endpoint");
            Check(fake.Read(oldProfile) == "", "UI: switching endpoints cleans the unused old target after success");
            var committed = scopeStore.Settings.Provider;
            await shell.ApplySettingsAsync(SettingsUpdate.Model("fixture", "scope-model"),
                new Dictionary<string, string>(), new HashSet<string>());
            Check(fake.Read(committed) == "new-scope-key" && scopeStore.Settings.Provider.Model == "scope-model",
                "UI: a model-only patch keeps the endpoint-bound key");
            fake.FailNextSave = true;
            bool failed = false;
            try {
                await shell.ApplySettingsAsync(SettingsUpdate.Provider(new ProviderProfile {
                    Id = "fixture", Name = "Fixture", BaseUrl = "https://third-api.invalid/v1", Model = "scope-model"
                }), new Dictionary<string, string> { { "fixture", "third-scope-key" } }, new HashSet<string>());
            } catch (UserError) { failed = true; }
            Check(failed && scopeStore.Settings.Provider.BaseUrl == "https://new-api.invalid/v1",
                "UI: a credential failure rolls settings back to the committed endpoint");
            Check(fake.Read(committed) == "new-scope-key", "UI: a credential failure restores the previous key");
        }
    }
    private sealed class CountingHandler : System.Net.Http.HttpMessageHandler
    {
        public int Calls;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++;
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class FaultingFiles : IStoreFiles
    {
        private readonly IStoreFiles inner = new PhysicalStoreFiles();
        public string FailOperation = "Replace";
        public int FailAt = -1;
        public int MatchingCalls;
        public int DelayWritesMs;
        private void Before(string operation)
        {
            if (operation == FailOperation && ++MatchingCalls == FailAt)
                throw new IOException("Fixture file failure");
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
        public void Copy(string source, string destination, bool overwrite)
        { Before("Copy"); inner.Copy(source, destination, overwrite); }
        public void Move(string source, string destination)
        { Before("Move"); inner.Move(source, destination); }
        public void Replace(string source, string destination)
        { Before("Replace"); inner.Replace(source, destination); }
        public void Delete(string path) { Before("Delete"); inner.Delete(path); }
    }
    private sealed class PersistentFaultingFiles : IStoreFiles
    {
        private readonly IStoreFiles inner = new PhysicalStoreFiles();
        public string FailOperation = "Replace";
        private void Before(string operation)
        {
            if (operation == FailOperation) throw new IOException("Persistent fixture failure");
        }
        public bool Exists(string path) { return inner.Exists(path); }
        public long Length(string path) { return inner.Length(path); }
        public string Read(string path) { Before("Read"); return inner.Read(path); }
        public void Write(string path, string payload) { Before("Write"); inner.Write(path, payload); }
        public void Copy(string source, string destination, bool overwrite)
        { Before("Copy"); inner.Copy(source, destination, overwrite); }
        public void Move(string source, string destination) { Before("Move"); inner.Move(source, destination); }
        public void Replace(string source, string destination)
        { Before("Replace"); inner.Replace(source, destination); }
        public void Delete(string path) { Before("Delete"); inner.Delete(path); }
    }
    private sealed class FakeCredentialProfiles : ICredentialProfiles
    {
        private readonly Dictionary<string, string> keys = new Dictionary<string, string>();
        public bool FailNextSave;
        public string Read(ProviderProfile profile)
        {
            string key;
            return keys.TryGetValue(Credentials.ScopedId(profile), out key) ? key : "";
        }
        public void Save(ProviderProfile profile, string key)
        {
            if (FailNextSave) { FailNextSave = false; throw new UserError("credentials", "Fixture failure"); }
            keys[Credentials.ScopedId(profile)] = key;
        }
        public void Delete(ProviderProfile profile) { keys.Remove(Credentials.ScopedId(profile)); }
    }
}
