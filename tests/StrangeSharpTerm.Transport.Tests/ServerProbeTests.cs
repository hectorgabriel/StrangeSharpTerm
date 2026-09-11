namespace StrangeSharpTerm.Transport.Tests;

/// <summary>
/// Output captured from a real macOS machine, and realistic Linux equivalents. A
/// probe runs on whatever the server happens to be, so both are checked.
/// </summary>
internal static class Output
{
    public const string MacOS = """
        ###ST:uptime
         9:47  up 43 mins, 2 users, load averages: 2.18 2.48 3.09
        ###ST:load
        { 2.18 2.48 3.09 }
        ###ST:mem
        8589934592
        Mach Virtual Memory Statistics: (page size of 16384 bytes)
        Pages free:                                     3906.
        Pages active:                                 107661.
        Pages speculative:                              1024.
        ###ST:disk
        Filesystem     1024-blocks      Used Available Capacity iused      ifree %iused  Mounted on
        /dev/disk3s1s1   239362496  12339060 100351148    11%  458732 1003511480    0%   /
        ###ST:docker
        ###ST:end
        """;

    public const string Linux = """
        ###ST:uptime
         14:22:01 up 10 days,  4:33,  2 users,  load average: 0.52, 0.58, 0.59
        ###ST:load
        0.52 0.58 0.59 1/234 5678
        ###ST:mem
                       total        used        free      shared  buff/cache   available
        Mem:     16776642560  4294967296  8589934592    12345678  3891740672 11811160064
        Swap:     2147483648           0  2147483648
        ###ST:disk
        Filesystem     1K-blocks     Used Available Use% Mounted on
        /dev/sda1       51475068 12345678  36505390  26% /
        ###ST:docker
        web|Up 3 hours
        db|Exited (0) 2 minutes ago
        ###ST:end
        """;
}

public class ServerProbeTests
{
    [Fact]
    public void LinuxOutputIsParsed()
    {
        var metrics = ServerProbe.Parse(Output.Linux);

        metrics.Uptime.ShouldBe("10 days, 4:33");
        metrics.LoadAverages.ShouldBe(new[] { 0.52, 0.58, 0.59 });
        metrics.MemoryTotalBytes.ShouldBe(16_776_642_560UL);
        metrics.MemoryUsedBytes.ShouldBe(4_294_967_296UL);
        metrics.DiskTotalBytes.ShouldBe(51_475_068UL * 1024);
        metrics.Containers.Count.ShouldBe(2);
    }

    [Fact]
    public void MacOsOutputIsParsed()
    {
        var metrics = ServerProbe.Parse(Output.MacOS);

        metrics.Uptime.ShouldBe("43 mins");
        metrics.LoadAverages.ShouldBe(new[] { 2.18, 2.48, 3.09 });
        metrics.MemoryTotalBytes.ShouldBe(8_589_934_592UL);
        // free + speculative pages at 16 KiB, subtracted from the total.
        metrics.MemoryUsedBytes.ShouldBe(8_589_934_592UL - ((3906UL + 1024) * 16384));
        metrics.DiskTotalBytes.ShouldBe(239_362_496UL * 1024);
        metrics.Containers.ShouldBeEmpty();
    }

    [Fact]
    public void ContainerStatusDistinguishesRunningFromStopped()
    {
        var containers = ServerProbe.Parse(Output.Linux).Containers;

        containers.Single(c => c.Name == "web").IsRunning.ShouldBeTrue();
        containers.Single(c => c.Name == "db").IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void FractionsAreComputedOnlyWhenBothHalvesAreKnown()
    {
        ServerProbe.Parse(Output.Linux).DiskFraction.ShouldNotBeNull().ShouldBe(0.2398, 0.01);

        // A confident zero for something unmeasured would be worse than nothing.
        new ServerMetrics().DiskFraction.ShouldBeNull();
        new ServerMetrics().MemoryFraction.ShouldBeNull();
    }

    [Fact]
    public void AServerMissingEveryToolYieldsEmptyMetricsNotWrongOnes()
    {
        const string output = """
            ###ST:uptime
            ###ST:load
            ###ST:mem
            ###ST:disk
            ###ST:docker
            ###ST:end
            """;

        ServerProbe.Parse(output).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void GarbageDoesNotProduceConfidentNumbers()
    {
        ServerProbe.Parse("not a probe at all").IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void AMissingDockerBinaryJustMeansNoContainers()
    {
        ServerProbe.Parse(Output.MacOS).Containers.ShouldBeEmpty();
    }

    [Fact]
    public void LoadIsReadFromEitherPlatformsFormat()
    {
        ServerProbe.ParseLoad("0.52 0.58 0.59 1/234 5678").ShouldBe(new[] { 0.52, 0.58, 0.59 });
        ServerProbe.ParseLoad("{ 2.18 2.48 3.09 }").ShouldBe(new[] { 2.18, 2.48, 3.09 });
        ServerProbe.ParseLoad("nonsense").ShouldBeNull();
    }

    [Fact]
    public void TheProbeCommandGuardsEveryToolItUses()
    {
        // A server without free, docker or sysctl must still return sections
        // rather than failing the whole command.
        ServerProbe.Command.ShouldContain("2>/dev/null");
        ServerProbe.Command.ShouldContain("||");
    }

    [Fact]
    public void OutputWithWindowsLineEndingsParsesTheSame()
    {
        // The bytes come back from the server as the server wrote them, but a
        // trailing carriage return would otherwise land inside every value.
        ServerProbe.Parse(Output.Linux.Replace("\n", "\r\n")).Uptime.ShouldBe("10 days, 4:33");
    }
}
