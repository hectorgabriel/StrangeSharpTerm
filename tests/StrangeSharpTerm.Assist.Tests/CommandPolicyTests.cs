using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// The gate. These are the cases the design exists for: each one is something a
/// check that read only the head of a line would have let through.
/// </summary>
public class CommandPolicyTests
{
    [Theory]
    [InlineData("df -h")]
    [InlineData("ls -la /var/log")]
    [InlineData("uptime")]
    [InlineData("uname -srm")]
    [InlineData("free -m")]
    [InlineData("ps aux")]
    [InlineData("cat /etc/os-release")]
    [InlineData("tail -n 200 /var/log/syslog")]
    [InlineData("grep -i error /var/log/syslog")]
    [InlineData("du -xh /var --max-depth=2 | sort -rh | head -10")]
    [InlineData("journalctl --disk-usage")]
    [InlineData("systemctl status nginx")]
    [InlineData("docker ps")]
    [InlineData("find /var/log -name '*.gz'")]
    [InlineData("df -h 2>/dev/null")]
    [InlineData("ping -c 3 8.8.8.8")]
    [InlineData("/usr/bin/uptime")]
    public void ReadOnlyCommandsRun(string command) =>
        CommandPolicy.Judge(command).MayRunUnattended.ShouldBeTrue(command);

    /// <summary>The table in the Swift README, which is the specification for this.</summary>
    [Theory]
    [InlineData("ps aux | tee /tmp/x", "every stage of a pipeline is judged")]
    [InlineData("df -h; rm -rf /tmp/x", "chaining does not launder a command")]
    [InlineData("ls $(reboot)", "substitutions are refused before parsing")]
    [InlineData("/bin/rm -rf /", "a path is still the command it ends with")]
    [InlineData("bash -c 'df -h'", "an interpreter is never read-only")]
    [InlineData("sudo df -h", "elevation is a decision even when the command is harmless")]
    [InlineData("find . -delete", "a flag that turns a reader into a writer")]
    [InlineData("sed -i 's/a/b/' f", "a flag that turns a reader into a writer")]
    [InlineData("tail -f /var/log/syslog", "nothing can interrupt a command once ssh has it")]
    [InlineData("top", "nothing can interrupt a command once ssh has it")]
    [InlineData("watch df -h", "nothing can interrupt a command once ssh has it")]
    [InlineData("LANG=C rm -rf /tmp", "leading assignments are stepped over")]
    public void TheTableFromTheReadme(string command, string why) =>
        CommandPolicy.Judge(command).MayRunUnattended.ShouldBeFalse(why);

    [Theory]
    [InlineData("ls > /tmp/listing")]
    [InlineData("df -h >> /var/log/df.log")]
    [InlineData("cat <<EOF")]
    public void WritingToAFileStops(string command) =>
        CommandPolicy.Judge(command).MayRunUnattended.ShouldBeFalse();

    [Fact]
    public void WritingToDevNullIsNotWriting() =>
        CommandPolicy.Judge("systemctl status nginx 2>/dev/null").MayRunUnattended.ShouldBeTrue();

    [Fact]
    public void AnUnknownCommandStopsAndSaysSoByName()
    {
        var judgement = CommandPolicy.Judge("provision-everything --now");

        judgement.MayRunUnattended.ShouldBeFalse();
        judgement.Reason.ShouldContain("provision-everything");
        // Unknown is not the same as dangerous, and the warning should not shout.
        judgement.IsDestructive.ShouldBeFalse();
    }

    [Fact]
    public void TheDenylistOnlyMakesTheWarningLouder()
    {
        var judgement = CommandPolicy.Judge("rm -rf /var/log");

        judgement.MayRunUnattended.ShouldBeFalse();
        judgement.IsDestructive.ShouldBeTrue();
        judgement.Reason.ShouldContain("rm");
    }

    [Theory]
    [InlineData("ls `whoami`")]
    [InlineData("cat <(ls)")]
    [InlineData("echo ${HOME:-$(pwd)}")]
    public void SubstitutionsAreRefusedWhereverTheyAre(string command)
    {
        var judgement = CommandPolicy.Judge(command);

        judgement.MayRunUnattended.ShouldBeFalse();
        judgement.Reason.ShouldContain("substitution");
    }

    [Fact]
    public void EveryStageOfALongPipelineIsJudged() =>
        CommandPolicy.Judge("cat /var/log/syslog | grep error | sort | uniq -c | tee /tmp/counts")
            .MayRunUnattended.ShouldBeFalse();

    [Fact]
    public void AReadOnlyPipelineRuns() =>
        CommandPolicy.Judge("cat /var/log/syslog | grep error | sort | uniq -c | head -20")
            .MayRunUnattended.ShouldBeTrue();

    [Theory]
    [InlineData("ip route add default via 10.0.0.1")]
    [InlineData("ip link set eth0 down")]
    [InlineData("sysctl -w vm.swappiness=10")]
    [InlineData("docker logs -f web")]
    [InlineData("kubectl get pods -w")]
    [InlineData("journalctl --vacuum-size=1G")]
    [InlineData("git push")]
    [InlineData("crontab -e")]
    public void AReadingCommandAskedToWriteStops(string command) =>
        CommandPolicy.Judge(command).MayRunUnattended.ShouldBeFalse(command);

    [Theory]
    [InlineData("ip addr show")]
    [InlineData("docker logs --tail 50 web")]
    [InlineData("kubectl get pods -n kube-system")]
    [InlineData("git log --oneline -20")]
    [InlineData("crontab -l")]
    [InlineData("sysctl vm.swappiness")]
    public void TheReadingFormOfTheSameCommandRuns(string command) =>
        CommandPolicy.Judge(command).MayRunUnattended.ShouldBeTrue(command);

    [Fact]
    public void PingWithoutACountWouldNeverStop()
    {
        CommandPolicy.Judge("ping 8.8.8.8").MayRunUnattended.ShouldBeFalse();
        CommandPolicy.Judge("ping -c 4 8.8.8.8").MayRunUnattended.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingToRunIsNotSomethingToRun(string? command) =>
        CommandPolicy.Judge(command).MayRunUnattended.ShouldBeFalse();

    [Fact]
    public void AQuotedArgumentIsNotMistakenForAnAssignment() =>
        // The quoted word is an argument to echo, not a setting in front of it.
        CommandPolicy.Judge("echo 'A=B'").MayRunUnattended.ShouldBeTrue();

    [Fact]
    public void SeveralLeadingAssignmentsAreAllSteppedOver() =>
        CommandPolicy.Judge("LANG=C LC_ALL=C TZ=UTC rm -rf /tmp").MayRunUnattended.ShouldBeFalse();

    [Fact]
    public void AnAssignmentInFrontOfAReaderStillRuns() =>
        CommandPolicy.Judge("LANG=C df -h").MayRunUnattended.ShouldBeTrue();

    [Fact]
    public void ANewlineIsAStageBoundaryToo() =>
        CommandPolicy.Judge("df -h\nreboot").MayRunUnattended.ShouldBeFalse();

    [Fact]
    public void BackgroundingDoesNotLaunderACommandEither() =>
        CommandPolicy.Judge("df -h & rm -rf /tmp").MayRunUnattended.ShouldBeFalse();
}
