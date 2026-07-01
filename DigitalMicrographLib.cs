// DigitalMicrographLib.cs — reads Gatan DM3/DM4 files (C# 4.0, no System.Drawing).
// Extracts images and pixel-size calibration; pixels are normalised (using the
// file's display contrast limits when present) to a 1-D 8-bit greyscale array.
// Format: http://www.er-c.org/cbb/info/dmformat/  (ported from RosettaSciIO, GPL-3.0)
//
//   var reader = new DigitalMicrograph.DmReader("image.dm3");
//   reader.Read();
//   foreach (var img in reader.Images) { byte[] gray = img.Pixels; /* 0-255, row-major */ }

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DigitalMicrograph
{
    /// <summary>One image extracted from a DM3/DM4 file.</summary>
    public class DmImage
    {
        /// <summary>Normalised 8-bit greyscale pixels, row-major, length = Width*Height.</summary>
        public byte[] Pixels { get; internal set; }
        public double PixelSizeX { get; internal set; }
        public double PixelSizeY { get; internal set; }
        public string PixelSizeUnitX { get; internal set; }
        public string PixelSizeUnitY { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Raw DM data-type code (1=int16, 2=float32, 6=uint8, 7=int32, 10=uint16, ...).</summary>
        public int DataType { get; internal set; }
        /// <summary>Contrast low limit (raw data units); NaN if absent.</summary>
        public double ContrastLow { get; internal set; }
        /// <summary>Contrast high limit (raw data units); NaN if absent.</summary>
        public double ContrastHigh { get; internal set; }
        /// <summary>True when limits were found and used for normalisation.</summary>
        public bool HasContrastLimits { get; internal set; }
    }

    internal class TagNode
    {
        public bool IsGroup;
        public string Name;
        public List<TagNode> Children = new List<TagNode>();
        public object Value;              // scalar / string / array for data leaves
        public bool IsSkipped;            // pixel payload skipped during parse
        public long DataOffset;           // stream offset of pixel data
        public long DataElementCount;     // element count of skipped payload
    }

    /// <summary>Reads DM3/DM4 files; images available via <see cref="Images"/> after <see cref="Read"/>.</summary>
    public class DmReader
    {
        // Tag-data encoding type codes (infoarray element types).
        private const int ENC_SHORT=2, ENC_LONG=3, ENC_USHORT=4, ENC_ULONG=5,
                          ENC_FLOAT=6, ENC_DOUBLE=7, ENC_BOOLEAN=8, ENC_CHAR=9,
                          ENC_BYTE=10, ENC_LONGLONG=11, ENC_ULONGLONG=12,
                          ENC_STRUCT=15, ENC_STRING=18, ENC_ARRAY=20;

        private readonly string _path;
        private BinaryReader _br;
        private int  _dmVersion;
        private bool _littleEndian;

        public List<DmImage> Images { get; private set; }

        public DmReader(string filePath)
        {
            if (filePath == null) throw new ArgumentNullException("filePath");
            _path  = filePath;
            Images = new List<DmImage>();
        }

        public void Read()
        {
            Images.Clear();
            using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (_br = new BinaryReader(fs, Encoding.ASCII))
            {
                ParseHeader();
                TagNode root = new TagNode { IsGroup = true, Name = "root" };
                ParseTags(ParseTagGroupHeader(false), root, "root"); // root group has no size field
                ExtractImages(root);
            }
        }

        // ---- header ----

        private void ParseHeader()
        {
            _dmVersion = (int)ReadBE32();
            if (_dmVersion != 3 && _dmVersion != 4)
                throw new NotSupportedException(string.Format("DM version {0} not supported (only 3 and 4).", _dmVersion));
            ReadLorQ_BE();                 // file size
            _littleEndian = ReadBE32() != 0;
        }

        // ---- tag tree ----

        // is_sorted, is_open, [size on DM4 sub-groups], n_tags.
        private long ParseTagGroupHeader(bool hasSize)
        {
            _br.ReadByte(); _br.ReadByte();
            if (_dmVersion == 4 && hasSize) ReadLorQ_BE();
            return (long)ReadLorQ_BE();
        }

        private void ParseTags(long nTags, TagNode parent, string groupName)
        {
            for (long t = 0; t < nTags; t++)
            {
                byte tagId = _br.ReadByte();
                ushort nameLen = ReadBE16();
                string name = nameLen > 0 ? ReadAsciiTagName(nameLen) : string.Empty;
                if (name.IndexOf('.') >= 0) name = name.Replace(".", "");

                bool skip = (groupName == "ImageData" && name == "Data"); // skip large pixel payload

                if (tagId == 21)            // data tag
                {
                    TagNode node = new TagNode { IsGroup = false, Name = name };
                    parent.Children.Add(node);
                    ReadDataTag(node, skip);
                }
                else if (tagId == 20)       // sub-group
                {
                    TagNode node = new TagNode { IsGroup = true, Name = name };
                    parent.Children.Add(node);
                    ParseTags(ParseTagGroupHeader(true), node, name);
                }
                else
                    throw new InvalidDataException(string.Format("Unexpected tag id {0} at {1}.", tagId, _br.BaseStream.Position));
            }
        }

        private void ReadDataTag(TagNode node, bool skip)
        {
            CheckDataTagDelimiter();
            long infoSize = (long)ReadLorQ_BE();

            if (infoSize == 1)              // scalar
            {
                node.Value = ReadSimpleData((int)ReadLorQ_BE());
            }
            else if (infoSize == 2)         // string
            {
                int enc = (int)ReadLorQ_BE();
                if (enc != ENC_STRING) throw new InvalidDataException("Expected string (18), got " + enc);
                node.Value = ReadString((long)ReadLorQ_BE(), skip, node);
            }
            else if (infoSize == 3)         // array of simple type
            {
                int enc = (int)ReadLorQ_BE();
                if (enc != ENC_ARRAY) throw new InvalidDataException("Expected array (20), got " + enc);
                int el; long size = ParseArrayDefinition(out el);
                node.Value = ReadArray(size, el, skip, node);
            }
            else if (infoSize > 3)
            {
                int enc = (int)ReadLorQ_BE();
                if (enc == ENC_STRUCT)      // struct
                {
                    node.Value = ReadStruct(ParseStructDefinition(), skip);
                }
                else if (enc == ENC_ARRAY)  // array of complex type
                {
                    int el = (int)ReadLorQ_BE();
                    if (el == ENC_STRUCT)
                    {
                        int[] def = ParseStructDefinition();
                        node.Value = ReadArrayOfStructs((long)ReadLorQ_BE(), def, skip);
                    }
                    else if (el == ENC_STRING)
                    {
                        long strLen = (long)ReadLorQ_BE();
                        node.Value = ReadArrayOfStrings((long)ReadLorQ_BE(), strLen, skip);
                    }
                    else if (el == ENC_ARRAY) // array of arrays (skip; never image pixels)
                    {
                        int inner; long elLen = ParseArrayDefinition(out inner);
                        long size = (long)ReadLorQ_BE();
                        _br.BaseStream.Seek(SimpleTypeSize(inner) * elLen * size, SeekOrigin.Current);
                        node.Value = null;
                    }
                    else throw new InvalidDataException("Unknown complex array element type " + el);
                }
                else throw new InvalidDataException("Unknown enctype " + enc);
            }
            else throw new InvalidDataException("Invalid infoarray size " + infoSize);
        }

        private long ParseArrayDefinition(out int encEltype)
        {
            encEltype = (int)ReadLorQ_BE();
            return (long)ReadLorQ_BE();
        }

        private int[] ParseStructDefinition()
        {
            ReadLorQ_BE();                  // total length (ignored)
            int nfields = (int)ReadLorQ_BE();
            int[] def = new int[nfields];
            for (int i = 0; i < nfields; i++) { ReadLorQ_BE(); def[i] = (int)ReadLorQ_BE(); } // name-len, type
            return def;
        }

        // ---- value readers ----

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
                case ENC_CHAR:
                case ENC_BYTE:      return (sbyte)_br.ReadByte();
                case ENC_LONGLONG:  return ReadI64();
                case ENC_ULONGLONG: return ReadU64();
                default: throw new InvalidDataException("Unknown simple data type " + etype);
            }
        }

        private int SimpleTypeSize(int etype)
        {
            switch (etype)
            {
                case ENC_SHORT: case ENC_USHORT:                 return 2;
                case ENC_LONG: case ENC_ULONG: case ENC_FLOAT:   return 4;
                case ENC_DOUBLE: case ENC_LONGLONG: case ENC_ULONGLONG: return 8;
                case ENC_BOOLEAN: case ENC_CHAR: case ENC_BYTE:  return 1;
                default: throw new InvalidDataException("Unknown simple data type size " + etype);
            }
        }

        // DM strings: `length` bytes, decoded UTF-8 with latin-1 fallback.
        private object ReadString(long length, bool skip, TagNode node)
        {
            if (skip) { MarkSkipped(node, length, 1); return null; }
            byte[] raw = _br.ReadBytes((int)length);
            try { return Encoding.UTF8.GetString(raw).TrimEnd('\0'); }
            catch { return Encoding.GetEncoding("ISO-8859-1").GetString(raw).TrimEnd('\0'); }
        }

        private object ReadArray(long size, int encEltype, bool skip, TagNode node)
        {
            if (skip) { MarkSkipped(node, size, SimpleTypeSize(encEltype)); return null; }

            switch (encEltype)
            {
                case ENC_USHORT: // DM stores unicode strings as ushort arrays
                {
                    char[] c = new char[size];
                    for (long i = 0; i < size; i++) c[i] = (char)ReadU16();
                    return new string(c).TrimEnd('\0');
                }
                case ENC_SHORT:  { short[] a = new short[size];  for (long i=0;i<size;i++) a[i]=ReadI16(); return a; }
                case ENC_LONG:   { int[] a = new int[size];      for (long i=0;i<size;i++) a[i]=ReadI32(); return a; }
                case ENC_ULONG:  { uint[] a = new uint[size];    for (long i=0;i<size;i++) a[i]=ReadU32(); return a; }
                case ENC_FLOAT:  { float[] a = new float[size];  for (long i=0;i<size;i++) a[i]=ReadF32(); return a; }
                case ENC_DOUBLE: { double[] a = new double[size];for (long i=0;i<size;i++) a[i]=ReadF64(); return a; }
                case ENC_BOOLEAN:
                case ENC_CHAR:
                case ENC_BYTE:   { sbyte[] a = new sbyte[size];  for (long i=0;i<size;i++) a[i]=(sbyte)_br.ReadByte(); return a; }
                case ENC_LONGLONG:  { long[] a = new long[size];   for (long i=0;i<size;i++) a[i]=ReadI64(); return a; }
                case ENC_ULONGLONG: { ulong[] a = new ulong[size]; for (long i=0;i<size;i++) a[i]=ReadU64(); return a; }
                default:
                    _br.BaseStream.Seek(SimpleTypeSize(encEltype) * size, SeekOrigin.Current);
                    return null;
            }
        }

        private object ReadStruct(int[] def, bool skip)
        {
            if (skip) { long b=0; foreach (int d in def) b += SimpleTypeSize(d); _br.BaseStream.Seek(b, SeekOrigin.Current); return null; }
            object[] vals = new object[def.Length];
            for (int i = 0; i < def.Length; i++) vals[i] = ReadSimpleData(def[i]);
            return vals;
        }

        private object ReadArrayOfStructs(long size, int[] def, bool skip)
        {
            long structBytes = 0; foreach (int d in def) structBytes += SimpleTypeSize(d);
            if (skip) { _br.BaseStream.Seek(structBytes * size, SeekOrigin.Current); return null; }
            return _br.ReadBytes((int)(structBytes * size)); // kept as raw bytes; unused for images
        }

        private object ReadArrayOfStrings(long size, long strLen, bool skip)
        {
            if (skip) { _br.BaseStream.Seek(strLen * size, SeekOrigin.Current); return null; }
            string[] arr = new string[size];
            for (long i = 0; i < size; i++) arr[i] = Encoding.UTF8.GetString(_br.ReadBytes((int)strLen)).TrimEnd('\0');
            return arr;
        }

        // "%%%%" data-tag delimiter, preceded by 8 skipped bytes on DM4.
        private void CheckDataTagDelimiter()
        {
            if (_dmVersion == 4) _br.BaseStream.Seek(8, SeekOrigin.Current);
            byte[] d = _br.ReadBytes(4);
            if (d.Length != 4 || d[0] != '%' || d[1] != '%' || d[2] != '%' || d[3] != '%')
                throw new InvalidDataException("Missing '%%%%' data-tag delimiter.");
        }

        // ---- image extraction ----

        private void ExtractImages(TagNode root)
        {
            TagNode imageList = FindChild(root, "ImageList");
            if (imageList == null) return;

            // Thumbnail image indices to skip.
            HashSet<int> thumbs = new HashSet<int>();
            TagNode t = FindChild(root, "Thumbnails");
            if (t != null)
                foreach (TagNode th in t.Children)
                {
                    object iv = GetLeafValue(th, "ImageIndex");
                    if (iv != null) thumbs.Add(ToInt(iv));
                }

            int idx = 0;
            foreach (TagNode entry in imageList.Children)
            {
                int here = idx++;
                if (thumbs.Contains(here)) continue;

                TagNode imageData = FindChild(entry, "ImageData");
                if (imageData == null) continue;
                TagNode dims = FindChild(imageData, "Dimensions");
                if (dims == null) continue;

                // Dimensions: unnamed leaves, [0]=width, [1]=height.
                List<int> dv = new List<int>();
                foreach (TagNode dn in dims.Children) if (!dn.IsGroup) dv.Add(ToInt(dn.Value));
                if (dv.Count < 2) continue;
                int width = dv[0], height = dv[1];
                if (width <= 0 || height <= 0) continue;

                int dataType = ToInt(GetLeafValue(imageData, "DataType"));

                double pxX = 1.0, pxY = 1.0; string unitX = "", unitY = "";
                TagNode cal = FindChild(imageData, "Calibrations");
                TagNode dimCal = cal != null ? FindChild(cal, "Dimension") : null;
                if (dimCal != null && dimCal.Children.Count >= 2)
                {
                    ParseCalibration(dimCal.Children[0], out pxX, out unitX);
                    ParseCalibration(dimCal.Children[1], out pxY, out unitY);
                }

                TagNode dataNode = FindChild(imageData, "Data");
                if (dataNode == null || !dataNode.IsSkipped) continue;
                if (dataNode.DataElementCount < (long)width * height) continue; // stacks: need >= one slice

                // Contrast limits live under root.DocumentObjectList (a separate branch),
                // so search the whole tree; fall back to the image entry.
                double cLow = double.NaN, cHigh = double.NaN;
                bool hasLimits = false;
                TagNode displayInfo = FindDescendant(root, "ImageDisplayInfo") ?? FindDescendant(entry, "ImageDisplayInfo");
                if (displayInfo != null)
                {
                    object lo = GetLeafValue(displayInfo, "LowLimit");
                    object hi = GetLeafValue(displayInfo, "HighLimit");
                    if (lo != null && hi != null)
                    {
                        cLow = ToDouble(lo); cHigh = ToDouble(hi);
                        if (!double.IsNaN(cLow) && !double.IsNaN(cHigh) && cHigh > cLow) hasLimits = true;
                    }
                }

                byte[] pixels = BuildPixels(dataNode.DataOffset, width, height, dataType, hasLimits, cLow, cHigh);
                if (pixels == null) continue;

                Images.Add(new DmImage {
                    Pixels = pixels, Width = width, Height = height, DataType = dataType,
                    PixelSizeX = pxX, PixelSizeY = pxY, PixelSizeUnitX = unitX, PixelSizeUnitY = unitY,
                    ContrastLow = cLow, ContrastHigh = cHigh, HasContrastLimits = hasLimits });
            }
        }

        // ---- pixels: decode + normalise to 8-bit greyscale ----

        private byte[] BuildPixels(long dataOffset, int width, int height, int dataType,
                                   bool hasLimits, double contrastLow, double contrastHigh)
        {
            if ((long)width * height > int.MaxValue) return null; // too large for a single array
            int n = width * height;
            int bpp = ImageDataTypeBytes(dataType);
            if (bpp == 0) return null;

            byte[] raw = ReadBytesAt(dataOffset, (long)n * bpp);
            if (raw == null) return null;

            // Decode to float intensity (RGBA collapsed to luminance).
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
            raw = null; // decoded; free the raw buffer before mapping

            // Range: display contrast limits if present, else data min/max.
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

            // Map to 0..255: clip then scale. Math.Round keeps output byte-identical.
            byte[] pixels = new byte[n];
            float range = max - min;
            if (range > 0f)
            {
                for (int i = 0; i < n; i++)
                {
                    float x = v[i];
                    if (float.IsNaN(x) || float.IsInfinity(x)) { pixels[i] = 0; continue; }
                    float u = (x - min) / range;
                    if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
                    pixels[i] = (byte)Math.Round(u * 255f);
                }
            }
            return pixels;
        }

        // Reads from the already-open stream (parsing is complete, so seeking is safe).
        private byte[] ReadBytesAt(long offset, long length)
        {
            if (length <= 0 || length > int.MaxValue) return null;
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

        // ---- tag-tree helpers ----

        private TagNode FindChild(TagNode parent, string name)
        {
            foreach (TagNode c in parent.Children)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        // Depth-first search for a group anywhere under parent.
        private TagNode FindDescendant(TagNode parent, string name)
        {
            foreach (TagNode c in parent.Children)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
                if (c.IsGroup) { TagNode hit = FindDescendant(c, name); if (hit != null) return hit; }
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

        private void MarkSkipped(TagNode node, long count, int elSize)
        {
            node.IsSkipped = true;
            node.DataOffset = _br.BaseStream.Position;
            node.DataElementCount = count;
            _br.BaseStream.Seek((long)elSize * count, SeekOrigin.Current);
        }

        private int ToInt(object v)    { if (v == null) return 0;   try { return Convert.ToInt32(v); }  catch { return 0; } }
        private double ToDouble(object v){ if (v == null) return 1.0; try { return Convert.ToDouble(v); } catch { return 1.0; } }

        // Bytes per pixel per DM image DataType code (0 = unsupported).
        private int ImageDataTypeBytes(int dt)
        {
            switch (dt)
            {
                case 6: case 9: case 14:   return 1; // uint8, int8, bool
                case 1: case 10:           return 2; // int16, uint16
                case 2: case 7: case 11:   return 4; // float32, int32, uint32
                case 8: case 23:           return 4; // RGBA
                case 12:                   return 8; // float64
                default:                   return 0; // 0/3/4/5/13/27/28 not supported
            }
        }

        private string ReadAsciiTagName(int len) { return Encoding.ASCII.GetString(_br.ReadBytes(len)); }

        // ---- buffer-level readers (allocation-free), file-endian ----

        private short  BufI16(byte[] b, int o){ return (short)BufU16(b,o); }
        private ushort BufU16(byte[] b, int o){ return _littleEndian ? (ushort)(b[o] | (b[o+1]<<8)) : (ushort)(b[o+1] | (b[o]<<8)); }
        private int    BufI32(byte[] b, int o){ return (int)BufU32(b,o); }
        private uint   BufU32(byte[] b, int o){ return _littleEndian ? (uint)(b[o] | (b[o+1]<<8) | (b[o+2]<<16) | (b[o+3]<<24)) : (uint)(b[o+3] | (b[o+2]<<8) | (b[o+1]<<16) | (b[o]<<24)); }
        private long   BufI64(byte[] b, int o){ return _littleEndian ? ((long)BufU32(b,o) | ((long)BufU32(b,o+4)<<32)) : ((long)BufU32(b,o+4) | ((long)BufU32(b,o)<<32)); }
        // Floats: no allocation when file endianness matches the CPU (usual case).
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

        // ---- stream-level readers (file-endian body; used for metadata) ----

        private short  ReadI16(){byte[] b=_br.ReadBytes(2);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToInt16(b,0);}
        private ushort ReadU16(){byte[] b=_br.ReadBytes(2);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToUInt16(b,0);}
        private int    ReadI32(){byte[] b=_br.ReadBytes(4);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToInt32(b,0);}
        private uint   ReadU32(){byte[] b=_br.ReadBytes(4);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToUInt32(b,0);}
        private long   ReadI64(){byte[] b=_br.ReadBytes(8);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToInt64(b,0);}
        private ulong  ReadU64(){byte[] b=_br.ReadBytes(8);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToUInt64(b,0);}
        private float  ReadF32(){byte[] b=_br.ReadBytes(4);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToSingle(b,0);}
        private double ReadF64(){byte[] b=_br.ReadBytes(8);if(!_littleEndian)Array.Reverse(b);return BitConverter.ToDouble(b,0);}

        // Structural fields are always big-endian.
        private uint   ReadBE32(){byte[] b=_br.ReadBytes(4);if(BitConverter.IsLittleEndian)Array.Reverse(b);return BitConverter.ToUInt32(b,0);}
        private ulong  ReadBE64(){byte[] b=_br.ReadBytes(8);if(BitConverter.IsLittleEndian)Array.Reverse(b);return BitConverter.ToUInt64(b,0);}
        private ushort ReadBE16(){byte[] b=_br.ReadBytes(2);if(BitConverter.IsLittleEndian)Array.Reverse(b);return BitConverter.ToUInt16(b,0);}

        // read_l_or_q: 4-byte long on DM3, 8-byte long-long on DM4.
        private ulong ReadLorQ_BE(){return (_dmVersion==4) ? ReadBE64() : (ulong)ReadBE32();}
    }
}
