using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 回归：`_entries` 的两条不变量。
///
/// 1. **主流程消息一条都不能缺**：Handoff 插入的 volatile 块、目标 Agent 的任务提示
///    （「[当前任务目标] 你是一个 Edit Agent…」）等只在请求副本上存在的内容，
///    必须补写进 `_entries` 并参与重放，否则从历史重建的链路会「缺了几条」。
/// 2. **一次性子任务绝不能落库**：记忆判定、总结润色等子任务的提示词与返回结果
///    不属于主流程对话；一旦落库，下一轮请求就会把「记忆管理助手」的问答当成本轮内容
///    （线上事故：681 号请求的 [11][12] 即为此类泄漏）。
/// </summary>
public class SentMessagesHistorySyncTests
{
    private static ChatApiMessage Msg(string role, string? content) => new() { Role = role, Content = content };

    /// <summary>
    /// 构造一次真实形状的请求：真实的结构性前缀（稳定 system + 动态块）+ 给定正文。
    /// 必须用 manager 自己产出的前缀，否则对账的结构性跳过逻辑无法识别。
    /// </summary>
    private static List<ChatApiMessage> RequestWith(ConversationContextManager mgr, params ChatApiMessage[] body)
    {
        var list = mgr.BuildContextPrefix();
        list.AddRange(body);
        return list;
    }

    // ── 不变量 1：主流程消息不得缺失 ──

    [Fact]
    public void Sync_AppendsMainFlowMessagesMissingFromHistory()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        // 模拟一次 Handoff 请求：历史 + volatile 块 + 目标 Agent 任务提示 + 尾部行为指令
        var sent = RequestWith(mgr, 
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("system", "[当前解决方案: F:\\CPP\\database]\n\n[Git Context]\nGit Branch: master"),
            Msg("user", "[当前任务目标] 你是一个 Edit Agent，正在执行任务：「执行代码变更」。"),
            Msg("system", "你当前处于 Edit 模式……"));

        int appended = mgr.SyncSentMessagesToHistory(sent);

        // volatile 块 + 任务提示 + 尾部行为指令 = 3 条脱节内容（都是主流程内容，一条都不能缺）
        appended.Should().Be(3);
    }

    [Fact]
    public void Sync_VolatileBlockAndTaskGoal_AreReplayed()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        mgr.SyncSentMessagesToHistory(RequestWith(mgr, 
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("system", "[当前解决方案: F:\\CPP\\database]\n\n[Git Context]\nGit Branch: master"),
            Msg("user", "[当前任务目标] 你是一个 Edit Agent。")));

        var rebuilt = mgr.BuildApiMessages();

        rebuilt.Should().Contain(m => m.Content == "[当前解决方案: F:\\CPP\\database]\n\n[Git Context]\nGit Branch: master",
            "volatile 块属于主流程内容，重建时必须出现，否则历史缺消息");
        rebuilt.Should().Contain(m => m.Content == "[当前任务目标] 你是一个 Edit Agent。",
            "Handoff 任务提示属于主流程内容，重建时必须出现");
    }

    [Fact]
    public void Sync_TailSystemPrompt_IsRecordedButNotReplayed()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        mgr.SyncSentMessagesToHistory(RequestWith(mgr, 
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("system", "你当前处于 Ask 模式……（尾部行为指令）")));

        // 已登记（保存不丢）
        mgr.GetDebugSummary().Should().Contain("你当前处于 Ask 模式");

        // 但不重放：尾部指令每次请求都会重新生成，重放会随请求数无限累积
        mgr.BuildApiMessages().Should().NotContain(m => m.Content == "你当前处于 Ask 模式……（尾部行为指令）");
    }

    [Fact]
    public void Sync_IsIdempotent()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        var sent = RequestWith(mgr,
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("user", "[当前任务目标] Edit 步骤提示"));

        // 真实用户轮次在落库时已写入「本轮用户需求」前缀，故只有任务提示属于脱节内容
        mgr.SyncSentMessagesToHistory(sent).Should().Be(1);
        mgr.SyncSentMessagesToHistory(sent).Should().Be(0, "重复对账不得重复写入");
    }

    [Fact]
    public void Sync_RepeatedSyncAcrossRequests_DoesNotDuplicateHistory()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        // 同一轮内多次请求（如 Edit 多步骤推进）反复对账，同一内容只能有一份
        var sent = RequestWith(mgr, 
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("system", "[当前解决方案: F:\\CPP\\database]\n\n[Git Context]\nGit Branch: master"),
            Msg("user", "[当前任务目标] Edit 步骤提示"));

        mgr.SyncSentMessagesToHistory(sent);
        mgr.SyncSentMessagesToHistory(sent);
        mgr.SyncSentMessagesToHistory(sent);

        var rebuilt = mgr.BuildApiMessages();
        rebuilt.Count(m => m.Content == "[当前任务目标] Edit 步骤提示").Should().Be(1);
        rebuilt.Count(m => (m.Content ?? "").StartsWith("[当前解决方案")).Should().Be(1);
    }

    /// <summary>
    /// 回归（线上 681 号请求的 [3]-[6] 与 [7]-[10] 逐字节相同）：
    /// 含工具调用链的一轮对话，反复对账后绝不能出现整轮重复。
    /// </summary>
    [Fact]
    public void Sync_ToolCallTurn_IsNotDuplicatedByRepeatedSync()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("抓取并解答这道题");

        var toolCall = new ToolCall
        {
            Id = "call_1",
            Type = "function",
            Function = new ToolCallFunction { Name = "fetch_webpage", Arguments = "{}" },
        };
        mgr.AddAssistantMessage("我先抓取题目。", null, new List<ToolCall> { toolCall });
        mgr.AddToolResult("call_1", "fetch_webpage", "=== 网页内容 ===");
        mgr.AddAssistantMessage("## 解答正文");

        var sent = mgr.BuildApiMessages();
        mgr.SyncSentMessagesToHistory(sent);
        mgr.SyncSentMessagesToHistory(sent);

        var rebuilt = mgr.BuildApiMessages();
        rebuilt.Count(m => (m.Content ?? "") == "我先抓取题目。").Should().Be(1, "工具调用轮不得整轮重复");
        rebuilt.Count(m => m.Role == "tool").Should().Be(1);
        rebuilt.Count(m => (m.Content ?? "") == "## 解答正文").Should().Be(1);
    }

    [Fact]
    public void Sync_StructuralPrefix_IsNotRecordedAsHistory()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        var sent = mgr.BuildApiMessages();   // 含稳定前缀与动态块的正常请求
        mgr.SyncSentMessagesToHistory(sent).Should().Be(0,
            "结构性前缀由字段重建，不应被当成脱节消息补写");

        // 用户内容仍在（带落库前缀），且未被重复登记
        var rebuilt = mgr.BuildApiMessages();
        rebuilt.Count(m => m.Role == "user").Should().Be(1);
        rebuilt.Count(m => (m.Content ?? "").Contains("原始提问")).Should().Be(1);
    }

    [Fact]
    public void Sync_DoesNotInventNewTurns()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");
        mgr.TurnCount.Should().Be(1);

        mgr.SyncSentMessagesToHistory(RequestWith(mgr, 
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("user", "[当前任务目标] Edit 步骤提示")));

        mgr.TurnCount.Should().Be(1, "步骤提示属于当前轮，不得凭空新增轮次");
    }

    // ── 不变量 2：一次性子任务不得落库 ──

    [Fact]
    public void SubTaskPromptBuild_DoesNotTouchHistory()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");
        mgr.AddAssistantMessage("原始回答");

        int before = mgr.MessageCount;
        mgr.BuildApiMessagesWithSubTaskPrompt("记忆判定指令", "记忆管理助手提示");

        mgr.MessageCount.Should().Be(before, "子任务只构建请求，绝不写历史");
    }

    [Fact]
    public void SubTaskPromptTail_IsAbsentFromNextRequest()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");
        mgr.AddAssistantMessage("原始回答");

        var sent = mgr.BuildApiMessagesWithSubTaskPrompt("记忆判定指令", "记忆管理助手提示");
        // 子任务确实把两条尾提示发出去了
        sent.Should().Contain(m => m.Content == "记忆判定指令");
        sent.Should().Contain(m => m.Content == "记忆管理助手提示");

        var next = mgr.BuildApiMessages();
        next.Should().NotContain(m => m.Content == "记忆判定指令",
            "子任务提示词不得出现在下一轮主流程请求中");
        next.Should().NotContain(m => m.Content == "记忆管理助手提示");
    }

    [Fact]
    public void SubTaskAssistantResult_DoesNotLeakIntoHistory()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");
        mgr.AddAssistantMessage("原始回答");

        mgr.BuildApiMessagesWithSubTaskPrompt("记忆判定指令", "记忆管理助手提示");
        // 子任务如果不落库，其返回结果也不应出现在历史里
        var next = mgr.BuildApiMessages();

        next.Should().NotContain(m => (m.Content ?? "").StartsWith("{\"scope\""));
    }

    [Fact]
    public void GetFullContext_ExcludesNonReplayedScaffolding()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        mgr.SyncSentMessagesToHistory(RequestWith(mgr, 
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("system", "尾部行为指令")));

        var saved = mgr.GetFullContext();
        saved.Should().NotContain(m => m.Content == "尾部行为指令",
            "只登记不重放的尾部提示不应写入会话持久化，否则重启后会被当成历史重放");
    }

    // ── 恢复语义 ──

    [Fact]
    public void RestoreFullContext_StepPrompt_DoesNotBecomeNewTurn()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");
        mgr.SyncSentMessagesToHistory(RequestWith(mgr, 
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("user", "[当前任务目标] Edit 步骤提示")));

        var restored = new ConversationContextManager();
        restored.RestoreFullContext(mgr.GetFullContext());

        restored.TurnCount.Should().Be(1, "步骤提示带「当前任务目标」前缀，恢复后不应算作独立轮次");
    }

    [Fact]
    public void RestoreFullContext_MultimodalOnlyUser_StillCountsAsTurn()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage(
            string.Empty,
            new List<ChatContentPart>
            {
                new()
                {
                    Type = "image_url",
                    ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,AAAA" },
                },
            });

        var restored = new ConversationContextManager();
        restored.RestoreFullContext(mgr.GetFullContext());

        restored.TurnCount.Should().Be(1, "纯图片轮次仍是真实用户轮次");
    }

    // ── volatile 快照去重 ──

    [Fact]
    public void HasVolatileSnapshotInCurrentTurn_TrueAfterPersistingSnapshot()
    {
        var mgr = new ConversationContextManager();
        mgr.SetSolutionPath(@"C:\proj");
        mgr.AddUserMessage("原始提问");

        string? block = mgr.BuildVolatileContextBlock();
        block.Should().NotBeNull();

        mgr.PersistCurrentVolatileSnapshot().Should().BeTrue();
        mgr.HasVolatileSnapshotInCurrentTurn(block).Should().BeTrue();
    }

    [Fact]
    public void HasVolatileSnapshotInCurrentTurn_FalseForDifferentContent()
    {
        var mgr = new ConversationContextManager();
        mgr.SetSolutionPath(@"C:\proj");
        mgr.AddUserMessage("原始提问");
        mgr.PersistCurrentVolatileSnapshot();

        mgr.HasVolatileSnapshotInCurrentTurn("完全不同的内容").Should().BeFalse();
        mgr.HasVolatileSnapshotInCurrentTurn(null).Should().BeFalse();
    }
}
