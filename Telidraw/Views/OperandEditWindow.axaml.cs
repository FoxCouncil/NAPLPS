// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Views;

public partial class OperandEditWindow : Window
{
    public OperandEditWindow()
    {
        InitializeComponent();
        DataContext = new OperandEditViewModel();
    }

    /// <summary>
    /// Open the edit dialog for the given (opcode, operands). Returns the updated operands
    /// when the user commits; null if they cancelled or the parse failed.
    /// </summary>
    public static async Task<NaplpsOperands?> PromptAsync(Window owner, byte opcode, NaplpsOperands current)
    {
        var dialog = new OperandEditWindow();

        if (dialog.DataContext is OperandEditViewModel vm)
        {
            vm.Initialize(opcode, current);
        }

        await dialog.ShowDialog(owner);

        if (dialog.DataContext is OperandEditViewModel vm2 && vm2.IsCommitted)
        {
            return vm2.ResultOperands;
        }

        return null;
    }
}
