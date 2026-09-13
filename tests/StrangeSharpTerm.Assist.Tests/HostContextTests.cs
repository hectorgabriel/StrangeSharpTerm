using System.Reflection;
using StrangeSharpTerm.Assist;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.Assist.Tests;

public class HostContextTests
{
    /// <summary>
    /// The boundary is structural rather than a habit of the call site: there is
    /// nowhere in this type to put a hostname, an address, a username or a key,
    /// so no call site can put one there by mistake.
    /// </summary>
    [Fact]
    public void ThereIsNowhereToPutAnythingElse()
    {
        string[] forbidden =
        [
            "hostname", "host", "address", "ip", "username", "user", "login",
            "port", "password", "passphrase", "key", "identity", "credential", "secret",
        ];

        var properties = typeof(HostContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        // Alias is the name the user chose, which is theirs to send.
        properties.ShouldBe(["Alias", "Kernel", "Metrics", "TerminalTail", "Redactions"], ignoreOrder: true);

        foreach (var property in properties)
        {
            forbidden.ShouldNotContain(property.ToLowerInvariant(), $"{property} would leave the machine");
        }
    }

    /// <summary>
    /// <c>uname -a</c> prints the machine's own hostname, which is the one thing
    /// this is careful not to send.
    /// </summary>
    [Fact]
    public void TheKernelCommandDoesNotAskForTheHostname()
    {
        HostContext.KernelCommand.ShouldNotContain("-a");
        HostContext.KernelCommand.ShouldBe("uname -srm");
    }

    [Fact]
    public void TheBlockNamesTheAliasAndNothingElse()
    {
        var context = new HostContext { Alias = "web-01", Kernel = "Linux 5.15.0-92-generic x86_64" };

        var block = context.Render();

        block.ShouldContain("Host: web-01");
        block.ShouldContain("Linux 5.15.0-92-generic");
    }

    [Fact]
    public void MetricsReadAsSentences()
    {
        var context = new HostContext
        {
            Alias = "web-01",
            Metrics = new ServerMetrics
            {
                Uptime = "10 days, 4:33",
                LoadAverages = [1.85, 1.98, 2.11],
                MemoryUsedBytes = 6_442_450_944,
                MemoryTotalBytes = 8_589_934_592,
                DiskUsedBytes = 49_392_123_904,
                DiskTotalBytes = 52_613_349_376,
                Containers = [new ServerMetrics.Container("web", "Up 3 hours")],
            },
        };

        var block = context.Render();

        block.ShouldContain("up 10 days, 4:33");
        block.ShouldContain("load average 1.85, 1.98, 2.11");
        block.ShouldContain("memory 6G of 8G used (75%)");
        block.ShouldContain("root filesystem");
        block.ShouldContain("container web: Up 3 hours");
    }

    [Fact]
    public void WhatTheServerDidNotReportIsNotInvented()
    {
        var context = new HostContext
        {
            Alias = "web-01",
            Metrics = new ServerMetrics { Uptime = "3 hours" },
        };

        var block = context.Render();

        block.ShouldContain("up 3 hours");
        block.ShouldNotContain("load average");
        block.ShouldNotContain("memory");
        block.ShouldNotContain("0%");
    }

    [Fact]
    public void AnEmptyProbeAddsNothing()
    {
        var block = new HostContext { Alias = "web-01", Metrics = new ServerMetrics() }.Render();

        block.ShouldBe("Host: web-01");
    }

    [Fact]
    public void TheTerminalTailIsFenced()
    {
        var context = new HostContext { Alias = "web-01", TerminalTail = "deploy@web-01:~$ df -h" };

        context.Render().ShouldContain("```\ndeploy@web-01:~$ df -h\n```");
    }

    /// <summary>
    /// The block is the same string on either operating system.
    ///
    /// It describes a remote host and is read by a provider, and neither has an
    /// opinion about the machine the window is running on. Built with
    /// <c>AppendLine</c> it carried CRLF on Windows, which CI caught and macOS
    /// never would have.
    /// </summary>
    [Fact]
    public void TheBlockIsTheSameOnEitherOperatingSystem()
    {
        var context = new HostContext
        {
            Alias = "web-01",
            Kernel = "Linux 6.1.0 x86_64",
            Metrics = new ServerMetrics { Uptime = "3 days" },
            // Whatever the far end sent, one ending leaves here.
            TerminalTail = "deploy@web-01:~$ df -h\r\n/dev/sda1 98% /",
        };

        var block = context.Render();

        block.ShouldNotContain("\r");
        block.ShouldContain("```\ndeploy@web-01:~$ df -h\n/dev/sda1 98% /\n```");
    }

    [Theory]
    [InlineData(0UL, "0B")]
    [InlineData(512UL, "512B")]
    [InlineData(1024UL, "1K")]
    [InlineData(1_572_864UL, "1.5M")]
    [InlineData(8_589_934_592UL, "8G")]
    public void SizesReadTheWayDfPrintsThem(ulong bytes, string expected) =>
        HostContext.Size(bytes).ShouldBe(expected);
}
