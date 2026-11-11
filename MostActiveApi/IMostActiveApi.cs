using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;

namespace MostActiveApi;

/// <summary>
/// Public API for MostActive, exposed as a CSSharp plugin capability. Other plugins grab it with
/// <c>IMostActiveApi.Capability.Get()</c> and read a player's live played-time (current session +
/// loaded baseline, in seconds). Returns -1 when the player's data isn't loaded yet / unknown.
/// </summary>
public interface IMostActiveApi
{
    /// <summary>Capability name other plugins use to resolve the API.</summary>
    static readonly PluginCapability<IMostActiveApi> Capability = new("mostactive:api");

    /// <summary>Seconds played as Counter-Terrorist (live). -1 if not loaded.</summary>
    int GetPlayTimeCt(CCSPlayerController player);

    /// <summary>Seconds played as Terrorist (live). -1 if not loaded.</summary>
    int GetPlayTimeT(CCSPlayerController player);

    /// <summary>Seconds played as Spectator (live). -1 if not loaded.</summary>
    int GetPlayTimeSpec(CCSPlayerController player);

    /// <summary>Total seconds (CT + T + Spectator). -1 if not loaded.</summary>
    int GetPlayTimeTotal(CCSPlayerController player);

    /// <summary>Total seconds excluding Spectator (CT + T). -1 if not loaded.</summary>
    int GetPlayTimeTotalExceptSpec(CCSPlayerController player);

    /// <summary>True once the player's stored time has been loaded from the DB this session.</summary>
    bool IsLoaded(CCSPlayerController player);
}
