# Leaf 正确性与存储 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复中日文句段误判、Unicode 截断、配置半提交与损坏数据启动失败，并让长历史保存保持界面响应。

**Architecture:** 先建立地址绑定的凭据与可恢复文件提交，再将设置 patch 和历史写入串行化。应用向存储提交隔离快照；存储只在提交完成后公布配置，历史裁剪只序列化每条记录一次。

**Tech Stack:** .NET Framework 4.8、C# 5、WPF dispatcher、Windows Credential Manager、UTF-8 JSON；无新增第三方包。

**Spec:** [设计约定](../specs/2026-10-06-leaf-improvements-design.md)，需求 D1–D2、S1–S3。本批次可独立实施，若第一批已合入须保留其会话/取词行为。

## Global Constraints

- 目标为 Windows 10/11 桌面应用。
- 密钥仅存 Windows Credential Manager。修改接口地址需重新填密钥。
- 不记录密钥、请求/响应正文、学习语境或对话；无自动上传。
- 设置/历史数据沿用 %LOCALAPPDATA%\\LeafTranslate，名称继续为 Leaf。
- 原文输入保留 Enter 提交、Shift+Enter 换行；显式提交建立新会话。
- 不静默截短原文；6000 字符输入上限维持。
- 新源码仍放 src/Leaf 顶层；保持 C# 5 语法。
- 当前仅编写计划，不执行以下步骤。
- 每项未来验证使用 `scripts/test.ps1 -OutputDirectory work/verification`，保持现有运输、缓存和学习语境检查。

## File Structure

| 文件 | 职责 |
| --- | --- |
| Domain.cs | 保守单词判断、文本元素截断、精简会话上下文 |
| 新增 SettingsUpdates.cs | 偏好/服务/模型/位置 patch，基于最新状态合并 |
| 新增 StoreQueue.cs | 有序后台任务、冲刷、失败通知 |
| 新增 StoreTransactions.cs | 固定本地文件的暂存、恢复标记与原子替换 |
| 新增 CredentialProfiles.cs | provider+endpoint 凭据目标与旧目标迁移 |
| Storage.cs | 数据读取校验、候选状态、历史操作、对外兼容入口 |
| AppShell.cs、Windows.cs、Popup.cs、Smoke.cs | 等待配置/保存、关闭冲刷与 UI 状态同步、异步冒烟场景 |
| 新增 HistorySnapshots.cs | 单条隔离快照及一次编码、UTF-8 字节裁剪 |
| 新增 tests/StorageRegressionTests.cs | 故障注入、恢复、并发顺序、长历史 heartbeat |
| CoreTests.cs、ApplicationTests.cs、WindowsNativeTests.cs、Performance.cs | 新测试入口、原生假凭据及压力样本 |

## Task 1: 多语言路由与 Unicode 边界（D1、D2）

**Files:**
- Modify: `src/Leaf/Domain.cs:170`、`src/Leaf/Domain.cs:252`
- Test: `tests/CoreTests.cs:48`

**Interfaces:**
- Produces: `TextTools.TruncateElements(string value, int maxUtf16): string`。
- Preserves: `TextTools.IsWordInput(string)`、`WordCard.Parse(string,string,string)`。

- [ ] **Step 1: 在 Domain 检查中补充失败用例。**

```csharp
Check(!TextTools.IsWordInput("这是一句需要翻译的完整中文句子。"),
    "A complete Chinese sentence uses the translation path");
Check(!TextTools.IsWordInput("彼は本を読んでいます。"),
    "A complete Japanese sentence uses the translation path");
Check(!TextTools.IsWordInput("这句话没有标点"),
    "Continuous-script input is not inferred to be one word");
Check(!TextTools.IsWordInput("สวัสดี"),
    "Non-segmented script defaults to translation");
Check(TextTools.IsWordInput("l'homme") && TextTools.IsWordInput("re-enter") &&
    TextTools.IsWordInput("e\u0301te\u0301"),
    "Apostrophes, hyphens and combining marks retain word support");
Check(TextTools.TruncateElements(new string('a', 249) + "😀", 250) ==
    new string('a', 249), "Truncation does not split a surrogate pair");
Check(TextTools.TruncateElements("Ae\u0301", 2) == "A",
    "Truncation does not detach a combining mark");
```

WordCard.Parse 再检查 lemma/part_of_speech/target_phrase/title/content 都使用该函数，而不是只修 lemma。ValidateInput 超限检查仍抛 length，不截短。

- [ ] **Step 2: 运行失败测试。**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory work/verification
```

- [ ] **Step 3: 实现保守判断与框架文本元素截断。**

```csharp
private static readonly Regex ContinuousScript = new Regex(
    @"[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff\u0e00-\u0eff\u1000-\u109f\u1780-\u17ff]|\p{Cs}");
private static readonly Regex SentenceMarker = new Regex(@"[.!?。！？；;]");
public static bool IsWordInput(string source)
{
    source = (source ?? "").Trim();
    return source.Length > 0 && source.Length <= 80 &&
        !ContinuousScript.IsMatch(source) && !SentenceMarker.IsMatch(source) &&
        Pieces(source).Count(p => p.IsWord) == 1;
}
public static string TruncateElements(string value, int maxUtf16)
{
    value = value ?? "";
    if (maxUtf16 < 0) throw new ArgumentOutOfRangeException("maxUtf16");
    if (value.Length <= maxUtf16) return value;
    int end = 0;
    var elements = System.Globalization.StringInfo.GetTextElementEnumerator(value);
    while (elements.MoveNext()) {
        int candidate = elements.ElementIndex + elements.GetTextElement().Length;
        if (candidate > maxUtf16) break;
        end = candidate;
    }
    return value.Substring(0, end);
}
```

WordCard.Safe trim 后调用 TruncateElements。这里的文本元素定义是 .NET Framework 的实现，保证代理对及组合字符；不声称已实现所有现代 emoji 字素规则。原始 TextTools.Pieces 不改，显式选区解释仍支持连写语言。

- [ ] **Step 4: 完整检查通过，验证既有词形、片段位置与译文高亮检查。**
- [ ] **Step 5: 提交这个独立正确性修复。**

```powershell
git add -- src/Leaf/Domain.cs tests/CoreTests.cs docs/PRODUCT.md
git commit -m "fix: preserve Unicode boundaries and translate continuous-script passages"
```

## Task 2: 配置完整提交与地址绑定凭据（S1）

**Files:**
- Create: `src/Leaf/SettingsUpdates.cs`、`src/Leaf/StoreQueue.cs`、`src/Leaf/StoreTransactions.cs`、`src/Leaf/CredentialProfiles.cs`
- Modify: `src/Leaf/Storage.cs:32`、`src/Leaf/AppShell.cs:234`、`src/Leaf/Windows.cs:404`、`src/Leaf/Popup.cs:308`、`src/Leaf/Smoke.cs`、`tests/CoreTests.cs:33`
- Test: `tests/StorageRegressionTests.cs`、`tests/ApplicationTests.cs`、`tests/WindowsNativeTests.cs`

**Interfaces:**
- `SettingsUpdate.Preferences(Settings)`、`Provider(ProviderProfile)`、`Model(string id,string model)`、`Placement(WindowPlacement)`、`Full(Settings)`，均返回 SettingsUpdate；`Build(Settings latest): Settings`。
- `StoreQueue.Enqueue(Action): Task`、`FlushAsync(): Task`、`FailureWatermark: long`、`AcknowledgeFailures(long through): void`。
- `LocalStore.SaveSettingsAsync(SettingsUpdate): Task`。
- `LocalStore.CommitSettingsAsync(SettingsUpdate, Action<Settings,Settings> applyExternal, Action<Settings,Settings> rollbackExternal): Task`。
- `AppShell.ApplySettingsAsync(SettingsUpdate, Dictionary<string,string>, HashSet<string>): Task`。
- `AppShell.WaitForSettingsAsync(): Task`，等待最近登记的配置任务，不同步阻塞 dispatcher。
- `Credentials.Read(ProviderProfile): string`、`Save(ProviderProfile,string): void`、`Delete(ProviderProfile): void`、`MigrateLegacyProfiles(IEnumerable<ProviderProfile>): void`；现有 string 重载保留给迁移和旧调用兼容。
- `ICredentialProfiles.Read(ProviderProfile): string`、`Save(ProviderProfile,string): void`、`Delete(ProviderProfile): void`；`WindowsCredentialProfiles` 委托到 Credentials 的 provider 重载。
- `AppShell(LocalStore store, bool native, LlmClient client, ICredentialProfiles credentials)` 新构造重载，原二/三参构造委托到它；注入为空且 native=true 才创建 WindowsCredentialProfiles，native=false 不访问系统凭据。保留既有 CredentialReader 测试覆盖入口。
- `StorageRegressionTests.Run(string folder): Task<int>` 注册到 CoreTests。
- `StorageRegressionTests.RunUi(string folder): Task<int>` 注册到已有 ApplicationTests.Scenarios，复用现有 WPF Application、dispatcher 与同步上下文。纯存储入口不得创建 Application 或启动 DispatcherTimer。
- `LocalStore(string directory, IStoreFiles files)`；`AtomicFileBatch(string directory, IStoreFiles files)`、`Recover(): void`、`Commit(Dictionary<string,string> payloads, Action applyExternal, Action rollbackExternal, Action publish): void`。publish 只发布内存快照，不执行 I/O。

- [ ] **Step 1: 写锁历史与提交中途失败检查。**

固定初始地址为 old-api.invalid、新地址 new-api.invalid，模型 fixture-model；keys 用纯夹具值。用 FileShare.None 锁住已有 history.json，并把 HistoryLimit 从200改180，使提交确实需要改历史。操作失败后检查内存与重载的配置都还是旧地址/限额，旧凭据还在，新地址没有错误继承旧 Key。

```csharp
var next = Json.Copy(store.Settings);
next.Provider.BaseUrl = "https://new-api.invalid/v1";
next.HistoryLimit = 180;
using (var locked = File.Open(Path.Combine(folder, "history.json"),
    FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
    bool failed = false;
    try { await store.SaveSettingsAsync(SettingsUpdate.Full(next)); }
    catch (UserError error) { failed = error.Code == "storage"; }
    Check(failed, "Locked history rejects the grouped settings change");
}
Check(store.Settings.Provider.BaseUrl == "https://old-api.invalid/v1",
    "Failed settings commit leaves the in-memory endpoint unchanged");
Check(new LocalStore(folder).Settings.Provider.BaseUrl ==
    "https://old-api.invalid/v1", "Failed commit leaves the persisted endpoint unchanged");
```

为 IStoreFiles 编写真实文件委托适配器 FaultingFiles，在第 N 次 Replace 前抛 IOException；其余方法委托 PhysicalStoreFiles。分别使首次设置替换、历史替换、完成标记写入失败，断言回滚后两个文件及内存一致。用 FakeCredentialProfiles（字典键为 Credentials.ScopedId(profile)，不调用系统凭据）验证外部保存失败/回滚、地址变更与模型变更的 Key 作用域。

- [ ] **Step 2: 跑失败测试并确认已有半提交。**
- [ ] **Step 3: 实现 patch 与后台队列基础。**

SettingsUpdate 捕获输入的独立副本，Build 在队列执行时基于最新已提交状态；不能在调用方先复制 Store.Settings 再异步覆盖整个设置。Full 只供兼容入口/测试使用。Preferences 仅复制目标语言、场景/细节、学习/预设、剪贴板模式、直接输入、快捷键、启动与历史选项；Provider 仅替换该 ID 的配置并选择它；Model 不提交未应用的高级选项；Placement 只改窗口位置。

```csharp
public sealed class SettingsUpdate
{
    private readonly Func<Settings, Settings> apply;
    private SettingsUpdate(Func<Settings, Settings> update) { apply = update; }
    public Settings Build(Settings latest)
    {
        var result = apply(Json.Copy(latest));
        result.Normalize();
        return result;
    }
    public static SettingsUpdate Model(string id, string model)
    {
        return new SettingsUpdate(s => {
            var p = s.Providers.First(x => x.Id == id);
            p.Model = model; s.ProviderId = id;
            if (LlmClient.IsGlm53(p) && p.ThinkingMode == "disabled")
                p.ThinkingMode = "auto";
            LlmClient.ValidateProfile(p);
            return s;
        });
    }
    public static SettingsUpdate Full(Settings value)
    {
        var captured = Json.Copy(value);
        return new SettingsUpdate(s => Json.Copy(captured));
    }
    public static SettingsUpdate Preferences(Settings value)
    {
        var captured = Json.Copy(value);
        return new SettingsUpdate(s => {
            var p = Json.Copy(captured);
            s.TargetLanguage = p.TargetLanguage;
            s.Scene = p.Scene; s.SceneDetail = p.SceneDetail; s.SceneDetails = p.SceneDetails;
            s.GameTextType = p.GameTextType; s.Style = p.Style;
            s.Learning = p.Learning; s.Presets = p.Presets;
            s.ClipboardMode = p.ClipboardMode; s.FocusInputOnShortcut = p.FocusInputOnShortcut;
            s.Shortcut = p.Shortcut; s.AutoStart = p.AutoStart;
            s.HistoryEnabled = p.HistoryEnabled; s.HistoryLimit = p.HistoryLimit;
            return s;
        });
    }
    public static SettingsUpdate Provider(ProviderProfile value)
    {
        var captured = Json.Copy(value);
        return new SettingsUpdate(s => {
            var p = Json.Copy(captured); LlmClient.ValidateProfile(p);
            int index = s.Providers.FindIndex(x => x.Id == p.Id);
            if (index < 0) s.Providers.Add(p); else s.Providers[index] = p;
            s.ProviderId = p.Id; return s;
        });
    }
    public static SettingsUpdate Placement(WindowPlacement value)
    {
        var captured = Json.Copy(value);
        return new SettingsUpdate(s => { s.Placement = Json.Copy(captured); return s; });
    }
}
```

每个工厂单独检查不会覆盖别的范围。Preferences 后 Model、Model 后 Placement 同时排队，最终三个变更都存在。

StoreQueue 不捕获 WPF synchronization context，任务在 TaskScheduler.Default 串行运行。先前失败不能让后续任务自动跳过；FlushAsync 等待调用时的尾部并报告尚未处理的失败。保存失败的历史快照由 LocalStore 保留以供显式重试，不能静默丢弃。队列的完整错误跟踪骨架如下：

```csharp
public sealed class StoreQueue
{
    private readonly object sync = new object();
    private Task tail = Task.FromResult(0);
    private long sequence;
    private readonly List<KeyValuePair<long, Exception>> failures =
        new List<KeyValuePair<long, Exception>>();
    public long FailureWatermark { get { lock (sync) return sequence; } }
    public Task Enqueue(Action action)
    {
        lock (sync) {
            long id = ++sequence;
            tail = tail.ContinueWith(previous => {
                if (previous.IsFaulted) { var observed = previous.Exception; }
                try { action(); }
                catch (Exception error) {
                    lock (sync) failures.Add(new KeyValuePair<long, Exception>(id, error));
                    throw;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            tail.ContinueWith(faulted => { var observed = faulted.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return tail;
        }
    }
    public async Task FlushAsync()
    {
        Task pending; long through;
        lock (sync) { pending = tail; through = sequence; }
        try { await pending.ConfigureAwait(false); } catch { }
        lock (sync) {
            if (failures.Any(f => f.Key <= through))
                throw new UserError("storage", "本地保存尚未完成，请重试保存。");
        }
    }
    public void AcknowledgeFailures(long through)
    {
        lock (sync) failures.RemoveAll(f => f.Key <= through);
    }
}
```

仅 LocalStore 的显式恢复流程在已重放或明确解决对应失败操作后调用 AcknowledgeFailures。记下恢复开始时的 watermark，不能用完成时的新 watermark 清掉随后发生的失败。普通后续成功不会自动确认旧失败；异常对象只留在内存，日志写固定 storage 类别与异常类型/HResult，不拼接数据或密钥。

- [ ] **Step 4: 实现可恢复的固定文件提交。**

StoreTransactions.cs 定义完整文件接口与真实适配器；所有 path 均是 LocalStore 验证过的本地绝对路径，接口本身不接受会话对象或凭据。读取 Length 后的 32 MiB 限制由 LocalStore 在 Read 前执行。

```csharp
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
```

AtomicFileBatch 只接受 settings.json/history.json 两个固定目标。顺序必须是：

```text
校验并计算候选 settings/records
→ 写 *.new；复制旧目标到 *.old；记录目标是否原来存在
→ 原子写 store-transaction.json，state=prepared
→ applyExternal(oldSettings, candidateSettings)
→ 逐个替换目标文件
→ 原子写完成标记，state=committed
→ 公布内存 Settings/records
→ 清理 new/old/标记
```

失败且尚未 committed：按 old 备份恢复所有固定目标，删除原来不存在的新目标，再 rollbackExternal；内存不变。回滚失败保留标记并阻止 AppShell 发新 API 请求，显示需要重新应用配置的真实错误，不宣称已恢复。重启在读取 settings/history 前恢复 prepared；committed 只清理。恢复读取的目标名必须属于固定白名单，拒绝绝对路径/父目录路径。备份/标记不能含凭据，只包含本地配置、历史及固定提交元数据。

用候选列表进行 Trim/clear，禁止先修改 records 再 Write；SaveSettings 的同步兼容包装通过 Full+GetAwaiter().GetResult 调用异步接口，应用 UI 不用该同步包装。

- [ ] **Step 5: 实现地址绑定凭据及旧目标迁移。**

Credentials 改为 partial static，新文件沿用旧 CredWrite/CredRead 实现。作用域函数只使用 provider ID 与完整规范化 Endpoint，绝不使用 Key：

```csharp
public static string ScopedId(ProviderProfile profile)
{
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
```

MigrateLegacyProfiles：只处理当前已保存且接口有效的 profile；scoped 为空且旧 id 目标有 Key 时复制、回读相等后删除旧目标；任何失败不删除旧目标、不使用非绑定的旧 Key 发请求。Native AppShell 在打开设置/发请求前做一次迁移。所有运行时 KeyFor、设置 KeyHint/ActiveKey、Start 配置检查改用 provider 重载；UI 密钥框仍不回填。

ApplySettingsAsync 在 Store 的串行提交里应用外部变更。RegisterShortcut/AutoStart 的调用回到 popup dispatcher；凭据通过 ICredentialProfiles。把旧/新目标的原值仅保留在内存，回滚恢复对应目标，不能在文件标记中存 Key。成功更换接口后清理不再使用的旧地址目标；失败不清理旧目标。只有最终提交成功才取消需要换 API 的活跃请求。

- [ ] **Step 6: 更新设置 UI 的等待和关闭逻辑。**

Windows.cs 的 ApplyPreferences/TestConnection/Save/AutoApplyModel 分清事务角色：ApplyPreferences 改 Task<bool>，Save/AutoApplyModel 改 Task，事件与 timer await。设置服务按钮提交前禁用，finally 恢复；成功后更新 original 与清空密码框。请求/状态的 generation 防止旧成功状态覆盖后来的编辑。

Closing 先取消默认关闭，停止 timers，等待最新偏好/模型 patch 和队列完成；成功再次 Close，失败停留窗口并显示真实保存错误。退出时通过 AppShell 的统一冲刷路径处理，不能在关闭回调再启动未观察的保存。模型切换只提交 Model patch；未完成的地址/Key/高级参数仍显式应用。

ApplicationTests 中 RaiseEvent 后使用 `await shell.WaitForSettingsAsync()` 再检查 store，而不是假设 click 同步保存。定义该方法等待 AppShell 最近的配置任务，UI 事件在开始提交时登记它。原生假凭据测试使用 provider 重载，并在 finally 清理其 GUID 前缀的旧/新目标。

Smoke.Run 仍为同步命令入口，创建一次 Application；内部增加 `private static Task RunScenariosAsync(string folder, List<string> checks)`，移动现有 using shell 场景到其中。Run 设置 DispatcherSynchronizationContext、用现有 ApplicationTests.Run 同样的 DispatcherFrame 泵等待这个 task，结束恢复原上下文；不在 dispatcher 上 GetResult 等待仍需 dispatcher 的配置任务。设置按钮/偏好后 await WaitForSettingsAsync 再检查/渲染；本任务保留现有同步历史入口，Task 4 再接入异步历史的等待。该步骤只改既有烟测流程，不新建第二个 Application。

- [ ] **Step 7: 跑所有自动、原生隔离检查，检查文件与凭据的故障矩阵。**
- [ ] **Step 8: 提交配置一致性基础。**

```powershell
git add -- src/Leaf/SettingsUpdates.cs src/Leaf/StoreQueue.cs src/Leaf/StoreTransactions.cs src/Leaf/CredentialProfiles.cs src/Leaf/Storage.cs src/Leaf/AppShell.cs src/Leaf/Windows.cs src/Leaf/Popup.cs src/Leaf/Smoke.cs tests/StorageRegressionTests.cs tests/CoreTests.cs tests/ApplicationTests.cs tests/WindowsNativeTests.cs docs/PRODUCT.md
git commit -m "fix: commit settings consistently and bind credentials to endpoints"
```

## Task 3: 有效 JSON 的结构恢复（S2）

**Files:**
- Modify: `src/Leaf/Storage.cs:18`、`src/Leaf/Domain.cs:108`
- Test: `tests/StorageRegressionTests.cs`

**Interfaces:**
- Produces: `Settings.NormalizeLoaded(): void`，读取时调用；`TranslationRecord.TryNormalizeLoaded(): bool`。
- Preserves: Defaults、已保存模型、场景迁移、历史 Context 形状与公开路径。

- [ ] **Step 1: 写有效 JSON 中异常结构的检查。**

```csharp
File.WriteAllText(Path.Combine(folder, "settings.json"),
    "{\"Version\":3,\"Providers\":[null],\"HistoryEnabled\":true}");
var recovered = new LocalStore(folder);
Check(recovered.Settings.Provider != null,
    "Semantically invalid settings cannot crash startup");
Check(Directory.GetFiles(folder, "settings.json.corrupt-*").Length == 1,
    "Semantic corruption is preserved before defaults");
```

历史夹具含一条正常记录、一条 invalid ticks、一条 null context、正常记录中 null card/section 和 null chat；启动后保留正常翻译及可修复字段，丢弃不可恢复记录，Warning 不含源文。重复/空 provider ID、Providers:null、缺字段的旧设置、空模型/空自定义地址逐项覆盖：旧设置缺字段能默认迁移，合法的未配置服务不能误判损坏。

- [ ] **Step 2: 跑失败检查，确认空 provider 会越过 Read 的异常保护。**
- [ ] **Step 3: 把语义校验放进读取保护中。**

NormalizeLoaded 在 Normalize 之前拒绝 Providers 的 null 条目、空/重复 ID；允许 null 列表通过默认迁移，provider Name/Model/BaseUrl 的缺值转换为空或合法默认值，非空地址仍校验 Endpoint。ProviderId 不存在时选合法首项；null 学习项/异常预设结构按损坏设置处理，避免 UI 枚举崩溃。

ReadSettings 的整个解析、旧位置迁移与 NormalizeLoaded 都在 catch 范围内。備份使用 `yyyyMMddHHmmssfff-Guid`，防止同秒重复失败。只备份发生错误的文件；正常历史不因为坏设置被清空。

TryNormalizeLoaded 检查 Id/CacheKey/Source/Context/Completed，ticks 在 DateTime 合法范围内且 >0；修复 Cards/Chat/Draft 的空集合，过滤坏词卡与 null section，Chat 保留顺序完整的 user/assistant 对。单条无效返回 false；只对无法解析的整个 history.json 做整文件备份，解析成功但部分坏记录时保存隔离副本并保留正常条目。

```csharp
public bool TryNormalizeLoaded()
{
    if (!Completed || string.IsNullOrWhiteSpace(Id) ||
        string.IsNullOrWhiteSpace(Source) || Context == null ||
        UpdatedUtcTicks <= 0 || UpdatedUtcTicks > DateTime.MaxValue.Ticks)
        return false;
    try { Context.NormalizeLoaded(); }
    catch { return false; }
    Cards = Cards ?? new Dictionary<string, WordCard>();
    Chat = Chat ?? new List<ChatTurn>();
    Draft = Draft ?? "";
    return true;
}
```

在该方法中补充上述 Cards/Chat 的字段过滤，已成功读出的合法 CacheKey 保持；缺 CacheKey 的旧记录用其上下文重新计算。备份属于私人本地数据，关闭保存/清空历史时同步删除本程序创建的历史备份；设置备份不包含 Key。

- [ ] **Step 4: 完整检查及重启恢复夹具通过。**
- [ ] **Step 5: 提交加载恢复修复。**

```powershell
git add -- src/Leaf/Storage.cs src/Leaf/Domain.cs tests/StorageRegressionTests.cs docs/PRODUCT.md docs/USAGE.md docs/USAGE.en.md
git commit -m "fix: recover semantic settings and preserve valid history"
```

## Task 4: 历史快照、线性裁剪与有序保存（S3）

**Files:**
- Modify: `src/Leaf/Storage.cs:47`、`src/Leaf/Domain.cs:195`、`src/Leaf/AppShell.cs:273`、`src/Leaf/Popup.cs`、`src/Leaf/Windows.cs:574`、`src/Leaf/Performance.cs`、`src/Leaf/Smoke.cs`
- Create: `src/Leaf/HistorySnapshots.cs`
- Test: `tests/StorageRegressionTests.cs`、`tests/ApplicationTests.cs`

**Interfaces:**
- `ConversationContext.Snapshot(Settings): Settings`。
- `HistorySnapshots.Copy(TranslationRecord): TranslationRecord`，显式复制集合/可变对象，不串行化全表。
- `HistorySnapshots.Encode(List<TranslationRecord>, int limit): HistoryPayload`；HistoryPayload 含 Records、Json、Utf8Bytes、OversizedSingle。
- `LocalStore.HistoryEpoch: long`；`SaveAsync(TranslationRecord record, long expectedEpoch): Task`。`SaveAsync(TranslationRecord): Task` 只作兼容入口，入队时捕获当前 epoch。
- `LocalStore.DeleteAsync(string)`、`ClearAsync()`、`SavePlacementAsync(WindowPlacement)`、`FlushAsync()`、`RetryFailedWritesAsync()`，均 Task。
- `AppShell.SaveCurrentAsync(): Task`、`WaitForPersistenceAsync(): Task`、`ExitAsync(): Task`。
- `AppShell` 的 `private long currentHistoryEpoch` 在创建或恢复 Current 时捕获；所有自动保存明确传它，不在保存时重新取得 epoch。
- 既有同步入口保留给命令式测试，通过等待异步方法实现；应用 dispatcher 不调用同步包装。

- [ ] **Step 1: 建立 200/1000 条长历史与顺序屏障检查。**

每条：2000 字符 Source、2000 字符 Translation、24 条 600 字符 Chat，唯一 ID/CacheKey，已完成，使用夹具 Context。字节裁剪、次序和故障检查放 StorageRegressionTests.Run；以下 heartbeat 放 RunUi，并在已有 ApplicationTests.Scenarios 内 await，禁止创建第二个 Application。在同一个 WPF Application 中按 20 ms DispatcherTimer 采样；保存 Task 真正在后台进行，不能通过关掉计时器伪造响应。计时器在 finally Stop。

```csharp
var beats = new List<long>();
var watch = Stopwatch.StartNew();
var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
timer.Tick += (s, e) => beats.Add(watch.ElapsedMilliseconds);
timer.Start();
try { await store.SaveAsync(longRecord); await store.FlushAsync(); }
finally { timer.Stop(); }
Check(beats.Count > 0, "UI dispatcher continues during long history persistence");
```

上述 RunUi 用 FaultingFiles.DelayWritesMs=160 保证至少一个 timer 周期，不因机器写盘太快而误报；这只能证明有序写入未阻塞 dispatcher。在 Performance 里另以真实 PhysicalStoreFiles 记录 heartbeat 最大间隔、提交耗时、样本大小和机器；本机目标最大间隔 <100 ms。核心 CI 不断言跨机器的绝对速度，断言不在 UI 线程序列化、每条 Encode 只调用一次、输出字节数与限额一致。

顺序测试：Save(A)→Delete(A)→迟到 Save(A,oldEpoch)→Flush 后没有 A；Save(A)→Clear→Save(B,newEpoch) 后只有 B；Save(A)→关闭保存→再次开启→迟到 Save(B,oldEpoch) 后重启为空；再次开启后只记录新成功结果；编辑 Context 的原始预设/位置不会改变已排队快照；失败保留待重试快照，错误不被之后成功掩盖。仅检查当前 HistoryEnabled 不够，须覆盖关闭后再开启的迟到结果。

- [ ] **Step 2: 跑失败检查并记录修改前压力结果。**
- [ ] **Step 3: 精简 Context 和隔离快照。**

ConversationContext.Snapshot 创建 Settings 兼容形状，只复制 Version、当前 ProviderId/对应单个 ProviderProfile、TargetLanguage、Scene/SceneDetail、GameTextType、Style、Learning。Presets 与 SceneDetails 初始化为空集合，Placement 为空，其他运行偏好不复制。旧 Context 读入仍使用 Task 3 的校验，不重写旧 API/学习语境。

HistorySnapshots.Copy 深复制 Cards/sections、Chat/turns 及 Context 的可变集合；字符串直接复用。Current 的后续 Draft/Chat/Cards 更改不影响后台快照。LocalStore 不向外暴露自己已提交的可变对象：Find/History 返回这些显式副本；内部发布的 records 列表只整体替换，不原地修改。

- [ ] **Step 4: 实现按 UTF-8 字节计数的线性编码。**

```csharp
public static HistoryPayload Encode(List<TranslationRecord> source, int limit)
{
    var kept = new List<TranslationRecord>();
    var parts = new List<string>();
    long bytes = 2;
    foreach (var record in source.OrderByDescending(x => x.UpdatedUtcTicks).Take(limit)) {
        string json = Json.Write(record);
        long next = bytes + Encoding.UTF8.GetByteCount(json) + (parts.Count == 0 ? 0 : 1);
        if (parts.Count > 0 && next > 4L * 1024 * 1024) break;
        kept.Add(record); parts.Add(json); bytes = next;
    }
    return new HistoryPayload { Records = kept,
        Json = "[" + string.Join(",", parts.ToArray()) + "]",
        Utf8Bytes = bytes, OversizedSingle = bytes > 4L * 1024 * 1024 };
}
```

单条达到 32 MiB 读取硬上限时保存失败并给出 storage/length 分类，不能写出重启后一定无法读的数据。清楚记录软上限与 UTF-8 字节规则；不能把字符数叫文件字节数。对英文、中文、emoji 三种内容检查长度准确性。

- [ ] **Step 5: 应用有序后台写入、删除屏障和关闭冲刷。**

SaveAsync 入队前快速获取单条快照；队列中基于最新 records 构造候选、去重、设更新时间、Encode、AtomicFileBatch 提交；成功后发布候选。固定屏障规则如下，不能由各调用点自行选择：

- HistoryEpoch 由 LocalStore 的 sync 锁保护。ClearAsync 入队时提升 epoch；关闭保存的配置任务在构造候选并清空历史前提升 epoch，再开启不使旧 epoch 有效。
- DeleteAsync 入队时将 record ID 加入内存 tombstones。SaveAsync 在工作真正执行时同时检查 HistoryEnabled、expectedEpoch == HistoryEpoch、ID 不在 tombstones；不符合就跳过落盘，不更改 Current。
- Clear/关闭保存同时使旧 epoch 的失败快照失效；Delete 清除该 ID 的待重试快照。删除/清空 I/O 失败时保留屏障并报告错误，旧磁盘记录仍如实显示，直到显式重试成功，不能假称已删。
- AppShell 在 TranslationRecord.Create、OpenRecord 恢复 Current 时捕获 currentHistoryEpoch；查词、追问完成、收起和退出沿用该值。Clear/关闭后原会话不得因迟到完成重新落盘；新的明确翻译会话才捕获新 epoch。
- tombstones 只在相应 epoch 的旧保存全部失效后清理。仅保存少量 ID/计数，不持久化内容或内容哈希。

Delete/Clear/HistoryEnabled patch 都在同一队列，设置提交不改变限额/启用状态时不无谓重写历史。RetryFailedWritesAsync 按原始次序重放仍有效的失败操作，先核对 epoch/tombstone；成功后才确认 watermark 内已解决失败，FlushAsync 不能靠清空错误集合伪装成功。

AppShell 的翻译/词卡/追问完成 await SaveCurrentAsync；HidePopup 立即收起，把已捕获草稿的保存入队并观察错误，不等磁盘阻塞点击。位置去抖提交 Placement patch。HistoryWindow 的删除/清空异步等待，成功才 Forget/Refresh，不提前修改 UI 缓存。

Smoke.RunScenariosAsync 的历史/位置/退出检查 await WaitForPersistenceAsync 后才渲染或写成功 result.json；队列失败照常写失败结果。延续 Task 2 的单 Application 和 dispatcher 泵，不在 async 场景内新建应用。

ExitAsync 停止新的请求/取词，保存最后草稿并等待 FlushAsync；正常成功再释放钩子/托盘/客户端。失败时保留应用及未落盘快照，显示真实错误并用现有 RetryButton 调用冲刷；不会假称已保存或无限自动重试。Dispose 保留同步资源兜底，应用正常退出走 ExitAsync，不能在 dispatcher 上同步等待尚需 dispatcher 的配置事务。

- [ ] **Step 6: 跑检查与独立性能脚本，逐项记录测量范围。**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory work/verification
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/performance.ps1
```

Performance 添加长历史阶段和 heartbeat 结果；保持隔离数据、延迟夹具和专用快捷键。除非确实实跑，不更新性能报告数字。检查 SettingsWindow 开关、退出、保存关闭和旧记录继续查词/追问。

- [ ] **Step 7: 同步数据/隐私说明并提交。**

```powershell
git add -- src/Leaf/HistorySnapshots.cs src/Leaf/Storage.cs src/Leaf/Domain.cs src/Leaf/AppShell.cs src/Leaf/Popup.cs src/Leaf/Windows.cs src/Leaf/Performance.cs src/Leaf/Smoke.cs tests/StorageRegressionTests.cs tests/ApplicationTests.cs docs/PRODUCT.md docs/USAGE.md docs/USAGE.en.md
git commit -m "perf: persist ordered history snapshots without blocking the popup"
```

## 批次出口

- [ ] 设置/凭据/文件故障矩阵与未完成提交重启恢复通过。
- [ ] 原路径、旧密钥迁移、旧历史上下文、删除和关闭保存通过。
- [ ] 新版没有把 Key、Context、历史正文或内容哈希写入日志/提交标记。
- [ ] 200/1000 样本有真实时间与 heartbeat 数据；普通机器性能不夸大。
- [ ] 显式读者确认差异后再决定是否实施下一批次；本文件不授权发布。

## 存储检查辅助代码

`tests/StorageRegressionTests.cs` 内定义下面的类及断言，不调用 CoreTests/ApplicationTests 的 private helper。Run/RunUi 分别使用独立计数并返回增量；CoreTests 在进入 WPF 测试前运行 Run，ApplicationTests.Scenarios 只运行 RunUi。各故障用例使用自己的 GUID 子目录，重载前 await 当前 store.FlushAsync（预期失败用显式 catch 检查），禁止多个活跃 store 写同一目录。

```csharp
private static int assertions;
private static void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    assertions++;
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
private static TranslationRecord LongRecord(int index)
{
    var record = TranslationRecord.Create(index.ToString() + new string('a', 2000),
        "夹具", Settings.Defaults());
    record.Completed = true; record.Translation = new string('中', 2000);
    for (int i = 0; i < 24; i++) record.Chat.Add(new ChatTurn {
        Role = i % 2 == 0 ? "user" : "assistant",
        Content = new string('字', 600), Topic = "原句"
    });
    return record;
}
```

故障计数在初始数据建立后才重置并设置 FailAt，单次触发后回滚文件操作不再被相同故障阻断。检查提交的每个实际 Write/Replace/Move 位置，并模拟 prepared/committed 标记的重启读取。另写一个持续故障用例验证回滚失败会保留恢复标记、拒绝新 API 请求；不用该单次注入类假称已覆盖持续磁盘故障。
