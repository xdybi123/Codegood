// DigitalMicrographLib.cs
// C# 4.0 library for reading Gatan Digital Micrograph DM3/DM4 files.
// Focuses on image extraction and pixel-size calibration.
// Images are stored in RAM as System.Drawing.Bitmap (greyscale, 32-bpp ARGB).
//
// DM format reference: http://www.er-c.org/cbb/info/dmformat/
// Based on the open-source RosettaSciIO / HyperSpy DM reader (GPL-3.0).
//
// Compile example (C# 4.0 / .NET 4):
//   csc /target:library /out:DigitalMicrographLib.dll DigitalMicrographLib.cs
//
// Usage:
//   var reader = new DigitalMicrograph.DmReader("image.dm3");
//   reader.Read();
//   foreach (var img in reader.Images)
//   {
//       Console.WriteLine("Size: {0}x{1}", img.Width, img.Height);
//       Console.WriteLine("Pixel size X: {0} {1}", img.PixelSizeX, img.PixelSizeUnitX);
//       Console.WriteLine("Pixel size Y: {0} {1}", img.PixelSizeY, img.PixelSizeUnitY);
//       img.Bitmap.Save("out.tif", System.Drawing.Imaging.ImageFormat.Tiff);
//   }

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DigitalMicrograph
{
    // =========================================================================
    // Public result type
    // =========================================================================

    /// <summary>One image (or image slice) extracted from a DM3/DM4 file.</summary>
    public class DmImage
    {
        /// <summary>Greyscale bitmap stored entirely in RAM.</summary>
        public Bitmap Bitmap { get; internal set; }
        /// <summary>Pixel width of the physical calibration in X (columns).</summary>
        public double PixelSizeX { get; internal set; }
        /// <summary>Pixel height of the physical calibration in Y (rows).</summary>
        public double PixelSizeY { get; internal set; }
        /// <summary>Unit string for X calibration (e.g. "nm", "Å", "µm").</summary>
        public string PixelSizeUnitX { get; internal set; }
        /// <summary>Unit string for Y calibration.</summary>
        public string PixelSizeUnitY { get; internal set; }
        /// <summary>Image width in pixels.</summary>
        public int Width { get; internal set; }
        /// <summary>Image height in pixels.</summary>
        public int Height { get; internal set; }
        /// <summary>Raw DM data-type code (1=int16, 3=uint16, 5=float32, etc.).</summary>
        public int DataType { get; internal set; }
    }

    // =========================================================================
    // Internal tag-tree
    // =========================================================================

    internal class TagNode
    {
        public bool IsGroup;          // true = group/directory, false = data leaf
        public string Name;
        public List<TagNode> Children = new List<TagNode>(); // groups
        public object Value;          // data leaves: scalar / string / array / long offset
        public bool IsImageDataPayload; // special: raw pixel array, value = stream offset after payload
    }

    // =========================================================================
    // Reader
    // =========================================================================

    /// <summary>
    /// Reads DM3/DM4 files and exposes the contained images via
    /// <see cref="Images"/>.  Call <see cref="Read"/> first.
    /// </summary>
    public class DmReader
    {
        // DM encoding type constants (same as in the spec and Python source)
        private const int ET_INT8    =  8;
        private const int ET_BOOL    =  7;  // stored as 4 bytes
        private const int ET_INT16   =  1;
        private const int ET_INT32   =  2;
        private const int ET_INT64   = 10;
        private const int ET_UINT8   =  9;
        private const int ET_UINT16  =  3;
        private const int ET_UINT32  =  4;
        private const int ET_UINT64  = 11;
        private const int ET_FLOAT32 =  5;
        private const int ET_FLOAT64 =  6;
        private const int ET_COMPLEX8  = 12;  // 2 x float32
        private const int ET_STRUCT  = 15;
        private const int ET_STRING  = 18;
        private const int ET_ARRAY   = 20;

        private readonly string _path;
        private BinaryReader _br;
        private int  _dmVersion;
        private bool _littleEndian;

        /// <summary>Images found in the file after <see cref="Read"/> completes.</summary>
        public List<DmImage> Images { get; private set; }

        public DmReader(string filePath)
        {
            if (filePath == null) throw new ArgumentNullException("filePath");
            _path  = filePath;
            Images = new List<DmImage>();
        }

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// Parse the DM3/DM4 file.  Populates <see cref="Images"/>.
        /// </summary>
        public void Read()
        {
            Images.Clear();
            using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (_br = new BinaryReader(fs, Encoding.ASCII))
            {
                ParseHeader();
                TagNode root = new TagNode { IsGroup = true, Name = "root" };
                ulong nRoot = ReadGroupPreamble();
                ParseGroup(nRoot, root, "root");
                ExtractImages(root);
            }
        }

        // =====================================================================
        // File header
        // =====================================================================

        private void ParseHeader()
        {
            _dmVersion = (int)ReadBE32();
            if (_dmVersion != 3 && _dmVersion != 4)
                throw new NotSupportedException(
                    string.Format("DM version {0} is not supported (only 3 and 4).", _dmVersion));

            if (_dmVersion == 3) ReadBE32();   // 4-byte file size
            else                 ReadBE64();   // 8-byte file size

            uint leFlag = ReadBE32();
            _littleEndian = (leFlag != 0);
        }

        // =====================================================================
        // Tag group / tag parsing
        // =====================================================================

        /// <summary>
        /// Reads the 2-byte sorted/open flags and the 4 or 8 byte child count.
        /// </summary>
        private ulong ReadGroupPreamble()
        {
            _br.ReadByte(); // isSorted
            _br.ReadByte(); // isOpen
            return (_dmVersion == 3) ? (ulong)ReadBE32() : ReadBE64();
        }

        private void ParseGroup(ulong nTags, TagNode parent, string parentName)
        {
            for (ulong i = 0; i < nTags; i++)
            {
                byte tagId = _br.ReadByte(); // 20 = group, 21 = data, 0 = end (shouldn't appear)
                ushort nameLen = ReadBE16();
                string name = (nameLen > 0)
                    ? Encoding.ASCII.GetString(_br.ReadBytes(nameLen))
                    : string.Empty;
                name = name.Replace(".", ""); // dots break lookup conventions

                if (tagId == 20) // sub-group (directory)
                {
                    TagNode child = new TagNode { IsGroup = true, Name = name };
                    parent.Children.Add(child);
                    ulong childCount = ReadGroupPreamble();
                    ParseGroup(childCount, child, name);
                }
                else if (tagId == 21) // data tag
                {
                    TagNode child = new TagNode { IsGroup = false, Name = name };
                    parent.Children.Add(child);
                    ReadDataTag(child, parentName);
                }
                // else: unknown, can't safely continue; break
            }
        }

        private void ReadDataTag(TagNode node, string parentGroupName)
        {
            // Verify "%%%%" delimiter
            byte[] delim = _br.ReadBytes(4);
            if (delim[0] != '%' || delim[1] != '%' || delim[2] != '%' || delim[3] != '%')
                throw new InvalidDataException("Missing '%%%%' data-tag delimiter.");

            // Info array: first the count, then the type codes
            ulong infoCount = ReadBESize();
            ulong[] info = new ulong[infoCount];
            for (ulong k = 0; k < infoCount; k++)
                info[k] = ReadBESize();

            // Large raw image pixel arrays: skip on first pass, record offset after
            bool skipPayload = (node.Name == "Data" && parentGroupName == "ImageData");

            if (skipPayload)
            {
                long beforePayload = _br.BaseStream.Position;
                SkipTagPayload(info);
                long afterPayload = _br.BaseStream.Position;
                node.Value = afterPayload; // sentinel: stream position AFTER payload
                node.IsImageDataPayload = true;
            }
            else
            {
                node.Value = ReadTagPayload(info);
            }
        }

        // =====================================================================
        // Tag payload reading
        // =====================================================================

        private object ReadTagPayload(ulong[] info)
        {
            if (info.Length == 0) return null;
            ulong encType = info[0];

            if (info.Length == 1)
            {
                return ReadScalar((int)encType);
            }

            if (encType == ET_STRING && info.Length == 2)
            {
                int len = (int)info[1];
                // DM strings are UTF-16LE
                return Encoding.Unicode.GetString(_br.ReadBytes(len * 2));
            }

            if (encType == ET_ARRAY && info.Length == 3)
            {
                // Simple array: [20, elType, count]
                int elType = (int)info[1];
                int count  = (int)info[2];
                return ReadSimpleArray(elType, count);
            }

            if (encType == ET_STRUCT)
            {
                // Struct: [15, nNameChars, nFields, nameLen0, type0, nameLen1, type1, ...]
                // (nameLen is always 0 in practice, types alternate with 0-length names)
                return ReadStructPayload(info);
            }

            if (encType == ET_ARRAY)
            {
                // Complex array: [20, elEncType, ...]
                return ReadComplexArray(info);
            }

            // Fallback: skip unknown by treating as raw data
            return null;
        }

        private void SkipTagPayload(ulong[] info)
        {
            if (info.Length == 0) return;
            ulong encType = info[0];

            if (info.Length == 1)
            {
                _br.BaseStream.Seek((long)ScalarByteSize((int)encType), SeekOrigin.Current);
                return;
            }
            if (encType == ET_STRING && info.Length == 2)
            {
                _br.BaseStream.Seek((long)info[1] * 2, SeekOrigin.Current);
                return;
            }
            if (encType == ET_ARRAY && info.Length == 3)
            {
                ulong elSize = ScalarByteSize((int)info[1]);
                _br.BaseStream.Seek((long)(elSize * info[2]), SeekOrigin.Current);
                return;
            }
            if (encType == ET_STRUCT)
            {
                ulong total = StructByteSize(info);
                _br.BaseStream.Seek((long)total, SeekOrigin.Current);
                return;
            }
            if (encType == ET_ARRAY)
            {
                ulong total = ComplexArrayByteSize(info);
                _br.BaseStream.Seek((long)total, SeekOrigin.Current);
                return;
            }
        }

        // --- Struct ---

        private object[] ReadStructPayload(ulong[] info)
        {
            // info layout: [15, nNameCharsTotal, nFields, fieldNameLen0, fieldType0, ...]
            if (info.Length < 3) return null;
            int nFields = (int)info[2];
            object[] result = new object[nFields];
            for (int f = 0; f < nFields; f++)
            {
                // fieldNameLen is in info[3 + f*2], always 0 in DM files
                int fieldType = (int)info[3 + f * 2 + 1];
                result[f] = ReadScalar(fieldType);
            }
            return result;
        }

        private ulong StructByteSize(ulong[] info)
        {
            if (info.Length < 3) return 0;
            int nFields = (int)info[2];
            ulong total = 0;
            for (int f = 0; f < nFields; f++)
                total += ScalarByteSize((int)info[3 + f * 2 + 1]);
            return total;
        }

        // --- Complex arrays ---

        private object ReadComplexArray(ulong[] info)
        {
            // [20, elEncType, ...]
            if (info.Length < 2) return null;
            int elType = (int)info[1];

            if (elType == ET_STRUCT)
            {
                // [20, 15, nNameCharsTotal, nFields, nameLen0, type0, ..., count]
                ulong count = info[info.Length - 1];
                ulong structSize = StructByteSize(BuildStructInfo(info));
                return _br.ReadBytes((int)(structSize * count));
            }
            if (elType == ET_STRING)
            {
                // [20, 18, strCharLen, count]
                ulong strLen  = info[2];
                ulong count   = info[3];
                string[] arr = new string[count];
                for (ulong k = 0; k < count; k++)
                    arr[k] = Encoding.Unicode.GetString(_br.ReadBytes((int)strLen * 2));
                return arr;
            }
            // Fallback: scalar element array with count at end
            {
                ulong count   = info[info.Length - 1];
                return ReadSimpleArray(elType, (int)count);
            }
        }

        private ulong ComplexArrayByteSize(ulong[] info)
        {
            if (info.Length < 2) return 0;
            int elType = (int)info[1];
            if (elType == ET_STRUCT)
            {
                ulong count = info[info.Length - 1];
                return StructByteSize(BuildStructInfo(info)) * count;
            }
            if (elType == ET_STRING)
            {
                ulong strLen = info[2];
                ulong count  = info[3];
                return strLen * count * 2;
            }
            {
                ulong count = info[info.Length - 1];
                return ScalarByteSize(elType) * count;
            }
        }

        /// <summary>Extract the struct-definition sub-array from a complex-array info vector.</summary>
        private ulong[] BuildStructInfo(ulong[] info)
        {
            // info = [20, 15, nNameCharsTotal, nFields, nameLen0, type0, ..., count]
            // struct portion = [15, nNameCharsTotal, nFields, nameLen0, type0, ...]
            int len = info.Length - 2; // drop leading 20 and trailing count
            ulong[] sub = new ulong[len];
            for (int i = 0; i < len; i++)
                sub[i] = info[i + 1];
            return sub;
        }

        // --- Simple arrays ---

        private object ReadSimpleArray(int elType, int count)
        {
            switch (elType)
            {
                case ET_UINT8:
                    return _br.ReadBytes(count);
                case ET_INT8:
                {
                    sbyte[] a = new sbyte[count];
                    for (int k = 0; k < count; k++) a[k] = (sbyte)_br.ReadByte();
                    return a;
                }
                case ET_INT16:
                {
                    short[] a = new short[count];
                    for (int k = 0; k < count; k++) a[k] = ReadI16();
                    return a;
                }
                case ET_UINT16:
                {
                    ushort[] a = new ushort[count];
                    for (int k = 0; k < count; k++) a[k] = ReadU16();
                    return a;
                }
                case ET_INT32:
                {
                    int[] a = new int[count];
                    for (int k = 0; k < count; k++) a[k] = ReadI32();
                    return a;
                }
                case ET_UINT32:
                {
                    uint[] a = new uint[count];
                    for (int k = 0; k < count; k++) a[k] = ReadU32();
                    return a;
                }
                case ET_FLOAT32:
                {
                    float[] a = new float[count];
                    for (int k = 0; k < count; k++) a[k] = ReadF32();
                    return a;
                }
                case ET_FLOAT64:
                {
                    double[] a = new double[count];
                    for (int k = 0; k < count; k++) a[k] = ReadF64();
                    return a;
                }
                case ET_INT64:
                {
                    long[] a = new long[count];
                    for (int k = 0; k < count; k++) a[k] = ReadI64();
                    return a;
                }
                case ET_UINT64:
                {
                    ulong[] a = new ulong[count];
                    for (int k = 0; k < count; k++) a[k] = ReadU64();
                    return a;
                }
                default:
                {
                    // Unknown element type: skip bytes
                    ulong elSize = ScalarByteSize(elType);
                    _br.BaseStream.Seek((long)(elSize * (ulong)count), SeekOrigin.Current);
                    return null;
                }
            }
        }

        // --- Scalars ---

        private object ReadScalar(int encType)
        {
            switch (encType)
            {
                case ET_INT8:    return (sbyte)_br.ReadByte();
                case ET_UINT8:   return _br.ReadByte();
                case ET_BOOL:    return ReadU32() != 0;
                case ET_INT16:   return ReadI16();
                case ET_UINT16:  return ReadU16();
                case ET_INT32:   return ReadI32();
                case ET_UINT32:  return ReadU32();
                case ET_INT64:   return ReadI64();
                case ET_UINT64:  return ReadU64();
                case ET_FLOAT32: return ReadF32();
                case ET_FLOAT64: return ReadF64();
                default:
                    // Skip unknown (rare)
                    ulong sz = ScalarByteSize(encType);
                    if (sz > 0) _br.BaseStream.Seek((long)sz, SeekOrigin.Current);
                    return null;
            }
        }

        private ulong ScalarByteSize(int encType)
        {
            switch (encType)
            {
                case ET_INT8:
                case ET_UINT8:   return 1;
                case ET_INT16:
                case ET_UINT16:  return 2;
                case ET_INT32:
                case ET_UINT32:
                case ET_FLOAT32:
                case ET_BOOL:    return 4;
                case ET_INT64:
                case ET_UINT64:
                case ET_FLOAT64: return 8;
                case ET_COMPLEX8: return 8;
                default:          return 0;
            }
        }

        // =====================================================================
        // Image extraction from the tag tree
        // =====================================================================

        private void ExtractImages(TagNode root)
        {
            TagNode imageList = FindChild(root, "ImageList");
            if (imageList == null) return;

            foreach (TagNode entry in imageList.Children)
            {
                TagNode imageData = FindChild(entry, "ImageData");
                if (imageData == null) continue;

                // --- Dimensions ---
                TagNode dims = FindChild(imageData, "Dimensions");
                if (dims == null) continue;
                int width  = GetDimAt(dims, 0);
                int height = GetDimAt(dims, 1);
                if (width <= 0 || height <= 0) continue;

                // --- Data type ---
                int dataType = ToInt(GetLeafValue(imageData, "DataType"));

                // --- Calibrations ---
                double pxX = 1.0, pxY = 1.0;
                string unitX = "", unitY = "";
                TagNode calibrations = FindChild(imageData, "Calibrations");
                if (calibrations != null)
                {
                    TagNode dimCal = FindChild(calibrations, "Dimension");
                    if (dimCal != null && dimCal.Children.Count >= 2)
                    {
                        ParseCalibration(dimCal.Children[0], out pxX, out unitX);
                        ParseCalibration(dimCal.Children[1], out pxY, out unitY);
                    }
                }

                // --- Raw pixel data (second-pass read using stored stream offset) ---
                TagNode dataNode = FindChild(imageData, "Data");
                if (dataNode == null || !dataNode.IsImageDataPayload) continue;

                long afterOffset = (long)dataNode.Value;
                int  bytesPerPixel = DmDataTypeBytes(dataType);
                long totalBytes = (long)width * height * bytesPerPixel;
                long startOffset = afterOffset - totalBytes;
                if (startOffset < 0 || bytesPerPixel == 0) continue;

                byte[] raw = ReadBytesAt(startOffset, (int)totalBytes);
                if (raw == null) continue;

                Bitmap bmp = BuildBitmap(raw, width, height, dataType);
                if (bmp == null) continue;

                Images.Add(new DmImage
                {
                    Bitmap         = bmp,
                    Width          = width,
                    Height         = height,
                    DataType       = dataType,
                    PixelSizeX     = pxX,
                    PixelSizeY     = pxY,
                    PixelSizeUnitX = unitX,
                    PixelSizeUnitY = unitY,
                });
            }
        }

        // =====================================================================
        // Bitmap construction (normalise to 8-bit greyscale in 32-bpp ARGB)
        // =====================================================================

        private Bitmap BuildBitmap(byte[] raw, int width, int height, int dataType)
        {
            // Build a float32 intensity array normalised to [0,255]
            float[] intensity = new float[width * height];
            float dataMin = float.MaxValue, dataMax = float.MinValue;

            int n = width * height;

            switch (dataType)
            {
                case 8: // int8
                    for (int i = 0; i < n; i++) intensity[i] = (sbyte)raw[i];
                    break;
                case 9: // uint8
                    for (int i = 0; i < n; i++) intensity[i] = raw[i];
                    break;
                case 1: // int16
                    for (int i = 0; i < n; i++) intensity[i] = BufI16(raw, i * 2);
                    break;
                case 3: // uint16
                    for (int i = 0; i < n; i++) intensity[i] = BufU16(raw, i * 2);
                    break;
                case 2: // int32
                    for (int i = 0; i < n; i++) intensity[i] = BufI32(raw, i * 4);
                    break;
                case 4: // uint32
                    for (int i = 0; i < n; i++) intensity[i] = BufU32(raw, i * 4);
                    break;
                case 5: // float32
                    for (int i = 0; i < n; i++) intensity[i] = BufF32(raw, i * 4);
                    break;
                case 6: // float64
                    for (int i = 0; i < n; i++) intensity[i] = (float)BufF64(raw, i * 8);
                    break;
                case 10: // int64
                    for (int i = 0; i < n; i++) intensity[i] = BufI64(raw, i * 8);
                    break;
                case 11: // uint64
                    for (int i = 0; i < n; i++) intensity[i] = BufU64(raw, i * 8);
                    break;
                default:
                    return null;
            }

            // Find range (ignoring NaN/Inf for float types)
            for (int i = 0; i < n; i++)
            {
                float v = intensity[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                if (v < dataMin) dataMin = v;
                if (v > dataMax) dataMax = v;
            }
            float range = dataMax - dataMin;

            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            byte[] pixels = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                float fv = intensity[i];
                byte grey;
                if (float.IsNaN(fv) || float.IsInfinity(fv))
                    grey = 0;
                else
                    grey = (range > 0f) ? (byte)Math.Round((fv - dataMin) / range * 255f) : (byte)0;

                int p = i * 4;
                pixels[p]     = grey;   // B
                pixels[p + 1] = grey;   // G
                pixels[p + 2] = grey;   // R
                pixels[p + 3] = 255;    // A
            }
            Marshal.Copy(pixels, 0, bd.Scan0, pixels.Length);
            bmp.UnlockBits(bd);
            return bmp;
        }

        // =====================================================================
        // Second-pass file read (for image payload)
        // =====================================================================

        private byte[] ReadBytesAt(long offset, int length)
        {
            using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                byte[] buf = new byte[length];
                int done = 0;
                while (done < length)
                {
                    int n = fs.Read(buf, done, length - done);
                    if (n == 0) return null;
                    done += n;
                }
                return buf;
            }
        }

        // =====================================================================
        // Tag-tree helper methods
        // =====================================================================

        private TagNode FindChild(TagNode parent, string name)
        {
            foreach (TagNode c in parent.Children)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        private object GetLeafValue(TagNode parent, string name)
        {
            TagNode c = FindChild(parent, name);
            return (c != null && !c.IsGroup) ? c.Value : null;
        }

        private int GetDimAt(TagNode dims, int index)
        {
            int idx = 0;
            foreach (TagNode c in dims.Children)
            {
                if (!c.IsGroup && idx == index) return ToInt(c.Value);
                idx++;
            }
            return 0;
        }

        private void ParseCalibration(TagNode calNode, out double scale, out string unit)
        {
            scale = 1.0; unit = "";
            if (calNode == null) return;
            object sv = GetLeafValue(calNode, "Scale");
            object uv = GetLeafValue(calNode, "Units");
            if (sv != null) scale = ToDouble(sv);
            if (uv != null) unit  = (uv as string) ?? "";
        }

        private int ToInt(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }
        private double ToDouble(object v)
        {
            if (v == null) return 1.0;
            try { return Convert.ToDouble(v); } catch { return 1.0; }
        }

        private int DmDataTypeBytes(int dt)
        {
            switch (dt)
            {
                case  8: return 1;  // int8
                case  9: return 1;  // uint8
                case  1: return 2;  // int16
                case  3: return 2;  // uint16
                case  2: return 4;  // int32
                case  4: return 4;  // uint32
                case  5: return 4;  // float32
                case  7: return 4;  // bool
                case  6: return 8;  // float64
                case 10: return 8;  // int64
                case 11: return 8;  // uint64
                default: return 0;
            }
        }

        // =====================================================================
        // Buffer-level endian-aware readers (for extracted pixel byte arrays)
        // =====================================================================

        private short  BufI16(byte[] b, int o) { byte[] t={b[o],b[o+1]};      if(!_littleEndian)Array.Reverse(t); return BitConverter.ToInt16(t,0); }
        private ushort BufU16(byte[] b, int o) { byte[] t={b[o],b[o+1]};      if(!_littleEndian)Array.Reverse(t); return BitConverter.ToUInt16(t,0); }
        private int    BufI32(byte[] b, int o) { byte[] t={b[o],b[o+1],b[o+2],b[o+3]}; if(!_littleEndian)Array.Reverse(t); return BitConverter.ToInt32(t,0); }
        private uint   BufU32(byte[] b, int o) { byte[] t={b[o],b[o+1],b[o+2],b[o+3]}; if(!_littleEndian)Array.Reverse(t); return BitConverter.ToUInt32(t,0); }
        private long   BufI64(byte[] b, int o) { byte[] t=new byte[8]; Array.Copy(b,o,t,0,8); if(!_littleEndian)Array.Reverse(t); return BitConverter.ToInt64(t,0); }
        private ulong  BufU64(byte[] b, int o) { byte[] t=new byte[8]; Array.Copy(b,o,t,0,8); if(!_littleEndian)Array.Reverse(t); return BitConverter.ToUInt64(t,0); }
        private float  BufF32(byte[] b, int o) { byte[] t={b[o],b[o+1],b[o+2],b[o+3]}; if(!_littleEndian)Array.Reverse(t); return BitConverter.ToSingle(t,0); }
        private double BufF64(byte[] b, int o) { byte[] t=new byte[8]; Array.Copy(b,o,t,0,8); if(!_littleEndian)Array.Reverse(t); return BitConverter.ToDouble(t,0); }

        // =====================================================================
        // Stream-level endian-aware readers
        // =====================================================================

        // Body data: uses file endianness
        private short  ReadI16() { byte[] b=_br.ReadBytes(2); if(!_littleEndian)Array.Reverse(b); return BitConverter.ToInt16(b,0); }
        private ushort ReadU16() { byte[] b=_br.ReadBytes(2); if(!_littleEndian)Array.Reverse(b); return BitConverter.ToUInt16(b,0); }
        private int    ReadI32() { byte[] b=_br.ReadBytes(4); if(!_littleEndian)Array.Reverse(b); return BitConverter.ToInt32(b,0); }
        private uint   ReadU32() { byte[] b=_br.ReadBytes(4); if(!_littleEndian)Array.Reverse(b); return BitConverter.ToUInt32(b,0); }
        private long   ReadI64() { byte[] b=_br.ReadBytes(8); if(!_littleEndian)Array.Reverse(b); return BitConverter.ToInt64(b,0); }
        private ulong  ReadU64() { byte[] b=_br.ReadBytes(8); if(!_littleEndian)Array.Reverse(b); return BitConverter.ToUInt64(b,0); }
        private float  ReadF32() { byte[] b=_br.ReadBytes(4); if(!_littleEndian)Array.Reverse(b); return BitConverter.ToSingle(b,0); }
        private double ReadF64() { byte[] b=_br.ReadBytes(8); if(!_littleEndian)Array.Reverse(b); return BitConverter.ToDouble(b,0); }

        // Structural fields are always big-endian regardless of body endian
        private uint   ReadBE32() { byte[] b=_br.ReadBytes(4); if(BitConverter.IsLittleEndian)Array.Reverse(b); return BitConverter.ToUInt32(b,0); }
        private ulong  ReadBE64() { byte[] b=_br.ReadBytes(8); if(BitConverter.IsLittleEndian)Array.Reverse(b); return BitConverter.ToUInt64(b,0); }
        private ushort ReadBE16() { byte[] b=_br.ReadBytes(2); if(BitConverter.IsLittleEndian)Array.Reverse(b); return BitConverter.ToUInt16(b,0); }
        /// <summary>4-byte (DM3) or 8-byte (DM4) big-endian size field.</summary>
        private ulong  ReadBESize() { return (_dmVersion==3) ? (ulong)ReadBE32() : ReadBE64(); }
    }
}
