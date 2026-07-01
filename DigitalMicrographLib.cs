// DigitalMicrographLib.cs
// C# 4.0 library for reading Gatan Digital Micrograph DM3/DM4 files.
// Focuses on image extraction and pixel-size calibration.
// Pixel data is normalised (using the file's display contrast limits when
// present) and stored in RAM as a 1-D 8-bit greyscale byte array.
// No System.Drawing dependency.
//
// DM format reference: http://www.er-c.org/cbb/info/dmformat/
// Faithfully ported from the open-source RosettaSciIO / HyperSpy DM reader
// (rsciio/digitalmicrograph/_api.py, GPL-3.0).
//
// Compile example (C# 4.0 / .NET 4):
//   csc /target:library /out:DigitalMicrographLib.dll DigitalMicrographLib.cs
//
// Usage:
//   var reader = new DigitalMicrograph.DmReader("image.dm3");
//   reader.Read();
//   foreach (var img in reader.Images)
//   {
//       Console.WriteLine("{0}x{1}", img.Width, img.Height);
//       Console.WriteLine("pixel size X: {0} {1}", img.PixelSizeX, img.PixelSizeUnitX);
//       byte[] gray = img.Pixels; // row-major, 0-255, length Width*Height
//   }

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DigitalMicrograph
{
    // =========================================================================
    // Public result type
    // =========================================================================

    /// <summary>One image extracted from a DM3/DM4 file.</summary>
    public class DmImage
    {
        /// <summary>Normalised 8-bit greyscale pixels, row-major (length = Width*Height), values 0-255.</summary>
        public byte[] Pixels { get; internal set; }
        /// <summary>Pixel size of the physical calibration in X (columns).</summary>
        public double PixelSizeX { get; internal set; }
        /// <summary>Pixel size of the physical calibration in Y (rows).</summary>
        public double PixelSizeY { get; internal set; }
        /// <summary>Unit string for X calibration (e.g. "nm", "Å", "1/nm").</summary>
        public string PixelSizeUnitX { get; internal set; }
        /// <summary>Unit string for Y calibration.</summary>
        public string PixelSizeUnitY { get; internal set; }
        /// <summary>Image width in pixels.</summary>
        public int Width { get; internal set; }
        /// <summary>Image height in pixels.</summary>
        public int Height { get; internal set; }
        /// <summary>Raw DM image data-type code (1=int16, 2=float32, 6=uint8, 7=int32, 10=uint16, ...).</summary>
        public int DataType { get; internal set; }
        /// <summary>Display contrast low limit from ImageDisplayInfo.LowLimit, in raw data units. NaN if absent.</summary>
        public double ContrastLow { get; internal set; }
        /// <summary>Display contrast high limit from ImageDisplayInfo.HighLimit, in raw data units. NaN if absent.</summary>
        public double ContrastHigh { get; internal set; }
        /// <summary>True when both contrast limits were found in the file and used for normalisation.</summary>
        public bool HasContrastLimits { get; internal set; }
    }

    // =========================================================================
    // Internal tag-tree
    // =========================================================================

    internal class TagNode
    {
        public bool IsGroup;
        public string Name;
        public List<TagNode> Children = new List<TagNode>();
        public object Value;             // data leaves: scalar / string / array
        // For skipped image-data payloads:
        public bool IsSkipped;
        public long DataOffset;          // stream offset where pixel data starts
        public long DataElementCount;    // number of elements
    }

    // =========================================================================
    // Reader
    // =========================================================================

    /// <summary>
    /// Reads DM3/DM4 files and exposes contained images via <see cref="Images"/>.
    /// Call <see cref="Read"/> first.
    /// </summary>
    public class DmReader
    {
        // ----- Tag-data encoding type codes (the infoarray element types) -----
        // These come from get_data_reader.dtype_dict in the Python source.
        private const int ENC_SHORT     =  2; // int16
        private const int ENC_LONG      =  3; // int32
        private const int ENC_USHORT    =  4; // uint16 (also used for unicode chars)
        private const int ENC_ULONG     =  5; // uint32
        private const int ENC_FLOAT     =  6; // float32
        private const int ENC_DOUBLE    =  7; // float64
        private const int ENC_BOOLEAN   =  8; // 1-byte bool
        private const int ENC_CHAR      =  9; // int8
        private const int ENC_BYTE      = 10; // int8 (0x0a)
        private const int ENC_LONGLONG  = 11; // int64  (DM4)
        private const int ENC_ULONGLONG = 12; // uint64 (DM4)
        private const int ENC_STRUCT    = 15; // 0x0f
        private const int ENC_STRING    = 18; // 0x12
        private const int ENC_ARRAY     = 20; // 0x14

        private readonly string _path;
        private BinaryReader _br;
        private int  _dmVersion;
        private bool _littleEndian;

        /// <summary>Images found in the file (thumbnails excluded). Populated by <see cref="Read"/>.</summary>
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

        public void Read()
        {
            Images.Clear();
            using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (_br = new BinaryReader(fs, Encoding.ASCII))
            {
                ParseHeader();
                TagNode root = new TagNode { IsGroup = true, Name = "root" };
                // Root tag group: NO size field even on DM4.
                long nRoot = ParseTagGroupHeader(false);
                ParseTags(nRoot, root, "root");
                ExtractImages(root);
            }
        }

        // =====================================================================
        // Header
        // =====================================================================

        private void ParseHeader()
        {
            _dmVersion = (int)ReadBE32();                       // version (big-endian long)
            if (_dmVersion != 3 && _dmVersion != 4)
                throw new NotSupportedException(
                    string.Format("DM version {0} is not supported (only 3 and 4).", _dmVersion));

            ReadLorQ_BE();                                      // file size (long or longlong)
            uint le = ReadBE32();                               // is-little-endian flag
            _littleEndian = (le != 0);
        }

        // =====================================================================
        // Tag group / tag parsing  (mirrors parse_tag_group + parse_tags)
        // =====================================================================

        /// <summary>
        /// Reads is_sorted, is_open and the tag count. On DM4, when
        /// <paramref name="hasSize"/> is true an extra 8-byte size precedes the count.
        /// Returns the number of tags.
        /// </summary>
        private long ParseTagGroupHeader(bool hasSize)
        {
            _br.ReadByte(); // is_sorted
            _br.ReadByte(); // is_open
            if (_dmVersion == 4 && hasSize)
                ReadLorQ_BE(); // size (guessed field, DM4 only)
            return (long)ReadLorQ_BE(); // n_tags
        }

        private void ParseTags(long nTags, TagNode parent, string groupName)
        {
            for (long t = 0; t < nTags; t++)
            {
                byte tagId = _br.ReadByte();
                ushort nameLen = ReadBE16();
                string name = (nameLen > 0)
                    ? ReadAsciiTagName(nameLen)
                    : string.Empty;
                if (name.IndexOf('.') >= 0) name = name.Replace(".", "");

                bool skip = (groupName == "ImageData" && name == "Data");

                if (tagId == 21)        // DATA tag
                {
                    TagNode node = new TagNode { IsGroup = false, Name = name };
                    parent.Children.Add(node);
                    ReadDataTag(node, skip);
                }
                else if (tagId == 20)   // GROUP tag (sub-group → has size on DM4)
                {
                    TagNode node = new TagNode { IsGroup = true, Name = name };
                    parent.Children.Add(node);
                    long childCount = ParseTagGroupHeader(true);
                    ParseTags(childCount, node, name);
                }
                else
                {
                    throw new InvalidDataException(
                        string.Format("Unexpected tag id {0} at offset {1}.",
                                      tagId, _br.BaseStream.Position));
                }
            }
        }

        // =====================================================================
        // Data tag reading  (mirrors the big if/elif on infoarray_size)
        // =====================================================================

        private void ReadDataTag(TagNode node, bool skip)
        {
            CheckDataTagDelimiter();                 // skipif4(2) + "%%%%"
            long infoSize = (long)ReadLorQ_BE();     // infoarray_size

            if (infoSize == 1)                       // simple scalar
            {
                int etype = (int)ReadLorQ_BE();
                node.Value = ReadSimpleData(etype);
            }
            else if (infoSize == 2)                  // string
            {
                int enctype = (int)ReadLorQ_BE();
                if (enctype != ENC_STRING)
                    throw new InvalidDataException("Expected 18 (string), got " + enctype);
                long strLen = (long)ReadLorQ_BE();   // parse_string_definition
                node.Value = ReadString(strLen, skip, node);
            }
            else if (infoSize == 3)                  // array of simple type
            {
                int enctype = (int)ReadLorQ_BE();
                if (enctype != ENC_ARRAY)
                    throw new InvalidDataException("Expected 20 (array), got " + enctype);
                int encEltype;
                long size = ParseArrayDefinition(out encEltype);
                node.Value = ReadArray(size, encEltype, skip, node);
            }
            else if (infoSize > 3)
            {
                int enctype = (int)ReadLorQ_BE();
                if (enctype == ENC_STRUCT)           // struct
                {
                    int[] def = ParseStructDefinition();
                    node.Value = ReadStruct(def, skip);
                }
                else if (enctype == ENC_ARRAY)       // array of complex type
                {
                    int encEltype = (int)ReadLorQ_BE();
                    if (encEltype == ENC_STRUCT)     // array of structs
                    {
                        int[] def = ParseStructDefinition();
                        long size = (long)ReadLorQ_BE();
                        node.Value = ReadArrayOfStructs(size, def, skip);
                    }
                    else if (encEltype == ENC_STRING) // array of strings
                    {
                        long strLen = (long)ReadLorQ_BE();
                        long size   = (long)ReadLorQ_BE();
                        node.Value = ReadArrayOfStrings(size, strLen, skip);
                    }
                    else if (encEltype == ENC_ARRAY) // array of arrays
                    {
                        int innerEltype;
                        long elLen = ParseArrayDefinition(out innerEltype);
                        long size  = (long)ReadLorQ_BE();
                        // Skip; this case does not occur for image pixels.
                        long elBytes = SimpleTypeSize(innerEltype) * elLen;
                        _br.BaseStream.Seek(elBytes * size, SeekOrigin.Current);
                        node.Value = null;
                    }
                    else
                    {
                        throw new InvalidDataException("Unknown complex array element type " + encEltype);
                    }
                }
                else
                {
                    throw new InvalidDataException("Unknown enctype " + enctype + " for infoSize " + infoSize);
                }
            }
            else
            {
                throw new InvalidDataException("Invalid infoarray size " + infoSize);
            }
        }

        // =====================================================================
        // Definition parsers
        // =====================================================================

        private long ParseArrayDefinition(out int encEltype)
        {
            encEltype = (int)ReadLorQ_BE();
            long length = (long)ReadLorQ_BE();
            return length;
        }

        private int[] ParseStructDefinition()
        {
            ReadLorQ_BE();                       // length (ignored)
            int nfields = (int)ReadLorQ_BE();
            int[] def = new int[nfields];
            for (int i = 0; i < nfields; i++)
            {
                ReadLorQ_BE();                   // field name length (ignored)
                def[i] = (int)ReadLorQ_BE();     // field type
            }
            return def;
        }

        // =====================================================================
        // Value readers
        // =====================================================================

        private object ReadSimpleData(int etype)
        {
            switch (etype)
            {
                case ENC_SHORT:     return ReadI16();
                case ENC_LONG:      return ReadI32();
                case ENC_USHORT:    return ReadU16();
                case ENC_ULONG:     return ReadU32();
                case ENC_FLOAT:     return ReadF32();
                case ENC_DOUBLE:    return ReadF64();
                case ENC_BOOLEAN:   return _br.ReadByte() != 0;
                case ENC_CHAR:      return (sbyte)_br.ReadByte();
                case ENC_BYTE:      return (sbyte)_br.ReadByte();
                case ENC_LONGLONG:  return ReadI64();
                case ENC_ULONGLONG: return ReadU64();
                default:
                    throw new InvalidDataException("Unknown simple data type " + etype);
            }
        }

        private int SimpleTypeSize(int etype)
        {
            switch (etype)
            {
                case ENC_SHORT:     return 2;
                case ENC_LONG:      return 4;
                case ENC_USHORT:    return 2;
                case ENC_ULONG:     return 4;
                case ENC_FLOAT:     return 4;
                case ENC_DOUBLE:    return 8;
                case ENC_BOOLEAN:   return 1;
                case ENC_CHAR:      return 1;
                case ENC_BYTE:      return 1;
                case ENC_LONGLONG:  return 8;
                case ENC_ULONGLONG: return 8;
                default:
                    throw new InvalidDataException("Unknown simple data type size " + etype);
            }
        }

        private object ReadString(long length, bool skip, TagNode node)
        {
            // Tag-data strings are stored as `length` bytes (each char 1 byte here,
            // decoded as UTF-8/latin-1 in the Python source).
            if (skip)
            {
                node.IsSkipped = true;
                node.DataOffset = _br.BaseStream.Position;
                node.DataElementCount = length;
                _br.BaseStream.Seek(length, SeekOrigin.Current);
                return null;
            }
            byte[] raw = _br.ReadBytes((int)length);
            try { return Encoding.UTF8.GetString(raw).TrimEnd('\0'); }
            catch { return Encoding.GetEncoding("ISO-8859-1").GetString(raw).TrimEnd('\0'); }
        }

        private object ReadArray(long size, int encEltype, bool skip, TagNode node)
        {
            if (skip)
            {
                node.IsSkipped = true;
                node.DataOffset = _br.BaseStream.Position;
                node.DataElementCount = size;
                long bytes = SimpleTypeSize(encEltype) * size;
                _br.BaseStream.Seek(bytes, SeekOrigin.Current);
                return null;
            }

            // enc_eltype == 4 (ushort) used for unicode strings in DM
            switch (encEltype)
            {
                case ENC_USHORT:
                {
                    // Could be a unicode string; return as string for metadata friendliness
                    char[] chars = new char[size];
                    for (long i = 0; i < size; i++) chars[i] = (char)ReadU16();
                    return new string(chars).TrimEnd('\0');
                }
                case ENC_SHORT:
                {
                    short[] a = new short[size];
                    for (long i = 0; i < size; i++) a[i] = ReadI16();
                    return a;
                }
                case ENC_LONG:
                {
                    int[] a = new int[size];
                    for (long i = 0; i < size; i++) a[i] = ReadI32();
                    return a;
                }
                case ENC_ULONG:
                {
                    uint[] a = new uint[size];
                    for (long i = 0; i < size; i++) a[i] = ReadU32();
                    return a;
                }
                case ENC_FLOAT:
                {
                    float[] a = new float[size];
                    for (long i = 0; i < size; i++) a[i] = ReadF32();
                    return a;
                }
                case ENC_DOUBLE:
                {
                    double[] a = new double[size];
                    for (long i = 0; i < size; i++) a[i] = ReadF64();
                    return a;
                }
                case ENC_BOOLEAN:
                case ENC_CHAR:
                case ENC_BYTE:
                {
                    sbyte[] a = new sbyte[size];
                    for (long i = 0; i < size; i++) a[i] = (sbyte)_br.ReadByte();
                    return a;
                }
                case ENC_LONGLONG:
                {
                    long[] a = new long[size];
                    for (long i = 0; i < size; i++) a[i] = ReadI64();
                    return a;
                }
                case ENC_ULONGLONG:
                {
                    ulong[] a = new ulong[size];
                    for (long i = 0; i < size; i++) a[i] = ReadU64();
                    return a;
                }
                default:
                    // Unknown element: skip
                    _br.BaseStream.Seek(SimpleTypeSize(encEltype) * size, SeekOrigin.Current);
                    return null;
            }
        }

        private object ReadStruct(int[] def, bool skip)
        {
            if (skip)
            {
                long bytes = 0;
                foreach (int d in def) bytes += SimpleTypeSize(d);
                _br.BaseStream.Seek(bytes, SeekOrigin.Current);
                return null;
            }
            object[] vals = new object[def.Length];
            for (int i = 0; i < def.Length; i++) vals[i] = ReadSimpleData(def[i]);
            return vals;
        }

        private object ReadArrayOfStructs(long size, int[] def, bool skip)
        {
            long structBytes = 0;
            foreach (int d in def) structBytes += SimpleTypeSize(d);
            if (skip)
            {
                _br.BaseStream.Seek(structBytes * size, SeekOrigin.Current);
                return null;
            }
            // Read but keep as raw bytes (not needed for image extraction)
            return _br.ReadBytes((int)(structBytes * size));
        }

        private object ReadArrayOfStrings(long size, long strLen, bool skip)
        {
            if (skip)
            {
                _br.BaseStream.Seek(strLen * size, SeekOrigin.Current);
                return null;
            }
            string[] arr = new string[size];
            for (long i = 0; i < size; i++)
            {
                byte[] raw = _br.ReadBytes((int)strLen);
                arr[i] = Encoding.UTF8.GetString(raw).TrimEnd('\0');
            }
            return arr;
        }

        // =====================================================================
        // %%%% delimiter  (mirrors check_data_tag_delimiter)
        // =====================================================================

        private void CheckDataTagDelimiter()
        {
            SkipIf4(2); // DM4: skip 8 bytes before the delimiter
            byte[] d = _br.ReadBytes(4);
            if (d.Length != 4 || d[0] != '%' || d[1] != '%' || d[2] != '%' || d[3] != '%')
                throw new InvalidDataException("Missing '%%%%' data-tag delimiter.");
        }

        private void SkipIf4(int n)
        {
            if (_dmVersion == 4)
                _br.BaseStream.Seek(4L * n, SeekOrigin.Current);
        }

        // =====================================================================
        // Image extraction
        // =====================================================================

        private void ExtractImages(TagNode root)
        {
            TagNode imageList = FindChild(root, "ImageList");
            if (imageList == null) return;

            // Determine thumbnail image indices to exclude.
            HashSet<int> thumbnailIdx = new HashSet<int>();
            TagNode thumbnails = FindChild(root, "Thumbnails");
            if (thumbnails != null)
            {
                foreach (TagNode th in thumbnails.Children)
                {
                    object iv = GetLeafValue(th, "ImageIndex");
                    if (iv != null) thumbnailIdx.Add(ToInt(iv));
                }
            }

            // ImageList children are unnamed groups → named "TagGroup0", "TagGroup1"...
            // We track positional index to match the thumbnail exclusion.
            int posIndex = 0;
            foreach (TagNode entry in imageList.Children)
            {
                int thisIndex = posIndex;
                posIndex++;

                if (thumbnailIdx.Contains(thisIndex)) continue;

                TagNode imageData = FindChild(entry, "ImageData");
                if (imageData == null) continue;

                TagNode dims = FindChild(imageData, "Dimensions");
                if (dims == null) continue;

                // Dimensions: unnamed data leaves. [0]=width(X), [1]=height(Y)
                List<int> dimValues = new List<int>();
                foreach (TagNode dn in dims.Children)
                    if (!dn.IsGroup) dimValues.Add(ToInt(dn.Value));

                // Only handle 2-D images here.
                if (dimValues.Count < 2) continue;
                int width  = dimValues[0];
                int height = dimValues[1];
                if (width <= 0 || height <= 0) continue;

                int dataType = ToInt(GetLeafValue(imageData, "DataType"));

                // Calibrations
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

                // Pixel data
                TagNode dataNode = FindChild(imageData, "Data");
                if (dataNode == null || !dataNode.IsSkipped) continue;

                long elementCount = dataNode.DataElementCount;
                long expected = (long)width * height;
                // For stacks, elementCount may be width*height*depth; clamp to first slice.
                if (elementCount < expected) continue;

                // Contrast limits (ImageDisplayInfo.LowLimit / HighLimit), in raw data units.
                // ImageDisplayInfo usually lives under root.DocumentObjectList, a separate
                // branch from ImageList, so search the WHOLE tag tree (from root), not just
                // this image entry. This mirrors the Fiji DM3_Reader, which matches any key
                // ending in "ImageDisplayInfo.LowLimit" / ".HighLimit".
                double cLow = double.NaN, cHigh = double.NaN;
                bool hasLimits = false;
                TagNode displayInfo = FindDescendant(root, "ImageDisplayInfo");
                if (displayInfo == null) displayInfo = FindDescendant(entry, "ImageDisplayInfo");
                if (displayInfo != null)
                {
                    object lo = GetLeafValue(displayInfo, "LowLimit");
                    object hi = GetLeafValue(displayInfo, "HighLimit");
                    if (lo != null && hi != null)
                    {
                        cLow  = ToDouble(lo);
                        cHigh = ToDouble(hi);
                        // Only use them if they form a valid, non-degenerate range.
                        if (!double.IsNaN(cLow) && !double.IsNaN(cHigh) && cHigh > cLow)
                            hasLimits = true;
                    }
                }

                byte[] pixels = BuildPixels(dataNode.DataOffset, width, height, dataType,
                                            hasLimits, cLow, cHigh);
                if (pixels == null) continue;

                Images.Add(new DmImage
                {
                    Pixels            = pixels,
                    Width             = width,
                    Height            = height,
                    DataType          = dataType,
                    PixelSizeX        = pxX,
                    PixelSizeY        = pxY,
                    PixelSizeUnitX    = unitX,
                    PixelSizeUnitY    = unitY,
                    ContrastLow       = cLow,
                    ContrastHigh      = cHigh,
                    HasContrastLimits = hasLimits,
                });
            }
        }

        // =====================================================================
        // Pixel construction (normalised 8-bit greyscale, 1-D row-major array)
        // =====================================================================

        private byte[] BuildPixels(long dataOffset, int width, int height, int dataType,
                                   bool hasLimits, double contrastLow, double contrastHigh)
        {
            int n = width * height;
            int bpp = ImageDataTypeBytes(dataType);
            if (bpp == 0) return null; // unsupported type

            byte[] raw = ReadBytesAt(dataOffset, (long)n * bpp);
            if (raw == null) return null;

            // Decode pixels to float intensity (RGBA collapsed to luminance).
            // The Buf* helpers are allocation-free, so this is a tight loop.
            float[] v = new float[n];
            switch (dataType)
            {
                case 1:  for (int i = 0; i < n; i++) v[i] = BufI16(raw, i * 2); break;         // int16
                case 2:  for (int i = 0; i < n; i++) v[i] = BufF32(raw, i * 4); break;         // float32
                case 6:  for (int i = 0; i < n; i++) v[i] = raw[i];            break;          // uint8
                case 7:  for (int i = 0; i < n; i++) v[i] = BufI32(raw, i * 4); break;         // int32
                case 9:  for (int i = 0; i < n; i++) v[i] = (sbyte)raw[i];     break;          // int8
                case 10: for (int i = 0; i < n; i++) v[i] = BufU16(raw, i * 2); break;         // uint16
                case 11: for (int i = 0; i < n; i++) v[i] = BufU32(raw, i * 4); break;         // uint32
                case 12: for (int i = 0; i < n; i++) v[i] = (float)BufF64(raw, i * 8); break;  // float64
                case 14: for (int i = 0; i < n; i++) v[i] = raw[i] != 0 ? 1f : 0f; break;      // bool
                case 8:  case 23:                                                              // RGBA (B,G,R,A)
                    for (int i = 0; i < n; i++) { int o = i * 4; v[i] = 0.299f*raw[o+2] + 0.587f*raw[o+1] + 0.114f*raw[o]; }
                    break;
                default: return null;
            }

            // Normalisation range: display contrast limits if present (matching how
            // DigitalMicrograph renders), otherwise the actual data min/max.
            float min, max;
            if (hasLimits)
            {
                min = (float)contrastLow; max = (float)contrastHigh;
            }
            else
            {
                min = float.MaxValue; max = float.MinValue;
                for (int i = 0; i < n; i++)
                {
                    float x = v[i];
                    if (float.IsNaN(x) || float.IsInfinity(x)) continue;
                    if (x < min) min = x;
                    if (x > max) max = x;
                }
            }

            // Map to 0..255: clip to [min, max] then scale. Uses Math.Round to
            // stay byte-identical to previous output (round-half-to-even).
            byte[] pixels = new byte[n];
            float range = max - min;
            if (range > 0f)
            {
                for (int i = 0; i < n; i++)
                {
                    float x = v[i];
                    if (float.IsNaN(x) || float.IsInfinity(x)) { pixels[i] = 0; continue; }
                    float t = (x - min) / range;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    pixels[i] = (byte)Math.Round(t * 255f);
                }
            }
            // range <= 0 leaves pixels all zero (flat image)
            return pixels;
        }

        private byte[] ReadBytesAt(long offset, long length)
        {
            if (length <= 0 || length > int.MaxValue) return null;
            // Parsing is finished by the time this runs, so we can seek the
            // already-open stream rather than opening the file a second time.
            Stream s = _br.BaseStream;
            s.Seek(offset, SeekOrigin.Begin);
            byte[] buf = new byte[length];
            int done = 0, len = (int)length;
            while (done < len)
            {
                int r = s.Read(buf, done, len - done);
                if (r == 0) return null;
                done += r;
            }
            return buf;
        }

        // =====================================================================
        // Tag-tree helpers
        // =====================================================================

        private TagNode FindChild(TagNode parent, string name)
        {
            foreach (TagNode c in parent.Children)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        /// <summary>Depth-first search for a group node with the given name anywhere under <paramref name="parent"/>.</summary>
        private TagNode FindDescendant(TagNode parent, string name)
        {
            foreach (TagNode c in parent.Children)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c;
                if (c.IsGroup)
                {
                    TagNode hit = FindDescendant(c, name);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        private object GetLeafValue(TagNode parent, string name)
        {
            TagNode c = FindChild(parent, name);
            return (c != null && !c.IsGroup) ? c.Value : null;
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

        /// <summary>Bytes per pixel for each DM image DataType code.</summary>
        private int ImageDataTypeBytes(int dt)
        {
            switch (dt)
            {
                case 1:  return 2;  // int16
                case 2:  return 4;  // float32
                case 6:  return 1;  // uint8
                case 7:  return 4;  // int32
                case 8:  return 4;  // RGBA (4 x u1)
                case 9:  return 1;  // int8
                case 10: return 2;  // uint16
                case 11: return 4;  // uint32
                case 12: return 8;  // float64
                case 14: return 1;  // bool
                case 23: return 4;  // RGBA (4 x u1)
                default: return 0;  // 0/3/4/5/13/27/28 not supported here
            }
        }

        private string ReadAsciiTagName(int len)
        {
            return Encoding.ASCII.GetString(_br.ReadBytes(len));
        }

        // =====================================================================
        // Buffer-level endian-aware readers (allocation-free)
        // =====================================================================

        private short  BufI16(byte[] b, int o){ return (short)BufU16(b,o); }
        private ushort BufU16(byte[] b, int o){ return _littleEndian ? (ushort)(b[o] | (b[o+1]<<8)) : (ushort)(b[o+1] | (b[o]<<8)); }
        private int    BufI32(byte[] b, int o){ return (int)BufU32(b,o); }
        private uint   BufU32(byte[] b, int o){ return _littleEndian ? (uint)(b[o] | (b[o+1]<<8) | (b[o+2]<<16) | (b[o+3]<<24)) : (uint)(b[o+3] | (b[o+2]<<8) | (b[o+1]<<16) | (b[o]<<24)); }
        // Float readers: no allocation when file endianness matches the CPU
        // (the usual case); otherwise fall back to a small reversed copy.
        private float  BufF32(byte[] b, int o)
        {
            if (_littleEndian == BitConverter.IsLittleEndian) return BitConverter.ToSingle(b, o);
            byte[] t = { b[o+3], b[o+2], b[o+1], b[o] };
            return BitConverter.ToSingle(t, 0);
        }
        private double BufF64(byte[] b, int o)
        {
            if (_littleEndian == BitConverter.IsLittleEndian) return BitConverter.ToDouble(b, o);
            byte[] t = { b[o+7], b[o+6], b[o+5], b[o+4], b[o+3], b[o+2], b[o+1], b[o] };
            return BitConverter.ToDouble(t, 0);
        }
        private long   BufI64(byte[] b, int o){ return _littleEndian ? ((long)BufU32(b,o) | ((long)BufU32(b,o+4)<<32)) : ((long)BufU32(b,o+4) | ((long)BufU32(b,o)<<32)); }

        // =====================================================================
        // Stream-level endian-aware readers
        // =====================================================================

        // Body data: uses the file endianness from the header.
        private short  ReadI16(){byte[] b=_br.ReadBytes(2);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToInt16(b,0);}
        private ushort ReadU16(){byte[] b=_br.ReadBytes(2);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToUInt16(b,0);}
        private int    ReadI32(){byte[] b=_br.ReadBytes(4);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToInt32(b,0);}
        private uint   ReadU32(){byte[] b=_br.ReadBytes(4);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToUInt32(b,0);}
        private long   ReadI64(){byte[] b=_br.ReadBytes(8);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToInt64(b,0);}
        private ulong  ReadU64(){byte[] b=_br.ReadBytes(8);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToUInt64(b,0);}
        private float  ReadF32(){byte[] b=_br.ReadBytes(4);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToSingle(b,0);}
        private double ReadF64(){byte[] b=_br.ReadBytes(8);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToDouble(b,0);}

        // Structural fields are ALWAYS big-endian regardless of body endianness.
        private uint   ReadBE32(){byte[] b=_br.ReadBytes(4);if(BitConverter.IsLittleEndian)Array.Reverse(b);return BitConverter.ToUInt32(b,0);}
        private ulong  ReadBE64(){byte[] b=_br.ReadBytes(8);if(BitConverter.IsLittleEndian)Array.Reverse(b);return BitConverter.ToUInt64(b,0);}
        private ushort ReadBE16(){byte[] b=_br.ReadBytes(2);if(BitConverter.IsLittleEndian)Array.Reverse(b);return BitConverter.ToUInt16(b,0);}

        /// <summary>read_l_or_q (big-endian): 4-byte long on DM3, 8-byte long-long on DM4.</summary>
        private ulong ReadLorQ_BE(){return (_dmVersion==4) ? ReadBE64() : (ulong)ReadBE32();}
    }
}
