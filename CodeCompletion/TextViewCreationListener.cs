using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Settings;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// MEF 导出：为每个可编辑代码视图绑定内联预测管理器和命令过滤器。
    /// 这是代码补全功能的入口点。
    ///
    /// 内容类型：<c>code</c> 覆盖 C#/VB/F#/Python 等继承自 code 的语言；
    /// 其余 [ContentType] 覆盖 C/C++（不继承 code）与 JSON/XML/YAML/Markdown 等
    /// 配置、文档类型。清单集中在 <see cref="DeepSeekCompletionContentTypes"/>。
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("code")]
    [ContentType(DeepSeekCompletionContentTypes.Cpp)]
    [ContentType(DeepSeekCompletionContentTypes.Java)]
    [ContentType(DeepSeekCompletionContentTypes.Sql)]
    [ContentType(DeepSeekCompletionContentTypes.Cmake)]
    [ContentType(DeepSeekCompletionContentTypes.Json)]
    [ContentType(DeepSeekCompletionContentTypes.Xml)]
    [ContentType(DeepSeekCompletionContentTypes.Yaml)]
    [ContentType(DeepSeekCompletionContentTypes.Ini)]
    [ContentType(DeepSeekCompletionContentTypes.Html)]
    [ContentType(DeepSeekCompletionContentTypes.Css)]
    [ContentType(DeepSeekCompletionContentTypes.Razor)]
    [ContentType(DeepSeekCompletionContentTypes.Markdown)]
    [ContentType(DeepSeekCompletionContentTypes.PlainText)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal class TextViewCreationListener : IVsTextViewCreationListener
    {
        #region Properties

        /// <summary>
        /// 适配器服务，用于从旧版 <see cref="IVsTextView"/> 获取 WPF 文本视图。
        /// </summary>
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService { get; set; }

        /// <summary>
        /// 文本结构导航器选择器，用于获取方法/块级别的结构导航器。
        /// </summary>
        [Import]
        internal ITextStructureNavigatorSelectorService NavigatorSelectorService { get; set; }

        /// <summary>
        /// 包尚未初始化时被挂起的视图（等待 <see cref="DeepSeekOptionsPage.Instance"/> 就绪后补做绑定）。
        /// MEF 对该导出类型只创建一个实例，因此这些状态可安全地作为实例字段保存。
        /// </summary>
        private readonly List<IWpfTextView> _pendingViews = new();

        /// <summary>挂起处理器是否已注册（0=未注册，1=已注册），防止重复订阅。</summary>
        private int _deferredBindHooked;

        #endregion

        #region Public Methods

        /// <summary>
        /// VS 文本视图创建时调用：绑定 InlinePredictionManager 和 CommandFilter。
        /// </summary>
        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            // ── 无条件探针（必须保持在方法第一条语句）──
            // 该入口此前只在通过全部前置判断后才写日志，导致"MEF 未激活"与
            // "判断分支提前返回"两种情况在日志上不可区分，排查成本极高。
            // 这条日志不依赖任何服务、本地化资源或选项页，是判断组件是否被
            // MEF 调用的唯一可靠信号，请勿移除或下移。
            Logger.Info("[补全] ### VsTextViewCreated 入口被调用 ###");

            IWpfTextView view = AdapterService.GetWpfTextView(textViewAdapter);

            if (view == null)
            {
                Logger.Warn("[补全] GetWpfTextView 返回 null，跳过绑定");
                return;
            }

            // ── 差异视图判定 ──
            // 注意：绝不能因为视图带 UBIDIFF / UBIRIGHTDIFF 就判为差异视图。
            // 实测普通 C# 文档视图的角色集合为：
            //   [DEBUGGABLE, PRIMARYDOCUMENT, ANALYZABLE, DOCUMENT, EDITABLE,
            //    INTERACTIVE, STRUCTURED, ZOOMABLE, UBIDIFF, UBIRIGHTDIFF]
            // 其中 UBIDIFF / UBIRIGHTDIFF 是内联差异渲染的"能力"角色，普通可编辑
            // 文档视图同样携带它们（这正是 line diff / CodeLens 等能内联渲染的原因）。
            // 早期实现据此提前 return，导致所有正常视图都被拒绝、补全永久静默失效。
            //
            // 真正的 diff 视图（DifferenceViewer 创建的只读对比视图）带的是
            // DiffTextViewRole；且真正的编辑器视图一定带 EDITABLE。
            // 因此判定条件收紧为：带 diff 角色 **且** 不可编辑。
            bool hasDiffRole = view.Roles.Contains(DifferenceViewerRoles.DiffTextViewRole);
            bool isEditable = view.Roles.Contains(PredefinedTextViewRoles.Editable);

            if (hasDiffRole && !isEditable)
            {
                Logger.Info($"[补全] 只读差异视图，跳过绑定。实际角色=[{string.Join(", ", view.Roles)}]");
                return;
            }

            Logger.Info(LocalizationService.Instance["autocomplete.viewCreated"]);

            // 记录内容类型：便于确认目标文件走的是哪条内容类型分支
            // （C/C++、json、markdown 等非 code 类型依赖显式 [ContentType] 声明）。
            try
            {
                Logger.Info($"[补全] 内容类型=[{view.TextBuffer.ContentType.TypeName}]");
            }
            catch (Exception ex)
            {
                Logger.Info($"[补全] 读取内容类型失败（忽略）: {ex.Message}");
            }

            // 修复 VS 挂起 (perfwatsonhang ~700 hits)：
            // 旧实现通过 ThreadHelper.JoinableTaskFactory.Run(...) → LoadPackage 同步加载包。
            // 但 VsTextViewCreated 本身运行在 UI 线程，JTF.Run 阻塞等待的同时，
            // LoadPackage 又需要 UI 线程完成包初始化 → 双向等待 → 死锁/挂起。
            //
            // 方案 A+B：
            //   A) 绝不在此扩展点同步 LoadPackage。包在 InitializeAsync 中已将
            //      Options 缓存到静态 DeepSeekOptionsPage.Instance；若包尚未初始化
            //      则 Instance 为 null，本次直接跳过绑定（后续视图重建会重试），
            //      而不是强制同步加载引发死锁。
            //   B) 不再切换线程上下文，直接在 UI 线程读取缓存，消除等待环节。
            DeepSeekOptionsPage? options = DeepSeekOptionsPage.Instance;
            if (options == null)
            {
                // 包尚未完成 InitializeAsync（Instance 由 Package 缓存），此时不能同步
                // LoadPackage（会死锁，见上）。但仍必须完成绑定，否则此视图将永久失去
                // 补全能力——VS 对已打开的文档不会重建文本视图，原实现的"直接 return"
                // 正是导致补全长时间静默失效的根因之一。
                //
                // 改为：记录视图并订阅包就绪信号，待 Instance 可用后在原视图上补做绑定。
                Logger.Warn(LocalizationService.Instance["autocomplete.packageNotFound"]);
                ScheduleBindAfterPackageReady(view, textViewAdapter);
                return;
            }

            Bind(view, textViewAdapter, options);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 在指定视图上完成补全绑定：缓存视图适配器、创建预测管理器、挂接命令过滤器。
        /// 幂等——重复调用不会创建第二个 <see cref="InlinePredictionManager"/>。
        /// </summary>
        private void Bind(IWpfTextView view, IVsTextView textViewAdapter, DeepSeekOptionsPage options)
        {
            if (view.Properties.ContainsProperty(typeof(InlinePredictionManager)))
            {
                Logger.Info("[补全] 该视图已完成绑定，跳过重复绑定");
                return;
            }

            string status = options.AutoCompleteEnabled
                ? LocalizationService.Instance["autocomplete.statusEnabled"]
                : LocalizationService.Instance["autocomplete.statusDisabled"];
            Logger.Info(string.Format(LocalizationService.Instance["autocomplete.status"], status));

            // Store the text view adapter for later use (e.g., formatting)
            view.Properties.GetOrCreateSingletonProperty(
                typeof(IVsTextView), () => textViewAdapter);

            // Create and store the inline prediction manager
            ITextStructureNavigator structureNavigator =
                NavigatorSelectorService.GetTextStructureNavigator(view.TextBuffer);
            InlinePredictionManager manager = new(options, view, structureNavigator);
            view.Properties.GetOrCreateSingletonProperty(
                typeof(InlinePredictionManager), () => manager);

            // Attach command filter for Tab/Escape handling
            _ = new CommandFilter(view, textViewAdapter);
        }

        /// <summary>
        /// 包尚未初始化时，把本次绑定挂起到 <see cref="DeepSeekOptionsPage.Instance"/> 就绪之后执行。
        /// 避免在 UI 线程同步 LoadPackage 造成死锁（见 <see cref="VsTextViewCreated"/> 注释），
        /// 同时保证已打开的视图不会永久失去补全能力。
        /// </summary>
        private void ScheduleBindAfterPackageReady(IWpfTextView view, IVsTextView textViewAdapter)
        {
            if (view.Properties.ContainsProperty(typeof(InlinePredictionManager)))
            {
                return;
            }

            // 适配器预先存入属性包，补绑定时取回。
            view.Properties.GetOrCreateSingletonProperty(
                typeof(IVsTextView), () => textViewAdapter);
            _pendingViews.Add(view);

            // 仅在首次挂起时启动一次轮询。
            if (Interlocked.CompareExchange(ref _deferredBindHooked, 1, 0) != 0)
            {
                return;
            }

            Logger.Info($"[补全] 包尚未就绪，已挂起绑定并等待包初始化完成（当前挂起 {_pendingViews.Count} 个视图）");

            // 包在 InitializeAsync 中写入 Instance（步骤 3/8），但实测存在视图先于包创建
            // 的场景：某次会话中视图于 02:00:54 创建，包直到 02:01:33 才完成初始化
            // （间隔 39 秒，远超最初的 30 秒上限，导致该视图被永久放弃）。
            // 因此这里不设硬上限，改为持续轮询直到包就绪；同时订阅设置变更作为即时信号，
            // 两者互为补充（SettingsChanged 只在用户点"应用"时触发，不足以单独依赖）。
            DeepSeekOptionsPage.SettingsChanged += OnSettingsChangedForDeferredBind;
            ScheduleDeferredBindPoll();
        }

        /// <summary>设置应用时的即时补绑定信号（与轮询互为补充）。</summary>
        private void OnSettingsChangedForDeferredBind()
        {
            if (_pendingViews.Count == 0)
            {
                DeepSeekOptionsPage.SettingsChanged -= OnSettingsChangedForDeferredBind;
                return;
            }

            TryApplyDeferredBind();
        }

        /// <summary>以 500ms 间隔轮询等待 <see cref="DeepSeekOptionsPage.Instance"/> 就绪。</summary>
        private void ScheduleDeferredBindPoll()
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            timer.Tick += (_, _) =>
            {
                try
                {
                    if (_pendingViews.Count == 0)
                    {
                        timer.Stop();
                        DeepSeekOptionsPage.SettingsChanged -= OnSettingsChangedForDeferredBind;
                        Interlocked.Exchange(ref _deferredBindHooked, 0);
                        return;
                    }

                    if (TryApplyDeferredBind())
                    {
                        timer.Stop();
                    }
                }
                catch (Exception ex)
                {
                    timer.Stop();
                    DeepSeekOptionsPage.SettingsChanged -= OnSettingsChangedForDeferredBind;
                    Interlocked.Exchange(ref _deferredBindHooked, 0);
                    Logger.Error($"[补全] 延迟绑定轮询失败: {ex.Message}", ex);
                }
            };

            timer.Start();
        }

        /// <summary>
        /// 若包已就绪则补做全部挂起绑定。返回 true 表示已完成（无需继续等待）。
        /// </summary>
        private bool TryApplyDeferredBind()
        {
            DeepSeekOptionsPage? options = DeepSeekOptionsPage.Instance;
            if (options == null)
            {
                return false;
            }

            DeepSeekOptionsPage.SettingsChanged -= OnSettingsChangedForDeferredBind;
            Interlocked.Exchange(ref _deferredBindHooked, 0);
            ApplyDeferredBind(options);
            return true;
        }

        /// <summary>
        /// 对所有已挂起且未绑定的视图补做绑定。
        /// 仅从 UI 线程调用（DispatcherTimer 与 SettingsChanged 均在 UI 线程触发），
        /// 因此对 <see cref="_pendingViews"/> 的访问天然串行，无需额外加锁。
        /// </summary>
        private void ApplyDeferredBind(DeepSeekOptionsPage options)
        {
            Logger.Info($"[补全] 包已就绪，开始为 {_pendingViews.Count} 个挂起视图补做绑定");

            foreach (IWpfTextView pending in _pendingViews.ToArray())
            {
                try
                {
                    if (!pending.Properties.TryGetProperty(typeof(IVsTextView), out IVsTextView adapter))
                    {
                        continue;
                    }

                    Bind(pending, adapter, options);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[补全] 补做绑定失败（已跳过该视图）: {ex.Message}");
                }
            }

            _pendingViews.Clear();
        }

        #endregion
    }
}
