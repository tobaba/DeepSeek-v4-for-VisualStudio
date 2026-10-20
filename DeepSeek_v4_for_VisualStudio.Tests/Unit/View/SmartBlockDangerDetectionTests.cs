using DeepSeek_v4_for_VisualStudio.View;
using FluentAssertions;

namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.View;

/// <summary>
/// 智能拦截模式（SmartBlock）的危险命令识别。
/// 重点覆盖「敏感词出现在普通参数里」的误报，例如 PowerShell 的 -Format。
/// </summary>
public class SmartBlockDangerDetectionTests
{
    [Theory]
    // 回归：-Format "HH:mm" 中的 "H:" 曾被当成盘符，取时间命令被误判为格式化磁盘
    [InlineData("Write-Output (Get-Date -Format \"yyyy-MM-dd HH:mm:ss\")")]
    [InlineData("Get-Date -Format \"HH:mm\"")]
    [InlineData("Get-ChildItem | Format-Table -AutoSize")]
    [InlineData("git status")]
    [InlineData("dotnet test")]
    public void IsDangerousCommand_CommonCommands_AreNotFlagged(string command)
    {
        DeepSeekChatControl.IsDangerousCommand(command, "terminal_command")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("format C:")]
    [InlineData("format D: /q")]
    [InlineData("format /fs:NTFS D:")]
    [InlineData("Format-Volume -DriveLetter C")]
    [InlineData("diskpart clean")]
    public void IsDangerousCommand_RealDiskFormatting_IsFlagged(string command)
    {
        DeepSeekChatControl.IsDangerousCommand(command, "terminal_command")
            .Should().BeTrue();
    }
}
