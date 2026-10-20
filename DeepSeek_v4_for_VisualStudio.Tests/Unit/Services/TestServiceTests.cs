using DeepSeek_v4_for_VisualStudio.Services;
using FluentAssertions;
using System.IO;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Services;

/// <summary>
/// .NET 测试服务：测试项目识别与 TRX 解析。
/// 运行测试本身需要 dotnet 环境，这里只覆盖不依赖进程的确定性逻辑。
/// </summary>
public class TestServiceTests
{
    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ds-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FindTestProjects_RecognizesTestSdkAndFrameworkReferences()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.12.0\" />" +
                "</ItemGroup></Project>");

            File.WriteAllText(Path.Combine(dir, "B.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<IsTestProject>true</IsTestProject>" +
                "</PropertyGroup></Project>");

            File.WriteAllText(Path.Combine(dir, "NUnit.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<PackageReference Include=\"NUnit3TestAdapter\" Version=\"5.0.0\" />" +
                "</ItemGroup></Project>");

            var service = new TestService();
            var projects = service.FindTestProjects(dir);

            projects.Should().HaveCount(3);
            projects.Should().Contain(p => Path.GetFileName(p) == "A.csproj");
            projects.Should().Contain(p => Path.GetFileName(p) == "B.csproj");
            projects.Should().Contain(p => Path.GetFileName(p) == "NUnit.csproj");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindTestProjects_IgnoresProductProjectsAndBuildOutput()
    {
        string dir = CreateTempDir();
        try
        {
            // 产品项目：不含任何测试标记
            File.WriteAllText(Path.Combine(dir, "Product.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net472</TargetFramework>" +
                "</PropertyGroup></Project>");

            // obj/ 下的中间项目文件不能算作独立测试项目
            string objDir = Path.Combine(dir, "obj");
            Directory.CreateDirectory(objDir);
            File.WriteAllText(Path.Combine(objDir, "Temp.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.12.0\" />" +
                "</ItemGroup></Project>");

            var service = new TestService();

            service.FindTestProjects(dir).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindTestProjects_AcceptsSolutionFilePath()
    {
        string dir = CreateTempDir();
        try
        {
            string sln = Path.Combine(dir, "App.sln");
            File.WriteAllText(sln, "Microsoft Visual Studio Solution File");
            File.WriteAllText(Path.Combine(dir, "Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<PackageReference Include=\"xunit\" Version=\"2.9.3\" />" +
                "</ItemGroup></Project>");

            var service = new TestService();

            // 传 .sln 文件时应落到其所在目录扫描
            service.FindTestProjects(sln).Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunTestsAsync_NoTestProject_ReportsNoTestProject()
    {
        string dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "Product.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

            var service = new TestService();
            var result = await service.RunTestsAsync(
                dir, null, null, null, System.Threading.CancellationToken.None);

            result.Error.Should().Be(TestRunErrorKind.NoTestProject);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunTestsAsync_MultipleTestProjects_ReportsCandidates()
    {
        string dir = CreateTempDir();
        try
        {
            foreach (string name in new[] { "T1.csproj", "T2.csproj" })
            {
                File.WriteAllText(Path.Combine(dir, name),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                    "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.12.0\" />" +
                    "</ItemGroup></Project>");
            }

            var service = new TestService();
            var result = await service.RunTestsAsync(
                dir, null, null, null, System.Threading.CancellationToken.None);

            result.Error.Should().Be(TestRunErrorKind.MultipleProjects);
            result.CandidateProjects.Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
