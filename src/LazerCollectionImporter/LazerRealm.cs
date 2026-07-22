using System.Text.RegularExpressions;
using Realms;
using Realms.Exceptions;

namespace LazerCollectionImporter;

public sealed record MergeStats(
    int CollectionsCreated,
    int CollectionsUpdated,
    int HashesAdded,
    int InvalidHashesSkipped);

/// <summary>
/// Safe access to osu!lazer's client.realm.
///
/// Safety rules (learned from lazer's RealmAccess and existing tools):
/// - Never open the file with a schema version HIGHER than the file's own:
///   that would migrate/stamp it upward and make lazer set the database aside
///   as "client_newer_version.realm" on next launch. We therefore probe with
///   version 0 (always throws before modifying anything), parse the file's
///   actual version from the error message, and reopen with exactly that.
/// - Only declare the BeatmapCollection table in our schema: realm leaves all
///   other tables untouched when they are not part of the opened schema.
/// - Merge by collection name (same behaviour as lazer's own
///   LegacyCollectionImporter); nothing is ever deleted implicitly.
/// </summary>
public static partial class LazerRealm
{
    [GeneratedRegex(@"(\d+)(?!.*\d)")]
    private static partial Regex LastNumberRegex();

    /// <summary>Finds client.realm: explicit path, else %APPDATA%\osu (honoring storage.ini).</summary>
    public static string ResolvePath(string? overridePath)
    {
        if (overridePath != null)
        {
            string p = Directory.Exists(overridePath)
                ? Path.Combine(overridePath, "client.realm")
                : overridePath;
            if (!File.Exists(p))
                throw new FileNotFoundException($"No client.realm found at \"{overridePath}\".");
            return p;
        }

        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");

        // lazer stores a custom data directory in storage.ini ("FullPath = D:\...").
        string ini = Path.Combine(dataDir, "storage.ini");
        if (File.Exists(ini))
        {
            foreach (string line in File.ReadAllLines(ini))
            {
                var m = Regex.Match(line, @"^\s*FullPath\s*=\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                if (m.Success && Directory.Exists(m.Groups[1].Value))
                {
                    dataDir = m.Groups[1].Value;
                    break;
                }
            }
        }

        string realmPath = Path.Combine(dataDir, "client.realm");
        if (!File.Exists(realmPath))
            throw new FileNotFoundException(
                $"client.realm not found in \"{dataDir}\". " +
                "If osu!lazer uses a custom data folder, pass it with: --realm <path>");
        return realmPath;
    }

    /// <summary>Copies client.realm next to itself with a timestamped suffix.</summary>
    public static string Backup(string realmPath)
    {
        string backupPath = $"{realmPath}.backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(realmPath, backupPath, overwrite: false);
        return backupPath;
    }

    public static Realm Open(string realmPath)
    {
        ulong fileVersion = DetectSchemaVersion(realmPath);
        var config = new RealmConfiguration(realmPath)
        {
            SchemaVersion = fileVersion,
            Schema = new[] { typeof(BeatmapCollection) },
        };

        try
        {
            return Realm.GetInstance(config);
        }
        catch (RealmException e)
        {
            throw new InvalidOperationException(
                "Could not open the osu!lazer database (schema version " + fileVersion + "). " +
                "The BeatmapCollection table may have changed in a recent lazer update - " +
                "please report this. Original error: " + e.Message, e);
        }
    }

    /// <summary>
    /// Detects the file's schema version by opening with version 0: realm-core
    /// throws "Provided schema version 0 is less than last set version N" before
    /// touching the file. Works for every possible file version, past or future.
    /// </summary>
    private static ulong DetectSchemaVersion(string realmPath)
    {
        var probe = new RealmConfiguration(realmPath)
        {
            SchemaVersion = 0,
            Schema = new[] { typeof(BeatmapCollection) },
        };

        try
        {
            // Only succeeds if the file's schema version is 0 - never the case
            // for a real lazer database, but then 0 is simply the correct answer.
            Realm.GetInstance(probe).Dispose();
            return 0;
        }
        catch (RealmException e)
        {
            if (e.Message.Contains("file format version"))
                throw new InvalidOperationException(
                    "The database uses a different realm-core file format than this tool. " +
                    "This happens when osu!lazer upgrades its Realm library - the tool needs " +
                    "to be rebuilt with the matching Realm package version. Original error: " + e.Message, e);

            // Sanity bound: lazer's schema version is 51 in 2026 and grows by a few
            // per year. Anything huge means we parsed something else (e.g. digits
            // from a file path) - opening with it could migrate the db upward.
            Match m = LastNumberRegex().Match(e.Message);
            if (m.Success && ulong.TryParse(m.Value, out ulong version) && version is > 0 and < 10_000)
                return version;

            throw new InvalidOperationException(
                "Could not detect the database schema version. Original error: " + e.Message, e);
        }
    }

    public static List<(string Name, int Count, DateTimeOffset LastModified)> List(Realm realm)
        => realm.All<BeatmapCollection>()
            .AsEnumerable()
            .Select(c => (c.Name, c.BeatmapMD5Hashes.Count, c.LastModified))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Md5Regex();

    /// <summary>Merges collections into the realm. Never deletes collections.</summary>
    public static MergeStats Merge(Realm realm, IEnumerable<RawCollection> incoming, bool replace)
    {
        int created = 0, updated = 0, added = 0, invalid = 0;

        realm.Write(() =>
        {
            var byName = new Dictionary<string, BeatmapCollection>(StringComparer.Ordinal);
            foreach (var c in realm.All<BeatmapCollection>())
                byName.TryAdd(c.Name, c);

            foreach (var col in incoming)
            {
                var hashes = new List<string>(col.Hashes.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string raw in col.Hashes)
                {
                    string h = raw.Trim().ToLowerInvariant();
                    if (!Md5Regex().IsMatch(h)) { invalid++; continue; }
                    if (seen.Add(h)) hashes.Add(h);
                }

                if (byName.TryGetValue(col.Name, out var existing))
                {
                    if (replace)
                    {
                        bool changed = existing.BeatmapMD5Hashes.Count != hashes.Count
                            || !new HashSet<string>(existing.BeatmapMD5Hashes).SetEquals(hashes);
                        if (changed)
                        {
                            existing.BeatmapMD5Hashes.Clear();
                            foreach (string h in hashes) existing.BeatmapMD5Hashes.Add(h);
                            existing.LastModified = DateTimeOffset.Now;
                            updated++;
                            added += hashes.Count;
                        }
                    }
                    else
                    {
                        var have = new HashSet<string>(existing.BeatmapMD5Hashes, StringComparer.Ordinal);
                        int before = added;
                        foreach (string h in hashes)
                        {
                            if (!have.Add(h)) continue;
                            existing.BeatmapMD5Hashes.Add(h);
                            added++;
                        }
                        if (added > before)
                        {
                            existing.LastModified = DateTimeOffset.Now;
                            updated++;
                        }
                    }
                }
                else
                {
                    var fresh = new BeatmapCollection
                    {
                        ID = Guid.NewGuid(),
                        Name = col.Name,
                        LastModified = DateTimeOffset.Now,
                    };
                    foreach (string h in hashes) fresh.BeatmapMD5Hashes.Add(h);
                    realm.Add(fresh);
                    byName[col.Name] = fresh;
                    created++;
                    added += hashes.Count;
                }
            }
        });

        return new MergeStats(created, updated, added, invalid);
    }
}
