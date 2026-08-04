// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Runtime.CompilerServices;

namespace NAPLPS;

/// <summary>
/// The byte substrate the resumable parser reads from: a growable buffer of real stream
/// bytes, a front-insertion injection queue for spliced macro bodies (X3.110 5.5), an
/// at-end flag, and storage for exactly ONE pending parser continuation.
///
/// The parse loop awaits availability via <see cref="WaitForData"/>; when the required
/// bytes are not there and the stream has not ended, the awaiter parks the parser's
/// continuation here. <see cref="Resume"/> - called by the decoder's Feed/Flush after
/// appending bytes or declaring the end - invokes it INLINE on the caller's thread; the
/// call returns when the parser suspends again or the parse completes. No threads, no
/// SynchronizationContext, no thread pool.
///
/// Real bytes stay retained from the last released position (the decoder releases up to
/// each command boundary) so <see cref="ReadRealRange"/> can capture the exact real bytes
/// a splice-boundary command consumed. Consumption itself is just a position advance;
/// the buffer compacts a released prefix only when it would otherwise force growth.
/// </summary>
internal sealed class ByteSource(bool canSplice)
{
    private byte[] _buffer = [];

    /// <summary>Index within <see cref="_buffer"/> of the byte at <see cref="_baseAbs"/>.</summary>
    private int _start;

    /// <summary>Count of retained bytes, read and unread.</summary>
    private int _count;

    /// <summary>Absolute stream position of the first retained byte.</summary>
    private long _baseAbs;

    /// <summary>Absolute stream position of the next unread real byte.</summary>
    private long _readAbs;

    private readonly List<byte> _injected = [];

    private int _injectedCursor;

    private Action? _continuation;

    /// <summary>Builds a complete, ended source over a whole buffer: the one-shot shape.
    /// Every await over it completes synchronously, so a parse never suspends.</summary>
    public static ByteSource FromBuffer(byte[] bytes, bool canSplice = false)
    {
        var source = new ByteSource(canSplice);
        source.Append(bytes);
        source.SetEnd();

        return source;
    }

    /// <summary>
    /// Whether macro invocations splice their bodies into this source's front (the
    /// top-level coded stream, one-shot or wire) or expand by recursive descent
    /// (isolated sub-parses: DEFP replay, DRCS bodies, non-spliced expansion).
    /// </summary>
    public bool CanSplice { get; } = canSplice;

    /// <summary>True once the driver has declared that no more bytes are coming.
    /// End-of-stream is real ONLY when this is set AND nothing is readable.</summary>
    public bool AtEnd { get; private set; }

    /// <summary>Total real bytes ever appended.</summary>
    public long TotalWritten => _baseAbs + _count;

    /// <summary>Absolute position of the next unread real byte.</summary>
    public long RealPosition => _readAbs;

    /// <summary>Readable bytes: pending injected bytes first, then unread real bytes.</summary>
    public int Available => InjectedRemaining + (int)(TotalWritten - _readAbs);

    /// <summary>True when injected bytes are pending ahead of the real stream.</summary>
    public bool HasInjected => _injectedCursor < _injected.Count;

    /// <summary>Count of injected bytes still pending.</summary>
    public int InjectedRemaining => _injected.Count - _injectedCursor;

    /// <summary>
    /// Monotonic count of injected bytes consumed. The parser diffs this across one
    /// command's parse to attribute byte provenance (real stream vs spliced macro body).
    /// </summary>
    public long InjectedConsumed { get; private set; }

    /// <summary>
    /// Monotonic count of InjectFront calls. A delta across one command's parse means an
    /// invocation byte was consumed mid-operand even if no body byte was consumed yet
    /// (a body opening with an opcode ends the operand scan at its first byte).
    /// </summary>
    public long InjectionCount { get; private set; }

    /// <summary>Insert bytes so they are read next, ahead of any pending injected bytes.</summary>
    public void InjectFront(byte[] bytes)
    {
        _injected.InsertRange(_injectedCursor, bytes);
        InjectionCount++;
    }

    /// <summary>Drop all pending injected bytes (CAN terminates executing macros).</summary>
    public void ClearInjected()
    {
        _injected.Clear();
        _injectedCursor = 0;
    }

    /// <summary>Appends real bytes, growing or compacting the retained region as needed.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_start + _count + bytes.Length > _buffer.Length)
        {
            if (_count + bytes.Length <= _buffer.Length)
            {
                // The retained bytes plus the new ones fit once the released prefix is dropped.
                Array.Copy(_buffer, _start, _buffer, 0, _count);
            }
            else
            {
                var grown = new byte[Math.Max(_buffer.Length * 2, _count + bytes.Length)];
                Array.Copy(_buffer, _start, grown, 0, _count);
                _buffer = grown;
            }

            _start = 0;
        }

        bytes.CopyTo(_buffer.AsSpan(_start + _count));
        _count += bytes.Length;
    }

    /// <summary>Declares the stream finished; pending reads then report end-of-stream.</summary>
    public void SetEnd()
    {
        AtEnd = true;
    }

    /// <summary>
    /// Releases retention of real bytes before the given absolute position (a committed
    /// command boundary): they can no longer be re-read, and the next compaction may
    /// reclaim their space.
    /// </summary>
    public void ReleaseBefore(long position)
    {
        var drop = (int)(position - _baseAbs);

        if (drop <= 0)
        {
            return;
        }

        _start += drop;
        _count -= drop;
        _baseAbs = position;

        if (_count == 0)
        {
            _start = 0;
        }
    }

    /// <summary>Reads the next byte: injected bytes first, then the real stream. The caller
    /// must have established availability (via an await); an empty read at the true end
    /// throws, exactly as a BinaryReader would.</summary>
    public byte ReadByte()
    {
        if (HasInjected)
        {
            var b = _injected[_injectedCursor++];
            InjectedConsumed++;

            if (_injectedCursor == _injected.Count)
            {
                _injected.Clear();
                _injectedCursor = 0;
            }

            return b;
        }

        if (_readAbs >= TotalWritten)
        {
            throw new EndOfStreamException();
        }

        return _buffer[_start + (int)(_readAbs++ - _baseAbs)];
    }

    /// <summary>The byte a read would return next, or 0 when nothing is readable.</summary>
    public byte PeekByte()
    {
        if (HasInjected)
        {
            return _injected[_injectedCursor];
        }

        if (_readAbs >= TotalWritten)
        {
            return byte.MinValue;
        }

        return _buffer[_start + (int)(_readAbs - _baseAbs)];
    }

    /// <summary>
    /// Re-reads a retained range of the real stream. Used to capture the exact real bytes
    /// a splice-boundary command consumed (see <see cref="NaplpsSequence.RawCodedBytes"/>).
    /// </summary>
    public byte[] ReadRealRange(long startPosition, long endPosition)
    {
        var result = new byte[endPosition - startPosition];
        Array.Copy(_buffer, _start + (int)(startPosition - _baseAbs), result, 0, result.Length);

        return result;
    }

    /// <summary>
    /// True at the genuine end of the stream: at-end declared AND nothing readable.
    /// Otherwise waits (suspending the parser if needed) until a byte is readable.
    /// </summary>
    public ValueTask<bool> IsEofAsync()
    {
        if (Available > 0)
        {
            return ValueTask.FromResult(false);
        }

        if (AtEnd)
        {
            return ValueTask.FromResult(true);
        }

        return IsEofSlowAsync();
    }

    private async ValueTask<bool> IsEofSlowAsync()
    {
        // A resume is a wake-up, not a guarantee: Feed may have appended fewer bytes than
        // needed (or none), so every await sits in a loop that re-checks the condition.
        while (Available == 0 && !AtEnd)
        {
            await WaitForData(1);
        }

        return Available == 0;
    }

    /// <summary>An awaitable that completes when at least <paramref name="needed"/> bytes are
    /// readable or the stream has ended. Callers MUST re-check their condition after the
    /// await: a resume only means the driver appended something or declared the end.</summary>
    public DataAwaitable WaitForData(int needed)
    {
        return new DataAwaitable(this, needed);
    }

    /// <summary>Invokes the parked parser continuation, if any, inline on this thread.
    /// Returns when the parser suspends again or the parse completes.</summary>
    public void Resume()
    {
        var continuation = _continuation;
        _continuation = null;
        continuation?.Invoke();
    }

    private void StoreContinuation(Action continuation)
    {
        if (_continuation is not null)
        {
            throw new InvalidOperationException("the parser is already suspended; only one continuation can be pending");
        }

        _continuation = continuation;
    }

    internal readonly struct DataAwaitable(ByteSource source, int needed)
    {
        public DataAwaiter GetAwaiter()
        {
            return new DataAwaiter(source, needed);
        }
    }

    internal readonly struct DataAwaiter(ByteSource source, int needed) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => source.Available >= needed || source.AtEnd;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation)
        {
            source.StoreContinuation(continuation);
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            source.StoreContinuation(continuation);
        }
    }
}
