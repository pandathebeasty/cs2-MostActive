<?php
declare(strict_types=1);

/**
 * Thin PDO wrapper for MostActive (MySQL + SQLite, same queries). Two tables:
 *   <prefix>mostactive          all-time cumulative totals (default view).
 *   <prefix>mostactive_periods  per-month buckets (steamid, period "YYYY-MM").
 * A period of '' or 'all' reads the all-time table; a "YYYY-MM" reads that month's bucket.
 */
final class Db
{
    private PDO $pdo;
    private string $allTable;
    private string $perTable;

    public function __construct(array $config)
    {
        $prefix = $config['table_prefix'] ?? 'ma_';
        $this->allTable = $prefix . 'mostactive';
        $this->perTable = $prefix . 'mostactive_periods';

        if (($config['db_type'] ?? 'mysql') === 'sqlite') {
            $file = $config['sqlite_file'] ?? '';
            if (!is_file($file)) {
                throw new RuntimeException('SQLite file not found: ' . $file);
            }
            $this->pdo = new PDO('sqlite:' . $file);
        } else {
            $m = $config['mysql'];
            $dsn = sprintf('mysql:host=%s;port=%d;dbname=%s;charset=%s',
                $m['host'], (int) $m['port'], $m['database'], $m['charset'] ?? 'utf8mb4');
            $this->pdo = new PDO($dsn, $m['user'], $m['password']);
        }
        $this->pdo->setAttribute(PDO::ATTR_ERRMODE, PDO::ERRMODE_EXCEPTION);
        $this->pdo->setAttribute(PDO::ATTR_DEFAULT_FETCH_MODE, PDO::FETCH_ASSOC);
    }

    /** Only these columns are allowed for ORDER BY (guards against injection). */
    private const SORTS = ['total', 'time_ct', 'time_t', 'time_spec'];

    /** A period is either '' (all-time) or an exact "YYYY-MM". Anything else is treated as all-time. */
    public static function normalizePeriod(string $period): string
    {
        $period = trim($period);
        if ($period === '' || strtolower($period) === 'all') {
            return '';
        }
        return preg_match('/^\d{4}-(0[1-9]|1[0-2])$/', $period) ? $period : '';
    }

    /** Which table + WHERE a period selects. Returns [table, whereSql, params]. */
    private function source(string $period): array
    {
        if ($period === '') {
            return [$this->allTable, '', []];
        }
        return [$this->perTable, 'period = :period', [':period' => $period]];
    }

    /** @return array{rows: array, total: int} */
    public function top(string $sort, int $page, int $perPage, string $search, string $period = ''): array
    {
        $sort = in_array($sort, self::SORTS, true) ? $sort : 'total';
        $period = self::normalizePeriod($period);
        [$table, $periodWhere, $params] = $this->source($period);

        $page = max(1, $page);
        $offset = ($page - 1) * $perPage;

        $conds = [];
        if ($periodWhere !== '') {
            $conds[] = $periodWhere;
        }
        if ($search !== '') {
            $conds[] = 'playername LIKE :q';
            $params[':q'] = '%' . $search . '%';
        }
        $where = $conds ? ('WHERE ' . implode(' AND ', $conds)) : '';

        $count = $this->pdo->prepare("SELECT COUNT(*) FROM {$table} $where");
        $count->execute($params);
        $total = (int) $count->fetchColumn();

        $sql = "SELECT steamid, playername, time_ct, time_t, time_spec, total, last_seen
                FROM {$table} $where
                ORDER BY $sort DESC
                LIMIT :lim OFFSET :off";
        $stmt = $this->pdo->prepare($sql);
        foreach ($params as $k => $v) {
            $stmt->bindValue($k, $v);
        }
        $stmt->bindValue(':lim', $perPage, PDO::PARAM_INT);
        $stmt->bindValue(':off', $offset, PDO::PARAM_INT);
        $stmt->execute();

        return ['rows' => $stmt->fetchAll(), 'total' => $total];
    }

    public function sumTotal(string $period = ''): int
    {
        $period = self::normalizePeriod($period);
        [$table, $periodWhere, $params] = $this->source($period);
        $where = $periodWhere !== '' ? "WHERE $periodWhere" : '';
        $stmt = $this->pdo->prepare("SELECT COALESCE(SUM(total),0) FROM {$table} $where");
        $stmt->execute($params);
        return (int) $stmt->fetchColumn();
    }

    public function playerCount(string $period = ''): int
    {
        $period = self::normalizePeriod($period);
        [$table, $periodWhere, $params] = $this->source($period);
        $where = $periodWhere !== '' ? "WHERE $periodWhere" : '';
        $stmt = $this->pdo->prepare("SELECT COUNT(*) FROM {$table} $where");
        $stmt->execute($params);
        return (int) $stmt->fetchColumn();
    }

    /** Distinct months present in the periods table, newest first (for the dropdown). */
    public function periods(): array
    {
        try {
            $rows = $this->pdo->query(
                "SELECT DISTINCT period FROM {$this->perTable} ORDER BY period DESC"
            )->fetchAll(PDO::FETCH_COLUMN);
            return array_values(array_filter(array_map('strval', $rows)));
        } catch (Throwable $e) {
            return []; // periods table may not exist yet on a brand-new install
        }
    }
}
