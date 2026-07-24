// Mirror of osu!lazer's realm models (ppy/osu, MIT), persisted properties only.
#nullable disable

using Realms;

namespace LazerCollectionImporter;

public partial class BeatmapDifficulty : IEmbeddedObject
{
    public float DrainRate { get; set; }
    public float CircleSize { get; set; }
    public float OverallDifficulty { get; set; }
    public float ApproachRate { get; set; }
    public double SliderMultiplier { get; set; } = 1.4;
    public double SliderTickRate { get; set; } = 1;
}

public partial class BeatmapUserSettings : IEmbeddedObject
{
    public double Offset { get; set; }
}

public partial class RealmUser : IEmbeddedObject
{
    public int OnlineID { get; set; } = 1;

    public string Username { get; set; } = string.Empty;

    [MapTo("CountryCode")]
    public string CountryString { get; set; } = "N/A";
}

public partial class RealmNamedFileUsage : IEmbeddedObject
{
    public RealmFile File { get; set; }

    public string Filename { get; set; }
}

[MapTo("File")]
public partial class RealmFile : IRealmObject
{
    [PrimaryKey]
    public string Hash { get; set; } = string.Empty;
}

[MapTo("Ruleset")]
public partial class RulesetInfo : IRealmObject
{
    [PrimaryKey]
    public string ShortName { get; set; } = string.Empty;

    [Indexed]
    public int OnlineID { get; set; } = -1;

    public string Name { get; set; } = string.Empty;

    public string InstantiationInfo { get; set; } = string.Empty;

    public int LastAppliedDifficultyVersion { get; set; }

    public bool Available { get; set; }
}
