// Mirror of osu!lazer's realm models (ppy/osu, MIT), trimmed to the persisted
// properties only — the schema must match the tables exactly, helper members
// are irrelevant. Used READ-ONLY, to match collection hashes against the maps
// actually installed (remap-by-online-id feature).
#nullable disable

using Realms;

namespace LazerCollectionImporter;

[MapTo("Beatmap")]
public partial class BeatmapInfo : IRealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; }

    public string DifficultyName { get; set; } = string.Empty;

    public RulesetInfo Ruleset { get; set; }

    public BeatmapDifficulty Difficulty { get; set; }

    public BeatmapMetadata Metadata { get; set; }

    public BeatmapUserSettings UserSettings { get; set; }

    public BeatmapSetInfo BeatmapSet { get; set; }

    [MapTo("Status")]
    public int StatusInt { get; set; }

    [Indexed]
    public int OnlineID { get; set; } = -1;

    public double Length { get; set; }

    public double BPM { get; set; }

    public string Hash { get; set; } = string.Empty;

    public double StarRating { get; set; } = -1;

    [Indexed]
    public string MD5Hash { get; set; } = string.Empty;

    public string OnlineMD5Hash { get; set; } = string.Empty;

    public DateTimeOffset? LastLocalUpdate { get; set; }

    public DateTimeOffset? LastOnlineUpdate { get; set; }

    public bool Hidden { get; set; }

    public int EndTimeObjectCount { get; set; } = -1;

    public int TotalObjectCount { get; set; } = -1;

    public DateTimeOffset? LastPlayed { get; set; }

    public int BeatDivisor { get; set; } = 4;

    public double? EditorTimestamp { get; set; }
}
