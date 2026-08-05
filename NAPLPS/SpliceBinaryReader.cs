// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS;

/// <summary>
/// Marks a reader as the TOP-LEVEL coded stream for <see cref="NaplpsDecoder.ReadStream"/>:
/// macro invocations in a top-level parse splice their bodies into the coded stream at the
/// invocation byte (X3.110 5.5), where isolated sub-stream parses expand them recursively.
/// The splicing itself lives in the decoder's <see cref="ByteSource"/> (its front-insertion
/// injection queue); this class survives only so the one-shot facade can tell the two parse
/// shapes apart without changing its public signature.
/// </summary>
internal sealed class SpliceBinaryReader(Stream input) : BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
