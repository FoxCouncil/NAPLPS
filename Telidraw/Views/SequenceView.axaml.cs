// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace Telidraw.Views;

public partial class SequenceView : UserControl
{
    public SequenceView()
    {
        InitializeComponent();

        DataContext = new SequenceWindowViewModel();
    }

    public SequenceView(DrawContext drawContext, UndoManager? undoManager = null) : this()
    {
        var vectorPlot = this.Find<AvaPlot>("VectorPlot");

        if (vectorPlot is null)
        {
            return;
        }

        DataContext = new SequenceWindowViewModel(drawContext, vectorPlot, undoManager);
    }
}
