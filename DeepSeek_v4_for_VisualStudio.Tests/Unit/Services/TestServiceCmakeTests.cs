using DeepSeek_v4_for_VisualStudio.Services;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// C++ 测试服务：CMake 工程识别、预设解析、构建目录选择与 JUnit / ctest 文本结果解析。
/// 这里只覆盖不依赖进程的确定性逻辑；真正的 configure/build/ctest 需要本机工具链。
/// </summary>
public class TestServiceCmakeTests
{
    private static string CreateTempDir(string prefix = "ds-cmake-")
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ───────────────────────────── 工程识别 ─────────────────────────────

    [Fact]
    public void FindCppTestProjects_RecognizesGtestDiscovery()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CMakeLists.txt"),
                "add_executable(x test.cpp)\ninclude(GoogleTest)\ngtest_discover_tests(x)\n");

            var projects = new TestService().FindCppTestProjects(dir);

            projects.Should().HaveCount(1);
            projects[0].Should().Be(Path.GetFullPath(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("enable_testing()")]
    [InlineData("include(CTest)")]
    [InlineData("add_test(NAME foo COMMAND foo)")]
    [InlineData("catch_discover_tests(x)")]
    [InlineData("doctest_discover_tests(x)")]
    [InlineData("qt_add_test(foo)")]
    public void IsCmakeTestProjectFile_DetectsCommonTestRegistrations(string marker)
    {
        string path = Path.Combine(Path.GetTempPath(), "ds-cmakelists-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "project(x)\n" + marker + "\n");

        try
        {
            TestService.IsCmakeTestProjectFile(path).Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsCmakeTestProjectFile_IgnoresNonTestProject()
    {
        string path = Path.Combine(Path.GetTempPath(), "ds-cmakelists-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "project(x)\nadd_executable(x main.cpp)\n");

        try
        {
            TestService.IsCmakeTestProjectFile(path).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FindCppTestProjects_IgnoresBuildAndDependencyDirectories()
    {
        string dir = CreateTempDir();
        try
        {
            // 依赖源码里的 CMakeLists 不应算作独立测试工程
            string deps = Path.Combine(dir, "out", "build", "x64-debug", "_deps", "googletest-src");
            Directory.CreateDirectory(deps);
            File.WriteAllText(Path.Combine(deps, "CMakeLists.txt"), "enable_testing()\n");

            // 产品工程：没有测试注册
            File.WriteAllText(Path.Combine(dir, "CMakeLists.txt"), "project(app)\nadd_executable(app main.cpp)\n");

            new TestService().FindCppTestProjects(dir).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindCppTestProjects_ReturnsOutermostProjectOnly()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CMakeLists.txt"),
                "add_subdirectory(test)\nenable_testing()\n");

            string sub = Path.Combine(dir, "test");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "CMakeLists.txt"),
                "add_executable(t t.cpp)\ngtest_discover_tests(t)\n");

            var projects = new TestService().FindCppTestProjects(dir);

            // 子目录已被根工程 add_subdirectory 引入，只保留根工程
            projects.Should().HaveCount(1);
            projects[0].Should().Be(Path.GetFullPath(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ───────────────────────────── 预设解析 ─────────────────────────────

    private const string PresetsFixture = @"{
  ""version"": 3,
  ""configurePresets"": [
    {
      ""name"": ""windows-base"",
      ""hidden"": true,
      ""generator"": ""Ninja"",
      ""binaryDir"": ""${sourceDir}/out/build/${presetName}"",
      ""cacheVariables"": { ""CMAKE_C_COMPILER"": ""cl.exe"", ""CMAKE_CXX_COMPILER"": ""cl.exe"" }
    },
    {
      ""name"": ""x64-debug"",
      ""inherits"": ""windows-base"",
      ""cacheVariables"": { ""CMAKE_BUILD_TYPE"": ""Debug"" },
      ""environment"": { ""CXXFLAGS"": ""/fsanitize=address"" }
    }
  ]
}";

    [Fact]
    public void ParseCmakeConfigurePresets_ExpandsInheritanceAndMacros()
    {
        var presets = TestService.ParseCmakeConfigurePresets(PresetsFixture, @"C:\src\database");

        presets.Should().HaveCount(2);

        var hidden = presets.First(p => p.Name == "windows-base");
        hidden.Hidden.Should().BeTrue();
        hidden.BinaryDir.Should().Be(@"C:\src\database\out\build\windows-base");

        var debug = presets.First(p => p.Name == "x64-debug");
        debug.Hidden.Should().BeFalse("hidden 不沿 inherits 传递：子 preset 未声明 hidden 即为可见");
        debug.BinaryDir.Should().Be(@"C:\src\database\out\build\x64-debug");
        debug.Generator.Should().Be("Ninja");
        debug.CacheVariables["CMAKE_CXX_COMPILER"].Should().Be("cl.exe");
        debug.CacheVariables["CMAKE_BUILD_TYPE"].Should().Be("Debug", "子 preset 的 cacheVariables 覆盖/补充父级");
    }

    [Fact]
    public void ParseCmakeConfigurePresets_HandlesInheritsArrayAndCycle()
    {
        const string json = @"{
  ""configurePresets"": [
    { ""name"": ""a"", ""inherits"": [""b""], ""binaryDir"": ""${sourceDir}/a"" },
    { ""name"": ""b"", ""inherits"": ""a"", ""generator"": ""Ninja"" }
  ]
}";

        // 循环 inherits 必须被阻断而不是栈溢出
        var presets = TestService.ParseCmakeConfigurePresets(json, @"C:\w");

        presets.Should().HaveCount(2);
        presets.First(p => p.Name == "a").BinaryDir.Should().Be(@"C:\w\a");
        presets.First(p => p.Name == "b").BinaryDir.Should().Be(@"C:\w\a", "b 没有自己的 binaryDir，继承自 a");
        presets.First(p => p.Name == "b").Generator.Should().Be("Ninja");
    }

    [Fact]
    public void ParseCmakeConfigurePresets_ReturnsEmptyWhenNoConfigurePresets()
    {
        TestService.ParseCmakeConfigurePresets(@"{ ""version"": 3 }", @"C:\w").Should().BeEmpty();
    }

    // ───────────────────────────── 构建目录选择 ─────────────────────────────

    [Fact]
    public void ResolveCmakeBuildTarget_PrefersConfiguredPreset()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CMakePresets.json"), PresetsFixture);

            // 只有 x64-debug 的构建目录已配置过
            string buildDir = Path.Combine(dir, "out", "build", "x64-debug");
            Directory.CreateDirectory(buildDir);
            File.WriteAllText(Path.Combine(buildDir, "CMakeCache.txt"), "CMAKE_GENERATOR:INTERNAL=Ninja\n");

            var target = TestService.ResolveCmakeBuildTarget(dir, null);

            target.PresetName.Should().Be("x64-debug");
            target.BuildDirectory.Should().Be(buildDir);
            target.IsConfigured.Should().BeTrue();
            // 单配置生成器不需要 -C/--config
            target.Configuration.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveCmakeBuildTarget_HonorsExplicitPresetName()
    {
        string dir = CreateTempDir();
        try
        {
            const string json = @"{
  ""configurePresets"": [
    { ""name"": ""x64-debug"", ""generator"": ""Ninja"", ""binaryDir"": ""${sourceDir}/out/build/x64-debug"" },
    { ""name"": ""x64-release"", ""generator"": ""Ninja"", ""binaryDir"": ""${sourceDir}/out/build/x64-release"" }
  ]
}";
            File.WriteAllText(Path.Combine(dir, "CMakePresets.json"), json);

            var target = TestService.ResolveCmakeBuildTarget(dir, "x64-release");

            target.PresetName.Should().Be("x64-release");
            target.BuildDirectory.Should().Be(Path.Combine(dir, "out", "build", "x64-release"));
            target.IsConfigured.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveCmakeBuildTarget_FallsBackToExistingBuildDirectory()
    {
        string dir = CreateTempDir();
        try
        {
            string buildDir = Path.Combine(dir, "build");
            Directory.CreateDirectory(buildDir);
            File.WriteAllText(Path.Combine(buildDir, "CTestTestfile.cmake"), "# generated\n");

            var target = TestService.ResolveCmakeBuildTarget(dir, null);

            target.PresetName.Should().BeNull();
            target.BuildDirectory.Should().Be(buildDir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveCmakeBuildTarget_DefaultsToBuildSubdirectory()
    {
        string dir = CreateTempDir();
        try
        {
            var target = TestService.ResolveCmakeBuildTarget(dir, null);

            target.BuildDirectory.Should().Be(Path.Combine(dir, "build"));
            target.IsConfigured.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ───────────────────────────── 工程入参归一化 ─────────────────────────────

    [Fact]
    public void IsCppProjectRequest_RecognizesCmakeInputs()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CMakeLists.txt"), "project(x)\n");
            string cmakeLists = Path.Combine(dir, "CMakeLists.txt");

            TestService.IsCppProjectRequest(cmakeLists).Should().BeTrue();
            TestService.IsCppProjectRequest(dir).Should().BeTrue();
            TestService.IsCppProjectRequest(Path.Combine(dir, "App.csproj")).Should().BeFalse();
            TestService.IsCppProjectRequest(null).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveCppProjectDirectory_MapsCmakeListsToItsDirectory()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CMakeLists.txt"), "project(x)\n");

            TestService.ResolveCppProjectDirectory(Path.Combine(dir, "CMakeLists.txt"), null)
                .Should().Be(Path.GetFullPath(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveCppProjectDirectory_ClimbsFromBuildDirectoryToProjectRoot()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CMakeLists.txt"), "project(x)\n");
            string buildDir = Path.Combine(dir, "out", "build", "x64-debug");
            Directory.CreateDirectory(buildDir);
            File.WriteAllText(Path.Combine(buildDir, "CMakeCache.txt"), "CMAKE_GENERATOR:INTERNAL=Ninja\n");

            TestService.ResolveCppProjectDirectory(buildDir, null)
                .Should().Be(Path.GetFullPath(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ───────────────────────────── MSVC 环境判断 ─────────────────────────────

    [Fact]
    public void NeedsMsvcEnvironment_DetectsClExeFromCache()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CMakeCache.txt"),
                "CMAKE_GENERATOR:INTERNAL=Ninja\nCMAKE_CXX_COMPILER:STRING=cl.exe\n");

            var target = new TestService.CmakeBuildTarget { BuildDirectory = dir };

            TestService.NeedsMsvcEnvironment(target).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NeedsMsvcEnvironment_DetectsAddressSanitizerFromCache()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CMakeCache.txt"),
                "CMAKE_CXX_FLAGS:STRING=/fsanitize=address\n");

            var target = new TestService.CmakeBuildTarget { BuildDirectory = dir };

            TestService.NeedsMsvcEnvironment(target).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NeedsMsvcEnvironment_ReturnsFalseForNonMsvcToolchain()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "CMakeCache.txt"),
                "CMAKE_CXX_COMPILER:STRING=/usr/bin/g++\n");

            var target = new TestService.CmakeBuildTarget { BuildDirectory = dir };

            TestService.NeedsMsvcEnvironment(target).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NeedsMsvcEnvironment_UsesPresetCacheVariablesWhenNotConfigured()
    {
        string dir = CreateTempDir();
        try
        {
            var target = new TestService.CmakeBuildTarget { BuildDirectory = dir };
            target.CacheVariables["CMAKE_CXX_COMPILER"] = "cl.exe";

            TestService.NeedsMsvcEnvironment(target).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParseEnvironmentBlock_SkipsPseudoVariablesAndNormalizesNames()
    {
        const string block = "=C:=C:\\work\nPath=C:\\bin;C:\\tools\nINCLUDE=C:\\inc\n\nbad name=value\n";

        var environment = TestService.ParseEnvironmentBlock(block);

        environment.Should().ContainKey("Path");
        environment["Path"].Should().Be("C:\\bin;C:\\tools");
        environment.Should().ContainKey("INCLUDE");
        environment.Should().NotContainKey("=C:");
        environment.Should().NotContainKey("bad name");
    }

    // ───────────────────────────── JUnit 解析 ─────────────────────────────

    private const string JUnitFixture = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<testsuite name=""(empty)"" tests=""4"" failures=""2"" disabled=""0"" skipped=""1"" time=""1.234"">
  <testcase name=""PassesOne"" classname=""PassesOne"" time=""0.1"" status=""run""/>
  <testcase name=""FailsOne"" classname=""FailsOne"" time=""0.2"" status=""run"">
    <failure message=""Expected: 3 but got 4"" type=""AssertionError"">stack trace here</failure>
  </testcase>
  <testcase name=""AbortedOne"" classname=""AbortedOne"" time=""0.3"" status=""run"">
    <error message=""STATUS_DLL_NOT_FOUND"" type=""RuntimeError""></error>
  </testcase>
  <testcase name=""SkippedOne"" classname=""SkippedOne"" time=""0"" status=""run"">
    <skipped/>
  </testcase>
</testsuite>";

    [Fact]
    public void ParseJUnitResult_CountsOutcomesAndCapturesFailures()
    {
        string path = Path.Combine(Path.GetTempPath(), "ds-junit-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, JUnitFixture);

        try
        {
            var result = new TestRunResult();
            TestService.ParseJUnitResult(path, result);

            result.Total.Should().Be(4);
            result.Passed.Should().Be(1);
            result.Failed.Should().Be(2);   // <failure> 与 <error> 都算失败
            result.Skipped.Should().Be(1);  // <skipped> 算跳过
            result.Duration.TotalMilliseconds.Should().BeApproximately(1234, 5);

            result.FailedTests.Should().HaveCount(2);
            result.FailedTests[0].Name.Should().Be("FailsOne");
            result.FailedTests[0].Message.Should().Contain("Expected: 3 but got 4");
            result.FailedTests[1].Message.Should().Contain("STATUS_DLL_NOT_FOUND");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseJUnitResult_StatusFailAttributeCountsAsFailure()
    {
        const string xml = @"<?xml version=""1.0""?>
<testsuite>
  <testcase name=""A"" classname=""Suite.A"" status=""fail"" time=""0.01""/>
</testsuite>";

        string path = Path.Combine(Path.GetTempPath(), "ds-junit-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, xml);

        try
        {
            var result = new TestRunResult();
            TestService.ParseJUnitResult(path, result);

            result.Failed.Should().Be(1);
            result.FailedTests[0].Name.Should().Be("Suite.A");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseJUnitResult_TruncatesVeryLongFailureText()
    {
        string longText = new string('x', 3000);
        string xml = @"<?xml version=""1.0""?>
<testsuite>
  <testcase name=""Long"" classname=""Long"" status=""run""><failure>" + longText + @"</failure></testcase>
</testsuite>";

        string path = Path.Combine(Path.GetTempPath(), "ds-junit-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, xml);

        try
        {
            var result = new TestRunResult();
            TestService.ParseJUnitResult(path, result);

            result.FailedTests[0].Message.Length.Should().BeLessThan(500);
            result.FailedTests[0].Message.Should().EndWith("…");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseJUnitResult_NestedTestSuites_CountsAllCases()
    {
        const string xml = @"<?xml version=""1.0""?>
<testsuites>
  <testsuite name=""S1""><testcase name=""a"" classname=""S1.a"" time=""0.1""/></testsuite>
  <testsuite name=""S2""><testcase name=""b"" classname=""S2.b"" time=""0.2""/></testsuite>
</testsuites>";

        string path = Path.Combine(Path.GetTempPath(), "ds-junit-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, xml);

        try
        {
            var result = new TestRunResult();
            TestService.ParseJUnitResult(path, result);

            result.Total.Should().Be(2);
            result.Passed.Should().Be(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseJUnitResult_UsesSystemOutExcerptWhenMessageIsGeneric()
    {
        // CTest 的 JUnit 会写 <failure message="Failed"/> 并把真正的断言详情放在 <system-out>；
        // 若只取 message，模型看到的失败原因就是无意义的 "Failed"。
        const string xml = @"<?xml version=""1.0""?>
<testsuite name=""(empty)"" tests=""1"" failures=""1"">
  <testcase name=""SQLParserTest.ParseStripsTrailingSemicolon"" classname=""SQLParserTest.ParseStripsTrailingSemicolon"" time=""0.05"" status=""fail"">
    <failure message=""Failed""/>
    <properties/>
    <system-out>[ RUN      ] SQLParserTest.ParseStripsTrailingSemicolon
F:\CPP\database\test\execution\sql_parser_test.cpp(304): error: Expected equality of these values:
  static_cast&lt;SelectStmt*&gt;(stmt.get())-&gt;table_name
    Which is: ""users;""
  ""users""
[  FAILED  ] SQLParserTest.ParseStripsTrailingSemicolon (1 ms)
</system-out>
  </testcase>
</testsuite>";

        string path = Path.Combine(Path.GetTempPath(), "ds-junit-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, xml);

        try
        {
            var result = new TestRunResult();
            TestService.ParseJUnitResult(path, result);

            result.Failed.Should().Be(1);
            result.FailedTests[0].Message.Should().Contain("error: Expected equality");
            result.FailedTests[0].Message.Should().Contain("Which is");
            // 仍然是单行摘要
            result.FailedTests[0].Message.Should().NotContain("\n");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseJUnitResult_KeepsInformativeMessageWhenPresent()
    {
        const string xml = @"<?xml version=""1.0""?>
<testsuite name=""S"" tests=""1"" failures=""1"">
  <testcase name=""A"" classname=""S.A"" time=""0.01"" status=""fail"">
    <failure message=""Expected 3 but got 4""/>
    <system-out>noise that should not win</system-out>
  </testcase>
</testsuite>";

        string path = Path.Combine(Path.GetTempPath(), "ds-junit-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, xml);

        try
        {
            var result = new TestRunResult();
            TestService.ParseJUnitResult(path, result);

            result.FailedTests[0].Message.Should().Contain("Expected 3 but got 4");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseJUnitResult_FallsBackToOutputTailWhenNoKnownMarker()
    {
        // 无 error:/AddressSanitizer 等关键字时（例如 gtest FATAL 或自定义框架），
        // 至少给出输出尾部，避免失败原因完全是空的。
        const string xml = @"<?xml version=""1.0""?>
<testsuite name=""(empty)"" tests=""1"" failures=""1"">
  <testcase name=""A"" classname=""A"" time=""0.01"" status=""fail"">
    <failure message=""Failed""/>
    <system-out>setup line
something went wrong here
[  FAILED  ] A (1 ms)
1 FAILED TEST
</system-out>
  </testcase>
</testsuite>";

        string path = Path.Combine(Path.GetTempPath(), "ds-junit-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, xml);

        try
        {
            var result = new TestRunResult();
            TestService.ParseJUnitResult(path, result);

            result.FailedTests[0].Message.Should().NotBeNullOrWhiteSpace();
            result.FailedTests[0].Message.Should().Contain("FAILED");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ───────────────────── ctest 文本汇总兜底解析（CMake < 3.21） ─────────────────────

    [Fact]
    public void ParseCtestTextSummary_ParsesCountsAndFailedTests()
    {
        const string output = @"Test project F:/CPP/database/out/build/x64-debug
    Start 16: TransactionManagerTest.MultiStatementTransactionLifecycle
1/3 Test #16: TransactionManagerTest.MultiStatementTransactionLifecycle ***Failed    0.17 sec
    Start 17: TransactionManagerTest.AbortReleasesLocks
2/3 Test #17: TransactionManagerTest.AbortReleasesLocks .......   Passed    0.04 sec
    Start 18: IndexTest.Insert
3/3 Test #18: IndexTest.Insert ***Failed    0.05 sec

The following tests FAILED:
	 16 - TransactionManagerTest.MultiStatementTransactionLifecycle (Failed)
	 18 - IndexTest.Insert (Subprocess aborted)
Errors while running CTest
33% tests passed, 2 tests failed out of 3";

        var result = new TestRunResult();
        TestService.ParseCtestTextSummary(output, result);

        result.Total.Should().Be(3);
        result.Failed.Should().Be(2);
        result.Passed.Should().Be(1);
        result.FailedTests.Should().HaveCount(2);
        result.FailedTests[0].Name.Should().Be("TransactionManagerTest.MultiStatementTransactionLifecycle");
        result.FailedTests[1].Name.Should().Be("IndexTest.Insert");
    }

    [Fact]
    public void ParseCtestTextSummary_AllPassed()
    {
        const string output = @"1/1 Test #152: SQLParserTest.ParseUnknownStatementReturnsNull ...   Passed    0.04 sec

100% tests passed, 0 tests failed out of 1";

        var result = new TestRunResult();
        TestService.ParseCtestTextSummary(output, result);

        result.Total.Should().Be(1);
        result.Passed.Should().Be(1);
        result.Failed.Should().Be(0);
    }

    [Fact]
    public void ParseCtestTextSummary_NoTestsFound_LeavesCountsEmpty()
    {
        var result = new TestRunResult();
        TestService.ParseCtestTextSummary("No tests were found!!!", result);

        result.Total.Should().Be(0);
    }
}
