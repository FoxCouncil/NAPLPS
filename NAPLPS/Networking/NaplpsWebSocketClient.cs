// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Net.WebSockets;

namespace NAPLPS.Networking;

/// <summary>
/// Streams NAPLPS bytes over a WebSocket.
///
/// This is the transport that works everywhere. A browser is not allowed to open a TCP socket or
/// listen on a port, so <see cref="NaplpsNetworkService"/> is desktop-and-mobile only; WebSocket is
/// the one wire a browser gets. <see cref="ClientWebSocket"/> is also supported on desktop, iOS and
/// Android, so a single implementation serves every head rather than each growing its own.
///
/// Note the asymmetry that follows from the platform, not from this class: a browser can CONNECT to
/// a NAPLPS stream but can never ACCEPT one, so there is deliberately no listen-side here. A head
/// that can listen uses the TCP service for that.
///
/// Bytes are treated as an ordered stream, not as messages. NAPLPS is a byte protocol whose commands
/// can span any chunk boundary, so frames are handed on exactly as they arrive and it is the parser's
/// job to deal with partial commands - the same contract the TCP path has.
/// </summary>
public sealed class NaplpsWebSocketClient : NaplpsEndpoint
{
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    /// <summary>Size of each receive chunk. NAPLPS scenes are small; this is about latency, not throughput.</summary>
    private const int ReceiveChunk = 4096;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>
    /// Connects to <paramref name="uri"/> (ws:// or wss://) and starts reading. Bytes surface via
    /// <see cref="NaplpsEndpoint.BytesReceived"/> as they arrive.
    /// </summary>
    public async Task ConnectAsync(Uri uri, CancellationToken ct = default)
    {
        await DisconnectAsync(ct);

        _socket = new ClientWebSocket();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await _socket.ConnectAsync(uri, _cts.Token);
        }
        catch (Exception ex)
        {
            RaiseStatus($"Connect failed: {ex.Message}");
            _socket.Dispose();
            _socket = null;

            throw;
        }

        RaiseStatus($"Connected to {uri}");
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_socket, _cts.Token), CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[ReceiveChunk];

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    RaiseStatus("Remote closed the connection");
                    break;
                }

                if (result.Count <= 0)
                {
                    continue;
                }

                // A text frame carrying NAPLPS would be mangled by UTF-8 validation, but accept the
                // bytes rather than dropping a stream over a server's framing choice.
                var chunk = new byte[result.Count];
                Array.Copy(buffer, chunk, result.Count);

                Receive(chunk);
            }
        }
        catch (OperationCanceledException)
        {
            // Disconnect requested.
        }
        catch (Exception ex)
        {
            RaiseStatus($"Receive error: {ex.Message}");
        }
    }

    /// <summary>Sends <paramref name="bytes"/> as a single binary frame.</summary>
    public async Task SendAsync(byte[] bytes, CancellationToken ct = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected.");
        }

        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, endOfMessage: true, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_socket is null)
        {
            return;
        }

        try
        {
            _cts?.Cancel();

            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
            }
        }
        catch { /* best-effort */ }

        try
        {
            if (_receiveTask is not null)
            {
                await _receiveTask;
            }
        }
        catch { /* best-effort */ }

        _socket.Dispose();
        _socket = null;
        _cts?.Dispose();
        _cts = null;
        _receiveTask = null;

        RaiseStatus("Disconnected");
    }

    /// <summary>
    /// Connect, push <paramref name="bytes"/>, close. The WebSocket counterpart of
    /// <see cref="NaplpsNetworkService.SendAsync(string, int, byte[], CancellationToken)"/>.
    /// </summary>
    public static async Task SendOnceAsync(Uri uri, byte[] bytes, CancellationToken ct = default)
    {
        using var socket = new ClientWebSocket();

        await socket.ConnectAsync(uri, ct);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, endOfMessage: true, ct);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "sent", ct);
    }

    public override void Dispose()
    {
        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch { /* best-effort */ }

        base.Dispose();
    }
}
