# Leaf 发布与验证 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 同步发布删除、保存可审查的 CI 验证产物、统一版本来源并整理双语文档。

**Architecture:** 发布文件清单与镜像操作拆成可离线测试的 PowerShell helper，继续只操作隔离 checkout。版本常量驱动程序集、构建清单与打包，CI 保持只读权限并上传验证产物。

**Tech Stack:** Windows PowerShell 5.1、Git、现有 .NET Framework 编译器、GitHub Actions；无新增依赖或服务。

**Spec:** [设计约定](../specs/2026-10-06-leaf-improvements-design.md)，需求 E1–E3。可独立实施；CI 检查应自动覆盖届时已经合入的第一/第二批测试。

## Global Constraints

- 不记录密钥、请求/响应正文、学习语境或对话；无自动上传。
- 名称继续为 Leaf，设置/密钥/历史数据沿用原路径。
- 公开源不含二进制构建产物、本地数据、凭据或 work/。
- 任何删除只能针对事先验证的隔离发布/夹具目录；不强推，不改工作区 .git。
- 当前仅编写计划，不执行以下步骤，不创建 release、不推送。
- 本批次保留当前版本 0.4.1；真正发布新版本时只改新的版本常量，另行获得发布授权。

## File Structure

| 文件 | 职责 |
| --- | --- |
| 新增 scripts/public-source.ps1 | 固定允许范围、源清单与 private/binary 排除校验 |
| 新增 scripts/sync-public-source.ps1 | 隔离目录的新增/覆盖/删除计划及执行 |
| 修改 scripts/publish.ps1 | 复用清单与镜像 helper，保留身份/远端/非强推检查 |
| 新增 scripts/test-publish.ps1 | 离线 Git/文件夹夹具，验证删除与边界 |
| 新增 src/Leaf/Version.cs、scripts/version.ps1 | 单一版本常量及解析 helper |
| 修改 Program.cs、app.manifest、build/package/publish.ps1 | 从版本常量派生版本与 manifest |
| 修改 windows.yml、test.ps1、CONTRIBUTING.md | CI 产物与工程检查说明 |
| 修改 README.md/README.en.md、USAGE 双语 | 一致行为说明与重复链接清理 |

## Task 1: 发布完整镜像与离线删除检查（E1）

**Files:**
- Create: `scripts/public-source.ps1`、`scripts/sync-public-source.ps1`、`scripts/test-publish.ps1`
- Modify: `scripts/publish.ps1:6`、`scripts/publish.ps1:55`

**Interfaces:**
- `Get-PublicSourceManifest -ProjectRoot <absolute>` 返回对象：Roots、Files（RelativePath/FullName）。
- `Get-PublicSyncPlan -ProjectRoot <absolute> -PublishRoot <absolute>` 返回 Copy/Delete 数组。
- `Sync-PublicSource -ProjectRoot <absolute> -PublishRoot <absolute> [-DryRun]` 返回变更计划，DryRun 不修改文件。
- helper 只定义函数；dot-source 不执行 clone、读取 GitHub 身份或写文件。

- [ ] **Step 1: 写离线测试夹具，旧逻辑必须出现残留文件。**

新建 GUID 命名的 work/publish-tests 目录，在其中分别创建 source/destination；source 具有所有允许根的最小结构，destination 添加 src/Deleted.cs、docs/old-name.md 以及范围外的 keep-local.txt 和 .git/config。清单保留 app.cs 与改名后的 docs/new-name.md。

```powershell
$plan = Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $destinationRoot -DryRun
if (-not (Test-Path -LiteralPath (Join-Path $destinationRoot 'src\Deleted.cs'))) {
    throw 'Dry-run changed the fixture.'
}
Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $destinationRoot | Out-Null
if (Test-Path -LiteralPath (Join-Path $destinationRoot 'src\Deleted.cs')) {
    throw 'Deleted public source was left behind.'
}
if (Test-Path -LiteralPath (Join-Path $destinationRoot 'docs\old-name.md')) {
    throw 'Renamed public source was left behind.'
}
if (-not (Test-Path -LiteralPath (Join-Path $destinationRoot 'docs\new-name.md'))) {
    throw 'New public source was not copied.'
}
if (-not (Test-Path -LiteralPath (Join-Path $destinationRoot 'keep-local.txt'))) {
    throw 'Synchronization touched a path outside the allowlist.'
}
```

在 destination 建立本地 Git 夹具，将 Deleted.cs 先提交，然后镜像并 git add -A -- 允许根；git diff --cached --name-status 应包含删除。测试不能连接远端。源与目标一致时第二次计划为空。PublishRoot 等于 ProjectRoot、ProjectRoot 的父目录或包含 reparse point 时必须拒绝；测试故障时同样验证范围外文件仍存在。

- [ ] **Step 2: 单独运行并确认失败。**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-publish.ps1
```

- [ ] **Step 3: 实现允许范围与安全镜像。**

把现有 roots 移入 public-source.ps1，保持 .github、.gitignore、AGENTS.md、README 双语、CONTRIBUTING.md、docs、scripts、src、tests。清单是相对路径的 OrdinalIgnoreCase 集合。保留现有密钥模式检查，并明确拒绝公开目录中的 .exe/.dll/.pdb/.pfx/.key/.log、本地 settings/history 与 .env；.png/.ico 等已有源资产继续允许。

先清点 source 与 destination 的允许根，遇到 ReparsePoint 拒绝，不沿链接递归。删除只逐个删除已经验证的文件；空目录可在文件删除后非递归清理。不采用跨 shell 拼接删除，不删除 .git 或范围外目录。

边界校验必须在每个写/删目标之前执行：

```powershell
function Assert-PublishChild {
    param([string]$PublishRoot, [string]$Candidate)
    $rootPath = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\') + '\'
    $candidatePath = [IO.Path]::GetFullPath($Candidate)
    if (-not $candidatePath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publication target escaped the isolated directory.'
    }
    return $candidatePath
}
```

Get-PublicSyncPlan 的 Delete 是目标允许范围内路径减源清单。完整校验源、目标与所有动作之后才开始写入；DryRun 仅返回计划。publish.ps1 在 clone/config 后调用 Sync-PublicSource，之后 git add -A -- Roots，保留现有 remote SHA 验证。CheckOnly 只生成和校验清单，不 clone、不修改任何目录。

- [ ] **Step 4: 跑离线夹具与公开源检查。**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-publish.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1 -CheckOnly
```

- [ ] **Step 5: 提交发布镜像修复。**

```powershell
git add -- scripts/public-source.ps1 scripts/sync-public-source.ps1 scripts/test-publish.ps1 scripts/publish.ps1 CONTRIBUTING.md
git commit -m "fix: synchronize removed files in isolated publication checkouts"
```

## Task 2: 单一版本来源与一致性检查（E3）

**Files:**
- Create: `src/Leaf/Version.cs`、`scripts/version.ps1`
- Modify: `src/Leaf/Program.cs:11`、`src/Leaf/app.manifest`、`scripts/build.ps1`、`scripts/package.ps1`、`scripts/publish.ps1`
- Test: `scripts/test-publish.ps1` 的版本夹具、构建/打包检查

**Interfaces:**
- `Leaf.LeafVersion.SemVer`、`Leaf.LeafVersion.Assembly` 两个 const string。
- `Get-LeafVersion -ProjectRoot <absolute>` 返回 SemVer、Assembly。
- package/publish 的 -Version 可显式填写；未填读取常量，显式不一致必须拒绝。

- [ ] **Step 1: 为当前不一致的 manifest 和版本参数补充失败检查。**

```powershell
$versionInfo = Get-LeafVersion -ProjectRoot $projectRoot
$exe = Join-Path $projectRoot 'work\verification\Leaf.exe'
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if ($actualVersion -ne $versionInfo.Assembly) { throw 'Executable version is inconsistent.' }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'work\verification\Leaf.generated.manifest') -Raw
if ($manifest.assembly.assemblyIdentity.version -ne $versionInfo.Assembly) {
    throw 'Runtime manifest version is inconsistent.'
}
```

解析 helper 的夹具包含合法常量、重复常量、非法版本字符串；后两项拒绝。将 package -Version 9.9.9 与当前常量比较，应在实际打包前失败。不在测试中真的执行 publish。

- [ ] **Step 2: 跑失败版本检查。**
- [ ] **Step 3: 实现常量和派生构建清单。**

```csharp
namespace Leaf
{
    internal static class LeafVersion
    {
        public const string SemVer = "0.4.1";
        public const string Assembly = SemVer + ".0";
    }
}
```

Program.cs 的 AssemblyVersion/AssemblyFileVersion 引用 Leaf.LeafVersion.Assembly。version.ps1 只读取文件并用锚定正则找到唯一的 SemVer 常量；检查三段非负整数与 Windows 四段版本有效范围，不执行 C# 文本。

src/Leaf/app.manifest 是构建输入模板，移除 assemblyIdentity 的 version 属性，由 build.ps1 的 XML SetAttribute 注入派生版本，再写到 OutputDirectory/Leaf.generated.manifest。编译应用与测试都使用这个生成文件；源码构建不回写 tracked manifest。保持 asInvoker、DPI、OS 与 longPath 设置原值。

package/publish 移除默认写死的0.4.1；从 helper 读取版本并核对显式参数、exe FileVersion、ZIP 文件名。当前不自动 bump 版本，也不创建新 release。新版本将来只改 SemVer 常量，构建所有版本值自动一致。

- [ ] **Step 4: 构建、测试、打包，核对四个发布条目与 executable hash。**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory work/verification
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

- [ ] **Step 5: 提交版本来源整理。**

```powershell
git add -- src/Leaf/Version.cs src/Leaf/Program.cs src/Leaf/app.manifest scripts/version.ps1 scripts/build.ps1 scripts/package.ps1 scripts/publish.ps1 scripts/test-publish.ps1 CONTRIBUTING.md
git commit -m "build: derive assembly and package versions from one constant"
```

## Task 3: CI 报告、界面产物和文档一致性（E2、E3）

**Files:**
- Modify: `.github/workflows/windows.yml`、`scripts/test.ps1`、`README.md:242`、`README.en.md`、`docs/USAGE.md`、`docs/USAGE.en.md`、`CONTRIBUTING.md`
- Create: `scripts/check-docs.ps1`
- Test: 离线文档链接、工作流与产物路径检查

**Interfaces:**
- test.ps1 在 work/verification-results 保存进程退出码、断言输出和 smoke 结果；内容只来自隔离测试，不复制真实用户日志。
- check-docs.ps1 检查本地 Markdown 链接/图片存在、重复完全相同的资源链接、演示标记及必要行为文案；不发网络请求。

- [ ] **Step 1: 写文档重复链接与产物检查。**

check-docs.ps1 枚举 README 与 docs 下的用户/开发文档，使用 Select-String/正则检查相对路径（忽略 http(s)、纯锚点）；只对同一文档完全相同的 Markdown 列表项报重复。当前中文 README 的两条性能报告链接应该失败。

UI smoke 失败夹具检查脚本仍保留 result.json 和已有 PNG；进程输出写 UTF-8 文件，原始测试结束码不得被 Tee-Object/后续 Get-Content 覆盖。

- [ ] **Step 2: 跑失败文档检查并确认重复项。**
- [ ] **Step 3: 更新工作流并保留真实失败状态。**

保持 contents: read、windows-latest 和现有构建/打包。加入独立的公开源与离线发布夹具步骤。上传验证产物的步骤使用 always：

```yaml
- name: Check public source
  shell: powershell
  run: ./scripts/publish.ps1 -CheckOnly
- name: Verify publication fixtures and docs
  shell: powershell
  run: |
    ./scripts/test-publish.ps1
    ./scripts/check-docs.ps1
- name: Upload verification evidence
  if: ${{ always() }}
  uses: actions/upload-artifact@v4
  with:
    name: Leaf-verification
    path: |
      work/ui-smoke/*.png
      work/ui-smoke/result.json
      work/verification-results/*
    if-no-files-found: warn
```

test.ps1 保存日志时先捕获 LASTEXITCODE，再读取/展示内容，然后按真实结果 throw。UI 检查的 catch/finally 同样留存失败 result。公开源扫描在 clone/gh 前返回；CI 不调用真正发布路径。

将可在隔离进程稳定运行的原生检查以 workflow_dispatch 的布尔输入 `native_checks` 提供，默认 false；普通 PR 保持不依赖交互桌面。Codeg 取词、焦点与多屏检查始终独立，不把运行 --native 的有限断言称为全桌面兼容。native 验证记录 GUID 假凭据清理结果。

- [ ] **Step 4: 同步双语文档和演示截图。**

删除 README 重复性能链接；同步“空选区准备输入”“同文本恢复不重复请求”“清空草稿不提交”“原文/追问轻量按钮”“日志结果分类”“地址绑定 Key 迁移”“历史软字节上限与失败反馈”。保持用户首页聚焦使用，工程细节放 CONTRIBUTING。截图只用演示数据，明确未调用真实 API；不将旧测量数字改成新版本实测值。

- [ ] **Step 5: 跑全部检查并查看上传路径，最后提交。**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory work/verification
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-publish.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-docs.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1 -CheckOnly
git add -- .github/workflows/windows.yml scripts/test.ps1 scripts/check-docs.ps1 README.md README.en.md docs/USAGE.md docs/USAGE.en.md CONTRIBUTING.md
git commit -m "ci: retain verification artifacts and check publication inputs"
```

## 批次出口

- [ ] 改名/删除在隔离 Git 夹具内完整同步；范围外文件与 .git 不变。
- [ ] 当前版本的程序集、runtime manifest、ZIP、脚本参数一致；没有自动 bump 或发布。
- [ ] CI 失败依然留存可用截图/结果，原始失败状态不被后续输出覆盖。
- [ ] 双语本地链接、截图、隐私及行为说明一致。
- [ ] 外部发布仍需独立的明确授权，本计划不会调用 gh push/release。
