using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Leaf
{
    public sealed class LocalStore
    {
        private const long ReadLimit = 33554432;
        private readonly string folder;
        private readonly IStoreFiles files;
        private readonly StoreQueue queue = new StoreQueue();
        private readonly object sync = new object();
        private List<TranslationRecord> records;
        private Settings settings;
        private long historyEpoch = 1;
        private readonly HashSet<string> tombstones = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<FailedWrite> failedWrites = new List<FailedWrite>();
        public Settings Settings { get { lock (sync) return settings; } }
        public string Warning { get; private set; }
        public bool RecoveryBlocked { get; private set; }
        public string Folder { get { return folder; } }
        public long HistoryEpoch { get { lock (sync) return historyEpoch; } }

        private sealed class FailedWrite
        {
            public Action Retry;
            public string Id;
            public long Epoch;
        }

        public LocalStore(string directory) : this(directory, new PhysicalStoreFiles()) { }

        public LocalStore(string directory, IStoreFiles files)
        {
            folder = directory; this.files = files;
            Directory.CreateDirectory(folder);
            var recovery = new AtomicFileBatch(folder, files);
            try { recovery.Recover(); } catch { RecoveryBlocked = true; }
            if (recovery.RecoveryBlocked) RecoveryBlocked = true;
            settings = ReadSettings() ?? Settings.Defaults();
            records = ReadHistory() ?? new List<TranslationRecord>();
            if (!settings.HistoryEnabled) records.Clear();
        }

        public Task SaveSettingsAsync(SettingsUpdate update)
        {
            return CommitSettingsAsync(update, null, null);
        }

        public Task CommitSettingsAsync(SettingsUpdate update, Action<Settings, Settings> applyExternal, Action<Settings, Settings> rollbackExternal)
        {
            if (update == null) throw new ArgumentNullException("update");
            return queue.Enqueue(() => {
                Settings latest; List<TranslationRecord> currentRecords;
                lock (sync) { latest = settings; currentRecords = records; }
                var candidate = update.Build(latest);
                var payloads = new Dictionary<string, string>();
                payloads[AtomicFileBatch.SettingsName] = Json.Write(candidate);
                List<TranslationRecord> candidateRecords = null;
                if (!candidate.HistoryEnabled && latest.HistoryEnabled) {
                    // Turning history off is a barrier: every snapshot captured before this
                    // point is invalid even if the user re-enables history later.
                    lock (sync) { historyEpoch++; tombstones.Clear(); failedWrites.Clear(); }
                }
                if (!candidate.HistoryEnabled) {
                    candidateRecords = new List<TranslationRecord>();
                    payloads[AtomicFileBatch.HistoryName] =
                        HistorySnapshots.Encode(candidateRecords, candidate.HistoryLimit).Json;
                } else if (candidate.HistoryLimit != latest.HistoryLimit) {
                    var limited = currentRecords.OrderByDescending(r => r.UpdatedUtcTicks)
                        .Take(candidate.HistoryLimit).ToList();
                    var encoded = HistorySnapshots.Encode(limited, candidate.HistoryLimit);
                    candidateRecords = encoded.Records;
                    payloads[AtomicFileBatch.HistoryName] = encoded.Json;
                }
                var batch = new AtomicFileBatch(folder, files);
                try {
                    batch.Commit(payloads,
                        applyExternal == null ? (Action)null : () => applyExternal(latest, candidate),
                        rollbackExternal == null ? (Action)null : () => rollbackExternal(latest, candidate),
                        () => {
                            lock (sync) {
                                settings = candidate;
                                if (candidateRecords != null) records = candidateRecords;
                            }
                            if (candidateRecords != null && !candidate.HistoryEnabled) DeleteHistoryBackups();
                        });
                } finally {
                    if (batch.RecoveryBlocked) RecoveryBlocked = true;
                }
            });
        }

        // Compatibility wrapper for imperative tests; the UI never calls a synchronous queue wait.
        public void SaveSettings(Settings settings)
        {
            SaveSettingsAsync(SettingsUpdate.Full(settings)).GetAwaiter().GetResult();
        }

        public Task SavePlacementAsync(WindowPlacement placement)
        {
            return SaveSettingsAsync(SettingsUpdate.Placement(placement));
        }

        public void SavePlacement(WindowPlacement placement)
        {
            SavePlacementAsync(placement).GetAwaiter().GetResult();
        }

        public Task FlushAsync() { return queue.FlushAsync(); }

        // Replays failed history writes in their original order after re-checking the
        // epoch/tombstone barriers. Only fully resolved failures are acknowledged.
        public async Task RetryFailedWritesAsync()
        {
            List<FailedWrite> pending;
            lock (sync) pending = failedWrites.ToList();
            long through = queue.FailureWatermark;
            bool allResolved = true;
            foreach (var item in pending) {
                var capture = item;
                try {
                    await queue.Enqueue(() => {
                        capture.Retry();
                        lock (sync) failedWrites.Remove(capture);
                    }).ConfigureAwait(false);
                } catch {
                    allResolved = false;
                }
            }
            if (allResolved) queue.AcknowledgeFailures(through);
        }

        public TranslationRecord Find(string key)
        {
            lock (sync) return HistorySnapshots.Copy(records.FirstOrDefault(r => r.CacheKey == key));
        }

        public List<TranslationRecord> History(string query)
        {
            lock (sync) {
                query = (query ?? "").Trim();
                return records.Where(r => query.Length == 0 || r.Source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Translation ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(r => r.UpdatedUtcTicks).Select(HistorySnapshots.Copy).ToList();
            }
        }

        public void Save(TranslationRecord record) { SaveAsync(record, HistoryEpoch).GetAwaiter().GetResult(); }
        public Task SaveAsync(TranslationRecord record) { return SaveAsync(record, HistoryEpoch); }
        public Task SaveAsync(TranslationRecord record, long expectedEpoch)
        {
            if (record == null || !record.Completed) return Task.FromResult(0);
            var snapshot = HistorySnapshots.Copy(record);
            return EnqueueHistory(() => SaveCore(snapshot, expectedEpoch), snapshot.Id, expectedEpoch);
        }

        public void Delete(string id) { DeleteAsync(id).GetAwaiter().GetResult(); }
        public Task DeleteAsync(string id)
        {
            if (string.IsNullOrEmpty(id)) return Task.FromResult(0);
            long epoch;
            lock (sync) {
                tombstones.Add(id);
                failedWrites.RemoveAll(item => item.Id == id);
                epoch = historyEpoch;
            }
            return EnqueueHistory(() => DeleteCore(id), id, epoch);
        }

        public void Clear() { ClearAsync().GetAwaiter().GetResult(); }
        public Task ClearAsync()
        {
            lock (sync) { historyEpoch++; tombstones.Clear(); failedWrites.Clear(); }
            return EnqueueHistory(ClearCore, null, HistoryEpoch);
        }

        private Task EnqueueHistory(Action action, string id, long epoch)
        {
            return queue.Enqueue(() => {
                try { action(); }
                catch (Exception) {
                    lock (sync) failedWrites.Add(new FailedWrite { Retry = action, Id = id, Epoch = epoch });
                    throw;
                }
            });
        }
        private void SaveCore(TranslationRecord snapshot, long expectedEpoch)
        {
            var candidate = new List<TranslationRecord>();
            int limit;
            lock (sync) {
                if (!settings.HistoryEnabled) return;
                if (expectedEpoch != historyEpoch) return;
                if (tombstones.Contains(snapshot.Id)) return;
                candidate = records.Where(r => r.Id != snapshot.Id && r.CacheKey != snapshot.CacheKey).ToList();
                snapshot.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                candidate.Insert(0, snapshot);
                limit = settings.HistoryLimit;
            }
            var payload = HistorySnapshots.Encode(candidate, limit);
            if (payload.Utf8Bytes > HistorySnapshots.HardLimit)
                throw new UserError("length", "单条历史记录过大，无法安全保存。请缩小输入后重试。");
            CommitHistoryPayload(payload);
        }
        private void DeleteCore(string id)
        {
            List<TranslationRecord> candidate;
            int limit;
            lock (sync) {
                candidate = records.Where(r => r.Id != id).ToList();
                limit = settings.HistoryLimit;
            }
            CommitHistoryPayload(HistorySnapshots.Encode(candidate, limit));
        }
        private void ClearCore()
        {
            CommitHistoryPayload(HistorySnapshots.Encode(new List<TranslationRecord>(), Settings.HistoryLimit));
            DeleteHistoryBackups();
        }
        private void CommitHistoryPayload(HistorySnapshots.HistoryPayload payload)
        {
            var payloads = new Dictionary<string, string> {
                { AtomicFileBatch.HistoryName, payload.Json }
            };
            var batch = new AtomicFileBatch(folder, files);
            try {
                batch.Commit(payloads, null, null, () => { lock (sync) records = payload.Records; });
            } finally {
                if (batch.RecoveryBlocked) RecoveryBlocked = true;
            }
        }

        private Settings ReadSettings()
        {
            string path = Path.Combine(folder, AtomicFileBatch.SettingsName);
            if (!files.Exists(path)) return null;
            try {
                if (files.Length(path) > ReadLimit) throw new InvalidDataException();
                string payload = files.Read(path);
                var parsed = Json.Read<Settings>(payload);
                if (parsed == null) throw new InvalidDataException();
                if (parsed.Placement == null) {
                    var legacy = Json.Read<LegacyPlacement>(payload);
                    if (legacy.Positions != null && legacy.Positions.Count > 0) {
                        var position = legacy.Positions.FirstOrDefault(p => p.Key == legacy.Monitor);
                        if (position.Value == null) position = legacy.Positions.First();
                        parsed.Placement = new WindowPlacement { X = position.Value.X, Y = position.Value.Y,
                            Width = 456, Height = 620, Screen = position.Key };
                    }
                }
                parsed.NormalizeLoaded();
                return parsed;
            } catch {
                BackupFile(path, AtomicFileBatch.SettingsName);
                Warning = "一份本地数据无法读取，已尽量保留备份。请检查设置。";
                return null;
            }
        }

        private List<TranslationRecord> ReadHistory()
        {
            string path = Path.Combine(folder, AtomicFileBatch.HistoryName);
            if (!files.Exists(path)) return new List<TranslationRecord>();
            try {
                if (files.Length(path) > ReadLimit) throw new InvalidDataException();
                var parsed = Json.Read<List<TranslationRecord>>(files.Read(path));
                if (parsed == null) return new List<TranslationRecord>();
                var kept = new List<TranslationRecord>(); int rejected = 0;
                foreach (var record in parsed) {
                    if (record != null && record.TryNormalizeLoaded()) kept.Add(record);
                    else rejected++;
                }
                if (rejected > 0) {
                    QuarantineFile(path, AtomicFileBatch.HistoryName);
                    Warning = "部分历史记录已跳过，原始数据已保留一份隔离副本。";
                }
                return kept;
            } catch {
                BackupFile(path, AtomicFileBatch.HistoryName);
                Warning = "一份本地数据无法读取，已尽量保留备份。请检查设置。";
                return new List<TranslationRecord>();
            }
        }

        private void BackupFile(string path, string name)
        {
            try {
                files.Copy(path, Path.Combine(folder, name + ".corrupt-" + Stamp()), false);
            } catch { }
        }
        private void QuarantineFile(string path, string name)
        {
            try {
                files.Copy(path, Path.Combine(folder, name + ".quarantine-" + Stamp()), false);
            } catch { }
        }
        private static string Stamp()
        {
            return DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        }
        private void DeleteHistoryBackups()
        {
            try {
                foreach (string pattern in new[] { "history.json.corrupt-*", "history.json.quarantine-*" })
                    foreach (string path in Directory.GetFiles(folder, pattern))
                        try { files.Delete(path); } catch { }
            } catch { }
        }

        private sealed class LegacyPlacement
        {
            public string Monitor { get; set; }
            public Dictionary<string, PointSetting> Positions { get; set; }
        }
    }
}
