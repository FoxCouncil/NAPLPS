// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.ViewModels;

/// <summary>
/// Drives the OperandEditWindow. Shows the target command's current opcode and operands,
/// lets the user rewrite the operands as a space-separated hex string. When the user
/// commits, <see cref="IsCommitted"/> is true and <see cref="ResultOperands"/> holds the
/// parsed bytes — the caller swaps them into the target command via an InsertAt/Remove
/// pair (routed through UndoManager).
/// </summary>
public partial class OperandEditViewModel : ViewModelBase
{
    [ObservableProperty]
    private byte opcode;

    [ObservableProperty]
    private string commandName = string.Empty;

    [ObservableProperty]
    private string dslKeyword = string.Empty;

    [ObservableProperty]
    private string description = string.Empty;

    /// <summary>Space-separated hex bytes, editable by the user. Example: "C0 C8 D5".</summary>
    [ObservableProperty]
    private string operandHex = string.Empty;

    [ObservableProperty]
    private string parseError = string.Empty;

    /// <summary>Set to true on commit; caller reads ResultOperands afterwards.</summary>
    public bool IsCommitted { get; private set; }

    /// <summary>The parsed bytes the caller should install on the target command.</summary>
    public NaplpsOperands ResultOperands { get; private set; } = [];

    public void Initialize(byte opcode, NaplpsOperands currentOperands)
    {
        Opcode = opcode;

        var descriptor = CommandRegistry.GetByOpcode(opcode);
        CommandName = descriptor?.Name ?? $"Opcode 0x{opcode:X2}";
        DslKeyword = descriptor?.DslKeyword ?? string.Empty;
        Description = descriptor?.Description ?? "(no description)";

        OperandHex = string.Join(" ", currentOperands.Select(b => b.ToString("X2")));
    }

    [RelayCommand]
    private void Commit(Window host)
    {
        if (!TryParseHex(OperandHex, out var bytes, out var error))
        {
            ParseError = error;
            return;
        }

        ParseError = string.Empty;
        ResultOperands = bytes;
        IsCommitted = true;
        host.Close();
    }

    [RelayCommand]
    private static void Cancel(Window host)
    {
        host.Close();
    }

    private static bool TryParseHex(string input, out NaplpsOperands result, out string error)
    {
        result = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var tokens = input.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            var clean = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;

            if (!byte.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                error = $"'{token}' is not a valid hex byte (expected 00-FF)";
                return false;
            }

            result.Add(b);
        }

        return true;
    }
}
