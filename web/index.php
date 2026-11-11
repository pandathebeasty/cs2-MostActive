<?php
declare(strict_types=1);

/**
 * MostActive web — single-page rank board. No framework/build: PDO + plain PHP view.
 * Shows a sortable, searchable, paged table of played time (T / CT / Spectator / Total).
 * Works with the plugin's MySQL or SQLite database.
 */

require __DIR__ . '/Db.php';
$config = require __DIR__ . '/config.php';

function h(string $s): string { return htmlspecialchars($s, ENT_QUOTES, 'UTF-8'); }

function fmt_time(int $seconds, string $fmt = 'HH:mm:ss'): string
{
    if ($seconds < 0) $seconds = 0;
    $d = intdiv($seconds, 86400);
    $h = intdiv($seconds, 3600);              // total hours (can exceed 24)
    $m = intdiv($seconds % 3600, 60);
    $s = $seconds % 60;
    if ($fmt === '') $fmt = 'HH:mm:ss';
    // Order matters: 2-char tokens before 1-char.
    return strtr($fmt, [
        'HH' => sprintf('%02d', $h),
        'mm' => sprintf('%02d', $m),
        'ss' => sprintf('%02d', $s),
        'D'  => (string) $d,
        'H'  => (string) $h,
        'm'  => (string) $m,
        's'  => (string) $s,
    ]);
}

function steam_profile(string $steamid64): string
{
    return ctype_digit($steamid64) ? 'https://steamcommunity.com/profiles/' . $steamid64 : '#';
}

$sortLabels = ['total' => 'Total', 'time_t' => 'T', 'time_ct' => 'CT', 'time_spec' => 'Spectator'];
$sort = $_GET['sort'] ?? 'total';
if (!isset($sortLabels[$sort])) $sort = 'total';
$page = max(1, (int) ($_GET['page'] ?? 1));
$search = trim((string) ($_GET['q'] ?? ''));
$perPage = min(25, max(1, (int) ($config['per_page'] ?? 25)));

$timeFmt = (string) ($config['time_format'] ?? 'HH:mm:ss');
$isApi = isset($_GET['api']);

$error = null;
$rows = [];
$total = 0;
$sumTotal = 0;
$players = 0;
try {
    $db = new Db($config);
    $res = $db->top($sort, $page, $perPage, $search);
    $rows = $res['rows'];
    $total = $res['total'];
    $sumTotal = $db->sumTotal();
    $players = $db->playerCount();
} catch (Throwable $e) {
    $error = $e->getMessage();
}

// Live-search / paging endpoint: return JSON (used by fetch() from the page).
if ($isApi) {
    header('Content-Type: application/json; charset=utf-8');
    if ($error) { http_response_code(500); echo json_encode(['error' => $error]); exit; }
    $out = [];
    $rankStart = ($page - 1) * $perPage;
    foreach ($rows as $i => $r) {
        $out[] = [
            'rank' => $rankStart + $i + 1,
            'steamid' => (string) $r['steamid'],
            'profile' => steam_profile((string) $r['steamid']),
            'name' => (string) $r['playername'],
            't' => fmt_time((int) $r['time_t'], $timeFmt),
            'ct' => fmt_time((int) $r['time_ct'], $timeFmt),
            'spec' => fmt_time((int) $r['time_spec'], $timeFmt),
            'total' => fmt_time((int) $r['total'], $timeFmt),
        ];
    }
    echo json_encode([
        'rows' => $out,
        'total' => $total,
        'pages' => max(1, (int) ceil($total / $perPage)),
        'page' => $page,
        'players' => $players,
        'sum' => fmt_time($sumTotal, $timeFmt),
    ]);
    exit;
}

$pages = max(1, (int) ceil($total / $perPage));
$base = strtok($_SERVER['REQUEST_URI'] ?? '/', '?');
function link_with(string $base, array $params): string
{
    return $base . '?' . http_build_query($params);
}
$rankStart = ($page - 1) * $perPage;
?>
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title><?= h($config['site_title'] ?? 'Most Active') ?></title>
<style>
  :root {
    --accent:#c7a977; --accent2:#e6cfa6; --bg:#0c0f0b; --bg2:#10140f;
    --panel:#171d18cc; --panel2:#1c231d; --line:#ffffff14; --txt:#e9ece7; --muted:#99a39b;
    --t:#eb6a5c; --ct:#5b9bd8; --radius:14px;
  }
  * { box-sizing:border-box; }
  body {
    margin:0; color:var(--txt); font-family:'Segoe UI', system-ui, Arial, sans-serif;
    background:
      radial-gradient(900px 500px at 15% -10%, #1d2a1f55, transparent 60%),
      radial-gradient(700px 400px at 100% 0%, #2a241a55, transparent 55%),
      linear-gradient(180deg, var(--bg2), var(--bg));
    min-height:100vh;
  }
  .wrap { width:100%; margin:0; padding:34px 40px 60px; }
  .head { display:flex; align-items:flex-end; justify-content:space-between; gap:16px; flex-wrap:wrap; margin-bottom:22px; }
  h1 { font-size:30px; margin:0; font-weight:800; letter-spacing:.5px; }
  h1 .acc { background:linear-gradient(90deg,var(--accent2),var(--accent)); -webkit-background-clip:text; background-clip:text; color:transparent; }
  .sub { color:var(--muted); font-size:13px; margin-top:6px; }
  .sub strong { color:var(--accent2); }
  .stats { display:flex; gap:10px; }
  .stat { background:var(--panel); border:1px solid var(--line); border-radius:12px; padding:10px 16px; text-align:center; backdrop-filter:blur(6px); }
  .stat .v { font-size:18px; font-weight:700; color:var(--accent2); }
  .stat .k { font-size:11px; color:var(--muted); text-transform:uppercase; letter-spacing:.5px; }

  /* Podium (top 3) */
  .podium { display:grid; grid-template-columns:1fr 1.15fr 1fr; gap:12px; margin:4px 0 22px; align-items:end; }
  .pod { background:var(--panel); border:1px solid var(--line); border-radius:var(--radius); padding:16px 12px; text-align:center; position:relative; overflow:hidden; backdrop-filter:blur(6px); }
  .pod::before { content:''; position:absolute; inset:0 0 auto 0; height:3px; }
  .pod.g1::before { background:linear-gradient(90deg,#ffe08a,#e9b64a); } .pod.g1 { transform:translateY(-10px); }
  .pod.g2::before { background:linear-gradient(90deg,#d8dde3,#9aa3ad); }
  .pod.g3::before { background:linear-gradient(90deg,#e0a479,#b06f44); }
  .pod .medal { font-size:26px; }
  .pod .pname { font-weight:700; margin:6px 0 2px; font-size:15px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .pod .ptime { color:var(--accent2); font-size:14px; }
  .pod a { color:inherit; text-decoration:none; }
  .pod a:hover .pname { color:var(--accent); }

  .bar { display:flex; gap:10px; flex-wrap:wrap; align-items:center; margin-bottom:14px; }
  .tabs { display:flex; gap:6px; background:var(--panel); padding:4px; border-radius:22px; border:1px solid var(--line); }
  button.tab { padding:7px 15px; border-radius:18px; background:transparent; border:none; cursor:pointer;
               color:var(--muted); font-size:13px; font-family:inherit; transition:.15s; }
  button.tab:hover { color:var(--txt); }
  button.tab.active { background:linear-gradient(90deg,var(--accent2),var(--accent)); color:#141a12; font-weight:700; }
  .searchwrap { margin-left:auto; position:relative; }
  .searchwrap svg { position:absolute; left:11px; top:50%; transform:translateY(-50%); opacity:.5; }
  #q { background:var(--panel); border:1px solid var(--line); color:var(--txt); border-radius:22px;
       padding:9px 14px 9px 34px; font-size:13px; min-width:230px; outline:none; transition:.15s; }
  #q:focus { border-color:var(--accent); min-width:260px; }

  .card { background:var(--panel); border:1px solid var(--line); border-radius:var(--radius); overflow:hidden; backdrop-filter:blur(6px); }
  table { width:100%; border-collapse:collapse; }
  th, td { padding:11px 14px; text-align:left; font-size:14px; }
  th { color:var(--muted); font-weight:600; font-size:11px; text-transform:uppercase; letter-spacing:.5px; background:#ffffff08; }
  tbody tr { border-top:1px solid var(--line); transition:background .12s; }
  tbody tr:hover { background:#ffffff08; }
  td.rank { color:var(--accent); font-weight:800; width:52px; }
  td.rank .m { font-size:16px; }
  td.name a { color:var(--txt); text-decoration:none; font-weight:600; }
  td.name a:hover { color:var(--accent); }
  td.num { color:var(--muted); white-space:nowrap; font-variant-numeric:tabular-nums; }
  td.t { color:var(--t); } td.ct { color:var(--ct); }
  td.total { color:var(--accent2); font-weight:700; font-variant-numeric:tabular-nums; }
  .empty { padding:26px; text-align:center; color:var(--muted); }

  .pager { display:flex; gap:8px; justify-content:center; margin-top:18px; align-items:center; }
  .pager button { padding:7px 13px; border-radius:9px; border:1px solid var(--line); background:var(--panel);
                  color:var(--muted); cursor:pointer; font-size:13px; font-family:inherit; }
  .pager button:hover:not(:disabled) { color:var(--txt); border-color:var(--accent); }
  .pager button:disabled { opacity:.4; cursor:default; }
  .pager .cur { color:var(--accent2); font-size:13px; font-variant-numeric:tabular-nums; }
  .err { background:#3a1f1f; border:1px solid #6b2b2b; color:#f0c7c7; padding:12px 14px; border-radius:10px; }
  .foot { color:#5d655e; font-size:12px; margin-top:24px; text-align:center; }
  .loading { opacity:.5; transition:opacity .15s; }
</style>
</head>
<body>
<div class="wrap">
  <div class="head">
    <div>
      <h1>Most <span class="acc">Active</span></h1>
      <div class="sub" id="sub"></div>
    </div>
    <div class="stats">
      <div class="stat"><div class="v" id="stPlayers">—</div><div class="k">Players</div></div>
      <div class="stat"><div class="v" id="stTime">—</div><div class="k">Total played</div></div>
    </div>
  </div>

  <?php if ($error): ?>
    <div class="err">Database error: <?= h($error) ?></div>
  <?php else: ?>

  <div class="podium" id="podium"></div>

  <div class="bar">
    <div class="tabs" id="tabs">
      <?php foreach ($sortLabels as $key => $label): ?>
        <button class="tab <?= $sort === $key ? 'active' : '' ?>" data-sort="<?= h($key) ?>"><?= h($label) ?></button>
      <?php endforeach; ?>
    </div>
    <div class="searchwrap">
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/></svg>
      <input type="text" id="q" placeholder="Search player…" value="<?= h($search) ?>" autocomplete="off">
    </div>
  </div>

  <div class="card">
    <table>
      <thead>
        <tr><th>#</th><th>Player</th><th>T</th><th>CT</th><th>Spectator</th><th>Total</th></tr>
      </thead>
      <tbody id="rows"></tbody>
    </table>
  </div>

  <div class="pager">
    <button id="prev">‹ Prev</button>
    <span class="cur" id="pageLabel">1 / 1</span>
    <button id="next">Next ›</button>
  </div>

  <?php endif; ?>

  <div class="foot">MostActive — CS2 time-played rank. Data shared with the server plugin.</div>
</div>

<script>
(() => {
  const esc = s => String(s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
  const medals = { 1:'🥇', 2:'🥈', 3:'🥉' };
  let sort = <?= json_encode($sort) ?>, q = <?= json_encode($search) ?>, page = 1, pages = 1, timer = null;

  const el = id => document.getElementById(id);
  const rowsEl = el('rows'), podEl = el('podium'), cardTable = document.querySelector('.card');

  async function load() {
    cardTable.classList.add('loading');
    const url = `?api=1&sort=${encodeURIComponent(sort)}&q=${encodeURIComponent(q)}&page=${page}`;
    let data;
    try { data = await (await fetch(url)).json(); }
    catch { cardTable.classList.remove('loading'); return; }
    cardTable.classList.remove('loading');
    if (data.error) { rowsEl.innerHTML = `<tr><td colspan="6" class="empty">${esc(data.error)}</td></tr>`; return; }

    pages = data.pages; page = data.page;
    el('stPlayers').textContent = Number(data.players).toLocaleString();
    el('stTime').textContent = data.sum;
    el('sub').innerHTML = `${Number(data.players).toLocaleString()} players · total time played: <strong>${esc(data.sum)}</strong>`;
    el('pageLabel').textContent = `${page} / ${pages}`;
    el('prev').disabled = page <= 1;
    el('next').disabled = page >= pages;

    const showPod = page === 1 && sort === 'total' && q === '' && data.rows.length >= 3;
    podEl.style.display = showPod ? 'grid' : 'none';
    if (showPod) {
      const [a, b, c] = data.rows;
      const card = (r, cls) => `<div class="pod ${cls}"><a href="${esc(r.profile)}" target="_blank" rel="noopener">
        <div class="medal">${medals[r.rank]}</div><div class="pname">${esc(r.name)}</div>
        <div class="ptime">${esc(r.total)}</div></a></div>`;
      podEl.innerHTML = card(b, 'g2') + card(a, 'g1') + card(c, 'g3');
    }

    if (!data.rows.length) { rowsEl.innerHTML = `<tr><td colspan="6" class="empty">No players found.</td></tr>`; return; }
    rowsEl.innerHTML = data.rows.map(r => {
      const rk = medals[r.rank] ? `<span class="m">${medals[r.rank]}</span>` : r.rank;
      return `<tr>
        <td class="rank">${rk}</td>
        <td class="name"><a href="${esc(r.profile)}" target="_blank" rel="noopener">${esc(r.name)}</a></td>
        <td class="num t">${esc(r.t)}</td>
        <td class="num ct">${esc(r.ct)}</td>
        <td class="num">${esc(r.spec)}</td>
        <td class="total">${esc(r.total)}</td>
      </tr>`;
    }).join('');
  }

  el('tabs').addEventListener('click', e => {
    const b = e.target.closest('button[data-sort]'); if (!b) return;
    document.querySelectorAll('#tabs .tab').forEach(t => t.classList.remove('active'));
    b.classList.add('active'); sort = b.dataset.sort; page = 1; load();
  });
  el('q').addEventListener('input', e => {
    q = e.target.value.trim(); page = 1;
    clearTimeout(timer); timer = setTimeout(load, 220);
  });
  el('prev').addEventListener('click', () => { if (page > 1) { page--; load(); } });
  el('next').addEventListener('click', () => { if (page < pages) { page++; load(); } });

  load();
})();
</script>
</body>
</html>
