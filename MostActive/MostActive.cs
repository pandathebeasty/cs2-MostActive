using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using MostActiveApi;

namespace MostActive;

/// <summary>
/// MostActive — a time-played rank system for CS2 (CounterStrikeSharp port of Franc1sco's SM plugin).
/// Counts seconds played per team (T / CT / Spectator), stored per SteamID64 in MySQL or SQLite, and
/// shows ranks via chat commands (no in-game menu). Companion web panel reads the same table.
/// </summary>
public sealed partial class MostActive : BasePlugin, IPluginConfig<MostActiveConfig>
{
    public override string ModuleName => "MostActive";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "panda";
    public override string ModuleDescription => "Time-played rank system (T/CT/Spectator).";

    public MostActiveConfig Config { get; set; } = new();

    private Storage? _db;

    // Live per-slot seconds for THIS session so far (CT/T/Spec), on top of the loaded all-time baseline.
    // These are what the API reports (current-session + loaded baseline).
    private readonly int[] _ct = new int[64];
    private readonly int[] _t = new int[64];
    private readonly int[] _spec = new int[64];
    // Pending (not-yet-persisted) seconds since the last flush, added to BOTH the all-time + month row.
    private readonly int[] _pendCt = new int[64];
    private readonly int[] _pendT = new int[64];
    private readonly int[] _pendSpec = new int[64];
    // The month ("YYYY-MM", timezone-adjusted) the pending seconds belong to. On a month rollover we
    // flush the pending seconds into the OLD month, then start accruing into the new one.
    private readonly string[] _period = new string[64];
    private readonly ulong[] _steam = new ulong[64];
    private readonly string[] _name = new string[64];
    private readonly bool[] _loaded = new bool[64];

    private TimeZoneInfo _tz = TimeZoneInfo.Utc;

    /// <summary>Current "YYYY-MM" in the configured timezone (UTC by default).</summary>
    private string CurrentPeriod() =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz).ToString("yyyy-MM");

    /// <summary>Resolves a config timezone id (IANA or Windows). Empty/invalid -> UTC.</summary>
    private TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch
        {
            Logger.LogWarning("[MostActive] Unknown TimeZone '{Id}', falling back to UTC.", id);
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Oldest "YYYY-MM" to keep given KeepMonths (inclusive). Empty if KeepMonths&lt;=0.</summary>
    private string OldestKeptPeriod()
    {
        if (Config.KeepMonths <= 0) return "";
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz);
        // KeepMonths includes the current month, so step back (KeepMonths - 1) months.
        return now.AddMonths(-(Config.KeepMonths - 1)).ToString("yyyy-MM");
    }

    public void OnConfigParsed(MostActiveConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        _tz = ResolveTimeZone(Config.TimeZone);

        _db = new Storage(Logger, Config.Database);
        _db.SetSqliteDir(ModuleDirectory);

        _ = Task.Run(async () =>
        {
            try
            {
                await _db.InitAsync();
                await _db.PruneDaysAsync(Config.PruneDays);
                await _db.PruneMonthsAsync(OldestKeptPeriod(), Config.KeepMonths);
                Logger.LogInformation("[MostActive] Storage ready ({Type}), tz={Tz}.",
                    Config.Database.Type, _tz.Id);
            }
            catch (Exception ex) { Logger.LogError(ex, "[MostActive] Storage init failed."); }
        });

        RegisterListener<Listeners.OnClientPutInServer>(OnClientJoin);
        RegisterListener<Listeners.OnClientDisconnect>(OnDisconnect);
        RegisterEventHandler<EventPlayerDeath>(OnDeath); // cheap periodic persist on deaths

        // 1-second tick: add a second to each player's current-team bucket.
        AddTimer(1.0f, Tick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        RegisterCommands();

        // Expose the public API so other plugins can read live played-time.
        Capabilities.RegisterPluginCapability(IMostActiveApi.Capability, () => new MostActiveApiImpl(this));

        if (hotReload)
            foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }))
                OnClientJoin(p.Slot);
    }

    public override void Unload(bool hotReload)
    {
        for (int s = 0; s < 64; s++)
            if (_loaded[s]) FlushSlot(s);
    }

    // ---- lifecycle ----
    private void OnClientJoin(int slot)
    {
        var p = Utilities.GetPlayerFromSlot(slot);
        if (p is not { IsValid: true, IsBot: false }) return;
        var steam = p.SteamID; var name = p.PlayerName;
        _steam[slot] = steam; _name[slot] = name;
        _ct[slot] = _t[slot] = _spec[slot] = 0;
        _pendCt[slot] = _pendT[slot] = _pendSpec[slot] = 0;
        _period[slot] = CurrentPeriod();
        _loaded[slot] = false;

        if (_db is null) return;
        _ = Task.Run(async () =>
        {
            // Baseline for the live counters + API is the ALL-TIME row (CT+T+Spec since forever).
            var row = await _db.GetAllTimeAsync(steam);
            Server.NextFrame(() =>
            {
                if (_steam[slot] != steam) return; // slot reused
                if (row is not null) { _ct[slot] = row.TimeCt; _t[slot] = row.TimeT; _spec[slot] = row.TimeSpec; }
                _loaded[slot] = true;
            });
        });
    }

    private void OnDisconnect(int slot)
    {
        if (_loaded[slot]) FlushSlot(slot);
        _loaded[slot] = false; _steam[slot] = 0;
    }

    private HookResult OnDeath(EventPlayerDeath e, GameEventInfo info)
    {
        var v = e.Userid;
        if (v is { IsValid: true, IsBot: false } && _loaded[v.Slot]) FlushSlot(v.Slot);
        return HookResult.Continue;
    }

    private void Tick()
    {
        var nowPeriod = CurrentPeriod();
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, IsBot: false }) continue;
            var s = p.Slot;
            if (!_loaded[s]) continue;

            // Month rollover while connected: persist whatever is pending into the OLD month first,
            // then switch the pending bucket to the new month. This keeps seconds from bleeding across
            // the month boundary (precise split).
            if (_period[s] != nowPeriod)
            {
                FlushSlot(s);              // writes pending into _period[s] (the old month)
                _period[s] = nowPeriod;    // subsequent seconds accrue into the new month
            }

            switch ((int)p.Team)
            {
                case 2: _t[s]++; _pendT[s]++; break;      // Terrorist
                case 3: _ct[s]++; _pendCt[s]++; break;    // CounterTerrorist
                default: if (Config.TrackSpectator) { _spec[s]++; _pendSpec[s]++; } break;
            }
        }
    }

    /// <summary>
    /// Persists a slot's PENDING seconds (since the last flush) into both the all-time row and the
    /// current month's bucket (<see cref="_period"/>), then clears the pending counters (off-thread).
    /// </summary>
    private void FlushSlot(int slot)
    {
        if (_db is null || _steam[slot] == 0) return;
        int dCt = _pendCt[slot], dT = _pendT[slot], dSpec = _pendSpec[slot];
        if (dCt == 0 && dT == 0 && dSpec == 0) return;
        _pendCt[slot] = _pendT[slot] = _pendSpec[slot] = 0; // clear now; we add deltas, not absolutes

        var steam = _steam[slot];
        var name = _name[slot] ?? "";
        var period = _period[slot];
        var ls = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _ = Task.Run(async () =>
        {
            try { await _db!.AddAsync(steam, name, period, dCt, dT, dSpec, ls); }
            catch (Exception ex) { Logger.LogWarning(ex, "[MostActive] Save failed for {Steam}", steam); }
        });
    }
}
