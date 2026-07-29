// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS;

/// <summary>
/// A BinaryReader with a front-insertion injection queue used to splice macro bodies into
/// the coded stream at the invocation byte. X3.110 5.5 treats a macro call as if the body
/// bytes replaced the invocation byte in the incoming stream, so command operands may flow
/// across the splice boundary in both directions: a body ending in a bare opcode takes its
/// operands from the bytes following the invocation, and a body beginning with numeric data
/// extends the command preceding the invocation. Nested invocations insert ahead of the
/// remaining outer body, preserving textual expansion order.
/// </summary>
internal sealed class SpliceBinaryReader(Stream input) : BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true)
{
    private readonly List<byte> _injected = [];
    private int _cursor;

    /// <summary>True when injected bytes are pending ahead of the base stream.</summary>
    public bool HasInjected => _cursor < _injected.Count;

    /// <summary>Count of injected bytes still pending.</summary>
    public int InjectedRemaining => _injected.Count - _cursor;

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
        _injected.InsertRange(_cursor, bytes);
        InjectionCount++;
    }

    /// <summary>Drop all pending injected bytes (CAN terminates executing macros).</summary>
    public void ClearInjected()
    {
        _injected.Clear();
        _cursor = 0;
    }

    public bool TryPeekInjected(out byte value)
    {
        if (HasInjected)
        {
            value = _injected[_cursor];
            return true;
        }

        value = 0;
        return false;
    }

    public override byte ReadByte()
    {
        if (HasInjected)
        {
            var b = _injected[_cursor++];
            InjectedConsumed++;

            if (_cursor == _injected.Count)
            {
                _injected.Clear();
                _cursor = 0;
            }

            return b;
        }

        return base.ReadByte();
    }
}
