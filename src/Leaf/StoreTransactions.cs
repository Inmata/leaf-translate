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
    // stage *.new, back up *.old, write a prepared marker, apply external side effects,
    // swap targets, write the committed marker, publish memory, then clean up.
    // Only settings.json and history.json are accepted, so a tampered marker cannot
    // redirect recovery outside the store directory.
    public sealed class AtomicFileBatch
    {
        public const string SettingsName = "settings.json";
        public const string HistoryName = "history.json";
        public const string MarkerName = "store-transaction.json";
        private static readonly string[] AllowedNames = { SettingsName, HistoryName };
        private readonly string directory;
        private readonly IStoreFiles files;
        public bool RecoveryBlocked { get; private set; }

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
            public List<TransactionTarget> Targets { get; set; }
        }

        public void Commit(Dictionary<string, string> payloads, Action applyExternal, Action rollbackExternal, Action publish)
        {
            if (payloads == null || payloads.Count == 0) throw new ArgumentException("payloads");
            string[] names = payloads.Keys.OrderBy(name => name == SettingsName ? 0 : 1).ToArray();
            foreach (string name in names) ValidateName(name);
            var targets = new List<TransactionTarget>();
            var staged = new Dictionary<string, string>();
            bool committed = false;
            try {
                foreach (string name in names) {
                    string target = Path.Combine(directory, name);
                    string fresh = target + ".new", backup = target + ".old";
                    files.Write(fresh, payloads[name]);
                    bool existed = files.Exists(target);
                    if (existed) files.Copy(target, backup, true);
                    else files.Delete(backup);
                    staged[name] = fresh;
                    targets.Add(new TransactionTarget { Name = name, Existed = existed });
                }
                WriteMarker("prepared", targets);
                if (applyExternal != null) applyExternal();
                foreach (var item in targets) {
                    string target = Path.Combine(directory, item.Name);
                    if (files.Exists(target)) files.Replace(staged[item.Name], target);
                    else files.Move(staged[item.Name], target);
                }
                WriteMarker("committed", targets);
                committed = true;
                if (publish != null) publish();
                try { Cleanup(targets); } catch { }
            } catch (Exception error) {
                if (committed) {
                    // The new files are durable; the committed marker lets startup clean up.
                    if (error is UserError) throw;
                    throw new UserError("storage", "本地数据已保存，但清理未完成；下次启动会自动处理。");
                }
                bool filesRestored = TryRestore(targets);
                bool externalRestored = true;
                if (filesRestored && rollbackExternal != null) {
                    try { rollbackExternal(); } catch { externalRestored = false; }
                }
                if (filesRestored && externalRestored) {
                    try { Cleanup(targets); } catch { }
                } else {
                    RecoveryBlocked = true;
                }
                if (RecoveryBlocked)
                    throw new UserError("storage", "本地保存失败，且未能完全恢复原状态。请重新应用配置后重试。");
                if (error is UserError) throw;
                throw new UserError("storage", "无法保存本地数据。请检查磁盘空间或目录权限。");
            }
        }

        public void Recover()
        {
            string marker = Path.Combine(directory, MarkerName);
            if (!files.Exists(marker)) { CleanupOrphans(); return; }
            TransactionState state = null;
            try { state = Json.Read<TransactionState>(files.Read(marker)); } catch { state = null; }
            if (state == null || state.Targets == null || state.Targets.Count == 0 ||
                (state.State != "prepared" && state.State != "committed")) {
                TryDelete(marker); CleanupOrphans(); return;
            }
            var targets = new List<TransactionTarget>();
            foreach (var item in state.Targets) {
                if (item == null || !IsAllowed(item.Name)) { RecoveryBlocked = true; return; }
                targets.Add(item);
            }
            if (state.State == "committed") { Cleanup(targets); return; }
            if (!TryRestore(targets)) { RecoveryBlocked = true; return; }
            Cleanup(targets);
        }

        private void WriteMarker(string state, List<TransactionTarget> targets)
        {
            files.Write(Path.Combine(directory, MarkerName),
                Json.Write(new TransactionState { State = state, Targets = targets }));
        }
        private bool TryRestore(List<TransactionTarget> targets)
        {
            try {
                foreach (var item in targets) {
                    string target = Path.Combine(directory, item.Name), backup = target + ".old";
                    if (item.Existed) {
                        if (files.Exists(backup)) {
                            if (files.Exists(target)) files.Replace(backup, target);
                            else files.Move(backup, target);
                        }
                    } else files.Delete(target);
                }
                return true;
            } catch { return false; }
        }
        private void Cleanup(List<TransactionTarget> targets)
        {
            foreach (var item in targets) {
                string target = Path.Combine(directory, item.Name);
                TryDelete(target + ".new"); TryDelete(target + ".old");
            }
            TryDelete(Path.Combine(directory, MarkerName));
        }
        private void CleanupOrphans()
        {
            foreach (string name in AllowedNames) {
                string target = Path.Combine(directory, name);
                TryDelete(target + ".new"); TryDelete(target + ".old");
            }
        }
        private void TryDelete(string path)
        {
            try { if (files.Exists(path)) files.Delete(path); } catch { }
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
