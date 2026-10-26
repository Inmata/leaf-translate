<!-- workflow-alignment:begin -->
# 项目对齐

## 对齐依据

- 生成时间：2026-10-25（Asia/Taipei），workflow 0.5.0 重新对齐（首次对齐为 0.4.1）。
- 仓库状态：main，HEAD 0c33cb2（已合并 leaf/20261011-v052-integration）；对齐时已有用户未提交修改 .gitignore、AGENTS.md、CONTRIBUTING.md、docs/PRODUCT.md、docs/plans/v0.6.0 三份文档、scripts/test-publish.ps1，其中除 scripts/test-publish.ps1 外均为已裁决项 D4、F1、F2、F3、F5 的落实，scripts/test-publish.ps1 为用户自己的改动，本次未改动；本次对齐新增未提交的 .spec/ 与 backups/（被 .gitignore 排除）。
- 已读文件（大小字节 / 修改时间）：

| 路径 | 大小 | mtime |
| --- | --- | --- |
| AGENTS.md | 3027 | 2026-10-25 04:31 |
| CONTRIBUTING.md | 7644 | 2026-10-25 18:46 |
| .spec/settings-page/spec.md | 2638 | 2026-10-25 18:49 |
| .spec/word-card/spec.md | 2102 | 2026-10-25 18:49 |
| README.md（标题与“文档与反馈”段） | 16066 | 2026-10-22 21:11 |
| docs/PRODUCT.md | 12491 | 2026-10-25 04:44 |
| docs/plans/v0.6.0/PLAN.md（开头、第 8-10 节） | 27893 | 2026-10-25 04:43 |
| docs/plans/v0.6.0/ARCHITECTURE.md | 16998 | 2026-10-25 04:44 |
| docs/plans/v0.6.0/CHECKLIST.md | 8376 | 2026-10-25 04:44 |
| docs/RELEASE-v0.5.2.md（标题与要点） | 7877 | 2026-10-22 21:11 |
| docs/superpowers/README.md | 393 | 2026-10-25 04:44 |
| src/Leaf/Version.cs | 177 | 2026-10-22 21:11 |
| .github/workflows/windows.yml | 1923 | 2026-10-10 20:05 |
| scripts/test.ps1（参数段） | 12221 | 2026-10-10 20:05 |
| scripts/build.ps1（参数段） | 6414 | 2026-10-10 20:05 |
| scripts/test-publish.ps1（解释器调用） | 58643 | 2026-10-24 14:17 |
| scripts/public-source.ps1（公开范围） | 5403 | 2026-10-10 20:05 |
| .gitignore | 121 | 2026-10-25 04:44 |

- 未找到：CLAUDE.md、docs/ARCHITECTURE.md、docs/CODE-GUIDE.md（PLAN、ARCHITECTURE、CHECKLIST 仍引用，PLAN 第 10 节说明尚未生成）、work/agent-worktrees/integration（PLAN 所述基线 worktree 已不存在）。

## 文档索引

| 资料 | 职责 | 何时必读 |
| --- | --- | --- |
| AGENTS.md | 仓库约定：弹窗行为、隐私、验证比例、构建与测试入口 | 每次会话 |
| docs/PRODUCT.md | 已确认产品需求（含 0.5.2 行为），接续开发依据 | 改产品行为前 |
| CONTRIBUTING.md | 构建、测试、打包、版本与发布流程（中英） | 构建、验证、发布前 |
| docs/plans/v0.6.0/PLAN.md、ARCHITECTURE.md、CHECKLIST.md | 0.6.0 计划、工作包、分工与逐包审查安排（状态：计划，未实施） | 进入 0.6.0 开发时 |
| README.md、README.en.md | 用户首页：下载、设置、使用 | 改用户可见功能或文档时 |
| docs/USAGE.md、docs/USAGE.en.md | 用户使用说明，随包分发 | 改用户可见功能时 |
| docs/RELEASE-v*.md | 各版本发布说明与验证记录 | 发布或核对历史行为时 |
| .spec/settings-page/spec.md、.spec/word-card/spec.md | 设置页与词卡的验收条件和 code: 管辖文件；需求以 docs/PRODUCT.md 为准（追问不建，0.6.0 计划删除） | 改设置页、词卡及其管辖文件前 |
| docs/superpowers/README.md | 说明 docs/superpowers 为 2026-10-06 的归档计划与设计，并指向当前资料 | 误读旧计划前 |
| src/Leaf/Version.cs | 唯一版本常量（当前 0.5.2） | 涉及版本时 |

## 全量文档清单

| 路径 | 分类 | 是否已读 |
| --- | --- | --- |
| AGENTS.md | 约定 | 已读 |
| CONTRIBUTING.md | 使用/开发 | 已读 |
| README.md | 产品 | 部分已读 |
| README.en.md | 产品 | 存在但未读，按需查阅 |
| docs/PRODUCT.md | 产品 | 已读 |
| docs/USAGE.md | 使用 | 仅看标题 |
| docs/USAGE.en.md | 使用 | 存在但未读，按需查阅 |
| docs/PERFORMANCE-v0.4.0.md | 其他（实测） | 仅看标题 |
| docs/RELEASE-v0.1.0.md 至 v0.5.1.md（7 份） | 发布 | 存在但未读，按需查阅 |
| docs/RELEASE-v0.5.2.md | 发布 | 部分已读 |
| docs/plans/v0.6.0/PLAN.md | 计划 | 部分已读 |
| docs/plans/v0.6.0/ARCHITECTURE.md | 计划 | 已读 |
| docs/plans/v0.6.0/CHECKLIST.md | 计划 | 已读 |
| .spec/settings-page/spec.md | spec | 已读（本次新建） |
| .spec/word-card/spec.md | spec | 已读（本次新建） |
| docs/superpowers/README.md | 归档说明 | 已读 |
| docs/superpowers/plans/2026-10-06-*.md（4 份） | 归档 | 存在但未读，按需查阅 |
| docs/superpowers/specs/2026-10-06-leaf-improvements-design.md | 归档 | 存在但未读，按需查阅 |
| src/Leaf/Assets/Fonts/LICENSE.txt、SOURCE.txt | 其他（字体许可） | 存在但未读，按需查阅 |

## 验收入口

| 用途 | 命令 | 环境要求 |
| --- | --- | --- |
| 构建 | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1` | Windows 10/11，.NET Framework 4.8 自带 csc |
| 构建（程序占用时） | `scripts/build.ps1 -OutputDirectory bin/verification` | 不结束用户的 Leaf.exe |
| 测试与 UI smoke | `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1` | Windows PowerShell 5.1（AGENTS.md 明确不等同 PowerShell 7）；结果在 work/verification-results、work/ui-smoke |
| 原生桌面检查 | `scripts/native-checks.ps1` 或 `bin/Leaf.Tests.exe --native` | 交互桌面；CI 默认关闭 |
| 文档检查 | `scripts/check-docs.ps1` | 改文档后 |
| 发布检查 | `scripts/publish.ps1 -CheckOnly`、`scripts/test-publish.ps1` | 离线；test-publish.ps1 内部子进程解释器见 D6 |
| 打包 | `scripts/package.ps1` | 输出 dist/ |
| 性能实测 | `scripts/performance.ps1` | 会短暂打开窗口 |
| CI | .github/workflows/windows.yml（`shell: powershell`） | push main / PR |

## 目录与命名

- src/Leaf 应用代码，src/Leaf/Views 嵌入式 XAML；tests 测试；scripts 构建、验证、打包与发布脚本。
- bin、obj、dist、work、backups 为本地产物，由 .gitignore 排除；work/ 不进入公开源码。backups/ 现有 CONTRIBUTING.md.20261025-before-F6（F6 移动段落前的备份）。
- .spec/<id>/spec.md 为功能长期说明，code: 只列具体文件；新建 spec 尚未提交，提交后 spec check 的 drift 才会消除。
- 公开源码白名单（scripts/public-source.ps1）：.github、.gitignore、AGENTS.md、README*.md、CONTRIBUTING.md、docs、scripts、src、tests。docs/ 下所有文件（含本文件）会随发布公开（D5）。
- 发布说明命名 docs/RELEASE-v<SemVer>.md；计划按版本放 docs/plans/v<版本>/。

## 术语

- 浮窗/popup：主界面；设置页在同一浮窗内。
- 选中模式、剪贴板模式：两种取词方式，剪贴板只在快捷键唤起时读取。
- 词卡：单词学习结果；追问：同浮窗内对整句或当前词提问。
- Pi：PLAN 第 8 节指定的统一 Reviewer；R/U/A/B/C/V：0.6.0 工作包代号。

## 待裁决差异

| ID | 项目现行做法 | workflow 默认 | 影响 | 我的建议 | 状态 |
| --- | --- | --- | --- | --- | --- |
| D1 | PLAN 第 8 节：A/B/C 只在联合交付时由 Pi 做一次联合正式审查，单包只自测；R 单独前置审查 | delegated 任务的 task-end 门禁要求每个 worker 包都有 reviewedRef 等于 deliveredRef 的 PASS 审查包 | 按项目做法时，联合审查需用一个 reviewer 包对应多个 worker 包，或在 task-end 用 waivers 显式豁免 | 沿用项目联合审查，按包分别登记 reviewedRef 指向联合版本；与门禁不符处用 waivers 记录理由 | accepted-workflow |
| D2 | PLAN 第 8 节：默认同时 2 个任务，无依赖且不重叠时最多 4 个，审查任务计入 | 不设默认人数，按依赖、所有权和收益决定，不为并发强拆 | 两者上限兼容；差别在“默认 2 个” | 沿用项目规则（已是用户明确的阶段安排） | accepted-workflow |
| D3 | PLAN 第 10 节：进入开发后使用 efficient-parallel-development 技能，派发只用 Codeg delegate_to_agent，Worker 由用户逐包指定 | workflow Lead 指引负责调度与 packages 记账 | 两套协调流程同时生效会重复记账和播报 | 以 workflow 为调度与记账入口，保留“用户逐包指定 Worker、Codeg 派发、Pi 审查”这些项目决定；不再同时加载该技能 | accepted-workflow |
| D4 | 项目无 backups/ 目录，依赖 Git 历史；.gitignore 未排除 backups/ | 重要配置/核心文件修改前在项目 backups/ 保存非敏感备份 | 新建 backups/ 会出现在 git status，需决定是否加入 .gitignore；不在公开白名单内，不会发布 | 已提交内容用 Git 作为备份；只对未提交的重要文件做 backups/ 备份，并把 backups/ 加入 .gitignore | accepted-change |
| D5 | docs/ 整体在公开源码白名单内 | 对齐文件固定写在 docs/WORKFLOW-ALIGNMENT.md | 本文件会随下次发布公开，内容是内部协作信息 | 在 public-source.ps1 中排除该文件，或改放项目根并保持不公开 | accepted-project |
| D6 | scripts/test-publish.ps1（未提交改动）约 16 处子进程调用由 `powershell.exe` 改为 `pwsh.exe`；AGENTS.md “Leaf targets .NET Framework 4.8”一条写明 PowerShell 7 与 Windows PowerShell 5.1 的程序集加载不等价，CONTRIBUTING.md 命令、scripts/test.ps1 的命令行字符串、CI 的 `shell: powershell` 均使用 Windows PowerShell 5.1 | workflow 对脚本解释器无默认；验证约定要求验证环境与项目声明一致 | 发布检查在 pwsh 下通过，不能代表 5.1 通过；本机同时有 pwsh 7.6.8 与 5.1，其他机器或 CI 未必有 pwsh | 先与改动意图对齐：若为有意迁移，则同步 AGENTS.md、CONTRIBUTING.md、test.ps1、CI；若为试验，则改回 powershell.exe。无论哪种，该文件由用户处理，我不改 | accepted-project |

## 结构建议

| ID | 问题 | 位置 | 建议改法 | 状态 |
| --- | --- | --- | --- | --- |
| F1 | 写“当前版本线停留在 0.4.1”，与 Version.cs 的 0.5.2 及已有 RELEASE-v0.5.x 不一致 | CONTRIBUTING.md “Versions and publication”段 | 改为以 Version.cs 为准，删除固定版本号 | accepted-change |
| F2 | 仓库目录表缺 docs/plans、docs/RELEASE-*.md、docs/superpowers | CONTRIBUTING.md “Repository layout” | 补充这几行及其职责 | accepted-change |
| F3 | 第 10 节状态与接续提示仍指向 work/agent-worktrees/integration 和未提交的 0.5.2 基线；该 worktree 已不存在，0.5.2 已合并进 main（0c33cb2） | docs/plans/v0.6.0/PLAN.md 第 10 节 | 更新基线为 main 当前提交，修改接续提示 | accepted-change |
| F4 | 2026-10-06 的旧计划与设计已被后续版本取代，但被跟踪且公开，无归档说明 | docs/superpowers/ | 加一行归档说明或移到 docs/archive/，并评估是否排除出公开源码 | accepted-change |
| F5 | 未包含 0.5.2 的目标语言下拉、字体选择、原文译文交换等已发布行为 | docs/PRODUCT.md | 按 RELEASE-v0.5.2.md 补入已确认行为 | accepted-change |
| F6 | “Repository layout”标题下、目录表之前夹着一段隔离资源基准与 performance.ps1 的说明，与目录表无关，位置错位 | CONTRIBUTING.md “Repository layout” | 把该段移到测试或性能相关小节，或在目录表后单列小节，内容不变 | accepted-change |
| F7 | 无 .spec；0.6.0 将跨多个会话开发并有用户可感知行为与验收条件，满足建 spec 条件，但目前尚未实施、没有漂移历史，PLAN、ARCHITECTURE、CHECKLIST 已承担计划职责 | .spec/ | 已建 .spec/settings-page 与 .spec/word-card（追问因 0.6.0 计划删除而不建），code: 只列具体文件，related: 引用 docs/PRODUCT.md | accepted-change |

## 已裁决记录

D1 accepted-workflow：用户选择逐包审查：每个 Worker 包各自由 Reviewer 审查并 PASS，替代 PLAN 第 8 节的单次联合审查；PLAN 第 8 节需随后同步。复核条件：用户要求恢复联合审查。
D3 accepted-workflow：用户选择以 workflow Lead 指引为调度与记账入口，不再同时加载 efficient-parallel-development；保留用户逐包指定 Worker、Codeg 派发、Pi 审查。复核条件：用户改换调度方式。
D5 accepted-project：用户允许对齐文件随 docs/ 公开发布，不改 public-source.ps1；内容须保持无个人路径与凭据。
D2 accepted-workflow：用户选择不设默认并发人数，由 Lead 按依赖、所有权和协调成本决定；已同步 PLAN 第 8 节、ARCHITECTURE 第 1 节、CHECKLIST V03。
D4 accepted-change：用户选择 Git 为主：已提交内容以 Git 作为备份，只对未提交的重要文件备份到 backups/；已把 backups/ 加入 .gitignore。
F1 accepted-change：CONTRIBUTING 版本说明改为以 Version.cs 为准，删除固定 0.4.1。
F2 accepted-change：CONTRIBUTING 目录表补充 RELEASE、plans、superpowers 与本对齐文件。
F3 accepted-change：PLAN 第 10 节基线改为 main 0c33cb2，接续提示同步 D1/D2/D3；第 8、9 节改为逐包审查。
F4 accepted-change：新增 docs/superpowers/README.md 说明归档性质和当前资料入口；未移动文件，仍随 docs 公开。
F5 accepted-change：PRODUCT 按 RELEASE-v0.5.2 补入目标语言下拉、字体选择、原文译文交换与版本号显示。
D6 accepted-project：用户确认有意把 scripts/test-publish.ps1 的子进程解释器迁移到 pwsh。须由用户后续同步 AGENTS.md 第 17 行、CONTRIBUTING.md 命令、scripts/test.ps1 命令行字符串与 CI 的 shell: powershell；同步前，发布检查结果只能标注为 pwsh 下通过。复核条件：用户改回 Windows PowerShell 5.1 或同步完成。
F6 accepted-change：用户批准：CONTRIBUTING.md 的隔离资源基准段落从 Repository layout 标题下移到 Verification and packaging 小节末尾（原生桌面检查段之后），内容不变；改动前备份在 backups/CONTRIBUTING.md.20261025-before-F6。
F7 accepted-change：用户选择现在就建 spec，并排除追问（0.6.0 计划已删除追问）。已建 .spec/settings-page/spec.md 与 .spec/word-card/spec.md，code: 只列具体文件，related: 引用 docs/PRODUCT.md；原文档保留原位。新 spec 未提交前 spec check 对其管辖文件报 drift，提交后按 workflow 规则消除；提交需用户另行授权。
<!-- workflow-alignment:end -->
