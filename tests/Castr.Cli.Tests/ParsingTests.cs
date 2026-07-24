using System.CommandLine;
using Castr.Cli;
using Castr.Core.Trust;
using Spectre.Console.Testing;

namespace Castr.Cli.Tests;

/// <summary>System.CommandLine parsing/validation and help-surface tests for the castr command tree.</summary>
public class ParsingTests
{
    private static RootCommand Root() => CastrCli.BuildRootCommand(new TestConsole());

    [Fact]
    public void Root_ExposesSendReceiveTrust()
    {
        var names = Root().Subcommands.Select(c => c.Name).ToHashSet();
        Assert.Contains("send", names);
        Assert.Contains("receive", names);
        Assert.Contains("trust", names);
    }

    [Fact]
    public void Send_WithFile_ParsesWithoutError()
    {
        var result = Root().Parse(["send", "somefile.bin"]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Send_WithoutFileArgument_IsAParseError()
    {
        var result = Root().Parse(["send"]);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Send_DefaultGroupAndPort_MatchWireProtocolDefaults()
    {
        var result = Root().Parse(["send", "f.bin"]);
        Assert.Empty(result.Errors);
        Assert.Equal(CastrPaths.DefaultGroup, result.GetValue<System.Net.IPAddress>("--group")!.ToString());
        Assert.Equal(CastrPaths.DefaultPort, result.GetValue<int>("--port"));
    }

    [Fact]
    public void Send_InvalidGroupAddress_IsAParseError()
    {
        var result = Root().Parse(["send", "f.bin", "--group", "not-an-ip"]);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("not-an-ip"));
    }

    [Fact]
    public void Send_ValidGroupOverride_Parses()
    {
        var result = Root().Parse(["send", "f.bin", "--group", "239.192.55.77", "--port", "45123"]);
        Assert.Empty(result.Errors);
        Assert.Equal("239.192.55.77", result.GetValue<System.Net.IPAddress>("--group")!.ToString());
        Assert.Equal(45123, result.GetValue<int>("--port"));
    }

    [Theory]
    [InlineData("deny", UnknownSenderPolicy.Deny)]
    [InlineData("queue", UnknownSenderPolicy.Queue)]
    [InlineData("prompt", UnknownSenderPolicy.Prompt)]
    public void Receive_OnUnknownSender_ParsesPolicyCaseInsensitively(string value, UnknownSenderPolicy expected)
    {
        var result = Root().Parse(["receive", "--on-unknown-sender", value]);
        Assert.Empty(result.Errors);
        Assert.Equal(expected, result.GetValue<UnknownSenderPolicy>("--on-unknown-sender"));
    }

    [Fact]
    public void Receive_DefaultPolicy_IsDeny()
    {
        var result = Root().Parse(["receive"]);
        Assert.Empty(result.Errors);
        Assert.Equal(UnknownSenderPolicy.Deny, result.GetValue<UnknownSenderPolicy>("--on-unknown-sender"));
    }

    [Fact]
    public void Receive_UnknownPolicyValue_IsAParseError()
    {
        var result = Root().Parse(["receive", "--on-unknown-sender", "banana"]);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Trust_Add_RequiresPublicKeyIdArgument()
    {
        var result = Root().Parse(["trust", "add"]);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Help_ForRoot_IsNotAnError()
    {
        var result = Root().Parse(["--help"]);
        Assert.Empty(result.Errors);
    }
}
