// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace NAPLPS;

/// <summary>
/// The NAPLPS codec: coded bytes in, complete presentation commands out.
///
/// The decoder owns the parse loop and nothing else. It mutates a live <see cref="NaplpsState"/>
/// as it goes and emits <see cref="NaplpsSequence"/> values, each pairing a command with a
/// snapshot of the state in force when that command executes. It knows nothing about
/// rendering, byte history, undo, or how its output will be consumed.
///
/// Two drivers sit above it: <see cref="NaplpsFormat"/> (the editor - retains the coded bytes
/// and every per-command state snapshot so the whole stream can be re-rendered or edited) and
/// <see cref="NaplpsStreamSession"/> (the runtime - retains nothing and renders at the decode
/// frontier). Neither distinction reaches this class; there is no mode flag here.
///
/// See docs/plans/streaming-decode-and-surface-model.md.
/// </summary>
public sealed class NaplpsDecoder
{
    /// <summary>The live decoder state, mutated in place as bytes are consumed.</summary>
    public NaplpsState State { get; }

    /// <summary>
    /// We operate in 7-bit mode until a byte above 0x80 appears; once switched, we cannot
    /// go back. Sticky for the lifetime of the decoder, not per call.
    /// </summary>
    public bool Is7Bit { get; private set; } = true;

    /// <summary>Last spacing character parsed, so a following REPEAT can advance the pen.</summary>
    private char? _lastCharForRepeat;

    private int _drcsRecursionDepth;

    private const int MaxDrcsRecursionDepth = 4;

    /// <summary>
    /// Bytes handed to <see cref="Feed"/> that no complete command has claimed yet: the live
    /// segment is <see cref="_pending"/>[<see cref="_pendingStart"/> ..+ <see cref="_pendingCount"/>].
    /// Feeds append into spare capacity and consuming a committed prefix advances the start
    /// offset, so buffer bookkeeping is O(1) amortized per byte - no per-feed copy, no shift.
    /// The array is compacted only when a dead prefix would otherwise force it to grow.
    /// </summary>
    private byte[] _pending = [];

    private int _pendingStart;

    private int _pendingCount;

    /// <summary>
    /// The reader over <see cref="_pending"/> while a <see cref="Feed"/> or <see cref="Flush"/>
    /// is in flight. Deferral applies to this reader ONLY: macro bodies, DEFP expansions and
    /// DRCS definitions decode from their own complete buffers, where end-of-buffer is real.
    /// </summary>
    private BinaryReader? _streamingReader;

    /// <summary>Position in <see cref="_streamingReader"/> just past the last complete command.</summary>
    private long _committedPosition;

    /// <summary>Set by <see cref="Flush"/>: no more bytes are coming, so nothing is ambiguous.</summary>
    private bool _atStreamEnd;

    /// <summary>Bytes received but not yet resolved into a complete command.</summary>
    public int PendingByteCount => _pendingCount;

    /// <summary>Appends bytes to the live pending segment, growing or compacting as needed.</summary>
    private void PendingAppend(ReadOnlySpan<byte> bytes)
    {
        if (_pendingStart + _pendingCount + bytes.Length > _pending.Length)
        {
            if (_pendingCount + bytes.Length <= _pending.Length)
            {
                // The live bytes plus the new ones fit once the dead prefix is dropped.
                Array.Copy(_pending, _pendingStart, _pending, 0, _pendingCount);
            }
            else
            {
                var grown = new byte[Math.Max(_pending.Length * 2, _pendingCount + bytes.Length)];
                Array.Copy(_pending, _pendingStart, grown, 0, _pendingCount);
                _pending = grown;
            }

            _pendingStart = 0;
        }

        bytes.CopyTo(_pending.AsSpan(_pendingStart + _pendingCount));
        _pendingCount += bytes.Length;
    }

    /// <summary>Releases a consumed prefix of the live pending segment.</summary>
    private void PendingConsume(int count)
    {
        _pendingStart += count;
        _pendingCount -= count;
        if (_pendingCount == 0)
        {
            _pendingStart = 0;
        }
    }

    public NaplpsDecoder(NaplpsState state)
    {
        State = state;
    }

    /// <summary>
    /// Seeds a state with the decoder defaults for a NAPLPS variant: the color map, coordinate
    /// precision and PDI set that the variant assumes rather than transmits. Every driver must
    /// apply this before the first byte, and both must apply the SAME one, or per-command state
    /// snapshots diverge between the editor and the wire.
    /// </summary>
    public static void ApplySystemDefaults(NaplpsState state, NaplpsSystemType systemType)
    {
        state.SystemType = systemType;

        switch (systemType)
        {
            case NaplpsSystemType.Prodigy:
            state.ColorMap = new Dictionary<byte, NaplpsColor>(NaplpsState.ColorMapProdigyDefaults);
            // Prodigy applications (GCU.EXE, COOK.EXE, the reception client) send a standard
            // prologue to MVDI before presenting an object: a DOMAIN (a1 c8 c0 c0 c9) whose
            // logical pel is 1/256, and a TEXT command (a2 c0 c0 c0 c1 f2) setting the 6x10
            // char field on the 256 logical grid. Objects are authored against that ambient
            // state, not the decoder's power-on defaults (zero pel, 1/40 char field).
            state.CharSize = new Vector2(3.0f / 128.0f, 5.0f / 128.0f);
            state.LogicalPel = new Vector2(1.0f / 256.0f, 1.0f / 256.0f);
            break;

            case NaplpsSystemType.Telidon:
            // Telidon v699: higher default coordinate precision, restricted PDI set
            state.MultiByteValue = 4;
            NaplpsState.TelidonPDISet.CopyTo(state.InUseTable, NaplpsState.GRight);
            break;

            case NaplpsSystemType.NAPLPS:
            default:
            // Default color map is already set in NaplpsState
            break;
        }
    }

    /// <summary>
    /// Raised when the streaming reader runs out of bytes at a point where the bytes still to
    /// come could change the decode. Unwinds to the last complete command boundary; the partial
    /// command's bytes stay in <see cref="_pending"/> and are re-decoded on the next feed.
    /// </summary>
    private sealed class NeedMoreDataException : Exception;

    /// <summary>
    /// Signals a truncated tail on the streaming reader. A no-op on any other reader, and a
    /// no-op once <see cref="Flush"/> has declared the stream finished.
    /// </summary>
    private void NeedMoreData(BinaryReader reader)
    {
        if (!_atStreamEnd && ReferenceEquals(reader, _streamingReader))
        {
            throw new NeedMoreDataException();
        }
    }

    /// <summary>
    /// True when a partial command is waiting on bytes still to come - whether its bytes
    /// sit in the pending buffer or, for a deferred macro expansion whose real bytes were
    /// all consumed, in the saved injection tail. A caller must not append bytes of its
    /// own devising over either: they would splice into the partial command.
    /// </summary>
    public bool HasDeferredTail => _pendingCount > 0 || _savedInjected is { Length: > 0 };

    private static long Remaining(BinaryReader reader) =>
        reader.BaseStream.Length - reader.BaseStream.Position
        + (reader is SpliceBinaryReader s ? s.InjectedRemaining : 0);

    /// <summary>
    /// Feeds coded bytes and returns the commands that became complete. A command whose operand
    /// list or trailing operand bytes run to the end of what has arrived is DEFERRED, not
    /// guessed at: an X3.110 operand list is terminated by the next non-numeric byte, so a
    /// complete command ending at the frontier is byte-identical to a truncated one and the
    /// difference is undetectable in principle. Its bytes are held and re-decoded on the next
    /// feed, exactly as MVDI's PLPDecode returns "internal decode in progress".
    /// </summary>
    public List<NaplpsSequence> Feed(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.IsEmpty)
        {
            PendingAppend(bytes);
        }

        return Decode(atStreamEnd: false);
    }

    /// <summary>
    /// Declares the stream finished: decodes whatever is pending without deferring, so a
    /// command that ends at the last byte is emitted rather than held. This is the only
    /// difference between the file path and the wire path, and the DRIVER decides it - the
    /// decode rules themselves never branch on which consumer is asking.
    /// </summary>
    public List<NaplpsSequence> Flush() => Decode(atStreamEnd: true);

    private List<NaplpsSequence> Decode(bool atStreamEnd)
    {
        var emitted = new List<NaplpsSequence>();

        // Commands a failed attempt settled but never delivered - the exception preempted
        // the return value while their state mutations and consumed bytes stood. They belong
        // to the caller, so the next decode leads with them; losing them would make a
        // consumer painting from returned commands skip them forever.
        if (_settledUndelivered is { Count: > 0 })
        {
            emitted.AddRange(_settledUndelivered);
        }

        _settledUndelivered = null;

        // A saved expansion tail counts as pending input even when no real bytes are:
        // it must decode on the next attempt (or be released by Flush), never dropped.
        if (_pendingCount == 0 && _savedInjected is not { Length: > 0 })
        {
            // Even with no bytes pending, declaring the stream over must settle definition
            // buffers whose bytes were consumed in earlier feeds - the one-shot path flushes
            // an unterminated definition at EOF and the wire path must match.
            if (atStreamEnd)
            {
                FlushOpenDefinitionBuffers(emitted);
            }

            return emitted;
        }

        // Read the live pending segment in place: the stream is scoped to this attempt and
        // the buffer cannot move under it (no reentrant Feed), so no copy is taken.
        using var stream = new MemoryStream(_pending, _pendingStart, _pendingCount, writable: false);

        // The wire path splices macro expansions exactly like the one-shot path (X3.110
        // 5.5): equivalence between Feed and FromBytes is the contract the corpus gate
        // holds. The splice queue lives and dies with this attempt; expansions replay
        // whole on re-decode because no command boundary is committed mid-expansion.
        using var reader = new SpliceBinaryReader(stream);

        // Resume a macro expansion interrupted by an earlier truncated tail: the saved
        // injected bytes go back to the front of the queue, making mid-expansion command
        // boundaries committable like any other (nothing is ever re-executed, so state
        // mutations from already-emitted expansion commands are never double-applied).
        if (_savedInjected is { Length: > 0 })
        {
            reader.InjectFront(_savedInjected);
        }

        _savedInjected = null;

        _streamingReader = reader;
        _atStreamEnd = atStreamEnd;
        _committedPosition = 0;
        _committedCommandCount = emitted.Count;   // a re-delivered prefix is already settled
        _committedInjected = null;
        _shiftAtBoundary = State.PendingSingleShift;   // the attempt starts AT a boundary

        try
        {
            ReadStream(reader, sink: emitted);
        }
        catch (NeedMoreDataException)
        {
            // The tail is a partial command; it stays pending. The defer discipline throws
            // before anything is emitted or state is mutated, so the sink trim is a no-op
            // safety net. A mid-expansion unwind saves the injection queue for resumption.
            if (emitted.Count > _committedCommandCount)
            {
                emitted.RemoveRange(_committedCommandCount, emitted.Count - _committedCommandCount);
            }

            _savedInjected = _committedInjected;
        }
        catch
        {
            // Any unexpected exception must not desynchronize the context: settle to the
            // last committed boundary exactly as a deferral does - commands and state up to
            // the boundary stand, the unconsumed tail stays pending for a retry, and a
            // mid-expansion queue snapshot is preserved. The exception still propagates,
            // but the context remains boundary-consistent.
            if (emitted.Count > _committedCommandCount)
            {
                emitted.RemoveRange(_committedCommandCount, emitted.Count - _committedCommandCount);
            }

            // The settled prefix cannot reach the caller through a throw; stash it so the
            // next decode delivers it. And a single shift consumed by the aborted command
            // goes back, exactly as the deferral unwind in ReadStream puts it back.
            _settledUndelivered = emitted.Count > 0 ? emitted : null;
            State.PendingSingleShift = _shiftAtBoundary;

            _savedInjected = _committedInjected;
            PendingConsume((int)_committedPosition);
            throw;
        }
        finally
        {
            _streamingReader = null;
            _atStreamEnd = false;
        }

        PendingConsume(atStreamEnd ? _pendingCount : (int)_committedPosition);

        return emitted;
    }

    /// <summary>
    /// Records the current position as a command boundary, for unwinding - together with how
    /// many commands the sink held, so an aborted attempt can also retract sequences emitted
    /// past the boundary (a spliced macro expansion that must replay whole).
    /// </summary>
    private void Commit(BinaryReader reader, int commandCount)
    {
        if (ReferenceEquals(reader, _streamingReader))
        {
            _committedPosition = reader.BaseStream.Position;
            _committedCommandCount = commandCount;
            _shiftAtBoundary = State.PendingSingleShift;

            // The boundary's injection-queue snapshot: an abort resumes from HERE, and the
            // aborted attempt may have consumed queue bytes for the partial command. Null
            // (the overwhelmingly common empty case) costs nothing per command.
            _committedInjected = reader is SpliceBinaryReader { HasInjected: true } s
                ? s.SnapshotPending()
                : null;
        }
    }

    private int _committedCommandCount;

    /// <summary>Injection queue as of the last committed boundary, for resumption.</summary>
    private byte[]? _committedInjected;

    /// <summary>Injected macro-body bytes to restore into the next attempt's reader.</summary>
    private byte[]? _savedInjected;

    /// <summary>Commands settled by an attempt that ended in a throw; see the Decode catch.</summary>
    private List<NaplpsSequence>? _settledUndelivered;

    /// <summary>
    /// A pending SS2/SS3 single-shift is consumed by <see cref="NaplpsState.ResolveByte"/> before
    /// the operand list is read, so an attempt aborted in the operands must put it back or the
    /// re-decode resolves the same byte through the wrong G-set. The two supplementary-resolution
    /// flags need no restoring: ResolveByte recomputes both, and nothing reads them in between.
    /// </summary>
    private NaplpsState.GsetSlot? _shiftAtBoundary;

    private void RecordError(NaplpsErrorSeverity severity, NaplpsErrorType type, string message, byte? opcode = null, long? streamPosition = null)
    {
        State.RecordError(severity, type, message, opcode, streamPosition);
    }

    public List<NaplpsSequence> ReadStream(BinaryReader reader, bool isMacroExpansion = false, List<NaplpsSequence>? sink = null)
    {
        var commands = sink ?? [];
        var splice = reader as SpliceBinaryReader;

        // Expansion sequences render but are not coded input; commands materialized while the
        // opcode byte came from a spliced macro body are marked synthetic after the fact.
        static void MarkSynthetic(List<NaplpsSequence> list, int fromIndex)
        {
            for (var i = fromIndex; i < list.Count; i++)
            {
                list[i].IsSynthetic = true;
            }
        }

        try
        {
            while (!reader.IsEOF())
            {
                // Every iteration begins at a command boundary; record it so a truncated tail
                // can unwind here. An aborted attempt emits nothing past the boundary, so
                // `commands` always holds exactly the commands that completed. Boundaries
                // inside a macro expansion commit too - the injection queue survives an
                // unwind (see Decode), so the expansion resumes rather than replays.
                Commit(reader, commands.Count);

                // ANSI X3.110 §6.1.6.3: CAN terminates currently executing macros immediately.
                // Only check inside macro expansion; at top level CAN is a no-op (the flag
                // is cleared by the outer call when it returns).
                if (isMacroExpansion && State.IsCancelRequested)
                {
                    State.IsCancelRequested = false;
                    break;
                }

                // Byte-provenance snapshot: whether this command's opcode comes from a spliced
                // macro body, and how far along the real stream and the injection queue are.
                // The deltas after the command parses attribute its bytes to body vs stream.
                bool opcodeInjected = splice?.HasInjected == true;
                long injectedBefore = splice?.InjectedConsumed ?? 0;
                long injectionsBefore = splice?.InjectionCount ?? 0;
                long realStart = splice != null ? reader.BaseStream.Position : 0;
                int commandsBefore = commands.Count;

                var opcode = reader.ReadByte();

                // We operate in 7 bit mode until we get 8 bits,
                // once switched, we can't go back to 7 bit mode.
                if (opcode > 0x80)
                {
                    Is7Bit = false;
                }

                // Buffered modes: macro/DRCS/texture definition consume bytes until END
                if (HandleBufferedByte(opcode, reader, commands))
                {
                    if (opcodeInjected)
                    {
                        MarkSynthetic(commands, commandsBefore);
                    }

                    continue;
                }

                // Macro invocation: a byte resolved through a G-set designated as the macro set
                // (via SS3/LS3 into a macro-designated slot) expands that macro at parse time
                // instead of drawing a character.
                if (State.IsMacroByte(opcode))
                {
                    State.PendingSingleShift = null; // the single-shift, if any, is consumed here

                    // X3.110 section 5.5: the coded stream carries only the single invocation
                    // byte. Preserve it as a raw (non-drawing) command so ToBytes round-trips;
                    // everything the expansion produces is presentation output, marked synthetic.
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, opcode, [])) { IsSynthetic = opcodeInjected });

                    if (splice != null)
                    {
                        // Splice the body into the coded stream at the invocation byte (5.5).
                        // Operands may then flow across the boundary in both directions: a body
                        // ending in a bare opcode draws its operands from the bytes following
                        // the invocation, and a body beginning with numeric data extends the
                        // command preceding it (see ReadOperands).
                        if (State.Macros.TryGetValue((char)opcode, out var macroBody) && macroBody.Length > 0)
                        {
                            splice.InjectFront(macroBody);
                        }
                    }
                    else
                    {
                        // Isolated sub-streams (DEFP replay, DRCS parsing) keep the recursive
                        // expansion - they have no outer coded stream to splice into.
                        ExecuteMacro(new NaplpsOperands(new byte[] { opcode }), commands);
                    }

                    continue;
                }

                // Use ResolveByte so a pending SS2/SS3 single-shift gets consumed by this byte.
                var commandReference = State.ResolveByte(opcode);

                if (commandReference == null)
                {
                    RecordError(NaplpsErrorSeverity.Error, NaplpsErrorType.UnknownOpcode, "Unknown opcode in InUseTable", opcode, reader.BaseStream.Position - 1);
                    // Preserve the unknown byte so ToBytes round-trips. The renderer won't
                    // draw it (not IDrawable), but the byte survives serialization.
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, opcode, [])) { IsSynthetic = opcodeInjected });
                    continue;
                }

                var commandType = commandReference.CommandType ?? typeof(NaplpsCommand);
                var commandParameters = commandReference.Parameters;
                var additionalParameters = ReadOperands(reader, commandReference.OperandType);

                if (commandReference.CommandType == typeof(NumericalDataCommand))
                {
                    RecordError(NaplpsErrorSeverity.Warning, NaplpsErrorType.UnknownOpcode, "NumericalDataCommand reached unexpectedly", opcode, reader.BaseStream.Position);
                    // Preserve the orphan byte as a bare NaplpsCommand so it round-trips
                    // through ToBytes() — historically these bytes (e.g. 0x41 in card1.nap
                    // after ESC D) were silently dropped, breaking byte-level round-trip.
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, opcode, [])) { IsSynthetic = opcodeInjected });
                    continue;
                }

                // Clone the current state before executing the command
                var currentState = State.Clone();

                if (commandType == typeof(ControlCommand) && commandParameters.Count == 1)
                {
                    HandleControlCommand((NaplpsControlCommands)commandParameters[0], reader, additionalParameters, commands);

                    // Sequences the handler materialized (e.g. DEFP replay) inside an
                    // expansion are presentation output like the rest of the expansion.
                    if (opcodeInjected)
                    {
                        MarkSynthetic(commands, commandsBefore);
                    }

                    // Re-clone AFTER control command so the sequence's state snapshot
                    // reflects changes made by the handler (cursor position, scroll flag, etc.)
                    currentState = State.Clone();
                    State.ScrollEventOccurred = false;
                }

                var command = TryInstantiateCommand(commandType, commandParameters, opcode, additionalParameters, reader);

                if (command != null)
                {
                    var sequence = new NaplpsSequence(currentState, command) { IsSynthetic = opcodeInjected };

                    // A command whose parse crossed a splice boundary - it consumed body bytes,
                    // or it consumed an invocation byte mid-operand (a new injection) - may mix
                    // real coded-stream bytes with body bytes. Capture exactly the real bytes so
                    // serialization stays byte-exact: they include any invocation byte consumed
                    // mid-operand, and exclude the body bytes, which serialize once inside their
                    // definition.
                    if (splice != null
                        && (splice.InjectedConsumed != injectedBefore || splice.InjectionCount != injectionsBefore)
                        && reader.BaseStream.Position > realStart)
                    {
                        sequence.RawCodedBytes = ReadRealRange(reader, realStart, reader.BaseStream.Position);
                    }

                    commands.Add(sequence);

                    // Track the last spacing character so a following REPEAT can advance the pen
                    // across the repeated cells (see HandleControlCommand's Repeat branch).
                    if (command is AsciiCharCommand ac && !ac.IsNonSpacing && !ac.IsDiscarded)
                    {
                        _lastCharForRepeat = ac.AsciiCharacter;
                    }
                }

                // One-shot: clear scroll flag set by non-ControlCommand constructors
                // (e.g. IncrementalFieldCommand) so only the triggering command carries it.
                State.ScrollEventOccurred = false;
            }

            Commit(reader, commands.Count);
        }
        catch (NeedMoreDataException)
        {
            State.PendingSingleShift = _shiftAtBoundary;
            throw;
        }
        catch (EndOfStreamException)
        {
            RecordError(NaplpsErrorSeverity.Error, NaplpsErrorType.UnexpectedEndOfStream, "Stream ended unexpectedly during parsing");
        }

        // Only the top-level parse (always spliced) flushes, and only at TRUE end of
        // stream: a definition open at a feed boundary stays pending - its END may be in
        // the next chunk. Recursive sub-stream parses (DEFP replay, DRCS data) must not
        // disturb definition state they did not open.
        if (splice != null && (_streamingReader == null || _atStreamEnd))
        {
            FlushOpenDefinitionBuffers(commands);
        }

        return commands;
    }

    /// <summary>
    /// A definition left open at end of stream - a stray DEF byte in embedded text (e.g. the
    /// 0x80 inside a UTF-8 em-dash), a trailer byte, or a truncated file - still owns its
    /// buffered bytes. Emit them as raw commands so serialization stays byte-exact. The
    /// macro/DRCS/texture itself is not stored: it was never terminated.
    /// </summary>
    private void FlushOpenDefinitionBuffers(List<NaplpsSequence> commands)
    {
        if (State.MacroBeingDefined != null)
        {
            State.MacroBeingDefined = null;

            foreach (var b in State.MacroBuffer)
            {
                commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, b, [])));
            }

            State.MacroBuffer.Clear();
        }

        if (State.DrcsStartCode != null)
        {
            State.DrcsStartCode = null;

            foreach (var b in State.DrcsBuffer)
            {
                commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, b, [])));
            }

            State.DrcsBuffer.Clear();
        }

        if (State.TextureBeingDefined != null)
        {
            State.TextureBeingDefined = null;

            foreach (var b in State.TextureBuffer)
            {
                commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, b, [])));
            }

            State.TextureBuffer.Clear();
        }
    }

    /// <summary>
    /// Handles bytes while in a buffered definition mode (macro, DRCS, texture).
    /// Returns true if the byte was consumed by the buffer, false if normal processing should continue.
    /// </summary>
    private bool HandleBufferedByte(byte opcode, BinaryReader reader, List<NaplpsSequence> commands)
    {
        // If we're in macro definition mode, buffer bytes until END.
        // Body bytes are ALSO injected as raw byte commands so the Telidraw
        // decompiler can see them and the round-trip preserves every byte.
        if (State.MacroBeingDefined != null)
        {
            // A definition may be terminated by a single-byte END or the 7-bit ESC-coded
            // END (ESC 4/5). Consume the trailing final byte so it is not buffered as body.
            bool escEnd = IsEscEnd(opcode, reader);
            if (escEnd)
            {
                reader.ReadByte();
            }

            // X3.110 6.2.2: a DEF MACRO is ALSO terminated by the next DEF MACRO, DEFP
            // MACRO, DEFT MACRO, DEF DRCS, or DEF TEXTURE. The Prodigy logon templates
            // (TL80TB10) chain dozens of definitions this way with no ENDs at all.
            bool defTerminated = !escEnd && !IsEndCommand(opcode) && IsDefinitionCommand(opcode);

            // The terminating DEF byte is handed back to normal command processing below,
            // which reads its operand list (or name byte) and DEFERS if those bytes run to
            // the frontier. That unwind would retract the body and replay commands emitted
            // here while the definition-close state mutations stood - the retry skips the
            // closed definition and the commands are lost for good. So defer the WHOLE
            // handoff, before any mutation, unless the terminator's own command is
            // decidable from the bytes that have arrived: a non-numeric byte must
            // terminate its operand run (an empty run's first byte then serves as the
            // name). Skipped when spliced bytes are pending - a peek there would consume
            // the injection queue - which keeps this exact for the real-stream case.
            if (defTerminated && ReferenceEquals(reader, _streamingReader) && !_atStreamEnd)
            {
                if (reader.IsEOF())
                {
                    NeedMoreData(reader);
                }

                if (reader is not SpliceBinaryReader { InjectedRemaining: > 0 })
                {
                    var scan = reader.BaseStream.Position;
                    bool listTerminated = false;
                    while (reader.BaseStream.Position < reader.BaseStream.Length)
                    {
                        var next = (byte)reader.BaseStream.ReadByte();
                        if (State.InUseTable[next]?.CommandType != typeof(NumericalDataCommand))
                        {
                            listTerminated = true;
                            break;
                        }
                    }

                    reader.BaseStream.Position = scan;
                    if (!listTerminated)
                    {
                        NeedMoreData(reader);
                    }
                }
            }

            if (escEnd || defTerminated || IsEndCommand(opcode))
            {
                var macroName = State.MacroBeingDefined.Value;
                var macroType = State.MacroDefType;
                State.Macros[macroName] = [.. State.MacroBuffer];
                State.MacroBeingDefined = null;

                // Inject buffered body bytes as individual raw commands for decompiler fidelity.
                foreach (var b in State.MacroBuffer)
                {
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, b, [])));
                }

                State.MacroBuffer.Clear();

                if (!defTerminated)
                {
                    // Inject the END command itself, carrying the ESC form's final byte when
                    // present. A definition-terminated definition has no END byte to inject -
                    // the terminating DEF byte is processed as its own command below.
                    commands.Add(new NaplpsSequence(State.Clone(), MakeEndCommand(opcode, escEnd)));
                }

                if (macroType == 1 && State.Macros.TryGetValue(macroName, out var macroData))
                {
                    // DEFP MACRO defines and displays in one step (X3.110 define-and-display
                    // form): the executed body is presentation output, not coded input, so
                    // those sequences are synthetic - the definition serializes exactly once.
                    using var macroStream = new MemoryStream(macroData);
                    using var macroReader = new BinaryReader(macroStream);
                    foreach (var seq in ReadStream(macroReader))
                    {
                        seq.IsSynthetic = true;
                        commands.Add(seq);
                    }
                }

                if (defTerminated)
                {
                    // Hand the terminating DEF byte back to normal processing so it starts
                    // the next definition (or DRCS/texture mode) itself.
                    return false;
                }
            }
            else
            {
                State.MacroBuffer.Add(opcode);
            }

            return true;
        }

        // DRCS definition mode
        if (State.DrcsStartCode != null)
        {
            if (IsEndCommand(opcode))
            {
                ParseDrcsData(State.DrcsStartCode.Value, State.DrcsBuffer);
                State.DrcsStartCode = null;

                foreach (var b in State.DrcsBuffer)
                {
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, b, [])));
                }

                State.DrcsBuffer.Clear();
                commands.Add(new NaplpsSequence(State.Clone(), new ControlCommand(NaplpsControlCommands.End, State, opcode, [])));
            }
            else
            {
                State.DrcsBuffer.Add(opcode);
            }

            return true;
        }

        // Texture definition mode
        if (State.TextureBeingDefined != null)
        {
            // Like macros, a 7-bit stream terminates the definition with the ESC-coded END
            // (ESC 4/5); consume the trailing final byte so it is not buffered as body.
            bool textureEscEnd = IsEscEnd(opcode, reader);
            if (textureEscEnd)
            {
                reader.ReadByte();
            }

            if (textureEscEnd || IsEndCommand(opcode))
            {
                ParseTextureData(State.TextureBeingDefined.Value, State.TextureBuffer);
                State.TextureBeingDefined = null;

                foreach (var b in State.TextureBuffer)
                {
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, b, [])));
                }

                State.TextureBuffer.Clear();
                commands.Add(new NaplpsSequence(State.Clone(), MakeEndCommand(opcode, textureEscEnd)));
            }
            else
            {
                State.TextureBuffer.Add(opcode);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// X3.110 inherits ISO 2022 code extension: in a 7-bit environment a C1 control is coded
    /// as ESC Fe. END is C1 8/5, i.e. ESC 4/5 (0x1B 0x45). Callers that consume the final
    /// byte must carry it on the injected END command so serialization reproduces both bytes.
    /// </summary>
    private bool IsEscEnd(byte opcode, BinaryReader reader)
    {
        if (opcode != 0x1B)
        {
            return false;
        }

        if (reader.IsEOF())
        {
            // The ESC may be the first half of an ESC-coded END split across chunks; buffering it
            // as body would corrupt the definition irreversibly.
            NeedMoreData(reader);
            return false;
        }

        return reader.PeekByte() == 0x45;
    }

    private ControlCommand MakeEndCommand(byte opcode, bool escEnd) =>
        new(NaplpsControlCommands.End, State, opcode,
            escEnd ? new NaplpsOperands(new byte[] { 0x45 }) : []);

    /// <summary>
    /// Checks if the given opcode maps to the END control command.
    /// </summary>
    /// <summary>
    /// True when the byte is one of the five C1 definition controls (DEF MACRO, DEFP MACRO,
    /// DEFT MACRO, DEF DRCS, DEF TEXTURE) - each of which terminates a definition in
    /// progress (X3.110 6.2.2-6.2.4).
    /// </summary>
    private bool IsDefinitionCommand(byte opcode)
    {
        var cmdRef = State.InUseTable[opcode];

        if (cmdRef?.CommandType != typeof(ControlCommand) || cmdRef.Parameters.Count != 1)
        {
            return false;
        }

        var c1 = (NaplpsControlCommands)cmdRef.Parameters[0];

        return c1 is DefMacro or DefPMacro or DefTMacro or DefDRCS or DefTexture;
    }

    private bool IsEndCommand(byte opcode)
    {
        var cmdRef = State.InUseTable[opcode];

        return cmdRef?.CommandType == typeof(ControlCommand) &&
               cmdRef.Parameters.Count == 1 &&
               (NaplpsControlCommands)cmdRef.Parameters[0] == End;
    }

    /// <summary>
    /// Reads operand bytes following a command opcode.
    /// </summary>
    private NaplpsOperands ReadOperands(BinaryReader reader, NaplpsOperandType operandType)
    {
        var operands = new NaplpsOperands();

        if (operandType != NaplpsOperandType.None)
        {
            while (true)
            {
                if (IsValidNumericalDataNext(reader))
                {
                    operands.Add(reader.ReadByte());
                    continue;
                }

                // X3.110 5.5: a macro call splices its body into the coded stream at the
                // invocation byte, so operand data flows across the boundary. When the next
                // byte invokes a defined macro, consume it and keep scanning inside the
                // spliced body - a body that begins with numeric data extends THIS command,
                // and a body that begins with an opcode ends the scan there naturally.
                if (reader is SpliceBinaryReader splice && !reader.IsEOF())
                {
                    var next = reader.PeekByte();

                    if (State.IsMacroByte(next) && State.Macros.TryGetValue((char)next, out var macroBody) && macroBody.Length > 0)
                    {
                        reader.ReadByte();
                        State.PendingSingleShift = null; // the single-shift, if any, is consumed here
                        splice.InjectFront(macroBody);
                        continue;
                    }
                }

                break;
            }
        }

        return operands;
    }

    /// <summary>
    /// Re-reads a range of the underlying coded stream. Used to capture the exact real bytes
    /// a splice-boundary command consumed (see <see cref="NaplpsSequence.RawCodedBytes"/>).
    /// </summary>
    private static byte[] ReadRealRange(BinaryReader reader, long start, long end)
    {
        var stream = reader.BaseStream;
        var saved = stream.Position;
        var buffer = new byte[end - start];

        stream.Position = start;
        stream.ReadExactly(buffer);
        stream.Position = saved;

        return buffer;
    }

    /// <summary>
    /// Dispatches a control command to the appropriate handler.
    /// </summary>
    private void HandleControlCommand(NaplpsControlCommands controlCommand, BinaryReader reader, NaplpsOperands additionalParameters, List<NaplpsSequence> commands)
    {
        // Core C0 controls
        if (controlCommand == Escape)
        {
            ControlCommandEscape(reader, additionalParameters);

            // ESC + byte in 0x40-0x5F = 7-bit encoding of C1 control codes.
            // Only dispatch safe state-flag C1 codes — NOT buffer-mode starters
            // (DefMacro, DefTexture, DefDRCS, End) which would swallow subsequent data.
            if (additionalParameters.Count == 1 && additionalParameters[0] >= 0x40 && additionalParameters[0] <= 0x5F)
            {
                byte c1Code = (byte)(additionalParameters[0] + 0x40); // 0x40→0x80, 0x5F→0x9F
                var c1Ref = State.InUseTable[c1Code];
                if (c1Ref?.CommandType == typeof(ControlCommand) && c1Ref.Parameters.Count == 1)
                {
                    var c1Command = (NaplpsControlCommands)c1Ref.Parameters[0];

                    // 7-bit DEF MACRO (ESC 4/0..4/2): read the single-byte macro name that
                    // follows and enter buffered definition mode. The body is captured until the
                    // ESC-encoded END (handled in HandleBufferedByte). MSZB0000-style messaging
                    // templates define one block macro and invoke it N times to tile the screen.
                    if (c1Command == DefMacro || c1Command == DefPMacro || c1Command == DefTMacro)
                    {
                        byte macroType = c1Command == DefMacro ? (byte)0 : c1Command == DefPMacro ? (byte)1 : (byte)2;
                        if (reader.IsEOF())
                        {
                            // The macro name byte has not arrived; entering definition mode now
                            // would swallow it as body.
                            NeedMoreData(reader);
                        }
                        else
                        {
                            byte nameByte = reader.ReadByte();
                            additionalParameters.Add(nameByte); // keep for byte-exact round-trip
                            StartMacroDefinition(new NaplpsOperands(new byte[] { nameByte }), macroType);
                        }
                    }
                    // 7-bit DEF TEXTURE (6.2.4): the byte after the control selects mask A-D
                    // (4/1..4/4); out of range makes the whole command a null operation. The
                    // body is captured until END. Previously the selector byte dangled as an
                    // unknown opcode and the mask body (DOMAIN, the INCREMENTAL POINT tile,
                    // CLEAR...) executed as LIVE drawing commands on the picture.
                    else if (c1Command == DefTexture)
                    {
                        if (reader.IsEOF())
                        {
                            // On the wire the selector may simply not have arrived yet; only a
                            // finished stream makes the selector-less form a real null operation.
                            NeedMoreData(reader);
                        }
                        else
                        {
                            byte maskSelector = reader.ReadByte();
                            additionalParameters.Add(maskSelector); // keep for byte-exact round-trip

                            if (maskSelector >= 0x41 && maskSelector <= 0x44)
                            {
                                State.TextureBeingDefined = maskSelector;
                                State.TextureBuffer.Clear();
                            }
                        }
                    }
                    // DefDRCS/End via ESC keep their prior handling (DRCS is not yet buffered
                    // via ESC; End is consumed inside the definition buffers).
                    // Pass the OUTER additionalParameters into the recursive call so that
                    // operand-consuming C1 commands (e.g. Repeat reads a count byte) append
                    // their bytes to the outer ESC command's operands, preserving byte fidelity.
                    else if (c1Command != DefDRCS && c1Command != End)
                    {
                        HandleControlCommand(c1Command, reader, additionalParameters, commands);
                    }
                }
            }

            State.DoEscape(additionalParameters);
        }
        else if (controlCommand == NonSelectiveReset)
        {
            ControlCommandNonSelectiveReset(reader, additionalParameters);
        }
        else if (controlCommand == ShiftIn)
        {
            State.DoShiftIn();
        }
        else if (controlCommand == ShiftOut)
        {
            State.DoShiftOut();
        }
        else if (controlCommand == Cancel)
        {
            // ANSI X3.110: CAN terminates all currently executing macros immediately.
            // Effect is immediate — not queued. Under spliced expansion the pending
            // injected bytes ARE the executing macros: drop them all.
            State.MacroBeingDefined = null;
            State.MacroBuffer.Clear();

            if (reader is SpliceBinaryReader splice)
            {
                splice.ClearInjected();
            }

            State.IsCancelRequested = true;
        }
        else if (controlCommand == Bell)
        {
            // ANSI X3.110: BEL triggers an audible or visual alert.
            State.BellCount++;
        }
        // Cursor positioning
        else if (controlCommand == ActivePositionSet)
        {
            // ANSI X3.110: APS (0x1C) sets cursor to row/column position.
            HandleActivePositionSet(reader);
        }
        else if (controlCommand == ClearScreen)
        {
            // ANSI X3.110: Clear screen to nominal black in modes 0/1,
            // background color in mode 2. Move cursor to upper left.
            State.Pen = new Vector3(0f, 0.75f - State.CharSize.Y, 0f);
        }
        else if (controlCommand == ActivePositionDown)
        {
            HandleActivePositionDown();
        }
        else if (controlCommand == ActivePositionUp)
        {
            var pen = State.Pen;
            pen.Y += State.CharSize.Y * GetInterrowMultiplier(State.TextInterrowSpacing);
            State.Pen = pen;
        }
        else if (controlCommand == ActivePositionReturn)
        {
            HandleActivePositionReturn();
        }
        else if (controlCommand == ActivePositionForward)
        {
            HandleActivePositionForward();
        }
        else if (controlCommand == ActivePositionBackward)
        {
            HandleActivePositionBackward();
        }
        else if (controlCommand == ActivePositionHome)
        {
            var pen = State.Pen;
            pen.X = State.Field.Left;
            pen.Y = State.Field.Top - State.CharSize.Y;
            State.Pen = pen;
        }
        // Text attributes
        else if (controlCommand == ReverseVideo) { State.IsReverseVideo = true; }
        else if (controlCommand == NormalVideo) { State.IsReverseVideo = false; }
        else if (controlCommand == UnderLineStart) { State.IsUnderline = true; }
        else if (controlCommand == UnderLineStop) { State.IsUnderline = false; }
        else if (controlCommand == BlinkStart) { State.IsBlinkMode = true; }
        else if (controlCommand == BlinkStop) { State.IsBlinkMode = false; }
        else if (controlCommand == ScrollOn) { State.IsScrollMode = true; }
        else if (controlCommand == ScrollOff) { State.IsScrollMode = false; }
        else if (controlCommand == WordWrapOn) { State.IsWordWrapMode = true; }
        else if (controlCommand == WordWrapOff) { State.IsWordWrapMode = false; }
        else if (controlCommand == Protect) { State.IsProtectMode = true; }
        else if (controlCommand == Unprotect) { State.IsProtectMode = false; }
        // Text size
        else if (controlCommand == SmallText) { State.TextSizeMode = 1; State.CharSize = new Vector2(1.0f / 80.0f, 5.0f / 128.0f); }
        else if (controlCommand == MedText) { State.TextSizeMode = 2; State.CharSize = new Vector2(1.0f / 32.0f, 3.0f / 64.0f); }
        else if (controlCommand == NormalText) { State.TextSizeMode = 0; State.CharSize = new Vector2(1.0f / 40.0f, 5.0f / 128.0f); }
        else if (controlCommand == DoubleHeight) { State.TextSizeMode = 3; State.CharSize = new Vector2(1.0f / 40.0f, 10.0f / 128.0f); }
        else if (controlCommand == DoubleSize) { State.TextSizeMode = 4; State.CharSize = new Vector2(2.0f / 40.0f, 10.0f / 128.0f); }
        // Macro/DRCS/texture definitions
        // The macro NAME is the byte following the control (X3.110 6.2.2). The 8-bit C1
        // forms (0x80-0x82) arrive here with no operands read, so consume the name from the
        // stream; the 7-bit ESC forms pre-read it into additionalParameters.
        else if (controlCommand == DefMacro) { StartMacroDefinition(ReadMacroName(reader, additionalParameters), 0); }
        else if (controlCommand == DefPMacro) { StartMacroDefinition(ReadMacroName(reader, additionalParameters), 1); }
        else if (controlCommand == DefTMacro) { StartMacroDefinition(ReadMacroName(reader, additionalParameters), 2); }
        // ANSI X3.110 §6.1.3.3: SS2 invokes G2 into the in-use table for ONE next byte (nonlocking).
        // Spec §5.5 macros are invoked by designating the Macro Set into G1/G2/G3 then transmitting
        // a character from that invoked area — NOT via SS2.
        else if (controlCommand == SingleShiftTwo) { State.DoSingleShiftTwo(); }
        // §6.1.3.4: SS3 — same pattern with G3.
        else if (controlCommand == SingleShiftThree) { State.DoSingleShiftThree(); }
        // §6.1.6.4: SDC — null operation at the presentation layer.
        else if (controlCommand == ServiceDelimiterCharacter) { /* no-op per spec */ }
        else if (controlCommand == DefDRCS) { if (additionalParameters.Count > 0) { State.DrcsStartCode = additionalParameters[0]; State.DrcsBuffer.Clear(); } }
        else if (controlCommand == DefTexture) { if (additionalParameters.Count > 0) { State.TextureBeingDefined = additionalParameters[0]; State.TextureBuffer.Clear(); } }
        else if (controlCommand == Repeat)
        {
            // Repeat command: read the count byte and store it in operands (the actual glyph
            // repetition happens at render time). Advance the parse-time pen across the repeated
            // cells so the FOLLOWING text's state snapshot starts after the repeated run — e.g. a
            // highlighted-space bar (title) must push the text after it, not overprint it.
            if (reader.IsEOF())
            {
                NeedMoreData(reader);
            }
            else
            {
                var countByte = reader.ReadByte();
                additionalParameters.Add(countByte);

                if (_lastCharForRepeat is char c && (countByte & 0x7F) >= 0x40)
                {
                    int repeatCount = countByte & 0x3F;
                    for (int i = 0; i < repeatCount; i++)
                    {
                        AsciiCharCommand.AdvancePen(State, c);
                    }
                }
            }
        }
        // RepeatToEOL doesn't need special handling here - count is calculated at render time
    }

    private void HandleActivePositionSet(BinaryReader reader)
    {
        // Followed by two bytes: row (0x40-0x5F) and column (0x40-0x7F).
        if (Remaining(reader) < 2)
        {
            NeedMoreData(reader);
        }

        if (!reader.IsEOF())
        {
            byte rowByte = reader.ReadByte();

            if (!reader.IsEOF())
            {
                byte colByte = reader.ReadByte();
                int row = (rowByte & 0x3F); // Strip header bits
                int col = (colByte & 0x3F);

                // Position pen: column * charWidth from field left, row * charHeight from field top
                var pen = State.Pen;
                pen.X = State.Field.Left + col * State.CharSize.X;
                pen.Y = State.Field.Top - row * State.CharSize.Y;
                State.Pen = pen;
            }
        }
    }

    /// <summary>
    /// X3.110's format effectors (APR 6.1.2.7, APF 6.1.2.2, APB 6.1.2.1, APD 6.1.2.3) bound
    /// their movement by the active field only when "the full character field corresponding to
    /// the cursor lie[s] entirely within the active field before the movement"; otherwise the
    /// display area (unit screen) bounds apply. COLORBAR's field extends left of the visible
    /// screen: its cursor is never within it, so carriage returns go to the screen's left
    /// edge on the device, not the field's.
    /// </summary>
    private bool CursorCellInField()
    {
        // Membership is the cursor POSITION, half-open at the far edges - not whole-cell
        // containment. Device-verified: the Prodigy message/logon pages return their lines to
        // the field's left margin even though the cell pokes past their shallow fields, while
        // COLORBAR's cursor (right of its off-screen field) returns to the display edge.
        var f = State.Field;
        var pen = State.Pen;

        return f.IsSet
            && pen.X >= f.Left && pen.X < f.Right
            && pen.Y >= f.Bottom && pen.Y < f.Top;
    }

    /// <summary>
    /// Line starts return to the FIELD ORIGIN's X - the authored corner - not the normalized
    /// left edge. Device-verified: COLORBAR's field extends LEFT from an origin at x=0, and
    /// its lines return to 0 (the origin), while the message/logon pages' fields have their
    /// origin at the left margin and return there.
    /// </summary>
    private float LineStartX() => State.Field.Origin.X;

    private void HandleActivePositionDown()
    {
        if (State.AutoWrapJustOccurred)
        {
            State.AutoWrapJustOccurred = false;
            return;
        }

        var pen = State.Pen;
        var newY = pen.Y - State.CharSize.Y * GetInterrowMultiplier(State.TextInterrowSpacing);

        if (State.IsScrollMode)
        {
            // PP3 behavior (FUN_2168_02c6): every APD triggers scroll when scroll mode is on.
            // Direction determined by field position in ScrollImage().
            State.ScrollEventOccurred = true;
            pen.Y = newY < State.Field.Bottom ? State.Field.Bottom : newY;
        }
        else
        {
            // X3.110 6.1.2.3 + 6.2.7.14 (scroll off): an APD whose character field was
            // entirely within the active field and would leave it repositions to the opposite
            // edge so the cell lies entirely within again - the circular window. The
            // entirely-within precondition naturally excludes the one-row Prodigy fields
            // (cell taller than the field), whose APD is a plain newline on the device.
            // Device-verified on MVDI and gated to Prodigy: generic content (icosamp) is
            // authored to flow APD-continued rows straight out of the field's bottom, and
            // the historical renderers let it.
            if (State.SystemType == NaplpsSystemType.Prodigy && CursorCellInField() && newY < State.Field.Bottom)
            {
                newY = State.Field.Top - State.CharSize.Y;
            }

            pen.Y = newY;
            State.ScrollEventOccurred = false;
        }

        // Prodigy's MVDI treats APD as a newline (down + carriage return to the field left), unlike the
        // strict ANSI X3.110 APD which is down-only. Verified against the reference render (e.g. jcpenny-vcr's
        // "4-Head VCR;<APD>cable-capable" renders cable-capable at the field's left margin, not trailing
        // the previous line and mid-word field-wrapping).
        if (State.SystemType == NaplpsSystemType.Prodigy)
        {
            pen.X = LineStartX();
        }

        State.Pen = pen;
    }

    private void HandleActivePositionReturn()
    {
        if (!State.AutoWrapJustOccurred)
        {
            var pen = State.Pen;
            pen.X = LineStartX();
            State.Pen = pen;
        }
    }

    private void HandleActivePositionForward()
    {
        var pen = State.Pen;
        bool inField = CursorCellInField();
        float fieldRight = inField ? State.Field.Right : 1f;
        float fieldLeft = inField ? State.Field.Left : 0f;

        switch (State.TextPath)
        {
            case TextPath.Right:
            {
                pen.X += State.CharSize.X;

                if (fieldRight > fieldLeft && pen.X > fieldRight)
                {
                    pen.X = fieldLeft;
                    pen.Y -= State.CharSize.Y;
                }
            }
            break;

            case TextPath.Left:
            {
                pen.X -= State.CharSize.X;

                if (fieldRight > fieldLeft && pen.X < fieldLeft)
                {
                    pen.X = fieldRight;
                    pen.Y -= State.CharSize.Y;
                }
            }
            break;

            default:
            {
                pen.X += State.CharSize.X;
            }
            break;
        }

        State.Pen = pen;
    }

    private void HandleActivePositionBackward()
    {
        var pen = State.Pen;
        bool inField = CursorCellInField();
        float fieldRight = inField ? State.Field.Right : 1f;
        float fieldLeft = inField ? State.Field.Left : 0f;

        switch (State.TextPath)
        {
            case TextPath.Right:
            {
                pen.X -= State.CharSize.X;

                if (fieldRight > fieldLeft && pen.X < fieldLeft)
                {
                    pen.X = fieldRight - State.CharSize.X;
                    pen.Y += State.CharSize.Y;
                }
            }
            break;

            case TextPath.Left:
            {
                pen.X += State.CharSize.X;

                if (fieldRight > fieldLeft && pen.X > fieldRight)
                {
                    pen.X = fieldLeft + State.CharSize.X;
                    pen.Y += State.CharSize.Y;
                }
            }
            break;

            default:
            {
                pen.X -= State.CharSize.X;
            }
            break;
        }

        State.Pen = pen;
    }

    /// <summary>
    /// Returns operands already carrying the macro name (the 7-bit ESC path pre-reads it),
    /// or consumes the name byte from the stream for the direct 8-bit C1 forms - appending
    /// it to the command's operands so serialization stays byte-exact.
    /// </summary>
    private NaplpsOperands ReadMacroName(BinaryReader reader, NaplpsOperands operands)
    {
        if (operands.Count == 0)
        {
            if (reader.IsEOF())
            {
                // The 8-bit name byte has not arrived; entering definition mode now would
                // swallow it as body. Defer the whole DEF to the next feed.
                NeedMoreData(reader);
            }
            else
            {
                operands.Add(reader.ReadByte());
            }
        }

        return operands;
    }

    private void StartMacroDefinition(NaplpsOperands operands, byte macroType)
    {
        if (operands.Count > 0)
        {
            State.MacroBeingDefined = (char)operands[0];
            State.MacroDefType = macroType;
            State.MacroBuffer.Clear();
        }
    }

    private void ExecuteMacro(NaplpsOperands operands, List<NaplpsSequence> commands)
    {
        if (operands.Count > 0)
        {
            var macroName = (char)operands[0];

            if (State.Macros.TryGetValue(macroName, out var macroData))
            {
                using var macroStream = new MemoryStream(macroData);
                using var macroReader = new BinaryReader(macroStream);
                // ANSI X3.110 §6.1.6.3: pass isMacroExpansion = true so a CAN inside the
                // macro body terminates it immediately. The outer ReadStream resumes
                // at the next byte after the macro invocation. Expansion sequences are
                // synthetic: they render, but only the invocation byte is coded input.
                foreach (var seq in ReadStream(macroReader, isMacroExpansion: true))
                {
                    seq.IsSynthetic = true;
                    commands.Add(seq);
                }

                State.IsCancelRequested = false;
            }
        }
    }

    /// <summary>
    /// Attempts to instantiate a command from its type and parameters.
    /// Returns null if instantiation fails.
    /// </summary>
    private NaplpsCommand? TryInstantiateCommand(
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
        Type commandType,
        List<object> commandParameters, byte opcode, NaplpsOperands additionalParameters, BinaryReader reader)
    {
        var finalCommandParams = commandParameters.Concat([State, opcode, additionalParameters]).ToArray();

        try
        {
            if (Activator.CreateInstance(commandType, finalCommandParams) is not NaplpsCommand cmd)
            {
                RecordError(NaplpsErrorSeverity.Error, NaplpsErrorType.CommandInstantiationFailed, $"Failed to instantiate {commandType.Name}", opcode, reader.BaseStream.Position);
                return null;
            }

            return cmd;
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            RecordError(NaplpsErrorSeverity.Error, NaplpsErrorType.CommandInstantiationFailed, $"{commandType.Name} constructor threw: {ex.InnerException?.Message ?? ex.Message}", opcode, reader.BaseStream.Position);
            return null;
        }
    }

    private bool IsValidNumericalDataNext(BinaryReader reader)
    {
        if (reader.IsEOF())
        {
            // X3.110 operand lists are terminated by the next non-numeric byte, never by a
            // length. A list that reaches the frontier may continue in the next chunk.
            NeedMoreData(reader);
            return false;
        }

        var nextByte = reader.PeekByte();

        var operandReference = State.InUseTable[nextByte];

        var isNumericalData = operandReference.CommandType == typeof(NumericalDataCommand);

        return isNumericalData;
    }

    private void ControlCommandEscape(BinaryReader reader, NaplpsOperands additionalParameters)
    {
        bool isEscape = true;

        while (isEscape)
        {
            if (reader.IsEOF())
            {
                // No final byte yet: the rest of the escape sequence may still be in flight.
                NeedMoreData(reader);
                break;
            }

            var peakValue = reader.PeekByte();

            // ANSI X3.110-1983 §4.3.3: an ESC sequence consists of zero or more
            // intermediate bytes (0x20-0x2F) followed by a single final byte. The final
            // byte is in 0x30-0x7E for 7-bit transmission, or 0xA0-0xFE for 8-bit.
            // Earlier parser only handled the 7-bit final range — so 8-bit ESC sequences
            // like `ESC 0xDF` (= 7-bit `ESC 0x5F`, "Designate other coding system") leaked
            // their final byte into the next command-decode iteration, producing a stray
            // bare NaplpsCommand. 82 occurrences of 0xDF across the corpus turned out to
            // be exactly this case in dow-jones-frame.nap and similar Prodigy files.
            bool isIntermediate = peakValue >= 0x20 && peakValue <= 0x2F;
            bool isFinal7Bit = peakValue >= 0x30 && peakValue <= 0x7E;
            bool isFinal8Bit = peakValue >= 0xA0 && peakValue <= 0xFE;

            if (isIntermediate || isFinal7Bit || isFinal8Bit)
            {
                additionalParameters.Add(reader.ReadByte());
                isEscape = !(isFinal7Bit || isFinal8Bit); // Stop at any final byte
            }
            else
            {
                isEscape = false;
            }
        }
    }

    private void ControlCommandNonSelectiveReset(BinaryReader reader, NaplpsOperands additionalParameters)
    {
        // NSR's cursor operand is OPTIONAL, so whether it is present can only be known once
        // enough bytes have arrived. Decide that BEFORE the resets below - unwinding after them
        // would leave a wholly reset state behind for the re-decode to inherit.
        var remaining = Remaining(reader);

        if (remaining == 0 || (remaining == 1 && reader.PeekByte() is >= 0x40 and <= 0x7F))
        {
            NeedMoreData(reader);
        }

        // ANSI X3.110 NSR: Reset G0-G3, C0, C1 to defaults; reset GL/GR
        State.Reset();
        State.DoShiftIn();

        // Reset DOMAIN parameters to defaults
        State.Dimensionality = 2;
        State.MultiByteValue = 3;
        State.SingleByteValue = 1;
        State.LogicalPel = new Vector2(0f, 0f);

        // Reset text parameters to defaults
        State.TextRotation = TextRotation.Zero;
        State.TextPath = TextPath.Right;
        State.TextSpacing = TextSpacing.One;
        State.TextInterrowSpacing = TextInterrowSpacing.One;
        State.TextMoveAttributes = TextMoveAttributes.MoveTogether;
        State.TextCursorStyle = TextCursorStyle.Underscore;
        State.CharSize = new Vector2(1.0f / 40.0f, 5.0f / 128.0f);
        State.TextSizeMode = 0;
        State.IsReverseVideo = false;
        State.IsUnderline = false;
        State.IsWordWrapMode = false;
        State.IsScrollMode = false;

        // Reset active field to unit screen
        State.Field = new NaplpsField();

        // Reset texture attributes (programmable masks are NOT cleared)
        State.Texture = new NaplpsTexture();

        // Reset color mode to 0 and drawing color to nominal white
        // Palette is NOT cleared by NSR
        State.ColorMode = 0;
        State.Foreground = new NaplpsColor(255, 255, 255);
        State.ColorMapForeground = 0x07; // Nominal white

        // Reset drawing position
        State.Pen = new Vector3(0f, 0f, 0f);

        // NSR cursor positioning: if two bytes 0x40-0x7F follow, decode row/column.
        // Origin is UPPER LEFT (row 0, col 0 = top-left) - different from 0x1C which uses bottom-left.
        // Capture both bytes into additionalParameters so the serializer re-emits them on ToBytes().
        if (reader.BytesRemaining() >= 2)
        {
            // PeekChar() UTF-8-decodes the upcoming bytes and THROWS on a multi-byte lead
            // (e.g. NSR followed by 0xF0..): peek the raw byte via the stream instead.
            var peek1 = reader.PeekByte();

            if (peek1 >= 0x40 && peek1 <= 0x7F)
            {
                byte rowByte = reader.ReadByte();
                additionalParameters.Add(rowByte);
                int peek2 = reader.PeekByte();

                if (peek2 >= 0x40 && peek2 <= 0x7F)
                {
                    byte colByte = reader.ReadByte();
                    additionalParameters.Add(colByte);

                    // Extract row/column from bits 6-1 (6 data bits each)
                    int row = (rowByte & 0x3F);
                    int col = (colByte & 0x3F);

                    // Convert from upper-left origin row/col to NAPLPS normalized coords
                    // Row 0 = top of visible display (Y = 0.75 in NAPLPS)
                    // Using default 40x19 visible grid (char field 1/40 x 5/128)
                    float penX = col * (1.0f / 40.0f);
                    float penY = 0.75f - (row * (5.0f / 128.0f));

                    State.Pen = new Vector3(penX, penY, 0f);
                }
            }
        }
    }

    private static float GetInterrowMultiplier(TextInterrowSpacing spacing) => spacing switch
    {
        TextInterrowSpacing.One => 1.0f,
        TextInterrowSpacing.FiveQuarters => 1.25f,
        TextInterrowSpacing.ThreeHalves => 1.5f,
        TextInterrowSpacing.Two => 2.0f,
        _ => 1.0f
    };

    /// <summary>
    /// Parses DRCS bitmap data and stores character definitions.
    /// DRCS format: each character is an 8x10 bitmap (standard),
    /// encoded as 10 bytes (one byte per row, 8 bits per pixel).
    /// </summary>
    private void ParseDrcsData(byte startCode, List<byte> data)
    {
        if (data.Count == 0)
        {
            // Empty definition = reset to space character
            State.DrcsCharacters.Remove(startCode);
            return;
        }

        // Defensive guard against malformed files where a DRCS body contains a DEF DRCS
        // command for another character — the inner ParseDrcsData would recurse via
        // ReadStream and could stack-overflow on adversarial input. Spec is silent on the
        // recursion limit, but no real-world file does this; cap it to a small constant
        // and skip silently if exceeded (the partially-decoded bitmap survives).
        if (_drcsRecursionDepth >= MaxDrcsRecursionDepth)
        {
            return;
        }

        // ANSI X3.110: DRCS definitions are NAPLPS command streams rendered to an
        // offscreen monochrome bitmap. The bitmap aspect ratio matches the character
        // field dimensions at DEF DRCS time.

        // Determine offscreen bitmap size from character field aspect ratio
        float charW = Math.Abs(State.CharSize.X);
        float charH = Math.Abs(State.CharSize.Y);
        float aspect = charW > 0 && charH > 0 ? charW / charH : 0.625f; // Default 5/8

        // Use a reasonable resolution (larger = more detail, slower)
        int bitmapHeight = 32;
        int bitmapWidth = Math.Max(8, (int)(bitmapHeight * aspect));
        var offscreenSize = new Size(bitmapWidth, bitmapHeight);

        // Try to parse as NAPLPS commands first
        bool parsedAsCommands = false;

        _drcsRecursionDepth++;
        try
        {
            using var stream = new MemoryStream(data.ToArray());
            using var reader = new BinaryReader(stream);

            // Save pen position (spec: drawing point set to 0,0 after DRCS)
            var savedPen = State.Pen;
            State.Pen = new Vector3(0, 0, 0);

            var drcsCommands = ReadStream(reader);

            if (drcsCommands.Count > 0)
            {
                // Render commands to offscreen monochrome image
                using var offscreen = new Image<Rgba32>(bitmapWidth, bitmapHeight);
                offscreen.Mutate(ctx => ctx.Fill(SixLabors.ImageSharp.Color.Black));

                Drawing.Drawable.LivePalette = State.ColorMap;

                foreach (var (command, state) in drcsCommands)
                {
                    var drawable = Drawing.DrawContext.ConvertToDrawable(command, state);
                    drawable?.Draw(offscreen, state, offscreenSize);
                }

                Drawing.Drawable.LivePalette = null;

                // Convert to monochrome bitmap (any non-black pixel = set)
                var bitmap = new bool[bitmapHeight, bitmapWidth];

                for (int y = 0; y < bitmapHeight; y++)
                {
                    for (int x = 0; x < bitmapWidth; x++)
                    {
                        var pixel = offscreen[x, y];
                        bitmap[y, x] = pixel.R > 10 || pixel.G > 10 || pixel.B > 10;
                    }
                }

                State.DrcsCharacters[startCode] = bitmap;
                parsedAsCommands = true;
            }

            // Spec: drawing point set to (0,0) after DRCS definition
            State.Pen = new Vector3(0, 0, 0);
        }
        catch
        {
            // If NAPLPS parsing fails, fall through to raw bitmap interpretation
        }
        finally
        {
            _drcsRecursionDepth--;
        }

        if (!parsedAsCommands)
        {
            // Fallback: interpret as raw 8x10 bitmap data (legacy/simple DRCS)
            const int charWidth = 8;
            const int charHeight = 10;
            const int bytesPerChar = charHeight;

            var charCode = startCode;
            var index = 0;

            while (index + bytesPerChar <= data.Count)
            {
                var bitmap = new bool[charHeight, charWidth];

                for (int row = 0; row < charHeight && index < data.Count; row++)
                {
                    byte rowByte = data[index++];

                    for (int col = 0; col < charWidth; col++)
                    {
                        bitmap[row, col] = (rowByte & (0x80 >> col)) != 0;
                    }
                }

                State.DrcsCharacters[charCode] = bitmap;
                charCode++;
            }
        }
    }

    /// <summary>
    /// Parses texture pattern data and stores the mask definition.
    /// Texture patterns are bitmaps used for fill patterns.
    /// </summary>
    private void ParseTextureData(byte maskId, List<byte> data)
    {
        if (data.Count == 0)
        {
            return;
        }

        // Determine pattern size from data length
        // Common sizes are 8x8, 16x16, etc.
        int size = (int)Math.Sqrt(data.Count * 8);

        if (size < 1)
        {
            size = 8;
        }

        var pattern = new bool[size, size];
        int bitIndex = 0;

        for (int row = 0; row < size && bitIndex / 8 < data.Count; row++)
        {
            for (int col = 0; col < size && bitIndex / 8 < data.Count; col++)
            {
                int byteIndex = bitIndex / 8;
                int bitOffset = 7 - (bitIndex % 8);
                pattern[row, col] = (data[byteIndex] & (1 << bitOffset)) != 0;
                bitIndex++;
            }
        }

        // Store the pattern in the appropriate mask slot
        switch (maskId)
        {
            case 0:
            {
                State.TextureMaskA = pattern;
            }
            break;

            case 1:
            {
                State.TextureMaskB = pattern;
            }
            break;

            case 2:
            {
                State.TextureMaskC = pattern;
            }
            break;

            case 3:
            {
                State.TextureMaskD = pattern;
            }
            break;
        }
    }
}
