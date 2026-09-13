namespace StrangeSharpTerm.Assist;

/// <summary>
/// What the policy decided about one command.
/// </summary>
/// <param name="MayRunUnattended">
/// True only when the command is recognised and every one of its arguments is
/// recognised as harmless.
/// </param>
/// <param name="Reason">
/// Why a person is being asked, in words fit to put in front of one. Empty when
/// the command was allowed.
/// </param>
/// <param name="IsDestructive">
/// The denylist recognised it. This grants nothing -- it only makes the warning
/// louder -- so being incomplete costs nothing.
/// </param>
public sealed record CommandJudgement(bool MayRunUnattended, string Reason = "", bool IsDestructive = false)
{
    public static CommandJudgement Allowed { get; } = new(true);

    internal static CommandJudgement Ask(string reason) => new(false, reason);

    internal static CommandJudgement Refuse(string reason) => new(false, reason, IsDestructive: true);
}

/// <summary>
/// Whether a command may run without a person watching.
///
/// It is an <em>allowlist</em>. A command runs unattended only if it is
/// recognised and every one of its arguments is recognised as harmless;
/// everything else stops and waits for someone -- not because it is known to be
/// dangerous, but because it is not known to be safe. The denylist beside it
/// never grants anything, so a gap in it costs nothing.
///
/// That direction is the whole design. A read-only command wrongly stopped costs
/// a click; a writing command wrongly allowed costs a server.
/// </summary>
public static class CommandPolicy
{
    /// <summary>Judges a whole command line, every stage of it.</summary>
    public static CommandJudgement Judge(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return CommandJudgement.Ask("There is nothing to run.");

        // Before parsing, not after: a substitution can produce any command at
        // all, so there is nothing here worth reading.
        if (ShellWords.HasSubstitution(command))
            return CommandJudgement.Ask("It contains a substitution, which could run anything.");

        var stages = ShellWords.Stages(command);
        if (stages.Count == 0)
            return CommandJudgement.Ask("There is nothing to run.");

        foreach (var stage in stages)
        {
            var judgement = JudgeStage(stage);
            if (!judgement.MayRunUnattended)
                return judgement;
        }
        return CommandJudgement.Allowed;
    }

    private static CommandJudgement JudgeStage(IReadOnlyList<ShellToken> stage)
    {
        if (Redirects(stage) is { } redirection)
            return redirection;

        var words = stage.Where(token => token.Kind == ShellTokenKind.Word).ToArray();
        // Leading assignments are stepped over rather than mistaken for the
        // command: LANG=C rm -rf /tmp is rm, not LANG=C.
        var start = 0;
        while (start < words.Length && IsAssignment(words[start]))
            start++;

        if (start >= words.Length)
            return CommandJudgement.Ask("There is nothing to run.");

        // A path is still the command it ends with, so /bin/rm is rm.
        var name = Basename(words[start].Text);
        var arguments = words[(start + 1)..].Select(token => token.Text).ToArray();

        if (Elevation.Contains(name))
            return CommandJudgement.Ask($"{name} runs it as another user, which is a decision even when the command is harmless.");

        if (Interpreters.Contains(name))
            return CommandJudgement.Ask($"{name} is an interpreter, and is never read-only whatever it is handed.");

        if (NeverReturns.TryGetValue(name, out var howItHangs))
            return CommandJudgement.Ask($"{howItHangs} Nothing can interrupt a command once ssh has it.");

        if (Destructive.TryGetValue(name, out var whatItDoes))
            return CommandJudgement.Refuse(whatItDoes);

        if (!ReadOnly.TryGetValue(name, out var rule))
            return CommandJudgement.Ask($"{name} is not one of the commands known to be read-only.");

        return rule.Judge(name, arguments);
    }

    /// <summary>
    /// Anything written to a file stops, and <c>/dev/null</c> is the exception
    /// because writing there changes nothing. Without it every
    /// <c>2&gt;/dev/null</c> in an otherwise harmless command would stop, and
    /// that idiom is in most of them.
    /// </summary>
    private static CommandJudgement? Redirects(IReadOnlyList<ShellToken> stage)
    {
        for (var i = 0; i < stage.Count; i++)
        {
            if (stage[i].Kind != ShellTokenKind.Redirection)
                continue;

            var operatorText = stage[i].Text;
            if (operatorText == "<<")
                return CommandJudgement.Ask("It uses a here-document, whose contents cannot be judged.");
            if (operatorText == "<")
                continue;

            var target = i + 1 < stage.Count ? stage[i + 1].Text : "";
            if (target != "/dev/null")
                return CommandJudgement.Ask($"It writes to {(target.Length == 0 ? "a file" : target)}.");
        }
        return null;
    }

    /// <summary><c>NAME=value</c> in front of a command, which is a setting rather than a command.</summary>
    private static bool IsAssignment(ShellToken token)
    {
        if (token.WasQuoted)
            return false;
        var equals = token.Text.IndexOf('=');
        return equals > 0
            && token.Text[..equals].All(c => char.IsLetterOrDigit(c) || c == '_')
            && char.IsLetter(token.Text[0]);
    }

    internal static string Basename(string command)
    {
        var cut = command.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? command : command[(cut + 1)..];
    }

    /// <summary>
    /// What a read-only command is allowed to be handed.
    /// </summary>
    /// <param name="Subcommands">
    /// When set, the first argument that is not a flag must be one of these.
    /// <c>docker ps</c> reads and <c>docker rm</c> does not, and they are the
    /// same command.
    /// </param>
    /// <param name="Forbidden">
    /// Arguments that turn a reader into a writer, or into something that never
    /// returns. Matched against every argument, and a flag that begins with one
    /// counts -- <c>sed -i.bak</c> is <c>sed -i</c>.
    /// </param>
    /// <param name="Required">At least one of these must be there. <c>ping</c> without <c>-c</c> never stops.</param>
    private sealed record Rule(
        string[]? Subcommands = null,
        string[]? Forbidden = null,
        string[]? Required = null)
    {
        internal CommandJudgement Judge(string name, string[] arguments)
        {
            if (Forbidden is { } forbidden
                && arguments.FirstOrDefault(argument => forbidden.Any(bad => Matches(argument, bad))) is { } offending)
            {
                return CommandJudgement.Ask($"{name} {offending} does more than read.");
            }

            if (Required is { } required && !arguments.Any(required.Contains))
                return CommandJudgement.Ask($"{name} without {string.Join(" or ", required)} would not stop on its own.");

            if (Subcommands is { } subcommands)
            {
                var first = arguments.FirstOrDefault(argument => !argument.StartsWith('-'));
                if (first is null)
                    return CommandJudgement.Ask($"{name} needs one of its reading subcommands: {string.Join(", ", subcommands)}.");
                if (!subcommands.Contains(first))
                    return CommandJudgement.Ask($"{name} {first} is not one of its reading subcommands.");
            }

            return CommandJudgement.Allowed;
        }

        /// <summary>
        /// Whether an argument is one of the forbidden ones.
        ///
        /// A flag carries its value in two shapes and both count:
        /// <c>-i.bak</c> is <c>-i</c>, and <c>--vacuum-size=1G</c> is
        /// <c>--vacuum-size</c>.
        /// </summary>
        private static bool Matches(string argument, string forbidden)
        {
            if (argument == forbidden)
                return true;
            if (!forbidden.StartsWith('-'))
                return false;
            return forbidden.StartsWith("--", StringComparison.Ordinal)
                ? argument.StartsWith(forbidden + "=", StringComparison.Ordinal)
                : argument.StartsWith(forbidden, StringComparison.Ordinal);
        }
    }

    private static readonly HashSet<string> Elevation =
        new(["sudo", "doas", "su", "runuser", "pkexec"], StringComparer.Ordinal);

    /// <summary>
    /// An interpreter is never read-only, whatever it is handed: judging
    /// <c>bash -c '…'</c> would mean judging the string inside it, and the
    /// string inside that.
    /// </summary>
    private static readonly HashSet<string> Interpreters = new(
        [
            "sh", "bash", "zsh", "ksh", "csh", "tcsh", "dash", "fish", "ash",
            "python", "python2", "python3", "perl", "ruby", "node", "php", "lua",
            "awk", "gawk", "mawk", "env", "eval", "exec", "xargs", "nohup", "setsid",
            "ssh", "scp", "sftp", "rsync", "screen", "tmux", "at", "batch",
        ],
        StringComparer.Ordinal);

    private static readonly Dictionary<string, string> NeverReturns = new(StringComparer.Ordinal)
    {
        ["top"] = "top runs until it is quit.",
        ["htop"] = "htop runs until it is quit.",
        ["watch"] = "watch repeats until it is stopped.",
        ["less"] = "less waits for a keypress.",
        ["more"] = "more waits for a keypress.",
        ["man"] = "man opens a pager and waits.",
        ["vi"] = "vi opens an editor and waits.",
        ["vim"] = "vim opens an editor and waits.",
        ["nano"] = "nano opens an editor and waits.",
        ["emacs"] = "emacs opens an editor and waits.",
        ["tailf"] = "tailf follows a file and never ends.",
        ["iotop"] = "iotop runs until it is quit.",
        ["tcpdump"] = "tcpdump captures until it is stopped.",
    };

    /// <summary>
    /// The denylist. It grants nothing; a command here would have been stopped
    /// anyway for not being on the allowlist. What it adds is a sentence saying
    /// what the thing actually does, so the person approving it is reading about
    /// this command rather than about an unrecognised one.
    /// </summary>
    private static readonly Dictionary<string, string> Destructive = new(StringComparer.Ordinal)
    {
        ["rm"] = "rm deletes files, and there is no undo on a server.",
        ["rmdir"] = "rmdir removes a directory.",
        ["mv"] = "mv moves or overwrites files.",
        ["cp"] = "cp overwrites whatever is at the destination.",
        ["dd"] = "dd writes raw blocks, and a wrong target is a wiped disk.",
        ["mkfs"] = "mkfs formats a filesystem.",
        ["fdisk"] = "fdisk edits the partition table.",
        ["parted"] = "parted edits the partition table.",
        ["shred"] = "shred destroys a file beyond recovery.",
        ["wipefs"] = "wipefs erases filesystem signatures.",
        ["truncate"] = "truncate empties or resizes a file.",
        ["chmod"] = "chmod changes permissions.",
        ["chown"] = "chown changes ownership.",
        ["chgrp"] = "chgrp changes group ownership.",
        ["kill"] = "kill stops a running process.",
        ["killall"] = "killall stops every process with that name.",
        ["pkill"] = "pkill stops every process matching the pattern.",
        ["shutdown"] = "shutdown takes the server down.",
        ["reboot"] = "reboot restarts the server.",
        ["halt"] = "halt takes the server down.",
        ["poweroff"] = "poweroff takes the server down.",
        ["init"] = "init changes the runlevel.",
        ["iptables"] = "iptables changes the firewall, which can lock everyone out.",
        ["nft"] = "nft changes the firewall, which can lock everyone out.",
        ["ufw"] = "ufw changes the firewall, which can lock everyone out.",
        ["useradd"] = "useradd creates an account.",
        ["userdel"] = "userdel removes an account and may remove its files.",
        ["usermod"] = "usermod changes an account.",
        ["passwd"] = "passwd changes a password.",
        ["mount"] = "mount attaches a filesystem.",
        ["umount"] = "umount detaches a filesystem.",
        ["swapoff"] = "swapoff disables swap.",
        ["modprobe"] = "modprobe loads or removes a kernel module.",
        ["tee"] = "tee writes its input to a file.",
    };

    /// <summary>
    /// Everything that may run unattended, and what each of them may be handed.
    ///
    /// Short on purpose. Adding a command here is a decision about a fleet, and
    /// the cost of leaving one out is a click.
    /// </summary>
    private static readonly Dictionary<string, Rule> ReadOnly = new(StringComparer.Ordinal)
    {
        ["ls"] = new(),
        ["pwd"] = new(),
        ["whoami"] = new(),
        ["id"] = new(),
        ["groups"] = new(),
        ["hostname"] = new(Forbidden: ["-b", "--set", "-F", "--file"]),
        ["uname"] = new(),
        ["arch"] = new(),
        ["nproc"] = new(),
        ["lscpu"] = new(),
        ["lsblk"] = new(),
        ["lsmod"] = new(),
        ["lspci"] = new(),
        ["lsusb"] = new(),
        ["uptime"] = new(),
        ["date"] = new(Forbidden: ["-s", "--set"]),
        ["df"] = new(),
        ["du"] = new(),
        ["free"] = new(Forbidden: ["-s", "--seconds"]),
        ["vmstat"] = new(),
        ["iostat"] = new(),
        ["mpstat"] = new(),
        ["sar"] = new(),
        ["ps"] = new(),
        ["pgrep"] = new(),
        ["pidof"] = new(),
        ["lsof"] = new(),
        ["cat"] = new(),
        ["head"] = new(),
        ["tail"] = new(Forbidden: ["-f", "-F", "--follow", "--retry"]),
        ["wc"] = new(),
        ["nl"] = new(),
        ["tac"] = new(),
        ["grep"] = new(),
        ["egrep"] = new(),
        ["fgrep"] = new(),
        ["zgrep"] = new(),
        ["sort"] = new(Forbidden: ["-o", "--output"]),
        ["uniq"] = new(),
        ["cut"] = new(),
        ["tr"] = new(),
        ["column"] = new(),
        ["sed"] = new(Forbidden: ["-i", "--in-place"]),
        ["find"] = new(Forbidden:
        [
            "-delete", "-exec", "-execdir", "-ok", "-okdir",
            "-fls", "-fprint", "-fprint0", "-fprintf",
        ]),
        ["stat"] = new(),
        ["file"] = new(),
        ["readlink"] = new(),
        ["realpath"] = new(),
        ["dirname"] = new(),
        ["basename"] = new(),
        ["echo"] = new(),
        ["printf"] = new(),
        ["which"] = new(),
        ["whereis"] = new(),
        ["md5sum"] = new(),
        ["sha1sum"] = new(),
        ["sha256sum"] = new(),
        ["dmesg"] = new(Forbidden: ["-C", "--clear", "-c", "-w", "--follow"]),
        ["last"] = new(Forbidden: ["-f"]),
        ["who"] = new(),
        ["w"] = new(),
        ["getent"] = new(),
        ["getconf"] = new(),
        ["locale"] = new(),
        ["printenv"] = new(),
        ["netstat"] = new(Forbidden: ["-c", "--continuous"]),
        ["ss"] = new(Forbidden: ["-K", "--kill"]),
        ["ip"] = new(
            Subcommands: ["addr", "address", "a", "link", "l", "route", "r", "neigh", "n", "rule", "netns"],
            // The object comes first and the verb second, so the verbs are what
            // has to be refused: ip route add and ip link set both write.
            Forbidden: ["add", "del", "delete", "set", "change", "replace", "flush", "append"]),
        ["ifconfig"] = new(Forbidden: ["up", "down", "add", "del", "netmask", "mtu"]),
        ["route"] = new(Forbidden: ["add", "del", "delete"]),
        ["ping"] = new(Required: ["-c"]),
        ["ping6"] = new(Required: ["-c"]),
        ["dig"] = new(),
        ["host"] = new(),
        ["nslookup"] = new(),
        ["traceroute"] = new(),
        ["systemctl"] = new(
            Subcommands:
            [
                "status", "show", "cat", "is-active", "is-enabled", "is-failed",
                "list-units", "list-unit-files", "list-timers", "list-sockets",
                "list-dependencies", "get-default", "show-environment",
            ],
            Forbidden: ["-f", "--follow"]),
        ["journalctl"] = new(Forbidden: ["-f", "--follow", "--vacuum-size", "--vacuum-time", "--vacuum-files", "--rotate", "--flush"]),
        ["docker"] = new(
            Subcommands:
            [
                "ps", "images", "image", "inspect", "logs", "stats", "version",
                "info", "top", "port", "diff", "history", "system", "volume", "network",
            ],
            // -f stops docker ps --filter too, which costs a click. docker logs
            // -f costs a pane that waits sixty seconds for a command that was
            // never going to end, so the flag is refused for all of them.
            Forbidden: ["-f", "--follow", "prune", "rm", "rmi", "kill", "stop", "restart", "exec", "run"]),
        ["kubectl"] = new(
            Subcommands: ["get", "describe", "logs", "version", "top", "api-resources", "explain", "config", "cluster-info"],
            Forbidden: ["-f", "--follow", "-w", "--watch"]),
        ["git"] = new(
            Subcommands: ["status", "log", "diff", "show", "branch", "remote", "describe", "blame", "shortlog", "rev-parse"]),
        ["apt"] = new(Subcommands: ["list", "show", "policy", "search"]),
        ["apt-get"] = new(Required: ["-s", "--simulate", "--dry-run"]),
        ["apt-cache"] = new(Subcommands: ["policy", "show", "showpkg", "search", "depends"]),
        ["dpkg"] = new(Required: ["-l", "-L", "-s", "-S", "--list", "--status", "--search"]),
        ["rpm"] = new(Required: ["-q", "-qa", "-qi", "-ql", "--query"]),
        ["yum"] = new(Subcommands: ["list", "info", "search", "repolist", "history"]),
        ["dnf"] = new(Subcommands: ["list", "info", "search", "repolist", "history"]),
        ["crontab"] = new(Required: ["-l"]),
        ["sysctl"] = new(Forbidden: ["-w", "--write", "-p", "--load"]),
        ["ulimit"] = new(),
        ["nvidia-smi"] = new(Forbidden: ["-l", "--loop", "-r", "--gpu-reset"]),
        ["smartctl"] = new(Forbidden: ["-t", "--test"]),
    };
}
