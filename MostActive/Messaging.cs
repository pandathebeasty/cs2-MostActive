using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace MostActive;

public sealed partial class MostActive
{
    /// <summary>Localized chat message with the configured prefix + color tags resolved.</summary>
    private void Msg(CCSPlayerController player, string key, params object[] args)
    {
        var text = Localizer[key, args];
        player.PrintToChat(Colorize(Config.ChatPrefix + text));
    }

    /// <summary>Formats seconds using Config.TimeFormat (default "HH:mm:ss").</summary>
    private string FmtTime(int seconds)
    {
        if (seconds < 0) seconds = 0;
        int d = seconds / 86400;
        int h = seconds / 3600;        // total hours (HH can exceed 24)
        int hd = (seconds % 86400) / 3600; // hours within the day (for D token)
        int m = (seconds % 3600) / 60;
        int s = seconds % 60;

        var fmt = string.IsNullOrEmpty(Config.TimeFormat) ? "HH:mm:ss" : Config.TimeFormat;
        return fmt
            .Replace("HH", h.ToString("00"))
            .Replace("mm", m.ToString("00"))
            .Replace("ss", s.ToString("00"))
            .Replace("D", d.ToString())
            .Replace("H", h.ToString())
            .Replace("m", m.ToString())
            .Replace("s", s.ToString());
    }

    /// <summary>Replaces {tag} color tokens with the CS2 chat color chars.</summary>
    private static string Colorize(string text)
    {
        foreach (var (tag, ch) in ColorMap)
            text = text.Replace(tag, ch);
        return text;
    }

    private static readonly (string, string)[] ColorMap =
    {
        ("{default}", $"{ChatColors.Default}"), ("{white}", $"{ChatColors.White}"),
        ("{green}", $"{ChatColors.Green}"), ("{lime}", $"{ChatColors.Lime}"),
        ("{red}", $"{ChatColors.Red}"), ("{blue}", $"{ChatColors.Blue}"),
        ("{olive}", $"{ChatColors.Olive}"), ("{gold}", $"{ChatColors.Gold}"),
        ("{grey}", $"{ChatColors.Grey}"), ("{yellow}", $"{ChatColors.Yellow}"),
        ("{silver}", $"{ChatColors.Silver}"), ("{purple}", $"{ChatColors.Purple}"),
        ("{orange}", $"{ChatColors.Orange}"), ("{lightblue}", $"{ChatColors.LightBlue}"),
        ("{darkred}", $"{ChatColors.DarkRed}"), ("{lightpurple}", $"{ChatColors.LightPurple}"),
    };
}
