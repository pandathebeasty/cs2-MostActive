using System.Text.RegularExpressions;
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

    // Matches an explicit month argument "YYYY-MM".
    private static readonly Regex PeriodRe = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    /// <summary>
    /// Parses a period argument into a resolved month. Returns (ok, period) where period == null means
    /// ALL-TIME. Empty arg defaults to all-time. Accepts localized keywords (all/month/last from lang),
    /// their English originals, and an explicit "YYYY-MM". ok=false => unknown/invalid argument.
    /// </summary>
    private (bool ok, string? period) ParsePeriod(string? arg)
    {
        var a = (arg ?? "").Trim().ToLowerInvariant();
        if (a.Length == 0) return (true, null); // default: all-time

        string kwAll = Localizer["kw_all"].ToString().ToLowerInvariant();
        string kwMonth = Localizer["kw_month"].ToString().ToLowerInvariant();
        string kwLast = Localizer["kw_last"].ToString().ToLowerInvariant();

        if (a == kwAll || a == "all") return (true, null);
        if (a == kwMonth || a == "month") return (true, CurrentPeriod());
        if (a == kwLast || a == "last") return (true, PreviousPeriod());
        if (PeriodRe.IsMatch(a)) return (true, a);
        return (false, null);
    }

    /// <summary>Previous month as "YYYY-MM" in the configured timezone.</summary>
    private string PreviousPeriod()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz);
        return now.AddMonths(-1).ToString("yyyy-MM");
    }

    /// <summary>Human label for a period (null = localized "all time", else the "YYYY-MM" string).</summary>
    private string PeriodLabel(string? period) =>
        period is null ? Localizer["period_alltime"].ToString() : period;

    /// <summary>Header suffix shown after titles, e.g. " (2026-10)" or "" for all-time.</summary>
    private string PeriodSuffix(string? period) =>
        period is null ? Localizer["period_suffix_alltime"].ToString()
                        : Localizer["period_suffix_month", period].ToString();

    // !time / !active [period] -> your own T/CT/Spectator/Total breakdown.
    private void Cmd_Time(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true }) return;
        var (ok, period) = ParsePeriod(info.ArgCount > 1 ? info.GetArg(1) : null);
        if (!ok) { Msg(player, "period_bad", info.GetArg(1)); return; }
        ShowRank(player, period);
    }

    /// <summary>
    /// Prints a player's T/CT/Spectator/Total breakdown in chat. period == null => all-time (from the
    /// live session counters, which are seeded from the all-time row). A month => read that bucket
    /// from the DB (current month is read live; past months come straight from storage).
    /// </summary>
    public void ShowRank(CCSPlayerController player, string? period = null)
    {
        if (player is not { IsValid: true }) return;
        var s = player.Slot;
        if (!_loaded[s]) { Msg(player, "not_ready"); return; }

        // All-time, or the current month while we already hold its live counters: print immediately.
        if (period is null)
        {
            PrintRank(player, period, _t[s], _ct[s], _spec[s]);
            return;
        }

        // A specific month -> query the DB (works for both the current and past months).
        if (_db is null) { Msg(player, "not_ready"); return; }
        var steam = player.SteamID; var slot = s; var per = period;
        _ = Task.Run(async () =>
        {
            var row = await _db.GetPeriodAsync(steam, per);
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is not { IsValid: true } || p.SteamID != steam) return;
                PrintRank(p, per, row?.TimeT ?? 0, row?.TimeCt ?? 0, row?.TimeSpec ?? 0);
            });
        });
    }

    private void PrintRank(CCSPlayerController player, string? period, int t, int ct, int spec)
    {
        Msg(player, "rank_header", PeriodSuffix(period));
        Msg(player, "rank_line_t", FmtTime(t));
        Msg(player, "rank_line_ct", FmtTime(ct));
        if (Config.TrackSpectator) Msg(player, "rank_line_spec", FmtTime(spec));
        Msg(player, "rank_line_total", FmtTime(t + ct + (Config.TrackSpectator ? spec : 0)));
    }

    // !topactive [period] -> top N by total (all-time by default, or a month).
    private void Cmd_Top(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true } || _db is null) return;
        var (ok, period) = ParsePeriod(info.ArgCount > 1 ? info.GetArg(1) : null);
        if (!ok) { Msg(player, "period_bad", info.GetArg(1)); return; }

        var steam = player.SteamID; var slot = player.Slot; var n = Config.TopCount; var per = period;
        _ = Task.Run(async () =>
        {
            var top = per is null
                ? await _db.TopAllTimeAsync("total", n)
                : await _db.TopPeriodAsync(per, "total", n);
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is not { IsValid: true } || p.SteamID != steam) return;
                Msg(p, "top_header", n, PeriodSuffix(per));
                if (top.Count == 0) { Msg(p, "top_empty"); return; }
                int i = 1;
                foreach (var r in top)
                    Msg(p, "top_line", i++, r.PlayerName, FmtTime(r.Total));
            });
        });
    }

    // !wasted [period] -> sum of everyone's total time (all-time by default, or a month).
    private void Cmd_Wasted(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true } || _db is null) return;
        var (ok, period) = ParsePeriod(info.ArgCount > 1 ? info.GetArg(1) : null);
        if (!ok) { Msg(player, "period_bad", info.GetArg(1)); return; }

        var steam = player.SteamID; var slot = player.Slot; var per = period;
        _ = Task.Run(async () =>
        {
            var sum = per is null ? await _db.SumAllTimeAsync() : await _db.SumPeriodAsync(per);
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                if (p is { IsValid: true } && p.SteamID == steam)
                    Msg(p, "wasted", FmtTime((int)Math.Min(sum, int.MaxValue)), PeriodSuffix(per));
            });
        });
    }
}
