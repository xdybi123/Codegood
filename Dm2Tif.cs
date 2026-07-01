// Dm2Tif.cs
// Console application: converts DM3/DM4 files to 8-bit greyscale TIFF.
// Requires DigitalMicrographLib.cs (same project or referenced DLL).
// No System.Drawing dependency: TIFFs are written directly from the
// library's 1-D 8-bit greyscale pixel array.
//
// Compile example (C# 4.0 / .NET 4):
//   csc /target:exe /out:Dm2Tif.exe Dm2Tif.cs DigitalMicrographLib.cs
//
// Usage:
//   Dm2Tif.exe input.dm3
//   Dm2Tif.exe input.dm4 output_prefix
//   Dm2Tif.exe *.dm3
//
// Multiple images in one file → <prefix>_0.tif, <prefix>_1.tif, …
// Single image → <prefix>.tif

using System;
using System.Collections.Generic;
using System.IO;
using DigitalMicrograph;

class Dm2Tif
{
    static int Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 1; }

        List<string> inputFiles = new List<string>();
        string outputPrefix = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool isGlob = a.IndexOf('*') >= 0 || a.IndexOf('?') >= 0;
            if (isGlob)
            {
                string dir = Path.GetDirectoryName(a);
                string pat = Path.GetFileName(a);
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
                outputPrefix = a; // last non-glob arg after inputs = output prefix
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

            string prefix = outputPrefix;
            if (string.IsNullOrEmpty(prefix))
            {
                string dir  = Path.GetDirectoryName(inputPath);
                string stem = Path.GetFileNameWithoutExtension(inputPath);
                prefix = string.IsNullOrEmpty(dir) ? stem : Path.Combine(dir, stem);
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
                string outPath = (reader.Images.Count == 1)
                    ? prefix + ".tif"
                    : string.Format("{0}_{1}.tif", prefix, idx);

                try
                {
                    WriteGrayTiff(outPath, img.Pixels, img.Width, img.Height);

                    Console.WriteLine(
                        "  Saved [{0}] {1}x{2} px  |  pixel size: {3:G6} {4} x {5:G6} {6}  →  {7}",
                        idx, img.Width, img.Height,
                        img.PixelSizeX, img.PixelSizeUnitX,
                        img.PixelSizeY, img.PixelSizeUnitY, outPath);

                    if (img.HasContrastLimits)
                        Console.WriteLine("        contrast limits (raw units): low={0:G6}  high={1:G6}",
                                          img.ContrastLow, img.ContrastHigh);
                    else
                        Console.WriteLine("        contrast: auto (min/max); no limits stored in file");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("  Error saving '{0}': {1}", outPath, ex.Message);
                    exitCode = 1;
                }
            }
        }

        return exitCode;
    }

    // -------------------------------------------------------------------------
    // Minimal baseline TIFF writer: 8-bit greyscale, little-endian, uncompressed,
    // single strip. Layout: 8-byte header, image data, then the IFD.
    // -------------------------------------------------------------------------
    static void WriteGrayTiff(string path, byte[] pixels, int width, int height)
    {
        if (pixels == null) throw new ArgumentNullException("pixels");
        int dataLen = width * height;
        if (pixels.Length < dataLen) throw new ArgumentException("pixel array too small");

        uint imageDataOffset = 8;
        uint ifdOffset = (uint)(8 + dataLen);

        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs)) // BinaryWriter is little-endian
        {
            // Header
            w.Write((byte)'I'); w.Write((byte)'I'); // little-endian byte order
            w.Write((ushort)42);                    // TIFF magic
            w.Write(ifdOffset);                     // offset of first IFD

            // Image data (one strip, row-major, no padding)
            w.Write(pixels, 0, dataLen);

            // IFD: entries MUST be written in ascending tag order.
            const ushort SHORT = 3, LONG = 4;
            w.Write((ushort)9);                     // entry count
            Entry(w, 256, LONG,  1, (uint)width);   // ImageWidth
            Entry(w, 257, LONG,  1, (uint)height);  // ImageLength
            Entry(w, 258, SHORT, 1, 8);             // BitsPerSample
            Entry(w, 259, SHORT, 1, 1);             // Compression = none
            Entry(w, 262, SHORT, 1, 1);             // Photometric = BlackIsZero
            Entry(w, 273, LONG,  1, imageDataOffset); // StripOffsets
            Entry(w, 277, SHORT, 1, 1);             // SamplesPerPixel
            Entry(w, 278, LONG,  1, (uint)height);  // RowsPerStrip (whole image)
            Entry(w, 279, LONG,  1, (uint)dataLen); // StripByteCounts
            w.Write((uint)0);                       // next IFD offset (none)
        }
    }

    // Writes one 12-byte IFD entry. For SHORT values the 2-byte value sits in the
    // low bytes of the 4-byte value field, which is correct on little-endian.
    static void Entry(BinaryWriter w, ushort tag, ushort type, uint count, uint value)
    {
        w.Write(tag);
        w.Write(type);
        w.Write(count);
        w.Write(value);
    }

    static void PrintUsage()
    {
        Console.WriteLine("Dm2Tif — convert DM3/DM4 files to 8-bit greyscale TIFF");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Dm2Tif.exe <input.dm3|dm4> [output_prefix]");
        Console.WriteLine("  Dm2Tif.exe *.dm3");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  Dm2Tif.exe image.dm3            → image.tif");
        Console.WriteLine("  Dm2Tif.exe image.dm4 converted  → converted.tif (or _0,_1… if multiple)");
        Console.WriteLine("  Dm2Tif.exe *.dm3                → converts every DM3 in the folder");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  • Output is 8-bit greyscale, normalised using the file's display");
        Console.WriteLine("    contrast limits when present, otherwise the data min/max.");
        Console.WriteLine("  • Pixel size and contrast limits are printed to the console.");
        Console.WriteLine("  • Requires .NET 4.0 or later; no System.Drawing dependency.");
    }
}
