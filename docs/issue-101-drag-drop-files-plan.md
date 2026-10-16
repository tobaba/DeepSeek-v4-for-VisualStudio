# Issue #101 可行性分析与实施方案：拖拽文件直接添加到上下文

> 目标 issue：[zmy15/DeepSeek-for-VisualStudio#101](https://github.com/zmy15/DeepSeek-for-VisualStudio/issues/101)
> 标题：添加支持拖拽文件直接添加（label: `enhancement`，state: `open`，assignee: zmy15）
> 验收标准：功能正常，测试全绿
> 本文档为**规划**文档，尚未实施。

---

## 1. 需求

| 项 | 内容 |
|---|---|
| 使用场景 | 当前只能点击按钮（`UploadButton`）通过文件对话框选择文件加入上下文，希望支持拖拽 |
| 期望方案 | 将文档拖拽到窗口自动附加 |
| 验收标准 | 功能正常，测试全绿 |

已核查的基线：`dotnet test -c Release` → **1789 passed / 0 failed**（22s），起点干净。

---

## 2. 可行性结论

**可行 —— 且已通过 M0 实测定案：采用 WPF 拖放通道（§4.4）。**

原判断中"能否用 JS `drop` 监听"的技术风险**已实测确认为不可用**（§2.3），
实现路径因此确定为在 WPF 层接管拖放，页面完全不参与。
工作量与风险均低于原估（无需引入 `AdditionalObjects` 通路，也不受 Chromium 事件路由变更影响）。

### 2.1 三条关键事实（已核实）

**事实 A — 不能用 `dataTransfer.files[i].path`。**
该属性是 Electron / NW.js 的私有扩展，WebView2 中为 `undefined`。官方在
[WebView2Feedback #3615](https://github.com/MicrosoftEdge/WebView2Feedback/issues/3615) 中明确答复：
Electron 的 `drop` 事件带 `path` 属性，而 "WebView2 does not have this"。

本仓库现状佐证：`AdditionalObjects` / `CoreWebView2File` / `AddHostObjectToScript` / `SetVirtualHostNameToFolderMapping`
**全仓零使用**（`grep` 仅命中 handler 签名 `View\DeepSeekChatControl.Events.cs:2246` 一处）。
现有 JS→宿主通道一律是 `postMessage(JSON.stringify(msg))` 字符串重载（22 处），
宿主一律 `e.TryGetWebMessageAsString()`。

> **M0 后状态**：本事实已无关紧要 —— JS 路线整体作废（§4.2），WPF 路线直接拿到真实绝对路径。

**事实 B — 官方支持的通路是 `postMessageWithAdditionalObjects`（本方案已不再需要）。**
依据 [WebMessageObjects 规范](https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/WebMessageObjects.md)：

- JS：`chrome.webview.postMessageWithAdditionalObjects(message, additionalObjects)`，`additionalObjects` 需为 ArrayLike（`FileList` 直接可用）
- 宿主：`CoreWebView2WebMessageReceivedEventArgs.AdditionalObjects` → 逐个 `is CoreWebView2File file` → `file.Path` 得到**真实绝对路径**
- 该 API 自 SDK `1.0.1777-prerelease` 转正；本仓库用 `Microsoft.Web.WebView2 1.0.3912.50`（`DeepSeek_v4_for_VisualStudio.csproj:47`），**远超门槛**
- 规范中明确写明用途："Use this API with a DOM File object to be able to get the path of files dropped on WebView2"

**事实 C — 真正的风险：宿主控件是 `WebView2CompositionControl`（视觉托管）。**
本仓库为修 issue #31 特意选用视觉托管控件（**故意无子 HWND**），
见 `View\DeepSeekChatControl.xaml.cs:665-668` 与 `View\DeepSeekChatControl.xaml:374-390` 的说明注释。

而上游 [WebView2Feedback #5676](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5676)
（2026-08-19 提交，**state=open、label=bug、0 comments、"Regression: No, this never worked"**）报告：
在使用 `WebView2CompositionControl` 的 WPF 应用中，**`dragover` 事件不触发**
（同一页面改用窗口式 `WebView2` 则一切正常，复现库 `DAST-Synchronics/WebView2CompositionBug`）。

这直接威胁 JS 方案：`dragover` + `preventDefault()` 是 HTML5 drop 生效的**必要条件**，
`dragover` 不来则 `drop` 不会来，纯 JS 方案会**静默完全失效**。

**为什么必须是视觉托管？（issue #31 的来龙去脉）**

- issue #31「Chat panel content hides all other panels」（2026-06-24 报，2026-10-02 关闭）：
  窗口式 `WebView2` 在 `HwndHost` 内创建 Chromium **子 HWND**，受 Win32 空域限制永远绘制在 WPF 合成内容之上，
  导致 pinned 的聊天面板盖住 VS 自动隐藏弹窗（如「Git 更改」）。
- 修复 commit `2e3fdd4e`（经 PR #87 合入，merge commit `f59fd1c`；仅 26 行增 / 10 行删）：
  字段与实例化由 `WebView2` 改为 `WebView2CompositionControl`；两者同实现 `IWebView2`，
  故 `CoreWebView2` / `EnsureCoreWebView2Async` / `ZoomFactor` 等既有用法无需改动。
- 后续 commit `a828e37a`：换合成控件**引入 0 尺寸崩溃**（`framePool.Recreate(0,h)` →
  `ArgumentException 0x80070057`，在 WPF 渲染回调 `MediaContext.RenderMessageHandler` 中抛出，
  托管代码无法捕获，上游 #5485）。修法为宿主与控件各加 `MinWidth/MinHeight=1`。
  ⚠️ **M2/M3 实现拖拽 overlay 时不得改动这两个下限值，也不要套用会把该行压到 0 的布局。**
- 结论：这是**真实的功能取舍**，不是配置疏漏 —— 换回窗口式可让网页拖放恢复正常，
  但会让 #31 复发。因此 §4.4 的 WPF 拖放路线（M0 后已成为主实现）具有长期价值。

补充排查（用于排除干扰项）：

- `DragStarting`（`ICoreWebView2CompositionController5`，SDK 1.0.3719.77 引入）**只覆盖"从 WebView 内部往外拖"**，
  对"从资源管理器往里拖"无效，**无法**用于救此场景。
- [WebView2Feedback #5141](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5141)（拖拽锁死控件）经查是
  **WinUI3 / WinForms** 场景且与 `DragStarting` 相关，与本仓库 WPF 场景不直接相关，列为已知上游风险而非阻塞项。
- `WebView2CompositionControl` 实现了 `IWebView2.AllowExternalDrop`（`ICoreWebView2Controller4`，
  默认 `TRUE`），**说明它作为常规 WPF `UIElement` 参与路由** —— 这也正是 WPF 拖放路线（§4.4）可行的依据。

### 2.2 风险与对策矩阵

| 风险 | 概率 | 影响 | 对策 |
|---|---|---|---|
| #5676 命中：`dragover`/`drop` 不触发 | ~~中高~~ **已确认发生** | JS 主方案静默失效 | **已由 M0 实测证实**（§2.3）；改走 WPF 路线 |
| VS 主窗口 `IDropTarget` 抢先截获 | ~~中~~ **已排除** | 无法收到拖放 | M0 实测：`ChatWebViewHost` 声明 `AllowDrop` 后正常接管 |
| `postMessageWithAdditionalObjects` 抛异常 | 低 | —— | **已无关**：JS 路线整体作废 |
| 未来 WebView2 运行时回归 | 低 | 功能退化 | WPF 路线不依赖 Chromium 事件路由，天然免疫 |
| 拖入目录（无先例） | 高 | 上下文被撑爆 | 明确跳过并提示，**不做递归**（§6 决策项） |

**~~关键判断：M0 冒烟验证必须前置~~ —— M0 已完成，结论见 §2.3：采用 WPF 路线。**

---

### 2.3 M0 实测结论（2026-10-17 完成）

在 VS 实验实例中装载 JS 探针（`[DropProbe]`）与 WPF 探针（`[DropProbe-WPF]`）后实测拖入文件，
日志证据如下。

**① JS 路线：页面收不到任何拖放事件 —— 上游 #5676 确认命中**

```
00:00:40 [DropProbe] 页面拖放事件: probeInstalled        ← 探针已装载（排除探针未生效）
（此后两次拖拽期间，JS 侧 dragenter / dragover / drop 一条都没有）
```

探针确认装载，但拖拽时页面内**零事件** → `WebView2CompositionControl` 下 HTML5 拖放不可用，
与上游 [#5676](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5676) 描述一致。

> 严谨性说明：本轮 WPF 侧已声明 `AllowDrop`，故"页面零事件"亦可能是 WPF 优先接管所致，
> 不能 100% 单独归因于 #5676。但**前一轮未加 WPF 处理器时页面同样零事件**，两次结果一致；
> 且无论何种解释，**实现结论相同：采用 WPF 路线**。

**② WPF 路线：完全可用，且能拿到真实绝对路径**

```
[DropProbe-WPF] DragEnter | FileDrop=True | formats=…,FileDrop,FileNameW,…
[DropProbe-WPF] DragOver  | effects=Copy
[DropProbe-WPF] Drop | count=1 | paths=F:\桌面\面试深挖问答-实习经历-晓多客服Agent.md
```

`DataFormats.FileDrop` 格式齐备，`Drop` 事件返回**真实绝对路径**。
同时**推翻了原"VS 主窗口 `IDropTarget` 截获"的猜测** —— 自行注册拖放（`AllowDrop`）后即可正常接管，
符合 [SVsMainWindowDropTarget](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.interop.svsmainwindowdroptarget)
文档所述 "Those that register for drag/drop itself will not inherit this file opening"。

**③ 事实 A 复验**：JS 路线即便能触发，`dataTransfer.files[].path` 亦不可用（WebView2 不暴露），
必须走 `AdditionalObjects`；但事件根本不触发，故该路线在视觉托管宿主下整体作废。

**④ 实现要点（来自实测）**：`DragOver` 在单次拖拽中触发 **40+ 次**（随鼠标移动连续触发）。
因此实现中**禁止**在 `DragOver` 内打日志或重建 overlay —— overlay 显隐必须由
`DragEnter` / `DragLeave`（配合计数或 `IsMouseOver` 判定）驱动。

**→ 结论：按 §4.4 实施 WPF-only 方案。§4.2 的 JS 通道作废，§4.1 双通道简化为单通道。**

---

## 3. 现状梳理

> **注意**：本节 file:line 为**实施前**的快照，用于说明当时的代码结构。
> 拖放功能落地后多处行号已漂移（尤其 `Events.cs`），引用时请以实际代码为准。
> 关键结论（管线形状、消费方式）未变，仍然有效。

### 3.1 附件管线（实施前快照）

- `_attachedFilePaths`：`List<string>`，`private readonly`，定义于 `View\DeepSeekChatControl.xaml.cs:209`
- 读取 9 处：`Messaging.cs:66, 187, 190, 197, 202, 205, 247, 262`；`Events.cs:305`
- `Add` 11 处：`Events.cs:258, 412, 431, 466, 579, 643, 691, 763`；`Clipboard.cs:121, 184, 227`
- `Remove`：`Events.cs:282`；`Clear`：`Events.cs:369`
- 内联去重守卫 8 处：`Events.cs:254, 403, 462, 577, 759`；`Clipboard.cs:119, 182, 225`

各处**高度重复**且**行为不一致**：

| 场景 | 位置 | 当时行为 | 现状 |
|---|---|---|---|
| 格式不支持（对话框） | `Events.cs:260-263` | `status.fileFormatUnsupported` | ✅ 已收敛到统一提示 |
| 重复（对话框） | `Events.cs:254` | **静默跳过**，无状态提示 | ✅ 已收敛（现会提示跳过数） |
| 已附加（活动文档） | `Events.cs:403-407` | `status.context.fileAlreadyAttached` | 未改（本就走独立提示） |
| 格式不支持（项目文件） | `Events.cs:471` | **仅写日志**，无用户提示 | ✅ 已收敛到统一提示 |

> ✅ 已按计划统一为 `AttachmentCollector` + `BuildAttachmentStatusText`；
> 上表前两处与第四处的提示口径现已一致（见 §4.3）。

### 3.2 附件 UI（纯 WPF，与 WebView 无关）

- `AttachedFilesControl`（`ItemsControl` + `WrapPanel`）：`View\DeepSeekChatControl.xaml:824-889`
- `RefreshAttachedFilesUI()`：`Events.cs:302-319`
- `AttachedFileItem`（嵌套 `internal sealed`）：`xaml.cs:2397-2413`
- **宿主从不把"待发送附件"状态推送到页面**（无 `updateAttachments`/`setContextFiles` 之类），
  附件仅在**发送后**作为消息 HTML 渲染（`ChatHtmlService.cs:789-845`）

### 3.3 发送路径（消费端，无需改动）

- `SendMessageCoreAsync`：`View\DeepSeekChatControl.Messaging.cs:55`
- 附件块：`:187-242`；`CreateFileReferences` **只传路径，不读内容**（`:197`，`FileParserService.cs:253-276`）
- 仅附件、无文本也可发送（`:134`）；发送后 `ClearAttachedFiles()`（`:326`）

**结论：只要往 `_attachedFilePaths` 里加路径并刷新 UI，即可自动被发送路径消费。**

### 3.4 WebView2 与消息通道

- 控件：`new WebView2CompositionControl()`，`xaml.cs:699`（程序化创建，规避 ReSharper 程序集冲突，issue #18）
- 消息接收注册：`Rendering.cs:1096` → 处理器 `Events.cs:2246`
- 解析模式：`JsonSerializer.Deserialize<JsonElement>`（`Events.cs:2261`）+ 防御式 `TryGetProperty`
- **21 个已知 `type`**，未知 type **静默忽略**；顶层单一 `try/catch`（`Events.cs:2537-2540`）

### 3.5 页面结构（全部由 C# 生成，仓库内无 .html/.js/.css 文件）

- 页面根：`Services\ChatHtmlService.cs:1385-1449`（`WrapFullPage`），根容器 `<div id='chat-container'>`（`:1410`）
- 单一 `<script>` 块：`ChatHtmlService.cs:1411-1448`，拼接 11 个 JS 片段构建器
- JS 片段：`Services\ChatHtmlService.Js.cs`（990 行）；`__sendToHost` 定义于 `:431-434`
- CSS：`Services\ThemeService.cs:377`（深色）/ `:496`（浅色），**两套必须同步修改**
- 现有最高 `z-index` 为 10；已有 `@keyframes fadeIn`（`ThemeService.cs:439` / `:558`）可复用
- ⚠️ 全量刷新走 `NavigateToString`（`Rendering.cs:1028`），会**销毁重建 DOM**，overlay 状态必须可由 JS 重新推导

### 3.6 测试与 i18n

- xUnit 2.9.3 + FluentAssertions + Moq，`net472`，`InternalsVisibleTo` 已配置（`csproj:200-205`）
- 测试项目通过 `ProjectReference` 引用主项目（`Tests.csproj:36`）
- 范式参照 `Tests\Unit\View\ClipboardPasteTextTests.cs`：**只测 static 纯逻辑，不实例化控件**
- i18n：扁平 JSON 字典，`Resources\Locales\zh-CN.json`（默认）+ `en.json`，新增 key **必须两处都加**
- CJK 守卫：`tools\check-cjk-strings.ps1`（基线已含 `Events.cs`、`ChatHtmlService.Js.cs`）；
  CI 中为 **report-only**（`build-and-test.yml:49` 未传 `-Enforce`），但不应引入新违规

### 3.7 两个已知坑（顺带修复）

1. **`RemoveAttachedFile_Click` 按文件名删除**（`Events.cs:273-286`，`Tag="{Binding FileName}"`，
   XAML `:889`）：不同目录下的同名文件会删错。应改为按 `FilePath` 删除。
2. **`%LocalAppData%\DeepSeekVS\temp\context` 会在关窗时被递归删除**
   （`CleanupTempContextFiles()`，`xaml.cs:1640-1658`，调用点 `:1605`）。
   **绝不可**把用户拖入的文件复制进该目录。

---

## 4. 方案设计

### 4.1 总体策略：WPF 单通道 + 共享纯函数

> M0 实测（§2.3）已确认 JS 路线不可用，故原"双通道"设计简化为单通道：
> 拖放在 WPF 层完成，页面完全不参与。

```
拖拽文件到聊天区
        ↓
ChatWebViewHost.AllowDrop + PreviewDragEnter/DragOver/Drop   （§4.4，实测已跑通）
        ↓
共享纯函数 AttachmentCollector（§4.3）
        ↓
_attachedFilePaths + RefreshAttachedFilesUI()
        ↓
既有发送路径消费（Messaging.cs:187-242，零改动）
```

**优势**：不依赖 Chromium 事件路由，因而不受上游 #5676 及未来 WebView2 运行时变更影响；
且拿到的是真实绝对路径（无需 `postMessageWithAdditionalObjects`）。

### 4.2 ~~JS 通道（主实现）~~ —— 已作废

**M0 实测确认 `WebView2CompositionControl` 下页面收不到 `dragenter`/`dragover`/`drop`
（§2.3），本方案整体取消。** 以下内容仅作记录，不再实施：

1. ~~`ChatHtmlService.Js.cs` 新增 `BuildFileDropZoneJs()`~~
2. ~~页内监听 `dragenter`/`dragover`/`drop` + 全屏 overlay~~
3. ~~`postMessageWithAdditionalObjects('__dropFiles__', e.dataTransfer.files)`
   → 宿主读 `e.AdditionalObjects` → `CoreWebView2File.Path`~~

作废理由汇总：事件不触发（主因）；且即便触发 `dataTransfer.files[].path` 亦不可用，
必须额外引入本仓库从未使用过的 `AdditionalObjects` 通路，复杂度更高而收益为零。

### 4.3 共享纯函数 `AttachmentCollector`（新增）

放 `Services\AttachmentCollector.cs`，纯静态、无 WPF/DI 依赖：

```csharp
internal static AttachmentResult Collect(IEnumerable<string> candidates, IReadOnlyCollection<string> existing);
// → Added / AlreadyAttached / Unsupported / NotFound / DirectoriesSkipped
```

职责：

- 过滤 `null` / 空白
- `Path.GetFullPath` 归一化后按 `OrdinalIgnoreCase` 去重
  （**注意**：这是相对现状的行为改进 —— 现有代码直接比较原始字符串，
   `C:\a\B.cs` 与 `c:\A\b.cs` 会被视为两个文件。属有意改进，需在测试中断言）
- 复用 `FileParserService.IsSupportedFormat`（`Services\FileParserService.cs:104-113`）
- **不检查** `File.Exists` 之外的额外条件（与现状一致：`IsSupportedFormat` 本身不校验存在性）
- 目录：单独归类为 `DirectoriesSkipped`（不递归）

同时**收敛既有 8 处内联逻辑**改用此 helper，统一提示行为。

### 4.4 WPF 拖放通道（**主实现**，M0 实测已跑通）

- 在 `View\DeepSeekChatControl.xaml` 的 `ChatWebViewHost` 上：
  `AllowDrop="True"` + `PreviewDragEnter` + `PreviewDragOver` + `PreviewDrop`
- 挂在 `ChatWebViewHost` 而非控件根：挂在根上会被 WebView2 先行吃掉
- 处理器：`e.Handled = true`，取 `(string[])e.Data.GetData(DataFormats.FileDrop)` → helper
- `PreviewDragOver` 中设置 `e.Effects = Copy`，否则 `Drop` 不触发
- **实测注意（§2.3 ④）**：`DragOver` 单次拖拽触发 40+ 次，
  **禁止**在其中打日志或重建 overlay；overlay 显隐应由 `DragEnter`/`DragLeave` 驱动
- **实测收益**：拿到的是真实绝对路径，无需 `postMessageWithAdditionalObjects` / `AdditionalObjects`
- overlay 可用 WPF `Border`/`Adorner` 覆盖在 `ChatWebViewHost` 之上
  （WPF 元素与 CompositionControl 同属 WPF 视觉树，z-order 可控）

### 4.5 i18n key（zh-CN.json 与 en.json 同步新增）

- `status.dropFilesUnsupported`：拖入文件格式不支持
- `status.dropDirectoriesSkipped`：目录已跳过
- 复用既有 `status.filesAddedToContext` / `status.noFilesAdded`

---

## 5. 实施阶段

> 状态更新（2026-10-17）：**M0 / M1 / M2 已完成并提交**，M3 部分完成，M4 已完成。
> 提交对应关系：M1 → `743f319`，M2（overlay）→ `fe89aef`，拖入提示层语言修复 → `c5faed3`。

| 阶段 | 内容 | 状态 |
|---|---|---|
| **M0 冒烟验证** | JS 探针 + WPF 探针实测，结论见 §2.3 | ✅ 已完成 |
| **M1 纯函数 + 测试** | `Services\AttachmentCollector.cs` + 13 个用例；收敛 2 处重复逻辑 | ✅ 已完成（`743f319`） |
| **M2 WPF 拖放主实现** | 探针转为正式实现、接入 helper、新增拖入 overlay | ✅ 已完成（`fe89aef`、`c5faed3`） |
| **M3 健壮性** | 目录策略 ✅ 已有（跳过并提示）；按路径删除 ❌ **未做** | ⚠️ 部分 |
| **M4 收尾** | i18n key 双语 ✅；全量测试 1820 全绿 ✅；README/issue 说明 ❌ 未做 | ⚠️ 部分 |

**M0 分支决策（已定）：**

- ~~若 `dragover`/`drop` 触发 → 按 §4.2 JS 主通道实施~~
- ✅ **确认 #5676 命中（页面不触发）** → 采用 **WPF-only** 方案（已落地）：
  `ChatWebViewHost` 拖放通道即主实现，overlay 用 WPF `Border` 覆盖在 `ChatWebViewHost` 之上
  （WPF 元素与 CompositionControl 同属 WPF 视觉树，z-order 可控）。

**遗留探针清理**：✅ 已完成 —— JS 侧 `BuildDragDropProbeJs()`、`<script>` 链注册、
`Events.cs` 的 `dropProbe` 分支、WPF 侧三个 `ChatWebViewHost_Preview*` 探针处理器
均已删除，由正式实现替代。

**M3/M4 未完成项（如需收口）：**

1. `RemoveAttachedFile_Click` 仍按**文件名**删除（同名不同目录的文件会删错），
   建议改为按 `FilePath` 删除 —— `AttachmentCollector` 已就位，改动很小。
2. README 与 issue #101 的功能说明尚未补充。
3. 拖放目前仅在聊天显示区（`ChatWebViewHost`）生效，**输入框区域不可拖入**；
   如需覆盖整个面板需把 `AllowDrop` 提升到根元素并重新验证路由。

---

## 6. 待确认决策项

1. **目录拖入如何处理？**（仓库无先例）
   - ✅ **已定：跳过并提示，不递归**（已实现，文案 `status.dropDirectoryUnsupported`）。
     理由：递归极易被拖入 `bin/`、`obj/`、`node_modules/` 等大目录撑爆上下文
   - 备选（未采用）：递归但限制文件数/深度（复杂度显著上升）
2. **`Path.GetFullPath` 归一化去重**是否接受？（相对现状的行为改进，见 §4.3）
3. ~~WPF 兜底是否长期保留~~ —— **已定**：WPF 拖放即主实现（M0 实测），无需再决策。

---

## 7. 测试计划

遵循 `ClipboardPasteTextTests.cs` 范式（只测 static 纯逻辑，不实例化控件）：

| # | 用例 | 断言 |
|---|---|---|
| 1 | 支持格式的新文件 | 归入 `Added` |
| 2 | 已在列表中的文件 | 归入 `AlreadyAttached`，不重复添加 |
| 3 | 大小写/相对路径差异的同一文件 | 归一化后判定为重复 |
| 4 | 不支持的扩展名 | 归入 `Unsupported` |
| 5 | 目录路径 | 归入 `Directories`，不递归 |
| 6 | 不存在的文件 | 归入 `NotFound` |
| 7 | `null` / 空白输入 | 安全忽略，不抛异常 |
| 8 | 混合批次 | 各类计数准确 |
| 9 | i18n：新增 key 在 zh-CN 与 en 均存在且非空 | 通过 `LocalizationService.Instance[...]` |
| 10 | `RemoveAttachedFile_Click` 改为按路径删除后的契约 | ⚠️ **未做**（M3 遗留项，见 §5） |

> 实际落地：`AttachmentCollectorTests` 13 个用例（覆盖上表 1–8 与纯函数契约）。

运行方式：

```
dotnet test DeepSeek_v4_for_VisualStudio.Tests\DeepSeek_v4_for_VisualStudio.Tests.csproj -c Release
powershell -File tools\check-cjk-strings.ps1     # 确认无新增违规
```

**验收对齐**：✅ 已完成 —— 全量 **1820 passed / 0 failed**（#101 落地时 1802，其后 i18n 收口新增用例增至 1820）。

---

## 8. 工作量小结

| 项 | 评估 |
|---|---|
| 总体可行性 | **可行（M0 已实测定案，功能已落地）** |
| 代码改动面 | **小**：1 个新文件（`AttachmentCollector.cs`）+ `Events.cs`（正式处理器）+ `xaml`（拖放接线 + overlay） |
| 拖放功能涉及的 HTML/CSS 改动 | **免除**：JS 路线作废，`ChatHtmlService.*` / `ThemeService.cs` 对**本功能**均无需改动（`ChatHtmlService.Js.cs` 后来因独立的 i18n 收口任务被改动，与拖放无关） |
| 单元测试 | 13 个用例（`AttachmentCollectorTests`），全部纯逻辑，无 UI 测试负担 |
| 主要不确定性 | ~~#5676 是否命中~~ —— **已解决（§2.3）** |
| 风险等级 | **低**：实现路径已验证跑通，且不依赖 Chromium 事件路由 |
| 预估总工时 | **5 - 7 h**（实际集中在 M0 验证 + M1/M2 实现） |

---

## 9. 参考

- [issue #101](https://github.com/zmy15/DeepSeek-for-VisualStudio/issues/101)
- [WebView2Feedback #5676 — WebView2CompositionControl not firing mouseleave and dragover events](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5676)
- [WebView2Feedback #3615 — 能否通过拖放获取文件路径](https://github.com/MicrosoftEdge/WebView2Feedback/issues/3615)
- [WebView2Feedback #501 — Support 'full' paths for dropped files in the HTML5 ondrop event](https://github.com/MicrosoftEdge/WebView2Feedback/issues/501)
- [WebMessageObjects 规范（postMessageWithAdditionalObjects / CoreWebView2File）](https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/WebMessageObjects.md)
- [CoreWebView2File.Path](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2file)
- [CoreWebView2WebMessageReceivedEventArgs.AdditionalObjects](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2webmessagereceivedeventargs.additionalobjects)
- [WebView2CompositionControl.AllowExternalDrop](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2compositioncontrol.allowexternaldrop)
- [ICoreWebView2Controller4.AllowExternalDrop](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2controller4)