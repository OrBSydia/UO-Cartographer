﻿namespace Cartography.userPlugin.fileTypeConverters
{
    public enum FileType
    {
        ArtLegacyMUL,
        GumpartLegacyMUL,
        MapLegacyMUL,
        SoundLegacyMUL,
        TileArtLegacy,
        tileart
    }

    public class MUL2UOPConverter
    {
        public MUL2UOPConverter()
        {
        }

        private struct IdxEntry
        {
            public int m_Id;
            public int m_Offset;
            public int m_Size;
            public int m_Extra;
        }

        private struct TableEntry
        {
            public long m_Offset;
            public int m_HeaderLength;
            public int m_Size;
            public ulong m_Identifier;
            public uint m_Hash;
        }

        //
        // IO shortcuts
        //
        private BinaryReader OpenInput(string path)
        {
            if (path == null)
            {
                return null;
            }

            return new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        private BinaryWriter OpenOutput(string path)
        {
            if (path == null)
            {
                return null;
            }

            return new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None));
        }

        //
        // MUL -> UOP
        //
        public void ToUOP(string inFile, string inFileIdx, string outFile, FileType type, int typeIndex)
        {
            // Same for all UOP files
            long firstTable = 0x200;
            var tableSize = 0x3E8;

            // Sanity, in case firstTable is customized by you!
            if (firstTable < 0x28)
            {
                throw new Exception("At least 0x28 bytes are needed for the header.");
            }

            using var reader = OpenInput(inFile);
            using var readerIdx = OpenInput(inFileIdx);
            using var writer = OpenOutput(outFile);
            List<IdxEntry> idxEntries;

            if (type == FileType.MapLegacyMUL)
            {
                // No IDX file, just group the data into 0xC4000 long chunks
                var length = (int)reader.BaseStream.Length;
                idxEntries = new List<IdxEntry>((int)Math.Ceiling((double)length / 0xC4000));

                var position = 0;
                var id = 0;

                while (position < length)
                {
                    var e = new IdxEntry
                    {
                        m_Id = id++,
                        m_Offset = position,
                        m_Size = 0xC4000,
                        m_Extra = 0
                    };

                    idxEntries.Add(e);

                    position += 0xC4000;
                }
            }
            else
            {
                var idxEntryCount = (int)(readerIdx.BaseStream.Length / 12);
                idxEntries = new List<IdxEntry>(idxEntryCount);

                for (var i = 0; i < idxEntryCount; ++i)
                {
                    var offset = readerIdx.ReadInt32();

                    if (offset < 0)
                    {
                        _ = readerIdx.BaseStream.Seek(8, SeekOrigin.Current); // skip
                        continue;
                    }

                    var e = new IdxEntry
                    {
                        m_Id = i,
                        m_Offset = offset,
                        m_Size = readerIdx.ReadInt32(),
                        m_Extra = readerIdx.ReadInt32()
                    };

                    idxEntries.Add(e);
                }
            }

            // File header
            writer.Write(0x50594D); // MYP
            writer.Write(5); // version
            writer.Write(0xFD23EC43); // format timestamp?
            writer.Write(firstTable); // first table
            writer.Write(tableSize); // table size
            writer.Write(idxEntries.Count); // file count
            writer.Write(1); // modified count?
            writer.Write(1); // ?
            writer.Write(0); // ?

            // Padding
            for (var i = 0x28; i < firstTable; ++i)
            {
                writer.Write((byte)0);
            }

            var tableCount = (int)Math.Ceiling((double)idxEntries.Count / tableSize);
            var tableEntries = new TableEntry[tableSize];

            var hashFormat = GetHashFormat(type, typeIndex, out var maxId);

            for (var i = 0; i < tableCount; ++i)
            {
                var thisTable = writer.BaseStream.Position;

                var idxStart = i * tableSize;
                var idxEnd = Math.Min((i + 1) * tableSize, idxEntries.Count);

                // Table header
                writer.Write(tableSize);
                writer.Write((long)0); // next table, filled in later
                _ = writer.Seek(34 * tableSize, SeekOrigin.Current); // table entries, filled in later

                // Data
                var tableIdx = 0;

                for (var j = idxStart; j < idxEnd; ++j, ++tableIdx)
                {
                    _ = reader.BaseStream.Seek(idxEntries[j].m_Offset, SeekOrigin.Begin);
                    var data = reader.ReadBytes(idxEntries[j].m_Size);

                    tableEntries[tableIdx].m_Offset = writer.BaseStream.Position;
                    tableEntries[tableIdx].m_Size = data.Length;
                    tableEntries[tableIdx].m_Identifier = HashLittle2(string.Format(hashFormat, idxEntries[j].m_Id));
                    tableEntries[tableIdx].m_Hash = HashAdler32(data);

                    if (type == FileType.GumpartLegacyMUL)
                    {
                        // Prepend width/height from IDX's extra
                        var width = (idxEntries[j].m_Extra >> 16) & 0xFFFF;
                        var height = idxEntries[j].m_Extra & 0xFFFF;

                        writer.Write(width);
                        writer.Write(height);

                        tableEntries[tableIdx].m_Size += 8;
                    }

                    writer.Write(data);
                }

                var nextTable = writer.BaseStream.Position;

                // Go back and fix table header
                if (i < tableCount - 1)
                {
                    _ = writer.BaseStream.Seek(thisTable + 4, SeekOrigin.Begin);
                    writer.Write(nextTable);
                }
                else
                {
                    _ = writer.BaseStream.Seek(thisTable + 12, SeekOrigin.Begin);
                    // No need to fix the next table address, it's the last
                }

                // Table entries
                tableIdx = 0;

                for (var j = idxStart; j < idxEnd; ++j, ++tableIdx)
                {
                    writer.Write(tableEntries[tableIdx].m_Offset);
                    writer.Write(0); // header length
                    writer.Write(tableEntries[tableIdx].m_Size); // compressed size
                    writer.Write(tableEntries[tableIdx].m_Size); // decompressed size
                    writer.Write(tableEntries[tableIdx].m_Identifier);
                    writer.Write(tableEntries[tableIdx].m_Hash);
                    writer.Write((short)0); // compression method, none
                }

                // Fill remainder with empty entries
                for (; tableIdx < tableSize; ++tableIdx)
                {
                    writer.Write(m_EmptyTableEntry);
                }

                _ = writer.BaseStream.Seek(nextTable, SeekOrigin.Begin);
            }
        }

        private static readonly byte[] m_EmptyTableEntry = new byte[8 + 4 + 4 + 4 + 8 + 4 + 2];

        // 
        // UOP -> MUL
        //
        public void FromUOP(string inFile, string outFile, string outFileIdx, FileType type, int typeIndex)
        {
            var chunkIds = new Dictionary<ulong, int>();

            var format = GetHashFormat(type, typeIndex, out var maxId);

            for (var i = 0; i < maxId; ++i)
            {
                chunkIds[HashLittle2(string.Format(format, i))] = i;
            }

            var used = new bool[maxId];

            using var reader = OpenInput(inFile);
            using var writer = OpenOutput(outFile);
            using var writerIdx = OpenOutput(outFileIdx);
            if (reader.ReadInt32() != 0x50594D) // MYP
            {
                throw new ArgumentException("inFile is not a UOP file.");
            }

            var stream = reader.BaseStream;

            var version = reader.ReadInt32();
            _ = reader.ReadInt32(); // format timestamp? 0xFD23EC43
            var nextTable = reader.ReadInt64();

            do
            {
                // Table header
                _ = stream.Seek(nextTable, SeekOrigin.Begin);
                var entries = reader.ReadInt32();
                nextTable = reader.ReadInt64();

                // Table entries
                var offsets = new TableEntry[entries];

                for (var i = 0; i < entries; ++i)
                {
                    /*
                     * Empty entries are read too, because they do not always indicate the
                     * end of the table. (Example: 7.0.26.4+ Fel/Tram maps)
                     */
                    offsets[i].m_Offset = reader.ReadInt64();
                    offsets[i].m_HeaderLength = reader.ReadInt32(); // header length
                    offsets[i].m_Size = reader.ReadInt32(); // compressed size
                    _ = reader.ReadInt32(); // decompressed size
                    offsets[i].m_Identifier = reader.ReadUInt64(); // filename hash (HashLittle2)
                    offsets[i].m_Hash = reader.ReadUInt32(); // data hash (Adler32)
                    _ = reader.ReadInt16(); // compression method (0 = none, 1 = zlib)
                }

                // Copy chunks
                for (var i = 0; i < offsets.Length; ++i)
                {
                    if (offsets[i].m_Offset == 0)
                    {
                        continue; // skip empty entry
                    }

                    if (!chunkIds.TryGetValue(offsets[i].m_Identifier, out var chunkID))
                    {
                        throw new Exception("Unknown identifier encountered");
                    }

                    _ = stream.Seek(offsets[i].m_Offset + offsets[i].m_HeaderLength, SeekOrigin.Begin);
                    var chunkData = reader.ReadBytes(offsets[i].m_Size);

                    if (type == FileType.MapLegacyMUL)
                    {
                        // Write this chunk on the right position (no IDX file to point to it)
                        _ = writer.Seek(chunkID * 0xC4000, SeekOrigin.Begin);
                        writer.Write(chunkData);
                    }
                    else
                    {
                        var dataOffset = 0;

                        #region Idx
                        _ = writerIdx.Seek(chunkID * 12, SeekOrigin.Begin);
                        writerIdx.Write((int)writer.BaseStream.Position); // Position

                        switch (type)
                        {
                            case FileType.GumpartLegacyMUL:
                            {
                                // Width and height are prepended to the data
                                var width = chunkData[0] | (chunkData[1] << 8) | (chunkData[2] << 16) | (chunkData[3] << 24);
                                var height = chunkData[4] | (chunkData[5] << 8) | (chunkData[6] << 16) | (chunkData[7] << 24);

                                writerIdx.Write(offsets[i].m_Size - 8);
                                writerIdx.Write((width << 16) | height);
                                dataOffset = 8;
                                break;
                            }
                            case FileType.SoundLegacyMUL:
                            {
                                // Extra contains the ID of this sound file + 1
                                writerIdx.Write(offsets[i].m_Size);
                                writerIdx.Write(chunkID + 1);
                                break;
                            }
                            default:
                            {
                                writerIdx.Write(offsets[i].m_Size); // Size
                                writerIdx.Write(0); // Extra
                                break;
                            }
                        }

                        used[chunkID] = true;
                        #endregion

                        writer.Write(chunkData, dataOffset, chunkData.Length - dataOffset);
                    }
                }

                // Move to next table
                if (nextTable != 0)
                {
                    _ = stream.Seek(nextTable, SeekOrigin.Begin);
                }
            }
            while (nextTable != 0);

            // Fix idx
            // TODO: Only go until the last used entry? Does the client mind?
            if (writerIdx != null)
            {
                for (var i = 0; i < used.Length; ++i)
                {
                    if (!used[i])
                    {
                        _ = writerIdx.Seek(i * 12, SeekOrigin.Begin);
                        writerIdx.Write(-1);
                        writerIdx.Write((long)0);
                    }
                }
            }
        }

        //
        // Hash filename formats (remember: lower case!)
        //
        public static string GetHashFormat(FileType type, int typeIndex, out int maxId)
        {
            /*
             * MaxID is only used for constructing a lookup table.
             * Decrease to save some possibly unneeded computation.
             */
            maxId = 0x10000;

            switch (type)
            {
                case FileType.ArtLegacyMUL:
                {
                    maxId = 0x13FDC; // UOFiddler requires this exact index length to recognize UOHS art files
                    return "build/artlegacymul/{0:00000000}.tga";
                }
                case FileType.GumpartLegacyMUL:
                {
                    // MaxID = 0xEF3C on 7.0.8.2
                    return "build/gumpartlegacymul/{0:00000000}.tga";
                }
                case FileType.MapLegacyMUL:
                {
                    // MaxID = 0x71 on 7.0.8.2 for Fel/Tram
                    return string.Concat("build/map", typeIndex, "legacymul/{0:00000000}.dat");
                }
                case FileType.SoundLegacyMUL:
                {
                    // MaxID = 0x1000 on 7.0.8.2
                    return "build/soundlegacymul/{0:00000000}.dat";
                }
                default:
                {
                    throw new ArgumentException("Unknown file type!");
                }
            }
        }

        //
        // Hash functions (EA didn't write these, see http://burtleburtle.net/bob/c/lookup3.c)
        //
        public static ulong HashLittle2(string s)
        {
            var length = s.Length;

            uint a, b, c;
            a = b = c = 0xDEADBEEF + (uint)length;

            var k = 0;

            while (length > 12)
            {
                a += s[k];
                a += (uint)s[k + 1] << 8;
                a += (uint)s[k + 2] << 16;
                a += (uint)s[k + 3] << 24;
                b += s[k + 4];
                b += (uint)s[k + 5] << 8;
                b += (uint)s[k + 6] << 16;
                b += (uint)s[k + 7] << 24;
                c += s[k + 8];
                c += (uint)s[k + 9] << 8;
                c += (uint)s[k + 10] << 16;
                c += (uint)s[k + 11] << 24;

                a -= c;
                a ^= (c << 4) | (c >> 28);
                c += b;
                b -= a;
                b ^= (a << 6) | (a >> 26);
                a += c;
                c -= b;
                c ^= (b << 8) | (b >> 24);
                b += a;
                a -= c;
                a ^= (c << 16) | (c >> 16);
                c += b;
                b -= a;
                b ^= (a << 19) | (a >> 13);
                a += c;
                c -= b;
                c ^= (b << 4) | (b >> 28);
                b += a;

                length -= 12;
                k += 12;
            }

            if (length != 0)
            {
                switch (length)
                {
                    case 12:
                    c += (uint)s[k + 11] << 24;
                    goto case 11;
                    case 11:
                    c += (uint)s[k + 10] << 16;
                    goto case 10;
                    case 10:
                    c += (uint)s[k + 9] << 8;
                    goto case 9;
                    case 9:
                    c += s[k + 8];
                    goto case 8;
                    case 8:
                    b += (uint)s[k + 7] << 24;
                    goto case 7;
                    case 7:
                    b += (uint)s[k + 6] << 16;
                    goto case 6;
                    case 6:
                    b += (uint)s[k + 5] << 8;
                    goto case 5;
                    case 5:
                    b += s[k + 4];
                    goto case 4;
                    case 4:
                    a += (uint)s[k + 3] << 24;
                    goto case 3;
                    case 3:
                    a += (uint)s[k + 2] << 16;
                    goto case 2;
                    case 2:
                    a += (uint)s[k + 1] << 8;
                    goto case 1;
                    case 1:
                    a += s[k];
                    break;
                }

                c ^= b;
                c -= (b << 14) | (b >> 18);
                a ^= c;
                a -= (c << 11) | (c >> 21);
                b ^= a;
                b -= (a << 25) | (a >> 7);
                c ^= b;
                c -= (b << 16) | (b >> 16);
                a ^= c;
                a -= (c << 4) | (c >> 28);
                b ^= a;
                b -= (a << 14) | (a >> 18);
                c ^= b;
                c -= (b << 24) | (b >> 8);
            }

            return ((ulong)b << 32) | c;
        }

        public static uint HashAdler32(byte[] d)
        {
            uint a = 1;
            uint b = 0;

            for (var i = 0; i < d.Length; i++)
            {
                a = (a + d[i]) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }
    }
}