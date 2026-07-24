using System.Runtime.ExceptionServices;
using LazerCollectionImporter;
using Realms;
using Xunit;

namespace LazerCollectionImporter.Tests;

/// <summary>Stand-in for any other lazer table, to verify we never touch them.</summary>
public partial class DummyItem : IRealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; }

    public string Title { get; set; } = string.Empty;
}

public class RealmTests : IDisposable
{
    private const ulong lazer_schema_version = 51;

    private readonly string dir = Directory.CreateTempSubdirectory("lci-realm-").FullName;
    private readonly string realmPath;

    /// <summary>
    /// Runs realm work on a dedicated thread with no SynchronizationContext.
    /// xUnit installs an async context that realm-dotnet's notification
    /// scheduler cannot post to (SEHException). The real console app has no
    /// SynchronizationContext either, so this also matches production.
    /// </summary>
    private static void OnRealmThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        }) { IsBackground = true };
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public RealmTests()
    {
        realmPath = Path.Combine(dir, "client.realm");

        // Simulate a lazer database: schema version 51, a BeatmapCollection and
        // an unrelated table with data that must survive our imports.
        OnRealmThread(() =>
        {
            using var realm = Realm.GetInstance(FullConfig());
            realm.Write(() =>
            {
                var existing = new BeatmapCollection
                {
                    ID = Guid.NewGuid(),
                    Name = "Existing",
                    LastModified = DateTimeOffset.Now.AddDays(-7),
                };
                existing.BeatmapMD5Hashes.Add("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                realm.Add(existing);
                realm.Add(new DummyItem { ID = Guid.NewGuid(), Title = "must survive" });
            });
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private RealmConfiguration FullConfig() => new(realmPath)
    {
        SchemaVersion = lazer_schema_version,
        Schema = new[] { typeof(BeatmapCollection), typeof(DummyItem) },
    };

    [Fact]
    public void Open_detects_schema_version_without_migrating() => OnRealmThread(() =>
    {
        using (var realm = LazerRealm.Open(realmPath))
            Assert.Equal(1, realm.All<BeatmapCollection>().Count());

        // Reopening with the full schema at v51 must still work: our Open must
        // not have bumped the schema version or altered other tables.
        using var full = Realm.GetInstance(FullConfig());
        Assert.Equal("must survive", full.All<DummyItem>().Single().Title);
    });

    [Fact]
    public void Merge_appends_and_creates_without_deleting() => OnRealmThread(() =>
    {
        var incoming = new[]
        {
            new RawCollection("Existing", new[]
            {
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", // duplicate (case-insensitive)
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "not-a-hash",
            }),
            new RawCollection("Brand new", new[]
            {
                "cccccccccccccccccccccccccccccccc",
                "cccccccccccccccccccccccccccccccc", // in-file duplicate
            }),
        };

        MergeStats stats;
        using (var realm = LazerRealm.Open(realmPath))
            stats = LazerRealm.Merge(realm, incoming, replace: false);

        Assert.Equal(1, stats.CollectionsCreated);
        Assert.Equal(1, stats.CollectionsUpdated);
        Assert.Equal(2, stats.HashesAdded); // bbb... + ccc...
        Assert.Equal(1, stats.InvalidHashesSkipped);

        using var full = Realm.GetInstance(FullConfig());
        var existing = full.All<BeatmapCollection>().Single(c => c.Name == "Existing");
        Assert.Equal(
            new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            existing.BeatmapMD5Hashes);
        var created = full.All<BeatmapCollection>().Single(c => c.Name == "Brand new");
        Assert.Equal(new[] { "cccccccccccccccccccccccccccccccc" }, created.BeatmapMD5Hashes);
        Assert.Equal("must survive", full.All<DummyItem>().Single().Title);
    });

    [Fact]
    public void Merge_replace_overwrites_content_but_keeps_collection() => OnRealmThread(() =>
    {
        Guid idBefore;
        using (var full = Realm.GetInstance(FullConfig()))
            idBefore = full.All<BeatmapCollection>().Single(c => c.Name == "Existing").ID;

        var incoming = new[]
        {
            new RawCollection("Existing", new[] { "dddddddddddddddddddddddddddddddd" }),
        };

        using (var realm = LazerRealm.Open(realmPath))
            LazerRealm.Merge(realm, incoming, replace: true);

        using var check = Realm.GetInstance(FullConfig());
        var existing = check.All<BeatmapCollection>().Single(c => c.Name == "Existing");
        Assert.Equal(idBefore, existing.ID); // same collection object, content replaced
        Assert.Equal(new[] { "dddddddddddddddddddddddddddddddd" }, existing.BeatmapMD5Hashes);
    });

    [Fact]
    public void Merge_is_idempotent() => OnRealmThread(() =>
    {
        var incoming = new[]
        {
            new RawCollection("Existing", new[] { "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }),
        };

        using (var realm = LazerRealm.Open(realmPath))
            LazerRealm.Merge(realm, incoming, replace: false);

        MergeStats second;
        using (var realm = LazerRealm.Open(realmPath))
            second = LazerRealm.Merge(realm, incoming, replace: false);

        Assert.Equal(0, second.CollectionsCreated);
        Assert.Equal(0, second.CollectionsUpdated);
        Assert.Equal(0, second.HashesAdded);
    });

    [Fact]
    public void Merge_remaps_unknown_hashes_to_installed_versions() => OnRealmThread(() =>
    {
        const string installedHash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        const string apiHash = "dddddddddddddddddddddddddddddddd";
        // an installed map whose local version differs from the online hash
        using (var realm = LazerRealm.Open(realmPath))
            realm.Write(() => realm.Add(new BeatmapInfo
            {
                ID = Guid.NewGuid(),
                OnlineID = 777,
                MD5Hash = installedHash,
            }));

        var incoming = new[] { new RawCollection("Remap", new[] { apiHash }) };
        MergeStats stats;
        using (var realm = LazerRealm.Open(realmPath))
            stats = LazerRealm.Merge(realm, incoming, replace: false,
                new Dictionary<string, int> { [apiHash] = 777 });

        Assert.Equal(1, stats.Remapped);
        Assert.Equal(0, stats.NotInstalled);
        using var check = LazerRealm.Open(realmPath);
        var col = check.All<BeatmapCollection>().Single(c => c.Name == "Remap");
        Assert.Equal(new[] { installedHash }, col.BeatmapMD5Hashes);
    });

    [Fact]
    public void Backup_creates_copy() => OnRealmThread(() =>
    {
        string backup = LazerRealm.Backup(realmPath);
        Assert.True(File.Exists(backup));
        Assert.Equal(new FileInfo(realmPath).Length, new FileInfo(backup).Length);
    });
}
