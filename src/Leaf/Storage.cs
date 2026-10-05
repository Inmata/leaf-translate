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
        public Settings Settings { get { lock (sync) return settings; } }
        public string Warning { get; private set; }
        public bool RecoveryBlocked { get; private set; }
        public string Folder { get { return folder; } }

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
                if (!candidate.HistoryEnabled) {
                    candidateRecords = new List<TranslationRecord>();
                    payloads[AtomicFileBatch.HistoryName] = Json.Write(candidateRecords);
                } else if (candidate.HistoryLimit != latest.HistoryLimit) {
                    candidateRecords = currentRecords.OrderByDescending(r => r.UpdatedUtcTicks)
                        .Take(candidate.HistoryLimit).ToList();
                    payloads[AtomicFileBatch.HistoryName] = Json.Write(candidateRecords);
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

        public TranslationRecord Find(string key)
        {
            lock (sync) return records.FirstOrDefault(r => r.CacheKey == key);
        }

        public List<TranslationRecord> History(string query)
        {
            lock (sync) {
                query = (query ?? "").Trim();
                return records.Where(r => query.Length == 0 || r.Source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Translation ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(r => r.UpdatedUtcTicks).ToList();
            }
        }

        public void Save(TranslationRecord record)
        {
            if (record == null || !record.Completed) return;
            lock (sync) {
                if (!settings.HistoryEnabled) return;
                var candidate = records.Where(r => r.Id != record.Id && r.CacheKey != record.CacheKey).ToList();
                record.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                candidate.Insert(0, record);
                candidate = TrimRecords(candidate, settings.HistoryLimit);
                CommitHistory(candidate);
                records = candidate;
            }
        }

        public void Delete(string id)
        {
            lock (sync) {
                var candidate = records.Where(r => r.Id != id).ToList();
                CommitHistory(candidate);
                records = candidate;
            }
        }

        public void Clear()
        {
            lock (sync) {
                var candidate = new List<TranslationRecord>();
                CommitHistory(candidate);
                records = candidate;
            }
        }

        private void CommitHistory(List<TranslationRecord> candidate)
        {
            var payloads = new Dictionary<string, string> {
                { AtomicFileBatch.HistoryName, Json.Write(candidate) }
            };
            var batch = new AtomicFileBatch(folder, files);
            try { batch.Commit(payloads, null, null, null); }
            finally { if (batch.RecoveryBlocked) RecoveryBlocked = true; }
        }

        private static List<TranslationRecord> TrimRecords(List<TranslationRecord> candidate, int limit)
        {
            var trimmed = candidate.OrderByDescending(r => r.UpdatedUtcTicks).Take(limit).ToList();
            while (trimmed.Count > 1 && Json.Write(trimmed).Length > 4194304) trimmed.RemoveAt(trimmed.Count - 1);
            return trimmed;
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
                parsed.Normalize();
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
                return parsed.Where(r => r != null).ToList();
            } catch {
                BackupFile(path, AtomicFileBatch.HistoryName);
                Warning = "一份本地数据无法读取，已尽量保留备份。请检查设置。";
                return new List<TranslationRecord>();
            }
        }

        private void BackupFile(string path, string name)
        {
            try {
                files.Copy(path, Path.Combine(folder,
                    name + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")), false);
            } catch { }
        }

        private sealed class LegacyPlacement
        {
            public string Monitor { get; set; }
            public Dictionary<string, PointSetting> Positions { get; set; }
        }
    }
}
