// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Telidraw.Services;

namespace Telidraw.Views;

public partial class OperandEditView : UserControl
{
    public OperandEditView()
    {
        InitializeComponent();
        DataContext = new OperandEditViewModel();
    }

    /// <summary>
    /// Open the edit dialog for the given (opcode, operands). Returns the updated operands
    /// when the user commits; null if they cancelled or the parse failed.
    /// </summary>
    public static async Task<NaplpsOperands?> PromptAsync(byte opcode, NaplpsOperands current)
    {
        var view = new OperandEditView();

        if (view.DataContext is OperandEditViewModel vm)
        {
            vm.Initialize(opcode, current);
            vm.RequestClose += () => Shell.CloseHost(view);
        }

        await Shell.ShowDialogAsync(view, new ChildShellOptions("Edit Operands") { Width = 500, Height = 360 });

        if (view.DataContext is OperandEditViewModel vm2 && vm2.IsCommitted)
        {
            return vm2.ResultOperands;
        }

        return null;
    }
}
