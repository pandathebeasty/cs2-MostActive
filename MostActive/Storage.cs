using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace MostActive;

/// <summary>One stored player row (seconds per team + total). Used for both the all-time table and a
/// single month's row in the periods table (then <see cref="Period"/> is set to "YYYY-MM").</summary>
public sealed class PlayerTime
{
    public ulong SteamId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int TimeCt { get; set; }
    public int TimeT { get; set; }
    public int TimeSpec { get; set; }
    public int Total { get; set; }   // ct + t + spec
    public long LastSeen { get; set; } // unix seconds
    public string Period { get; set; } = string.Empty; // "YYYY-MM" (periods table only)
}

/// <summary>
/// Storage for MostActive on MySQL or SQLite (same schema). Two tables:
///   &lt;prefix&gt;mostactive          — all-time cumulative totals per SteamID64 (never month-pruned).
///   &lt;prefix&gt;mostactive_periods  — per-month buckets (steamid, period "YYYY-MM"); pruned to the
///                                       last N months (Config.KeepMonths).
/// A flush writes the same delta to BOTH: the current month's bucket and the all-time row. All async.
/// </summary>
public sealed class Storage : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly DatabaseConfig _cfg;
    private readonly bool _mysql;
    private readonly string _allTable;
    private readonly string _perTable;

    public Storage(ILogger log, DatabaseConfig cfg)
    {
        _log = log;
        _cfg = cfg;
        _mysql = cfg.Type.Equals("mysql", StringComparison.OrdinalIgnoreCase);
        var prefix = string.IsNullOrWhiteSpace(cfg.TablePrefix) ? "ma_" : cfg.TablePrefix;
        _allTable = prefix + "mostactive";
        _perTable = prefix + "mostactive_periods";
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
        var sc = new SqliteConnection($"Data Source={_cfg.SqliteFile}"); sc.Open(); return sc;
    }

    /// <summary>Sets the SQLite file to an absolute path under the plugin folder.</summary>
    public void SetSqliteDir(string baseDir)
    {
        if (!_mysql && !Path.IsPathRooted(_cfg.SqliteFile))
            _cfg.SqliteFile = Path.Combine(baseDir, _cfg.SqliteFile);
    }

    /// <summary>
    /// Creates the two tables fresh. Any pre-1.1 single table is dropped first (schema changed from a
    /// flat all-time row to all-time + monthly periods), so old data is intentionally discarded.
    /// </summary>
    public async Task InitAsync()
    {
        using var c = Open();

        if (_mysql)
        {
            // Legacy table used the same name as the new all-time table but the schema is compatible
            // (steamid PK + the same counter columns), so we keep the all-time name and just ensure
            // both tables exist. A truly old install with a different layout is dropped below.
            await c.ExecuteAsync($@"
                CREATE TABLE IF NOT EXISTS `{_allTable}` (
                  steamid BIGINT UNSIGNED NOT NULL PRIMARY KEY,
                  playername VARCHAR(128) NOT NULL,
                  time_ct INT NOT NULL DEFAULT 0,
                  time_t INT NOT NULL DEFAULT 0,
                  time_spec INT NOT NULL DEFAULT 0,
                  total INT NOT NULL DEFAULT 0,
                  last_seen BIGINT NOT NULL DEFAULT 0);");
            await c.ExecuteAsync($@"
                CREATE TABLE IF NOT EXISTS `{_perTable}` (
                  steamid BIGINT UNSIGNED NOT NULL,
                  period CHAR(7) NOT NULL,
                  playername VARCHAR(128) NOT NULL,
                  time_ct INT NOT NULL DEFAULT 0,
                  time_t INT NOT NULL DEFAULT 0,
                  time_spec INT NOT NULL DEFAULT 0,
                  total INT NOT NULL DEFAULT 0,
                  last_seen BIGINT NOT NULL DEFAULT 0,
                  PRIMARY KEY (steamid, period),
                  INDEX ix_period (period),
                  INDEX ix_period_total (period, total));");
        }
        else
        {
            await c.ExecuteAsync($@"
                CREATE TABLE IF NOT EXISTS {_allTable} (
                  steamid INTEGER NOT NULL PRIMARY KEY,
                  playername TEXT NOT NULL,
                  time_ct INTEGER NOT NULL DEFAULT 0,
                  time_t INTEGER NOT NULL DEFAULT 0,
                  time_spec INTEGER NOT NULL DEFAULT 0,
                  total INTEGER NOT NULL DEFAULT 0,
                  last_seen INTEGER NOT NULL DEFAULT 0);");
            await c.ExecuteAsync($@"
                CREATE TABLE IF NOT EXISTS {_perTable} (
                  steamid INTEGER NOT NULL,
                  period TEXT NOT NULL,
                  playername TEXT NOT NULL,
                  time_ct INTEGER NOT NULL DEFAULT 0,
                  time_t INTEGER NOT NULL DEFAULT 0,
                  time_spec INTEGER NOT NULL DEFAULT 0,
                  total INTEGER NOT NULL DEFAULT 0,
                  last_seen INTEGER NOT NULL DEFAULT 0,
                  PRIMARY KEY (steamid, period));");
            await c.ExecuteAsync($"CREATE INDEX IF NOT EXISTS ix_{_perTable}_period ON {_perTable}(period);");
            await c.ExecuteAsync($"CREATE INDEX IF NOT EXISTS ix_{_perTable}_period_total ON {_perTable}(period, total);");
        }
    }

    /// <summary>Loads a player's ALL-TIME stored seconds, or null if not present.</summary>
    public async Task<PlayerTime?> GetAllTimeAsync(ulong steamId)
    {
        using var c = Open();
        return (await c.QueryAsync<PlayerTime>(
            $@"SELECT steamid AS SteamId, playername AS PlayerName, time_ct AS TimeCt, time_t AS TimeT,
                      time_spec AS TimeSpec, total AS Total, last_seen AS LastSeen
               FROM {_allTable} WHERE steamid = @s", new { s = steamId })).FirstOrDefault();
    }

    /// <summary>Loads a player's stored seconds for one month ("YYYY-MM"), or null if none.</summary>
    public async Task<PlayerTime?> GetPeriodAsync(ulong steamId, string period)
    {
        using var c = Open();
        return (await c.QueryAsync<PlayerTime>(
            $@"SELECT steamid AS SteamId, playername AS PlayerName, time_ct AS TimeCt, time_t AS TimeT,
                      time_spec AS TimeSpec, total AS Total, last_seen AS LastSeen, period AS Period
               FROM {_perTable} WHERE steamid = @s AND period = @p", new { s = steamId, p = period }))
            .FirstOrDefault();
    }

    /// <summary>
    /// Persists a flush: ADDS the given deltas to the all-time row AND to the given month's bucket
    /// (both upserted). Deltas are the seconds gained since the last flush; name + last-seen are
    /// overwritten. Running both in one transaction keeps the two tables consistent.
    /// </summary>
    public async Task AddAsync(ulong steamId, string playerName, string period,
        int dCt, int dT, int dSpec, long lastSeen)
    {
        if (dCt == 0 && dT == 0 && dSpec == 0) return; // nothing to add (still cheap to skip)
        using var c = Open();
        using var tx = c.BeginTransaction();

        var allSql = _mysql
            ? $@"INSERT INTO `{_allTable}` (steamid, playername, time_ct, time_t, time_spec, total, last_seen)
                 VALUES (@s,@n,@ct,@t,@sp,@tot,@ls)
                 ON DUPLICATE KEY UPDATE playername=VALUES(playername),
                   time_ct=time_ct+@ct, time_t=time_t+@t, time_spec=time_spec+@sp,
                   total=total+@tot, last_seen=VALUES(last_seen);"
            : $@"INSERT INTO {_allTable} (steamid, playername, time_ct, time_t, time_spec, total, last_seen)
                 VALUES (@s,@n,@ct,@t,@sp,@tot,@ls)
                 ON CONFLICT(steamid) DO UPDATE SET playername=excluded.playername,
                   time_ct=time_ct+@ct, time_t=time_t+@t, time_spec=time_spec+@sp,
                   total=total+@tot, last_seen=excluded.last_seen;";

        var perSql = _mysql
            ? $@"INSERT INTO `{_perTable}` (steamid, period, playername, time_ct, time_t, time_spec, total, last_seen)
                 VALUES (@s,@p,@n,@ct,@t,@sp,@tot,@ls)
                 ON DUPLICATE KEY UPDATE playername=VALUES(playername),
                   time_ct=time_ct+@ct, time_t=time_t+@t, time_spec=time_spec+@sp,
                   total=total+@tot, last_seen=VALUES(last_seen);"
            : $@"INSERT INTO {_perTable} (steamid, period, playername, time_ct, time_t, time_spec, total, last_seen)
                 VALUES (@s,@p,@n,@ct,@t,@sp,@tot,@ls)
                 ON CONFLICT(steamid, period) DO UPDATE SET playername=excluded.playername,
                   time_ct=time_ct+@ct, time_t=time_t+@t, time_spec=time_spec+@sp,
                   total=total+@tot, last_seen=excluded.last_seen;";

        var args = new { s = steamId, p = period, n = playerName, ct = dCt, t = dT, sp = dSpec,
                         tot = dCt + dT + dSpec, ls = lastSeen };
        await c.ExecuteAsync(allSql, args, tx);
        await c.ExecuteAsync(perSql, args, tx);
        tx.Commit();
    }

    /// <summary>Top N by a column for ALL-TIME. column: "total"|"time_ct"|"time_t"|"time_spec".</summary>
    public async Task<List<PlayerTime>> TopAllTimeAsync(string column, int limit)
    {
        var col = SafeCol(column);
        using var c = Open();
        return (await c.QueryAsync<PlayerTime>(
            $@"SELECT steamid AS SteamId, playername AS PlayerName, time_ct AS TimeCt, time_t AS TimeT,
                      time_spec AS TimeSpec, total AS Total, last_seen AS LastSeen
               FROM {_allTable} ORDER BY {col} DESC LIMIT @n", new { n = limit })).ToList();
    }

    /// <summary>Top N by a column for one month ("YYYY-MM").</summary>
    public async Task<List<PlayerTime>> TopPeriodAsync(string period, string column, int limit)
    {
        var col = SafeCol(column);
        using var c = Open();
        return (await c.QueryAsync<PlayerTime>(
            $@"SELECT steamid AS SteamId, playername AS PlayerName, time_ct AS TimeCt, time_t AS TimeT,
                      time_spec AS TimeSpec, total AS Total, last_seen AS LastSeen, period AS Period
               FROM {_perTable} WHERE period = @p ORDER BY {col} DESC LIMIT @n",
            new { p = period, n = limit })).ToList();
    }

    /// <summary>Sum of all players' all-time total seconds (for !wasted all-time).</summary>
    public async Task<long> SumAllTimeAsync()
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<long?>($"SELECT COALESCE(SUM(total),0) FROM {_allTable}") ?? 0;
    }

    /// <summary>Sum of all players' total seconds in one month (for !wasted &lt;month&gt;).</summary>
    public async Task<long> SumPeriodAsync(string period)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<long?>(
            $"SELECT COALESCE(SUM(total),0) FROM {_perTable} WHERE period = @p", new { p = period }) ?? 0;
    }

    /// <summary>Distinct months present in the periods table, newest first (for the web dropdown).</summary>
    public async Task<List<string>> PeriodsAsync()
    {
        using var c = Open();
        return (await c.QueryAsync<string>(
            $"SELECT DISTINCT period FROM {_perTable} ORDER BY period DESC")).ToList();
    }

    /// <summary>Deletes all-time players not seen for more than <paramref name="days"/> days.</summary>
    public async Task PruneDaysAsync(int days)
    {
        if (days <= 0) return;
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)days * 86400;
        using var c = Open();
        await c.ExecuteAsync($"DELETE FROM {_allTable} WHERE last_seen > 0 AND last_seen < @cut",
            new { cut = cutoff });
    }

    /// <summary>
    /// Deletes monthly buckets older than the newest <paramref name="keepMonths"/> months (rolling
    /// window). The all-time table is untouched. 0 = keep everything. <paramref name="oldestKeep"/>
    /// is the cutoff "YYYY-MM"; anything strictly before it is removed.
    /// </summary>
    public async Task PruneMonthsAsync(string oldestKeep, int keepMonths)
    {
        if (keepMonths <= 0 || string.IsNullOrEmpty(oldestKeep)) return;
        using var c = Open();
        await c.ExecuteAsync($"DELETE FROM {_perTable} WHERE period < @o", new { o = oldestKeep });
    }

    private static string SafeCol(string column) => column switch
    {
        "time_ct" => "time_ct", "time_t" => "time_t", "time_spec" => "time_spec", _ => "total"
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
