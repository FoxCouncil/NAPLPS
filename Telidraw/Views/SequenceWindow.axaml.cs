// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Views;

public partial class SequenceWindow : Window
{
    public SequenceWindow()
    {
        InitializeComponent();

        DataContext = new SequenceWindowViewModel();
    }

    public SequenceWindow(DrawContext drawContext, UndoManager? undoManager = null) : this()
    {
        var vectorPlot = this.Find<AvaPlot>("VectorPlot");

        if (vectorPlot is null)
        {
            return;
        }

        DataContext = new SequenceWindowViewModel(drawContext, vectorPlot, undoManager);
    }
}
