using System.Diagnostics;
using LazerCollectionImporter;

const string usage = """
    LazerCollectionImporter - import osu! collections into osu!lazer

    Usage:
      LazerCollectionImporter <files...> [options]

      Tip: you can simply drag & drop one or more .osdb / collection.db
      files onto LazerCollectionImporter.exe.

    Accepted files:
      collection.db   legacy osu!stable format (any tool that exports it)
      *.osdb          collection file format, all versions (o!dm .. o!dm8min)

    Options:
      --realm <path>  client.realm file or lazer data folder (default: auto-detect,
                      honors a custom folder configured in storage.ini)
      --replace       replace the content of same-name collections instead of merging
      --dry-run       parse and show what would be imported, write nothing
      --list          list the collections currently in lazer, then exit
      --force         skip the "osu! is running" check
      --yes           no confirmation prompt and no pause on exit
      --help          show this help

    Collections are merged by name (hashes are deduplicated); nothing is ever
    deleted. A timestamped backup of client.realm is created before writing.
    Close osu!lazer before importing; restart it to see the collections.
    """;

var files = new List<string>();
string? realmOverride = null;
bool replace = false, dryRun = false, list = false, force = false, yes = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--help" or "-h" or "/?":
            Console.WriteLine(usage);
            return 0;
        case "--realm":
            if (i + 1 >= args.Length) return Fail("--realm requires a path.");
            realmOverride = args[++i];
            break;
        case "--replace": replace = true; break;
        case "--dry-run": dryRun = true; break;
        case "--list": list = true; break;
        case "--force": force = true; break;
        case "--yes" or "-y": yes = true; break;
        default:
            if (args[i].StartsWith('-')) return Fail($"Unknown option \"{args[i]}\". Use --help.");
            files.Add(args[i]);
            break;
    }
}

if (!list && files.Count == 0)
{
    Console.WriteLine(usage);
    Pause();
    return files.Count == 0 && args.Length == 0 ? 0 : 1;
}

try
{
    string realmPath = LazerRealm.ResolvePath(realmOverride);
    Console.WriteLine($"osu!lazer database: {realmPath}");

    if (list)
    {
        using var listRealm = LazerRealm.Open(realmPath);
        var existing = LazerRealm.List(listRealm);
        Console.WriteLine($"\n{existing.Count} collection(s) in lazer:");
        foreach (var (name, count, modified) in existing)
            Console.WriteLine($"  {name,-40} {count,6} map(s)   last modified {modified:yyyy-MM-dd}");
        Pause();
        return 0;
    }

    // Parse all input files first - nothing is touched if any of them is invalid.
    var collections = new List<RawCollection>();
    Console.WriteLine();
    foreach (string file in files)
    {
        if (!File.Exists(file)) return Fail($"File not found: \"{file}\"");
        var parsed = CollectionFormats.ReadFile(file);
        Console.WriteLine($"  {Path.GetFileName(file)}: {parsed.Count} collection(s), {parsed.Sum(c => c.Hashes.Count)} map entrie(s)");
        foreach (var c in parsed)
            Console.WriteLine($"    - {c.Name} ({c.Hashes.Count})");
        collections.AddRange(parsed);
    }

    if (collections.Count == 0) return Fail("No collections found in the given file(s).");

    if (dryRun)
    {
        Console.WriteLine("\nDry run - nothing was written.");
        Pause();
        return 0;
    }

    if (!force && Process.GetProcessesByName("osu!").Length > 0)
        return Fail("osu! appears to be running. Close it first (or use --force).");

    if (!yes)
    {
        Console.Write($"\nImport {collections.Count} collection(s) into lazer ({(replace ? "replace" : "merge")} mode)? [y/N] ");
        string? answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Aborted.");
            return 1;
        }
    }

    string backup = LazerRealm.Backup(realmPath);
    Console.WriteLine($"Backup created: {Path.GetFileName(backup)}");

    MergeStats stats;
    using (var realm = LazerRealm.Open(realmPath))
        stats = LazerRealm.Merge(realm, collections, replace);

    // stable machine-readable line for callers (e.g. the tracker's server)
    Console.WriteLine($"RESULT created={stats.CollectionsCreated} updated={stats.CollectionsUpdated} hashes={stats.HashesAdded} invalid={stats.InvalidHashesSkipped}");
    Console.WriteLine($"""

        Done.
          collections created : {stats.CollectionsCreated}
          collections updated : {stats.CollectionsUpdated}
          hashes written      : {stats.HashesAdded}
          invalid hashes      : {stats.InvalidHashesSkipped}

        Start osu!lazer to see your collections. Maps you don't have installed
        stay in the collection and appear once you download them.
        """);
    Pause();
    return 0;
}
catch (Exception e) when (e is FormatException or FileNotFoundException or InvalidOperationException
    or IOException or Realms.Exceptions.RealmException)
{
    return Fail(e.Message);
}

int Fail(string message)
{
    Console.Error.WriteLine($"\nError: {message}");
    Pause();
    return 1;
}

void Pause()
{
    if (yes) return;
    try
    {
        Console.Write("\nPress Enter to exit...");
        Console.ReadLine();
    }
    catch (IOException)
    {
        // no interactive console attached
    }
}
