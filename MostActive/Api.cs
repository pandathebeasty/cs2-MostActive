using CounterStrikeSharp.API.Core;
using MostActiveApi;

namespace MostActive;

public sealed partial class MostActive
{
    /// <summary>Live played-time accessors for a valid, loaded player slot. -1 when not loaded.</summary>
    internal int ApiCt(CCSPlayerController p) => Loaded(p) ? _ct[p.Slot] : -1;
    internal int ApiT(CCSPlayerController p) => Loaded(p) ? _t[p.Slot] : -1;
    internal int ApiSpec(CCSPlayerController p) => Loaded(p) ? _spec[p.Slot] : -1;
    internal int ApiTotal(CCSPlayerController p) => Loaded(p) ? _ct[p.Slot] + _t[p.Slot] + _spec[p.Slot] : -1;
    internal int ApiTotalNoSpec(CCSPlayerController p) => Loaded(p) ? _ct[p.Slot] + _t[p.Slot] : -1;
    internal bool Loaded(CCSPlayerController p) => p is { IsValid: true } && p.Slot is >= 0 and < 64 && _loaded[p.Slot];
}

/// <summary>Capability implementation handed to other plugins via PluginCapability.</summary>
public sealed class MostActiveApiImpl(MostActive plugin) : IMostActiveApi
{
    public int GetPlayTimeCt(CCSPlayerController player) => plugin.ApiCt(player);
    public int GetPlayTimeT(CCSPlayerController player) => plugin.ApiT(player);
    public int GetPlayTimeSpec(CCSPlayerController player) => plugin.ApiSpec(player);
    public int GetPlayTimeTotal(CCSPlayerController player) => plugin.ApiTotal(player);
    public int GetPlayTimeTotalExceptSpec(CCSPlayerController player) => plugin.ApiTotalNoSpec(player);
    public bool IsLoaded(CCSPlayerController player) => plugin.Loaded(player);
}
