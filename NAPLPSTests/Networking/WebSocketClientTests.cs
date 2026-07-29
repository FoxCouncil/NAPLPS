// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Net;
using System.Net.WebSockets;
using NAPLPS.Networking;

namespace NAPLPSTests.Networking;

/// <summary>
/// Exercises the WebSocket transport against a real server rather than a stand-in, because the
/// things most likely to break are framing and close handling - exactly what a mock would fake.
/// This is the transport a browser head has to use, so it needs to be as trustworthy as the TCP one.
/// </summary>
[TestClass]
public class WebSocketClientTests
{
    /// <summary>Minimal echo/push server built on HttpListener's WebSocket support.</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }

        public Uri Uri => new($"ws://127.0.0.1:{Port}/naplps/");

        /// <summary>Bytes the server pushes to a client as soon as it connects.</summary>
        public byte[]? Push { get; set; }

        /// <summary>Bytes the server received from clients.</summary>
        public List<byte> Received { get; } = [];

        public TestServer(int port)
        {
            Port = port;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/naplps/");
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;

                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;

                    if (Push is { Length: > 0 })
                    {
                        // Deliberately split across two frames: NAPLPS is a byte stream, and a
                        // command may straddle a frame boundary. The client must not care.
                        int half = Push.Length / 2;
                        await ws.SendAsync(new ArraySegment<byte>(Push, 0, half), WebSocketMessageType.Binary, false, CancellationToken.None);
                        await ws.SendAsync(new ArraySegment<byte>(Push, half, Push.Length - half), WebSocketMessageType.Binary, true, CancellationToken.None);
                    }

                    var buf = new byte[4096];

                    try
                    {
                        while (ws.State == WebSocketState.Open)
                        {
                            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);

                            if (r.MessageType == WebSocketMessageType.Close)
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                                break;
                            }

                            lock (Received)
                            {
                                Received.AddRange(buf.AsSpan(0, r.Count).ToArray());
                            }
                        }
                    }
                    catch { /* client vanished */ }
                });
            }
        }

        public void Dispose()
        {
            _cts.Cancel();

            try { _listener.Stop(); } catch { }

            _cts.Dispose();
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        return port;
    }

    [TestMethod]
    public async Task ReceivesStreamedBytesAcrossFrameBoundaries()
    {
        using var server = new TestServer(FreePort());
        var payload = new byte[] { 0x1B, 0x25, 0x41, 0x24, 0x20, 0x40, 0x0C, 0xFF, 0x00, 0x42 };
        server.Push = payload;

        using var client = new NaplpsWebSocketClient();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.BytesReceived += _ =>
        {
            if (client.SnapshotReceivedBuffer().Length >= payload.Length)
            {
                done.TrySetResult(true);
            }
        };

        await client.ConnectAsync(server.Uri);

        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.AreSame(done.Task, finished, "timed out waiting for pushed bytes");

        // The two frames must reassemble into the original stream, in order.
        CollectionAssert.AreEqual(payload, client.SnapshotReceivedBuffer());

        await client.DisconnectAsync();
    }

    [TestMethod]
    public async Task SendDeliversBytesToTheServer()
    {
        using var server = new TestServer(FreePort());
        using var client = new NaplpsWebSocketClient();

        await client.ConnectAsync(server.Uri);

        Assert.IsTrue(client.IsConnected);

        var payload = new byte[] { 0x18, 0x41, 0x42, 0x43 };
        await client.SendAsync(payload);

        for (int i = 0; i < 100; i++)
        {
            lock (server.Received)
            {
                if (server.Received.Count >= payload.Length)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        lock (server.Received)
        {
            CollectionAssert.AreEqual(payload, server.Received.ToArray());
        }

        await client.DisconnectAsync();

        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    public async Task SendBeforeConnectThrowsRatherThanSilentlyDropping()
    {
        using var client = new NaplpsWebSocketClient();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await client.SendAsync([1, 2, 3]));
    }

    [TestMethod]
    public async Task SendOnceConnectsSendsAndCloses()
    {
        using var server = new TestServer(FreePort());
        var payload = new byte[] { 0x0C, 0x1B, 0x40 };

        await NaplpsWebSocketClient.SendOnceAsync(server.Uri, payload);

        for (int i = 0; i < 100; i++)
        {
            lock (server.Received)
            {
                if (server.Received.Count >= payload.Length)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        lock (server.Received)
        {
            CollectionAssert.AreEqual(payload, server.Received.ToArray());
        }
    }

    /// <summary>The buffer semantics must match the TCP transport's, since both feed the same UI.</summary>
    [TestMethod]
    public async Task ClearBufferEmptiesAccumulatedBytes()
    {
        using var server = new TestServer(FreePort());
        server.Push = [1, 2, 3, 4];

        using var client = new NaplpsWebSocketClient();
        var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BytesReceived += _ => { if (client.SnapshotReceivedBuffer().Length >= 4) { got.TrySetResult(true); } };

        await client.ConnectAsync(server.Uri);
        await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.IsNotEmpty(client.SnapshotReceivedBuffer());

        client.ClearReceivedBuffer();

        Assert.IsEmpty(client.SnapshotReceivedBuffer());

        await client.DisconnectAsync();
    }
}
