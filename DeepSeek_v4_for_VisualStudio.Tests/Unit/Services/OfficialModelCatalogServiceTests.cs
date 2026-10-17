using System.Net;
using System.Net.Http;
using System.Text;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

[Collection("OfficialModelCatalogService")]
public class OfficialModelCatalogServiceTests
{
    [Fact]
    public async Task RefreshAsync_EmptyApiKey_DoesNotRequestModels()
    {
        OfficialModelCatalogService.ResetForTests();
        var handler = new RoutingHandler();

        var models = await OfficialModelCatalogService.RefreshAsync("", default, handler);

        models.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
        OfficialModelCatalogService.ResetForTests();
    }

    [Fact]
    public async Task RefreshAsync_RemoteModels_CachedUntilKeyOrLifetimeChanges()
    {
        OfficialModelCatalogService.ResetForTests();
        var handler = new RoutingHandler();
        handler.Route["https://api.deepseek.com/v1/models"] = (HttpStatusCode.NotFound, "<html>404</html>");
        handler.Route["https://api.deepseek.com/models"] = (
            HttpStatusCode.OK,
            """{"data":[{"id":"model-b"},{"id":"model-a"}]}""");

        var first = await OfficialModelCatalogService.RefreshAsync("sk-test", default, handler);
        var second = await OfficialModelCatalogService.RefreshAsync("sk-test", default, handler);

        first.Should().Equal("model-a", "model-b");
        second.Should().Equal("model-a", "model-b");
        handler.Requests.Should().HaveCount(2);
        OfficialModelCatalogService.HasRemoteModels.Should().BeTrue();
        OfficialModelCatalogService.ResetForTests();
    }

    [Fact]
    public async Task RefreshAsync_RemoteFailure_DoesNotUseBuiltInFallback()
    {
        OfficialModelCatalogService.ResetForTests();
        var handler = new RoutingHandler();
        handler.Route["https://api.deepseek.com/v1/models"] = (
            HttpStatusCode.InternalServerError, """{"error":"boom"}""");

        var models = await OfficialModelCatalogService.RefreshAsync("sk-test", default, handler);

        models.Should().BeEmpty();
        OfficialModelCatalogService.HasRemoteModels.Should().BeFalse();
        OfficialModelCatalogService.ResetForTests();
    }

    [Fact]
    public async Task RefreshAsync_ExposesDeclaredMetadata()
    {
        OfficialModelCatalogService.ResetForTests();
        var handler = new RoutingHandler();
        handler.Route["https://api.deepseek.com/v1/models"] = (
            HttpStatusCode.OK,
            """
            {"data":[
              {"id":"deepseek-flash","name":"DeepSeek-V4.1-Flash","context_window":1048576,
               "max_output_tokens":393216,"input_modalities":["text","image"],
               "effort":{"supported_levels":["low","high","max"],"default_level":"high"}},
              {"id":"deepseek-v4-pro","input_modalities":["text"],"context_window":65536}
            ]}
            """);

        await OfficialModelCatalogService.RefreshAsync("sk-test", default, handler);

        var flash = OfficialModelCatalogService.TryGetMetadata("deepseek-flash");
        flash.Should().NotBeNull();
        flash!.Display.Should().Be("DeepSeek-V4.1-Flash");
        flash.ContextWindow.Should().Be(1_048_576);
        flash.MaxOutputTokens.Should().Be(393_216);
        flash.SupportsImageInput.Should().BeTrue();
        OfficialModelCatalogService.GetEffortLevels("deepseek-flash")
            .Should().Equal("low", "high", "max");

        var pro = OfficialModelCatalogService.TryGetMetadata("deepseek-v4-pro");
        pro.Should().NotBeNull();
        pro!.ContextWindow.Should().Be(65_536);
        pro.SupportsImageInput.Should().BeFalse();
        pro.HasEffortInfo.Should().BeFalse();
        OfficialModelCatalogService.GetEffortLevels("deepseek-v4-pro").Should().BeEmpty();

        OfficialModelCatalogService.ResetForTests();
    }

    [Fact]
    public void TryGetMetadata_UnknownModel_ReturnsNull()
    {
        OfficialModelCatalogService.ResetForTests();

        OfficialModelCatalogService.TryGetMetadata("not-in-catalog").Should().BeNull();
        OfficialModelCatalogService.GetEffortLevels("not-in-catalog").Should().BeEmpty();
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Route { get; } = new();
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (Route.TryGetValue(url, out var entry))
            {
                return Task.FromResult(new HttpResponseMessage(entry.Status)
                {
                    Content = new StringContent(entry.Body, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
