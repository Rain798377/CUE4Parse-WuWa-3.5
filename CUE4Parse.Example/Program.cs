using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;
using Newtonsoft.Json;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace CUE4Parse.Example
{
    // ---------------------------------------------------------------------------------------------
    // Verification harness for Wuthering Waves "OodleTextureStorageProviderFactory" textures.
    //
    // Goal: figure out how the mip chain stored inside UOodleTextureStorageProviderFactory is
    // compressed, so we can implement proper texture export.
    //
    // HOW TO RUN (Windows, so the native Oodle dll can be downloaded/loaded):
    //   1. Put the sample .uasset/.uexp files in a folder.
    //   2. Set SampleDir below (or pass the folder as the first command-line arg).
    //   3. dotnet run --project CUE4Parse.Example -c Release
    //
    // The harness will, for each texture:
    //   * parse the factory (SizeX/SizeY, compressed payload size, raw header ints)
    //   * report whether the following Texture2D now parses (format + mips)
    //   * try to Oodle-decompress the payload at several candidate uncompressed sizes
    //     (full BC7 mip chain, top mip only, etc.) and report which one succeeds.
    // ---------------------------------------------------------------------------------------------
    public static class Program
    {
        // EDIT THIS (or pass as args[0]):
        private const string SampleDir = @"D:\FModel\Output\Exports\Client\Content\Aki\Character\Role\FemaleXL\Qingxiao\R2T1QingxiaoMd10011\Model";

        private const EGame Game = EGame.GAME_WutheringWaves;

        public static void Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console(theme: AnsiConsoleTheme.Literate)
                .CreateLogger();

            var (pathArg, outputDirArg, exportFlag) = ParseArgs(args);

            if (pathArg is null && args.Length > 0)
            {
                PrintUsage();
                return;
            }

            var dir = pathArg ?? SampleDir;
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"Folder not found: {dir}");
                PrintUsage();
                return;
            }

            var decodedFolder = outputDirArg ?? Path.Combine(dir, "Decoded");
            Console.WriteLine($"Path:   {dir}");
            if (exportFlag)
            {
                Directory.CreateDirectory(decodedFolder);
                Console.WriteLine($"Export: on, writing PNGs to {decodedFolder}");
            }
            else
            {
                Console.WriteLine("Export: off (diagnostics only, no files written). pass --export to write PNGs");
            }

            // Native Oodle: on Windows this downloads oodle-data-shared.dll from the OodleUE release.
            try
            {
                OodleHelper.Initialize();
                Console.WriteLine(OodleHelper.Instance is null
                    ? "!! Oodle didn't initialize, decompression tests + some textures will be skipped"
                    : "Oodle initialized OK");
            }
            catch (Exception e)
            {
                Console.WriteLine($"!! Oodle init failed: {e.Message}");
            }

            // Native Detex: extracts the embedded Detex.dll next to the exe and loads it.
            if (DetexHelper.LoadDll())
            {
                DetexHelper.Initialize(DetexHelper.DLL_NAME);
                Console.WriteLine("Detex initialized OK");
            }
            else
            {
                Console.WriteLine("!! Detex DLL extraction failed");
            }

            // AllDirectories so we don't miss stuff sitting in subfolders
            var provider = new DefaultFileProvider(dir, SearchOption.AllDirectories, new VersionContainer(Game));
            provider.Initialize();

            int converted = 0, skipped = 0, failed = 0;

            foreach (var file in Directory.GetFiles(dir, "*.uasset", SearchOption.AllDirectories))
            {
                // package name relative to dir, no extension, forward slashes (CUE4Parse wants it that way)
                var relativeNoExt = Path.GetRelativePath(dir, file);
                relativeNoExt = relativeNoExt.Substring(0, relativeNoExt.Length - Path.GetExtension(relativeNoExt).Length);
                var packageName = relativeNoExt.Replace('\\', '/');

                Console.WriteLine($"\n############################## {packageName} ##############################");
                try
                {
                    var exports = provider.LoadPackage(packageName + ".uasset").GetExports();

                    UOodleTextureStorageProviderFactory? factory = null;
                    var textures = new List<UTexture2D>();
                    foreach (var e in exports)
                    {
                        if (e is UOodleTextureStorageProviderFactory f) factory = f;
                        if (e is UTexture2D t) textures.Add(t);
                    }

                    if (factory is not null)
                    {
                        Console.WriteLine($"Factory: SizeX={factory.SizeX} SizeY={factory.SizeY} " +
                                          $"flags=0x{factory.BulkDataFlags:X} ElementCount={factory.ElementCount} " +
                                          $"SizeOnDisk={factory.SizeOnDisk} payload={factory.CompressedData.Length} bytes");
                        Console.WriteLine("ModeCounts: " + string.Join(", ", factory.ModeCounts));
                    }

                    if (textures.Count == 0)
                    {
                        Console.WriteLine("No UTexture2D export found in this package.");
                    }

                    string? outSubDir = null;
                    if (exportFlag)
                    {
                        outSubDir = Path.Combine(decodedFolder, Path.GetDirectoryName(relativeNoExt) ?? "");
                        Directory.CreateDirectory(outSubDir);
                    }

                    // Saves decoded textures as png files in SampleDir\Decoded
                    foreach (var texture in textures)
                    {
                        Console.WriteLine($"Texture2D: {texture.Name} Format={texture.Format} " +
                                          $"PlatformSize={texture.PlatformData.SizeX}x{texture.PlatformData.SizeY} " +
                                          $"Mips={texture.PlatformData.Mips.Length}");

                        if (!exportFlag) continue; // diagnostics only, don't touch disk

                        var (didConvert, wasSkipped) = ConvertTextureToPng(texture, outSubDir!);
                        if (didConvert) converted++;
                        else if (wasSkipped) skipped++;
                        else failed++;
                    }

                    if (factory is not null)
                        TryDecompress(factory);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"FAILED: {ex}");
                    failed++;
                }
            }

            if (exportFlag)
                Console.WriteLine($"\nDone. Converted={converted} Skipped(already PNG)={skipped} Failed={failed}");
            else
                Console.WriteLine("\nDone. (diagnostics only, nothing was written -- pass --export to write PNGs)");
        }

        private static (string? path, string? outputDir, bool export) ParseArgs(string[] args)
        {
            string? path = null;
            string? outputDir = null;
            var export = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--path":
                    case "-p":
                        if (i + 1 < args.Length) path = args[++i];
                        break;
                    case "--output-dir":
                    case "-o":
                        if (i + 1 < args.Length) outputDir = args[++i];
                        break;
                    case "--export":
                    case "-e":
                        export = true;
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        Environment.Exit(0);
                        break;
                }
            }

            return (path, outputDir, export);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  CUE4Parse.Example.exe --path \"<folder with .uasset files>\" [--export] [--output-dir \"<folder>\"]");
            Console.WriteLine();
            Console.WriteLine("  --path, -p          folder to scan for .uasset files (required)");
            Console.WriteLine("  --export, -e        actually write PNGs to disk. without this it's diagnostics only");
            Console.WriteLine("  --output-dir, -o    where to dump the PNGs when --export is set, defaults to <path>\\Decoded");
        }

        // turns one texture export into a png in outDir, unless it's already one:
        //  - a png already sitting there from a previous run -> skip it
        //  - texture format is already raw png bytes (some games do this for small UI icons) -> just copy the bytes, no re-encoding needed
        // returns (didConvert, wasSkipped). if both are false, something went wrong.
        private static (bool didConvert, bool wasSkipped) ConvertTextureToPng(UTexture2D texture, string outDir)
        {
            var outPng = Path.Combine(outDir, $"{texture.Name}.png");

            if (File.Exists(outPng))
            {
                Console.WriteLine($"  [skip] {texture.Name} already converted -> {outPng}");
                return (false, true);
            }

            try
            {
                if (texture.Format.ToString().Contains("PNG", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = texture.PlatformData.Mips.FirstOrDefault()?.BulkData?.Data;
                    if (raw is { Length: > 0 })
                    {
                        File.WriteAllBytes(outPng, raw);
                        Console.WriteLine($"  [copy] {texture.Name} already PNG data -> {outPng}");
                        return (true, false);
                    }
                }

                var bitmap = texture.Decode();
                if (bitmap is null)
                {
                    Console.WriteLine($"  [fail] {texture.Name}: Decode() returned null");
                    return (false, false);
                }

                var pngBytes = bitmap.Encode(ETextureFormat.Png, false, out _);
                File.WriteAllBytes(outPng, pngBytes);
                Console.WriteLine($"  [ok]   {texture.Name} -> {outPng}");
                return (true, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [fail] {texture.Name}: {ex.Message}");
                return (false, false);
            }
        }

        private static void TryDecompress(UOodleTextureStorageProviderFactory factory)
        {
            if (OodleHelper.Instance is null || factory.CompressedData.Length == 0)
                return;

            Console.WriteLine("Payload first 32 bytes: " + Convert.ToHexString(factory.CompressedData, 0, Math.Min(32, factory.CompressedData.Length)));

            var candidates = new List<(string label, int size)>();

            // BC7 = 16 bytes per 4x4 block.
            int Bc7(int w, int h) => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16;

            var full = 0;
            {
                int w = factory.SizeX, h = factory.SizeY;
                while (true)
                {
                    full += Bc7(w, h);
                    if (w <= 1 && h <= 1) break;
                    w = Math.Max(1, w / 2);
                    h = Math.Max(1, h / 2);
                }
            }
            candidates.Add(("BC7 full mip chain", full));
            candidates.Add(("BC7 top mip only", Bc7(factory.SizeX, factory.SizeY)));

            // A few defensive extras in case block rounding / alignment differs.
            candidates.Add(("SizeX*SizeY (1 byte/px)", factory.SizeX * factory.SizeY));
            candidates.Add(("SizeX*SizeY*4 (RGBA)", factory.SizeX * factory.SizeY * 4));

            foreach (var (label, size) in candidates)
            {
                if (size <= 0) continue;
                try
                {
                    var dst = new byte[size];
                    var decoded = OodleHelper.Instance.Decompress(factory.CompressedData, dst);
                    var ok = decoded == size;
                    Console.WriteLine($"  [{(ok ? "MATCH" : "    ")}] {label}: dst={size} -> decoded={decoded}" +
                                      (decoded > 0 ? $"  first16={Convert.ToHexString(dst, 0, Math.Min(16, (int)decoded))}" : ""));
                    if (ok)
                    {
                        var outPath = Path.Combine(Path.GetTempPath(), $"decoded_{size}.bc7.bin");
                        File.WriteAllBytes(outPath, dst);
                        Console.WriteLine($"        -> wrote decoded payload to {outPath}");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"  [FAIL ] {label}: dst={size} -> {e.Message}");
                }
            }
        }
    }
}