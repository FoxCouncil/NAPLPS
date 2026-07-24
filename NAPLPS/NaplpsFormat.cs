// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace NAPLPS;

public enum NaplpsSystemType
{
    /// <summary>Standard NAPLPS (version 709) with default color map</summary>
    NAPLPS,

    /// <summary>Original Telidon format (version 699) - files starting with 0x0E (Shift-Out)</summary>
    Telidon,

    /// <summary>Prodigy-style files (8-bit, start with A1 C8 domain command)</summary>
    Prodigy
}

public partial class NaplpsFormat
{
    public bool IsErrored => Errors.Any(e => e.Severity == NaplpsErrorSeverity.Error);

    public bool Is8Bit => !Is7Bit;

    public bool Is7Bit { get; private set; } = true;

    public bool IsValid { get; private set; }

    /// <summary>Detected system type based on file header patterns</summary>
    public NaplpsSystemType SystemType { get; private set; } = NaplpsSystemType.NAPLPS;

    /// <summary>If we are streaming, we'll assume there is no end and wait indefinately until more data comes in</summary>
    public bool IsStreaming { get; private set; } = false;

    public List<NaplpsError> Errors => State.Errors;

    public List<NaplpsSequence> Commands { get; } = [];

    /// <summary>
    /// Eventually this doesn't need to be in the Format class, but for now it is; considering:
    /// - It's not part of the NAPLPS specification.
    /// - It's implimentation dependent and can vary between different NAPLPS systems.
    /// - We'll eventually want to support different rendering styles for different systems.
    /// </summary>
    public NaplpsState State { get; }

    /// <summary>Last spacing character parsed, so a following REPEAT can advance the pen.</summary>
    private char? _lastCharForRepeat;

    private NaplpsFormat(BinaryReader reader, NaplpsSystemType? forcedSystemType = null) : this(reader, new(), forcedSystemType) { }

    private NaplpsFormat(BinaryReader reader, NaplpsState state, NaplpsSystemType? forcedSystemType = null)
    {
        State = state;

        // Detect system type from header before parsing, unless the caller forces one. Forcing
        // must happen BEFORE ApplySystemDefaults so the per-command state snapshots built during
        // ReadStream carry the correct color map and text metrics (e.g. a known-Prodigy corpus
        // whose files lack the A1 C8 domain marker and would otherwise parse as generic NAPLPS).
        SystemType = forcedSystemType ?? DetectSystemType(reader);
        ApplySystemDefaults();

        Commands = ReadStream(reader);

        IsValid = true;
    }

    /// <summary>
    /// Bare constructor: creates an empty format with the given state and no commands.
    /// Used by the Telidraw compiler's BareFormat mode where the .td source is the
    /// complete byte specification (no CAN+NSR sentinels added).
    /// </summary>
    internal NaplpsFormat(NaplpsState state)
    {
        State = state;
    }

    /// <summary>
    /// Detects the NAPLPS system type based on file header patterns.
    /// - Telidon (699): First byte is 0x0E (Shift-Out) - original 1978 hardware format
    /// - Prodigy: First two bytes are A1 C8 (Domain command in 8-bit mode)
    /// - Standard NAPLPS (709): Everything else
    /// </summary>
    private static NaplpsSystemType DetectSystemType(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 1)
        {
            return NaplpsSystemType.NAPLPS;
        }

        var position = reader.BaseStream.Position;
        var firstByte = reader.ReadByte();

        // Telidon (version 699): starts with 0x0E (Shift-Out command)
        // Original format from 1978 Telidon hardware
        if (firstByte == 0x0E)
        {
            reader.BaseStream.Position = position;
            return NaplpsSystemType.Telidon;
        }

        // Need second byte for Prodigy detection
        if (reader.BaseStream.Length < 2)
        {
            reader.BaseStream.Position = position;
            return NaplpsSystemType.NAPLPS;
        }

        // Prodigy-style: starts with A1 C8 (Domain command with specific operand) —
        // possibly behind the CAN+NSR sentinels that NaplpsFormat.New and the Telidraw
        // compiler prepend. Without skipping them, a Prodigy file reloaded through .td
        // loses detection and renders with generic metrics (issue #41).
        var probe = firstByte;
        var skipped = 0;
        while ((probe == 0x18 || probe == 0x1F) && skipped < 8 && reader.BaseStream.Position < reader.BaseStream.Length)
        {
            probe = reader.ReadByte();
            skipped++;
        }

        if (probe == 0xA1 && reader.BaseStream.Position < reader.BaseStream.Length && reader.ReadByte() == 0xC8)
        {
            reader.BaseStream.Position = position;
            return NaplpsSystemType.Prodigy;
        }

        reader.BaseStream.Position = position; // Reset to start
        return NaplpsSystemType.NAPLPS;
    }

    /// <summary>
    /// Applies system-specific defaults (color maps, etc.) based on detected system type.
    /// </summary>
    private void ApplySystemDefaults()
    {
        State.SystemType = SystemType;

        switch (SystemType)
        {
            case NaplpsSystemType.Prodigy:
            State.ColorMap = new Dictionary<byte, NaplpsColor>(NaplpsState.ColorMapProdigyDefaults);
            break;

            case NaplpsSystemType.Telidon:
            // Telidon v699: higher default coordinate precision, restricted PDI set
            State.MultiByteValue = 4;
            NaplpsState.TelidonPDISet.CopyTo(State.InUseTable, NaplpsState.GRight);
            break;

            case NaplpsSystemType.NAPLPS:
            default:
            // Default color map is already set in NaplpsState
            break;
        }
    }

    public static NaplpsFormat FromFile(string fullpath, NaplpsSystemType? forcedSystemType = null)
    {
        var data = File.ReadAllBytes(fullpath);

        return FromBytes(data, forcedSystemType);
    }

    public static NaplpsFormat New(NaplpsSystemType systemType = NaplpsSystemType.NAPLPS, int colorCapacity = 16)
    {
        var state = new NaplpsState(colorCapacity);

        if (systemType == NaplpsSystemType.Prodigy)
        {
            state.ColorMap = new Dictionary<byte, NaplpsColor>(NaplpsState.ColorMapProdigyDefaults);
        }

        var newFile = new NaplpsFormat(state)
        {
            SystemType = systemType
        };

        if (systemType == NaplpsSystemType.Prodigy)
        {
            // Prodigy files start with Domain command (A1 C8) for auto-detection
            newFile.AddCommand(0xA1, new NaplpsOperands([0xC8]));
        }

        newFile.AddControlCommand(Cancel);
        newFile.AddControlCommand(NonSelectiveReset);

        return newFile;
    }

    public void Save(string fullpath)
    {
        File.WriteAllBytes(fullpath, ToBytes());
    }

    /// <summary>
    /// Adds a PDI command to the end of the command list.
    /// Looks up the command type from the InUseTable, clones state, instantiates via reflection.
    /// </summary>
    public void AddCommand(byte opcode, NaplpsOperands? operands = null)
    {
        operands ??= [];

        var commandReference = State.InUseTable[opcode];

        if (commandReference == null)
        {
            return;
        }

        var currentState = State.Clone();
        var commandType = commandReference.CommandType ?? typeof(NaplpsCommand);
        var commandParameters = commandReference.Parameters;

        var finalCommandParams = commandParameters.Concat([State, opcode, operands]).ToArray();

        if (Activator.CreateInstance(commandType, finalCommandParams) is NaplpsCommand command)
        {
            Commands.Add(new NaplpsSequence(currentState, command));
        }
    }

    /// <summary>
    /// Inserts a PDI command at the specified index.
    /// </summary>
    public void InsertCommand(int index, byte opcode, NaplpsOperands? operands = null)
    {
        operands ??= [];

        var commandReference = State.InUseTable[opcode];

        if (commandReference == null)
        {
            return;
        }

        var currentState = State.Clone();
        var commandType = commandReference.CommandType ?? typeof(NaplpsCommand);
        var commandParameters = commandReference.Parameters;

        var finalCommandParams = commandParameters.Concat([State, opcode, operands]).ToArray();

        if (Activator.CreateInstance(commandType, finalCommandParams) is NaplpsCommand command)
        {
            Commands.Insert(index, new NaplpsSequence(currentState, command));
        }
    }

    /// <summary>
    /// Removes the command at the specified index.
    /// </summary>
    public void RemoveCommand(int index)
    {
        if (index >= 0 && index < Commands.Count)
        {
            Commands.RemoveAt(index);
        }
    }

    /// <summary>
    /// Creates a NaplpsFormat from raw bytes by parsing them through the standard pipeline.
    /// Useful for re-parsing after edits (undo/redo) to rebuild the state chain.
    /// </summary>
    public static NaplpsFormat FromBytes(byte[] data, NaplpsSystemType? forcedSystemType = null)
    {
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);

        return new NaplpsFormat(reader, forcedSystemType);
    }

    /// <summary>
    /// Serializes all commands to a byte array.
    /// </summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        foreach (var command in Commands)
        {
            // Parser-materialized (macro expansion) sequences are not part of the coded stream.
            if (command.IsSynthetic)
            {
                continue;
            }

            writer.Write(command.Command.OpCode);

            foreach (var operand in command.Command.Operands)
            {
                writer.Write(operand);
            }
        }

        writer.Flush();

        return stream.ToArray();
    }

    private void AddControlCommand(NaplpsControlCommands command, NaplpsOperands? operands = null)
    {
        var newCommand = new ControlCommand(command, State, (byte)command, operands ?? []);

        if (newCommand.IsValid)
        {
            Commands.Add(new NaplpsSequence(newCommand.State.Clone(), newCommand));
        }
        else
        {
            RecordError(NaplpsErrorSeverity.Error, NaplpsErrorType.InvalidCommand, $"Control command {command} produced an invalid NaplpsCommand");
        }
    }

    private void RecordError(NaplpsErrorSeverity severity, NaplpsErrorType type, string message, byte? opcode = null, long? streamPosition = null)
    {
        State.RecordError(severity, type, message, opcode, streamPosition);
    }

    private List<NaplpsSequence> ReadStream(BinaryReader reader, bool isMacroExpansion = false)
    {
        var commands = new List<NaplpsSequence>();

        try
        {
            while (!reader.IsEOF())
            {
                // ANSI X3.110 §6.1.6.3: CAN terminates currently executing macros immediately.
                // Only check inside macro expansion; at top level CAN is a no-op (the flag
                // is cleared by the outer call when it returns).
                if (isMacroExpansion && State.IsCancelRequested)
                {
                    State.IsCancelRequested = false;
                    break;
                }

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
                    continue;
                }

                // Macro invocation: a byte resolved through a G-set designated as the macro set
                // (via SS3/LS3 into a macro-designated slot) expands that macro at parse time,
                // injecting its body commands here, instead of drawing a character.
                if (State.IsMacroByte(opcode))
                {
                    State.PendingSingleShift = null; // the single-shift, if any, is consumed here

                    // X3.110 section 5.5: the coded stream carries only the single invocation
                    // byte. Preserve it as a raw (non-drawing) command so ToBytes round-trips;
                    // the expansion added below is presentation output, marked synthetic.
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, opcode, [])));
                    ExecuteMacro(new NaplpsOperands(new byte[] { opcode }), commands);
                    continue;
                }

                // Use ResolveByte so a pending SS2/SS3 single-shift gets consumed by this byte.
                var commandReference = State.ResolveByte(opcode);

                if (commandReference == null)
                {
                    RecordError(NaplpsErrorSeverity.Error, NaplpsErrorType.UnknownOpcode, "Unknown opcode in InUseTable", opcode, reader.BaseStream.Position - 1);
                    // Preserve the unknown byte so ToBytes round-trips. The renderer won't
                    // draw it (not IDrawable), but the byte survives serialization.
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, opcode, [])));
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
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, opcode, [])));
                    continue;
                }

                // Clone the current state before executing the command
                var currentState = State.Clone();

                if (commandType == typeof(ControlCommand) && commandParameters.Count == 1)
                {
                    HandleControlCommand((NaplpsControlCommands)commandParameters[0], reader, additionalParameters, commands);

                    // Re-clone AFTER control command so the sequence's state snapshot
                    // reflects changes made by the handler (cursor position, scroll flag, etc.)
                    currentState = State.Clone();
                    State.ScrollEventOccurred = false;
                }

                var command = TryInstantiateCommand(commandType, commandParameters, opcode, additionalParameters, reader);

                if (command != null)
                {
                    commands.Add(new NaplpsSequence(currentState, command));

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
        }
        catch (EndOfStreamException)
        {
            RecordError(NaplpsErrorSeverity.Error, NaplpsErrorType.UnexpectedEndOfStream, "Stream ended unexpectedly during parsing");
        }

        return commands;
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

            if (escEnd || IsEndCommand(opcode))
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

                // Inject the END command itself, carrying the ESC form's final byte when present.
                commands.Add(new NaplpsSequence(State.Clone(), MakeEndCommand(opcode, escEnd)));

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
            if (IsEndCommand(opcode))
            {
                ParseTextureData(State.TextureBeingDefined.Value, State.TextureBuffer);
                State.TextureBeingDefined = null;

                foreach (var b in State.TextureBuffer)
                {
                    commands.Add(new NaplpsSequence(State.Clone(), new NaplpsCommand(State, b, [])));
                }

                State.TextureBuffer.Clear();
                commands.Add(new NaplpsSequence(State.Clone(), new ControlCommand(NaplpsControlCommands.End, State, opcode, [])));
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
    private static bool IsEscEnd(byte opcode, BinaryReader reader) =>
        opcode == 0x1B && !reader.IsEOF() && reader.PeekByte() == 0x45;

    private ControlCommand MakeEndCommand(byte opcode, bool escEnd) =>
        new(NaplpsControlCommands.End, State, opcode,
            escEnd ? new NaplpsOperands(new byte[] { 0x45 }) : []);

    /// <summary>
    /// Checks if the given opcode maps to the END control command.
    /// </summary>
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
            while (IsValidNumericalDataNext(reader))
            {
                operands.Add(reader.ReadByte());
            }
        }

        return operands;
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
                        if (!reader.IsEOF())
                        {
                            byte nameByte = reader.ReadByte();
                            additionalParameters.Add(nameByte); // keep for byte-exact round-trip
                            StartMacroDefinition(new NaplpsOperands(new byte[] { nameByte }), macroType);
                        }
                    }
                    // DefDRCS/DefTexture/End via ESC keep their prior handling (DRCS/Texture are
                    // not yet buffered via ESC; End is consumed inside the macro buffer).
                    // Pass the OUTER additionalParameters into the recursive call so that
                    // operand-consuming C1 commands (e.g. Repeat reads a count byte) append
                    // their bytes to the outer ESC command's operands, preserving byte fidelity.
                    else if (c1Command != DefDRCS && c1Command != DefTexture && c1Command != End)
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
            // Effect is immediate — not queued.
            State.MacroBeingDefined = null;
            State.MacroBuffer.Clear();
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
            pen.X = State.Field.Origin.X;
            pen.Y = State.Field.Origin.Y + State.Field.Dimensions.Y - State.CharSize.Y;
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
        else if (controlCommand == DefMacro) { StartMacroDefinition(additionalParameters, 0); }
        else if (controlCommand == DefPMacro) { StartMacroDefinition(additionalParameters, 1); }
        else if (controlCommand == DefTMacro) { StartMacroDefinition(additionalParameters, 2); }
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
            if (!reader.IsEOF())
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
                pen.X = State.Field.Origin.X + col * State.CharSize.X;
                pen.Y = State.Field.Origin.Y + State.Field.Dimensions.Y - row * State.CharSize.Y;
                State.Pen = pen;
            }
        }
    }

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
            pen.Y = newY < State.Field.Origin.Y ? State.Field.Origin.Y : newY;
        }
        else
        {
            pen.Y = newY;
            State.ScrollEventOccurred = false;
        }

        // Prodigy's MVDI treats APD as a newline (down + carriage return to the field left), unlike the
        // strict ANSI X3.110 APD which is down-only. Verified against the reference render (e.g. jcpenny-vcr's
        // "4-Head VCR;<APD>cable-capable" renders cable-capable at the field's left margin, not trailing
        // the previous line and mid-word field-wrapping).
        if (State.SystemType == NaplpsSystemType.Prodigy)
        {
            pen.X = State.Field.Origin.X;
        }

        State.Pen = pen;
    }

    private void HandleActivePositionReturn()
    {
        if (!State.AutoWrapJustOccurred)
        {
            var pen = State.Pen;
            pen.X = State.Field.Origin.X;
            State.Pen = pen;
        }
    }

    private void HandleActivePositionForward()
    {
        var pen = State.Pen;
        float fieldRight = State.Field.Origin.X + State.Field.Dimensions.X;
        float fieldLeft = State.Field.Origin.X;

        switch (State.TextPath)
        {
            case TextPath.Right:
            {
                pen.X += State.CharSize.X;

                if (State.Field.Dimensions.X > 0 && pen.X > fieldRight)
                {
                    pen.X = fieldLeft;
                    pen.Y -= State.CharSize.Y;
                }
            }
            break;

            case TextPath.Left:
            {
                pen.X -= State.CharSize.X;

                if (State.Field.Dimensions.X > 0 && pen.X < fieldLeft)
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
        float fieldRight = State.Field.Origin.X + State.Field.Dimensions.X;
        float fieldLeft = State.Field.Origin.X;

        switch (State.TextPath)
        {
            case TextPath.Right:
            {
                pen.X -= State.CharSize.X;

                if (State.Field.Dimensions.X > 0 && pen.X < fieldLeft)
                {
                    pen.X = fieldRight - State.CharSize.X;
                    pen.Y += State.CharSize.Y;
                }
            }
            break;

            case TextPath.Left:
            {
                pen.X += State.CharSize.X;

                if (State.Field.Dimensions.X > 0 && pen.X > fieldRight)
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
            return false;
        }

        var nextByte = reader.PeekByte();

        var operandReference = State.InUseTable[nextByte];

        var isNumericalData = operandReference.CommandType == typeof(NumericalDataCommand);

        return isNumericalData;
    }

    private static void ControlCommandEscape(BinaryReader reader, NaplpsOperands additionalParameters)
    {
        bool isEscape = true;

        while (isEscape && !reader.IsEOF())
        {
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
        if (reader.BaseStream.Position + 2 <= reader.BaseStream.Length)
        {
            var peek1 = reader.PeekChar();

            if (peek1 >= 0x40 && peek1 <= 0x7F)
            {
                byte rowByte = reader.ReadByte();
                additionalParameters.Add(rowByte);
                int peek2 = reader.PeekChar();

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
    private int _drcsRecursionDepth;
    private const int MaxDrcsRecursionDepth = 4;

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
