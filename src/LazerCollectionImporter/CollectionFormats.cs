using System.IO.Compression;
using System.Text;

namespace LazerCollectionImporter;

/// <summary>A collection read from a file: a name and the MD5 hashes of its .osu files.</summary>
public sealed record RawCollection(string Name, IReadOnlyList<string> Hashes);

/// <summary>
/// Readers for the two collection file formats:
/// - legacy osu!stable collection.db (also produced by many tools)
/// - .osdb collection files (all versions, "o!dm" .. "o!dm8min")
///   (legacy format documented in the osu! wiki, "Legacy database file structure")
/// </summary>
public static class CollectionFormats
{
    /// <summary>Reads a collection file, auto-detecting the format from its content.</summary>
    public static List<RawCollection> ReadFile(string path)
    {
        byte[] head = new byte[8];
        using (var probe = File.OpenRead(path))
        {
            int n = probe.Read(head, 0, head.Length);
            if (n < 8) throw new FormatException($"\"{Path.GetFileName(path)}\" is too small to be a collection file.");
        }

        // .osdb starts with a .NET BinaryWriter string: 7-bit length prefix then "o!dm...".
        // Every known version string is < 128 chars so the prefix is a single byte.
        if (head[1] == (byte)'o' && head[2] == (byte)'!' && head[3] == (byte)'d' && head[4] == (byte)'m')
            return ReadOsdb(path);

        return ReadLegacyDb(path);
    }

    // ---------------------------------------------------------------- .osdb

    private static readonly Dictionary<string, int> osdb_versions = new()
    {
        { "o!dm", 1 }, { "o!dm2", 2 }, { "o!dm3", 3 }, { "o!dm4", 4 },
        { "o!dm5", 5 }, { "o!dm6", 6 }, { "o!dm7", 7 }, { "o!dm8", 8 },
        { "o!dm7min", 1007 }, { "o!dm8min", 1008 },
    };

    public static List<RawCollection> ReadOsdb(string path)
    {
        using var fs = File.OpenRead(path);
        using var outer = new BinaryReader(fs, Encoding.UTF8);

        string versionString = outer.ReadString();
        if (!osdb_versions.TryGetValue(versionString, out int version))
            throw new FormatException($"Unrecognized .osdb version header \"{versionString}\".");
        bool minimal = versionString.EndsWith("min", StringComparison.Ordinal);

        BinaryReader reader = outer;
        MemoryStream? decompressed = null;
        try
        {
            if (version >= 7)
            {
                // Remainder of the file is a gzip stream (written by SharpCompress with a
                // FNAME header entry; GZipStream skips optional header fields per RFC 1952).
                decompressed = new MemoryStream();
                using (var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true))
                    gz.CopyTo(decompressed);
                decompressed.Position = 0;
                reader = new BinaryReader(decompressed, Encoding.UTF8);
                _ = reader.ReadString(); // version string is repeated inside the compressed body
            }

            _ = reader.ReadDouble();          // save date (DateTime.ToOADate)
            _ = reader.ReadString();          // editor (who saved the file)
            int collectionCount = reader.ReadInt32();
            CheckCount(collectionCount, "collection");

            var result = new List<RawCollection>(collectionCount);
            for (int i = 0; i < collectionCount; i++)
            {
                string name = reader.ReadString();
                if (version >= 7) _ = reader.ReadInt32(); // osu!stats online id

                int mapCount = reader.ReadInt32();
                CheckCount(mapCount, "beatmap");
                var hashes = new List<string>(mapCount);

                for (int j = 0; j < mapCount; j++)
                {
                    _ = reader.ReadInt32();                    // map id
                    if (version >= 2) _ = reader.ReadInt32();  // mapset id
                    if (!minimal)
                    {
                        _ = reader.ReadString();               // artist (roman)
                        _ = reader.ReadString();               // title (roman)
                        _ = reader.ReadString();               // difficulty name
                    }
                    string md5 = reader.ReadString();
                    if (version >= 4) _ = reader.ReadString(); // user comment
                    if (version >= 8 || (version >= 5 && !minimal))
                        _ = reader.ReadByte();                 // play mode
                    if (version >= 8 || (version >= 6 && !minimal))
                        _ = reader.ReadDouble();               // nomod star rating
                    hashes.Add(md5);
                }

                if (version >= 3)
                {
                    int hashOnlyCount = reader.ReadInt32();
                    CheckCount(hashOnlyCount, "hash-only beatmap");
                    for (int j = 0; j < hashOnlyCount; j++)
                        hashes.Add(reader.ReadString());
                }

                result.Add(new RawCollection(name, hashes));
            }

            // Every valid .osdb file ends with this exact footer string; not
            // finding it means our read position drifted (corrupt/truncated file).
            if (reader.ReadString() != "By Piotrekol")
                throw new FormatException("Invalid .osdb footer - the file may be corrupted.");

            return result;
        }
        catch (EndOfStreamException)
        {
            throw new FormatException("Unexpected end of .osdb file - the file may be corrupted.");
        }
        finally
        {
            if (!ReferenceEquals(reader, outer)) reader.Dispose();
            decompressed?.Dispose();
        }
    }

    // ------------------------------------------------- legacy collection.db

    public static List<RawCollection> ReadLegacyDb(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs, Encoding.UTF8);

        try
        {
            _ = reader.ReadInt32(); // osu! version (e.g. 20220101)
            int collectionCount = reader.ReadInt32();
            CheckCount(collectionCount, "collection");

            var result = new List<RawCollection>(collectionCount);
            for (int i = 0; i < collectionCount; i++)
            {
                string name = ReadOsuString(reader);
                int mapCount = reader.ReadInt32();
                CheckCount(mapCount, "beatmap");
                var hashes = new List<string>(mapCount);
                for (int j = 0; j < mapCount; j++)
                    hashes.Add(ReadOsuString(reader));
                result.Add(new RawCollection(name, hashes));
            }

            return result;
        }
        catch (EndOfStreamException)
        {
            throw new FormatException("Unexpected end of collection.db file - the file may be corrupted.");
        }
    }

    /// <summary>osu! string: flag byte 0x00 = empty, 0x0b = .NET BinaryReader string.</summary>
    private static string ReadOsuString(BinaryReader reader)
    {
        byte flag = reader.ReadByte();
        return flag switch
        {
            0x00 => string.Empty,
            0x0b => reader.ReadString(),
            _ => throw new FormatException(
                $"Invalid string marker 0x{flag:x2} - this does not look like a collection.db file."),
        };
    }

    private static void CheckCount(int count, string what)
    {
        if (count is < 0 or > 10_000_000)
            throw new FormatException($"Implausible {what} count ({count}) - the file may be corrupted.");
    }
}
