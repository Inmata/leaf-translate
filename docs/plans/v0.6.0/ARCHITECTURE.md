# Leaf 0.6.0.0：代码结构与实现说明

本文配套 [PLAN.md](PLAN.md)，是目标结构与工程建议，**不是已完成的代码导读**。正式中文导读在实现后保存到 docs/CODE-GUIDE.md。

## 1. 重构交给谁、放在哪一步

建议增加一个独立的 **R 重构包**，由用户指定一个 Worker 负责，Pi 独立审查。R 是 0.6.0.0 的第一阶段，先于 B/C 的正式功能实施；U 图片设计可以同时进行。

并发规则与 PLAN.md 一致：默认两个运行任务；任务间无未解决的依赖，且修改文件与状态不重叠时，才扩展到最多四个。实施与审查一起计数，Lead 单独计数。C 对 B 接口与 U 选定的依赖是启动前提，不是另一套人数限制。

原因很具体：现在界面、请求、缓存和保存有不少逻辑混在 AppShell 的多个 partial 文件中。仅 AppShell.cs 约 780 行、Popup.cs 约 1674 行，而且仍属于同一个大类。继续让 B/C 一边新增功能一边各拆一半，容易同时改状态与接口；做完全部功能再整理，又要重复接线和验证。

R 先把“现有程序怎么运行”拆清楚，保持外观与既有可运行行为。之后 B 实现新请求和学习规则，C 连接新交互并删除冗余功能。R 不负责一次性把整个 Windows 底层重写。

程序主体是 **C# + WPF，目标 .NET Framework 4.8**。PowerShell 主要负责构建和测试，不是界面与业务的主体语言。

## 2. 目标代码目录

采用一个应用项目，按职责分目录。保留 Views 与 Assets 的现有资源位置，不引入新 UI 框架或一整套依赖注入框架。

下面的文件名表达目标职责；R 对现有代码做迁移与必要拆分，不生成空文件凑目录。交接时记录最终实际文件名，再据此完善导读。

~~~text
src/Leaf/
├─ Program.cs                         程序入口
├─ Version.cs                         唯一产品版本定义
├─ Smoke.cs                           现有轻量加载检查入口
├─ app.manifest
│
├─ Models/                            数据，不引用 WPF 控件
│  ├─ AppSettings.cs                  服务、目标语言、字体等设置
│  ├─ TranslationSession.cs           原文、译文、当前方向、原文版本
│  ├─ TextRange.cs                    原文位置与连续范围
│  ├─ LearningObject.cs               四类对象、完整词定位、学习结果
│  ├─ LearningOptions.cs              词语 / 句子 / 段落偏好
│  └─ RequestSnapshot.cs              请求开始时固定的参数
│
├─ Application/                       组织操作与会话
│  ├─ AppShell.cs                     组装服务、应用启动和退出
│  ├─ WorkspaceController.cs          当前会话、编辑提交、方向切换
│  ├─ TranslationCoordinator.cs       取到文本后组织翻译与自动展开
│  ├─ LearningCoordinator.cs          选择对象、展开、切换与取消
│  └─ HotkeyController.cs             显示时收起，隐藏时呼出
│
├─ Translation/                       翻译业务
│  ├─ TranslationService.cs           纯翻译与首次语种 / 类型判断
│  ├─ TranslationStreamParser.cs      拆开内部判断与用户译文
│  └─ TranslationPromptBuilder.cs     翻译提示词与当前场景
│
├─ Learning/                          学习业务
│  ├─ LearningService.cs              分类、完整词定位与讲解
│  ├─ LearningCache.cs                完整结果与定位索引
│  ├─ LearningRequestRegistry.cs      同请求复用、在途状态
│  ├─ LearningStreamParser.cs         增量解析对象、标题与正文
│  └─ LearningPromptBuilder.cs        四类学习格式及分组偏好
│
├─ UI/                                只处理界面事件与呈现
│  ├─ Popup/
│  │  ├─ PopupView.cs                 连接浮窗控件、布局与显示
│  │  └─ PopupPresenter.cs            将会话状态呈现到浮窗
│  ├─ Source/
│  │  ├─ SourceInteraction.cs         点击、双击、拖选、键盘选区
│  │  ├─ SourceOffsetMap.cs           控件位置与原文位置映射
│  │  └─ SourceHighlightLayer.cs      保留现有局部高亮机制
│  ├─ Learning/
│  │  └─ LearningRenderer.cs          标题、分组标签、增量正文
│  ├─ Settings/
│  │  ├─ SettingsWindow.cs            设置窗口
│  │  ├─ SettingsPresenter.cs         应用设置、状态反馈
│  │  └─ LearningPreferencesPresenter.cs
│  └─ History/
│     ├─ HistoryWindow.cs             历史窗口
│     └─ HistoryPresenter.cs          搜索、恢复、删除
│
├─ Infrastructure/                    连接网络、磁盘与 Windows
│  ├─ Api/
│  │  ├─ LlmClient.cs                 现有 Chat Completions / SSE
│  │  ├─ ModelCatalog.cs              服务模型列表
│  │  ├─ LanguageClassifier.cs        判断结果与缺项补问
│  │  └─ JevClient.cs                 TypeSafe 官方 HTTP 接入
│  ├─ Storage/
│  │  ├─ SettingsStore.cs             设置迁移与保存
│  │  ├─ HistoryStore.cs              历史迁移、保存与删除
│  │  ├─ StoreQueue.cs                沿用后台保存队列
│  │  └─ HistorySnapshots.cs          保存时复制必要状态
│  ├─ Security/
│  │  ├─ CredentialStore.cs           Windows Credential Manager
│  │  └─ CredentialProfiles.cs        密钥与服务地址绑定
│  ├─ Windows/
│  │  ├─ SelectionCapture.cs          外部选区读取与复制后备链路
│  │  ├─ WindowsSelectionProbe.cs     UI Automation 文档与选区
│  │  ├─ WindowOwnership.cs           窗口 / 焦点归属
│  │  ├─ GlobalHotkey.cs              热键注册与 Alt+Space 处理
│  │  ├─ TrayHost.cs                  托盘驻留
│  │  └─ Native.cs                    必要的系统 API 声明
│  └─ Diagnostics/
│     ├─ Diagnostics.cs               中文结构化日志
│     └─ Performance.cs               现有性能观测
│
├─ Views/                              保留现有资源名
│  ├─ Popup.xaml
│  ├─ Settings.xaml
│  ├─ SettingsPanel.xaml
│  ├─ History.xaml
│  └─ Theme.xaml
└─ Assets/                             原图标、字体和许可证
   ├─ Leaf.ico
   └─ Fonts/

scripts/
├─ build.ps1
├─ test.ps1
└─ test-publish.ps1

tests/                                 沿用现有测试入口与必要回归
docs/
├─ PRODUCT.md                          实现后同步本版行为
└─ CODE-GUIDE.md                        实现后对应真实代码的中文导读
docs/plans/v0.6.0/                     正式计划与交接，纳入版本管理
├─ PLAN.md
├─ ARCHITECTURE.md
└─ CHECKLIST.md
~~~

普通业务文件以 **100–300 行左右**为整理目标，不为了达标把完整职责切碎。Native.cs 等系统声明文件可以较长。不能只是把大类分成更多 partial 文件、仍然共享所有状态，然后宣称耦合降低了。

目前 build.ps1 只编译 src/Leaf 顶层的 .cs；R 移入子目录时必须改为递归枚举，并同步受影响的测试引用。XAML 继续按 Leaf.Views.<文件名> 嵌入，字体、图标和 manifest 资源保持可用。

## 3. 各层怎样连接

~~~mermaid
flowchart TD
    Interaction["原文点击、拖选或展开按钮"] --> Workspace["会话与学习协调"]
    Workspace --> Translation["翻译服务"]
    Workspace --> Learning["学习服务"]
    Learning --> Cache["完整结果缓存与在途请求"]
    Translation --> Api["主模型 / 可选 Jev"]
    Learning --> Api
    Api --> Parser["流式增量解析"]
    Parser --> Updates["标题、正文增量、完成状态"]
    Updates --> Renderer["界面呈现"]
    Workspace --> Storage["后台保存"]
~~~

界面只告诉协调器“选中了哪一段、用户点了什么”，不自己取 Key、拼请求或写历史。服务接收普通数据，不读取 TextBox，也不负责改变按钮和窗口布局。

请求开始时固定原文、目标语言、接口 / 模型、场景和相关学习偏好。用户后来改设置，不让正在生成的内容半途换一套要求；下一次请求使用新设置。

先使用现有 Task、CancellationToken 和小型进度回调组织流式更新。不为这次重构引入与 .NET Framework 4.8 不匹配的运行机制。

## 4. 非连续重复学习怎样复用

### 4.1 缓存对象不是一个裸字符串

一个完整结果包含：学习对象类型、原文起止位置、完整词或片段、语境、讲解语言、章节正文，以及生成时采用的配置。

可复用的条件包括：

| 条件 | 作用 |
| --- | --- |
| 会话与原文版本 | 编辑后正式提交的新原文不混入旧结果。 |
| 位置、片段与语境 | 同样写着 bank，银行与河岸分开。 |
| 当前方向与目标语言 | 中文讲解和日文讲解不混用。 |
| 服务地址 / 服务、模型 | 切换生成来源后使用对应结果。 |
| 场景与相关学习组偏好 | 修改段落学习偏好不必使无关词语结果全部失效。 |
| 学习格式规则版本 | 将来格式调整后识别旧结果。 |

Key 不包含密钥，也不把整份配置或无关窗口位置塞进去。用户再次明确提交即使文字相同，也按该操作的会话语义处理，不把跨会话合并当成本版要求。

### 4.2 先查缓存，再发请求

例：在同一篇文章中先学习研究，再学发展，再学方法，最后又点研究。

1. SourceInteraction 传入点击位置或明确选区。
2. 已有定位索引把点击的“研”或“究”对应到同一个完整词范围。
3. LearningService 用这个范围和当前请求条件查完整结果。
4. 找到研究的完成结果，立即呈现；本次不调用 Jev，也不调用主模型。
5. 刷新研究的使用顺序，便于优先保留常用结果。

拖选已经学过的同一范围也先查缓存。缓存结果保存已有类型，不需要先花一次模型请求“判断能不能命中缓存”。

索引建立在原文位置上，不能看到哪里都写着“研究”就全部合并；不同位置的语境需要分别判断。

### 4.3 在途请求和切换

同范围、同条件已有请求时，把后续点击连到这一份请求，避免同一次双击产生重复学习。

如果 A 未完成时用户明确转去学习 B，取消 A、开启 B。取消的半份内容不进完整缓存，之后回 A 才重新请求。取消不会抹掉此前已经完整完成的其他结果。

建议每会话只在内存保留 32 份最近使用的完整结果。历史通过既有保存机制记录完成内容；32 份内存容量不等于“只能保存 32 个历史会话”。历史恢复只能使用配置和语境匹配的完整结果，不能把旧追问重新当成学习界面。

## 5. 点击、拖选与无空格语言

英语单击和双击对应完整词；中文或日语不能仅靠空格、正则或字符数决定词边界。

首次点击中文“研究”的“研”，学习请求携带原文和点击位置，由主模型同时返回完整词范围和讲解。范围确认后建立定位索引，后续点“究”也能复用这次结果。这是一次学习请求，不先判断再额外讲解两次。

拖选只处理控件自己的连续选区。比如用户拖选“在极其复杂的实验条件下”，本地保存范围并高亮；没有任何判断请求。用户点展开后，模型根据意义判定类型，不能因为字多就强制当句子。

控件显示位置与原文位置之间使用稳定映射，保留 Unicode，避免换行转换造成选错片段。映射随文本改变更新，不能随每次鼠标移动重算。继续利用现有原生文本控件和高亮层，避免回到逐词创建控件的方案。

## 6. 真正的流式学习怎样显示

当前词卡路径通常等完整 JSON 后才解析。虽然已有主模型 SSE，但这不等于学习界面已经流式。

B/C 采用明确的增量协议：先给出可解析的对象和类型，随后逐步给出章节标题、正文增量，最后结束。内部结构不直接出现在正文里。

例如短语学习：

~~~text
先显示：短语 · take into account
随后显示：“含义”
正文逐步增加：“把……考虑在内……”
接着显示：“当前句中的用法”
下一节继续增加……
最后标记完整，并放入缓存。
~~~

协议由 B 定义，向 C 交付调用说明与必要自测证据，C 按同一协议连接。这里不安排提前的 B 单包正式审查；协议与实际界面的完整行为在 A/B/C 联合交付时由 Pi 正式审查。解析器要保留自己的读取位置与未完成片段，支持 SSE 把一个标题、字符或转义拆开传送；不用每次从头解析全部正文。

现有 LlmClient 的部分进度回调是累积全文，接入时需要转换成新增长度对应的片段，或提供增量回调。LearningRenderer 在短时间内合并更新，只追加变化内容。高频输出不能触发整页重建、抢走原文选区或让用户滚动卡顿。

完整结束且内容有效后才写完整缓存。用户切换对象取消请求时，已经缓存的其他结果不受影响。

## 7. 设置与存储怎样迁移

- 旧词语学习偏好迁到“词语”组，保留勾选及自定义内容。
- 新增“句子”和“段落”组，不把三组偏好拼成所有请求的统一长提示词。
- 目标语言、字体、服务、Key 与窗口位置继续保留；Key 仍在 Credential Manager。
- 会话方向存为会话状态，不经自动保存覆盖全局目标语言。
- 追问字段允许为兼容旧历史而读取，但不再创建新问答或恢复问答 UI。
- 删除历史同时清理对应缓存；关闭历史保存沿用既有语义。
- 最终只清理一次用户本机旧历史，是验证操作，不写成所有用户升级都会执行的迁移。

## 8. 中文代码导读怎么写

用户希望从零读懂，而不是只看一张目录树。正式 docs/CODE-GUIDE.md 按下面顺序写，并链接实现后的真实方法：

1. Program：程序为什么启动后驻留托盘。
2. AppShell：怎样创建窗口、服务与存储。
3. WorkspaceController：什么是一份会话，原文、译文和方向存在哪里。
4. SourceInteraction：一次点击与一次拖选有什么区别。
5. LearningCoordinator / LearningService：点展开后为什么先查缓存，再决定请求。
6. LlmClient / JevClient：网络请求怎样出去。
7. LearningStreamParser / LearningRenderer：输出怎样一部分一部分显示。
8. HistoryStore / StoreQueue：完成结果怎样保存，为什么不阻塞界面。

用“研究 → 发展 → 再点研究”贯穿缓存说明；用“拖选一句话 → 点展开”贯穿请求与流式说明。解释事件、Task 和取消为什么存在，重要方法用中文注释说明原因。目录表不能替代这些实际流程。

## 9. 已定位的 Alt+Space 故障与模块归属

用户给出的同一 Chrome 进程对照表明：Alt+Space 失败，Ctrl+Alt+A 连续两次通过后备模拟复制取词；两者 UIA 都碰到相同的 32 个候选上限。主修复方向已明确是 Alt+Space 的处理，UIA 候选覆盖另行改进。

当前 GlobalShortcut.KeyboardHook 在 Native.cs 中只拦截空格事件，Alt 的按下 / 松开仍通过 CallNextHookEx。原软件可能因此看到一次单独 Alt 并改变内部焦点。当前复制前的窗口归属检查不能区分 Chrome 的 PDF 正文与菜单；不能把它当作“正文焦点未变”的证明。

重构后的职责分配：

- **A 所有：Infrastructure/Windows/GlobalHotkey.cs** 的原始按键处理、组合键序列和钩子释放；相关 Windows 取词及诊断。修复要保留正常单独 Alt 与其他组合，不留下修饰键状态。
- **C 所有：Application/HotkeyController.cs** 的应用操作规则：可见时收起、隐藏时呼出。C 调用原生热键模块，不自行改 A 的键盘钩子。
- R 迁移现有钩子时保持基线行为并交接该已知缺口；不能因迁移后构建通过，就将故障标记解决。

修复验收包含同一 Chrome PDF 正文的 Alt+Space / Ctrl+Alt+A 对照、正常单独 Alt、其他 Alt 组合、不同松键顺序、长按及退出。主要触发条件已经定位，具体菜单焦点切换路径尚待直接观察；本次文档更新不代表代码已经修复。
