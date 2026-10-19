using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeepSeek_v4_for_VisualStudio.View
{
    /// <summary>
    /// 文件改动列表（聊天区与状态行之间）。
    ///
    /// 数据来源是每轮随会话持久化的 <see cref="ChatMessage.ChangedFiles"/>
    /// （Agent 编辑与内联编辑统一经 <c>RecordFileChangesForTurn</c> 登记）；
    /// 早期版本的消息没有该字段时，回退到该轮计划 JSON 里的改动文件。
    /// 按会话聚合，因此重启、切换、回退后依然与真实改动一致。
    /// 默认只显示总计（+X / -Y、文件数），展开后逐文件列出，点击条目在 IDE 中打开该文件。
    /// </summary>
    public partial class DeepSeekChatControl
    {
        /// <summary>每个文件一条的展示模型。</summary>
        private sealed class ChangeFileRow
        {
            public string FilePath { get; init; } = string.Empty;
            public string FileName { get; init; } = string.Empty;
            public int LinesAdded { get; init; }
            public int LinesRemoved { get; init; }
            public string AddedText => LinesAdded > 0 ? $"+{LinesAdded}" : string.Empty;
            public string RemovedText => LinesRemoved > 0 ? $"-{LinesRemoved}" : string.Empty;
        }

        private bool _changeSummaryExpanded;

        /// <summary>
        /// 重新计算并刷新文件改动列表。可从任意线程调用；WPF 操作会切回 UI 线程。
        /// </summary>
        private void RefreshChangeSummary()
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(RefreshChangeSummary));
                return;
            }

            if (ChangeSummaryArea == null) return;

            var files = BuildChangeFileRows();
            if (files.Count == 0)
            {
                ChangeSummaryArea.Visibility = Visibility.Collapsed;
                ChangeFileListBox.ItemsSource = null;
                return;
            }

            int totalAdded = files.Sum(f => f.LinesAdded);
            int totalRemoved = files.Sum(f => f.LinesRemoved);

            ChangeSummaryArea.Visibility = Visibility.Visible;
            ChangeSummaryTitle.Text = string.Format(
                LocalizationService.Instance["changeSummary.files"], files.Count);
            ChangeSummaryAdded.Text = totalAdded > 0 ? $"+{totalAdded}" : string.Empty;
            ChangeSummaryRemoved.Text = totalRemoved > 0 ? $"-{totalRemoved}" : string.Empty;
            ApplyChangeSummaryExpandedState();

            ChangeFileListBox.ItemsSource = files;
        }

        /// <summary>
        /// 按会话聚合同一文件的增删行数。
        /// 每轮优先取消息自身持久化的 <see cref="ChatMessage.ChangedFiles"/>；
        /// 早期版本的消息没有该字段，回退到该轮计划 JSON 里的 ChangedFiles。
        /// 同一条消息只取一个来源，避免同一轮被统计两次。
        /// </summary>
        private List<ChangeFileRow> BuildChangeFileRows()
        {
            // 路径（忽略大小写）→ 展示用路径 + 增删累计
            var aggregated = new Dictionary<string, (string DisplayPath, int Added, int Removed)>(
                StringComparer.OrdinalIgnoreCase);

            void Accumulate(IEnumerable<FileChangeSummary>? changes)
            {
                if (changes == null) return;
                foreach (var change in changes)
                {
                    if (change == null || string.IsNullOrWhiteSpace(change.FilePath))
                        continue;

                    string key = change.FilePath;
                    // 删除文件时 LinesRemoved 可能记为 -1（仅表示「已删除」），不计入行数。
                    int added = Math.Max(0, change.LinesAdded);
                    int removed = Math.Max(0, change.LinesRemoved);

                    if (aggregated.TryGetValue(key, out var existing))
                    {
                        aggregated[key] = (
                            string.IsNullOrWhiteSpace(existing.DisplayPath) ? key : existing.DisplayPath,
                            existing.Added + added,
                            existing.Removed + removed);
                    }
                    else
                    {
                        aggregated[key] = (key, added, removed);
                    }
                }
            }

            List<ChatMessage> snapshot;
            lock (_lock)
            {
                snapshot = new List<ChatMessage>(_messages);
            }

            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            foreach (var msg in snapshot)
            {
                // 首选本轮持久化的改动记录
                if (msg.ChangedFiles != null && msg.ChangedFiles.Count > 0)
                {
                    Accumulate(msg.ChangedFiles);
                    continue;
                }

                // 兼容历史会话：回退到该轮计划中的改动文件
                if (string.IsNullOrEmpty(msg.PlanJson)) continue;
                try
                {
                    var plan = JsonSerializer.Deserialize<AgentTaskPlan>(msg.PlanJson, opts);
                    Accumulate(plan?.ChangedFiles);
                }
                catch
                {
                    // 历史数据格式异常时跳过该轮，不影响其它轮次统计
                }
            }

            return aggregated
                .Select(kv => new ChangeFileRow
                {
                    FilePath = kv.Value.DisplayPath,
                    FileName = Path.GetFileName(kv.Value.DisplayPath),
                    LinesAdded = kv.Value.Added,
                    LinesRemoved = kv.Value.Removed,
                })
                .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>按展开状态同步明细列表与箭头方向。</summary>
        private void ApplyChangeSummaryExpandedState()
        {
            if (ChangeFileListBox != null)
                ChangeFileListBox.Visibility = _changeSummaryExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (ChangeSummaryChevron != null)
                ChangeSummaryChevron.Text = _changeSummaryExpanded ? "\uE70D" : "\uE76C";
        }

        /// <summary>
        /// 清除某轮登记的文件改动（该轮改动已被回退时调用）。
        /// </summary>
        private void ClearRecordedFileChanges(int userMsgIndex)
        {
            lock (_lock)
            {
                if (userMsgIndex >= 0 && userMsgIndex < _messages.Count)
                    _messages[userMsgIndex].ChangedFiles = new List<FileChangeSummary>();
            }
        }

        private void ChangeSummaryHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _changeSummaryExpanded = !_changeSummaryExpanded;
            ApplyChangeSummaryExpandedState();
        }

        private void ChangeFileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChangeFileListBox.SelectedItem is not ChangeFileRow row) return;

            // 立即清空选中，避免同一行再次点击无响应
            ChangeFileListBox.SelectedItem = null;

            if (string.IsNullOrWhiteSpace(row.FilePath)) return;
            _ = NavigateToFileAsync(row.FilePath);
        }
    }
}
