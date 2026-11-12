using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace MostActive;

/// <summary>
/// Config (configs/plugins/MostActive/MostActive.json). A commented reference ships as
/// MostActive.example.jsonc (the live .json is rewritten by CSSharp, which strips comments).
/// </summary>
public sealed class MostActiveConfig : BasePluginConfig
{
    [JsonPropertyName("Database")]
    public DatabaseConfig Database { get; set; } = new();

    /// <summary>Chat commands (aliases without the trigger). Empty list = disabled.</summary>
    [JsonPropertyName("TimeCommand")]
    public List<string> TimeCommand { get; set; } = ["time", "active"];

    [JsonPropertyName("TopCommand")]
    public List<string> TopCommand { get; set; } = ["topactive"];

    [JsonPropertyName("WastedCommand")]
    public List<string> WastedCommand { get; set; } = ["wasted"];

    /// <summary>
    /// How time is printed. Default "HH:mm:ss" (zero-padded). Supported tokens: HH/mm/ss (padded),
    /// H/m/s (no pad), D (days). Anything else shows literally. Examples: "HH:mm:ss", "H h m m s s".
    /// </summary>
    [JsonPropertyName("TimeFormat")]
    public string TimeFormat { get; set; } = "HH:mm:ss";

    /// <summary>How many rows the !top command prints.</summary>
    [JsonPropertyName("TopCount")]
    public int TopCount { get; set; } = 10;

    /// <summary>Delete players not seen for this many days (all-time table). 0 = never prune.</summary>
    [JsonPropertyName("PruneDays")]
    public int PruneDays { get; set; } = 30;

    /// <summary>
    /// How many monthly buckets to keep in the periods table. Older months are deleted (the all-time
    /// table is never pruned by month). Default 12 (a rolling year). 0 = keep all months.
    /// </summary>
    [JsonPropertyName("KeepMonths")]
    public int KeepMonths { get; set; } = 12;

    /// <summary>
    /// IANA/Windows timezone used to decide which month a second belongs to (and shown by the web).
    /// Empty = UTC. Examples: "Europe/Bucharest", "UTC". Keep the web panel's timezone in sync.
    /// </summary>
    [JsonPropertyName("TimeZone")]
    public string TimeZone { get; set; } = "";

    /// <summary>Count spectator time into the stored spectator bucket.</summary>
    [JsonPropertyName("TrackSpectator")]
    public bool TrackSpectator { get; set; } = true;

    /// <summary>Chat prefix shown before plugin messages (supports color tags like {green}).</summary>
    [JsonPropertyName("ChatPrefix")]
    public string ChatPrefix { get; set; } = "{green}[MostActive]{default} ";
}

/// <summary>Database selection. Same schema on both; web panel reads MySQL.</summary>
public sealed class DatabaseConfig
{
    /// <summary>"sqlite" or "mysql".</summary>
    [JsonPropertyName("Type")]
    public string Type { get; set; } = "sqlite";

    [JsonPropertyName("TablePrefix")]
    public string TablePrefix { get; set; } = "ma_";

    [JsonPropertyName("SqliteFile")]
    public string SqliteFile { get; set; } = "mostactive.db";

    [JsonPropertyName("MySqlHost")]
    public string MySqlHost { get; set; } = "127.0.0.1";

    [JsonPropertyName("MySqlPort")]
    public uint MySqlPort { get; set; } = 3306;

    [JsonPropertyName("MySqlDatabase")]
    public string MySqlDatabase { get; set; } = "mostactive";

    [JsonPropertyName("MySqlUser")]
    public string MySqlUser { get; set; } = "root";

    [JsonPropertyName("MySqlPassword")]
    public string MySqlPassword { get; set; } = "";
}
