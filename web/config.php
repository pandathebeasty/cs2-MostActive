<?php
/**
 * MostActive web — config. Works with MySQL or SQLite (same table the plugin writes).
 * Copy to config.local.php to override without editing this file (config.local.php wins).
 */
declare(strict_types=1);

$config = [
    // 'mysql' or 'sqlite' — must match the plugin's Database.Type.
    'db_type' => 'mysql',

    // Table prefix — must match the plugin's Database.TablePrefix (default 'ma_').
    'table_prefix' => 'ma_',

    // MySQL (used when db_type = mysql).
    'mysql' => [
        'host' => '127.0.0.1',
        'port' => 3306,
        'database' => 'mostactive',
        'user' => 'root',
        'password' => '',
        'charset' => 'utf8mb4',
    ],

    // SQLite (used when db_type = sqlite) — absolute path to the plugin's mostactive.db.
    'sqlite_file' => 'C:/servers/cs2/game/csgo/addons/counterstrikesharp/plugins/MostActive/mostactive.db',

    // UI
    'site_title' => 'Most Active — Rank',
    'per_page' => 25,
    // Time display — match the plugin's TimeFormat. Tokens: HH/mm/ss (padded), H/m/s, D (days).
    'time_format' => 'HH:mm:ss',
];

$local = __DIR__ . '/config.local.php';
if (is_file($local)) {
    $override = require $local;
    if (is_array($override)) {
        $config = array_replace_recursive($config, $override);
    }
}

return $config;
