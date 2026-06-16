// DigitalMicrographLib.cs
// C# 4.0 library for reading Gatan Digital Micrograph DM3/DM4 files.
// Extracts images and pixel-size calibration; images are normalised to
// 8-bit greyscale and stored in RAM as System.Drawing.Bitmap.
//
// Format reference: http://www.er-c.org/cbb/info/dmformat/
// Ported from RosettaSciIO / HyperSpy (rsciio/digitalmicrograph/_api.py, GPL-3.0).
//
// Compile:  csc /target:library /out:DigitalMicrographLib.dll DigitalMicrographLib.cs

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DigitalMicrograph
{
    /// <summary>One image extracted from a DM3/DM4 file.</summary>
    public class DmImage
    {
        /// <summary>8-bit greyscale bitmap (Format8bppIndexed) held in RAM.</summary>
        public Bitmap Bitmap { get; internal set; }
        public double PixelSizeX { get; internal set; }
        public double PixelSizeY { get; internal set; }
        public string PixelSizeUnitX { get; internal set; }
        public string PixelSizeUnitY { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Raw DM image data-type code (1=int16, 2=float32, 6=uint8, 7=int32, 10=uint16, ...).</summary>
        public int DataType { get; internal set; }
    }

    internal class TagNode
    {
        public bool IsGroup;
        public string Name;
        public List<TagNode> Children = new List<TagNode>();
        public object Value;          // scalar / string / array for data leaves
        public bool IsSkipped;        // image pixel payload was skipped
        public long DataOffset;       // stream offset where pixel data starts
        public long DataElementCount; // element count of the skipped payload
    }

    /// <summary>Reads DM3/DM4 files. Call <see cref="Read"/>, then use <see cref="Images"/>.</summary>
    public class DmReader
    {
        // Tag-data encoding type codes (infoarray element types).
        private const int ENC_SHORT=2, ENC_LONG=3, ENC_USHORT=4, ENC_ULONG=5,
                          ENC_FLOAT=6, ENC_DOUBLE=7, ENC_BOOL=8, ENC_CHAR=9,
                          ENC_BYTE=10, ENC_LONGLONG=11, ENC_ULONGLONG=12,
                          ENC_STRUCT=15, ENC_STRING=18, ENC_ARRAY=20;

        private readonly string _path;
        private BinaryReader _br;
        private int  _dmVersion;
        private bool _le;   // file body little-endian?

        public List<DmImage> Images { get; private set; }

        public DmReader(string filePath)
        {
            if (filePath == null) throw new ArgumentNullException("filePath");
            _path = filePath;
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
                ParseTags(GroupHeader(false), root, "root"); // root group has no size field
                ExtractImages(root);
            }
        }

        // ---------- header ----------

        private void ParseHeader()
        {
            _dmVersion = (int)BE32();
            if (_dmVersion != 3 && _dmVersion != 4)
                throw new NotSupportedException("DM version " + _dmVersion + " not supported (only 3 and 4).");
            LorQ();           // file size
            _le = BE32() != 0;
        }

        // ---------- tag tree ----------

        /// <summary>is_sorted, is_open, [size on DM4 sub-groups], n_tags → returns n_tags.</summary>
        private long GroupHeader(bool hasSize)
        {
            _br.ReadByte(); _br.ReadByte();
            if (_dmVersion == 4 && hasSize) LorQ();
            return (long)LorQ();
        }

        private void ParseTags(long nTags, TagNode parent, string groupName)
        {
            for (long t = 0; t < nTags; t++)
            {
                byte tagId = _br.ReadByte();
                ushort nameLen = BE16();
                string name = nameLen > 0 ? Encoding.ASCII.GetString(_br.ReadBytes(nameLen)) : "";
                if (name.IndexOf('.') >= 0) name = name.Replace(".", "");

                if (tagId == 21) // data
                {
                    TagNode node = new TagNode { IsGroup = false, Name = name };
                    parent.Children.Add(node);
                    ReadDataTag(node, groupName == "ImageData" && name == "Data");
                }
                else if (tagId == 20) // group
                {
                    TagNode node = new TagNode { IsGroup = true, Name = name };
                    parent.Children.Add(node);
                    ParseTags(GroupHeader(true), node, name);
                }
                else
                {
                    throw new InvalidDataException("Unexpected tag id " + tagId + " at " + _br.BaseStream.Position);
                }
            }
        }

        private void ReadDataTag(TagNode node, bool skip)
        {
            if (_dmVersion == 4) _br.BaseStream.Seek(8, SeekOrigin.Current); // skipif4(2)
            byte[] d = _br.ReadBytes(4);
            if (d.Length != 4 || d[0] != '%' || d[1] != '%' || d[2] != '%' || d[3] != '%')
                throw new InvalidDataException("Missing '%%%%' delimiter.");

            long infoSize = (long)LorQ();
            int enc = (int)LorQ();

            if (infoSize == 1)                       // scalar
            {
                node.Value = ReadScalar(enc);
            }
            else if (infoSize == 2 && enc == ENC_STRING)  // string
            {
                long len = (long)LorQ();
                if (skip) MarkSkipped(node, len, 1);
                else      node.Value = ReadBytesAsString(_br.ReadBytes((int)len));
            }
            else if (infoSize == 3 && enc == ENC_ARRAY)   // simple array
            {
                int el = (int)LorQ();
                long size = (long)LorQ();
                node.Value = ReadArray(size, el, skip, node);
            }
            else if (enc == ENC_STRUCT)              // struct
            {
                int[] def = StructDef();
                node.Value = ReadStruct(def, skip);
            }
            else if (enc == ENC_ARRAY)               // array of complex type
            {
                int el = (int)LorQ();
                if (el == ENC_STRUCT)            // array of structs
                {
                    int[] def = StructDef();
                    long count = (long)LorQ();
                    long structBytes = 0; foreach (int d in def) structBytes += Sz(d);
                    SkipPayload(structBytes, count);
                }
                else if (el == ENC_STRING)       // array of strings
                {
                    long strLen = (long)LorQ();
                    SkipPayload(strLen, (long)LorQ());
                }
                else if (el == ENC_ARRAY)        // array of arrays
                {
                    int inner = (int)LorQ();
                    long elLen = (long)LorQ();
                    SkipPayload(Sz(inner) * elLen, (long)LorQ());
                }
                node.Value = null; // complex arrays aren't needed for image extraction
            }
            else
            {
                throw new InvalidDataException("Unsupported info layout (size=" + infoSize + ", enc=" + enc + ").");
            }
        }

        // ---------- definitions ----------

        private int[] StructDef()
        {
            LorQ();                       // total length (ignored)
            int n = (int)LorQ();
            int[] def = new int[n];
            for (int i = 0; i < n; i++) { LorQ(); def[i] = (int)LorQ(); } // name-len, type
            return def;
        }

        // ---------- value readers ----------

        private object ReadScalar(int et)
        {
            switch (et)
            {
                case ENC_BOOL: return _br.ReadByte() != 0;
                case ENC_CHAR:
                case ENC_BYTE: return (sbyte)_br.ReadByte();
                case ENC_SHORT: case ENC_USHORT: case ENC_LONG: case ENC_ULONG:
                case ENC_FLOAT: case ENC_DOUBLE: case ENC_LONGLONG: case ENC_ULONGLONG:
                    return BoxNumber(et);
                default: throw new InvalidDataException("Unknown scalar type " + et);
            }
        }

        private object ReadArray(long size, int el, bool skip, TagNode node)
        {
            if (skip) { MarkSkipped(node, size, Sz(el)); return null; }
            if (el == ENC_USHORT) // DM stores unicode strings as ushort arrays
            {
                char[] c = new char[size];
                for (long i = 0; i < size; i++) c[i] = (char)Convert.ToUInt16(BoxNumber(ENC_USHORT));
                return new string(c).TrimEnd('\0');
            }
            object[] a = new object[size];
            for (long i = 0; i < size; i++) a[i] = ReadScalar(el);
            return a;
        }

        private object ReadStruct(int[] def, bool skip)
        {
            if (skip) { long b = 0; foreach (int d in def) b += Sz(d); SkipPayload(0, b); return null; }
            object[] v = new object[def.Length];
            for (int i = 0; i < def.Length; i++) v[i] = ReadScalar(def[i]);
            return v;
        }

        // ---------- image extraction ----------

        private void ExtractImages(TagNode root)
        {
            TagNode imageList = Find(root, "ImageList");
            if (imageList == null) return;

            HashSet<int> thumbs = new HashSet<int>();
            TagNode t = Find(root, "Thumbnails");
            if (t != null)
                foreach (TagNode th in t.Children)
                {
                    object iv = Leaf(th, "ImageIndex");
                    if (iv != null) thumbs.Add(ToInt(iv));
                }

            int idx = 0;
            foreach (TagNode entry in imageList.Children)
            {
                int here = idx++;
                if (thumbs.Contains(here)) continue;

                TagNode imData = Find(entry, "ImageData");
                TagNode dims   = imData != null ? Find(imData, "Dimensions") : null;
                TagNode data   = imData != null ? Find(imData, "Data") : null;
                if (imData == null || dims == null || data == null || !data.IsSkipped) continue;

                List<int> dv = new List<int>();
                foreach (TagNode dn in dims.Children) if (!dn.IsGroup) dv.Add(ToInt(dn.Value));
                if (dv.Count < 2 || dv[0] <= 0 || dv[1] <= 0) continue;
                int w = dv[0], h = dv[1];

                int dataType = ToInt(Leaf(imData, "DataType"));
                if ((long)w * h > data.DataElementCount) continue;

                double pxX = 1, pxY = 1; string uX = "", uY = "";
                TagNode dim = null, cal = Find(imData, "Calibrations");
                if (cal != null) dim = Find(cal, "Dimension");
                if (dim != null && dim.Children.Count >= 2)
                {
                    ReadCal(dim.Children[0], out pxX, out uX);
                    ReadCal(dim.Children[1], out pxY, out uY);
                }

                Bitmap bmp = BuildBitmap(data.DataOffset, w, h, dataType);
                if (bmp == null) continue;

                Images.Add(new DmImage {
                    Bitmap = bmp, Width = w, Height = h, DataType = dataType,
                    PixelSizeX = pxX, PixelSizeY = pxY,
                    PixelSizeUnitX = uX, PixelSizeUnitY = uY });
            }
        }

        // ---------- bitmap (normalised 8-bit greyscale) ----------

        private Bitmap BuildBitmap(long offset, int w, int h, int dataType)
        {
            int bpp = ImageBytes(dataType);
            if (bpp == 0) return null;

            int n = w * h;
            byte[] raw = ReadBytesAt(offset, (long)n * bpp);
            if (raw == null) return null;

            // Decode every supported pixel type into doubles via one dispatch.
            double[] v = new double[n];
            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double x = DecodePixel(raw, i * bpp, dataType);
                v[i] = x;
                if (double.IsNaN(x) || double.IsInfinity(x)) continue;
                if (x < min) min = x;
                if (x > max) max = x;
            }
            double range = max - min;

            // 8-bit indexed greyscale bitmap with an identity palette.
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed);
            ColorPalette pal = bmp.Palette;
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                                         ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try
            {
                int stride = bd.Stride;            // may exceed w (row padding)
                byte[] rowbuf = new byte[stride];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        double d = v[y * w + x];
                        byte g = (double.IsNaN(d) || double.IsInfinity(d))
                            ? (byte)0
                            : (range > 0 ? (byte)Math.Round((d - min) / range * 255.0) : (byte)0);
                        rowbuf[x] = g;
                    }
                    Marshal.Copy(rowbuf, 0, (IntPtr)(bd.Scan0.ToInt64() + y * stride), stride);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        /// <summary>Decode one pixel of any supported DM image type to a double.</summary>
        private double DecodePixel(byte[] b, int o, int dataType)
        {
            switch (dataType)
            {
                case 1:  return Num(b, o, ENC_SHORT);   // int16
                case 2:  return Num(b, o, ENC_FLOAT);   // float32
                case 6:  return b[o];                   // uint8
                case 7:  return Num(b, o, ENC_LONG);    // int32
                case 9:  return (sbyte)b[o];            // int8
                case 10: return Num(b, o, ENC_USHORT);  // uint16
                case 11: return Num(b, o, ENC_ULONG);   // uint32
                case 12: return Num(b, o, ENC_DOUBLE);  // float64
                case 14: return b[o] != 0 ? 1 : 0;      // bool
                // RGBA (8/23): collapse B,G,R to luminance.
                case 8:
                case 23: return 0.299 * b[o + 2] + 0.587 * b[o + 1] + 0.114 * b[o];
                default: return 0;
            }
        }

        // ---------- helpers ----------

        private void MarkSkipped(TagNode node, long count, int elSize)
        {
            node.IsSkipped = true;
            node.DataOffset = _br.BaseStream.Position;
            node.DataElementCount = count;
            _br.BaseStream.Seek((long)elSize * count, SeekOrigin.Current);
        }
        private void SkipPayload(long elSize, long count) { _br.BaseStream.Seek(elSize * count, SeekOrigin.Current); }

        private TagNode Find(TagNode p, string name)
        {
            foreach (TagNode c in p.Children)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
        private object Leaf(TagNode p, string name) { TagNode c = Find(p, name); return (c != null && !c.IsGroup) ? c.Value : null; }
        private void ReadCal(TagNode c, out double s, out string u)
        {
            s = 1; u = "";
            object sv = Leaf(c, "Scale"), uv = Leaf(c, "Units");
            if (sv != null) s = ToDouble(sv);
            if (uv != null) u = (uv as string) ?? "";
        }
        private int ToInt(object v) { try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; } }
        private double ToDouble(object v) { try { return v == null ? 1 : Convert.ToDouble(v); } catch { return 1; } }

        /// <summary>Bytes per pixel per DM image DataType code (0 = unsupported).</summary>
        private int ImageBytes(int dt)
        {
            switch (dt)
            {
                case 6: case 9: case 14:       return 1; // uint8, int8, bool
                case 1: case 10:               return 2; // int16, uint16
                case 2: case 7: case 11:       return 4; // float32, int32, uint32
                case 8: case 23:               return 4; // RGBA
                case 12:                       return 8; // float64
                default:                       return 0;
            }
        }

        /// <summary>Byte size of a tag-data encoding type.</summary>
        private int Sz(int et)
        {
            switch (et)
            {
                case ENC_BOOL: case ENC_CHAR: case ENC_BYTE:     return 1;
                case ENC_SHORT: case ENC_USHORT:                 return 2;
                case ENC_LONG: case ENC_ULONG: case ENC_FLOAT:   return 4;
                case ENC_DOUBLE: case ENC_LONGLONG: case ENC_ULONGLONG: return 8;
                default: throw new InvalidDataException("Unknown type size " + et);
            }
        }

        private string ReadBytesAsString(byte[] raw)
        {
            try { return Encoding.UTF8.GetString(raw).TrimEnd('\0'); }
            catch { return Encoding.GetEncoding("ISO-8859-1").GetString(raw).TrimEnd('\0'); }
        }

        private byte[] ReadBytesAt(long offset, long length)
        {
            if (length <= 0 || length > int.MaxValue) return null;
            using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                byte[] buf = new byte[length];
                int done = 0, len = (int)length;
                while (done < len) { int r = fs.Read(buf, done, len - done); if (r == 0) return null; done += r; }
                return buf;
            }
        }

        // ---------- endian-aware primitives ----------

        // Body values use the file's declared endianness.
        private double Num(byte[] b, int o, int et)
        {
            byte[] s = Slice(b, o, Sz(et));
            if (!_le) Array.Reverse(s);
            switch (et)
            {
                case ENC_SHORT:     return BitConverter.ToInt16(s, 0);
                case ENC_USHORT:    return BitConverter.ToUInt16(s, 0);
                case ENC_LONG:      return BitConverter.ToInt32(s, 0);
                case ENC_ULONG:     return BitConverter.ToUInt32(s, 0);
                case ENC_FLOAT:     return BitConverter.ToSingle(s, 0);
                case ENC_DOUBLE:    return BitConverter.ToDouble(s, 0);
                case ENC_LONGLONG:  return BitConverter.ToInt64(s, 0);
                case ENC_ULONGLONG: return BitConverter.ToUInt64(s, 0);
                default: return 0;
            }
        }
        private object BoxNumber(int et)
        {
            byte[] s = _br.ReadBytes(Sz(et));
            if (!_le) Array.Reverse(s);
            switch (et)
            {
                case ENC_SHORT:     return BitConverter.ToInt16(s, 0);
                case ENC_USHORT:    return BitConverter.ToUInt16(s, 0);
                case ENC_LONG:      return BitConverter.ToInt32(s, 0);
                case ENC_ULONG:     return BitConverter.ToUInt32(s, 0);
                case ENC_FLOAT:     return BitConverter.ToSingle(s, 0);
                case ENC_DOUBLE:    return BitConverter.ToDouble(s, 0);
                case ENC_LONGLONG:  return BitConverter.ToInt64(s, 0);
                case ENC_ULONGLONG: return BitConverter.ToUInt64(s, 0);
                default: return 0;
            }
        }
        private static byte[] Slice(byte[] b, int o, int n) { byte[] r = new byte[n]; Array.Copy(b, o, r, 0, n); return r; }

        // Structural fields are ALWAYS big-endian.
        private uint   BE32() { byte[] b = _br.ReadBytes(4); if (BitConverter.IsLittleEndian) Array.Reverse(b); return BitConverter.ToUInt32(b, 0); }
        private ushort BE16() { byte[] b = _br.ReadBytes(2); if (BitConverter.IsLittleEndian) Array.Reverse(b); return BitConverter.ToUInt16(b, 0); }
        private ulong  BE64() { byte[] b = _br.ReadBytes(8); if (BitConverter.IsLittleEndian) Array.Reverse(b); return BitConverter.ToUInt64(b, 0); }
        /// <summary>read_l_or_q (big-endian): 4 bytes on DM3, 8 on DM4.</summary>
        private ulong  LorQ() { return _dmVersion == 4 ? BE64() : (ulong)BE32(); }
    }
}
