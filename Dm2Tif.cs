// Dm2Tif.cs
// Console application: converts DM3/DM4 files to TIFF.
// Requires DigitalMicrographLib.cs (same project or referenced DLL).
//
// Compile example (C# 4.0 / .NET 4):
//   csc /target:exe /out:Dm2Tif.exe Dm2Tif.cs DigitalMicrographLib.cs
//
// Usage:
//   Dm2Tif.exe input.dm3
//   Dm2Tif.exe input.dm4 output_prefix
//   Dm2Tif.exe *.dm3          (glob expansion performed by the shell or see below)
//
// If the file contains more than one image the output files are named:
//   <outputPrefix>_0.tif, <outputPrefix>_1.tif, …
// If only one image is found the suffix is omitted:
//   <outputPrefix>.tif

using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using DigitalMicrograph;

class Dm2Tif
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        // Collect input files (support simple *.dm3 / *.dm4 globs on Windows,
        // where the shell does not expand wildcards automatically).
        List<string> inputFiles = new List<string>();
        string outputPrefix = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool isGlob = a.IndexOf('*') >= 0 || a.IndexOf('?') >= 0;
            if (isGlob)
            {
                string dir  = Path.GetDirectoryName(a);
                string pat  = Path.GetFileName(a);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                try
                {
                    string[] found = Directory.GetFiles(dir, pat);
                    Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                    inputFiles.AddRange(found);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Warning: could not expand glob '{0}': {1}", a, ex.Message);
                }
            }
            else if (i == args.Length - 1 && inputFiles.Count > 0)
            {
                // Last argument with no glob and inputs already collected → treat as prefix
                outputPrefix = a;
            }
            else
            {
                inputFiles.Add(a);
            }
        }

        if (inputFiles.Count == 0)
        {
            Console.Error.WriteLine("Error: no input files found.");
            return 1;
        }

        int exitCode = 0;

        foreach (string inputPath in inputFiles)
        {
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Error: file not found: {0}", inputPath);
                exitCode = 1;
                continue;
            }

            // Derive default output prefix from input file name
            string prefix = outputPrefix;
            if (string.IsNullOrEmpty(prefix))
            {
                string dir  = Path.GetDirectoryName(inputPath);
                string stem = Path.GetFileNameWithoutExtension(inputPath);
                prefix = string.IsNullOrEmpty(dir)
                    ? stem
                    : Path.Combine(dir, stem);
            }

            Console.WriteLine("Reading: {0}", inputPath);

            DmReader reader;
            try
            {
                reader = new DmReader(inputPath);
                reader.Read();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("  Error reading file: {0}", ex.Message);
                exitCode = 1;
                continue;
            }

            if (reader.Images.Count == 0)
            {
                Console.WriteLine("  No images found in file.");
                continue;
            }

            for (int idx = 0; idx < reader.Images.Count; idx++)
            {
                DmImage img = reader.Images[idx];

                // Build output path
                string outPath = (reader.Images.Count == 1)
                    ? prefix + ".tif"
                    : string.Format("{0}_{1}.tif", prefix, idx);

                try
                {
                    img.Bitmap.Save(outPath, ImageFormat.Tiff);

                    Console.WriteLine(
                        "  Saved [{0}] {1}x{2} px  |  pixel size: {3:G6} {4} x {5:G6} {6}  →  {7}",
                        idx,
                        img.Width, img.Height,
                        img.PixelSizeX, img.PixelSizeUnitX,
                        img.PixelSizeY, img.PixelSizeUnitY,
                        outPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("  Error saving '{0}': {1}", outPath, ex.Message);
                    exitCode = 1;
                }
                finally
                {
                    // Free bitmap memory as soon as it's saved
                    img.Bitmap.Dispose();
                }
            }
        }

        return exitCode;
    }

    static void PrintUsage()
    {
        Console.WriteLine("Dm2Tif — convert DM3/DM4 files to TIFF");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Dm2Tif.exe <input.dm3|dm4> [output_prefix]");
        Console.WriteLine("  Dm2Tif.exe *.dm3");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  Dm2Tif.exe image.dm3");
        Console.WriteLine("    → image.tif");
        Console.WriteLine();
        Console.WriteLine("  Dm2Tif.exe image.dm4 converted");
        Console.WriteLine("    → converted.tif   (or converted_0.tif, converted_1.tif … if multiple images)");
        Console.WriteLine();
        Console.WriteLine("  Dm2Tif.exe *.dm3");
        Console.WriteLine("    → converts every DM3 in the current directory");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  • All image types are normalised to 8-bit greyscale in the output TIFF.");
        Console.WriteLine("  • Pixel-size calibration is printed to the console but not embedded in");
        Console.WriteLine("    the TIFF (GDI+ does not expose DM-specific metadata tags).");
        Console.WriteLine("  • Requires .NET 4.0 or later on Windows.");
    }
}
