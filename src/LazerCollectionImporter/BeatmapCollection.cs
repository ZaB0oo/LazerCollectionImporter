// Mirror of osu!lazer's realm model:
// https://github.com/ppy/osu/blob/master/osu.Game/Collections/BeatmapCollection.cs
// The table shape is unchanged since realm schema v21 (2022-07-27).
// Property names and the class name must match the realm schema exactly.
#nullable disable

using Realms;

namespace LazerCollectionImporter;

public partial class BeatmapCollection : IRealmObject
{
    [PrimaryKey]
    public Guid ID { get; set; }

    public string Name { get; set; } = string.Empty;

    public IList<string> BeatmapMD5Hashes { get; }

    public DateTimeOffset LastModified { get; set; }
}
