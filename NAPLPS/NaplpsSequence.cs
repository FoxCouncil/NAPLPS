// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS;

public class NaplpsSequence(NaplpsState state, NaplpsCommand command)
{
    public NaplpsState State { get; set; } = state;

    public NaplpsCommand Command { get; set; } = command;

    /// <summary>
    /// True for sequences the parser materializes rather than reads from the stream (macro
    /// expansions, DEFP MACRO's define-and-display execution). X3.110 codes a macro call as a
    /// single G-set byte (section 5.5) - expansion is presentation behavior - so synthetic
    /// sequences render but are skipped by serialization (ToBytes, Telidraw decompile).
    /// </summary>
    public bool IsSynthetic { get; set; }

    /// <summary>
    /// The exact coded-stream bytes this sequence consumed, captured when a command's bytes
    /// straddle a macro-splice boundary (a body opcode taking operands from the coded stream,
    /// or coded-stream operands continuing into a body). When set, serialization emits these
    /// bytes verbatim: reconstructing opcode+operands would leak macro-body bytes into the
    /// coded stream and drop the invocation byte consumed mid-operand.
    /// </summary>
    [JsonIgnore]
    public byte[]? RawCodedBytes { get; set; }

    public void Deconstruct(out NaplpsCommand command, out NaplpsState state)
    {
        command = Command;
        state = State;
    }

    public override string ToString()
    {
        return $"{Command}:|:{State}";
    }
}
