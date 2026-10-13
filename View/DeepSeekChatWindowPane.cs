using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 工具窗口窗格，对标共享项目 TerminalWindowTurbo。
    /// 宿主 DeepSeekChatControl (WPF UserControl with WebView2)。
    /// </summary>
    [Guid("8F3A9C2D-1E5B-4F6A-9C8D-2E3F5A7B1D4E")]
    public class DeepSeekChatWindowPane : ToolWindowPane
    {
        /// <summary>
        /// 初始化工具窗口。
        /// </summary>
        public DeepSeekChatWindowPane() : base(null)
        {
            DiagnosticLog.Write("[DeepSeek Pane] Constructor: starting...");
            try
            {
                this.Caption = LocalizationService.Instance["chat.windowTitle"];
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor: caption='{this.Caption}'");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor: caption failed ({ex.GetType().Name}: {ex.Message}), using fallback");
                this.Caption = "DeepSeek Chat";
            }

            try
            {
                this.Content = new DeepSeekChatControl();
                DiagnosticLog.Write("[DeepSeek Pane] Constructor: DeepSeekChatControl created OK");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor: DeepSeekChatControl FAILED: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    DiagnosticLog.Write($"[DeepSeek Pane] Constructor inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                throw;
            }

            // 订阅语言变更以动态更新标题。
            // 用具名方法而非匿名 lambda：Dispose 时要能退订，
            // 否则进程级单例 LocalizationService 会长期强引用已关闭的工具窗口。
            try
            {
                LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] Constructor: LanguageChanged subscription failed: {ex.Message}");
            }

            DiagnosticLog.Write("[DeepSeek Pane] Constructor: completed");
        }

        /// <summary>
        /// 语言切换时更新工具窗口标题。
        /// 事件在触发线程上同步调用，这里只做字符串赋值，不做跨线程封送。
        /// </summary>
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            try
            {
                this.Caption = LocalizationService.Instance["chat.windowTitle"];
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] LanguageChanged handler failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 窗口创建完成，将 Package 引用传入 UserControl。
        /// </summary>
        protected override void OnCreate()
        {
            DiagnosticLog.Write("[DeepSeek Pane] OnCreate: starting...");
            try
            {
                base.OnCreate();
                if (Content is DeepSeekChatControl control)
                {
                    control.StartControl((DeepSeek_v4_for_VisualStudioPackage)Package);
                    DiagnosticLog.Write("[DeepSeek Pane] OnCreate: StartControl completed OK");
                }
                else
                {
                    DiagnosticLog.Write($"[DeepSeek Pane] OnCreate: Content type is {Content?.GetType().FullName ?? "null"} (expected DeepSeekChatControl)");
                }

                // 工具窗口窗格创建完成：与 webview.unloaded / control.unloaded 日志对照，
                // 可判断控件被卸出视觉树是否由工具窗口生命周期引起。
                Logger.Info("[WebViewLifecycle] pane.onCreated");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"[DeepSeek Pane] OnCreate FAILED: {ex.GetType().Name}: {ex.Message}");
                DiagnosticLog.Write($"[DeepSeek Pane] OnCreate stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    DiagnosticLog.Write($"[DeepSeek Pane] OnCreate inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                throw;
            }
        }

        /// <summary>
        /// 窗口销毁时释放资源。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            // 工具窗口关闭或 VS 重建布局都会走到这里，是「控件被卸出视觉树」的强候选原因
            Logger.Info($"[WebViewLifecycle] pane.dispose | disposing={disposing} | content={Content?.GetType().Name ?? "null"}");
            if (disposing)
            {
                // 先退订全局事件，再释放内容，避免释放过程中被语言切换回调重入
                LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

                if (Content is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
