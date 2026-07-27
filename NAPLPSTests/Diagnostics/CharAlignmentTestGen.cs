// Generate a comprehensive character alignment test .nap file.
// Draws outlined rectangles where each character cell should be,
// then draws the characters inside them. Misalignment = bug.
using NAPLPS;
using NAPLPS.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using IOFile = System.IO.File;

namespace NAPLPSTests.Diagnostics;

[TestClass]
public class CharAlignmentTestGen
{
    [TestMethod]
    public void GenerateProportionalTestFiles()
    {
        // All proportional text sizes used in 1.nap
        var propSizes = new (float w, float h, int idx)[]
        {
            (0.023438f, 0.023438f, 137),
            (0.023438f, 0.023438f, 218),
            (0.011719f, 0.031250f, 527),
            (0.015625f, 0.015625f, 542),
            (0.011719f, 0.031250f, 819),
            (0.015625f, 0.031250f, 954),
        };

        // Deduplicate by size
        var uniqueSizes = propSizes
            .Select(s => (s.w, s.h))
            .Distinct()
            .ToList();

        foreach (var (charW, charH) in uniqueSizes)
        {
            var naplps = NaplpsFormat.New(NaplpsSystemType.Prodigy);

            string sizeLabel = $"{charW:F6}x{charH:F6}";
            string safeName = $"chrtp_{(int)(charW * 10000)}x{(int)(charH * 10000)}";

            // Add TEXT command to set the size + proportional spacing
            // TEXT opcode = 0xA2, operands set charSize and spacing mode
            // For now, use AddCommand with raw TEXT PDI
            // Actually, we need to set charSize via the state before adding chars
            // The simplest way: use the TEXT command format

            // Set the text size + proportional spacing via TEXT command
            var textCmd = NaplpsCommandBuilder.BuildText(charW, charH, TextCommand.TextSpacing.Proportional);
            naplps.AddCommand(textCmd.opcode, textCmd.operands);

            float startX = 0.02f;
            float startY = 0.70f;

            // Row 1: Cell grid + individually positioned chars
            var selGrid = NaplpsCommandBuilder.BuildSelectColor(1);
            naplps.AddCommand(selGrid.opcode, selGrid.operands);

            int charsPerRow = Math.Min(40, (int)(0.95f / charW));
            string testText = "The quick brown fox jumps over the lazy dog 0123456789";
            if (testText.Length > charsPerRow)
            {
                testText = testText[..charsPerRow];
            }

            // Draw cell grid
            for (int col = 0; col < testText.Length; col++)
            {
                float cellX = startX + col * charW;
                var moveTo = NaplpsCommandBuilder.BuildPointSetAbsolute(cellX, startY);
                naplps.AddCommand(moveTo.opcode, moveTo.operands);
                var rect = NaplpsCommandBuilder.BuildRectangleOutlined(charW, charH);
                naplps.AddCommand(rect.opcode, rect.operands);
            }

            // Draw chars individually positioned (green = reference)
            var selRef = NaplpsCommandBuilder.BuildSelectColor(2);
            naplps.AddCommand(selRef.opcode, selRef.operands);

            for (int col = 0; col < testText.Length; col++)
            {
                float cellX = startX + col * charW;
                var moveTo = NaplpsCommandBuilder.BuildPointSetAbsolute(cellX, startY);
                naplps.AddCommand(moveTo.opcode, moveTo.operands);
                naplps.AddCommand((byte)testText[col]);
            }

            // Row 2: Same text with natural proportional advance (white)
            float row2Y = startY - charH * 2.5f;

            // Cell grid for row 2
            var selGrid2 = NaplpsCommandBuilder.BuildSelectColor(1);
            naplps.AddCommand(selGrid2.opcode, selGrid2.operands);

            for (int col = 0; col < testText.Length; col++)
            {
                float cellX = startX + col * charW;
                var moveTo = NaplpsCommandBuilder.BuildPointSetAbsolute(cellX, row2Y);
                naplps.AddCommand(moveTo.opcode, moveTo.operands);
                var rect = NaplpsCommandBuilder.BuildRectangleOutlined(charW, charH);
                naplps.AddCommand(rect.opcode, rect.operands);
            }

            // Draw with natural advance
            var selText = NaplpsCommandBuilder.BuildSelectColor(7);
            naplps.AddCommand(selText.opcode, selText.operands);

            var moveToRow2 = NaplpsCommandBuilder.BuildPointSetAbsolute(startX, row2Y);
            naplps.AddCommand(moveToRow2.opcode, moveToRow2.operands);

            foreach (char c in testText)
            {
                naplps.AddCommand((byte)c);
            }

            // Row 3: Size label text
            float row3Y = startY - charH * 5f;
            var selLabel = NaplpsCommandBuilder.BuildSelectColor(11);
            naplps.AddCommand(selLabel.opcode, selLabel.operands);

            var moveToRow3 = NaplpsCommandBuilder.BuildPointSetAbsolute(startX, row3Y);
            naplps.AddCommand(moveToRow3.opcode, moveToRow3.operands);

            string label = $"Size: {charW}x{charH} Proportional";
            foreach (char c in label)
            {
                if (c >= 0x20 && c <= 0x7E)
                {
                    naplps.AddCommand((byte)c);
                }
            }

            // Save
            // Source corpus at the repo root. "Examples" must match the directory's real case:
            // on a case-sensitive filesystem the lowercase spelling resolves to nothing and the
            // save throws. The bin copy below stays lowercase - that is the Link name the csproj
            // gives it in the output directory.
            var srcExamples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Examples"));

            var napPath = Path.Combine(srcExamples, $"{safeName}.nap");
            naplps.Save(napPath);

            // Also save to bin examples
            var binExamples = Path.Combine(AppContext.BaseDirectory, "examples");
            Directory.CreateDirectory(binExamples);
            naplps.Save(Path.Combine(binExamples, $"{safeName}.nap"));

            // Render PNG
            var naplps2 = NaplpsFormat.FromFile(napPath);
            using var ctx = new DrawContext(naplps2, new SixLabors.ImageSharp.Size(1024, 768));
            ctx.Render();
            ctx.Image.SaveAsPng(Path.Combine(AppContext.BaseDirectory, $"{safeName}.png"));
        }
    }

    [TestMethod]
    public void GenerateCharAlignmentNap()
    {
        var naplps = NaplpsFormat.New(NaplpsSystemType.Prodigy);

        // Default char size: 1/40 = 0.025 wide, 5/128 = 0.0390625 high
        float charW = 1.0f / 40.0f;
        float charH = 5.0f / 128.0f;
        float startX = 0.02f;
        float startY = 0.75f;
        int charsPerRow = 32;

        // --- Draw outlined rectangles for cell boundaries (gray) ---
        var selGray = NaplpsCommandBuilder.BuildSelectColor(3); // entry 3
        naplps.AddCommand(selGray.opcode, selGray.operands);

        for (int row = 0; row < 3; row++)
        {
            float rowY = startY - (row * charH * 2.0f);

            for (int col = 0; col < charsPerRow; col++)
            {
                int charCode = 0x20 + row * 32 + col;

                if (charCode > 0x7E)
                {
                    break;
                }

                float cellX = startX + col * charW;

                var moveTo = NaplpsCommandBuilder.BuildPointSetAbsolute(cellX, rowY);
                naplps.AddCommand(moveTo.opcode, moveTo.operands);

                var rect = NaplpsCommandBuilder.BuildRectangleOutlined(charW, charH);
                naplps.AddCommand(rect.opcode, rect.operands);
            }
        }

        // --- Draw each character individually, repositioned to its cell ---
        // This bypasses proportional pen advance — each char is placed at the
        // EXACT cell position via PointSetAbsolute, so we can see if the
        // character renders correctly within its cell boundaries.
        var selWhite = NaplpsCommandBuilder.BuildSelectColor(7); // white
        naplps.AddCommand(selWhite.opcode, selWhite.operands);

        for (int row = 0; row < 3; row++)
        {
            float rowY = startY - (row * charH * 2.0f);

            for (int col = 0; col < charsPerRow; col++)
            {
                int charCode = 0x20 + row * 32 + col;

                if (charCode > 0x7E)
                {
                    break;
                }

                // Move pen to EXACT cell origin for this character
                float cellX = startX + col * charW;
                var moveTo = NaplpsCommandBuilder.BuildPointSetAbsolute(cellX, rowY);
                naplps.AddCommand(moveTo.opcode, moveTo.operands);

                // Draw the character
                naplps.AddCommand((byte)charCode);
            }
        }

        // --- Row 4: Proportional spacing test ---
        // Draw text with natural pen advance, with cell grid behind for comparison.
        // Each cell in the grid is charW wide. If proportional advance matches,
        // the text should align with the grid at key checkpoints.
        float freeRowY = startY - (3 * charH * 2.0f);

        // Draw cell grid
        var selDark = NaplpsCommandBuilder.BuildSelectColor(1);
        naplps.AddCommand(selDark.opcode, selDark.operands);

        for (int col = 0; col < charsPerRow; col++)
        {
            float cellX = startX + col * charW;
            var moveTo = NaplpsCommandBuilder.BuildPointSetAbsolute(cellX, freeRowY);
            naplps.AddCommand(moveTo.opcode, moveTo.operands);
            var rect = NaplpsCommandBuilder.BuildRectangleOutlined(charW, charH);
            naplps.AddCommand(rect.opcode, rect.operands);
        }

        // Draw chars with free pen advance (yellow)
        var selYellow = NaplpsCommandBuilder.BuildSelectColor(11);
        naplps.AddCommand(selYellow.opcode, selYellow.operands);

        var moveToFree = NaplpsCommandBuilder.BuildPointSetAbsolute(startX, freeRowY);
        naplps.AddCommand(moveToFree.opcode, moveToFree.operands);

        // A-Z then a-f with natural proportional advance
        for (int c = 0x41; c <= 0x5A; c++)
        {
            naplps.AddCommand((byte)c);
        }

        for (int c = 0x61; c <= 0x66; c++)
        {
            naplps.AddCommand((byte)c);
        }

        // --- Row 5: "The quick brown fox" proportional test ---
        float sentenceRowY = startY - (4 * charH * 2.0f);

        var selSentenceGrid = NaplpsCommandBuilder.BuildSelectColor(1);
        naplps.AddCommand(selSentenceGrid.opcode, selSentenceGrid.operands);

        string sentence = "The quick brown fox jumps over";
        for (int col = 0; col < sentence.Length; col++)
        {
            float cellX = startX + col * charW;
            var moveTo = NaplpsCommandBuilder.BuildPointSetAbsolute(cellX, sentenceRowY);
            naplps.AddCommand(moveTo.opcode, moveTo.operands);
            var rect = NaplpsCommandBuilder.BuildRectangleOutlined(charW, charH);
            naplps.AddCommand(rect.opcode, rect.operands);
        }

        var selSentence = NaplpsCommandBuilder.BuildSelectColor(7);
        naplps.AddCommand(selSentence.opcode, selSentence.operands);

        var moveToSentence = NaplpsCommandBuilder.BuildPointSetAbsolute(startX, sentenceRowY);
        naplps.AddCommand(moveToSentence.opcode, moveToSentence.operands);

        foreach (char c in sentence)
        {
            naplps.AddCommand((byte)c);
        }

        // --- Row 6: Same sentence individually positioned (reference) ---
        float refRowY = startY - (5 * charH * 2.0f);

        var selRefGrid = NaplpsCommandBuilder.BuildSelectColor(1);
        naplps.AddCommand(selRefGrid.opcode, selRefGrid.operands);

        for (int col = 0; col < sentence.Length; col++)
        {
            float cellX = startX + col * charW;
            var moveTo = NaplpsCommandBuilder.BuildPointSetAbsolute(cellX, refRowY);
            naplps.AddCommand(moveTo.opcode, refRowY.ToString() != "" ? moveTo.operands : moveTo.operands);
            var rect = NaplpsCommandBuilder.BuildRectangleOutlined(charW, charH);
            naplps.AddCommand(rect.opcode, rect.operands);
        }

        var selRef = NaplpsCommandBuilder.BuildSelectColor(2); // green for reference
        naplps.AddCommand(selRef.opcode, selRef.operands);

        for (int col = 0; col < sentence.Length; col++)
        {
            float cellX = startX + col * charW;
            var moveTo = NaplpsCommandBuilder.BuildPointSetAbsolute(cellX, refRowY);
            naplps.AddCommand(moveTo.opcode, moveTo.operands);
            naplps.AddCommand((byte)sentence[col]);
        }

        // --- Save ---
        var outputDir = Path.Combine(AppContext.BaseDirectory, "examples");
        Directory.CreateDirectory(outputDir);
        var napPath = Path.Combine(outputDir, "chrtest.nap");
        naplps.Save(napPath);

        // Also save to the source corpus - real directory case, see above.
        var srcExamples = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(AppContext.BaseDirectory))))!, "Examples");
        if (Directory.Exists(srcExamples))
        {
            IOFile.Copy(napPath, Path.Combine(srcExamples, "chrtest.nap"), true);
        }

        // --- Render to PNG ---
        var naplps2 = NaplpsFormat.FromFile(napPath);
        using var ctx = new DrawContext(naplps2, new SixLabors.ImageSharp.Size(1024, 768));
        ctx.Render();
        ctx.Image.SaveAsPng(Path.Combine(AppContext.BaseDirectory, "chrtest.png"));
    }
}
