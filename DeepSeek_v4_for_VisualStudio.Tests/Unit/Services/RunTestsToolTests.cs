using DeepSeek_v4_for_VisualStudio.Services;
using DeepSeek_v4_for_VisualStudio.Services.BuiltInTools;
using FluentAssertions;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// run_tests 工具：结果渲染与摘要。
/// </summary>
public class RunTestsToolTests
{
    [Fact]
    public void Name_IsRunTests()
    {
        new RunTestsTool().Name.Should().Be("run_tests");
    }

    /// <summary>
    /// 回归：工具实例必须携带可用的测试服务。
    /// WPF 控件曾直接 new BuiltInToolService(...) 漏传 ITestService，
    /// 导致 run_tests 在生产环境永远返回"测试服务未初始化"。
    /// 这里通过真实执行路径断言：缺少服务时返回明确错误，注入了服务时不应再报未初始化。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutService_ReportsServiceNotInit()
    {
        string result = await new RunTestsTool(null).ExecuteAsync(
            new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(),
            null);

        result.Should().Contain("run_tests");
        result.Should().Contain("Error:");
    }

    [Fact]
    public async Task ExecuteAsync_WithService_DoesNotReportServiceNotInit()
    {
        // 用独占的空目录，避免扫描到并行用例创建的临时工程文件
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ds-runtests-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);

        try
        {
            string result = await new RunTestsTool(new TestService()).ExecuteAsync(
                new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(),
                dir);

            // 空目录里没有测试项目，应返回"没有测试项目"而不是"服务未初始化"
            result.Should().NotContain("测试服务未初始化");
            result.Should().StartWith(RunTestsTool.NoTestProjectMarker);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FormatResult_AllPassed_ReportsCounts()
    {
        var result = new TestRunResult { Total = 10, Passed = 10, Duration = System.TimeSpan.FromSeconds(3.5) };

        string text = RunTestsTool.FormatResult(result);

        text.Should().Contain("total=10 passed=10 failed=0 skipped=0");
        text.Should().NotContain("失败用例");
    }

    [Fact]
    public void FormatResult_WithFailures_ListsNamesAndMessages()
    {
        var result = new TestRunResult { Total = 5, Passed = 3, Failed = 2 };
        result.FailedTests.Add(new FailedTestInfo
        {
            Name = "Alpha.Fails",
            Message = "Expected 1 but got 2.",
        });

        string text = RunTestsTool.FormatResult(result);

        text.Should().Contain("failed=2");
        text.Should().Contain("Alpha.Fails");
        text.Should().Contain("Expected 1 but got 2.");
    }

    [Fact]
    public void FormatResult_MoreFailuresThanListed_ReportsRemainder()
    {
        var result = new TestRunResult { Total = 30, Passed = 5, Failed = 25 };
        for (int i = 0; i < 20; i++)
            result.FailedTests.Add(new FailedTestInfo { Name = "T" + i, Message = "boom" });

        string text = RunTestsTool.FormatResult(result);

        text.Should().Contain("5");
    }

    [Fact]
    public void FormatResult_NoTestProject_ReturnsLocalizedError()
    {
        var result = new TestRunResult { Error = TestRunErrorKind.NoTestProject };

        RunTestsTool.FormatResult(result).Should().Contain("run_tests");
    }

    [Fact]
    public void FormatResult_MultipleProjects_ListsCandidates()
    {
        var result = new TestRunResult { Error = TestRunErrorKind.MultipleProjects };
        result.CandidateProjects.Add(@"C:\repo\A.Tests.csproj");
        result.CandidateProjects.Add(@"C:\repo\B.Tests.csproj");

        string text = RunTestsTool.FormatResult(result);

        text.Should().Contain("A.Tests.csproj");
        text.Should().Contain("B.Tests.csproj");
    }

    [Fact]
    public void FormatResult_NoResultProduced_IncludesOutputTail()
    {
        var result = new TestRunResult
        {
            Error = TestRunErrorKind.NoResultProduced,
            OutputTail = "MSB1003: Specify a project or solution file.",
        };

        string text = RunTestsTool.FormatResult(result);

        text.Should().Contain("MSB1003");
    }

    [Theory]
    [InlineData("total=4 passed=4 failed=0 skipped=0 elapsed=1.0s", "全部通过")]
    [InlineData("total=4 passed=3 failed=1 skipped=0 elapsed=1.0s", "1")]
    public void GetResultSummary_ReflectsPassFailStates(string toolResult, string expectedFragment)
    {
        new RunTestsTool().GetResultSummary(toolResult).Should().Contain(expectedFragment);
    }

    [Fact]
    public void GetResultSummary_ErrorPrefix_PassesThrough()
    {
        new RunTestsTool().GetResultSummary("Error: run_tests: no test project")
            .Should().Be("Error: run_tests: no test project");
    }

    [Fact]
    public void GetResultSummary_UnrecognizedOutput_ReturnsGenericComplete()
    {
        new RunTestsTool().GetResultSummary("something unexpected")
            .Should().NotBeNullOrEmpty();
    }
}
