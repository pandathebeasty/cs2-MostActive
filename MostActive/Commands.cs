using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace MostActive;

public sealed partial class MostActive
{
    private void RegisterCommands()
    {
        foreach (var a in Config.TimeCommand) AddCommand($"css_{a}", "Show your played time", Cmd_Time);
        foreach (var a in Config.TopCommand) AddCommand($"css_{a}", "Show the top players", Cmd_Top);
        foreach (var a in Config.WastedCommand) AddCommand($"css_{a}", "Total time wasted on the server", Cmd_Wasted);
    }

    // !time / !active -> your own T/CT/Spectator/Total breakdown.
    private void Cmd_Time(CCSPlayerController? player, CommandInfo info)
    {
        if (player is { IsValid: true }) ShowRank(player);
    }

    /// <summary>Prints a player's own T/CT/Spectator/Total breakdown in chat (no command bound).</summary>
    public void ShowRank(CCSPlayerController player)
    {
        if (player is not { IsValid: true }) return;
        var s = player.Slot;
        if (!_loaded[s]) { Msg(player, "not_ready"); return; }
        Msg(player, "rank_header");
        Msg(player, "rank_line_t", FmtTime(_t[s]));
        Msg(player, "rank_line_ct", FmtTime(_ct[s]));
        if (Config.TrackSpectator) Msg(player, "rank_line_spec", FmtTime(_spec[s]));
        Msg(player, "rank_line_total", FmtTime(_t[s] + _ct[s] + (Config.TrackSpectator ? _spec[s] : 0)));
    }

    // !topactive -> top N by total.
    private void Cmd_Top(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true } || _db is null) return;
        var steam = player.SteamID; var slot = player.Slot; var n = Config.TopCount;
        _ = Task.Run(async () =>
        {
            var top = await _db.TopAsync("total", n);
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is not { IsValid: true } || p.SteamID != steam) return;
                Msg(p, "top_header", n);
                if (top.Count == 0) { Msg(p, "top_empty"); return; }
                int i = 1;
                foreach (var r in top)
                    Msg(p, "top_line", i++, r.PlayerName, FmtTime(r.Total));
            });
        });
    }

    // !wasted -> sum of everyone's total time.
    private void Cmd_Wasted(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true } || _db is null) return;
        var steam = player.SteamID; var slot = player.Slot;
        _ = Task.Run(async () =>
        {
            var sum = await _db.SumTotalAsync();
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is { IsValid: true } && p.SteamID == steam)
                    Msg(p, "wasted", FmtTime((int)Math.Min(sum, int.MaxValue)));
            });
        });
    }
}
