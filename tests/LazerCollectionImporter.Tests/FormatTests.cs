using System.IO.Compression;
using System.Text;
using LazerCollectionImporter;
using Xunit;

namespace LazerCollectionImporter.Tests;

public class FormatTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("lci-tests-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string TempFile(string name) => Path.Combine(dir, name);

    private const string hash_a = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string hash_b = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string hash_c = "cccccccccccccccccccccccccccccccc";

    // ------------------------------------------------------------- helpers

    /// <summary>Writes an .osdb body (the part that gets gzipped in v7+).</summary>
    private static byte[] OsdbBody(string versionString, int version, bool minimal,
        params (string Name, (string Md5, bool HashOnly)[] Maps)[] collections)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(versionString);
        w.Write(DateTime.Now.ToOADate());
        w.Write("unit-test");
        w.Write(collections.Length);
        foreach (var (name, maps) in collections)
        {
            w.Write(name);
            if (version >= 7) w.Write(-1); // online id
            var full = maps.Where(m => !m.HashOnly).ToArray();
            var hashOnly = maps.Where(m => m.HashOnly).ToArray();
            w.Write(full.Length);
            foreach (var (md5, _) in full)
            {
                w.Write(123456);                       // map id
                if (version >= 2) w.Write(654321);     // mapset id
                if (!minimal)
                {
                    w.Write("Artist");
                    w.Write("Title");
                    w.Write("Diff");
                }
                w.Write(md5);
                if (version >= 4) w.Write("comment");
                if (version >= 8 || (version >= 5 && !minimal)) w.Write((byte)0);
                if (version >= 8 || (version >= 6 && !minimal)) w.Write(5.25);
            }
            if (version >= 3)
            {
                w.Write(hashOnly.Length);
                foreach (var (md5, _) in hashOnly) w.Write(md5);
            }
        }
        w.Write("By Piotrekol");
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Gzips a payload with an FNAME header field, byte-compatible with what
    /// SharpCompress' GZipArchive produces in real .osdb v7+ files.
    /// </summary>
    private static byte[] GzipWithFileName(byte[] payload, string entryName)
    {
        using var ms = new MemoryStream();
        // gzip header: magic, deflate, FLG=FNAME, mtime(4), XFL, OS
        ms.Write(new byte[] { 0x1f, 0x8b, 0x08, 0x08, 0, 0, 0, 0, 0, 0xff });
        var nameBytes = Encoding.ASCII.GetBytes(entryName);
        ms.Write(nameBytes);
        ms.WriteByte(0); // zero-terminated name
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(payload);
        ms.Write(BitConverter.GetBytes(Crc32(payload)));
        ms.Write(BitConverter.GetBytes((uint)payload.Length));
        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xedb88320 & (uint)-(crc & 1));
        }
        return ~crc;
    }

    private string WriteOsdbFile(string fileName, string versionString, int version, bool minimal,
        params (string Name, (string Md5, bool HashOnly)[] Maps)[] collections)
    {
        byte[] body = OsdbBody(versionString, version, minimal, collections);
        string path = TempFile(fileName);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.UTF8);
        w.Write(versionString);
        if (version >= 7)
        {
            byte[] gz = GzipWithFileName(body, "uncompressed_collection_NOTosdb.NOTosdb");
            w.Write(gz, 0, gz.Length);
        }
        else
        {
            // v1-6: the body follows the magic directly, without repeating it.
            // Our body starts with the magic, so skip that prefix.
            using var br = new BinaryReader(new MemoryStream(body), Encoding.UTF8);
            _ = br.ReadString();
            int offset = (int)br.BaseStream.Position;
            w.Write(body, offset, body.Length - offset);
        }
        return path;
    }

    // --------------------------------------------------------------- .osdb

    [Fact]
    public void Osdb_v8_full_roundtrip()
    {
        string path = WriteOsdbFile("v8.osdb", "o!dm8", 8, minimal: false,
            ("My favs", new[] { (hash_a, false), (hash_b, false), (hash_c, true) }),
            ("Empty", Array.Empty<(string, bool)>()));

        var cols = CollectionFormats.ReadOsdb(path);

        Assert.Equal(2, cols.Count);
        Assert.Equal("My favs", cols[0].Name);
        Assert.Equal(new[] { hash_a, hash_b, hash_c }, cols[0].Hashes);
        Assert.Equal("Empty", cols[1].Name);
        Assert.Empty(cols[1].Hashes);
    }

    [Fact]
    public void Osdb_v8min_roundtrip()
    {
        string path = WriteOsdbFile("v8min.osdb", "o!dm8min", 1008, minimal: true,
            ("Mins", new[] { (hash_a, false) }));

        var cols = CollectionFormats.ReadOsdb(path);

        Assert.Single(cols);
        Assert.Equal(new[] { hash_a }, cols[0].Hashes);
    }

    [Fact]
    public void Osdb_v4_uncompressed_roundtrip()
    {
        string path = WriteOsdbFile("v4.osdb", "o!dm4", 4, minimal: false,
            ("Old", new[] { (hash_a, false), (hash_b, true) }));

        var cols = CollectionFormats.ReadOsdb(path);

        Assert.Single(cols);
        Assert.Equal(new[] { hash_a, hash_b }, cols[0].Hashes);
    }

    [Fact]
    public void Osdb_bad_header_throws()
    {
        string path = TempFile("bad.osdb");
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
            w.Write("o!dm99");
        Assert.Throws<FormatException>(() => CollectionFormats.ReadOsdb(path));
    }

    // ------------------------------------------------------- collection.db

    private string WriteLegacyDb(string fileName, params (string Name, string[] Hashes)[] collections)
    {
        string path = TempFile(fileName);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.UTF8);
        w.Write(20220101);
        w.Write(collections.Length);
        foreach (var (name, hashes) in collections)
        {
            w.Write((byte)0x0b);
            w.Write(name);
            w.Write(hashes.Length);
            foreach (string h in hashes)
            {
                w.Write((byte)0x0b);
                w.Write(h);
            }
        }
        return path;
    }

    [Fact]
    public void LegacyDb_roundtrip()
    {
        string path = WriteLegacyDb("collection.db",
            ("stable favs", new[] { hash_a, hash_b }),
            ("empty", Array.Empty<string>()));

        var cols = CollectionFormats.ReadLegacyDb(path);

        Assert.Equal(2, cols.Count);
        Assert.Equal("stable favs", cols[0].Name);
        Assert.Equal(new[] { hash_a, hash_b }, cols[0].Hashes);
    }

    [Fact]
    public void LegacyDb_garbage_throws()
    {
        string path = TempFile("garbage.db");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 42, 42, 42 });
        Assert.Throws<FormatException>(() => CollectionFormats.ReadLegacyDb(path));
    }

    // ------------------------------------------------------ auto-detection

    [Fact]
    public void ReadFile_detects_format_from_content()
    {
        string osdb = WriteOsdbFile("detect.osdb", "o!dm8", 8, minimal: false,
            ("A", new[] { (hash_a, false) }));
        string legacy = WriteLegacyDb("detect.db", ("B", new[] { hash_b }));

        Assert.Equal("A", CollectionFormats.ReadFile(osdb)[0].Name);
        Assert.Equal("B", CollectionFormats.ReadFile(legacy)[0].Name);
    }
}
