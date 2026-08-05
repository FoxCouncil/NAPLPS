// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Models;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SixLabors.ImageSharp.Formats.Gif;

namespace Telidraw;

sealed class Program
{
    public static string Version { get; } = GetLibraryVersion();

#if !BROWSER
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break. The browser head has its own Main (Telidraw.Browser); this
    // one, the CLI handlers and the desktop AppBuilder are compiled out of the browser TFM.
    [STAThread]
    public static int Main(string[] args)
    {
        // Handle CLI commands before starting the GUI
        if (args.Length > 0)
        {
            var command = args[0].ToLowerInvariant();

            if (command == "info" || command == "--info" || command == "-i")
            {
                return HandleInfoCommand(args);
            }

            if (command == "export" || command == "--export" || command == "-e")
            {
                return HandleExportCommand(args);
            }

            if (command == "diff" || command == "--diff" || command == "-d")
            {
                return HandleDiffCommand(args);
            }

            if (command == "compile" || command == "--compile" || command == "-c")
            {
                return HandleCompileCommand(args);
            }

            if (command == "decompile" || command == "--decompile")
            {
                return HandleDecompileCommand(args);
            }

            if (command == "help" || command == "--help" || command == "-h" || command == "-?")
            {
                PrintHelp();
                return 0;
            }

            if (command == "--version" || command == "-v")
            {
                Console.WriteLine($"Telidraw v{Version}");
                return 0;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"Telidraw v{Version}");
        Console.WriteLine();
        Console.WriteLine("Usage: Telidraw [command] [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  info <file> [--format=text|json]        Display file information");
        Console.WriteLine("  export <file> [output] [options]        Export file to image format");
        Console.WriteLine("  export --batch <dir> [options]          Batch export all .nap files");
        Console.WriteLine("  diff <file1> <file2> [options]          Compare two NAPLPS files");
        Console.WriteLine("  compile <file.td> [-o output.nap]       Compile Telidraw source to NAPLPS");
        Console.WriteLine("  decompile <file.nap> [-o output.td]     Decompile NAPLPS to Telidraw source");
        Console.WriteLine();
        Console.WriteLine("Export Options:");
        Console.WriteLine("  --format=png|gif|apng Output format (default: png)");
        Console.WriteLine("  --size=WxH            Canvas size (default: 1024x768)");
        Console.WriteLine("  --at=FRAMES           Export specific frames (e.g. 1,2-5,500,1200)");
        Console.WriteLine("  --stdout, -           Output to stdout instead of file (Unix-reliable; on");
        Console.WriteLine("                        Windows the GUI-subsystem binary pipes unreliably)");
        Console.WriteLine();
        Console.WriteLine("Batch Export Options:");
        Console.WriteLine("  --batch               Enable batch mode (input is a directory)");
        Console.WriteLine("  --output-dir=<path>   Output directory (default: alongside source files)");
        Console.WriteLine();
        Console.WriteLine("GIF Options:");
        Console.WriteLine("  --loop                Loop the GIF animation (default: no loop)");
        Console.WriteLine("  --delay=N             Flat frame delay in 1/100s of a second; implies --baud=0");
        Console.WriteLine();
        Console.WriteLine("APNG Options:");
        Console.WriteLine("  --baud=N              Pace drawing at N bits/sec as a videotex link would,");
        Console.WriteLine("                        so a frame lasts as long as its bytes took to arrive");
        Console.WriteLine("                        (default: 1200, 0 = flat --delay pacing)");
        Console.WriteLine();
        Console.WriteLine("Palette Animation Options:");
        Console.WriteLine("  --palette-anim        Export blink/palette animation as GIF");
        Console.WriteLine("  --frames=N            Number of animation frames (default: 120)");
        Console.WriteLine();
        Console.WriteLine("Compile/Decompile Options:");
        Console.WriteLine("  --system-type=T       Force naplps|prodigy|telidon. On compile it sets the");
        Console.WriteLine("                        target system (default naplps). On decompile it forces");
        Console.WriteLine("                        how an ambiguous stream is decoded (default auto-detect);");
        Console.WriteLine("                        it rarely changes output for auto-detectable files.");
        Console.WriteLine("  --bare                Compile the .td as the complete byte specification");
        Console.WriteLine("                        (no CAN+NSR sentinels). Decompiler output expects");
        Console.WriteLine("                        this; it makes nap -> td -> nap byte-exact.");
        Console.WriteLine("  --stdout              Write the result to stdout (bytes for compile,");
        Console.WriteLine("                        source for decompile); mutually exclusive with -o.");
        Console.WriteLine("                        Reliable on Unix; on Windows the app is a GUI");
        Console.WriteLine("                        subsystem binary so piping stdout is unreliable.");
        Console.WriteLine("  --force, -f           Overwrite a defaulted output file that exists");
        Console.WriteLine("                        (an explicit -o always overwrites).");
        Console.WriteLine();
        Console.WriteLine("Diff Options:");
        Console.WriteLine("  --mode=text|visual    Diff mode (default: text)");
        Console.WriteLine("  --size=WxH            Canvas size for visual diff (default: 1024x768)");
        Console.WriteLine("  --output=<file>       Output file for visual diff (default: diff.png)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  Telidraw info myfile.nap");
        Console.WriteLine("  Telidraw info myfile.nap --format=json");
        Console.WriteLine("  Telidraw export myfile.nap output.png");
        Console.WriteLine("  Telidraw export myfile.nap --format=gif output.gif");
        Console.WriteLine("  Telidraw export myfile.nap --format=gif --loop --delay=10 output.gif");
        Console.WriteLine("  Telidraw export myfile.nap --stdout > output.png");
        Console.WriteLine("  Telidraw export --batch Examples/ --format=png");
        Console.WriteLine("  Telidraw export --batch Examples/ --output-dir=output/ --format=gif");
        Console.WriteLine("  Telidraw export building.nap --palette-anim --loop --frames=300 anim.gif");
        Console.WriteLine("  Telidraw compile drawing.td -o drawing.nap");
        Console.WriteLine("  Telidraw decompile picture.nap             # writes picture.td");
        Console.WriteLine("  Telidraw decompile picture.nap --stdout | less");
        Console.WriteLine("  Telidraw diff file1.nap file2.nap");
        Console.WriteLine("  Telidraw diff file1.nap file2.nap --mode=visual --output=diff.png");
    }

    private static int HandleInfoCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Error: No input file specified.");
            Console.Error.WriteLine("Usage: Telidraw info <file> [--format=text|json]");
            return 1;
        }

        var inputFile = args[1];
        var format = "text";

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i].StartsWith("--format="))
            {
                format = args[i]["--format=".Length..].ToLowerInvariant();
            }
        }

        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"Error: File not found: {inputFile}");
            return 1;
        }

        try
        {
            var naplps = NaplpsFormat.FromFile(inputFile);
            var fileInfo = new FileInfo(inputFile);

            var warnings = naplps.Errors.Where(e => e.Severity == NaplpsErrorSeverity.Warning).ToList();
            var errors = naplps.Errors.Where(e => e.Severity == NaplpsErrorSeverity.Error).ToList();

            if (format == "json")
            {
                var info = new
                {
                    FileName = fileInfo.Name,
                    FilePath = fileInfo.FullName,
                    FileSize = fileInfo.Length,
                    SystemType = naplps.SystemType.ToString(),
                    BitWidth = naplps.Is7Bit ? "7-Bit" : "8-Bit",
                    CommandCount = naplps.Commands.Count,
                    IsValid = naplps.IsValid,
                    ErrorCount = errors.Count,
                    WarningCount = warnings.Count,
                    Errors = errors.Select(e => e.ToString()).ToArray(),
                    Warnings = warnings.Select(e => e.ToString()).ToArray(),
                    Version
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.WriteLine(JsonSerializer.Serialize(info, options));
            }
            else
            {
                // Tab-aligned text format
                Console.WriteLine($"File Name:\t{fileInfo.Name}");
                Console.WriteLine($"File Path:\t{fileInfo.FullName}");
                Console.WriteLine($"File Size:\t{fileInfo.Length} bytes");
                Console.WriteLine($"System Type:\t{naplps.SystemType}");
                Console.WriteLine($"Bit Width:\t{(naplps.Is7Bit ? "7-Bit" : "8-Bit")}");
                Console.WriteLine($"Commands:\t{naplps.Commands.Count}");
                Console.WriteLine($"Valid:\t\t{naplps.IsValid}");
                Console.WriteLine($"Errors:\t\t{errors.Count}");
                Console.WriteLine($"Warnings:\t{warnings.Count}");

                if (errors.Count > 0)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Errors:");
                    foreach (var error in errors)
                    {
                        Console.WriteLine($"  {error}");
                    }
                    Console.ResetColor();
                }

                if (warnings.Count > 0)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Warnings:");
                    foreach (var warning in warnings)
                    {
                        Console.WriteLine($"  {warning}");
                    }
                    Console.ResetColor();
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to parse file: {ex.Message}");
            return 1;
        }
    }

    private static int HandleExportCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Error: No input file specified.");
            Console.Error.WriteLine("Usage: Telidraw export <file|--batch dir> [output] [--format=png|gif|apng] [--size=WxH] [--stdout]");
            return 1;
        }

        var opts = ParseExportArgs(args, out var parseError);

        if (parseError != null)
        {
            Console.Error.WriteLine(parseError);
            return 1;
        }

        if (!ParseSize(opts.Size, out var width, out var height))
        {
            Console.Error.WriteLine($"Error: Invalid size format: {opts.Size}. Expected WxH (e.g., 1024x768)");
            return 1;
        }

        if (opts.Batch)
        {
            return HandleBatchExport(opts.InputFile, opts.OutputDir, opts.Format, width, height, opts.Loop, opts.Delay, opts.GunWidthSet, opts.GunWidth, opts.DisplayRatio, opts.HardText, opts.Authentic, opts.ForceProdigy);
        }

        var outputFile = opts.OutputFile;

        if (!opts.UseStdout && outputFile == null)
        {
            outputFile = IOPath.ChangeExtension(opts.InputFile, opts.Format);
        }

        if (!File.Exists(opts.InputFile))
        {
            Console.Error.WriteLine($"Error: File not found: {opts.InputFile}");
            return 1;
        }

        try
        {
            var naplps = NaplpsFormat.FromFile(opts.InputFile, opts.ForceProdigy ? NaplpsSystemType.Prodigy : null);
            using var drawContext = new DrawContext(naplps, new SixLabors.ImageSharp.Size(width, height));

            drawContext.BaudRate = opts.Baud;

            if (opts.GunWidthSet)
            {
                drawContext.ColorGunWidth = opts.GunWidth;
            }

            if (opts.DisplayRatio is float dr)
            {
                drawContext.DisplayRatio = dr;
            }

            if (opts.HardText is bool ht)
            {
                drawContext.HardText = ht;
            }

            if (opts.Authentic)
            {
                drawContext.AuthenticGeometry = true;
            }

            if (opts.PaletteAnim)
            {
                return ExportPaletteAnimGif(drawContext, outputFile, opts.UseStdout, opts.Loop, opts.Delay, opts.PaletteFrames);
            }
            else if (opts.Format == "apng")
            {
                return ExportApng(drawContext, outputFile, opts.UseStdout, opts.Delay, opts.Loop, opts.BlinkCycles);
            }
            else if (opts.Format == "gif")
            {
                return ExportGif(drawContext, outputFile, opts.UseStdout, opts.Loop, opts.Delay);
            }
            else
            {
                return ExportPng(drawContext, outputFile, opts.UseStdout, opts.AtFrames);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to export file: {ex.Message}");
            return 1;
        }
    }

    private record ExportOptions(
        string InputFile, string? OutputFile, string? OutputDir,
        string Format, string Size, bool UseStdout, bool Loop,
        bool Batch, bool PaletteAnim, int PaletteFrames, int Delay, int Baud,
        string? AtFrames, int BlinkCycles, bool GunWidthSet, int? GunWidth, float? DisplayRatio, bool? HardText, bool Authentic, bool ForceProdigy);

    /// <summary>
    /// Parses a printer-style range string like "1,2-5,10" into a sorted list of indices.
    /// </summary>
    private static List<int> ParseFrameRanges(string rangeStr)
    {
        var result = new HashSet<int>();

        foreach (var part in rangeStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-'))
            {
                var bounds = part.Split('-', 2);
                if (int.TryParse(bounds[0], out var lo) && int.TryParse(bounds[1], out var hi))
                {
                    for (int i = lo; i <= hi; i++)
                    {
                        result.Add(i);
                    }
                }
            }
            else if (int.TryParse(part, out var single))
            {
                result.Add(single);
            }
        }

        return result.Order().ToList();
    }

    private static ExportOptions ParseExportArgs(string[] args, out string? error)
    {
        error = null;
        var inputFile = args[1];
        string? outputFile = null;
        string? outputDir = null;
        var format = "png";
        var size = "1024x768";
        var useStdout = false;
        var loop = false;
        var batch = false;
        var paletteAnim = false;
        var paletteFrames = 120;
        var delay = 5;
        var baud = NAPLPS.NaplpsBaud.Default;
        string? atFrames = null;
        var blinkCycles = 0;
        var gunWidthSet = false;
        int? gunWidth = null;
        float? displayRatio = null;
        bool? hardText = null;
        var authentic = false;
        var forceProdigy = false;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--stdout" || args[i] == "-")
            {
                useStdout = true;
            }
            else if (args[i] == "--loop")
            {
                loop = true;
            }
            else if (args[i] == "--batch")
            {
                batch = true;
            }
            else if (args[i].StartsWith("--display-ratio="))
            {
                // Invariant culture: "0.78" must parse the same way on comma-decimal locales.
                if (!float.TryParse(args[i]["--display-ratio=".Length..], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var dr) || dr is <= 0 or > 1)
                {
                    error = "Error: Invalid display-ratio value. Expected a value in (0, 1].";
                    break;
                }
                displayRatio = dr;
            }
            else if (args[i] == "--hard-text")
            {
                hardText = true;
            }
            else if (args[i] == "--no-hard-text")
            {
                hardText = false;
            }
            else if (args[i] == "--authentic")
            {
                authentic = true;
            }
            else if (args[i] == "--prodigy")
            {
                forceProdigy = true;
            }
            else if (args[i] == "--palette-anim")
            {
                paletteAnim = true;
                format = "gif";
            }
            else if (args[i].StartsWith("--format="))
            {
                format = args[i]["--format=".Length..].ToLowerInvariant();
            }
            else if (args[i].StartsWith("--size="))
            {
                size = args[i]["--size=".Length..];
            }
            else if (args[i].StartsWith("--output-dir="))
            {
                outputDir = args[i]["--output-dir=".Length..];
            }
            else if (args[i].StartsWith("--delay="))
            {
                if (!int.TryParse(args[i]["--delay=".Length..], out delay) || delay < 1)
                {
                    error = "Error: Invalid delay value. Expected positive integer.";
                    break;
                }

                // An explicit flat delay and baud pacing are mutually exclusive: baud derives each
                // frame's delay from the bytes it represents, which would silently discard this.
                baud = 0;
            }
            else if (args[i].StartsWith("--baud="))
            {
                if (!int.TryParse(args[i]["--baud=".Length..], out baud) || baud < 0)
                {
                    error = "Error: Invalid baud value. Expected 0 (flat delay) or a positive integer.";
                    break;
                }
            }
            else if (args[i].StartsWith("--at="))
            {
                atFrames = args[i]["--at=".Length..];
            }
            else if (args[i].StartsWith("--blink-cycles="))
            {
                if (!int.TryParse(args[i]["--blink-cycles=".Length..], out blinkCycles) || blinkCycles < 0)
                {
                    error = "Error: Invalid blink-cycles value. Expected non-negative integer.";
                    break;
                }
            }
            else if (args[i].StartsWith("--frames="))
            {
                if (!int.TryParse(args[i]["--frames=".Length..], out paletteFrames) || paletteFrames < 1)
                {
                    error = "Error: Invalid frames value. Expected positive integer.";
                    break;
                }
            }
            else if (args[i].StartsWith("--gun-width="))
            {
                var val = args[i]["--gun-width=".Length..];
                gunWidthSet = true;
                if (val is "full" or "0" or "none")
                {
                    gunWidth = null;
                }
                else if (int.TryParse(val, out var gw) && gw is > 0 and < 8)
                {
                    gunWidth = gw;
                }
                else
                {
                    error = "Error: Invalid gun-width value. Expected 1-7, or 'full'.";
                    break;
                }
            }
            else if (!args[i].StartsWith("--") && !args[i].StartsWith("-"))
            {
                outputFile = args[i];
            }
        }

        return new ExportOptions(inputFile, outputFile, outputDir, format, size, useStdout, loop, batch, paletteAnim, paletteFrames, delay, baud, atFrames, blinkCycles, gunWidthSet, gunWidth, displayRatio, hardText, authentic, forceProdigy);
    }

    private static int HandleBatchExport(string inputDir, string? outputDir, string format, int width, int height, bool loop, int delay, bool gunWidthSet, int? gunWidth, float? displayRatio, bool? hardText, bool authentic, bool forceProdigy)
    {
        if (!Directory.Exists(inputDir))
        {
            Console.Error.WriteLine($"Error: Directory not found: {inputDir}");
            return 1;
        }

        if (outputDir != null)
        {
            Directory.CreateDirectory(outputDir);
        }

        var files = Directory.EnumerateFiles(inputDir, "*.nap", SearchOption.AllDirectories).ToList();

        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No .nap files found in: {inputDir}");
            return 1;
        }

        int processed = 0, failed = 0;
        var total = files.Count;
        var parsedSize = new SixLabors.ImageSharp.Size(width, height);

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
        {
            try
            {
                var outPath = outputDir != null ? IOPath.Combine(outputDir, IOPath.ChangeExtension(IOPath.GetFileName(file), format)) : IOPath.ChangeExtension(file, format);

                var naplps = NaplpsFormat.FromFile(file, forceProdigy ? NaplpsSystemType.Prodigy : null);
                using var drawContext = new DrawContext(naplps, parsedSize);

                if (gunWidthSet)
                {
                    drawContext.ColorGunWidth = gunWidth;
                }

                if (displayRatio is float dr)
                {
                    drawContext.DisplayRatio = dr;
                }

                if (hardText is bool ht)
                {
                    drawContext.HardText = ht;
                }

                if (authentic)
                {
                    drawContext.AuthenticGeometry = true;
                }

                if (format == "gif")
                {
                    ExportGif(drawContext, outPath, false, loop, delay);
                }
                else
                {
                    ExportPng(drawContext, outPath, false);
                }

                var count = Interlocked.Increment(ref processed);
                Console.Error.WriteLine($"[{count}/{total}] Exported: {file}");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Console.Error.WriteLine($"[FAIL] {file}: {ex.Message}");
            }
        });

        Console.Error.WriteLine($"Done. {processed} exported, {failed} failed of {total} total.");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Shared option parsing for compile/decompile. Rejects unknown arguments outright -
    /// a silently ignored flag (a typo, --bare on the wrong command, a space where '='
    /// belongs) otherwise produces wrong bytes with a green exit code.
    /// </summary>
    private static bool TryParseConvertOptions(string[] args, bool isCompile, out string? outputPath,
        out bool bare, out bool toStdout, out NaplpsSystemType? systemType, out bool force)
    {
        outputPath = null;
        bare = false;
        toStdout = false;
        systemType = null;
        force = false;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "-o" || args[i] == "--output")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    Console.Error.WriteLine("Error: -o requires a path argument.");
                    return false;
                }
                outputPath = args[++i];
            }
            else if (args[i].StartsWith("--output="))
            {
                outputPath = args[i]["--output=".Length..];
            }
            else if (args[i].StartsWith("-o="))
            {
                outputPath = args[i]["-o=".Length..];
            }
            else if (args[i] == "--stdout" || args[i] == "-")
            {
                toStdout = true;
            }
            else if (args[i] == "--force" || args[i] == "-f")
            {
                force = true;
            }
            else if (args[i] == "--bare" && isCompile)
            {
                bare = true;
            }
            else if (args[i].StartsWith("--system-type="))
            {
                if (!TryParseSystemType(args[i]["--system-type=".Length..], out var st))
                {
                    return false;
                }
                systemType = st;
            }
            else if (args[i].StartsWith('-'))
            {
                Console.Error.WriteLine($"Error: unknown option '{args[i]}' for {(isCompile ? "compile" : "decompile")}.");
                return false;
            }
            else
            {
                Console.Error.WriteLine($"Error: unexpected argument '{args[i]}'; the input file must come before options.");
                return false;
            }
        }

        // An empty -o / --output= value (e.g. `--output=`) would otherwise reach GetFullPath
        // and throw an unhandled ArgumentException; reject it like any other bad argument.
        if (outputPath is not null && outputPath.Length == 0)
        {
            Console.Error.WriteLine("Error: -o/--output requires a non-empty path.");
            return false;
        }

        if (toStdout && outputPath != null)
        {
            Console.Error.WriteLine("Error: --stdout and -o are mutually exclusive.");
            return false;
        }

        return true;
    }

    // Full-path comparison honoring the filesystem: case-insensitive only on Windows, where
    // paths are; ordinal elsewhere, so `Foo.td` and `foo.td` are distinct on Linux.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Resolves symlinks so an aliased path to the input (e.g. an -o that points at
    /// the same file through a link) is recognized. GetFullPath alone normalizes ./.. but not
    /// links; this resolves the file and its directory. (An ancestor-directory symlink the
    /// leaf doesn't expose is not chased - a portable realpath is not in the BCL.)</summary>
    private static string RealPath(string path)
    {
        var full = IOPath.GetFullPath(path);
        try
        {
            var fi = new System.IO.FileInfo(full);
            var target = fi.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) { return target.FullName; }

            var dir = fi.DirectoryName;
            if (dir is not null)
            {
                var dirReal = new System.IO.DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir;
                return IOPath.Combine(dirReal, fi.Name);
            }
        }
        catch
        {
            // Fall through to the normalized-but-unresolved path.
        }

        return full;
    }

    /// <summary>The output path must never silently overwrite the input, including via a symlink.</summary>
    private static bool OutputCollidesWithInput(string inputPath, string outputPath)
    {
        if (string.Equals(RealPath(inputPath), RealPath(outputPath), PathComparison))
        {
            Console.Error.WriteLine($"Error: output path {outputPath} is the input file; pass -o to choose another.");
            return true;
        }

        return false;
    }

    /// <summary>A defaulted (not -o) output target must not silently clobber an existing file -
    /// several Examples .td/.nap are hand-authored. Refuse unless --force; an explicit -o is
    /// the caller's own choice and overwrites.</summary>
    private static bool DefaultTargetWouldClobber(string outputPath, bool force)
    {
        if (!force && System.IO.File.Exists(outputPath))
        {
            Console.Error.WriteLine($"Error: {outputPath} already exists; pass -o to a different path or --force to overwrite.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Compile a Telidraw source file (.td) to a NAPLPS binary (.nap).
    /// Usage: Telidraw compile input.td [-o output.nap]
    /// When -o is omitted, output goes to input.nap next to the source.
    /// </summary>
    private static int HandleCompileCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Error: Telidraw source file required.");
            Console.Error.WriteLine("Usage: Telidraw compile <file.td> [-o <output.nap>] [--bare] [--system-type=T] [--stdout]");
            return 1;
        }

        var inputPath = args[1];

        if (!TryParseConvertOptions(args, isCompile: true, out var outputPath, out var bare, out var toStdout, out var systemType, out var force))
        {
            return 1;
        }

        if (!System.IO.File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: Input file not found: {inputPath}");
            return 1;
        }

        // Wrong-direction guard: a .nap input is a NAPLPS stream, not Telidraw source.
        if (inputPath.EndsWith(".nap", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: {inputPath} looks like a NAPLPS stream (.nap); use 'decompile' to make a .td.");
            return 1;
        }

        var explicitOutput = outputPath is not null;
        outputPath ??= IOPath.ChangeExtension(inputPath, ".nap");

        if (!toStdout && OutputCollidesWithInput(inputPath, outputPath))
        {
            return 1;
        }

        // Only the DEFAULTED target is clobber-guarded; an explicit -o is the caller's choice.
        if (!toStdout && !explicitOutput && DefaultTargetWouldClobber(outputPath, force))
        {
            return 1;
        }

        try
        {
            var source = System.IO.File.ReadAllText(inputPath);

            // A decompiled source is the COMPLETE byte specification; recompiling it
            // without --bare doubles the CAN+NSR header the compiler prepends.
            if (!bare && source.StartsWith("// Decompiled from .nap"))
            {
                Console.Error.WriteLine("warning: this source was produced by the decompiler; compile with --bare to reproduce the original bytes (without it a fresh CAN+NSR header is prepended).");
            }

            var lexer = new NAPLPS.Telidraw.Lexer(source);
            var tokens = lexer.Tokenize();

            foreach (var diag in lexer.Diagnostics)
            {
                Console.Error.WriteLine($"lex: {diag}");
            }

            if (lexer.Diagnostics.Count > 0)
            {
                Console.Error.WriteLine($"Aborting compile due to {lexer.Diagnostics.Count} lex error(s).");
                return 1;
            }

            var parser = new NAPLPS.Telidraw.Parser(tokens);
            var program = parser.Parse();

            foreach (var diag in parser.Diagnostics)
            {
                Console.Error.WriteLine($"parse: {diag}");
            }

            if (parser.Diagnostics.Count > 0)
            {
                Console.Error.WriteLine($"Aborting compile due to {parser.Diagnostics.Count} parse error(s).");
                return 1;
            }

            var compiler = new NAPLPS.Telidraw.Compiler(program, systemType) { BareFormat = bare };
            var format = compiler.Compile();

            foreach (var diag in compiler.Diagnostics)
            {
                Console.Error.WriteLine($"compile: {diag}");
            }

            if (compiler.Diagnostics.Count > 0)
            {
                Console.Error.WriteLine($"Aborting compile due to {compiler.Diagnostics.Count} compile error(s).");
                return 1;
            }

            if (toStdout)
            {
                using var stdout = Console.OpenStandardOutput();
                var bytes = format.ToBytes();
                stdout.Write(bytes, 0, bytes.Length);
                return 0;
            }

            format.Save(outputPath);
            Console.WriteLine($"Compiled {inputPath} -> {outputPath} ({format.Commands.Count} commands, {new System.IO.FileInfo(outputPath).Length} bytes)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Decompile a NAPLPS byte stream into Telidraw source: the inverse door to the same
    /// engine `compile` fronts. `--bare` compiles of the output reproduce the input bytes
    /// exactly (the corpus round-trip invariant).
    /// </summary>
    private static int HandleDecompileCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Error: NAPLPS input file required.");
            Console.Error.WriteLine("Usage: Telidraw decompile <file.nap> [-o <output.td>] [--system-type=T] [--stdout]");
            return 1;
        }

        var inputPath = args[1];

        if (!TryParseConvertOptions(args, isCompile: false, out var outputPath, out _, out var toStdout, out var forcedType, out var force))
        {
            return 1;
        }

        var explicitOutput = outputPath is not null;

        if (!System.IO.File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: Input file not found: {inputPath}");
            return 1;
        }

        // Wrong-direction guard: a .td input is Telidraw source, not a NAPLPS stream.
        // NAPLPS is a permissive byte format - source text decodes to a "valid" run of
        // text-drawing commands - so this is caught by extension, not by the decode.
        if (inputPath.EndsWith(".td", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Error: {inputPath} looks like Telidraw source (.td); use 'compile' to make a .nap.");
            return 1;
        }

        try
        {
            var format = NaplpsFormat.FromFile(inputPath, forcedType);

            // Hard parse errors (an opcode absent from the in-use table, a truncated
            // definition) mean the stream is not recoverable - abort. NAPLPS decodes
            // almost any bytes to SOME command stream, though, so a clean decode is not
            // proof the input was really NAPLPS; warnings mean the .td is a lossy,
            // best-effort reconstruction and that is reported, not silently ignored.
            foreach (var err in format.Errors.Where(e => e.Severity == NaplpsErrorSeverity.Error))
            {
                Console.Error.WriteLine($"error: {err}");
            }

            if (format.IsErrored)
            {
                Console.Error.WriteLine($"Aborting decompile: {format.Errors.Count(e => e.Severity == NaplpsErrorSeverity.Error)} unrecoverable parse error(s); the output would be incomplete.");
                return 1;
            }

            var warnings = format.Errors.Count(e => e.Severity == NaplpsErrorSeverity.Warning);
            if (warnings > 0)
            {
                Console.Error.WriteLine($"warning: {warnings} decode warning(s); the .td is a best-effort reconstruction of this stream.");
            }

            var source = NAPLPS.Telidraw.Decompiler.Decompile(format);

            if (toStdout)
            {
                Console.Out.Write(source);
                return 0;
            }

            outputPath ??= IOPath.ChangeExtension(inputPath, ".td");

            if (OutputCollidesWithInput(inputPath, outputPath))
            {
                return 1;
            }

            // A defaulted target (foo.nap -> foo.td) must not silently clobber a hand-authored
            // .td; several Examples pairs are exactly that. An explicit -o is the caller's choice.
            if (!explicitOutput && DefaultTargetWouldClobber(outputPath, force))
            {
                return 1;
            }

            System.IO.File.WriteAllText(outputPath, source);
            Console.WriteLine($"Decompiled {inputPath} -> {outputPath} ({format.Commands.Count} commands, {source.Length} chars)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParseSystemType(string value, out NaplpsSystemType systemType)
    {
        switch (value.ToLowerInvariant())
        {
            case "naplps": systemType = NaplpsSystemType.NAPLPS; return true;
            case "prodigy": systemType = NaplpsSystemType.Prodigy; return true;
            case "telidon": systemType = NaplpsSystemType.Telidon; return true;
            default:
                Console.Error.WriteLine($"Error: unknown system type '{value}' (naplps|prodigy|telidon).");
                systemType = NaplpsSystemType.NAPLPS;
                return false;
        }
    }

    private static int HandleDiffCommand(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Error: Two input files required.");
            Console.Error.WriteLine("Usage: Telidraw diff <file1> <file2> [--mode=visual|text] [--size=WxH] [--output=file]");
            return 1;
        }

        var fileA = args[1];
        var fileB = args[2];
        var mode = "text";
        var size = "1024x768";
        string? outputFile = null;

        for (int i = 3; i < args.Length; i++)
        {
            if (args[i].StartsWith("--mode="))
            {
                mode = args[i]["--mode=".Length..].ToLowerInvariant();
            }
            else if (args[i].StartsWith("--size="))
            {
                size = args[i]["--size=".Length..];
            }
            else if (args[i].StartsWith("--output="))
            {
                outputFile = args[i]["--output=".Length..];
            }
        }

        if (!File.Exists(fileA))
        {
            Console.Error.WriteLine($"Error: File not found: {fileA}");
            return 1;
        }

        if (!File.Exists(fileB))
        {
            Console.Error.WriteLine($"Error: File not found: {fileB}");
            return 1;
        }

        try
        {
            var a = NaplpsFormat.FromFile(fileA);
            var b = NaplpsFormat.FromFile(fileB);

            if (mode == "visual")
            {
                if (!ParseSize(size, out var width, out var height))
                {
                    Console.Error.WriteLine($"Error: Invalid size format: {size}");
                    return 1;
                }

                using var diff = NapDiff.VisualDiff(a, b, new SixLabors.ImageSharp.Size(width, height));
                var outPath = outputFile ?? "diff.png";
                diff.SaveAsPng(outPath);
                Console.Error.WriteLine($"Visual diff saved to: {outPath}");
            }
            else
            {
                var entries = NapDiff.CommandDiff(a, b);
                int diffCount = 0;

                foreach (var entry in entries)
                {
                    if (entry.IsDifferent)
                    {
                        diffCount++;
                        string idxA = entry.IndexA.HasValue ? entry.IndexA.Value.ToString() : "-";
                        string idxB = entry.IndexB.HasValue ? entry.IndexB.Value.ToString() : "-";

                        if (entry.CommandA == "")
                        {
                            Console.WriteLine($"+ [{idxB}] {entry.CommandB}");
                        }
                        else if (entry.CommandB == "")
                        {
                            Console.WriteLine($"- [{idxA}] {entry.CommandA}");
                        }
                        else
                        {
                            Console.WriteLine($"- [{idxA}] {entry.CommandA}");
                            Console.WriteLine($"+ [{idxB}] {entry.CommandB}");
                        }
                    }
                }

                Console.Error.WriteLine($"{diffCount} differences found in {entries.Count} commands.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

#endif

    internal static bool ParseSize(string size, out int width, out int height)
    {
        width = 0; height = 0;
        var parts = size.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height);
    }

#if !BROWSER
    private static int ExportPng(DrawContext drawContext, string? outputFile, bool useStdout, string? atFrames = null)
    {
        if (atFrames == null)
        {
            drawContext.Render();
            SavePng(drawContext, outputFile, useStdout);
            return 0;
        }

        // Export specific frames: --at=1,2-5,500,1200
        var indices = ParseFrameRanges(atFrames);
        var baseName = outputFile != null ? IOPath.GetFileNameWithoutExtension(outputFile) : "frame";
        var baseDir = outputFile != null ? IOPath.GetDirectoryName(outputFile) ?? "." : ".";

        foreach (var idx in indices)
        {
            var ctx = new DrawContext(drawContext.NAPLPS, drawContext.Size);
            ctx.Render((uint)idx);

            var framePath = IOPath.Combine(baseDir, $"{baseName}_{idx:D4}.png");
            ctx.Image.SaveAsPng(framePath);
            Console.Error.WriteLine($"Frame {idx}: {framePath}");
            ctx.Image.Dispose();
        }

        return 0;
    }

    private static void SavePng(DrawContext drawContext, string? outputFile, bool useStdout)
    {
        if (useStdout)
        {
            using var stdout = Console.OpenStandardOutput();
            drawContext.Image.SaveAsPng(stdout);
        }
        else if (outputFile != null)
        {
            drawContext.Image.SaveAsPng(outputFile);
            Console.Error.WriteLine($"Exported to: {outputFile}");
        }
    }

    private static int ExportApng(DrawContext drawContext, string? outputFile, bool useStdout, int delay, bool loop = false, int blinkCycles = 0)
    {
        if (useStdout)
        {
            // The writer patches the frame count into acTL at the end, so it needs to seek and
            // stdout cannot. A MemoryStream holds only the compressed file - roughly a megabyte -
            // rather than every frame at full canvas size.
            using var buffer = new MemoryStream();
            drawContext.RenderApngToStream(buffer, delay, loop, blinkCycles);

            using var stdout = Console.OpenStandardOutput();
            buffer.Position = 0;
            buffer.CopyTo(stdout);

            return 0;
        }

        if (outputFile != null)
        {
            var visualFrames = drawContext.RenderApngToFile(outputFile, delay, loop, blinkCycles);

            Console.Error.WriteLine($"Exported APNG with {visualFrames} frames to: {outputFile}");
        }

        return 0;
    }

    private static int ExportGif(DrawContext drawContext, string? outputFile, bool useStdout, bool loop, int delay)
    {
        using var gif = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(drawContext.Size.Width, drawContext.Size.Height);

        var gifMetaData = gif.Metadata.GetGifMetadata();
        gifMetaData.RepeatCount = loop ? (ushort)0 : (ushort)1; // 0 = loop forever, 1 = play once

        // Render each frame and add to GIF
        for (uint i = 0; i <= drawContext.TotalFrames; i++)
        {
            drawContext.Render(i);

            // Clone the current frame
            var frame = drawContext.Image.Clone();

            // Set frame delay (in hundredths of a second)
            var frameMetadata = frame.Frames.RootFrame.Metadata.GetGifMetadata();
            frameMetadata.FrameDelay = delay;

            if (i == 0)
            {
                // First frame replaces the root frame
                gif.Frames.RootFrame.ProcessPixelRows(frame.Frames.RootFrame, (accessorGif, accessorFrame) =>
                {
                    for (int y = 0; y < accessorGif.Height; y++)
                    {
                        var rowGif = accessorGif.GetRowSpan(y);
                        var rowFrame = accessorFrame.GetRowSpan(y);
                        rowFrame.CopyTo(rowGif);
                    }
                });
                gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delay;
            }
            else
            {
                // Add subsequent frames
                gif.Frames.AddFrame(frame.Frames.RootFrame);
            }

            frame.Dispose();
        }

        if (useStdout)
        {
            using var stdout = Console.OpenStandardOutput();
            gif.SaveAsGif(stdout);
        }
        else if (outputFile != null)
        {
            gif.SaveAsGif(outputFile);
            Console.Error.WriteLine($"Exported GIF with {drawContext.TotalFrames + 1} frames to: {outputFile}");
        }

        return 0;
    }

    private static int ExportPaletteAnimGif(DrawContext drawContext, string? outputFile, bool useStdout, bool loop, int delay, int totalFrames)
    {
        // First render the full file with palette animation mode
        drawContext.PaletteAnimationMode = true;
        drawContext.Render();

        // Initialize blink animator
        drawContext.InitializeBlinkAnimator();

        Console.Error.WriteLine($"Blink processes: {drawContext.NAPLPS.State.BlinkProcesses.Count}");

        if (drawContext.BlinkAnimator == null || !drawContext.BlinkAnimator.HasActiveProcesses)
        {
            Console.Error.WriteLine("Warning: No active blink processes found. Exporting static image as GIF.");
            // Fall back to single-frame GIF
            using var staticGif = drawContext.Image.Clone();
            var outPath = outputFile ?? "palette_anim.gif";
            staticGif.SaveAsGif(outPath);
            Console.Error.WriteLine($"Exported to: {outPath}");
            return 0;
        }

        using var gif = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(drawContext.Size.Width, drawContext.Size.Height);

        var gifMetaData = gif.Metadata.GetGifMetadata();
        gifMetaData.RepeatCount = loop ? (ushort)0 : (ushort)1;

        // Capture the initial frame
        gif.ProcessPixelRows(drawContext.Image, (dst, src) =>
        {
            for (int y = 0; y < src.Height; y++)
            {
                src.GetRowSpan(y).CopyTo(dst.GetRowSpan(y));
            }
        });
        gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delay;

        // Tick the blink animator and capture frames
        const int tickMs = 16; // ~60Hz tick rate
        for (int frame = 1; frame < totalFrames; frame++)
        {
            bool changed = drawContext.TickBlink(tickMs);

            if (changed || frame == totalFrames - 1)
            {
                // Add frame to GIF
                var frameClone = drawContext.Image.Clone();
                var frameMetadata = frameClone.Frames.RootFrame.Metadata.GetGifMetadata();
                frameMetadata.FrameDelay = delay;
                gif.Frames.AddFrame(frameClone.Frames.RootFrame);
                frameClone.Dispose();
            }
        }

        var path = outputFile ?? "palette_anim.gif";

        if (useStdout)
        {
            using var stdout = Console.OpenStandardOutput();
            gif.SaveAsGif(stdout);
        }
        else
        {
            gif.SaveAsGif(path);
            Console.Error.WriteLine($"Exported palette animation GIF with {gif.Frames.Count} frames to: {path}");
        }

        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
#endif

    public static async Task ShowAboutBox()
    {
        var iconBitmap = new Bitmap(AssetLoader.Open(new Uri("avares://Telidraw/Assets/naplps.ico")));

        var bigDescription = "The North American Presentation Level Protocol Syntax (NAPLPS) was a pioneering \ngraphic display standard that emerged during the early era of online services. \nAlthough largely confined to videotex and teletext experiments, it represented a \nmeaningful step toward a unified way of depicting graphics across disparate \nterminals. NAPLPS introduced vector-based images, scalable fonts, and a color \npalette that was advanced for its time, influencing subsequent standards.";

        var messageBoxParams = new MessageBoxCustomParams
        {
            ContentHeader = $"Version: {Version}\n\nA modern toolbox to read, save, create, and alter NAPLPS files, new and old!\nAn Open Source Project: https://github.com/FoxCouncil/NAPLPS",
            ContentTitle = "About Telidraw",
            ContentMessage = $"{bigDescription}\n\nCreated by Fox & Contributors!\n\tpheller\n\tportyspice",
            ButtonDefinitions = [new ButtonDefinition { Name = "Cool Beans!", IsDefault = true }],
            WindowIcon = Telidraw.Services.Shell.AppIcon, // Null under single-view: constructing one there throws.
            ImageIcon = iconBitmap,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 520,
            MaxWidth = 820,
            MinHeight = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
        };

        await Telidraw.Services.Shell.ShowCustomMessageAsync(messageBoxParams);
    }

    public static async Task<bool> ShowQuestionDialogBox(string title, string question)
    {
        var iconBitmap = new Bitmap(AssetLoader.Open(new Uri("avares://Telidraw/Assets/naplps.ico")));

        var messageBoxParams = new MessageBoxCustomParams
        {
            ContentHeader = title,
            ContentTitle = "Question",
            ContentMessage = question,
            ButtonDefinitions = [new ButtonDefinition { Name = "Yes", IsDefault = true }, new ButtonDefinition { Name = "No" }],
            WindowIcon = Telidraw.Services.Shell.AppIcon, // Null under single-view: constructing one there throws.
            ImageIcon = iconBitmap,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            // Default dialog chrome cuts off multi-line prompts. Give a sensible minimum,
            // let height auto-size to content, and allow the user to resize if still tight.
            MinWidth = 460,
            MaxWidth = 720,
            MinHeight = 200,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
        };

        var result = await Telidraw.Services.Shell.ShowCustomMessageAsync(messageBoxParams);

        return result == "Yes";
    }

    private static string GetLibraryVersion()
    {
        var assembly = typeof(NaplpsFormat).Assembly;

        var info = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();

        if (!string.IsNullOrWhiteSpace(info?.Version))
        {
            return info.Version.ToString();
        }

        return assembly.GetName().Version?.ToString() ?? "?.?.?";
    }
}
