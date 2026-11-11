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
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "panda";
    public override string ModuleDescription => "Time-played rank system (T/CT/Spectator).";

    public MostActiveConfig Config { get; set; } = new();

    private Storage? _db;

    // Live per-slot seconds for THIS session (added on top of the stored baseline), + the loaded row.
    private readonly int[] _ct = new int[64];
    private readonly int[] _t = new int[64];
    private readonly int[] _spec = new int[64];
    private readonly ulong[] _steam = new ulong[64];
    private readonly string[] _name = new string[64];
    private readonly bool[] _loaded = new bool[64];

    public void OnConfigParsed(MostActiveConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        _db = new Storage(Logger, Config.Database);
        _db.SetSqliteDir(ModuleDirectory);

        _ = Task.Run(async () =>
        {
            try
            {
                await _db.InitAsync();
                await _db.PruneAsync(Config.PruneDays);
                Logger.LogInformation("[MostActive] Storage ready ({Type}).", Config.Database.Type);
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
        _ct[slot] = _t[slot] = _spec[slot] = 0; _loaded[slot] = false;

        if (_db is null) return;
        _ = Task.Run(async () =>
        {
            var row = await _db.GetAsync(steam);
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
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, IsBot: false }) continue;
            var s = p.Slot;
            if (!_loaded[s]) continue;
            switch ((int)p.Team)
            {
                case 2: _t[s]++; break;      // Terrorist
                case 3: _ct[s]++; break;     // CounterTerrorist
                default: if (Config.TrackSpectator) _spec[s]++; break;
            }
        }
    }

    /// <summary>Persists a slot's absolute seconds to the DB (off-thread).</summary>
    private void FlushSlot(int slot)
    {
        if (_db is null || _steam[slot] == 0) return;
        var row = new PlayerTime
        {
            SteamId = _steam[slot], PlayerName = _name[slot] ?? "",
            TimeCt = _ct[slot], TimeT = _t[slot], TimeSpec = _spec[slot],
            LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        _ = Task.Run(async () =>
        {
            try { await _db.SaveAsync(row); }
            catch (Exception ex) { Logger.LogWarning(ex, "[MostActive] Save failed for {Steam}", row.SteamId); }
        });
    }
}
