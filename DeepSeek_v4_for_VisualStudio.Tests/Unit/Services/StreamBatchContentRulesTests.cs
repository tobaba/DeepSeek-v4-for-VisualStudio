using System.Text;
using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 流式批处理正文累积规则：防止「有工具调用时最终回答不流式、直接弹出来」回归。
/// </summary>
public class StreamBatchContentRulesTests
{
    [Fact]
    public void TryMergeDelta_EmptyFullContent_IsSkippedSoDeltaPathStays()
    {
        // 纯问答流式：整段正文为空 → 增量不并入，继续走高高效的 cd 增量路径。
        var full = new StringBuilder();
        bool started = false;

        StreamBatchContentRules.TryMergeDelta(full, "hello", ref started).Should().BeFalse();
        full.Length.Should().Be(0);
        started.Should().BeFalse();
    }

    [Fact]
    public void TryMergeDelta_AfterTimeline_MergesAnswerWithSeparator()
    {
        // 工具轮次先把时间线作为整段正文推过一次。
        var full = new StringBuilder("思考过程");
        bool started = false;

        StreamBatchContentRules.TryMergeDelta(full, "最终回答", ref started).Should().BeTrue();
        full.ToString().Should().Be("思考过程\n最终回答");
        started.Should().BeTrue();

        // 后续增量直接追加，不再重复补分隔符。
        StreamBatchContentRules.TryMergeDelta(full, "，继续", ref started).Should().BeTrue();
        full.ToString().Should().Be("思考过程\n最终回答，继续");
    }

    [Fact]
    public void TryMergeDelta_EmptyOrNullDelta_NoOp()
    {
        var full = new StringBuilder("时间线");
        bool started = false;

        StreamBatchContentRules.TryMergeDelta(full, null, ref started).Should().BeFalse();
        StreamBatchContentRules.TryMergeDelta(full, string.Empty, ref started).Should().BeFalse();
        full.ToString().Should().Be("时间线");
        started.Should().BeFalse();
    }

    [Fact]
    public void ResetAfterFullContent_StartsNewSegment()
    {
        var full = new StringBuilder("时间线\n旧回答");
        bool started = true;
        int lastFlushed = 42;

        StreamBatchContentRules.ResetAfterFullContent(ref started, ref lastFlushed);
        started.Should().BeFalse();
        lastFlushed.Should().Be(0);

        // 整段替换为新的时间线后，下一段回答要重新补分隔符。
        full.Clear();
        full.Append("新的时间线");
        StreamBatchContentRules.TryMergeDelta(full, "新回答", ref started);
        full.ToString().Should().Be("新的时间线\n新回答");
    }

    [Fact]
    public void TryMergeDelta_AfterFullContentReplacement_SeparatesNewAnswer()
    {
        // 模拟真实时序：先有工具时间线（整段正文），随后最终回答以增量到达。
        var full = new StringBuilder();
        bool started = false;

        // 第一段：模型在工具调用前的中间文本（此时走增量，尚未有整段正文）。
        StreamBatchContentRules.TryMergeDelta(full, "先查一下", ref started).Should().BeFalse();
        full.Length.Should().Be(0);

        // 工具轮次：整段正文被替换为过程时间线。
        full.Append("过程时间线");
        int lastFlushed = 0;
        StreamBatchContentRules.ResetAfterFullContent(ref started, ref lastFlushed);
        lastFlushed.Should().Be(0);

        // 最终回答：必须并入整段正文，并且补一个分隔符。
        StreamBatchContentRules.TryMergeDelta(full, "最终回答", ref started).Should().BeTrue();
        full.ToString().Should().Be("过程时间线\n最终回答");
    }

    [Theory]
    // 从 0 起步：攒够 interval 才推送。
    [InlineData(99, 0, 100, false)]
    [InlineData(100, 0, 100, true)]
    // 长正文之后只增长一点点：不应每次增量都重推整段（旧的绝对长度判定会恒为 true）。
    [InlineData(1005, 1000, 100, false)]
    [InlineData(1100, 1000, 100, true)]
    public void GrewEnough_UsesGrowthSinceLastFlush(
        int currentLength, int lastFlushedLength, int interval, bool expected)
        => StreamBatchContentRules.GrewEnough(currentLength, lastFlushedLength, interval)
            .Should().Be(expected);
}
