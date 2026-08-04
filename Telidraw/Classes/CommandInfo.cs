// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace Telidraw.Classes;

public class CommandInfo
{
    public int Index { get; set; }

    public string OpCode { get; set; } = string.Empty;

    public string CommandType { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;
}
