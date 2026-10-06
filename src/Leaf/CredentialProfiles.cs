using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Leaf
{
    // Credential access seam. AppShell creates the Windows-backed implementation only for
    // native runs; tests inject a fixture that never touches the real Credential Manager.
    public interface ICredentialProfiles
    {
        string Read(ProviderProfile profile);
        void Save(ProviderProfile profile, string key);
        void Delete(ProviderProfile profile);
    }

    public sealed class WindowsCredentialProfiles : ICredentialProfiles
    {
        public string Read(ProviderProfile profile) { return Credentials.Read(profile); }
        public void Save(ProviderProfile profile, string key) { Credentials.Save(profile, key); }
        public void Delete(ProviderProfile profile) { Credentials.Delete(profile); }
    }

    public static partial class Credentials
    {
        private const int Generic = 1;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
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

        // Legacy provider-only target, kept for migration and old callers.
        private static string Target(string provider) { return "LeafTranslate/" + provider; }

        public static string Read(string provider)
        {
            IntPtr pointer;
            if (!CredRead(Target(provider), Generic, 0, out pointer)) {
                if (Marshal.GetLastWin32Error() == 1168) return "";
                throw new UserError("credentials", "无法读取 Windows 中的已存密钥，请检查系统权限或重新保存密钥。");
            }
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
            if (Read(provider) != key.Trim())
                throw new UserError("credentials", "密钥写入后未能重新读取，请重新保存。");
        }

        public static void Delete(string provider)
        {
            if (!CredDelete(Target(provider), Generic, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new UserError("credentials", "无法删除已保存的密钥。");
        }

        // Binds the credential to the provider id plus the fully normalized endpoint, never to the key text.
        public static string ScopedId(ProviderProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Id))
                throw new UserError("credentials", "无法确定服务凭据目标。");
            string endpoint = LlmClient.Endpoint(profile.BaseUrl).AbsoluteUri;
            using (var sha = SHA256.Create()) {
                string digest = BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(endpoint))).Replace("-", "");
                return profile.Id + "/endpoint/" + digest;
            }
        }

        public static string Read(ProviderProfile profile) { return Read(ScopedId(profile)); }
        public static void Save(ProviderProfile profile, string key) { Save(ScopedId(profile), key); }
        public static void Delete(ProviderProfile profile) { Delete(ScopedId(profile)); }

        // Moves a legacy provider-only key to the endpoint-bound target. The profile must expose
        // a valid endpoint; the legacy target is deleted only after the new target reads back.
        public static void MigrateLegacyProfiles(IEnumerable<ProviderProfile> profiles)
        {
            if (profiles == null) return;
            foreach (var profile in profiles) {
                if (profile == null || string.IsNullOrWhiteSpace(profile.Id)) continue;
                try {
                    string scoped;
                    try { scoped = ScopedId(profile); } catch (UserError) { continue; }
                    if (Read(scoped).Length > 0) continue;
                    string legacy = Read(profile.Id);
                    if (legacy.Length == 0) continue;
                    Save(scoped, legacy);
                    if (Read(scoped) == legacy) Delete(profile.Id);
                } catch {
                    // Any failure keeps the legacy target and never sends an unbound key.
                }
            }
        }
    }
}
