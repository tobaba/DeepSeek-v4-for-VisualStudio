namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 单测：ConversationContextManager.SyncSentMessagesToHistory ——
/// 「发出过的内容都必须能在 _entries 中查到」这一不变量的核心保障。
///
/// 背景（真实线上缺陷）：请求构建器会把 Agent 行为指令、未固化的 volatile 块、
/// Handoff 任务提示等临时追加到消息副本上，这些内容此前从不写回 _entries。
/// 于是从 _entries 重建历史的链路（记忆提取、总结润色等子任务）与会话持久化
/// 都会看到一份「缺了几条」的历史，既丢上下文，也让前缀缓存从缺失点断裂。
/// </summary>
public class SentMessagesHistorySyncTests
{
    private static ChatApiMessage Msg(string role, string? content) => new() { Role = role, Content = content };

    [Fact]
    public void SyncSentMessages_AppendsMessagesMissingFromHistory()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("原始提问");
        manager.AddAssistantMessage("原始回答");

        var sent = new List<ChatApiMessage>
        {
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("assistant", "原始回答"),
            Msg("system", "Agent 行为指令"),          // 请求侧临时注入
            Msg("user", "[当前任务目标] Edit 步骤提示"), // 请求侧临时注入
        };

        int appended = manager.SyncSentMessagesToHistory(sent);

        appended.Should().Be(2, "只有两条消息是历史中没有的");
        manager.MessageCount.Should().Be(4);
    }

    [Fact]
    public void SyncSentMessages_IsIdempotent()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("原始提问");

        var sent = new List<ChatApiMessage>
        {
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("user", "[当前任务目标] Edit 步骤提示"),
        };

        manager.SyncSentMessagesToHistory(sent).Should().Be(1);
        manager.SyncSentMessagesToHistory(sent).Should().Be(0, "重复对账不得重复写入");
        manager.MessageCount.Should().Be(2);
    }

    [Fact]
    public void SyncSentMessages_SystemMessagesAreRecordedButNotReplayed()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("原始提问");

        manager.SyncSentMessagesToHistory(new List<ChatApiMessage>
        {
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("system", "Agent 行为指令"),
        });

        // 已登记（可在 _entries / 调试摘要中查到）
        manager.GetDebugSummary().Should().Contain("Agent 行为指令");

        // 但不参与重放：下一次构建的请求不应重复出现该指令
        var rebuilt = manager.BuildApiMessages();
        rebuilt.Should().NotContain(m => m.Content == "Agent 行为指令");
    }

    [Fact]
    public void SyncSentMessages_UserMessageIsReplayedInSubsequentRequests()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("原始提问");

        manager.SyncSentMessagesToHistory(new List<ChatApiMessage>
        {
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("user", "[当前任务目标] Edit 步骤提示"),
        });

        var rebuilt = manager.BuildApiMessages();
        rebuilt.Should().Contain(m => m.Content == "[当前任务目标] Edit 步骤提示");
    }

    [Fact]
    public void SyncSentMessages_SubTaskTail_IsRecordedButNotReplayed()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("原始提问");
        manager.AddAssistantMessage("原始回答");

        var messages = manager.BuildApiMessagesWithSubTaskPrompt("记忆判定指令", "记忆管理助手提示");

        // 两条尾提示已登记
        var debug = manager.GetDebugSummary();
        debug.Should().Contain("记忆判定指令");
        debug.Should().Contain("记忆管理助手提示");

        // 但不得污染下一次主对话请求
        var rebuilt = manager.BuildApiMessages();
        rebuilt.Should().NotContain(m => m.Content == "记忆判定指令");
        rebuilt.Should().NotContain(m => m.Content == "记忆管理助手提示");

        // 原快照未受副作用影响
        messages[messages.Count - 1].Role.Should().Be("system");
        messages[messages.Count - 2].Role.Should().Be("user");
    }

    [Fact]
    public void SyncSentMessages_StructuralPrefix_IsNotRecordedAsHistory()
    {
        var manager = new ConversationContextManager();
        manager.SetSystemPrompt("稳定系统提示词");
        manager.AddUserMessage("原始提问");

        // 构建一次真实请求（含 [0] 稳定提示词、[1] 动态块）
        var sent = manager.BuildApiMessages();
        var stablePrompt = sent.First(m => m.Role == "system").Content;
        stablePrompt.Should().NotBeNullOrWhiteSpace();

        manager.SyncSentMessagesToHistory(sent).Should().Be(0,
            "结构性前缀由字段重建，不应被当成脱节消息补写");

        // 再构建一次不应出现重复前缀
        var rebuilt = manager.BuildApiMessages();
        rebuilt.Count(m => m.Content == stablePrompt).Should().Be(1);
    }

    [Fact]
    public void SyncSentMessages_RecordsToolCallsAndResults()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("原始提问");

        var toolCall = new ToolCall
        {
            Id = "call_1",
            Type = "function",
            Function = new ToolCallFunction { Name = "read_file", Arguments = "{}" },
        };

        manager.SyncSentMessagesToHistory(new List<ChatApiMessage>
        {
            Msg("user", "[本轮用户需求] 原始提问"),
            new() { Role = "assistant", Content = null, ToolCalls = new List<ToolCall> { toolCall } },
            new() { Role = "tool", Content = "文件内容", ToolCallId = "call_1", Name = "read_file" },
        });

        manager.MessageCount.Should().Be(3);

        var rebuilt = manager.BuildApiMessages();
        rebuilt.Should().Contain(m => m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count == 1);
        rebuilt.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_1");
    }

    [Fact]
    public void SyncSentMessages_DoesNotInventNewTurns()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("原始提问");
        manager.TurnCount.Should().Be(1);

        manager.SyncSentMessagesToHistory(new List<ChatApiMessage>
        {
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("user", "[当前任务目标] Edit 步骤提示"),
        });

        manager.TurnCount.Should().Be(1, "步骤提示属于当前轮，不得凭空新增轮次");
    }

    // ── HasVolatileSnapshotInCurrentTurn ──

    [Fact]
    public void HasVolatileSnapshotInCurrentTurn_TrueAfterPersistingSnapshot()
    {
        var manager = new ConversationContextManager();
        manager.SetSolutionPath(@"C:\proj");
        manager.AddUserMessage("原始提问");

        string? block = manager.BuildVolatileContextBlock();
        block.Should().NotBeNull();

        manager.PersistCurrentVolatileSnapshot().Should().BeTrue();
        manager.HasVolatileSnapshotInCurrentTurn(block).Should().BeTrue();
    }

    [Fact]
    public void HasVolatileSnapshotInCurrentTurn_FalseForDifferentContent()
    {
        var manager = new ConversationContextManager();
        manager.SetSolutionPath(@"C:\proj");
        manager.AddUserMessage("原始提问");
        manager.PersistCurrentVolatileSnapshot();

        manager.HasVolatileSnapshotInCurrentTurn("完全不同的内容").Should().BeFalse();
        manager.HasVolatileSnapshotInCurrentTurn(null).Should().BeFalse();
    }

    // ── 恢复语义：步骤提示不得被算作新轮次 ──

    [Fact]
    public void RestoreFullContext_StepPrompt_DoesNotBecomeNewTurn()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("原始提问");
        manager.SyncSentMessagesToHistory(new List<ChatApiMessage>
        {
            Msg("user", "[本轮用户需求] 原始提问"),
            Msg("user", "[当前任务目标] Edit 步骤提示"),
        });

        var saved = manager.GetFullContext();

        var restored = new ConversationContextManager();
        restored.RestoreFullContext(saved);

        restored.TurnCount.Should().Be(1, "步骤提示带「当前任务目标」前缀，恢复后不应算作独立轮次");
        restored.BuildApiMessages().Should().Contain(m => m.Content == "[当前任务目标] Edit 步骤提示");
    }

    [Fact]
    public void RestoreFullContext_MultimodalOnlyUser_StillCountsAsTurn()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage(
            string.Empty,
            new List<ChatContentPart>
            {
                new()
                {
                    Type = "image_url",
                    ImageUrl = new ChatImageUrl { Url = "data:image/png;base64,AAAA" },
                },
            });

        var saved = manager.GetFullContext();

        var restored = new ConversationContextManager();
        restored.RestoreFullContext(saved);

        restored.TurnCount.Should().Be(1, "纯图片轮次仍是真实用户轮次");
    }

    [Fact]
    public void GetFullContext_ExcludesReplayExcludedScaffolding()
    {
        var manager = new ConversationContextManager();
        manager.AddUserMessage("原始提问");
        manager.BuildApiMessagesWithSubTaskPrompt("记忆判定指令", "记忆管理助手提示");

        var saved = manager.GetFullContext();

        saved.Should().NotContain(m => m.Content == "记忆判定指令");
        saved.Should().NotContain(m => m.Content == "记忆管理助手提示");
    }
}
