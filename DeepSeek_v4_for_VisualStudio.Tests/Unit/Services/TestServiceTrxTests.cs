using DeepSeek_v4_for_VisualStudio.Services;
using FluentAssertions;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// TRX 解析：保证计数、失败明细与耗时来自结构化日志而非控制台文本。
/// </summary>
public class TestServiceTrxTests
{
    private const string TrxFixture = @"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun id=""abc"" name=""run"" xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <Times creation=""2026-10-20T10:00:00.0000000+08:00"" queuing=""2026-10-20T10:00:00.1000000+08:00"" start=""2026-10-20T10:00:00.2000000+08:00"" finish=""2026-10-20T10:00:12.2000000+08:00"" duration=""00:00:12.0000000"" />
  <Results>
    <UnitTestResult executionId=""e1"" testId=""t1"" testName=""PassesOne"" outcome=""Passed"" duration=""00:00:01.0000000"" />
    <UnitTestResult executionId=""e2"" testId=""t2"" testName=""FailsOne"" outcome=""Failed"" duration=""00:00:02.0000000"">
      <Output>
        <ErrorInfo>
          <Message>Expected value to be 3, but found 4.</Message>
          <StackTrace>at Tests.FailsOne() in C:\src\Tests.cs:line 42</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
    <UnitTestResult executionId=""e3"" testId=""t3"" testName=""SkippedOne"" outcome=""NotExecuted"" duration=""00:00:00.0000000"" />
    <UnitTestResult executionId=""e4"" testId=""t4"" testName=""TimeoutOne"" outcome=""Timeout"" duration=""00:00:05.0000000"" />
  </Results>
</TestRun>";

    [Fact]
    public void ParseTrxResult_CountsOutcomesAndCapturesFailures()
    {
        string path = Path.Combine(Path.GetTempPath(), "ds-trx-" + System.Guid.NewGuid().ToString("N") + ".trx");
        File.WriteAllText(path, TrxFixture);

        try
        {
            var result = new TestRunResult();
            TestService.ParseTrxResult(path, result);

            result.Total.Should().Be(4);
            result.Passed.Should().Be(1);
            result.Failed.Should().Be(2);   // Failed + Timeout 都算失败
            result.Skipped.Should().Be(1);  // NotExecuted 算跳过
            result.Duration.Should().Be(System.TimeSpan.FromSeconds(12));

            result.FailedTests.Should().HaveCount(2);
            result.FailedTests[0].Name.Should().Be("FailsOne");
            result.FailedTests[0].Message.Should().Contain("Expected value to be 3");
            // 断言消息归一化为单行
            result.FailedTests[0].Message.Should().NotContain("\n");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseTrxResult_MessageFallsBackToStackTraceWhenMessageMissing()
    {
        const string trx = @"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <Results>
    <UnitTestResult testName=""Boom"" outcome=""Failed"">
      <Output><ErrorInfo><StackTrace>at X.Y()</StackTrace></ErrorInfo></Output>
    </UnitTestResult>
  </Results>
</TestRun>";

        string path = Path.Combine(Path.GetTempPath(), "ds-trx-" + System.Guid.NewGuid().ToString("N") + ".trx");
        File.WriteAllText(path, trx);

        try
        {
            var result = new TestRunResult();
            TestService.ParseTrxResult(path, result);

            result.Failed.Should().Be(1);
            result.FailedTests[0].Message.Should().Contain("at X.Y()");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseTrxResult_TruncatesVeryLongMessages()
    {
        string longMessage = new string('x', 2000);
        string trx = @"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <Results>
    <UnitTestResult testName=""Long"" outcome=""Failed"">
      <Output><ErrorInfo><Message>" + longMessage + @"</Message></ErrorInfo></Output>
    </UnitTestResult>
  </Results>
</TestRun>";

        string path = Path.Combine(Path.GetTempPath(), "ds-trx-" + System.Guid.NewGuid().ToString("N") + ".trx");
        File.WriteAllText(path, trx);

        try
        {
            var result = new TestRunResult();
            TestService.ParseTrxResult(path, result);

            result.FailedTests[0].Message.Length.Should().BeLessThan(500);
            result.FailedTests[0].Message.Should().EndWith("…");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
