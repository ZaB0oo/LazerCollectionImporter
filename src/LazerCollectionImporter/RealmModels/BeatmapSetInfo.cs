// Mirror of osu!lazer's realm model (ppy/osu, MIT), persisted properties only.
#nullable disable

using Realms;

namespace LazerCollectionImporter;

[MapTo("BeatmapSet")]
public partial class BeatmapSetInfo : IRealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; }

    [Indexed]
    public int OnlineID { get; set; } = -1;

    public DateTimeOffset DateAdded { get; set; }

    public DateTimeOffset? DateSubmitted { get; set; }

    public DateTimeOffset? DateRanked { get; set; }

    public IList<BeatmapInfo> Beatmaps { get; }

    public IList<RealmNamedFileUsage> Files { get; }

    [MapTo("Status")]
    public int StatusInt { get; set; }

    public bool DeletePending { get; set; }

    public string Hash { get; set; } = string.Empty;

    public bool Protected { get; set; }
}
