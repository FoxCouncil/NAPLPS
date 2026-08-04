// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.ViewModels;

public class DiagnosticItem
{
    public bool IsError { get; init; }

    public string Severity { get; init; } = "";

    public string Type { get; init; } = "";

    public string Message { get; init; } = "";

    public string Details { get; init; } = "";
}
