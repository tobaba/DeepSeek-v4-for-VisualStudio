using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 流式批处理的正文累积规则（从 UI 批处理中拆出，便于单测）。
    ///
    /// 背景：气泡正文有两条推送路径——
    ///   c  = 整段正文（一次性替换 textNode）
    ///   cd = 正文增量（追加到 textNode）
    /// 工具调用轮次会把「过程时间线」当作整段正文推一次，此后整段正文非空。
    /// 若后续增量只写进增量缓冲，就会在推送时被整段快照挤掉并随增量缓冲一起清空，
    /// 最终回答只能等收尾定稿时一次性出现（表现为「有工具调用时最终回答不流式」）。
    /// 因此整段正文非空时，增量必须同时并入整段正文。
    /// </summary>
    internal static class StreamBatchContentRules
    {
        /// <summary>时间线与正文之间的段落分隔符，与 BuildAssistantDisplayContent 保持一致。</summary>
        public const char SegmentSeparator = '\n';

        /// <summary>
        /// 把正文增量并入整段正文（仅在整段正文非空时）。
        /// </summary>
        /// <param name="fullContent">整段正文缓冲。</param>
        /// <param name="delta">本次增量。</param>
        /// <param name="deltaStarted">
        /// 整段正文被整体替换后是否已并入过增量；用于在"新段落"开头补一次分隔符。
        /// </param>
        /// <returns>是否实际并入（整段正文为空时返回 false，此时仍走 cd 增量路径）。</returns>
        public static bool TryMergeDelta(
            StringBuilder fullContent,
            string? delta,
            ref bool deltaStarted)
        {
            if (string.IsNullOrEmpty(delta) || fullContent == null || fullContent.Length == 0)
                return false;

            if (!deltaStarted)
            {
                fullContent.Append(SegmentSeparator);
                deltaStarted = true;
            }

            fullContent.Append(delta);
            return true;
        }

        /// <summary>
        /// 整段正文被整体替换（工具时间线 / 收尾定稿）后重置增量记账：
        /// 后续增量属于新段落，需要重新补分隔符，并按新的长度基准计算增长量。
        /// </summary>
        public static void ResetAfterFullContent(ref bool deltaStarted, ref int lastFlushedLength)
        {
            deltaStarted = false;
            lastFlushedLength = 0;
        }

        /// <summary>
        /// 正文是否已增长到值得重推整段。按"自上次刷新以来的增长量"判定，
        /// 不能用绝对长度——否则正文一旦超过阈值就会每次增量都触发整段重推。
        /// </summary>
        public static bool GrewEnough(int currentLength, int lastFlushedLength, int interval)
            => currentLength - lastFlushedLength >= interval;
    }
}
