//! \file       ArcGRP.cs
//! \date       Sun Mar 20 02:07:17 2016
//! \brief      Ankh resource archive.
//
// Copyright (C) 2016 by morkt
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
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using GameRes.Utility;

namespace GameRes.Formats.Ankh
{
    [Export(typeof(ArchiveFormat))]
    public class GrpOpener : ArchiveFormat
    {
        public override string         Tag { get { return "GRP/ANKH"; } }
        public override string Description { get { return "Ankh resource archive"; } }
        public override uint     Signature { get { return 0; } }
        public override bool  IsHierarchic { get { return false; } }
        public override bool     CanCreate { get { return false; } }

        public GrpOpener ()
        {
            Extensions = new string[] { "grp" };
        }

        public override ArcFile TryOpen (ArcView file)
        {
            uint first_offset = file.View.ReadUInt32 (0);
            if (first_offset < 8 || first_offset >= file.MaxOffset)
                return null;
            int count = (int)(first_offset - 8) / 4;
            if (!IsSaneCount (count))
                return null;

            var base_name = Path.GetFileNameWithoutExtension (file.Name);
            uint index_offset = 0;
            uint next_offset = first_offset;
            var dir = new List<Entry> (count);
            for (int i = 0; i < count; ++i)
            {
                var entry = new PackedEntry { Offset = next_offset };
                index_offset += 4;
                next_offset = file.View.ReadUInt32 (index_offset);
                if (next_offset < entry.Offset)
                    return null;
                entry.Size = (uint)(next_offset - entry.Offset);
                entry.UnpackedSize = entry.Size;
                if (entry.Size != 0)
                {
                    if (!entry.CheckPlacement (file.MaxOffset))
                        return null;
                    entry.Name = string.Format ("{0}#{1:D4}", base_name, i);
                    dir.Add (entry);
                }
            }
            if (0 == dir.Count)
                return null;
            foreach (PackedEntry entry in dir)
            {
                if (entry.Size < 4)
                    continue;
                uint unpacked_size = file.View.ReadUInt32 (entry.Offset);
                if (entry.Size > 8 && file.View.AsciiEqual (entry.Offset+4, "HDJ\0"))
                {
                    if (file.View.AsciiEqual (entry.Offset+12, "BM"))
                    {
                        entry.Name = Path.ChangeExtension (entry.Name, "bmp");
                        entry.Type = "image";
                    }
                    entry.UnpackedSize = unpacked_size;
                    entry.IsPacked = true;
                }
                else if (entry.Size > 12 && file.View.AsciiEqual (entry.Offset+8, "RIFF"))
                {
                    entry.Name = Path.ChangeExtension (entry.Name, "wav");
                    entry.Type = "audio";
                    entry.UnpackedSize = unpacked_size;
                    entry.IsPacked = true;
                }
                else if (0x4D42 == (unpacked_size & 0xFFFF))
                {
                    entry.Name = Path.ChangeExtension (entry.Name, "bmp");
                    entry.Type = "image";
                }
            }
            return new ArcFile (file, this, dir);
        }

        public override Stream OpenEntry (ArcFile arc, Entry entry)
        {
            if (entry.Size > 8 && arc.File.View.AsciiEqual (entry.Offset+4, "HDJ\0"))
                return OpenImage (arc, entry);
            else if (entry.Size > 12 && 'W' == arc.File.View.ReadByte (entry.Offset+4)
                && arc.File.View.AsciiEqual (entry.Offset+8, "RIFF"))
                return OpenAudio (arc, entry);
            else
                return base.OpenEntry (arc, entry);
        }

        Stream OpenImage (ArcFile arc, Entry entry)
        {
            int unpacked_size = arc.File.View.ReadInt32 (entry.Offset);
            if (unpacked_size <= 0)
                return base.OpenEntry (arc, entry);
            using (var packed = arc.File.CreateStream (entry.Offset+8, entry.Size-8))
            using (var reader = new GrpUnpacker (packed))
            {
                var unpacked = new byte[unpacked_size];
                reader.UnpackHDJ (unpacked, 0);
                return new MemoryStream (unpacked);
            }
        }

        Stream OpenAudio (ArcFile arc, Entry entry)
        {
            int unpacked_size = arc.File.View.ReadInt32 (entry.Offset);
            byte pack_type = arc.File.View.ReadByte (entry.Offset+5);
            byte channels = arc.File.View.ReadByte (entry.Offset+6);
            byte header_size = arc.File.View.ReadByte (entry.Offset+7);
            if (unpacked_size <= 0 || header_size > unpacked_size
                || !('A' == pack_type || 'S' == pack_type))
                return base.OpenEntry (arc, entry);
            var unpacked = new byte[unpacked_size];
            arc.File.View.Read (entry.Offset+8, unpacked, 0, header_size);
            uint packed_size = entry.Size - 8 - header_size;
            using (var packed = arc.File.CreateStream (entry.Offset+8+header_size, packed_size))
            using (var reader = new GrpUnpacker (packed))
            {
                if ('A' == pack_type)
                    reader.UnpackA (unpacked, header_size, channels);
                else
                    reader.UnpackS (unpacked, header_size, channels);
                return new MemoryStream (unpacked);
            }
        }
    }

    internal sealed class GrpUnpacker : IDisposable
    {
        BinaryReader        m_input;
        uint                m_bits;
        int                 m_cached_bits;

        public GrpUnpacker (Stream input)
        {
            m_input = new ArcView.Reader (input);
        }

        public void UnpackHDJ (byte[] output, int dst)
        {
            ResetBits();
            int word_count = 0;
            int byte_count = 0;
            uint next_byte = 0;
            uint next_word = 0;
            while (dst < output.Length)
            {
                if (GetNextBit() != 0)
                {
                    int count = 0;
                    bool long_count = false;
                    int offset;
                    if (GetNextBit() != 0)
                    {
                        if (0 == word_count)
                        {
                            next_word = m_input.ReadUInt32();
                            word_count = 2;
                        }
                        count = (int)((next_word >> 13) & 7) + 3;
                        offset = (int)(next_word | 0xFFFFE000);
                        next_word >>= 16;
                        --word_count;
                        long_count = 10 == count;
                    }
                    else
                    {
                        count = GetBits (2) + 2;
                        long_count = 5 == count;
                        if (0 == byte_count)
                        {
                            next_byte = m_input.ReadUInt32();
                            byte_count = 4;
                        }
                        offset = (int)(next_byte | 0xFFFFFF00);
                        next_byte >>= 8;
                        --byte_count;
                    }
                    if (long_count)
                    {
                        int n = 0;
                        while (GetNextBit() != 0)
                            ++n;

                        if (n != 0)
                            count += GetBits (n) + 1;
                    }
                    Binary.CopyOverlapped (output, dst+offset, dst, count);
                    dst += count;
                }
                else
                {
                    if (0 == byte_count)
                    {
                        next_byte = m_input.ReadUInt32();
                        byte_count = 4;
                    }
                    output[dst++] = (byte)next_byte;
                    next_byte >>= 8;
                    --byte_count;
                }
            }
        }

        public void UnpackS (byte[] output, int dst, int channels)
        {
            if (channels != 1)
                m_input.BaseStream.Seek ((channels-1) * 4, SeekOrigin.Current);
            int step = channels * 2;
            for (int i = 0; i < channels; ++i)
            {
                ResetBits();
                int pos = dst;
                short last_word = 0;
                while (pos < output.Length)
                {
                    int word;
                    if (GetNextBit() != 0)
                    {
                        if (GetNextBit() != 0)
                        {
                            word = GetBits (10) << 6;
                        }
                        else
                        {
                            int repeat;
                            if (GetNextBit() != 0)
                            {
                                int bit_length = 0;
                                do
                                {
                                    ++bit_length;
                                }
                                while (GetNextBit() != 0);
                                repeat = GetBits (bit_length) + 4;
                            }
                            else
                            {
                                repeat = GetBits (2);
                            }
                            word = 0;
                            while (repeat --> 0)
                            {
                                output[pos]   = 0;
                                output[pos+1] = 0;
                                pos += step;
                            }
                        }
                    }
                    else
                    {
                        int adjust = (short)(GetBits (5) << 11) >> 5;
                        word = last_word + adjust;
                    }
                    LittleEndian.Pack ((short)word, output, pos);
                    last_word = (short)word;
                    pos += step;
                }
                dst += 2;
            }
        }

        public void UnpackA (byte[] output, int dst, int channels)
        {
            if (channels != 1)
                m_input.BaseStream.Seek ((channels-1) * 4, SeekOrigin.Current);
            int step = 2 * channels;
            for (int i = 0; i < channels; ++i)
            {
                int pos = dst;
                ResetBits();
                while (pos < output.Length)
                {
                    int word = GetBits (10) << 6;;
                    LittleEndian.Pack ((short)word, output, pos);
                    pos += step;
                }
                dst += 2;
            }
        }

        void ResetBits ()
        {
            m_cached_bits = 0;
        }

        int GetNextBit ()
        {
            return GetBits (1);
        }

        int GetBits (int count)
        {
            if (0 == m_cached_bits)
            {
                m_bits = m_input.ReadUInt32();
                m_cached_bits = 32;
            }
            uint val;
            if (m_cached_bits < count)
            {
                uint next_bits = m_input.ReadUInt32();
                val = (m_bits | (next_bits >> m_cached_bits)) >> (32 - count);
                m_bits = next_bits << (count - m_cached_bits);
                m_cached_bits = 32 - (count - m_cached_bits);
            }
            else
            {
                val = m_bits >> (32 - count);
                m_bits <<= count;
                m_cached_bits -= count;
            }
            return (int)val;
        }

        #region IDisposable Members
        bool _disposed = false;
        public void Dispose ()
        {
            if (!_disposed)
            {
                m_input.Dispose();
                _disposed = true;
            }
        }
        #endregion
    }
}
