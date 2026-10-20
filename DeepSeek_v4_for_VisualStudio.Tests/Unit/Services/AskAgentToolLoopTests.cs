using DeepSeek_v4_for_VisualStudio.Services.Agents;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// 回归测试：空白名单工具循环必须发送 tool_choice=none，避免模型误调用工具。
/// </summary>
public class AskAgentToolLoopTests
{
    [Fact]
    public async Task ToolLoop_WithEmptyWhitelist_SendsToolChoiceNone()
    {
        var sseLines = new[]
        {
            "data: {\"id\":\"chatcmpl-guard-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"summary text\"}}]}\n",
            "data: [DONE]\n",
        };

        var handler = new CapturingHttpMessageHandler(sseLines);
        var apiService = new DeepSeekApiService(new HttpClient(handler));
        var agent = new EmptyWhitelistToolLoopAgent(apiService)
        {
            BuiltInTools = new BuiltInToolService(),
        };

        var result = await agent.RunLoopAsync(
            new List<ChatApiMessage> { new() { Role = "user", Content = "总结" } },
            CancellationToken.None);

        result.Should().Contain("summary text");
        handler.RequestBodies.Should().ContainSingle()
            .Which.Should().Contain("\"tool_choice\":\"none\"");
    }

    /// <summary>
    /// 暴露 CallAiWithToolLoopAsync 以验证空白名单兜底行为。
    /// </summary>
    private sealed class EmptyWhitelistToolLoopAgent : BaseAgent
    {
        public EmptyWhitelistToolLoopAgent(DeepSeekApiService apiService) : base(apiService, AgentType.Ask)
        {
        }

        public Task<string> RunLoopAsync(List<ChatApiMessage> messages, CancellationToken ct)
            => CallAiWithToolLoopAsync(messages, null, ct, toolWhitelist: new List<string>());

        protected override AgentDefinition CreateDefinition(AgentType agentType)
        {
            return new AgentDefinition
            {
                Type = AgentType.Ask,
                Name = "Ask",
                AllowedTools = new List<string>(AskAgent.AskTools),
                SystemPrompt = "test",
            };
        }

        public override Task<AgentResult> ExecuteAsync(string userMessage, AgentContext context)
        {
            return Task.FromResult(new AgentResult { Content = userMessage });
        }
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly string[] _sseLines;

        public List<string> RequestBodies { get; } = new();

        public CapturingHttpMessageHandler(string[] sseLines)
        {
            _sseLines = sseLines;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync();
            RequestBodies.Add(body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Join("", _sseLines), Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
