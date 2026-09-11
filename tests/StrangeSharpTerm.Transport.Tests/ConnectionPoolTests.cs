using System.Net;
using System.Net.Sockets;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Transport.Tests;

/// <summary>
/// Replaces the Swift suite that pinned ssh's argument strings. Those arguments
/// are gone with the binary; what survives is the property they existed to
/// deliver — authenticate once per host, then share the connection.
/// </summary>
public class ConnectionPoolTests
{
    private sealed class FakeSession : ISshSession
    {
        public bool IsConnected { get; set; } = true;
        public int DisposeCount { get; private set; }
        public bool ThrowOnDispose { get; init; }

        public void Dispose()
        {
            DisposeCount++;
            IsConnected = false;
            if (ThrowOnDispose)
                throw new InvalidOperationException("the server hung up first");
        }
    }

    private static ResolvedConnection Host(string name = "db-01") =>
        new(new Connection { Name = name, Hostname = $"{name}.example.com" },
            new ResolvedSettings(ConnectionSettings.Empty),
            []);

    [Fact]
    public void OneHostGetsOneSessionHoweverManyConsumersAskForIt()
    {
        var opened = 0;
        using var pool = new ConnectionPool(_ => { opened++; return new FakeSession(); });
        var host = Host();

        var first = pool.Session(host);
        var second = pool.Session(host);

        second.ShouldBeSameAs(first);
        opened.ShouldBe(1);
        pool.OpenCount.ShouldBe(1);
    }

    [Fact]
    public void DifferentHostsGetDifferentSessions()
    {
        using var pool = new ConnectionPool(_ => new FakeSession());

        pool.Session(Host("db-01")).ShouldNotBeSameAs(pool.Session(Host("web-01")));
        pool.OpenCount.ShouldBe(2);
    }

    [Fact]
    public void ASessionThatDroppedIsReplacedRatherThanHandedBack()
    {
        // The caller asked for a usable connection; returning a dead one only
        // moves the failure somewhere less helpful.
        var sessions = new List<FakeSession>();
        using var pool = new ConnectionPool(_ =>
        {
            var session = new FakeSession();
            sessions.Add(session);
            return session;
        });
        var host = Host();

        var first = (FakeSession)pool.Session(host);
        first.IsConnected = false;
        var second = pool.Session(host);

        second.ShouldNotBeSameAs(first);
        first.DisposeCount.ShouldBe(1);
        sessions.Count.ShouldBe(2);
        pool.OpenCount.ShouldBe(1);
    }

    [Fact]
    public void DisconnectingClosesTheSessionAndForgetsIt()
    {
        using var pool = new ConnectionPool(_ => new FakeSession());
        var host = Host();
        var session = (FakeSession)pool.Session(host);

        pool.Disconnect(host.Connection.Id);

        session.DisposeCount.ShouldBe(1);
        pool.OpenCount.ShouldBe(0);
        pool.IsOpen(host.Connection.Id).ShouldBeFalse();
    }

    [Fact]
    public void DisposingThePoolClosesEverySession()
    {
        // Quitting drops every connection, which is the documented consequence of
        // having no background agent any more.
        var sessions = new List<FakeSession>();
        var pool = new ConnectionPool(_ =>
        {
            var session = new FakeSession();
            sessions.Add(session);
            return session;
        });
        pool.Session(Host("a"));
        pool.Session(Host("b"));

        pool.Dispose();

        sessions.Count.ShouldBe(2);
        sessions.ShouldAllBe(s => s.DisposeCount == 1);
        Should.Throw<ObjectDisposedException>(() => pool.Session(Host("a")));
    }

    [Fact]
    public void ASessionThatThrowsOnCloseIsStillForgotten()
    {
        using var pool = new ConnectionPool(_ => new FakeSession { ThrowOnDispose = true });
        var host = Host();
        pool.Session(host);

        pool.Disconnect(host.Connection.Id);

        pool.OpenCount.ShouldBe(0);
    }

    [Fact]
    public void ConcurrentCallersStillAuthenticateOnlyOnce()
    {
        // The whole point of the design: a dashboard refresh, a terminal and an
        // SFTP pane opening at once must not mean three logins.
        var opened = 0;
        using var pool = new ConnectionPool(_ =>
        {
            Interlocked.Increment(ref opened);
            return new FakeSession();
        });
        var host = Host();

        Parallel.For(0, 64, _ => pool.Session(host));

        opened.ShouldBe(1);
        pool.OpenCount.ShouldBe(1);
    }
}

public class LocalPortProbeTests
{
    [Fact]
    public async Task APortSomethingIsListeningOnIsDetected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            (await LocalPortProbe.IsListeningAsync(port, cancellationToken: TestContext.Current.CancellationToken)).ShouldBeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task APortNothingIsListeningOnIsNot()
    {
        // Bound, then released, so the number is real but nothing answers on it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        (await LocalPortProbe.IsListeningAsync(port, cancellationToken: TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task AnImpossiblePortNumberIsRefusedWithoutTouchingTheNetwork()
    {
        (await LocalPortProbe.IsListeningAsync(0, cancellationToken: TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await LocalPortProbe.IsListeningAsync(70_000, cancellationToken: TestContext.Current.CancellationToken)).ShouldBeFalse();
    }
}
