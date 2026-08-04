// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

public interface IEditorAction
{
    void Execute(NaplpsFormat format);
    void Undo(NaplpsFormat format);
}
