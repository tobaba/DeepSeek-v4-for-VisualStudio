using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 回归（线上事故 650/681）：记忆判定的**提示词**与**返回结果**都不得进入下一轮主流程请求。
///
/// 线上现象：681 号请求的历史里出现了
///   [11] user  = "请基于以上完整对话上下文，判断最近一轮问答是否有值得持久化的信息…"
///   [12] assistant = "{"scope":"user","path":"preferences.md",…}"
/// 这两条是 650 号请求（记忆判定子任务）的尾提示与返回结果，本不该出现在主对话历史中。
///
/// 根因有两条，本用例同时覆盖：
///   1. 工具循环无条件把 interim assistant（子任务返回的 JSON）落库；
///   2. 子任务尾提示被当成"发出内容"登记。
/// </summary>
public class MemorySubTaskIsolationTests
{
    private const string MemoryUserPrompt =
        "请基于以上完整对话上下文，判断最近一轮问答是否有值得持久化的信息。";

    private const string MemorySystemPrompt = "你是一个记忆管理助手。";

    [Fact]
    public void MemorySubTask_DoesNotWriteAnyHistory()
    {
        var mgr = new ConversationContextManager();
        mgr.SetSolutionPath(@"F:\CPP\leetcode");

        // 主流程：用户提问 + 一次工具调用 + 最终回答
        mgr.AddUserMessage("https://leetcode.cn/problems/number-of-ways-to-reorder-array-to-get-same-bst/");
        var toolCall = new ToolCall
        {
            Id = "call_1",
            Type = "function",
            Function = new ToolCallFunction { Name = "fetch_webpage", Arguments = "{}" },
        };
        mgr.AddAssistantMessage("我先抓取题目确认要求，再给出解答。", null, new List<ToolCall> { toolCall });
        mgr.AddToolResult("call_1", "fetch_webpage", "=== 网页内容: ... ===");
        mgr.AddAssistantMessage("## LeetCode 1569 解答正文");

        int before = mgr.MessageCount;

        // ── 记忆判定子任务（等价于 UI 的 AutoRecordMemoryAsync + persistToHistory:false）──
        var sent = mgr.BuildApiMessagesWithSubTaskPrompt(MemoryUserPrompt, MemorySystemPrompt);
        sent.Should().Contain(m => m.Content == MemoryUserPrompt);

        // 子任务既不应写提示词，也不应写返回结果（工具循环被 persistToHistory:false 关掉）。
        mgr.MessageCount.Should().Be(before, "记忆判定子任务不得改动 _entries");

        // ── 下一轮主流程请求 ──
        var next = mgr.BuildApiMessages();

        next.Should().NotContain(m => m.Content == MemoryUserPrompt,
            "记忆判定提示词不得出现在下一轮请求");
        next.Should().NotContain(m => m.Content == MemorySystemPrompt,
            "记忆管理助手系统提示词不得出现在下一轮请求");
        next.Should().NotContain(m => (m.Content ?? "").StartsWith("{\"scope\""),
            "记忆判定的 JSON 返回结果不得出现在下一轮请求");

        // 主流程内容必须完整保留（不能因为隔离子任务而丢消息）
        next.Should().Contain(m => m.Role == "tool" && m.Name == "fetch_webpage");
        next.Should().Contain(m => m.Content == "## LeetCode 1569 解答正文");
    }

    [Fact]
    public void MainFlowAfterSubTask_StillRecordsItsOwnInjections()
    {
        var mgr = new ConversationContextManager();
        mgr.AddUserMessage("原始提问");

        // 子任务先跑一遍（不应污染）
        mgr.BuildApiMessagesWithSubTaskPrompt(MemoryUserPrompt, MemorySystemPrompt);

        // 随后主流程请求带 Handoff 注入，必须被完整记录
        var mainSent = new List<ChatApiMessage>
        {
            new() { Role = "system", Content = "STABLE" },
            new() { Role = "user", Content = "[本轮用户需求] 原始提问" },
            new() { Role = "system", Content = "[当前解决方案: F:\\CPP\\database]\n\n[Git Context]\nGit Branch: master" },
            new() { Role = "user", Content = "[当前任务目标] 你是一个 Edit Agent。" },
        };
        mgr.SyncSentMessagesToHistory(mainSent);

        var rebuilt = mgr.BuildApiMessages();
        rebuilt.Should().Contain(m => m.Content == "[当前任务目标] 你是一个 Edit Agent。",
            "主流程注入必须保留：不得因为隔离子任务而丢掉主流程消息");
        rebuilt.Should().Contain(m => (m.Content ?? "").StartsWith("[当前解决方案"),
            "Handoff 的 volatile 块必须保留");
    }
}
