using StrangeSharpTerm.Assist;

namespace StrangeSharpTerm.Assist.Tests;

/// <summary>
/// When a host's password may be handed to sudo.
///
/// Every refusal here is a case where the password could be read by something
/// other than sudo: a second command, a substitution, a redirected input, or
/// sudo told not to read it. Getting one of these wrong does not break a
/// command -- it writes somebody's password into a file.
/// </summary>
public class SudoTests
{
    [Theory]
    [InlineData("sudo apt-get install -y nginx", "sudo -k -S -p '' apt-get install -y nginx")]
    [InlineData("sudo systemctl restart nginx", "sudo -k -S -p '' systemctl restart nginx")]
    [InlineData("  sudo whoami", "sudo -k -S -p '' whoami")]
    [InlineData("/usr/bin/sudo whoami", "/usr/bin/sudo -k -S -p '' whoami")]
    [InlineData("sudo -u postgres psql -c 'select 1'", "sudo -k -S -p '' -u postgres psql -c 'select 1'")]
    [InlineData("sudo -iu root whoami", "sudo -k -S -p '' -iu root whoami")]
    [InlineData("sudo --user=postgres psql", "sudo -k -S -p '' --user=postgres psql")]
    [InlineData("sudo -- ls /root", "sudo -k -S -p '' -- ls /root")]
    public void AOneCommandSudoIsGivenThePasswordOnItsInput(string command, string prepared) =>
        Sudo.Prepared(command).ShouldBe(prepared);

    [Theory]
    // Not sudo at all.
    [InlineData("apt-get install -y nginx")]
    [InlineData("echo sudo")]
    [InlineData("'sudo' whoami")]
    // Two of them: the second would want it again, and a short-circuit could
    // leave a spare copy for whatever reads next.
    [InlineData("sudo apt-get update && sudo apt-get install -y nginx")]
    [InlineData("sudo sudo whoami")]
    // Anything after it on the line could read the input too.
    [InlineData("sudo apt-get update; cat")]
    [InlineData("sudo cat /etc/shadow | tee out.txt")]
    [InlineData("sudo whoami &")]
    // A substitution can run anything, and anything can read stdin.
    [InlineData("sudo $(cat /tmp/cmd)")]
    [InlineData("sudo `cat /tmp/cmd`")]
    // Input pointed somewhere else: sudo would read the file, not the password.
    [InlineData("sudo tee /etc/hosts < /tmp/hosts")]
    [InlineData("sudo tee /etc/hosts <<< 'line'")]
    // Told not to read it, or to read it and run nothing.
    [InlineData("sudo -n whoami")]
    [InlineData("sudo -S whoami")]
    [InlineData("sudo -A whoami")]
    [InlineData("sudo -k whoami")]
    [InlineData("sudo -v")]
    [InlineData("sudo -l")]
    [InlineData("sudo -e /etc/hosts")]
    [InlineData("sudo -nu root whoami")]
    [InlineData("sudo --non-interactive whoami")]
    [InlineData("sudo --stdin whoami")]
    // Options and nothing to run.
    [InlineData("sudo -i")]
    [InlineData("sudo -u root")]
    [InlineData("sudo --")]
    [InlineData("sudo")]
    [InlineData("")]
    public void AnythingElseIsNotGivenIt(string command) =>
        Sudo.Prepared(command).ShouldBeNull();

    [Fact]
    public void ALetterAfterAValueTakingOptionIsItsValueAndNotAnOption()
    {
        // getopt reads -un as -u with the value "n" -- a user called n -- not as
        // -u and then -n. So this one is given the password, and sudo reads it:
        // there is no -n here to stop it.
        Sudo.Prepared("sudo -un root whoami").ShouldBe("sudo -k -S -p '' -un root whoami");
    }

    [Fact]
    public void AnOptionValueIsNotMistakenForTheCommand()
    {
        // -u takes the next word. Read naively, "-n" after it would look like
        // part of the command and slip through -- with -n, sudo reads no
        // password and the command after it reads it instead.
        Sudo.Prepared("sudo -u postgres -n psql").ShouldBeNull();
        Sudo.Prepared("sudo -u postgres psql").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("sudo whoami", true)]
    [InlineData("apt-get update && sudo reboot", true)]
    [InlineData("LANG=C sudo whoami", true)]
    [InlineData("echo sudo", false)]
    [InlineData("grep sudo /var/log/auth.log", false)]
    [InlineData("apt-get install -y nginx", false)]
    public void SudoIsNoticedAsACommandAndNotAsAnArgument(string command, bool mentions) =>
        Sudo.Mentions(command).ShouldBe(mentions);

    [Fact]
    public void ThePasswordIsNeverPartOfTheCommand()
    {
        // The whole point of stdin: nothing here ever puts the password in the
        // command text, where ps on the server would show it.
        var prepared = Sudo.Prepared("sudo apt-get install -y nginx").ShouldNotBeNull();

        prepared.ShouldNotContain("echo");
        prepared.ShouldNotContain("<<");
        prepared.ShouldContain("-S");
    }
}
