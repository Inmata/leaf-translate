using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Leaf
{
    public sealed class LocalStore
    {
        private readonly string folder;
        private readonly object sync = new object();
        private List<TranslationRecord> records;
        public Settings Settings { get; private set; }
        public string Warning { get; private set; }
        public LocalStore(string directory)
        {
            folder = directory; Directory.CreateDirectory(folder);
            Settings = Read<Settings>("settings.json") ?? Leaf.Settings.Defaults(); Settings.Normalize();
            records = Read<List<TranslationRecord>>("history.json") ?? new List<TranslationRecord>();
            records = records.Where(r => r != null && r.Completed && r.Context != null && r.Source != null).ToList();
            foreach (var record in records) {
                record.Context.Normalize();
                if (record.Cards == null) record.Cards = new Dictionary<string, WordCard>();
                if (record.Chat == null) record.Chat = new List<ChatTurn>();
            }
            if (!Settings.HistoryEnabled) records.Clear();
            Trim();
        }
        public void SaveSettings(Settings settings)
        {
            lock (sync) {
                settings.Normalize(); Write("settings.json", settings); Settings = settings;
                if (!Settings.HistoryEnabled) records.Clear();
                Trim(); Write("history.json", records);
            }
        }
        public TranslationRecord Find(string key) { lock (sync) return records.FirstOrDefault(r => r.CacheKey == key); }
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
            if (!record.Completed) return;
            lock (sync) {
                if (!Settings.HistoryEnabled) return;
                records.RemoveAll(r => r.Id == record.Id || r.CacheKey == record.CacheKey);
                record.UpdatedUtcTicks = DateTime.UtcNow.Ticks; records.Insert(0, record);
                Trim(); Write("history.json", records);
            }
        }
        public void Delete(string id) { lock (sync) { records.RemoveAll(r => r.Id == id); Write("history.json", records); } }
        public void Clear() { lock (sync) { records.Clear(); Write("history.json", records); } }
        private void Trim()
        {
            records = records.OrderByDescending(r => r.UpdatedUtcTicks).Take(Settings.HistoryLimit).ToList();
            while (records.Count > 1 && Json.Write(records).Length > 4194304) records.RemoveAt(records.Count - 1);
        }
        private T Read<T>(string name) where T : class
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) return null;
            try {
                if (new FileInfo(path).Length > 33554432) throw new InvalidDataException();
                return Json.Read<T>(File.ReadAllText(path, Encoding.UTF8));
            } catch {
                try { File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); } catch { }
                Warning = "一份本地数据无法读取，已尽量保留备份。请检查设置。"; return null;
            }
        }
        private void Write(string name, object value)
        {
            string target = Path.Combine(folder, name), temporary = target + ".tmp";
            try {
                File.WriteAllText(temporary, Json.Write(value), new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
            } catch {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                throw new UserError("storage", "无法保存本地数据。请检查磁盘空间或目录权限。");
            }
        }
    }
    public static class Credentials
    {
        private const int Generic = 1;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential {
            public int Flags; public int Type; public string TargetName; public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist;
            public int AttributeCount; public IntPtr Attributes; public string TargetAlias; public string UserName;
        }
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref Credential credential, int flags);
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);
        [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
        private static string Target(string provider) { return "LeafTranslate/" + provider; }
        public static string Read(string provider)
        {
            IntPtr pointer;
            if (!CredRead(Target(provider), Generic, 0, out pointer)) return "";
            try {
                var credential = (Credential)Marshal.PtrToStructure(pointer, typeof(Credential));
                if (credential.CredentialBlobSize == 0) return "";
                byte[] bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            } finally { CredFree(pointer); }
        }
        public static void Save(string provider, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            byte[] bytes = Encoding.Unicode.GetBytes(key.Trim());
            if (bytes.Length > 2560) throw new UserError("key", "密钥过长，请检查填写内容。");
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            try {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                var credential = new Credential { Type = Generic, TargetName = Target(provider), UserName = "Leaf",
                    CredentialBlob = pointer, CredentialBlobSize = bytes.Length, Persist = 2 };
                if (!CredWrite(ref credential, 0)) throw new UserError("credentials", "无法保存密钥到 Windows 凭据管理器。");
            } finally {
                for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0);
                Marshal.FreeHGlobal(pointer); Array.Clear(bytes, 0, bytes.Length);
            }
        }
        public static void Delete(string provider)
        {
            if (!CredDelete(Target(provider), Generic, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new UserError("credentials", "无法删除已保存的密钥。");
        }
    }
}
