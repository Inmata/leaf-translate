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
        await TransactionIntegrity(folder);
        await CredentialAndPatchScenarios(folder);
        await QueueScenarios();
        await QueueResolutionScenarios(folder);
        await SettingsFailureScopeScenarios(folder);
        await FailedDisableBarrierScenarios(folder);
        await ClearBoundaryScenarios(folder);
        await ExplicitRecoveryEvidenceScenarios(folder);
        await BackupCleanupScenarios(folder);
        await PostCommitCleanupScenarios(folder);
        await StructureScenarios(folder);
        await ByteLimitRegression(folder);
        await HistoryScenarios(folder);
        await HistoryBarrierScenarios(folder);
        await HistoryRetryScenarios(folder);
        await ExitScenarioChecks(folder);
        return assertions - start;
    }
    // WPF-side checks; must run inside the ApplicationTests application/dispatcher.
    public static async Task<int> RunUi(string folder)
    {
        int start = assertions;
        await UiRecoveryAndCredentials(folder);
        await UiExternalRollbackIntegrity(folder);
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
            Path.GetFileName(path) != AtomicFileBatch.MarkerName &&
            Path.GetFileName(path) != AtomicFileBatch.MarkerName + ".tmp");
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
        await ExhaustiveFileFaultMatrix(folder);
    }

    // Traces one clean commit and then repeats it with a fault at every traced operation.
    // The interrupted variant performs the operation (or a partial write) before throwing so
    // crash semantics are covered instead of only a throw-before-write shortcut.
    private static async Task ExhaustiveFileFaultMatrix(string folder)
    {
        string traceDirectory = NewDirectory(folder, "matrix-trace");
        var seed = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        seed.HistoryLimit = 180;
        await SeedStore(traceDirectory, seed);
        var traceFiles = new TracingStoreFiles();
        var traceStore = new LocalStore(traceDirectory, traceFiles);
        var traceNext = Json.Copy(traceStore.Settings);
        traceNext.Provider.BaseUrl = "https://new-api.invalid/v1";
        traceNext.HistoryLimit = 181; traceNext.TargetLanguage = "日本語";
        await traceStore.CommitSettingsAsync(SettingsUpdate.Full(traceNext), null, null);
        var trace = traceFiles.Operations.ToList();
        Check(trace.Count >= 12, "The fault trace enumerates every staging, marker and swap operation");
        Check(trace.Any(op => op.StartsWith("Write ", StringComparison.Ordinal) && op.EndsWith(AtomicFileBatch.MarkerName + ".tmp", StringComparison.Ordinal)),
            "The trace includes the atomic marker preparation");
        Check(trace.Any(op => op.StartsWith("Move ", StringComparison.Ordinal) || op.StartsWith("Replace ", StringComparison.Ordinal)),
            "The trace includes the marker publication");

        for (int position = 1; position <= trace.Count; position++) {
            string[] parts = trace[position - 1].Split(' ');
            string operation = parts[0];
            int match = trace.Take(position).Count(op => op.StartsWith(operation + " ", StringComparison.Ordinal));
            await FaultAtPosition(folder, position, operation, match, false);
            await FaultAtPosition(folder, position, operation, match, true);
        }
    }

    private static async Task FaultAtPosition(string folder, int position, string operation, int match, bool interrupted)
    {
        string directory = NewDirectory(folder, interrupted ? "matrix-interrupted" : "matrix-before");
        var seed = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        seed.HistoryLimit = 180;
        await SeedStore(directory, seed);
        var files = new TracingStoreFiles {
            FailOperation = operation, FailAt = match, Interrupted = interrupted
        };
        var store = new LocalStore(directory, files);
        var next = Json.Copy(store.Settings);
        next.Provider.BaseUrl = "https://new-api.invalid/v1";
        next.HistoryLimit = 181; next.TargetLanguage = "日本語";
        bool applied = false, rolledBack = false, succeeded = false;
        try {
            await store.CommitSettingsAsync(SettingsUpdate.Full(next),
                (oldSettings, candidateSettings) => applied = true,
                (oldSettings, candidateSettings) => rolledBack = true);
            succeeded = true;
        } catch (UserError) { }
        var reloaded = new LocalStore(directory);
        bool newState = reloaded.Settings.Provider.BaseUrl == "https://new-api.invalid/v1";
        string label = "matrix " + (interrupted ? "interrupted" : "before") + " #" + position + " (" + operation + ")";
        Check(reloaded.Settings.TargetLanguage == (newState ? "日本語" : "中文"), label + ": the settings candidate is applied as a whole");
        Check(succeeded == newState, label + ": the reloaded settings agree with the commit result");
        Check(reloaded.Settings.HistoryLimit == (newState ? 181 : 180), label + ": settings and history stay in one commit group");
        Check(reloaded.History("fixture source").Count == 1, label + ": the seeded record survives the fault");
        Check(!reloaded.RecoveryBlocked, label + ": a transient fault never leaves startup recovery blocked");
        Check(rolledBack == !succeeded, label + ": external rollback runs exactly for failed commits");
        Check(!succeeded || applied, label + ": a successful commit applied its external effects");
        Check(store.Settings.Provider.BaseUrl == reloaded.Settings.Provider.BaseUrl,
            label + ": the in-memory state matches the persisted state");
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
        // The fixture only starts failing after the prepared marker is durable, so the
        // commit stages normally but the rollback cannot restore the old target.
        var files = new RollbackFaultFiles();
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

    // Recovery must refuse to claim success without evidence: a corrupt marker, duplicate
    // targets or a missing backup keeps every recovery artifact for a later healthy retry.
    private static async Task TransactionIntegrity(string folder)
    {
        var settings = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");
        var next = Json.Copy(settings); next.TargetLanguage = "日本語";

        string corrupt = NewDirectory(folder, "corrupt-marker");
        await SeedStore(corrupt, settings);
        File.WriteAllText(Path.Combine(corrupt, "settings.json.new"), Json.Write(next));
        File.WriteAllText(Path.Combine(corrupt, "settings.json.old"), "previous backup");
        File.WriteAllText(Path.Combine(corrupt, AtomicFileBatch.MarkerName), "{not-json");
        var corruptStore = new LocalStore(corrupt);
        Check(corruptStore.RecoveryBlocked, "An unparseable transaction marker blocks recovery instead of guessing");
        Check(File.Exists(Path.Combine(corrupt, AtomicFileBatch.MarkerName)) &&
            File.ReadAllText(Path.Combine(corrupt, "settings.json.old")) == "previous backup",
            "An unparseable marker keeps the marker and staging backups");

        string invalidState = NewDirectory(folder, "invalid-state");
        File.WriteAllText(Path.Combine(invalidState, "settings.json.old"), "previous backup");
        File.WriteAllText(Path.Combine(invalidState, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "pending", Targets = new[] { new { Name = "settings.json", Existed = true } }
        }));
        var invalidStateStore = new LocalStore(invalidState);
        Check(invalidStateStore.RecoveryBlocked && File.Exists(Path.Combine(invalidState, "settings.json.old")),
            "An unknown marker state blocks recovery and keeps its backups");

        string emptyTargets = NewDirectory(folder, "empty-targets");
        File.WriteAllText(Path.Combine(emptyTargets, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared", Targets = new object[0]
        }));
        var emptyStore = new LocalStore(emptyTargets);
        Check(emptyStore.RecoveryBlocked && File.Exists(Path.Combine(emptyTargets, AtomicFileBatch.MarkerName)),
            "An empty target list blocks recovery instead of discarding the marker");

        string duplicate = NewDirectory(folder, "duplicate-targets");
        File.WriteAllText(Path.Combine(duplicate, "settings.json.old"), "previous backup");
        File.WriteAllText(Path.Combine(duplicate, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared", Targets = new[] {
                new { Name = "settings.json", Existed = true }, new { Name = "settings.json", Existed = true }
            }
        }));
        var duplicateStore = new LocalStore(duplicate);
        Check(duplicateStore.RecoveryBlocked && File.Exists(Path.Combine(duplicate, "settings.json.old")),
            "Duplicate marker targets block recovery and keep the backups");

        string missing = NewDirectory(folder, "missing-backup");
        File.WriteAllText(Path.Combine(missing, "settings.json"), Json.Write(settings));
        File.WriteAllText(Path.Combine(missing, "settings.json.new"), Json.Write(next));
        File.WriteAllText(Path.Combine(missing, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared", Targets = new[] { new { Name = "settings.json", Existed = true } }
        }));
        var missingStore = new LocalStore(missing);
        Check(missingStore.RecoveryBlocked, "A prepared marker without its required backup is not treated as recovered");
        Check(File.Exists(Path.Combine(missing, AtomicFileBatch.MarkerName)) &&
            File.Exists(Path.Combine(missing, "settings.json.new")),
            "A missing backup keeps the marker and staged candidate for a real retry");

        string partial = NewDirectory(folder, "partial-marker-temp");
        await SeedStore(partial, settings);
        File.WriteAllText(Path.Combine(partial, "settings.json.new"), Json.Write(next));
        File.Copy(Path.Combine(partial, "settings.json"), Path.Combine(partial, "settings.json.old"), true);
        File.WriteAllText(Path.Combine(partial, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared", Targets = new[] { new { Name = "settings.json", Existed = true } }
        }));
        File.WriteAllText(Path.Combine(partial, AtomicFileBatch.MarkerName + ".tmp"), "{\"State\":\"prep");
        var partialStore = new LocalStore(partial);
        Check(!partialStore.RecoveryBlocked && partialStore.Settings.TargetLanguage == "中文",
            "A partial marker temp file cannot replace the durable marker during recovery");
        Check(!File.Exists(Path.Combine(partial, AtomicFileBatch.MarkerName + ".tmp")), "Recovery removes a partial marker temp file");

        string repeat = NewDirectory(folder, "repeatable-recovery");
        await SeedStore(repeat, settings);
        File.WriteAllText(Path.Combine(repeat, "settings.json.new"), Json.Write(next));
        File.Copy(Path.Combine(repeat, "settings.json"), Path.Combine(repeat, "settings.json.old"), true);
        File.WriteAllText(Path.Combine(repeat, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared", Targets = new[] { new { Name = "settings.json", Existed = true } }
        }));
        var failingRestore = new FaultingFiles { FailOperation = "Copy", FailAt = 1 };
        var blockedRestore = new LocalStore(repeat, failingRestore);
        Check(blockedRestore.RecoveryBlocked, "A failed restore is reported instead of consuming the backup");
        Check(File.Exists(Path.Combine(repeat, "settings.json.old")), "The old backup survives a failed restore attempt");
        var healthyRestore = new LocalStore(repeat);
        Check(!healthyRestore.RecoveryBlocked && healthyRestore.Settings.TargetLanguage == "中文" && DirectoryIsClean(repeat),
            "A later healthy restart can still recover the same transaction");

        string markerDelete = NewDirectory(folder, "marker-delete-fault");
        await SeedStore(markerDelete, settings);
        File.WriteAllText(Path.Combine(markerDelete, "settings.json.new"), Json.Write(next));
        File.Copy(Path.Combine(markerDelete, "settings.json"), Path.Combine(markerDelete, "settings.json.old"), true);
        File.WriteAllText(Path.Combine(markerDelete, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared", Targets = new[] { new { Name = "settings.json", Existed = true } }
        }));
        var deleteFault = new TargetedFaultingFiles { Fail = (operation, path) =>
            operation == "Delete" && Path.GetFileName(path) == AtomicFileBatch.MarkerName };
        var blockedDelete = new LocalStore(markerDelete, deleteFault);
        Check(blockedDelete.RecoveryBlocked, "A failed marker cleanup keeps the transaction blocked");
        Check(File.Exists(Path.Combine(markerDelete, "settings.json.old")),
            "Recovery keeps the old backup when the marker cannot be cleared");
        var healthyDelete = new LocalStore(markerDelete);
        Check(!healthyDelete.RecoveryBlocked && healthyDelete.Settings.TargetLanguage == "中文" && DirectoryIsClean(markerDelete),
            "The transaction recovers correctly after the marker delete fault clears");

        string blockNew = NewDirectory(folder, "block-new-commit");
        await SeedStore(blockNew, settings);
        File.WriteAllText(Path.Combine(blockNew, "settings.json.new"), "stale candidate");
        File.WriteAllText(Path.Combine(blockNew, "settings.json.old"), "stale backup");
        File.WriteAllText(Path.Combine(blockNew, AtomicFileBatch.MarkerName), "{corrupt");
        var blockedStore = new LocalStore(blockNew);
        bool refused = false;
        try { await blockedStore.SaveSettingsAsync(SettingsUpdate.Preferences(Json.Copy(blockedStore.Settings))); }
        catch (UserError) { refused = true; }
        Check(refused, "A blocked store refuses new commits instead of overwriting recovery evidence");
        Check(File.ReadAllText(Path.Combine(blockNew, AtomicFileBatch.MarkerName)) == "{corrupt" &&
            File.ReadAllText(Path.Combine(blockNew, "settings.json.old")) == "stale backup",
            "A refused commit cannot overwrite an unrecovered marker, .new or .old");
    }

    // Failed operations stay in the queue until their exact operation is replayed or made moot
    // by an epoch/tombstone barrier. Unrelated successes must never clear them.
    private static async Task QueueResolutionScenarios(string folder)
    {
        string settingsDir = NewDirectory(folder, "queue-settings-retry");
        var settingsFiles = new FaultingFiles();
        var settingsStore = new LocalStore(settingsDir, settingsFiles);
        await settingsStore.SaveSettingsAsync(SettingsUpdate.Full(
            FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model")));
        var changed = Json.Copy(settingsStore.Settings); changed.TargetLanguage = "日本語";
        settingsFiles.MatchingCalls = 0; settingsFiles.FailOperation = "Replace"; settingsFiles.FailAt = 1;
        bool settingsFailed = false;
        try { await settingsStore.SaveSettingsAsync(SettingsUpdate.Preferences(changed)); }
        catch (UserError) { settingsFailed = true; }
        Check(settingsFailed, "A failed settings commit is reported before the explicit retry");
        settingsFiles.FailAt = -1; settingsFiles.MatchingCalls = 0;
        var retry = Json.Copy(settingsStore.Settings); retry.TargetLanguage = "日本語";
        await settingsStore.SaveSettingsAsync(SettingsUpdate.Preferences(retry));
        bool settingsFlushed = true;
        try { await settingsStore.FlushAsync(); } catch (UserError) { settingsFlushed = false; }
        Check(settingsFlushed, "A successful settings reapply acknowledges the older settings failure");
        Check(new LocalStore(settingsDir).Settings.TargetLanguage == "日本語", "The successful settings retry is persisted");

        string saveDelete = NewDirectory(folder, "queue-save-delete");
        var saveDeleteFiles = new FaultingFiles();
        var saveDeleteStore = new LocalStore(saveDelete, saveDeleteFiles);
        var deleted = Completed("queue failed save", saveDeleteStore.Settings);
        saveDeleteFiles.MatchingCalls = 0; saveDeleteFiles.FailOperation = "Replace"; saveDeleteFiles.FailAt = 1;
        try { await saveDeleteStore.SaveAsync(deleted, saveDeleteStore.HistoryEpoch); } catch (UserError) { }
        await saveDeleteStore.DeleteAsync(deleted.Id);
        bool saveDeleteFlushed = true;
        try { await saveDeleteStore.FlushAsync(); } catch (UserError) { saveDeleteFlushed = false; }
        Check(saveDeleteFlushed, "Deleting a record resolves its own failed save");
        Check(new LocalStore(saveDelete).History("").Count == 0, "The resolved failed save stays deleted");

        string saveClear = NewDirectory(folder, "queue-save-clear");
        var saveClearFiles = new FaultingFiles();
        var saveClearStore = new LocalStore(saveClear, saveClearFiles);
        var cleared = Completed("queue clear save", saveClearStore.Settings);
        saveClearFiles.MatchingCalls = 0; saveClearFiles.FailOperation = "Replace"; saveClearFiles.FailAt = 1;
        try { await saveClearStore.SaveAsync(cleared, saveClearStore.HistoryEpoch); } catch (UserError) { }
        await saveClearStore.ClearAsync();
        bool saveClearFlushed = true;
        try { await saveClearStore.FlushAsync(); } catch (UserError) { saveClearFlushed = false; }
        Check(saveClearFlushed, "Clearing history resolves a previous failed save");
        Check(new LocalStore(saveClear).History("").Count == 0, "Clearing history after the failed save leaves it empty");

        string saveDisable = NewDirectory(folder, "queue-save-disable");
        var saveDisableFiles = new FaultingFiles();
        var saveDisableStore = new LocalStore(saveDisable, saveDisableFiles);
        var disabledRecord = Completed("queue disable save", saveDisableStore.Settings);
        saveDisableFiles.MatchingCalls = 0; saveDisableFiles.FailOperation = "Replace"; saveDisableFiles.FailAt = 1;
        try { await saveDisableStore.SaveAsync(disabledRecord, saveDisableStore.HistoryEpoch); } catch (UserError) { }
        var disabled = Json.Copy(saveDisableStore.Settings); disabled.HistoryEnabled = false;
        await saveDisableStore.SaveSettingsAsync(SettingsUpdate.Preferences(disabled));
        bool disableFlushed = true;
        try { await saveDisableStore.FlushAsync(); } catch (UserError) { disableFlushed = false; }
        Check(disableFlushed, "Disabling history resolves a previous failed save");
        Check(new LocalStore(saveDisable).History("").Count == 0, "Disabling history discards the failed snapshot");

        string clearDir = NewDirectory(folder, "queue-clear-new-record");
        var clearFiles = new FaultingFiles();
        var clearStore = new LocalStore(clearDir, clearFiles);
        var oldRecord = Completed("queue clear old", clearStore.Settings);
        await clearStore.SaveAsync(oldRecord, clearStore.HistoryEpoch);
        await clearStore.FlushAsync();
        clearFiles.MatchingCalls = 0; clearFiles.FailOperation = "Replace"; clearFiles.FailAt = 1;
        bool clearFailed = false;
        try { await clearStore.ClearAsync(); } catch (UserError) { clearFailed = true; }
        var newRecord = Completed("queue clear new", clearStore.Settings);
        await clearStore.SaveAsync(newRecord, clearStore.HistoryEpoch);
        await clearStore.RetryFailedWritesAsync();
        await clearStore.FlushAsync();
        var clearDisk = new LocalStore(clearDir).History("");
        Check(clearFailed && clearDisk.Count == 1 && clearDisk[0].Id == newRecord.Id,
            "Replaying a failed clear removes only the records it was meant to clear");

        string deleteClear = NewDirectory(folder, "queue-delete-clear");
        var deleteClearFiles = new FaultingFiles();
        var deleteClearStore = new LocalStore(deleteClear, deleteClearFiles);
        var deleteTarget = Completed("queue delete clear", deleteClearStore.Settings);
        await deleteClearStore.SaveAsync(deleteTarget, deleteClearStore.HistoryEpoch);
        await deleteClearStore.FlushAsync();
        deleteClearFiles.MatchingCalls = 0; deleteClearFiles.FailOperation = "Replace"; deleteClearFiles.FailAt = 1;
        try { await deleteClearStore.DeleteAsync(deleteTarget.Id); } catch (UserError) { }
        await deleteClearStore.ClearAsync();
        bool deleteClearFlushed = true;
        try { await deleteClearStore.FlushAsync(); } catch (UserError) { deleteClearFlushed = false; }
        Check(deleteClearFlushed, "A later successful clear confirms a failed delete instead of replaying it");
        Check(new LocalStore(deleteClear).History("").Count == 0, "The cleared record cannot be resurrected by a retry");
    }

    // A successful settings commit acknowledges only failures in the same patch scope and
    // target; an unrelated save must not hide a provider/model/key change that was lost.
    private static async Task SettingsFailureScopeScenarios(string folder)
    {
        string directory = NewDirectory(folder, "settings-scope");
        var files = new FaultingFiles();
        var store = new LocalStore(directory, files);
        await store.SaveSettingsAsync(SettingsUpdate.Full(
            FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model")));
        await store.FlushAsync();

        var newProvider = new ProviderProfile { Id = "fixture", Name = "Fixture",
            BaseUrl = "https://new-api.invalid/v1", Model = "fixture-model" };
        files.MatchingCalls = 0; files.FailOperation = "Replace"; files.FailAt = 1;
        bool providerFailed = false;
        try { await store.SaveSettingsAsync(SettingsUpdate.Provider(newProvider)); }
        catch (UserError) { providerFailed = true; }
        Check(providerFailed, "A provider commit fault is reported before any retry");

        files.MatchingCalls = 0; files.FailAt = -1;
        await store.SaveSettingsAsync(SettingsUpdate.Placement(
            new WindowPlacement { X = 5, Y = 6, Width = 456, Height = 620, Screen = "scope" }));
        bool providerPending = false;
        try { await store.FlushAsync(); } catch (UserError) { providerPending = true; }
        Check(providerPending, "An unrelated successful placement cannot acknowledge the failed provider commit");

        files.MatchingCalls = 0;
        await store.SaveSettingsAsync(SettingsUpdate.Provider(newProvider));
        bool providerResolved = true;
        try { await store.FlushAsync(); } catch (UserError) { providerResolved = false; }
        Check(providerResolved, "A same-target provider retry acknowledges that provider failure");

        files.MatchingCalls = 0; files.FailOperation = "Replace"; files.FailAt = 1;
        bool modelFailed = false;
        try { await store.SaveSettingsAsync(SettingsUpdate.Model("fixture", "scope-model")); }
        catch (UserError) { modelFailed = true; }
        Check(modelFailed, "A model commit fault is reported before any retry");
        files.MatchingCalls = 0; files.FailAt = -1;
        var preferences = Json.Copy(store.Settings); preferences.TargetLanguage = "日本語";
        await store.SaveSettingsAsync(SettingsUpdate.Preferences(preferences));
        bool modelPending = false;
        try { await store.FlushAsync(); } catch (UserError) { modelPending = true; }
        Check(modelPending, "An unrelated successful preferences commit cannot acknowledge the failed model commit");
        await store.SaveSettingsAsync(SettingsUpdate.Model("fixture", "scope-model"));
        bool modelResolved = true;
        try { await store.FlushAsync(); } catch (UserError) { modelResolved = false; }
        Check(modelResolved, "A same-provider model retry acknowledges that model failure");
    }

    // Turning history off is a durable user intent: a failed disable must still invalidate
    // snapshots captured under the old epoch, and re-enabling must not revive them.
    private static async Task FailedDisableBarrierScenarios(string folder)
    {
        string directory = NewDirectory(folder, "failed-disable");
        var files = new FaultingFiles();
        var store = new LocalStore(directory, files);
        var seeded = Completed("disable seed", store.Settings);
        await store.SaveAsync(seeded, store.HistoryEpoch);
        await store.FlushAsync();
        long oldEpoch = store.HistoryEpoch;

        var pending = Completed("disable pending", store.Settings);
        files.MatchingCalls = 0; files.FailOperation = "Replace"; files.FailAt = 1;
        bool saveFailed = false;
        try { await store.SaveAsync(pending, oldEpoch); } catch (UserError) { saveFailed = true; }
        Check(saveFailed, "A history save fault is reported before the failed disable");

        var disabled = Json.Copy(store.Settings); disabled.HistoryEnabled = false;
        files.MatchingCalls = 0; files.FailAt = 1;
        bool disableFailed = false;
        try { await store.SaveSettingsAsync(SettingsUpdate.Preferences(disabled)); }
        catch (UserError) { disableFailed = true; }
        Check(disableFailed, "A failed history disable is reported instead of pretending success");
        Check(store.Settings.HistoryEnabled,
            "A failed history disable never publishes the disabled candidate");

        files.MatchingCalls = 0; files.FailAt = -1;
        var enabled = Json.Copy(store.Settings); enabled.HistoryEnabled = true;
        await store.SaveSettingsAsync(SettingsUpdate.Preferences(enabled));
        await store.RetryFailedWritesAsync();
        await store.SaveAsync(Completed("disable late", store.Settings), oldEpoch);
        await store.FlushAsync();

        var reloaded = new LocalStore(directory).History("");
        Check(reloaded.Count == 1 && reloaded[0].Id == seeded.Id,
            "A failed disable barrier prevents old-epoch saves and retries from reviving");
    }

    // A clear must remove every record that was published before the queued clear runs,
    // including a save that already passed its epoch check and was still writing.
    private static async Task ClearBoundaryScenarios(string folder)
    {
        string directory = NewDirectory(folder, "clear-boundary");
        var files = new BlockingWriteFiles();
        var store = new LocalStore(directory, files);
        var seeded = Completed("clear boundary seed", store.Settings);
        await store.SaveAsync(seeded, store.HistoryEpoch);
        await store.FlushAsync();

        var inFlight = Completed("clear boundary inflight", store.Settings);
        files.BlockWrites = true;
        var save = store.SaveAsync(inFlight, store.HistoryEpoch);
        Check(files.Entered.Wait(10000), "The in-flight save reaches its background write window");
        var clear = store.ClearAsync();
        files.Release.Set();
        await save;
        await clear;
        files.BlockWrites = false;
        await store.FlushAsync();

        var reloaded = new LocalStore(directory).History("");
        Check(reloaded.Count == 0,
            "A clear removes a record published by an in-flight save that passed its epoch check");
    }

    // An explicit reapply may clear a pending external rollback or an unreadable marker only
    // when the replacement commit is durable; a failure at any earlier point keeps evidence.
    private static async Task ExplicitRecoveryEvidenceScenarios(string folder)
    {
        var settings = FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model");

        string valid = NewDirectory(folder, "explicit-valid");
        await SeedStore(valid, settings);
        var prepared = Json.Copy(Settings.Defaults()); prepared.TargetLanguage = "日本語";
        File.WriteAllText(Path.Combine(valid, "settings.json.new"), Json.Write(prepared));
        File.Copy(Path.Combine(valid, "settings.json"), Path.Combine(valid, "settings.json.old"), true);
        File.WriteAllText(Path.Combine(valid, AtomicFileBatch.MarkerName), Json.Write(new {
            State = "prepared", ExternalApplied = true,
            Targets = new[] { new { Name = "settings.json", Existed = true } }
        }));
        var validFiles = new FaultingFiles();
        var validStore = new LocalStore(valid, validFiles);
        Check(validStore.ExternalRollbackPending && validStore.NeedsExplicitRecovery,
            "A recovered prepared transaction keeps its external rollback pending");
        var next = Json.Copy(validStore.Settings); next.TargetLanguage = "日本語";
        validFiles.MatchingCalls = 0; validFiles.FailOperation = "Write"; validFiles.FailAt = 1;
        bool reapplyFailed = false;
        try { await validStore.CommitSettingsAsync(SettingsUpdate.Full(next), null, null, true, null); }
        catch (UserError) { reapplyFailed = true; }
        Check(reapplyFailed && validStore.NeedsExplicitRecovery,
            "A staging failure during the explicit reapply keeps the pending evidence");
        Check(new LocalStore(valid).NeedsExplicitRecovery,
            "A restart after the failed reapply still requires explicit recovery");
        validFiles.MatchingCalls = 0; validFiles.FailOperation = "Replace"; validFiles.FailAt = 1;
        reapplyFailed = false;
        try { await validStore.CommitSettingsAsync(SettingsUpdate.Full(next), null, null, true, null); }
        catch (UserError) { reapplyFailed = true; }
        Check(reapplyFailed && validStore.NeedsExplicitRecovery,
            "A marker-write failure during the explicit reapply keeps the pending evidence");
        Check(new LocalStore(valid).NeedsExplicitRecovery,
            "A restart after the failed marker write still requires explicit recovery");
        validFiles.MatchingCalls = 0; validFiles.FailAt = -1;
        await validStore.CommitSettingsAsync(SettingsUpdate.Full(next), null, null, true, null);
        Check(!validStore.NeedsExplicitRecovery,
            "A completed explicit reapply clears the retained evidence");
        Check(!new LocalStore(valid).NeedsExplicitRecovery,
            "A clean restart after the explicit reapply needs no recovery");

        string corrupt = NewDirectory(folder, "explicit-corrupt");
        await SeedStore(corrupt, settings);
        File.WriteAllText(Path.Combine(corrupt, "settings.json.new"), "stale candidate");
        File.WriteAllText(Path.Combine(corrupt, "settings.json.old"), "stale backup");
        File.WriteAllText(Path.Combine(corrupt, AtomicFileBatch.MarkerName), "{not-json");
        var corruptFiles = new FaultingFiles();
        var corruptStore = new LocalStore(corrupt, corruptFiles);
        Check(corruptStore.RecoveryBlocked && corruptStore.NeedsExplicitRecovery,
            "An unreadable marker blocks until an explicit reapply");
        var corruptNext = Json.Copy(corruptStore.Settings); corruptNext.TargetLanguage = "日本語";
        corruptFiles.MatchingCalls = 0; corruptFiles.FailOperation = "Write"; corruptFiles.FailAt = 1;
        reapplyFailed = false;
        try { await corruptStore.CommitSettingsAsync(SettingsUpdate.Full(corruptNext), null, null, true, null); }
        catch (UserError) { reapplyFailed = true; }
        Check(reapplyFailed && corruptStore.NeedsExplicitRecovery,
            "A failed explicit recovery of an unreadable marker still needs recovery");
        Check(new LocalStore(corrupt).NeedsExplicitRecovery,
            "A restart after the failed corrupt-marker recovery is still blocked");
        corruptFiles.MatchingCalls = 0; corruptFiles.FailAt = -1;
        await corruptStore.CommitSettingsAsync(SettingsUpdate.Full(corruptNext), null, null, true, null);
        Check(!corruptStore.NeedsExplicitRecovery, "A completed explicit recovery replaces the unreadable marker");
        Check(!new LocalStore(corrupt).NeedsExplicitRecovery,
            "A restart after the explicit recovery is clean");
    }

    // Enumerating or deleting the sensitive history backups must fail loudly and stay
    // retryable instead of letting clear/disable report success while quarantine copies remain.
    private static async Task BackupCleanupScenarios(string folder)
    {
        string directory = NewDirectory(folder, "backup-cleanup");
        var files = new TargetedFaultingFiles();
        var store = new LocalStore(directory, files);
        var seeded = Completed("backup cleanup seed", store.Settings);
        await store.SaveAsync(seeded, store.HistoryEpoch);
        await store.FlushAsync();
        string corrupt = Path.Combine(directory, "history.json.corrupt-fixture");
        string quarantine = Path.Combine(directory, "history.json.quarantine-fixture");
        File.WriteAllText(corrupt, "sensitive fixture backup");
        File.WriteAllText(quarantine, "sensitive fixture quarantine");
        files.Fail = (operation, path) => operation == "Delete" &&
            (Path.GetFileName(path).StartsWith("history.json.corrupt-", StringComparison.Ordinal) ||
             Path.GetFileName(path).StartsWith("history.json.quarantine-", StringComparison.Ordinal));
        bool clearFailed = false;
        try { await store.ClearAsync(); } catch (UserError error) { clearFailed = error.Code == "storage"; }
        Check(clearFailed, "A backup deletion fault makes the clear report failure");
        Check(store.History("").Count == 0, "The clear still removes the records from memory");
        Check(File.Exists(corrupt) && File.Exists(quarantine), "Sensitive backups stay until their cleanup succeeds");
        var fresh = Completed("backup cleanup fresh", store.Settings);
        await store.SaveAsync(fresh, store.HistoryEpoch);
        files.Fail = null;
        await store.RetryFailedWritesAsync();
        await store.FlushAsync();
        Check(!File.Exists(corrupt) && !File.Exists(quarantine), "Retrying the clear removes the sensitive backups");
        var reloaded = new LocalStore(directory).History("");
        Check(reloaded.Count == 1 && reloaded[0].Id == fresh.Id,
            "The retried backup cleanup does not delete the later-epoch record");
    }

    // Post-commit cleanup runs after the files and memory are published; a cleanup fault must
    // be reported and retryable without rolling back the committed settings.
    private static async Task PostCommitCleanupScenarios(string folder)
    {
        string directory = NewDirectory(folder, "post-commit");
        var store = new LocalStore(directory);
        await store.SaveSettingsAsync(SettingsUpdate.Full(
            FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model")));
        await store.FlushAsync();
        bool failCleanup = true, rolledBack = false;
        var disabled = Json.Copy(store.Settings); disabled.HistoryEnabled = false;
        bool failed = false;
        try {
            await store.CommitSettingsAsync(SettingsUpdate.Preferences(disabled), null,
                (committed, candidate) => rolledBack = true, false,
                () => { if (failCleanup) throw new UserError("storage", "cleanup fixture"); });
        } catch (UserError) { failed = true; }
        Check(failed, "A post-commit cleanup fault is reported");
        Check(!rolledBack, "A post-commit cleanup fault never rolls back the durable settings commit");
        Check(store.Settings.HistoryEnabled == false && new LocalStore(directory).Settings.HistoryEnabled == false,
            "The committed settings stay applied despite the cleanup fault");
        bool pending = false;
        try { await store.FlushAsync(); } catch (UserError) { pending = true; }
        Check(pending, "Flush keeps reporting the unresolved post-commit cleanup");
        failCleanup = false;
        await store.RetryFailedWritesAsync();
        bool resolved = true;
        try { await store.FlushAsync(); } catch (UserError) { resolved = false; }
        Check(resolved, "Retrying the post-commit cleanup resolves the failure");

        string backupDir = NewDirectory(folder, "post-commit-backup");
        var backupFiles = new TargetedFaultingFiles();
        var backupStore = new LocalStore(backupDir, backupFiles);
        await backupStore.SaveSettingsAsync(SettingsUpdate.Full(
            FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model")));
        string quarantine = Path.Combine(backupDir, "history.json.quarantine-fixture");
        File.WriteAllText(quarantine, "sensitive fixture quarantine");
        backupFiles.Fail = (operation, path) => operation == "Delete" &&
            Path.GetFileName(path).StartsWith("history.json.quarantine-", StringComparison.Ordinal);
        bool backupRolledBack = false;
        var disableBackup = Json.Copy(backupStore.Settings); disableBackup.HistoryEnabled = false;
        bool disableFailed = false;
        try {
            await backupStore.CommitSettingsAsync(SettingsUpdate.Preferences(disableBackup), null,
                (committed, candidate) => backupRolledBack = true, false, null);
        } catch (UserError) { disableFailed = true; }
        Check(disableFailed && !backupRolledBack && backupStore.Settings.HistoryEnabled == false,
            "A failed backup cleanup does not roll back the history-disable commit");
        Check(File.Exists(quarantine), "The sensitive backup survives until the cleanup retry");
        backupFiles.Fail = null;
        await backupStore.RetryFailedWritesAsync();
        bool backupResolved = true;
        try { await backupStore.FlushAsync(); } catch (UserError) { backupResolved = false; }
        Check(backupResolved && !File.Exists(quarantine),
            "Retrying the post-commit cleanup removes the sensitive backup");
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
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
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
        var goodLoaded = kept.First(r => r.Id == good.Id);
        Check(goodLoaded.Context != null && goodLoaded.Context.Provider.BaseUrl == "https://old-api.invalid/v1" &&
            goodLoaded.Context.Provider.Model == "fixture-model" && goodLoaded.Context.Learning.Count > 0,
            "An old full history context keeps its provider and learning fields through recovery");
        Check(goodLoaded.Source == "good source" && goodLoaded.Translation == "好" && goodLoaded.SourceKind == "剪贴板",
            "Recovered history keeps the source, translation and source kind fields");
        Check(kept.First(r => r.Id == filtered.Id).Cards.Count == 1 &&
            kept.First(r => r.Id == filtered.Id).Cards.Values.Single().sections.Count == 1,
            "Null cards and null sections are filtered from loaded history");
        var filteredLoaded = kept.First(r => r.Id == filtered.Id);
        Check(filteredLoaded.Cards.Values.Single().meaning == "好的" &&
            filteredLoaded.Cards.Values.Single().word == "good" &&
            filteredLoaded.Cards.Values.Single().sections[0].content == "c",
            "Recovered word cards keep their meaning and section fields");
        Check(filteredLoaded.Chat.Count == 2 && filteredLoaded.Chat[0].Content == "q" && filteredLoaded.Chat[1].Content == "a",
            "Recovered chat turns keep their role and content fields");
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

    // Real child-process exits: normal reload, late word/chat rejection, and a failed exit
    // that stays alive and succeeds on the retry action. No second Application in-process.
    private static async Task ExitScenarioChecks(string folder)
    {
        string executable = typeof(CoreTests).Assembly.Location;
        string normal = NewDirectory(folder, "exit-normal");
        string late = NewDirectory(folder, "exit-late");
        string failing = NewDirectory(folder, "exit-fail");
        string placement = NewDirectory(folder, "exit-placement");
        string retry = NewDirectory(folder, "exit-retry");
        string settings = NewDirectory(folder, "exit-settings");
        await Task.Run(() => {
            RunExitChild(executable, "normal", normal);
            RunExitChild(executable, "late", late);
            RunExitChild(executable, "fail", failing);
            RunExitChild(executable, "placement", placement);
            RunExitChild(executable, "retry", retry);
            RunExitChild(executable, "settings", settings);
        });

        var normalStore = new LocalStore(Path.Combine(normal, "data"));
        Check(normalStore.Settings.Scene == "书籍" && normalStore.Settings.SceneDetail == "Exit pending patch",
            "A normal exit flushes the pending settings patch before shutdown");
        Check(normalStore.Settings.Placement != null && normalStore.Settings.Placement.X == 77 &&
            normalStore.Settings.Placement.Screen == "exit",
            "A normal exit flushes a queued placement save before shutdown");
        var normalHistory = normalStore.History("");
        Check(normalHistory.Count == 1 && normalHistory[0].Draft == "exit normal draft" &&
            normalHistory[0].Translation == "退出夹具译文",
            "A normal exit persists the final draft and translation before shutdown");

        var lateStore = new LocalStore(Path.Combine(late, "data"));
        var lateHistory = lateStore.History("");
        Check(lateHistory.Count == 1 && lateHistory[0].Translation == "迟到退出译文",
            "The late-request exit persists exactly the exit snapshot");
        Check(lateHistory[0].Cards.Count == 0 && lateHistory[0].Chat.Count == 0,
            "Late word/chat completions cannot write after the exit started");

        string alivePath = Path.Combine(failing, "alive.json");
        Check(File.Exists(alivePath), "A failed exit writes its survival report");
        string aliveReport = File.ReadAllText(alivePath);
        Check(aliveReport.Contains("\"alive\":true") && aliveReport.Contains("\"retry_visible\":true"),
            "A failed exit stays alive and offers a real retry");
        var failStore = new LocalStore(Path.Combine(failing, "data"));
        var failHistory = failStore.History("");
        Check(failHistory.Count == 1 && failHistory[0].Draft == "exit fail draft" &&
            failHistory[0].Translation == "退出夹具译文",
            "The retried exit persists the snapshot after the fault clears");

        // A flushed exit must not read or enqueue placement a second time while disposing.
        string placementReport = File.ReadAllText(Path.Combine(placement, "placement.json"));
        Check(placementReport.Contains("\"captures\":1") && placementReport.Contains("\"x\":111"),
            "A flushed exit persists its placement once instead of starting a late save: " + placementReport);

        // A retry closure that existed before the exit must stay inert once exiting begins.
        string retryReport = File.ReadAllText(Path.Combine(retry, "retry.json"));
        Check(retryReport.Contains("\"retry_visible\":true") && retryReport.Contains("\"before\":2") &&
            retryReport.Contains("\"after\":2"),
            "An exiting shell refuses a follow-up retry closure instead of starting a new request: " + retryReport);

        // The exit has to wait for the popup's own settings page: its pending edit is applied,
        // the page is left, and the session behind it is saved as usual.
        string settingsReport = File.ReadAllText(Path.Combine(settings, "exit-settings.json"));
        Check(settingsReport.Contains("\"opened\":true") && settingsReport.Contains("\"left\":true") &&
            settingsReport.Contains("\"target_saved\":true"),
            "An exit flushes and leaves the popup's settings page before shutdown: " + settingsReport);
        var settingsStore = new LocalStore(Path.Combine(settings, "data"));
        Check(settingsStore.Settings.TargetLanguage == "英语",
            "The preference edited on the settings page is saved by that exit");
        var settingsHistory = settingsStore.History("");
        Check(settingsReport.Contains("\"draft_saved\":true") && settingsHistory.Count == 1 &&
            settingsHistory[0].Translation == "退出夹具译文",
            "The session behind the settings page is still saved by the same exit: " + settingsReport);
    }

    private static void RunExitChild(string executable, string mode, string directory)
    {
        var info = new System.Diagnostics.ProcessStartInfo(executable,
            "--exit-scenario " + mode + " \"" + directory + "\"") {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)
        };
        using (var process = System.Diagnostics.Process.Start(info)) {
            if (!process.WaitForExit(120000)) {
                try { process.Kill(); } catch { }
                throw new Exception("Exit scenario timed out: " + mode);
            }
            Check(process.ExitCode == 0, "The exit scenario " + mode + " child process succeeds");
        }
        string error = Path.Combine(directory, "error.txt");
        if (File.Exists(error)) throw new Exception("Exit scenario " + mode + " failed: " + File.ReadAllText(error));
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
        var blockedStore = new LocalStore(blockedDirectory, new PersistentFaultingFiles { FailOperation = "Copy" });
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
    // External side effects must be captured before writing, restored completely on failure,
    // aggregated honestly when rollback fails, and kept inside the ordered commit after success.
    private static async Task UiExternalRollbackIntegrity(string folder)
    {
        string scopeDirectory = NewDirectory(folder, "ui-external-scope");
        var scopeFiles = new FaultingFiles();
        var scopeStore = new LocalStore(scopeDirectory, scopeFiles);
        await scopeStore.SaveSettingsAsync(SettingsUpdate.Full(
            FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model")));
        var scopeFake = new FakeCredentialProfiles();
        var oldProfile = scopeStore.Settings.Provider;
        scopeFake.Save(oldProfile, "old-key");
        var newProfile = new ProviderProfile { Id = "fixture", Name = "Fixture", BaseUrl = "https://new-api.invalid/v1", Model = "fixture-model" };
        scopeFake.Save(newProfile, "new-existing-key");
        using (var shell = new AppShell(scopeStore, false, new LlmClient(), scopeFake)) {
            scopeFiles.MatchingCalls = 0; scopeFiles.FailOperation = "Replace"; scopeFiles.FailAt = 1;
            bool failed = false;
            try {
                await shell.ApplySettingsAsync(SettingsUpdate.Provider(newProfile),
                    new Dictionary<string, string> { { "fixture", "user-new-key" } }, new HashSet<string>());
            } catch (UserError) { failed = true; }
            Check(failed, "UI: a file failure rejects the endpoint switch after credentials were staged");
            Check(scopeFake.Read(oldProfile) == "old-key", "UI: external rollback restores the old endpoint key");
            Check(scopeFake.Read(newProfile) == "new-existing-key",
                "UI: external rollback restores a pre-existing key at the new endpoint");
            Check(scopeStore.Settings.Provider.BaseUrl == "https://old-api.invalid/v1",
                "UI: the failed switch keeps the committed endpoint");
        }

        string rollbackDirectory = NewDirectory(folder, "ui-external-rollback");
        var rollbackFiles = new FaultingFiles();
        var rollbackStore = new LocalStore(rollbackDirectory, rollbackFiles);
        await rollbackStore.SaveSettingsAsync(SettingsUpdate.Full(
            FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model")));
        var rollbackFake = new FakeCredentialProfiles();
        var rollbackOld = rollbackStore.Settings.Provider;
        rollbackFake.Save(rollbackOld, "rollback-old-key");
        var rollbackNew = new ProviderProfile { Id = "fixture", Name = "Fixture", BaseUrl = "https://third-api.invalid/v1", Model = "fixture-model" };
        using (var shell = new AppShell(rollbackStore, false, new LlmClient(), rollbackFake)) {
            rollbackFake.FailDelete = id => id == Credentials.ScopedId(rollbackNew);
            rollbackFiles.MatchingCalls = 0; rollbackFiles.FailOperation = "Replace"; rollbackFiles.FailAt = 1;
            bool failed = false;
            try {
                await shell.ApplySettingsAsync(SettingsUpdate.Provider(rollbackNew),
                    new Dictionary<string, string> { { "fixture", "rollback-new-key" } }, new HashSet<string>());
            } catch (UserError) { failed = true; }
            Check(failed, "UI: a rollback item failure is reported to the transaction");
            Check(rollbackFake.Read(rollbackOld) == "rollback-old-key",
                "UI: one failed rollback item does not skip restoring the old key");
            Check(rollbackStore.ExternalRollbackPending && !rollbackStore.RecoveryBlocked,
                "UI: an incomplete external rollback keeps explicit recovery pending");
            var blockedHandler = new CountingHandler();
            using (var blockedShell = new AppShell(rollbackStore, false, new LlmClient(blockedHandler), rollbackFake)) {
                await blockedShell.TranslateAsync("Blocked external rollback", "剪贴板", false);
                Check(blockedHandler.Calls == 0, "UI: a pending external rollback refuses new API requests");
            }
            rollbackFake.FailDelete = null;
            rollbackFiles.FailAt = -1; rollbackFiles.MatchingCalls = 0;
            await shell.ApplySettingsAsync(SettingsUpdate.Provider(rollbackNew),
                new Dictionary<string, string> { { "fixture", "rollback-new-key" } }, new HashSet<string>(), true);
            Check(!rollbackStore.NeedsExplicitRecovery, "UI: an explicit reapply clears the pending external rollback");
            Check(rollbackFake.Read(rollbackNew) == "rollback-new-key" && rollbackFake.Read(rollbackOld) == "",
                "UI: the explicit reapply applies the new key and cleans the replaced target");
        }

        string orderDirectory = NewDirectory(folder, "ui-cleanup-order");
        var orderFiles = new FaultingFiles();
        var orderStore = new LocalStore(orderDirectory, orderFiles);
        await orderStore.SaveSettingsAsync(SettingsUpdate.Full(
            FixtureSettings("fixture", "https://old-api.invalid/v1", "fixture-model")));
        var orderFake = new FakeCredentialProfiles();
        var orderOld = orderStore.Settings.Provider;
        orderFake.Save(orderOld, "order-old-key");
        using (var shell = new AppShell(orderStore, false, new LlmClient(), orderFake)) {
            var endpointB = new ProviderProfile { Id = "fixture", Name = "Fixture", BaseUrl = "https://b-api.invalid/v1", Model = "fixture-model" };
            var endpointC = new ProviderProfile { Id = "fixture", Name = "Fixture", BaseUrl = "https://c-api.invalid/v1", Model = "fixture-model" };
            var first = shell.ApplySettingsAsync(SettingsUpdate.Provider(endpointB),
                new Dictionary<string, string> { { "fixture", "b-key" } }, new HashSet<string>());
            var second = orderStore.CommitSettingsAsync(SettingsUpdate.Provider(endpointC),
                (committed, candidate) => orderFake.Save(candidate.Provider, "c-key"), null);
            await Task.WhenAll(first, second);
            int deleteIndex = orderFake.Log.FindIndex(entry => entry == "delete " + Credentials.ScopedId(orderOld));
            int saveCIndex = orderFake.Log.FindIndex(entry => entry.StartsWith("save " + Credentials.ScopedId(endpointC), StringComparison.Ordinal));
            Check(deleteIndex >= 0 && saveCIndex > deleteIndex,
                "UI: cleanup of the replaced key stays inside the ordered settings commit");
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
    // Starts failing copies/deletes/replaces only after the prepared marker is durable, so a
    // commit can be forced into a genuine rollback failure instead of a staging failure.
    private sealed class RollbackFaultFiles : IStoreFiles
    {
        private readonly IStoreFiles inner = new PhysicalStoreFiles();
        private bool armed;
        private void Before(string operation)
        {
            if (armed && operation != "Write" && operation != "Move") throw new IOException("Rollback fixture failure");
        }
        public bool Exists(string path) { return inner.Exists(path); }
        public long Length(string path) { return inner.Length(path); }
        public string Read(string path) { Before("Read"); return inner.Read(path); }
        public void Write(string path, string payload)
        {
            inner.Write(path, payload);
            if (Path.GetFileName(path) == AtomicFileBatch.MarkerName + ".tmp") armed = true;
        }
        public void Copy(string source, string destination, bool overwrite)
        { Before("Copy"); inner.Copy(source, destination, overwrite); }
        public void Move(string source, string destination) { Before("Move"); inner.Move(source, destination); }
        public void Replace(string source, string destination) { Before("Replace"); inner.Replace(source, destination); }
        public void Delete(string path) { Before("Delete"); inner.Delete(path); }
    }

    // Records every file operation and can fail or interrupt any traced position.
    private sealed class TracingStoreFiles : IStoreFiles
    {
        private readonly IStoreFiles inner = new PhysicalStoreFiles();
        public readonly List<string> Operations = new List<string>();
        public string FailOperation;
        public int FailAt = -1;
        public int MatchingCalls;
        public bool Interrupted;
        private bool ShouldFail(string operation)
        {
            if (operation != FailOperation) return false;
            MatchingCalls++;
            return MatchingCalls == FailAt;
        }
        public bool Exists(string path) { return inner.Exists(path); }
        public long Length(string path) { return inner.Length(path); }
        public string Read(string path) { return inner.Read(path); }
        public void Write(string path, string payload)
        {
            Operations.Add("Write " + Path.GetFileName(path));
            if (ShouldFail("Write")) {
                if (Interrupted && payload.Length > 1) inner.Write(path, payload.Substring(0, payload.Length / 2));
                throw new IOException("Matrix fixture failure");
            }
            inner.Write(path, payload);
        }
        public void Copy(string source, string destination, bool overwrite)
        {
            Operations.Add("Copy " + Path.GetFileName(destination));
            if (ShouldFail("Copy")) {
                if (Interrupted) inner.Copy(source, destination, overwrite);
                throw new IOException("Matrix fixture failure");
            }
            inner.Copy(source, destination, overwrite);
        }
        public void Move(string source, string destination)
        {
            Operations.Add("Move " + Path.GetFileName(destination));
            if (ShouldFail("Move")) {
                if (Interrupted) inner.Move(source, destination);
                throw new IOException("Matrix fixture failure");
            }
            inner.Move(source, destination);
        }
        public void Replace(string source, string destination)
        {
            Operations.Add("Replace " + Path.GetFileName(destination));
            if (ShouldFail("Replace")) {
                if (Interrupted) inner.Replace(source, destination);
                throw new IOException("Matrix fixture failure");
            }
            inner.Replace(source, destination);
        }
        public void Delete(string path)
        {
            Operations.Add("Delete " + Path.GetFileName(path));
            if (ShouldFail("Delete")) throw new IOException("Matrix fixture failure");
            inner.Delete(path);
        }
    }

    // Fails a chosen operation by path so recovery cleanup order can be tested precisely.
    private sealed class TargetedFaultingFiles : IStoreFiles
    {
        private readonly IStoreFiles inner = new PhysicalStoreFiles();
        public Func<string, string, bool> Fail;
        private void Before(string operation, string path)
        {
            if (Fail != null && Fail(operation, path)) throw new IOException("Targeted fixture failure");
        }
        public bool Exists(string path) { return inner.Exists(path); }
        public long Length(string path) { return inner.Length(path); }
        public string Read(string path) { Before("Read", path); return inner.Read(path); }
        public void Write(string path, string payload) { Before("Write", path); inner.Write(path, payload); }
        public void Copy(string source, string destination, bool overwrite)
        { Before("Copy", destination); inner.Copy(source, destination, overwrite); }
        public void Move(string source, string destination) { Before("Move", destination); inner.Move(source, destination); }
        public void Replace(string source, string destination) { Before("Replace", destination); inner.Replace(source, destination); }
        public void Delete(string path) { Before("Delete", path); inner.Delete(path); }
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
    // Blocks the first background write so a test can interleave a clear with a save that
    // already passed its epoch check but has not published yet.
    private sealed class BlockingWriteFiles : IStoreFiles
    {
        private readonly IStoreFiles inner = new PhysicalStoreFiles();
        public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
        public bool BlockWrites;
        public bool Exists(string path) { return inner.Exists(path); }
        public long Length(string path) { return inner.Length(path); }
        public string Read(string path) { return inner.Read(path); }
        public void Write(string path, string payload)
        {
            if (BlockWrites) { Entered.Set(); Release.Wait(15000); }
            inner.Write(path, payload);
        }
        public void Copy(string source, string destination, bool overwrite)
        { inner.Copy(source, destination, overwrite); }
        public void Move(string source, string destination) { inner.Move(source, destination); }
        public void Replace(string source, string destination) { inner.Replace(source, destination); }
        public void Delete(string path) { inner.Delete(path); }
    }
    private sealed class FakeCredentialProfiles : ICredentialProfiles
    {
        private readonly Dictionary<string, string> keys = new Dictionary<string, string>();
        public bool FailNextSave;
        public Func<string, bool> FailDelete;
        public readonly List<string> Log = new List<string>();
        public string Read(ProviderProfile profile)
        {
            string key; string id = Credentials.ScopedId(profile);
            Log.Add("read " + id);
            return keys.TryGetValue(id, out key) ? key : "";
        }
        public void Save(ProviderProfile profile, string key)
        {
            string id = Credentials.ScopedId(profile);
            Log.Add("save " + id + " " + key);
            if (FailNextSave) { FailNextSave = false; throw new UserError("credentials", "Fixture failure"); }
            keys[id] = key;
        }
        public void Delete(ProviderProfile profile)
        {
            string id = Credentials.ScopedId(profile);
            Log.Add("delete " + id);
            if (FailDelete != null && FailDelete(id)) throw new UserError("credentials", "Fixture delete failure");
            keys.Remove(id);
        }
    }
}
