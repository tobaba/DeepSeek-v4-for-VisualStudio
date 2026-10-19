using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 回归（线上 152 号请求 [48][49]）：身份边界提示被重复写入两次。
///
/// 复现路径：
///   1. Handoff 前 <c>SnapshotForCache()</c> 记下边界 <c>N = _entries.Count</c>；
///   2. Handoff 分支把身份边界提示 <c>AddCustomMessage</c> 追加进 <c>_entries</c>（落在下标 N）；
///   3. 发送后对账仍以 N 为搜索上界 → 看不到刚写下标 N 的那条 → 判为"脱节内容"再补一份。
///
/// 结果：同一句提示在历史里相邻出现两次（都参与重放），白耗 token 并稀释该提示的约束力。
/// 该缺陷出现在 152 号请求 `[48]`/`[49]`，146 号请求对应位置只有一份。
/// </summary>
public class HandoffBoundaryHintDuplicationTests
{
    private const string Hint = ">  **身份边界提示**：上方历史来自前一个 Agent。";

    private static List<ChatApiMessage> BuildHandoffSent(
        ConversationContextManager mgr, string taskGoal)
    {
        var sent = mgr.BuildContextPrefix();
        sent.Add(new ChatApiMessage { Role = "user", Content = "[本轮用户需求] 原始提问" });
        sent.Add(new ChatApiMessage { Role = "system", Content = Hint });
        sent.Add(new ChatApiMessage { Role = "user", Content = taskGoal });
        sent.Add(new ChatApiMessage { Role = "system", Content = "尾部行为指令" });
        return sent;
    }

    [Fact]
    public void HandoffBoundaryHint_IsPersistedExactlyOnce_WhenCacheSnapshotIsActive()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        // Handoff 前的缓存边界快照 + Handoff 分支固化身份边界提示
        mgr.SnapshotForCache();
        mgr.AddCustomMessage("system", Hint);

        mgr.SyncSentMessagesToHistory(BuildHandoffSent(mgr, "[当前任务目标] 你是一个 Edit Agent。"));

        // GetFullContext = 会被持久化/重放的历史，不受边界快照渲染窗口影响
        mgr.GetFullContext().Count(m => m.Content == Hint).Should().Be(1,
            "身份边界提示在历史里必须唯一：重复会白耗 token 并稀释该提示的约束力");
    }

    [Fact]
    public void HandoffBoundaryHint_AppearsOnceInNextRequest_AfterBoundaryClears()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        mgr.SnapshotForCache();
        mgr.AddCustomMessage("system", Hint);
        mgr.SyncSentMessagesToHistory(BuildHandoffSent(mgr, "[当前任务目标] 你是一个 Edit Agent。"));

        // 边界结束后（新用户消息会清除快照），下一轮请求应只看到一份提示
        mgr.ClearCacheSnapshot();
        mgr.BuildApiMessages().Count(m => m.Content == Hint).Should().Be(1);
    }

    [Fact]
    public void HandoffBoundaryHint_IsNotDuplicated_AcrossRepeatedSync()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        mgr.SnapshotForCache();
        mgr.AddCustomMessage("system", Hint);

        var sent = BuildHandoffSent(mgr, "[当前任务目标] Edit 任务");
        mgr.SyncSentMessagesToHistory(sent);
        mgr.SyncSentMessagesToHistory(sent);

        mgr.GetFullContext().Count(m => m.Content == Hint).Should().Be(1);
    }

    /// <summary>
    /// 边界快照不应限制对账的匹配范围：快照之后追加的消息也必须能被匹配到，
    /// 否则每次 Handoff 都会把"快照后写入的内容"重新补一份。
    /// </summary>
    [Fact]
    public void Sync_MatchesEntriesWrittenAfterSnapshot()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        mgr.SnapshotForCache();
        mgr.AddAssistantMessage("快照之后写入的回答");

        var sent = mgr.BuildContextPrefix();
        sent.Add(new ChatApiMessage { Role = "user", Content = "[本轮用户需求] 原始提问" });
        sent.Add(new ChatApiMessage { Role = "assistant", Content = "快照之后写入的回答" });

        mgr.SyncSentMessagesToHistory(sent).Should().Be(0,
            "快照后可匹配的条目不应被当成脱节内容补写");
    }
}
