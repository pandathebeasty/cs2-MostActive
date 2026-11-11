using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace MostActive;

/// <summary>One stored player row (seconds per team + total).</summary>
public sealed class PlayerTime
{
    public ulong SteamId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int TimeCt { get; set; }
    public int TimeT { get; set; }
    public int TimeSpec { get; set; }
    public int Total { get; set; }   // ct + t + spec
    public long LastSeen { get; set; } // unix seconds
}

/// <summary>
/// Minimal storage for MostActive on MySQL or SQLite (same schema). Table: &lt;prefix&gt;mostactive with
/// SteamID64 as the key and seconds-per-team counters. All async / off the game thread.
/// </summary>
public sealed class Storage : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly DatabaseConfig _cfg;
    private readonly bool _mysql;
    private readonly string _table;

    public Storage(ILogger log, DatabaseConfig cfg)
    {
        _log = log;
        _cfg = cfg;
        _mysql = cfg.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase);
        var prefix = string.IsNullOrWhiteSpace(cfg.TablePrefix) ? "ma_" : cfg.TablePrefix;
        _table = prefix + "mostactive";
    }

    private IDbConnection Open()
    {
        if (_mysql)
        {
            var cs = new MySqlConnectionStringBuilder
            {
                Server = _cfg.MySqlHost, Port = _cfg.MySqlPort, Database = _cfg.MySqlDatabase,
                UserID = _cfg.MySqlUser, Password = _cfg.MySqlPassword, Pooling = true
            }.ConnectionString;
            var c = new MySqlConnection(cs); c.Open(); return c;
        }
        var file = Path.IsPathRooted(_cfg.SqliteFile) ? _cfg.SqliteFile : _cfg.SqliteFile;
        var sc = new SqliteConnection($"Data Source={file}"); sc.Open(); return sc;
    }

    /// <summary>Sets the SQLite file to an absolute path under the plugin folder.</summary>
    public void SetSqliteDir(string baseDir)
    {
        if (!_mysql && !Path.IsPathRooted(_cfg.SqliteFile))
            _cfg.SqliteFile = Path.Combine(baseDir, _cfg.SqliteFile);
    }

    public async Task InitAsync()
    {
        using var c = Open();
        var sql = _mysql
            ? $@"CREATE TABLE IF NOT EXISTS `{_table}` (
                   steamid BIGINT UNSIGNED NOT NULL PRIMARY KEY,
                   playername VARCHAR(128) NOT NULL,
                   time_ct INT NOT NULL DEFAULT 0,
                   time_t INT NOT NULL DEFAULT 0,
                   time_spec INT NOT NULL DEFAULT 0,
                   total INT NOT NULL DEFAULT 0,
                   last_seen BIGINT NOT NULL DEFAULT 0);"
            : $@"CREATE TABLE IF NOT EXISTS {_table} (
                   steamid INTEGER NOT NULL PRIMARY KEY,
                   playername TEXT NOT NULL,
                   time_ct INTEGER NOT NULL DEFAULT 0,
                   time_t INTEGER NOT NULL DEFAULT 0,
                   time_spec INTEGER NOT NULL DEFAULT 0,
                   total INTEGER NOT NULL DEFAULT 0,
                   last_seen INTEGER NOT NULL DEFAULT 0);";
        await c.ExecuteAsync(sql);
    }

    /// <summary>Loads a player's stored seconds, or null if not present.</summary>
    public async Task<PlayerTime?> GetAsync(ulong steamId)
    {
        using var c = Open();
        return (await c.QueryAsync<PlayerTime>(
            $@"SELECT steamid AS SteamId, playername AS PlayerName, time_ct AS TimeCt, time_t AS TimeT,
                      time_spec AS TimeSpec, total AS Total, last_seen AS LastSeen
               FROM {_table} WHERE steamid = @s", new { s = steamId })).FirstOrDefault();
    }

    /// <summary>Upserts a player's absolute seconds + name + last-seen.</summary>
    public async Task SaveAsync(PlayerTime p)
    {
        using var c = Open();
        p.Total = p.TimeCt + p.TimeT + p.TimeSpec;
        var sql = _mysql
            ? $@"INSERT INTO `{_table}` (steamid, playername, time_ct, time_t, time_spec, total, last_seen)
                 VALUES (@SteamId, @PlayerName, @TimeCt, @TimeT, @TimeSpec, @Total, @LastSeen)
                 ON DUPLICATE KEY UPDATE playername=VALUES(playername), time_ct=VALUES(time_ct),
                   time_t=VALUES(time_t), time_spec=VALUES(time_spec), total=VALUES(total),
                   last_seen=VALUES(last_seen);"
            : $@"INSERT INTO {_table} (steamid, playername, time_ct, time_t, time_spec, total, last_seen)
                 VALUES (@SteamId, @PlayerName, @TimeCt, @TimeT, @TimeSpec, @Total, @LastSeen)
                 ON CONFLICT(steamid) DO UPDATE SET playername=excluded.playername, time_ct=excluded.time_ct,
                   time_t=excluded.time_t, time_spec=excluded.time_spec, total=excluded.total,
                   last_seen=excluded.last_seen;";
        await c.ExecuteAsync(sql, p);
    }

    /// <summary>Top N by a column: "total" | "time_ct" | "time_t" | "time_spec".</summary>
    public async Task<List<PlayerTime>> TopAsync(string column, int limit)
    {
        var col = column switch { "time_ct" => "time_ct", "time_t" => "time_t", "time_spec" => "time_spec", _ => "total" };
        using var c = Open();
        return (await c.QueryAsync<PlayerTime>(
            $@"SELECT steamid AS SteamId, playername AS PlayerName, time_ct AS TimeCt, time_t AS TimeT,
                      time_spec AS TimeSpec, total AS Total, last_seen AS LastSeen
               FROM {_table} ORDER BY {col} DESC LIMIT @n", new { n = limit })).ToList();
    }

    /// <summary>Sum of all players' total seconds (for !wasted).</summary>
    public async Task<long> SumTotalAsync()
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<long?>($"SELECT COALESCE(SUM(total),0) FROM {_table}") ?? 0;
    }

    /// <summary>Deletes players not seen for more than <paramref name="days"/> days.</summary>
    public async Task PruneAsync(int days)
    {
        if (days <= 0) return;
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)days * 86400;
        using var c = Open();
        await c.ExecuteAsync($"DELETE FROM {_table} WHERE last_seen > 0 AND last_seen < @cut", new { cut = cutoff });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
