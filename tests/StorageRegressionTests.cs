using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
        await ByteLimitRegression(folder);
        await HistoryScenarios(folder);
        await HistoryBarrierScenarios(folder);
        await HistoryRetryScenarios(folder);
        return assertions - start;
    }
    // WPF-side checks; must run inside the ApplicationTests application/dispatcher.
    public static async Task<int> RunUi(string folder)
    {
        int start = assertions;
        await UiRecoveryAndCredentials(folder);
        await UiHeartbeat(folder);
        await UiHistoryWindow(folder);
        return assertions - start;
    }
    private static async Task UiHistoryWindow(string folder)
    {
        string directory = NewDirectory(folder, "ui-history");
        var store = new LocalStore(directory);
        await store.SaveAsync(Completed("ui history source", store.Settings), store.HistoryEpoch);
        await store.FlushAsync();
        using (var shell = new AppShell(store, false, new LlmClient())) {
            var window = new HistoryWindow(shell);
            Check(Ui.Get<ListBox>(window.Window, "HistoryList").Items.Count == 1,
                "UI: the history window lists the saved record");
            Ui.Get<Button>(window.Window, "DeleteHistory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await shell.WaitForPersistenceAsync();
            Check(store.History("").Count == 0 && new LocalStore(directory).History("").Count == 0,
                "UI: deleting from history waits for the store before updating memory and disk");
            window.Window.Close();
            await shell.WaitForSettingsAsync();
        }
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
        var retryQueue = new StoreQueue();
        try { await retryQueue.Enqueue(() => { throw new IOException("original fixture"); }); } catch { }
        try { await retryQueue.EnqueueRetry(() => { throw new IOException("retry fixture"); }); } catch { }
        Check(retryQueue.FailureCount == 1, "A failed replay does not add a duplicate failure entry");
    }

    private static TranslationRecord Completed(string source, Settings context)
    {
        var record = TranslationRecord.Create(source, "剪贴板", context);
        record.Translation = "译文 " + source; record.Completed = true;
        return record;
    }
    private static async Task HistoryScenarios(string folder)
    {
        var settings = Settings.Defaults();
        var payload = HistorySnapshots.Encode(new List<TranslationRecord> {
            Completed("plain english", settings),
            Completed("中文字符", settings),
            Completed("emoji 😀 tail", settings)
        }, 10);
        Check(payload.Utf8Bytes == Encoding.UTF8.GetByteCount(payload.Json),
            "Encode reports the real UTF-8 byte count");
        Check(payload.Utf8Bytes > payload.Json.Length, "UTF-8 byte counting exceeds UTF-16 length for non-ASCII content");
        Check(payload.Json.Contains("😀") && payload.Records.Count == 3,
            "Encode keeps emoji and every record inside the limit");

        string onceDirectory = NewDirectory(folder, "encode-once");
        var onceStore = new LocalStore(onceDirectory);
        long before = HistorySnapshots.EncodeCalls;
        await onceStore.SaveAsync(Completed("linear once", onceStore.Settings), onceStore.HistoryEpoch);
        await onceStore.FlushAsync();
        Check(HistorySnapshots.EncodeCalls - before == 1, "Each history save encodes the candidate list exactly once");

        string bigDirectory = NewDirectory(folder, "history-200");
        var bigStore = new LocalStore(bigDirectory);
        var bigSettings = Json.Copy(bigStore.Settings); bigSettings.HistoryLimit = 1000;
        await bigStore.SaveSettingsAsync(SettingsUpdate.Full(bigSettings));
        var recordSet = new List<TranslationRecord>();
        for (int i = 0; i < 200; i++) recordSet.Add(LongRecord(i));
        var saveWatch = Stopwatch.StartNew();
        foreach (var record in recordSet) await bigStore.SaveAsync(record, bigStore.HistoryEpoch);
        await bigStore.FlushAsync();
        saveWatch.Stop();
        Console.WriteLine("MEASURE history-200 sequential saves: " + saveWatch.ElapsedMilliseconds + " ms");
        var ordered = bigStore.History("");
        Check(ordered.Count > 20 && ordered.Count <= 200, "A 200-record sample stays inside the retention bounds");
        Check(ordered.Select(r => r.Id).Distinct().Count() == ordered.Count, "Ordered history has no duplicate ids");
        Check(!ordered.Skip(1).Where((r, i) => r.UpdatedUtcTicks > ordered[i].UpdatedUtcTicks).Any(),
            "History is ordered by the newest update time");
        long historyLength = new FileInfo(Path.Combine(bigDirectory, "history.json")).Length;
        Check(historyLength <= HistorySnapshots.SoftLimit, "A 200-record history file obeys the UTF-8 soft limit");
        HistorySnapshots.ResetEncodeCalls();
        var thousand = new List<TranslationRecord>();
        for (int i = 0; i < 1000; i++) thousand.Add(LongRecord(100000 + i));
        var encodeWatch = Stopwatch.StartNew();
        var linear = HistorySnapshots.Encode(thousand, 1000);
        encodeWatch.Stop();
        Console.WriteLine("MEASURE history-1000 encode: " + encodeWatch.ElapsedMilliseconds + " ms for " +
            linear.Utf8Bytes + " bytes in " + linear.Records.Count + " records");
        Check(HistorySnapshots.EncodeCalls == 1, "A 1000-record trim serializes the list once");
        Check(linear.Records.Count >= 1 && linear.Utf8Bytes <= HistorySnapshots.SoftLimit && linear.Utf8Bytes == Encoding.UTF8.GetByteCount(linear.Json),
            "The 1000-record encode caps at the soft limit with honest byte counts");
    }
    private static TranslationRecord LongRecord(int index)
    {
        var record = TranslationRecord.Create(index.ToString() + new string('a', 2000), "剪贴板", Settings.Defaults());
        record.Completed = true; record.Translation = new string('中', 2000);
        for (int i = 0; i < 24; i++) record.Chat.Add(new ChatTurn {
            Role = i % 2 == 0 ? "user" : "assistant", Content = new string('字', 600), Topic = "原句"
        });
        return record;
    }
    private static async Task HistoryBarrierScenarios(string folder)
    {
        string deleteDirectory = NewDirectory(folder, "barrier-delete");
        var deleteStore = new LocalStore(deleteDirectory);
        var deleted = Completed("barrier A", deleteStore.Settings);
        await deleteStore.SaveAsync(deleted, deleteStore.HistoryEpoch);
        await deleteStore.FlushAsync();
        await deleteStore.DeleteAsync(deleted.Id);
        await deleteStore.SaveAsync(deleted, deleteStore.HistoryEpoch);
        await deleteStore.FlushAsync();
        Check(deleteStore.History("").All(r => r.Id != deleted.Id), "A late save cannot resurrect a deleted record");
        Check(new LocalStore(deleteDirectory).History("").Count == 0, "The delete barrier is persisted across restart");

        string clearDirectory = NewDirectory(folder, "barrier-clear");
        var clearStore = new LocalStore(clearDirectory);
        var cleared = Completed("clear A", clearStore.Settings);
        await clearStore.SaveAsync(cleared, clearStore.HistoryEpoch);
        await clearStore.FlushAsync();
        await clearStore.ClearAsync();
        var fresh = Completed("clear B", clearStore.Settings);
        await clearStore.SaveAsync(fresh, clearStore.HistoryEpoch);
        await clearStore.FlushAsync();
        var afterClear = clearStore.History("");
        Check(afterClear.Count == 1 && afterClear[0].Id == fresh.Id,
            "After a clear only the new epoch's successful record remains");

        string disabledDirectory = NewDirectory(folder, "barrier-disabled");
        var disabledStore = new LocalStore(disabledDirectory);
        var beforeDisable = Completed("before disable", disabledStore.Settings);
        await disabledStore.SaveAsync(beforeDisable, disabledStore.HistoryEpoch);
        await disabledStore.FlushAsync();
        long oldEpoch = disabledStore.HistoryEpoch;
        var disabled = Json.Copy(disabledStore.Settings); disabled.HistoryEnabled = false;
        await disabledStore.SaveSettingsAsync(SettingsUpdate.Preferences(disabled));
        var enabled = Json.Copy(disabledStore.Settings); enabled.HistoryEnabled = true;
        await disabledStore.SaveSettingsAsync(SettingsUpdate.Preferences(enabled));
        var late = Completed("late after re-enable", disabledStore.Settings);
        await disabledStore.SaveAsync(late, oldEpoch);
        await disabledStore.FlushAsync();
        Check(new LocalStore(disabledDirectory).History("").Count == 0,
            "Re-enabling history cannot revive a snapshot captured before it was disabled");
        var freshAfterEnable = Completed("fresh after re-enable", disabledStore.Settings);
        await disabledStore.SaveAsync(freshAfterEnable, disabledStore.HistoryEpoch);
        await disabledStore.FlushAsync();
        var restarted = new LocalStore(disabledDirectory).History("");
        Check(restarted.Count == 1 && restarted[0].Id == freshAfterEnable.Id,
            "Only new successful results persist after history is re-enabled");

        string snapshotDirectory = NewDirectory(folder, "snapshot-isolation");
        var snapshotStore = new LocalStore(snapshotDirectory);
        var queued = TranslationRecord.Create("snapshot source", "剪贴板", snapshotStore.Settings);
        queued.Translation = "original translation"; queued.Completed = true;
        var save = snapshotStore.SaveAsync(queued, snapshotStore.HistoryEpoch);
        queued.Translation = "edited translation"; queued.Draft = "edited draft";
        queued.Chat.Add(new ChatTurn { Role = "user", Content = "later question" });
        queued.Context.Scene = "later scene";
        queued.Context.Placement = new WindowPlacement { X = 99, Y = 99 };
        queued.Context.Presets.Add(new LearningPreset { Name = "later preset" });
        await save; await snapshotStore.FlushAsync();
        var saved = snapshotStore.History("snapshot source").Single();
        Check(saved.Translation == "original translation" && saved.Draft == "" && saved.Chat.Count == 0,
            "A queued snapshot ignores later translation, draft and chat edits");
        Check(saved.Context.Scene == "通用" && saved.Context.Placement == null && saved.Context.Presets.Count == 0,
            "A snapshot keeps only the learning context and drops presets and placement");
        saved.Translation = "mutated copy";
        saved.Chat.Add(new ChatTurn { Role = "user", Content = "injected" });
        saved.Context.Scene = "mutated scene";
        var defensive = snapshotStore.History("snapshot source").Single();
        Check(defensive.Translation == "original translation" && defensive.Chat.Count == 0 && defensive.Context.Scene == "通用",
            "History and Find readers receive defensive copies");
    }
    private static async Task HistoryRetryScenarios(string folder)
    {
        string retryDirectory = NewDirectory(folder, "retry-save");
        var files = new FaultingFiles();
        var retryStore = new LocalStore(retryDirectory, files);
        var seed = Completed("retry seed", retryStore.Settings);
        await retryStore.SaveAsync(seed, retryStore.HistoryEpoch);
        await retryStore.FlushAsync();
        var failedRecord = Completed("retry A", retryStore.Settings);
        files.MatchingCalls = 0; files.FailOperation = "Replace"; files.FailAt = 1;
        bool failedSave = false;
        try { await retryStore.SaveAsync(failedRecord, retryStore.HistoryEpoch); }
        catch (UserError error) { failedSave = error.Code == "storage"; }
        var laterRecord = Completed("retry B", retryStore.Settings);
        await retryStore.SaveAsync(laterRecord, retryStore.HistoryEpoch);
        bool unresolved = false;
        try { await retryStore.FlushAsync(); } catch (UserError) { unresolved = true; }
        Check(failedSave && unresolved, "A failed save stays reported even after a later save succeeds");
        Check(retryStore.History("").Count == 2, "The failed save keeps its in-memory snapshot while the later save persists");
        await retryStore.RetryFailedWritesAsync();
        await retryStore.FlushAsync();
        var persisted = new LocalStore(retryDirectory).History("");
        Check(persisted.Count == 3 && persisted.Any(r => r.Id == failedRecord.Id) && persisted.Any(r => r.Id == laterRecord.Id),
            "RetryFailedWritesAsync replays the kept snapshot without losing later work");
        Check(retryStore.History("").Count == 3, "A successful retry publishes the recovered records to memory");

        string deleteDirectory = NewDirectory(folder, "retry-delete");
        var deleteFiles = new FaultingFiles();
        var deleteStore = new LocalStore(deleteDirectory, deleteFiles);
        var deleteRecord = Completed("delete barrier", deleteStore.Settings);
        await deleteStore.SaveAsync(deleteRecord, deleteStore.HistoryEpoch);
        await deleteStore.FlushAsync();
        deleteFiles.MatchingCalls = 0; deleteFiles.FailOperation = "Replace"; deleteFiles.FailAt = 1;
        bool deleteFailed = false;
        try { await deleteStore.DeleteAsync(deleteRecord.Id); }
        catch (UserError error) { deleteFailed = error.Code == "storage"; }
        Check(deleteFailed && deleteStore.History("").Count == 1, "A failed delete keeps the in-memory barrier and record");
        Check(new LocalStore(deleteDirectory).History("").Count == 1, "A failed delete still shows the old disk record");
        await deleteStore.RetryFailedWritesAsync();
        await deleteStore.FlushAsync();
        Check(deleteStore.History("").Count == 0 && new LocalStore(deleteDirectory).History("").Count == 0,
            "A retried delete removes the record only after it succeeds");

        string oversizeDirectory = NewDirectory(folder, "oversize");
        var oversizeStore = new LocalStore(oversizeDirectory);
        var huge = TranslationRecord.Create("huge single record", "剪贴板", oversizeStore.Settings);
        huge.Translation = new string('a', 34000000); huge.Completed = true;
        bool oversize = false;
        try { await oversizeStore.SaveAsync(huge, oversizeStore.HistoryEpoch); }
        catch (UserError error) { oversize = error.Code == "length"; }
        Check(oversize, "A single record above the 32 MiB hard limit is refused before writing");
        string oversizePath = Path.Combine(oversizeDirectory, "history.json");
        Check(!File.Exists(oversizePath) || new FileInfo(oversizePath).Length < HistorySnapshots.HardLimit,
            "A refused oversized record cannot leave unreadable data on disk");

        string mixedDirectory = NewDirectory(folder, "retry-mixed");
        var mixedFiles = new FaultingFiles();
        var mixedStore = new LocalStore(mixedDirectory, mixedFiles);
        await mixedStore.SaveAsync(Completed("mixed seed", mixedStore.Settings), mixedStore.HistoryEpoch);
        await mixedStore.FlushAsync();
        var changedLimit = Json.Copy(mixedStore.Settings); changedLimit.HistoryLimit = 500;
        mixedFiles.MatchingCalls = 0; mixedFiles.FailOperation = "Replace"; mixedFiles.FailAt = 1;
        bool settingsFailed = false;
        try { await mixedStore.SaveSettingsAsync(SettingsUpdate.Full(changedLimit)); }
        catch (UserError) { settingsFailed = true; }
        mixedFiles.MatchingCalls = 0; mixedFiles.FailOperation = "Replace"; mixedFiles.FailAt = 1;
        bool saveFailed = false;
        try { await mixedStore.SaveAsync(Completed("mixed retry", mixedStore.Settings), mixedStore.HistoryEpoch); }
        catch (UserError) { saveFailed = true; }
        await mixedStore.RetryFailedWritesAsync();
        bool stillFailing = false;
        try { await mixedStore.FlushAsync(); } catch (UserError) { stillFailing = true; }
        Check(settingsFailed && saveFailed && stillFailing,
            "Retrying a history failure does not clear an unresolved settings failure");
    }
    private static async Task ByteLimitRegression(string folder)
    {
        string directory = NewDirectory(folder, "byte-limit");
        var store = new LocalStore(directory);
        var settings = Json.Copy(store.Settings); settings.HistoryLimit = 1000;
        await store.SaveSettingsAsync(SettingsUpdate.Full(settings));
        for (int i = 0; i < 6; i++) {
            var record = TranslationRecord.Create("large source " + i, "剪贴板", store.Settings);
            record.Source = new string('文', 300000) + " " + i;
            record.Translation = new string('中', 300000);
            record.Completed = true;
            store.Save(record);
        }
        long length = new FileInfo(Path.Combine(directory, "history.json")).Length;
        Check(length <= 4194304, "History trimming counts UTF-8 bytes, not UTF-16 characters");
        Check(store.History("").Count <= 4, "Byte-limited history keeps only records that fit the soft limit");
    }
    private static async Task UiHeartbeat(string folder)
    {
        string directory = NewDirectory(folder, "ui-heartbeat");
        var files = new FaultingFiles { DelayWritesMs = 160 };
        var store = new LocalStore(directory, files);
        var record = TranslationRecord.Create("heartbeat source", "剪贴板", store.Settings);
        record.Translation = "heartbeat translation"; record.Completed = true;
        int beats = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (s, e) => beats++;
        timer.Start();
        try {
            await store.SaveAsync(record, store.HistoryEpoch);
            await store.FlushAsync();
        } finally { timer.Stop(); }
        Check(beats > 0, "UI dispatcher continues during long history persistence");
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
