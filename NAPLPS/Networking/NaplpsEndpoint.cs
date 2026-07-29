// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS.Networking;

/// <summary>
/// Behaviour shared by every NAPLPS transport: accumulate the bytes that arrive and announce them
/// as they do, so a consumer can render a scene incrementally and still ask for the whole stream
/// when it wants to parse it.
///
/// This exists because the transports underneath differ by platform. A browser cannot open a raw
/// socket or listen on a port at all, so the desktop's TCP service has no meaning there; WebSocket
/// is the only wire a browser gets. Keeping the buffering and events here means the editor's view
/// models work against one shape regardless of which transport a given head can actually provide.
/// </summary>
public abstract class NaplpsEndpoint : IDisposable
{
    private readonly object _bufferLock = new();
    private readonly List<byte> _receiveBuffer = [];

    /// <summary>
    /// Fired (on a worker thread) whenever bytes arrive. Subscribers should marshal back to the UI
    /// thread before touching view-model state.
    /// </summary>
    public event Action<byte[]>? BytesReceived;

    /// <summary>Fired on connect / disconnect / error. Argument is a human-readable status.</summary>
    public event Action<string>? StatusChanged;

    protected void RaiseStatus(string status)
    {
        StatusChanged?.Invoke(status);
    }

    /// <summary>Records a received chunk and announces it.</summary>
    protected void Receive(byte[] chunk)
    {
        lock (_bufferLock)
        {
            _receiveBuffer.AddRange(chunk);
        }

        BytesReceived?.Invoke(chunk);
    }

    /// <summary>Snapshot the bytes received so far (for parsing into a <see cref="NaplpsFormat"/>).</summary>
    public byte[] SnapshotReceivedBuffer()
    {
        lock (_bufferLock)
        {
            return [.. _receiveBuffer];
        }
    }

    public void ClearReceivedBuffer()
    {
        lock (_bufferLock)
        {
            _receiveBuffer.Clear();
        }
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
