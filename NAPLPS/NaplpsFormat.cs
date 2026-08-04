// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

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

/// <summary>
/// The editor driver over <see cref="NaplpsDecoder"/>: a whole coded stream, its complete
/// command list, and the per-command state snapshots that let any prefix be re-rendered or
/// edited. Parsing itself lives in the decoder; this class owns retention and serialization.
/// </summary>
public partial class NaplpsFormat
{
    public bool IsErrored => Errors.Any(e => e.Severity == NaplpsErrorSeverity.Error);

    public bool Is8Bit => !Is7Bit;

    public bool Is7Bit => _decoder?.Is7Bit ?? true;

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

    /// <summary>The codec that produced <see cref="Commands"/>; null for a bare format.</summary>
    private readonly NaplpsDecoder? _decoder;

    private NaplpsFormat(BinaryReader reader, NaplpsSystemType? forcedSystemType = null) : this(reader, new(), forcedSystemType) { }

    private NaplpsFormat(BinaryReader reader, NaplpsState state, NaplpsSystemType? forcedSystemType = null)
    {
        State = state;

        // Detect system type from header before parsing, unless the caller forces one. Forcing
        // must happen BEFORE ApplySystemDefaults so the per-command state snapshots built during
        // decoding carry the correct color map and text metrics (e.g. a known-Prodigy corpus
        // whose files lack the A1 C8 domain marker and would otherwise parse as generic NAPLPS).
        SystemType = forcedSystemType ?? DetectSystemType(reader);
        ApplySystemDefaults();

        _decoder = new NaplpsDecoder(State);
        // Parse through a splice reader so macro invocations expand by injecting body bytes
        // into the coded stream at the invocation byte (X3.110 5.5).
        Commands = _decoder.ReadStream(new SpliceBinaryReader(reader.BaseStream));

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
    /// Streaming shell: a format that fronts a LIVE decoder instead of a finished parse.
    /// <see cref="State"/> is the decoder's own state and <see cref="Commands"/> starts empty,
    /// growing as the driver appends what each feed completes. Exists so the renderer sees the
    /// same shape on the wire path as on the file path; the retention and serialization this
    /// class provides for the editor are the driver's business here.
    /// See <see cref="NaplpsStreamSession"/>.
    /// </summary>
    internal NaplpsFormat(NaplpsDecoder decoder, NaplpsSystemType systemType)
    {
        State = decoder.State;
        _decoder = decoder;
        SystemType = systemType;
        IsStreaming = true;
        IsValid = true;
    }

    /// <summary>
    /// Incremental header probe shared with the streaming session: the same rules as
    /// <see cref="DetectSystemType"/> (Telidon's leading 0x0E; A1 C8 Prodigy marker
    /// possibly behind up to eight CAN/NSR sentinel bytes - issue #41), but able to say
    /// "undecided" (null) while the header could still resolve differently as more bytes
    /// arrive. With <paramref name="atStreamEnd"/> it always decides.
    /// </summary>
    internal static NaplpsSystemType? TryDetectSystemType(IReadOnlyList<byte> header, bool atStreamEnd)
    {
        if (header.Count == 0)
        {
            return atStreamEnd ? NaplpsSystemType.NAPLPS : null;
        }

        if (header[0] == 0x0E)
        {
            return NaplpsSystemType.Telidon;
        }

        var i = 0;
        var skipped = 0;

        while (i < header.Count && (header[i] == 0x18 || header[i] == 0x1F) && skipped < 8)
        {
            i++;
            skipped++;
        }

        if (i >= header.Count)
        {
            // Nothing but (skippable) sentinels so far; the marker could still follow.
            return atStreamEnd ? NaplpsSystemType.NAPLPS : null;
        }

        if (header[i] != 0xA1)
        {
            return NaplpsSystemType.NAPLPS;
        }

        if (i + 1 >= header.Count)
        {
            // A1 seen, C8 could be the next byte.
            return atStreamEnd ? NaplpsSystemType.NAPLPS : null;
        }

        return header[i + 1] == 0xC8 ? NaplpsSystemType.Prodigy : NaplpsSystemType.NAPLPS;
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
    private void ApplySystemDefaults() => NaplpsDecoder.ApplySystemDefaults(State, SystemType);

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
            // A splice-boundary sequence serializes as the exact real-stream bytes it
            // consumed: reconstructing opcode+operands would leak macro-body bytes into the
            // coded stream and drop the invocation byte consumed mid-operand.
            if (command.RawCodedBytes is { Length: > 0 } rawBytes)
            {
                writer.Write(rawBytes);
                continue;
            }

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
}
