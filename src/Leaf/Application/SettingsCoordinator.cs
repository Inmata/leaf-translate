using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Leaf
{
    internal sealed class SettingsCoordinator
    {
        private readonly AppShell shell;
        private readonly ICredentialProfiles credentialProfiles;
        private readonly bool migrateLegacyCredentials;
        private bool credentialMigrationDone;
        private Task lastSettingsTask = Task.FromResult(0);
        private LocalStore Store { get { return shell.Store; } }
        private DiagnosticLog Log { get { return shell.Log; } }
        internal SettingsCoordinator(AppShell shell, ICredentialProfiles credentials)
        {
            this.shell = shell;
            if (credentials != null) credentialProfiles = credentials;
            else if (shell.NativeEnabled) { credentialProfiles = new WindowsCredentialProfiles(); migrateLegacyCredentials = true; }
        }

        public Task ApplySettingsAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted)
        {
            return ApplySettingsAsync(update, keys, deleted, false);
        }

        // explicitApply is set only by the settings Save button: it resolves a pending external
        // rollback or an unreadable transaction marker, and is never implied by an auto-save.
        public Task ApplySettingsAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted, bool explicitApply)
        {
            if (update == null) throw new ArgumentNullException("update");
            EnsureCredentialMigration();
            var previous = Json.Copy(Store.Settings);
            var changedKeys = keys == null ? new Dictionary<string, string>() : new Dictionary<string, string>(keys);
            var removedKeys = deleted == null ? new HashSet<string>() : new HashSet<string>(deleted);
            Task task = ApplySettingsCoreAsync(update, changedKeys, removedKeys, previous, explicitApply);
            lastSettingsTask = task;
            return task;
        }

        public async Task WaitForSettingsAsync()
        {
            Task pending;
            do {
                pending = lastSettingsTask;
                if (pending != null) await pending.ConfigureAwait(true);
                await Store.FlushAsync().ConfigureAwait(true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            } while (!ReferenceEquals(pending, lastSettingsTask));
        }

        internal async Task ApplySettingsCoreAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted, Settings previous, bool explicitApply)
        {
            var keyUndo = new List<Action>();
            var replaced = new List<KeyValuePair<ProviderProfile, ProviderProfile>>();
            Action<Settings, Settings> applyExternal = (committed, candidate) => {
                keyUndo.Clear(); replaced.Clear();
                var failures = new List<string>();
                if (credentialProfiles != null) {
                    // Capture the original value of every scope this change can touch before any
                    // write, including a key that may already exist at the new endpoint.
                    foreach (string id in keys.Keys.Concat(deleted).Distinct().ToArray()) {
                        var oldProfile = committed.Providers.FirstOrDefault(p => p.Id == id);
                        var newProfile = candidate.Providers.FirstOrDefault(p => p.Id == id);
                        string oldKey = "", newOriginalKey = "";
                        if (oldProfile != null) oldKey = ReadCredentialOrThrow(oldProfile, "旧");
                        if (newProfile != null && !SameCredentialScope(oldProfile, newProfile))
                            newOriginalKey = ReadCredentialOrThrow(newProfile, "当前");
                        var capturedOld = oldProfile; var capturedNew = newProfile;
                        string capturedOldKey = oldKey, capturedNewKey = newOriginalKey;
                        keyUndo.Add(() => {
                            var problems = new List<string>();
                            try { RestoreCredential(capturedNew, capturedNewKey); } catch { problems.Add("新地址"); }
                            if (!SameCredentialScope(capturedOld, capturedNew))
                                try { RestoreCredential(capturedOld, capturedOldKey); } catch { problems.Add("旧地址"); }
                            if (problems.Count > 0) throw new UserError("credentials", "未能恢复密钥作用域。");
                        });
                        if (deleted.Contains(id) && newProfile != null) {
                            try { credentialProfiles.Delete(newProfile); } catch { failures.Add("删除密钥"); }
                        }
                        string pending;
                        if (newProfile != null && keys.TryGetValue(id, out pending)) {
                            try { credentialProfiles.Save(newProfile, pending); } catch { failures.Add("保存密钥"); }
                        }
                        if (oldProfile != null && newProfile != null)
                            replaced.Add(new KeyValuePair<ProviderProfile, ProviderProfile>(oldProfile, newProfile));
                    }
                }
                if (shell.NativeEnabled && candidate.AutoStart != committed.AutoStart) {
                    try { shell.OnDispatcher(() => Native.AutoStart(candidate.AutoStart)); } catch { failures.Add("开机启动"); }
                }
                if (shell.NativeEnabled && candidate.Shortcut != committed.Shortcut) {
                    try { shell.OnDispatcher(() => shell.RegisterShortcut(candidate.Shortcut)); } catch { failures.Add("快捷键"); }
                }
                if (failures.Count > 0)
                    throw new UserError("storage", "部分设置未能应用（" + string.Join("、", failures.ToArray()) + "），正在恢复原状态。");
            };
            Action<Settings, Settings> rollbackExternal = (committed, candidate) => {
                // Every rollback item is attempted even when an earlier one fails; the
                // aggregate failure reaches the transaction so it is never reported as restored.
                var problems = new List<string>();
                for (int i = keyUndo.Count - 1; i >= 0; i--) {
                    try { keyUndo[i](); } catch { problems.Add("密钥"); }
                }
                if (shell.NativeEnabled && candidate.AutoStart != committed.AutoStart) {
                    try { shell.OnDispatcher(() => Native.AutoStart(committed.AutoStart)); } catch { problems.Add("开机启动"); }
                }
                if (shell.NativeEnabled && candidate.Shortcut != committed.Shortcut) {
                    try { shell.OnDispatcher(() => shell.RegisterShortcut(committed.Shortcut)); } catch { problems.Add("快捷键"); }
                }
                if (problems.Count > 0)
                    throw new UserError("storage", "未能恢复先前的密钥或系统设置，请重新应用配置。");
            };
            await Store.CommitSettingsAsync(update, applyExternal, rollbackExternal, explicitApply,
                () => { if (credentialProfiles != null) CleanupReplacedCredentials(replaced, deleted); }).ConfigureAwait(true);
            if (Store.Settings.ProviderId != previous.ProviderId || Json.Write(Store.Settings.Provider) != Json.Write(previous.Provider) || keys.Count > 0 || deleted.Count > 0) shell.Workspace.CancelRequests();
            if (!Store.Settings.HistoryEnabled) shell.Forget(null);
            shell.RefreshTraySettings();
            // A committed typeface travels from the settings the transaction left behind, so the
            // popup, the settings page, the history window and every reading document move
            // together even when the change arrived without the picker's own live preview - a
            // preset load, a reapply after recovery, a store restored from disk.
            if (Store.Settings.FontFamilyName != previous.FontFamilyName) {
                shell.RefreshTypeface();
            }
            shell.RefreshHistory();
            Log.Event("settings_saved", null);
        }

        internal static bool SameCredentialScope(ProviderProfile oldProfile, ProviderProfile newProfile)
        {
            if (oldProfile == null || newProfile == null) return false;
            try { return Credentials.ScopedId(oldProfile) == Credentials.ScopedId(newProfile); } catch { return false; }
        }

        internal string ReadCredentialOrThrow(ProviderProfile profile, string label)
        {
            try { return credentialProfiles.Read(profile); }
            catch (Exception error) {
                throw new UserError("credentials", error is UserError ? ((UserError)error).Message : "无法读取" + label + "密钥，已取消本次配置。");
            }
        }

        internal void RestoreCredential(ProviderProfile profile, string originalKey)
        {
            if (profile == null) return;
            if (originalKey.Length > 0) credentialProfiles.Save(profile, originalKey);
            else credentialProfiles.Delete(profile);
        }

        internal void CleanupReplacedCredentials(List<KeyValuePair<ProviderProfile, ProviderProfile>> replaced, HashSet<string> deleted)
        {
            var failures = new List<string>();
            foreach (var pair in replaced) {
                string oldScoped, newScoped;
                try { oldScoped = Credentials.ScopedId(pair.Key); newScoped = Credentials.ScopedId(pair.Value); }
                catch { failures.Add("地址"); continue; }
                if (oldScoped == newScoped) continue;
                string newKey = "";
                try { newKey = credentialProfiles.Read(pair.Value); }
                catch { failures.Add("读取"); continue; }
                if (deleted.Contains(pair.Value.Id) || newKey.Length > 0) {
                    try { credentialProfiles.Delete(pair.Key); }
                    catch { failures.Add("删除"); }
                }
            }
            if (failures.Count > 0)
                throw new UserError("credentials", "未能清理不再使用的旧地址密钥，请重试。");
        }

        internal string ReadCredential(ProviderProfile profile)
        {
            if (profile == null) return "";
            if (shell.CredentialReader != null) return shell.CredentialReader(profile);
            return credentialProfiles != null ? credentialProfiles.Read(profile) : "";
        }

        internal void EnsureCredentialMigration()
        {
            if (!migrateLegacyCredentials || credentialMigrationDone) return;
            credentialMigrationDone = true;
            try { Credentials.MigrateLegacyProfiles(Store.Settings.Providers); }
            catch (Exception error) { Log.Event("credential_migration_failed", error); }
        }

        public async Task WaitForPersistenceAsync()
        {
            Task pending;
            do {
                pending = lastSettingsTask;
                if (pending != null) { try { await pending.ConfigureAwait(true); } catch { } }
                await Store.FlushAsync().ConfigureAwait(true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            } while (!ReferenceEquals(pending, lastSettingsTask));
        }
        internal string KeyFor(ProviderProfile provider)
        {
            if (shell.CredentialReader != null) return shell.CredentialReader(provider);
            if (!shell.NativeEnabled || credentialProfiles == null) return "test-key";
            return credentialProfiles.Read(provider);
        }
    }
}
