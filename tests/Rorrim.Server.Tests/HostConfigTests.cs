using Rorrim.Server;

namespace Rorrim.Server.Tests;

public class CommandLineArgsTests
{
    [Fact]
    public void GetString_SupportsSpaceAndEqualsForms()
    {
        var args = new[] { "--port", "1234", "--agent=C:\\tools\\Rorrim.Agent.exe", "--test-mode" };

        Assert.Equal("1234", CommandLineArgs.GetString(args, "--port"));
        Assert.Equal("C:\\tools\\Rorrim.Agent.exe", CommandLineArgs.GetString(args, "--agent"));
        Assert.Null(CommandLineArgs.GetString(args, "--missing"));
    }

    [Fact]
    public void GetInt_FallsBackToDefault()
    {
        var args = new[] { "--port=50051" };

        Assert.Equal(50051, CommandLineArgs.GetInt(args, "--port", 1));
        Assert.Equal(7, CommandLineArgs.GetInt(args, "--missing", 7));
    }

    [Fact]
    public void Has_DetectsFlags()
    {
        var args = new[] { "--test-mode", "--port", "1" };

        Assert.True(CommandLineArgs.Has(args, "--test-mode"));
        Assert.True(CommandLineArgs.Has(args, "--port"));
        Assert.False(CommandLineArgs.Has(args, "--other"));
    }

    [Fact]
    public void GetString_SpaceForm_ValueBearingFlags()
    {
        var args = new[] { "--log", "C:\\logs\\broker.log" };
        Assert.Equal("C:\\logs\\broker.log", CommandLineArgs.GetString(args, "--log"));
    }
}

public class ServiceInstallerTests
{
    [Fact]
    public void BuildCommandLine_QuotesPathsContainingSpaces()
    {
        string result = ServiceInstaller.BuildCommandLine(new[]
        {
            "C:\\Program Files\\Rorrim\\Rorrim.Server.exe",
            "--agent",
            "C:\\Program Files\\Rorrim\\Rorrim.Agent.exe"
        });

        Assert.Equal(
            "\"C:\\Program Files\\Rorrim\\Rorrim.Server.exe\" --agent \"C:\\Program Files\\Rorrim\\Rorrim.Agent.exe\"",
            result);
    }

    [Fact]
    public void BuildCommandLine_LeavesSimplePathsUnquoted()
    {
        string result = ServiceInstaller.BuildCommandLine(new[] { "C:\\Tools\\Rorrim.Server.exe", "--test-mode" });
        Assert.Equal("C:\\Tools\\Rorrim.Server.exe --test-mode", result);
    }
}
