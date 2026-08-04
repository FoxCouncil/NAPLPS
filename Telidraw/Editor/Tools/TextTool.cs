// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.ComponentModel;

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Click to set insertion point, then type to buffer characters. Commits on Enter or
/// tool switch. Honours the text attributes set on this tool (char size, rotation, path,
/// spacing, interrow) by prefixing the committed stream with a TEXT command whenever
/// any differ from their default values. The attributes implement INotifyPropertyChanged
/// so the AttributesPanel binds to them directly.
/// </summary>
public class TextTool : EditorToolBase, INotifyPropertyChanged
{
    public override string Name => "Text";

    private readonly List<char> _buffer = [];

    /// <summary>Whether we have an active text insertion point.</summary>
    public bool HasInsertionPoint { get; private set; }

    public float InsertX { get; private set; }
    public float InsertY { get; private set; }

    // ---- Text attributes: dirty flag means "emit a TEXT command before the string" ----

    private float _charWidth = 1.0f / 40.0f;
    private float _charHeight = 5.0f / 128.0f;
    private TextCommand.TextRotation _rotation = TextCommand.TextRotation.Zero;
    private TextCommand.TextPath _path = TextCommand.TextPath.Right;
    private TextCommand.TextSpacing _spacing = TextCommand.TextSpacing.One;
    private TextCommand.TextInterrowSpacing _interrow = TextCommand.TextInterrowSpacing.One;

    /// <summary>Character width in unit-fraction coords (\u22480.025 for default 1/40).</summary>
    public float CharWidth
    {
        get => _charWidth;
        set { if (_charWidth != value) { _charWidth = value; OnPropertyChanged(nameof(CharWidth)); } }
    }

    /// <summary>Character height in unit-fraction coords (\u22480.039 for default 5/128).</summary>
    public float CharHeight
    {
        get => _charHeight;
        set { if (_charHeight != value) { _charHeight = value; OnPropertyChanged(nameof(CharHeight)); } }
    }

    public TextCommand.TextRotation Rotation
    {
        get => _rotation;
        set { if (_rotation != value) { _rotation = value; OnPropertyChanged(nameof(Rotation)); } }
    }

    public TextCommand.TextPath Path
    {
        get => _path;
        set { if (_path != value) { _path = value; OnPropertyChanged(nameof(Path)); } }
    }

    public TextCommand.TextSpacing Spacing
    {
        get => _spacing;
        set { if (_spacing != value) { _spacing = value; OnPropertyChanged(nameof(Spacing)); } }
    }

    public TextCommand.TextInterrowSpacing Interrow
    {
        get => _interrow;
        set { if (_interrow != value) { _interrow = value; OnPropertyChanged(nameof(Interrow)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// True if any attribute is non-default. When true, CommitText will prefix the text
    /// with a TEXT command so those attributes take effect. When false (the common case),
    /// the text is emitted as-is.
    /// </summary>
    public bool HasNonDefaultAttributes =>
        Rotation != TextCommand.TextRotation.Zero
        || Path != TextCommand.TextPath.Right
        || Spacing != TextCommand.TextSpacing.One
        || Interrow != TextCommand.TextInterrowSpacing.One
        || MathF.Abs(CharWidth - 1.0f / 40.0f) > 1e-5f
        || MathF.Abs(CharHeight - 5.0f / 128.0f) > 1e-5f;

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        if (isRightButton)
        {
            return;
        }

        // Commit any existing text first
        // (handled by the ViewModel which checks HasPendingCommit)

        InsertX = normX;
        InsertY = normY;
        HasInsertionPoint = true;
        _buffer.Clear();
    }

    public override void OnPointerMoved(float normX, float normY) { }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        // Text tool doesn't commit on pointer release — it waits for keyboard input
        return [];
    }

    /// <summary>Add a character to the buffer.</summary>
    public void OnKeyDown(char c)
    {
        if (!HasInsertionPoint)
        {
            return;
        }

        if (c >= 0x20 && c <= 0x7E)
        {
            _buffer.Add(c);
        }
    }

    /// <summary>Whether there are buffered characters ready to commit.</summary>
    public bool HasPendingCommit => HasInsertionPoint && _buffer.Count > 0;

    /// <summary>
    /// Commits the buffered text as NAPLPS commands.
    /// Called on Enter key or tool switch.
    /// </summary>
    public List<(byte opcode, NaplpsOperands operands)> CommitText()
    {
        if (!HasPendingCommit)
        {
            return [];
        }

        var commands = new List<(byte opcode, NaplpsOperands operands)>();

        // If any attribute deviates from the NAPLPS defaults, emit a TEXT command first
        // so the stream applies rotation/path/spacing/char-size before rendering glyphs.
        if (HasNonDefaultAttributes)
        {
            commands.Add(NaplpsCommandBuilder.BuildText(
                CharWidth, CharHeight,
                Spacing, Path, Rotation,
                Interrow));
        }

        // Move pen to insertion point
        commands.Add(NaplpsCommandBuilder.BuildPointSetAbsolute(InsertX, InsertY));

        // Each ASCII character is its own opcode (the character code itself)
        foreach (var c in _buffer)
        {
            commands.Add(((byte)c, new NaplpsOperands()));
        }

        _buffer.Clear();
        HasInsertionPoint = false;
        return commands;
    }

    public override ToolPreview? GetPreview()
    {
        if (!HasInsertionPoint)
        {
            return null;
        }

        // Show a cursor-like preview at insertion point
        return new ToolPreview
        {
            Shape = PreviewShape.Line,
            X1 = InsertX,
            Y1 = InsertY,
            X2 = InsertX,
            Y2 = InsertY + 0.03f // Small vertical line cursor
        };
    }
}
