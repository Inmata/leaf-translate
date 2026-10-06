using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Leaf
{
    // Thin file operations seam so storage regression checks can inject real I/O faults
    // without touching the user profile. Paths are always validated local absolute paths
    // owned by LocalStore; no session object or credential ever crosses this interface.
    public interface IStoreFiles
    {
        bool Exists(string path);
        long Length(string path);
        string Read(string path);
        void Write(string path, string payload);
        void Copy(string source, string destination, bool overwrite);
        void Move(string source, string destination);
        void Replace(string source, string destination);
        void Delete(string path);
    }

    public sealed class PhysicalStoreFiles : IStoreFiles
    {
        public bool Exists(string path) { return File.Exists(path); }
        public long Length(string path) { return new FileInfo(path).Length; }
        public string Read(string path) { return File.ReadAllText(path, Encoding.UTF8); }
        public void Write(string path, string payload)
        { File.WriteAllText(path, payload, new UTF8Encoding(false)); }
        public void Copy(string source, string destination, bool overwrite)
        { File.Copy(source, destination, overwrite); }
        public void Move(string source, string destination) { File.Move(source, destination); }
        public void Replace(string source, string destination) { File.Replace(source, destination, null); }
        public void Delete(string path) { File.Delete(path); }
    }

    // Groups the fixed local files behind one recoverable transaction:
    // stage *.new, back up *.old, atomically write a prepared marker, apply external side
    // effects, swap targets, atomically write the committed marker, publish memory, clean up.
    // Recovery never claims success without evidence: a corrupt marker, duplicate targets or a
    // missing backup keeps every artifact and blocks further commits. Restores copy from the
    // backup (never consume it) so a crash during recovery can be retried from the same marker.
    // Only settings.json and history.json are accepted, so a tampered marker cannot redirect
    // recovery outside the store directory.
    public sealed class AtomicFileBatch
    {
        public const string SettingsName = "settings.json";
        public const string HistoryName = "history.json";
        public const string MarkerName = "store-transaction.json";
        private static readonly string[] AllowedNames = { SettingsName, HistoryName };
        private readonly string directory;
        private readonly IStoreFiles files;
        public bool RecoveryBlocked { get; private set; }
        // True when files were restored but an applied external effect could not be rolled
        // back; the app must not claim the old configuration is fully active again.
        public bool ExternalRollbackPending { get; private set; }
        // True when Recover(true) kept old evidence because the replacement commit must first
        // become durable before the pending external state can count as resolved.
        public bool ExternalResolutionRetained { get; private set; }

        public AtomicFileBatch(string directory, IStoreFiles files)
        {
            this.directory = directory; this.files = files;
        }

        public sealed class TransactionTarget
        {
            public string Name { get; set; }
            public bool Existed { get; set; }
        }
        private sealed class TransactionState
        {
            public string State { get; set; }
            public bool ExternalApplied { get; set; }
            public List<TransactionTarget> Targets { get; set; }
        }

        public void Commit(Dictionary<string, string> payloads, Action applyExternal, Action rollbackExternal, Action publish)
        {
            Commit(payloads, applyExternal, rollbackExternal, publish, false);
        }

        public void Commit(Dictionary<string, string> payloads, Action applyExternal, Action rollbackExternal, Action publish,
            bool resolveExternalPending)
        {
            if (payloads == null || payloads.Count == 0) throw new ArgumentException("payloads");
            Recover(resolveExternalPending);
            if (RecoveryBlocked)
                throw new UserError("storage", "上一次保存尚未完全恢复，请重新应用配置后重试。");
            if (ExternalRollbackPending)
                throw new UserError("storage", "上次保存的外部设置未能恢复，请重新应用配置后重试。");
            bool inheritedExternal = ExternalResolutionRetained;

            string[] names = payloads.Keys.OrderBy(name => name == SettingsName ? 0 : 1).ToArray();
            foreach (string name in names) ValidateName(name);
            var targets = names.Select(name => new TransactionTarget {
                Name = name, Existed = files.Exists(Path.Combine(directory, name))
            }).ToList();
            var staged = new Dictionary<string, string>();
            // A reapply inherits the earlier external uncertainty: the new prepared marker must
            // keep it recorded so a mid-commit failure still blocks requests after a restart.
            bool markedExternalApplied = applyExternal != null || inheritedExternal;
            bool committed = false;
            bool newMarkerWritten = false;
            try {
                foreach (var item in targets) {
                    string target = Path.Combine(directory, item.Name);
                    string fresh = target + ".new", backup = target + ".old";
                    files.Write(fresh, payloads[item.Name]);
                    if (item.Existed) files.Copy(target, backup, true);
                    else files.Delete(backup);
                    staged[item.Name] = fresh;
                }
                // The prepared marker records whether external effects may have run. Writing it
                // before applyExternal keeps recovery conservative if the process dies mid-apply.
                WriteMarker("prepared", markedExternalApplied, targets);
                newMarkerWritten = true;
                if (applyExternal != null) applyExternal();
                foreach (var item in targets) {
                    string target = Path.Combine(directory, item.Name);
                    if (item.Existed) files.Replace(staged[item.Name], target);
                    else files.Move(staged[item.Name], target);
                }
                WriteMarker("committed", true, targets);
                committed = true;
                if (publish != null) publish();
                try { Cleanup(targets); } catch { }
            } catch (Exception error) {
                if (committed) {
                    // The new files are durable; the committed marker lets startup clean up.
                    if (error is UserError) throw;
                    throw new UserError("storage", "本地数据已保存，但清理未完成；下次启动会自动处理。", error);
                }
                // Without a durable prepared marker no target was replaced, so there is nothing
                // to restore from a backup and missing staging backups must not block recovery.
                bool filesRestored = newMarkerWritten ? TryRestore(targets) : true;
                bool externalRestored = !markedExternalApplied;
                if (filesRestored && rollbackExternal != null) {
                    try { rollbackExternal(); externalRestored = true; }
                    catch { externalRestored = false; }
                }
                if (inheritedExternal) {
                    // The earlier external uncertainty is unresolved until the reapply fully
                    // commits; whichever marker is durable stays as restart evidence.
                    if (!filesRestored) RecoveryBlocked = true;
                    ExternalRollbackPending = true;
                    throw new UserError("storage", "本地保存失败，且上次保存的外部设置尚未确认。请重新应用配置后重试。", error);
                }
                if (filesRestored && externalRestored) {
                    RemoveMarkerOrBlock(targets);
                } else if (filesRestored && !externalRestored) {
                    // The files are old again; only the external effects are uncertain and need
                    // an explicit reapply before requests may run.
                    ExternalRollbackPending = true;
                } else {
                    RecoveryBlocked = true;
                    ExternalRollbackPending = markedExternalApplied;
                }
                if (RecoveryBlocked || ExternalRollbackPending)
                    throw new UserError("storage", "本地保存失败，且未能完全恢复原状态。请重新应用配置后重试。", error);
                if (error is UserError) throw;
                throw new UserError("storage", "无法保存本地数据。请检查磁盘空间或目录权限。", error);
            }
        }

        public void Recover() { Recover(false); }

        // resolveExternalPending is the explicit user reapply path. It keeps the old marker,
        // backups and quarantine copies in place; only a replacement commit that becomes
        // durable supersedes them, so a failed reapply cannot look like a clean store.
        public void Recover(bool resolveExternalPending)
        {
            ExternalResolutionRetained = false;
            string marker = Path.Combine(directory, MarkerName);
            if (!files.Exists(marker)) { CleanupOrphans(); return; }
            TransactionState state = null;
            try { state = Json.Read<TransactionState>(files.Read(marker)); } catch { state = null; }
            if (!IsValidState(state)) {
                if (!resolveExternalPending) { RecoveryBlocked = true; return; }
                // Preserve the unreadable bytes for inspection but keep the marker itself until
                // the replacement commit is durable; deleting it now would erase the only
                // durable hint that this store still needs an explicit recovery.
                if (!QuarantineBadMarker()) { RecoveryBlocked = true; return; }
                ExternalResolutionRetained = true;
                return;
            }
            if (state.State == "committed") { RemoveMarkerOrBlock(state.Targets); return; }
            if (!TryRestore(state.Targets)) { RecoveryBlocked = true; return; }
            if (state.ExternalApplied) {
                if (!resolveExternalPending) {
                    // The files are back to the old state, but applied credentials/hotkeys may not
                    // be. Keep the marker as durable evidence and require an explicit reapply.
                    ExternalRollbackPending = true;
                    return;
                }
                ExternalResolutionRetained = true;
                return;
            }
            RemoveMarkerOrBlock(state.Targets);
        }

        private bool QuarantineBadMarker()
        {
            string marker = Path.Combine(directory, MarkerName);
            try {
                // Quarantine once; repeated reapply attempts must not multiply evidence copies.
                if (Directory.GetFiles(directory, MarkerName + ".corrupt-*").Length > 0) return true;
                files.Copy(marker, marker + ".corrupt-" + Stamp(), false);
                return true;
            } catch { return false; }
        }

        private static string Stamp()
        {
            return DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        }

        private static bool IsValidState(TransactionState state)
        {
            if (state == null || state.Targets == null || state.Targets.Count == 0) return false;
            if (state.State != "prepared" && state.State != "committed") return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in state.Targets) {
                if (item == null || !IsAllowed(item.Name)) return false;
                if (!names.Add(item.Name)) return false;
            }
            return true;
        }

        private void WriteMarker(string state, bool externalApplied, List<TransactionTarget> targets)
        {
            string marker = Path.Combine(directory, MarkerName), temporary = marker + ".tmp";
            files.Write(temporary, Json.Write(new TransactionState {
                State = state, ExternalApplied = externalApplied, Targets = targets
            }));
            if (files.Exists(marker)) files.Replace(temporary, marker);
            else files.Move(temporary, marker);
        }

        // Restores each target by copying the backup over it; the backup is kept until the
        // marker has been removed, so an interrupted recovery can always run again.
        private bool TryRestore(List<TransactionTarget> targets)
        {
            try {
                foreach (var item in targets) {
                    string target = Path.Combine(directory, item.Name), backup = target + ".old";
                    if (item.Existed) {
                        if (!files.Exists(backup)) return false;
                        files.Copy(backup, target, true);
                    } else files.Delete(target);
                }
                return true;
            } catch { return false; }
        }

        private bool RemoveMarkerOrBlock(List<TransactionTarget> targets)
        {
            string marker = Path.Combine(directory, MarkerName);
            if (!TryDelete(marker)) { RecoveryBlocked = true; return false; }
            Cleanup(targets);
            return true;
        }

        private void Cleanup(List<TransactionTarget> targets)
        {
            foreach (var item in targets) {
                string target = Path.Combine(directory, item.Name);
                TryDelete(target + ".new"); TryDelete(target + ".old");
            }
            TryDelete(Path.Combine(directory, MarkerName));
            TryDelete(Path.Combine(directory, MarkerName + ".tmp"));
        }

        private void CleanupOrphans()
        {
            foreach (string name in AllowedNames) {
                string target = Path.Combine(directory, name);
                TryDelete(target + ".new"); TryDelete(target + ".old");
            }
            TryDelete(Path.Combine(directory, MarkerName + ".tmp"));
        }

        private bool TryDelete(string path)
        {
            try { if (files.Exists(path)) files.Delete(path); return !files.Exists(path); } catch { return false; }
        }
        private static bool IsAllowed(string name)
        {
            return name == SettingsName || name == HistoryName;
        }
        private static void ValidateName(string name)
        {
            if (!IsAllowed(name) || name.Contains("..") || Path.IsPathRooted(name))
                throw new InvalidDataException("Store transactions only accept the fixed local files.");
        }
    }
}
