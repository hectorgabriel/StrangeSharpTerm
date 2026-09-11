// Spike: does SSH.NET actually cover what the OpenSSH/ControlMaster design gave us?
//
// Each check below stands in for something the Swift app relied on. They are
// written as assertions with names so the output reads as a verdict, not a log.
//
//   usage: SshSpike <user> <host> <port> <keyfile> <knownHostsDir>

using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshNet.Agent;

var user = args[0];
var host = args[1];
var port = int.Parse(args[2]);
var keyFile = args[3];
var work = args[4];

var failures = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine(ok ? $"  ok    {name}{(detail is null ? "" : $" ({detail})")}"
                         : $"  FAIL  {name}{(detail is null ? "" : $": {detail}")}");
    if (!ok) failures++;
}

int AcceptedAuths()
{
    var log = Path.Combine(work, "sshd.log");
    if (!File.Exists(log)) return -1;
    using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var sr = new StreamReader(fs);
    return sr.ReadToEnd().Split('\n').Count(l => l.Contains("Accepted publickey"));
}

// ---------------------------------------------------------------- connection
Console.WriteLine("\nconnection:");
var key = new PrivateKeyFile(keyFile);
var auth = new PrivateKeyAuthenticationMethod(user, key);
var info = new ConnectionInfo(host, port, user, auth) { Timeout = TimeSpan.FromSeconds(15) };

using var client = new SshClient(info);

// Host key verification, the thing StrangeTerm refused to delegate to ssh.
// The interesting part is that SSH.NET hands us the SHA256 fingerprint directly,
// so the ssh-keyscan subprocess the Swift app shelled out to is not needed.
string? seenFingerprint = null;
client.HostKeyReceived += (_, e) =>
{
    seenFingerprint = "SHA256:" + e.FingerPrintSHA256;
    e.CanTrust = true;   // a real client consults known_hosts here
};

var before = AcceptedAuths();
client.Connect();
Check("connected", client.IsConnected);
Check("host key fingerprint offered", seenFingerprint is not null, seenFingerprint);

// The fingerprint must match what ssh-keygen prints, byte for byte -- the Swift
// project asserted exactly this against real keys.
var psi = new ProcessStartInfo("ssh-keygen", $"-lf {Path.Combine(work, "host.pub")}")
{ RedirectStandardOutput = true };
var kg = Process.Start(psi)!;
var kgOut = kg.StandardOutput.ReadToEnd();
kg.WaitForExit();
var expected = kgOut.Split(' ').FirstOrDefault(t => t.StartsWith("SHA256:"));
Check("fingerprint matches ssh-keygen -lf", expected == seenFingerprint,
      $"ssh-keygen={expected} sshnet={seenFingerprint}");

// ------------------------------------------------------------- multiplexing
// The property that actually mattered about ControlMaster: many operations,
// one authentication.
Console.WriteLine("\nmultiplexing:");
for (var i = 0; i < 3; i++)
{
    using var cmd = client.RunCommand("echo hello");
    if (cmd.Result.Trim() != "hello") Check($"exec {i} output", false, cmd.Result);
}
var after = AcceptedAuths();
Check("three execs cost one authentication", after - before == 1, $"{after - before} auths");

// ------------------------------------------------------------- shell stream
Console.WriteLine("\nshell stream (the terminal pane's byte source):");
using var shell = client.CreateShellStream("xterm-256color", 80, 24, 800, 600, 4096);
shell.WriteLine("echo MARKER_$((6*7))");
var sb = new StringBuilder();
var deadline = DateTime.UtcNow.AddSeconds(10);
while (DateTime.UtcNow < deadline && !sb.ToString().Contains("MARKER_42"))
{
    var chunk = shell.Read();
    if (chunk.Length > 0) sb.Append(chunk); else Thread.Sleep(50);
}
Check("bytes flow back from a pty shell", sb.ToString().Contains("MARKER_42"));

// Resize. The Swift app got this free from SwiftTerm; the plan budgeted for
// writing it. It turns out SSH.NET exposes the window-change request directly.
try
{
    shell.ChangeWindowSize(120, 40, 1200, 800);
    shell.WriteLine("tput cols");
    var sb2 = new StringBuilder();
    deadline = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < deadline && !sb2.ToString().Contains("120"))
    {
        var chunk = shell.Read();
        if (chunk.Length > 0) sb2.Append(chunk); else Thread.Sleep(50);
    }
    Check("resize reaches the remote pty", sb2.ToString().Contains("120"), "tput cols == 120");
}
catch (Exception ex) { Check("resize reaches the remote pty", false, ex.Message); }

// ----------------------------------------------------------------- forwards
Console.WriteLine("\nport forward:");
var fwd = new ForwardedPortLocal("127.0.0.1", 18080, "127.0.0.1", (uint)port);
client.AddForwardedPort(fwd);
fwd.Start();
try
{
    using var probe = new TcpClient();
    probe.Connect("127.0.0.1", 18080);
    var buf = new byte[64];
    probe.ReceiveTimeout = 5000;
    var n = probe.GetStream().Read(buf, 0, buf.Length);
    var banner = Encoding.ASCII.GetString(buf, 0, n);
    Check("forward carries traffic", banner.StartsWith("SSH-2.0-"), banner.Trim());
}
catch (Exception ex) { Check("forward carries traffic", false, ex.Message); }
fwd.Stop();
Thread.Sleep(200);
var released = true;
try { using var t = new TcpClient(); t.Connect("127.0.0.1", 18080); released = false; }
catch { /* refused is what we want */ }
Check("forward releases its port on stop", released);

// --------------------------------------------------------------------- sftp
// Replaces 690 hand-written lines of SFTP v3 wire protocol.
Console.WriteLine("\nsftp:");
using var sftp = new SftpClient(info);
sftp.Connect();
var payload = RandomNumberGenerator.GetBytes(700 * 1024);   // same size the Swift suite used
var remote = $"/tmp/strangesharpterm-spike-{Guid.NewGuid():N}";
using (var up = new MemoryStream(payload)) sftp.UploadFile(up, remote);
using var down = new MemoryStream();
sftp.DownloadFile(remote, down);
Check("700 KiB round-trips by SHA-256",
      Convert.ToHexString(SHA256.HashData(payload)) == Convert.ToHexString(SHA256.HashData(down.ToArray())));
sftp.DeleteFile(remote);
Check("delete works", !sftp.Exists(remote));
sftp.Disconnect();

// ---------------------------------------------------------------- ssh-agent
// The preferred credential method: the key never enters the app's address space.
// This is the piece with no first-party SSH.NET support, so it is the real risk --
// and enumeration alone proves nothing. It has to actually authenticate.
Console.WriteLine("\nssh-agent (SshNet.Agent):");
try
{
    var agent = new SshAgent(TimeSpan.FromSeconds(5));
    var identities = agent.RequestIdentities();
    Check("agent reachable", true,
          $"{identities.Length} identit{(identities.Length == 1 ? "y" : "ies")} offered");

    if (identities.Length == 0)
    {
        Console.WriteLine("        (no identities loaded; run ssh-add to exercise agent auth)");
    }
    else
    {
        // Authenticate for real, using only what the agent will sign.
        var agentAuth = new PrivateKeyAuthenticationMethod(user, identities);
        var agentInfo = new ConnectionInfo(host, port, user, agentAuth)
        { Timeout = TimeSpan.FromSeconds(15) };
        using var agentClient = new SshClient(agentInfo);
        agentClient.HostKeyReceived += (_, e) => e.CanTrust = true;
        agentClient.Connect();
        using var c = agentClient.RunCommand("echo AGENT_OK");
        Check("authenticates using an agent-held key", c.Result.Trim() == "AGENT_OK");
        agentClient.Disconnect();
    }
}
catch (Exception ex)
{
    Check("agent reachable", false, $"{ex.GetType().Name}: {ex.Message}");
}

client.Disconnect();
Console.WriteLine($"\n{(failures == 0 ? "all checks passed" : $"{failures} check(s) failed")}");
return failures == 0 ? 0 : 1;
