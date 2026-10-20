using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 测试服务接口 — 为 Agent 提供"运行单元测试"能力。
    ///
    /// 与 <see cref="IBuildService"/> 的分工：本服务自带构建，跑测试时会先编译测试项目
    /// （含其依赖项目）再执行用例，因此"验证"可以一步完成，无需在计划里单列构建步骤。
    /// 需要 VS SDK 原生编译（例如修复编译错误）时仍走 build_solution。
    ///
    /// 支持两类测试工程：
    /// - .NET：dotnet test（VSTest + TRX 日志）
    /// - C++：CMake / CTest（JUnit 输出），构建目录沿用 CMakePresets.json 里已配置的 preset
    /// </summary>
    public interface ITestService
    {
        /// <summary>
        /// 在当前工作区中查找 .NET 测试项目（按 Microsoft.NET.Test.Sdk / xUnit / NUnit / MSTest 判定）。
        /// </summary>
        /// <param name="solutionPath">解决方案文件或工作区目录</param>
        /// <returns>测试项目绝对路径列表（有序）</returns>
        IReadOnlyList<string> FindTestProjects(string? solutionPath);

        /// <summary>
        /// 在当前工作区中查找 C++/CMake 测试工程目录
        /// （CMakeLists.txt 含 enable_testing / include(CTest) / add_test / gtest_discover_tests 等测试注册）。
        /// </summary>
        /// <param name="solutionPath">解决方案文件或工作区目录</param>
        /// <returns>CMake 工程根目录绝对路径列表（有序）</returns>
        IReadOnlyList<string> FindCppTestProjects(string? solutionPath);

        /// <summary>
        /// 运行测试并返回结构化结果。
        /// </summary>
        /// <param name="solutionPath">解决方案文件或工作区目录</param>
        /// <param name="projectPath">指定测试项目；为空时在工作区内自动定位（仅有一个测试项目时可用）</param>
        /// <param name="filter">测试过滤器（等价于 dotnet test --filter）</param>
        /// <param name="configuration">构建配置；为空时沿用项目默认（通常 Debug）</param>
        /// <param name="ct">取消令牌</param>
        Task<TestRunResult> RunTestsAsync(
            string? solutionPath,
            string? projectPath,
            string? filter,
            string? configuration,
            CancellationToken ct);
    }

    /// <summary>测试运行失败的分类，用于工具层映射成本地化文案。</summary>
    public enum TestRunErrorKind
    {
        None = 0,

        /// <summary>工作区内没有找到测试项目。</summary>
        NoTestProject,

        /// <summary>存在多个测试项目且未指定 project 参数。</summary>
        MultipleProjects,

        /// <summary>找不到测试运行器（dotnet 可执行文件）。</summary>
        RunnerNotFound,

        /// <summary>无法启动测试进程。</summary>
        LaunchFailed,

        /// <summary>运行超时或被用户取消。</summary>
        Timeout,

        /// <summary>测试工程构建（或 CMake 配置）失败，未能进入用例执行阶段。</summary>
        BuildFailed,

        /// <summary>进程结束但没有产出测试结果（通常是测试程序集尚未构建）。</summary>
        NoResultProduced,
    }

    /// <summary>本次实际使用的测试技术栈，用于工具层渲染与文案区分。</summary>
    public enum TestProjectKind
    {
        /// <summary>.NET 测试项目（dotnet test / VSTest）。</summary>
        DotNet = 0,

        /// <summary>C++ 测试工程（CMake / CTest）。</summary>
        Cpp = 1,
    }

    /// <summary>单个失败测试的信息。</summary>
    public class FailedTestInfo
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>断言消息或异常摘要（已截断）。</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>测试运行结果。</summary>
    public class TestRunResult
    {
        /// <summary>失败分类；None 表示测试流程正常跑完（测试本身仍可能有失败用例）。</summary>
        public TestRunErrorKind Error { get; set; } = TestRunErrorKind.None;

        /// <summary>实际使用的测试技术栈。</summary>
        public TestProjectKind Kind { get; set; } = TestProjectKind.DotNet;

        /// <summary>实际运行的测试项目（.NET 项目文件或 CMake 工程目录，绝对路径）。</summary>
        public string ProjectPath { get; set; } = string.Empty;

        /// <summary>本次运行的测试名称过滤器（CTest 为 -R 正则；.NET 为 --filter 表达式）。</summary>
        public string Filter { get; set; } = string.Empty;

        /// <summary>多个候选项目（Error == MultipleProjects 时填充）。</summary>
        public List<string> CandidateProjects { get; } = new();

        public int Total { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }

        public TimeSpan Duration { get; set; }

        /// <summary>失败用例明细（上限截断）。</summary>
        public List<FailedTestInfo> FailedTests { get; } = new();

        /// <summary>进程输出的尾部若干行，用于诊断"没有结果"等异常情况。</summary>
        public string OutputTail { get; set; } = string.Empty;

        /// <summary>所有用例是否都通过且无错误。</summary>
        public bool AllPassed => Error == TestRunErrorKind.None && Failed == 0 && Total > 0;
    }
}
