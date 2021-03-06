//! \file       ImageSeraph.cs
//! \date       Sat Jul 18 12:16:42 2015
//! \brief      Seraphim engine images.
//
// Copyright (C) 2015 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameRes.Utility;

namespace GameRes.Formats.Seraphim
{
    internal class SeraphMetaData : ImageMetaData
    {
        public int PackedSize;
        public int Colors;
    }

    [Export(typeof(ImageFormat))]
    public class SeraphCfImage : ImageFormat
    {
        public override string         Tag { get { return "CF"; } }
        public override string Description { get { return "Seraphim engine image format"; } }
        public override uint     Signature { get { return 0x4643; } }

        public override ImageMetaData ReadMetaData (Stream stream)
        {
            var header = new byte[0x10];
            if (0x10 != stream.Read (header, 0, 0x10))
                return null;
            int packed_size = LittleEndian.ToInt32 (header, 12);
            if (packed_size <= 0 || packed_size > stream.Length-0x10)
                return null;
            uint width  = LittleEndian.ToUInt16 (header, 8);
            uint height = LittleEndian.ToUInt16 (header, 10);
            if (0 == width || 0 == height)
                return null;
            return new SeraphMetaData
            {
                OffsetX = LittleEndian.ToInt16 (header, 4),
                OffsetY = LittleEndian.ToInt16 (header, 6),
                Width   = width,
                Height  = height,
                BPP     = 24,
                PackedSize = packed_size,
            };
        }

        public override ImageData Read (Stream stream, ImageMetaData info)
        {
            var meta = info as SeraphMetaData;
            if (null == meta)
                throw new ArgumentException ("SeraphCfImage.Read should be supplied with SeraphMetaData", "info");

            var reader = new SeraphReader (stream, meta);
            reader.UnpackCf();
            return ImageData.Create (info, PixelFormats.Bgr24, null, reader.Data);
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new NotImplementedException ("SeraphCfImage.Write not implemented");
        }
    }

    [Export(typeof(ImageFormat))]
    public class SeraphCtImage : SeraphCfImage
    {
        public override string         Tag { get { return "CT"; } }
        public override uint     Signature { get { return 0x5443; } }

        public override ImageMetaData ReadMetaData (Stream stream)
        {
            var info = base.ReadMetaData (stream);
            info.BPP = 32;
            return info;
        }

        public override ImageData Read (Stream stream, ImageMetaData info)
        {
            var meta = info as SeraphMetaData;
            if (null == meta)
                throw new ArgumentException ("SeraphCtImage.Read should be supplied with SeraphMetaData", "info");

            var reader = new SeraphReader (stream, meta);
            reader.UnpackCt();
            return ImageData.Create (info, reader.Format, null, reader.Data);
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new NotImplementedException ("SeraphCtImage.Write not implemented");
        }
    }

    [Export(typeof(ImageFormat))]
    public class SeraphCbImage : ImageFormat
    {
        public override string         Tag { get { return "CB"; } }
        public override string Description { get { return "Seraphim engine image format"; } }
        public override uint     Signature { get { return 0; } }

        public override ImageMetaData ReadMetaData (Stream stream)
        {
            if ('C' != stream.ReadByte() || 'B' != stream.ReadByte())
                return null;
            var header = new byte[0x10];
            if (0xE != stream.Read (header, 2, 0xE))
                return null;
            int colors = LittleEndian.ToUInt16 (header, 2);
            int packed_size = LittleEndian.ToInt32 (header, 12);
            if (packed_size <= 0 || packed_size > stream.Length-0x10)
                return null;
            uint width  = LittleEndian.ToUInt16 (header, 8);
            uint height = LittleEndian.ToUInt16 (header, 10);
            if (0 == width || 0 == height)
                return null;
            return new SeraphMetaData
            {
                OffsetX = LittleEndian.ToInt16 (header, 4),
                OffsetY = LittleEndian.ToInt16 (header, 6),
                Width   = width,
                Height  = height,
                BPP     = 8,
                PackedSize = packed_size,
                Colors  = colors,
            };
        }

        public override ImageData Read (Stream stream, ImageMetaData info)
        {
            var meta = info as SeraphMetaData;
            if (null == meta)
                throw new ArgumentException ("SeraphCbImage.Read should be supplied with SeraphMetaData", "info");

            var reader = new SeraphReader (stream, meta, 1);
            reader.UnpackCb();
            return ImageData.Create (info, reader.Format, reader.Palette, reader.Data);
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new NotImplementedException ("SeraphCbImage.Write not implemented");
        }
    }

    internal class SeraphReader
    {
        Stream      m_input;
        byte[]      m_output;
        int         m_width;
        int         m_height;
        int         m_stride;
        int         m_colors;
        int         m_packed_size;

        public byte[]           Data { get { return m_output; } }
        public PixelFormat    Format { get; private set; }
        public BitmapPalette Palette { get; private set; }

        public SeraphReader (Stream input, SeraphMetaData info, int pixel_size = 3)
        {
            m_input = input;
            m_input.Position = 0x10;
            m_width = (int)info.Width;
            m_height = (int)info.Height;
            m_stride = m_width * pixel_size;
            m_output = new byte[m_stride * m_height];
            m_packed_size = info.PackedSize;
            m_colors = info.Colors;
            if (1 == pixel_size && m_colors > 0)
                Palette = ReadPalette (m_colors);
        }

        public BitmapPalette ReadPalette (int colors)
        {
            int palette_size = colors * 3;
            var palette_data = new byte[Math.Max (palette_size, 0x300)];
            if (palette_size != m_input.Read (palette_data, 0, palette_size))
                throw new InvalidFormatException();
            var palette = new Color[0x100];
            if (colors > 0x100)
                colors = 0x100;
            int src = 0;
            for (int i = 0; i < palette.Length; ++i)
            {
                byte r = palette_data[src++];
                byte g = palette_data[src++];
                byte b = palette_data[src++];
                palette[i] = Color.FromRgb (r, g, b);
            }
            return new BitmapPalette (palette);
        }

        public void UnpackCb ()
        {
            var pixels = UnpackBytes();
            int dst = 0;
            for (int src = (m_height-1) * m_width; src >= 0; src -= m_width)
            {
                Buffer.BlockCopy (pixels, src, m_output, dst, m_width);
                dst += m_width;
            }
            Format = PixelFormats.Indexed8;
        }

        public void UnpackCt ()
        {
            UnpackRgb();
            m_input.Position = 0x10 + m_packed_size + 4;
            var alpha = UnpackBytes();
            var pixels = new byte[m_width*m_height*4];
            int dst = 0;
            for (int y = m_height-1; y >= 0; --y)
            {
                int rgb = y * m_stride;
                int a   = y * m_width;
                for (int x = 0; x < m_width; ++x)
                {
                    pixels[dst++] = m_output[rgb++];
                    pixels[dst++] = m_output[rgb++];
                    pixels[dst++] = m_output[rgb++];
                    int v = Math.Min (alpha[a++] * 0xff / 0x64, 0xff);
                    pixels[dst++] = (byte)~v;
                }
            }
            m_output = pixels;
            Format = PixelFormats.Bgra32;
        }

        public void UnpackCf ()
        {
            UnpackRgb();
            FlipPixels();
            Format = PixelFormats.Bgr24;
        }

        private void UnpackRgb () // sub_404250
        {
            int dst = 0;
            while (dst < m_output.Length)
            {
                int count;
                int v1 = m_input.ReadByte();
                if (-1 == v1)
                    break;
                if ((v1 & 0xF0) == 0xF0)
                    throw new InvalidFormatException();

                if (0 == (v1 & 0x80))
                {
                    if (0 != (v1 & 0x40))
                    {
                        count = (v1 & 0x3F) + 2;
                        int v2 = m_input.ReadByte();
                        for (int i = 0; i < count; ++i)
                            m_output[dst+i] = (byte)v2;
                    }
                    else
                    {
                        count = (v1 & 0x3F) + 1;
                        if (count != m_input.Read (m_output, dst, count))
                            break;
                    }
                }
                else if (0 == (v1 & 0x40))
                {
                    int v2 = m_input.ReadByte();
                    count = v2 | ((v1 & 0xF) << 8);
                    switch ((v1 >> 4) & 3)
                    {
                    case 0:
                        count += 2;
                        v2 = m_input.ReadByte();
                        for (int i = 0; i < count; ++i)
                            m_output[dst+i] = (byte)v2;
                        break;
                    case 1:
                        ++count;
                        Binary.CopyOverlapped (m_output, dst-m_stride, dst, count);
                        break;
                    case 2:
                        ++count;
                        Binary.CopyOverlapped (m_output, dst-2*m_stride, dst, count);
                        break;
                    case 3:
                        ++count;
                        Binary.CopyOverlapped (m_output, dst-4*m_stride, dst, count);
                        break;
                    }
                }
                else if (0 == (v1 & 0x30))
                {
                    int v2 = m_input.ReadByte();
                    int v19 = (v1 >> 3) & 1;
                    count = v2 + ((v1 & 7) << 8) + 1;
                    if (0 != v19)
                    {
                        m_input.Read (m_output, dst, 6);
                        Binary.CopyOverlapped (m_output, dst, dst+6, count*6);
                        ++count;
                        count *= 6;
                    }
                    else
                    {
                        m_input.Read (m_output, dst, 3);
                        Binary.CopyOverlapped (m_output, dst, dst+3, count*3);
                        ++count;
                        count *= 3;
                    }
                }
                else if (0 == (v1 & 0x20))
                {
                    int v30 = m_input.ReadByte();
                    int v31 = v30 + ((v1 & 0xF) << 8);
                    count = m_input.ReadByte() + 1;
                    int src = dst - 3 - 3 * v31; // auto v34 = &unpacked[dst - 3] - 3 * v31;
                    count *= 3;
                    Binary.CopyOverlapped (m_output, src, dst, count);
                }
                else
                {
                    int v37 = m_input.ReadByte();
                    int v38 = v37 + ((v1 & 0xF) << 8);
                    count = m_input.ReadByte() + 1;
                    int src = dst - 1 - v38; // auto v40 = &unpacked[dst - 1] - v38;
                    Binary.CopyOverlapped (m_output, src, dst, count);
                }
                if (0 == count)
                    throw new InvalidFormatException();
                dst += count;
            }
        }

        private byte[] UnpackBytes () // sub_403ED0
        {
            int total = m_width * m_height;
            var output = new byte[total];
            int dst = 0;
            while ( dst < total )
            {
                int count;
                int next = m_input.ReadByte();
                if (-1 == next)
                    break;
                if ((next & 0xF0) == 0xF0)
                    throw new InvalidFormatException();

                if (0 == (next & 0x80))
                {
                    if (0 != (next & 0x40))
                    {
                        count = (next & 0x3F) + 2;
                        int v4 = m_input.ReadByte();
                        for (int i = 0; i < count; ++i)
                            output[dst+i] = (byte)v4;
                    }
                    else
                    {
                        count = (next & 0x3F) + 1;
                        if (count != m_input.Read (output, dst, count))
                            break;
                    }
                }
                else if (0 == (next & 0x40))
                {
                    int v2 = m_input.ReadByte();
                    count = v2 | ((next & 0xF) << 8);
                    switch ((next >> 4) & 3)
                    {
                    case 0:
                        count += 2;
                        v2 = m_input.ReadByte();
                        for (int i = 0; i < count; ++i)
                            output[dst+i] = (byte)v2;
                        break;
                    case 1:
                        ++count;
                        Binary.CopyOverlapped (output, dst-m_width, dst, count);
                        break;
                    case 2:
                        ++count;
                        Binary.CopyOverlapped (output, dst-2*m_width, dst, count);
                        break;
                    case 3:
                        ++count;
                        Binary.CopyOverlapped (output, dst-4*m_width, dst, count);
                        break;
                    }
                }
                else if (0 == (next & 0x20))
                {
                    count = m_input.ReadByte() + ((next & 7) << 8) + 1;
                    switch ((next >> 3) & 3)
                    {
                    case 0:
                        m_input.Read (output, dst, 2);
                        Binary.CopyOverlapped (output, dst, dst+2, count*2);
                        ++count;
                        count *= 2;
                        break;
                    case 1:
                        m_input.Read (output, dst, 4);
                        Binary.CopyOverlapped (output, dst, dst+4, count*4);
                        ++count;
                        count *= 4;
                        break;
                    case 2:
                        m_input.Read (output, dst, 8);
                        Binary.CopyOverlapped (output, dst, dst+8, count*8);
                        ++count;
                        count *= 8;
                        break;
                    case 3:
                        m_input.Read (output, dst, 16);
                        Binary.CopyOverlapped (output, dst, dst+16, count*16);
                        ++count;
                        count *= 16;
                        break;
                    }
                }
                else
                {
                    int v36 = m_input.ReadByte() | ((next & 0xF) << 8);
                    count = m_input.ReadByte() + 1;
                    int src = dst - 1 - v36;
                    Binary.CopyOverlapped (output, src, dst, count);
                }
                dst += count;
            }
            return output;
        }

        private void FlipPixels ()
        {
            // flip pixels vertically
            var pixels = new byte[m_output.Length];
            int dst = 0;
            for (int src = m_stride * (m_height-1); src >= 0; src -= m_stride)
            {
                Buffer.BlockCopy (m_output, src, pixels, dst, m_stride);
                dst += m_stride;
            }
            m_output = pixels;
        }
    }
}
