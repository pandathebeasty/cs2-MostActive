# MostActive (CS2)

A time-played rank system for Counter-Strike 2 on [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp).
Counts seconds played per team (T / CT / Spectator) per SteamID64 and shows ranks via colored chat
commands. Includes a standalone web rank board. Port of Franc1sco's SM *Most Active*.

> **Version:** 1.0.0 · **Target:** .NET 10 / CounterStrikeSharp 1.0.379

## Parts
1. **Plugin** (`MostActive/`) — server-side C#; tracks time, stores to MySQL or SQLite, chat commands.
2. **Web** (`web/`) — standalone PHP rank board (no framework/build); reads the same MySQL or SQLite DB.

## Commands (chat; configurable aliases)
| Command | Does |
|---|---|
| `!topactive` | Top N players by total time |
| `!wasted` | Total time played by everyone on the server |

Time is shown per `TimeFormat` (default `HH:mm:ss`; tokens `HH/mm/ss`, `H/m/s`, `D` for days).

## Install — plugin
1. Build (`dotnet build MostActive/MostActive.csproj -c Release`) or grab a release.
2. Copy the output to `addons/counterstrikesharp/plugins/MostActive/` (folder name must match the DLL):
   `MostActive.dll` + `Dapper.dll`, `MySqlConnector.dll`, `Microsoft.Data.Sqlite.dll`, `runtimes/`, `lang/`.
3. Start once to generate `configs/plugins/MostActive/MostActive.json`; edit it
   (use `MostActive.example.jsonc` as the commented reference). Pick `sqlite` or `mysql`.

## Install — web
1. Put the `web/` folder on a PHP 8+ host (Apache/nginx + php-fpm).
2. Copy `config.php` values or create `config.local.php` (it overrides and is git-ignored):
   - `db_type` = `mysql` or `sqlite` (match the plugin),
   - `table_prefix` = same as the plugin (`ma_`),
   - MySQL creds, or the absolute path to the plugin's `mostactive.db` for SQLite.
3. Open `index.php` — sortable (Total / T / CT / Spectator), searchable, paged rank board.

> For a public website use **MySQL** (both plugin and web point at the same DB). SQLite works when
> the web host can read the plugin's `.db` file directly.

## Database
Table `<prefix>mostactive` (default `ma_mostactive`): `steamid` (SteamID64, PK), `playername`, `time_ct`,
`time_t`, `time_spec`, `total`, `last_seen`. Created automatically by the plugin. Players unseen for
`PruneDays` days are pruned on load.

## Notes
- Time is persisted on disconnect, on each death (cheap periodic save), and plugin unload.
- No in-game menu — plain colored chat (en/ro lang files; add more under `MostActive/lang/`).
- Credit: original SM plugin by Franc1sco / shanapu (GPL-3.0).
