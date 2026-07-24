# LazerCollectionImporter

Imports osu! collections (`.osdb` or `collection.db`) **directly into osu!lazer**, in seconds. No full client database loading, no heavy UI, and it never deletes your existing collections.

## Usage

**Drag & drop one or more `.osdb` / `collection.db` files onto `LazerCollectionImporter.exe`.** That's it: the tool finds your lazer database, backs it up, merges the collections and prints a summary. Restart osu!lazer to see them.

Command line:

```
LazerCollectionImporter <files...> [options]

--realm <path>  client.realm file or lazer data folder (default: auto-detect,
                including a custom folder configured in storage.ini)
--ids <path>    JSON file mapping md5 -> online beatmap id; hashes that match
                no installed map are remapped to the hash of the installed
                version of the same beatmap (see below)
--replace       replace the content of same-name collections (default: merge)
--dry-run       show what would be imported, write nothing
--list          list the collections currently in lazer
--force         skip the "osu! is running" check
--yes           no confirmation prompt and no pause on exit
```

Accepted formats: legacy `collection.db` (osu!stable and every tool that exports it) and `.osdb` collection files, **all versions** (`o!dm` through `o!dm8min`, gzip included).

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
publish.cmd
```

(or `dotnet publish src\LazerCollectionImporter -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish`)

→ `publish\LazerCollectionImporter.exe`, a single executable, nothing to install to run it.

Tests: `dotnet test`

## Why this tool

osu!lazer **silently ignores drag & drop of collection files**: only `.osz`/`.olz`/`.osr`/`.osk` have importers ([OsuGameBase.Importing.cs](https://github.com/ppy/osu/blob/master/osu.Game/OsuGameBase.Importing.cs)). The only native path is "Import from a previous osu! install", which requires a real stable installation folder.

## How it works (and why it's safe)

lazer stores collections in `client.realm` (a [Realm](https://github.com/realm/realm-dotnet) database), in the `BeatmapCollection` table (Guid, name, list of `.osu` MD5 hashes, modification date) — an isolated table with no relationships, unchanged since 2022.

1. **Automatic backup**: `client.realm.backup-<timestamp>` before any write.
2. **Schema version detection**: the database is probed with version 0 (which always fails *before* modifying anything), the file's real version is extracted from the error, then the file is reopened with **exactly** that version. Never an upward migration — that is what would make lazer set your database aside (`client_newer_version.realm`).
3. **Partial schema**: only the `BeatmapCollection` table is declared; Realm leaves every other table untouched (beatmaps, scores, skins…).
4. **Merge by name**, the same logic as lazer's own `LegacyCollectionImporter`: hashes are validated (32 hex chars) and deduplicated, and nothing is ever deleted unless you pass `--replace`.
5. **Same Realm package version as the game** (20.1.0, see [osu.Game.csproj](https://github.com/ppy/osu/blob/master/osu.Game/osu.Game.csproj)): the realm-core file format can never be silently upgraded.

Close osu!lazer before importing (the tool checks). Maps you don't have installed stay in the collection and appear once you download them.

### Outdated local maps (`--ids`)

Collections reference maps by the MD5 of their `.osu` file — so a map you downloaded before the mapper last updated it has a **different hash** than the current online version, and a collection built from online hashes won't show it. With `--ids` (a JSON object `{"<md5>": <beatmap id>, ...}`), the importer reads the installed beatmaps from `client.realm` (read-only) and substitutes any unmatched hash with the hash of the **installed version** of the same beatmap. The summary reports how many hashes were remapped and how many maps are simply not installed.

## Limitations

- If a future osu! update changes the `BeatmapCollection` table or the Realm package version, the tool will cleanly refuse to write (explicit message) instead of risking your database — it will then need a model/package update and a rebuild.
- Data folder auto-detection is Windows-only; on Linux/macOS pass `--realm <path>`.
