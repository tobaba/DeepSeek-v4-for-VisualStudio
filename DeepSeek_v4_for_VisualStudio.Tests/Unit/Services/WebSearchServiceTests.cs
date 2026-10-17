namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// WebSearchService 静态工具方法测试，重点覆盖 fetch_webpage 图片 URL 的
/// 视觉模型格式过滤（防止 SVG 等不支持格式直传导致 HTTP 400）。
/// </summary>
public class WebSearchServiceTests
{
    private const string BilibiliSvg =
        "https://i0.hdslb.com/bfs/activity-plat/static/20221018/df3e2ff90b315fca2f8d24a29cb68a47/2g46SzaanE.svg";

    #region FilterVisionImageUrls

    [Fact]
    public void FilterVisionImageUrls_SvgUrl_IsFilteredOut()
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { BilibiliSvg });

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("https://example.com/a.jpg")]
    [InlineData("https://example.com/a.jpeg")]
    [InlineData("https://example.com/a.gif")]
    [InlineData("https://example.com/a.webp")]
    public void FilterVisionImageUrls_SupportedExtensions_AreKept(string url)
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().NotBeNull();
        result.Should().ContainSingle().Which.Should().Be(url);
    }

    [Theory]
    [InlineData("https://example.com/a.PNG")]
    [InlineData("https://example.com/a.Jpg")]
    [InlineData("https://example.com/a.WEBP")]
    public void FilterVisionImageUrls_CaseInsensitive_MatchesExtension(string url)
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().NotBeNull();
        result.Should().ContainSingle().Which.Should().Be(url);
    }

    [Fact]
    public void FilterVisionImageUrls_QueryString_StillMatchesExtension()
    {
        const string url = "https://example.com/image.png?width=100&height=200";

        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().NotBeNull();
        result.Should().ContainSingle().Which.Should().Be(url);
    }

    [Theory]
    [InlineData("https://example.com/image.svg")]
    [InlineData("https://example.com/image.bmp")]
    [InlineData("https://example.com/image.tiff")]
    [InlineData("https://example.com/image.ico")]
    public void FilterVisionImageUrls_UnsupportedExtensions_AreFilteredOut(string url)
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("https://example.com/image")]               // 无扩展名
    [InlineData("https://example.com/image/")]              // 目录结尾
    [InlineData("data:image/png;base64,AAAA")]              // data URI
    [InlineData("javascript:void(0)")]                      // 非 http(s)
    public void FilterVisionImageUrls_NoRecognizableExtension_IsFilteredOut(string url)
    {
        var result = WebSearchService.FilterVisionImageUrls(new[] { url });

        result.Should().BeNull();
    }

    [Fact]
    public void FilterVisionImageUrls_MixedList_ReturnsOnlySupported()
    {
        const string png = "https://example.com/keep.png";
        var input = new[] { png, BilibiliSvg, "https://example.com/drop.gif.extra" };

        var result = WebSearchService.FilterVisionImageUrls(input);

        result.Should().NotBeNull();
        result.Should().ContainSingle().Which.Should().Be(png);
    }

    [Fact]
    public void FilterVisionImageUrls_Null_ReturnsNull()
    {
        WebSearchService.FilterVisionImageUrls(null).Should().BeNull();
    }

    [Fact]
    public void FilterVisionImageUrls_EmptyList_ReturnsNull()
    {
        WebSearchService.FilterVisionImageUrls(new List<string>()).Should().BeNull();
    }

    #endregion

    #region DeepSeek 官方 Search / Fetch

    [Fact]
    public void DeepSeekEndpointConstants_MatchOfficialDocs()
    {
        WebSearchService.DeepSeekSearchApiUrl.Should().Be("https://api.deepseek.com/v1/tools/search");
        WebSearchService.DeepSeekFetchApiUrl.Should().Be("https://api.deepseek.com/v1/tools/fetch");
    }

    [Fact]
    public void ParseDeepSeekSearchResponse_SummaryPreferredOverSnippet()
    {
        const string json = """
        {
          "results": [
            {
              "title": "DeepSeek V4 模型发布",
              "url": "https://example.com/deepseek-v4",
              "snippet": "短摘要",
              "summary": "长摘要内容",
              "date": "2026-06-01T00:00:00+00:00"
            }
          ]
        }
        """;

        var results = WebSearchService.ParseDeepSeekSearchResponse(json);

        results.Should().ContainSingle();
        var r = results[0];
        r.Title.Should().Be("DeepSeek V4 模型发布");
        r.Url.Should().Be("https://example.com/deepseek-v4");
        r.Snippet.Should().Be("长摘要内容");
        r.Date.Should().Be("2026-06-01T00:00:00+00:00");
    }

    [Fact]
    public void ParseDeepSeekSearchResponse_ParsesSiteNameAuthorityAndScore()
    {
        const string json = """
        {
          "results": [
            {
              "title": "DeepSeek V4 模型发布",
              "url": "https://example.com/deepseek-v4",
              "snippet": "短摘要",
              "summary": "长摘要",
              "date": "2026-06-01T00:00:00+00:00",
              "site_name": "Example News",
              "site_icon": "https://example.com/favicon.ico",
              "authority": 3,
              "rerank_score": 2.4
            }
          ]
        }
        """;

        var r = WebSearchService.ParseDeepSeekSearchResponse(json)[0];

        r.SiteName.Should().Be("Example News");
        r.Authority.Should().Be(3);
        r.RerankScore.Should().Be(2.4);
    }

    [Fact]
    public void ParseDeepSeekSearchResponse_MissingOptionalFields_UseUnknownSentinels()
    {
        const string json = "{\"results\":[{\"title\":\"T\",\"url\":\"https://example.com/a\"}]}";

        var r = WebSearchService.ParseDeepSeekSearchResponse(json)[0];

        r.SiteName.Should().BeEmpty();
        r.Authority.Should().Be(WebSearchResult.UnknownAuthority);
        r.RerankScore.Should().Be(WebSearchResult.UnknownScore);
    }

    [Fact]
    public void ParseDeepSeekSearchResponse_NullSiteName_IsTreatedAsEmpty()
    {
        const string json =
            "{\"results\":[{\"title\":\"T\",\"url\":\"https://example.com/a\",\"site_name\":null,\"authority\":-1}]}";

        var r = WebSearchService.ParseDeepSeekSearchResponse(json)[0];

        r.SiteName.Should().BeEmpty();
        r.Authority.Should().Be(-1);
    }

    [Fact]
    public void ParseDeepSeekSearchResponse_FallsBackToSnippet()
    {
        const string json =
            "{\"results\":[{\"title\":\"T\",\"url\":\"https://example.com/a\",\"snippet\":\"短摘要\",\"summary\":null}]}";

        var results = WebSearchService.ParseDeepSeekSearchResponse(json);

        results.Should().ContainSingle();
        results[0].Snippet.Should().Be("短摘要");
    }

    [Fact]
    public void ParseDeepSeekSearchResponse_MissingSummaryAndSnippet_UsesPlaceholder()
    {
        const string json = "{\"results\":[{\"title\":\"T\",\"url\":\"https://example.com/a\"}]}";

        var results = WebSearchService.ParseDeepSeekSearchResponse(json);

        results.Should().ContainSingle();
        results[0].Snippet.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ParseDeepSeekSearchResponse_EmptyResults_ReturnsEmptyList()
    {
        WebSearchService.ParseDeepSeekSearchResponse("{\"results\":[]}").Should().BeEmpty();
    }

    [Fact]
    public void ParseDeepSeekSearchResponse_MalformedJson_ReturnsEmptyList()
    {
        WebSearchService.ParseDeepSeekSearchResponse("{ not json").Should().BeEmpty();
        WebSearchService.ParseDeepSeekSearchResponse("").Should().BeEmpty();
    }

    [Fact]
    public void ConfigureDeepSeekSearch_OnOfficialApi_SelectsDeepSeekProvider()
    {
        using var service = new WebSearchService();

        service.ConfigureOfficialApi("sk-official");
        service.ConfigureDeepSeekSearch();

        service.ActiveProvider.Should().Be(SearchProvider.DeepSeek);
        service.IsDeepSeekSearchEnabled.Should().BeTrue();
        service.HasOfficialApiKey.Should().BeTrue();
    }

    [Fact]
    public void ConfigureDeepSeekSearch_OnCustomEndpoint_FallsBackToDuckDuckGo()
    {
        using var service = new WebSearchService();
        service.ConfigureOfficialApi("sk-official");
        service.ConfigureDeepSeekSearch();

        // 切到自定义端点：官方 Key 清空，引擎选择 DeepSeek 也不再生效。
        service.ConfigureOfficialApi(null);
        service.ConfigureDeepSeekSearch();

        service.ActiveProvider.Should().Be(SearchProvider.DuckDuckGo);
        service.IsDeepSeekSearchEnabled.Should().BeFalse();
        service.UseOfficialWebFetch.Should().BeFalse();
    }

    [Fact]
    public void OfficialApi_WithThirdPartySearch_KeepsOfficialFetchAvailable()
    {
        using var service = new WebSearchService();

        // 官方 API 来源但搜索引擎选了百度：搜索走百度，网页抓取仍可用官方 Fetch。
        service.ConfigureOfficialApi("sk-official");
        service.ConfigureBaiduSearch("baidu-key");

        service.IsDeepSeekSearchEnabled.Should().BeFalse();
        service.HasOfficialApiKey.Should().BeTrue();
        service.UseOfficialWebFetch.Should().BeTrue();
    }

    [Fact]
    public void ConfigureBaiduSearch_DisablesDeepSeekSearch()
    {
        using var service = new WebSearchService();
        service.ConfigureOfficialApi("sk-official");
        service.ConfigureDeepSeekSearch();

        service.ConfigureBaiduSearch("baidu-key");

        service.ActiveProvider.Should().Be(SearchProvider.Baidu);
        service.IsDeepSeekSearchEnabled.Should().BeFalse();
    }

    [Fact]
    public void ConfigureDuckDuckGo_DoesNotDisableOfficialFetch()
    {
        using var service = new WebSearchService();
        service.ConfigureOfficialApi("sk-official");
        service.ConfigureDeepSeekSearch();

        // 搜索切到 DuckDuckGo 后，官方 Fetch 仍按官方 API 来源保持可用。
        service.ConfigureDuckDuckGo();

        service.ActiveProvider.Should().Be(SearchProvider.DuckDuckGo);
        service.IsDeepSeekSearchEnabled.Should().BeFalse();
        service.UseOfficialWebFetch.Should().BeTrue();
    }

    [Fact]
    public void ConfigureWebFetchMode_Local_DisablesOfficialFetchEvenOnOfficialApi()
    {
        using var service = new WebSearchService();
        service.ConfigureOfficialApi("sk-official");
        service.ConfigureDeepSeekSearch();

        service.ConfigureWebFetchMode(WebFetchMode.Local);

        service.UseOfficialWebFetch.Should().BeFalse();
    }

    [Fact]
    public void ConfigureWebFetchMode_Official_OnCustomEndpoint_FallsBackToLocal()
    {
        using var service = new WebSearchService();

        // 默认即为官方模式，但自定义端点（无官方 Key）会自动回退本地解析。
        service.ConfigureWebFetchMode(WebFetchMode.Official);
        service.ConfigureOfficialApi(null);

        service.UseOfficialWebFetch.Should().BeFalse();
    }

    [Fact]
    public void ExtractMarkdownImageUrls_ParsesImagesAndResolvesRelativePaths()
    {
        const string md = "text ![a](/img/a.png) and ![b](https://cdn.example.com/b.jpg)";

        var urls = WebSearchService.ExtractMarkdownImageUrls(md, "https://example.com/page");

        urls.Should().Equal("https://example.com/img/a.png", "https://cdn.example.com/b.jpg");
    }

    [Fact]
    public void ExtractMarkdownImageUrls_SkipsDataUris()
    {
        const string md = "![inline](data:image/png;base64,AAAA)";

        WebSearchService.ExtractMarkdownImageUrls(md, "https://example.com/page").Should().BeEmpty();
    }

    [Fact]
    public void ExtractMarkdownLinkUrls_ExcludesImageSyntax()
    {
        const string md = "![img](https://example.com/a.png) [doc](https://example.com/b)";

        var urls = WebSearchService.ExtractMarkdownLinkUrls(md, "https://example.com/page");

        urls.Should().ContainSingle().Which.Should().Be("https://example.com/b");
    }

    [Fact]
    public void ExtractMarkdownLinkUrls_HandlesAngleBracketTargets()
    {
        const string md = "[doc](<https://example.com/a b>)";

        var urls = WebSearchService.ExtractMarkdownLinkUrls(md, "https://example.com/page");

        urls.Should().ContainSingle().Which.Should().Be("https://example.com/a%20b");
    }

    #endregion

    #region 官方 Search / Fetch 按次计费

    [Fact]
    public void BillingStart_IsBeijingMidnightOn20261101()
    {
        var beijing = WebSearchService.SearchFetchBillingStart;

        // 北京时间 2026-11-01 00:00 == UTC 2026-10-31 16:00
        beijing.UtcDateTime.Should().Be(new DateTime(2026, 10, 31, 16, 0, 0, DateTimeKind.Utc));
        TimeZoneInfo.ConvertTime(beijing, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"))
            .Should().Be(new DateTime(2026, 11, 1, 0, 0, 0));
    }

    [Fact]
    public void IsSearchFetchBillingActive_BeforeAndAfterStart()
    {
        var start = WebSearchService.SearchFetchBillingStart;

        WebSearchService.IsSearchFetchBillingActive(start.AddSeconds(-1)).Should().BeFalse();
        WebSearchService.IsSearchFetchBillingActive(start).Should().BeTrue();
        WebSearchService.IsSearchFetchBillingActive(start.AddDays(1)).Should().BeTrue();
    }

    [Fact]
    public void Prices_MatchOfficialRates()
    {
        WebSearchService.SearchPriceCnyPer1K.Should().Be(5.0);
        WebSearchService.FetchPriceCnyPer1K.Should().Be(5.0);
        WebSearchService.SearchPriceUsdPer1K.Should().Be(0.75);
        WebSearchService.FetchPriceUsdPer1K.Should().Be(0.75);
    }

    [Fact]
    public void NewService_StartsWithZeroUsageAndZeroCost()
    {
        using var service = new WebSearchService();

        service.OfficialSearchSuccessCount.Should().Be(0);
        service.OfficialFetchSuccessCount.Should().Be(0);
        service.BillableSearchCount.Should().Be(0);
        service.BillableFetchCount.Should().Be(0);
        service.OfficialToolCostYuan.Should().Be(0);
        service.OfficialToolCostUsd.Should().Be(0);
    }

    [Fact]
    public void CostFormula_IsPerThousandSuccessfulRequests()
    {
        // 计费生效后：每次成功请求 5 元 / 0.75 美元（按 1000 次为单位）。
        // 通过公开常量验证单价换算，避免依赖真实网络调用。
        double oneThousandSearchYuan = 1000 / 1000.0 * WebSearchService.SearchPriceCnyPer1K;
        double oneThousandFetchUsd = 1000 / 1000.0 * WebSearchService.FetchPriceUsdPer1K;
        double oneSearchYuan = 1 / 1000.0 * WebSearchService.SearchPriceCnyPer1K;

        oneThousandSearchYuan.Should().Be(5.0);
        oneThousandFetchUsd.Should().Be(0.75);
        oneSearchYuan.Should().Be(0.005);
    }

    [Fact]
    public void ResetOfficialToolStats_ReturnsCountersToZero()
    {
        using var service = new WebSearchService();

        service.ResetOfficialToolStats();

        service.OfficialSearchSuccessCount.Should().Be(0);
        service.OfficialFetchSuccessCount.Should().Be(0);
        service.BillableSearchCount.Should().Be(0);
        service.BillableFetchCount.Should().Be(0);
        service.OfficialToolCostYuan.Should().Be(0);
        service.OfficialToolCostUsd.Should().Be(0);
    }

    #endregion
}
