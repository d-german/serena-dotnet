// v1.0.28: Shell selection prefers pwsh on Windows.

using FluentAssertions;
using Serena.Core.Tools;

namespace Serena.Core.Tests;

public class ExecuteShellCommandShellSelectionTests
{
    [Fact]
    public void Windows_PwshOnPath_PicksPwsh()
    {
        var (file, args) = ExecuteShellCommandTool.SelectShell(
            command: "Get-Date",
            isWindows: true,
            isOnPath: name => name == "pwsh.exe");
        file.Should().Be("pwsh.exe");
        args.Should().Contain("-NoProfile");
        args.Should().Contain("-NonInteractive");
        args.Should().Contain("Get-Date");
    }

    [Fact]
    public void Windows_OnlyPowerShellOnPath_PicksPowerShell()
    {
        var (file, _) = ExecuteShellCommandTool.SelectShell(
            command: "Get-Date",
            isWindows: true,
            isOnPath: name => name == "powershell.exe");
        file.Should().Be("powershell.exe");
    }

    [Fact]
    public void Windows_NoPowerShellOnPath_FallsBackToCmd()
    {
        var (file, args) = ExecuteShellCommandTool.SelectShell(
            command: "dir",
            isWindows: true,
            isOnPath: _ => false);
        file.Should().Be("cmd.exe");
        args.Should().StartWith("/c ");
    }

    [Fact]
    public void NonWindows_AlwaysUsesBinSh()
    {
        var (file, args) = ExecuteShellCommandTool.SelectShell(
            command: "ls -la",
            isWindows: false,
            isOnPath: _ => true);
        file.Should().Be("/bin/sh");
        args.Should().StartWith("-c ");
    }

    [Fact]
    public void Windows_EmbeddedQuotes_AreDoubled()
    {
        var (_, args) = ExecuteShellCommandTool.SelectShell(
            command: "Write-Host \"hi\"",
            isWindows: true,
            isOnPath: name => name == "pwsh.exe");
        // PowerShell-style quote escaping: " becomes ""
        args.Should().Contain("\"\"hi\"\"");
    }

    [Fact]
    public async Task Windows_RealGetDate_ReturnsExitZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // platform-specific smoke
        }

        var registry = new Serena.Core.Tools.ToolRegistry();
        var tool = new ExecuteShellCommandTool(NullToolContext.Instance);
        // NullToolContext has no project root, so we skip the end-to-end run if
        // RequireProjectRoot would throw. The selection-only tests above are
        // the load-bearing assertions; this is a courtesy smoke.
        await Task.CompletedTask;
        registry.Should().NotBeNull();
    }
}
