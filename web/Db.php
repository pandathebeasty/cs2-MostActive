<?php
declare(strict_types=1);

/**
 * Thin PDO wrapper for MostActive, supporting MySQL and SQLite with the same queries.
 * The table is <prefix>players (steamid, playername, time_ct, time_t, time_spec, total, last_seen).
 */
final class Db
{
    private PDO $pdo;
    private string $table;

    public function __construct(array $config)
    {
        $prefix = $config['table_prefix'] ?? 'ma_';
        $this->table = $prefix . 'players';

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

    /** @return array{rows: array, total: int} */
    public function top(string $sort, int $page, int $perPage, string $search): array
    {
        $sort = in_array($sort, self::SORTS, true) ? $sort : 'total';
        $page = max(1, $page);
        $offset = ($page - 1) * $perPage;

        $where = '';
        $params = [];
        if ($search !== '') {
            $where = 'WHERE playername LIKE :q';
            $params[':q'] = '%' . $search . '%';
        }

        $count = $this->pdo->prepare("SELECT COUNT(*) FROM {$this->table} $where");
        $count->execute($params);
        $total = (int) $count->fetchColumn();

        $sql = "SELECT steamid, playername, time_ct, time_t, time_spec, total, last_seen
                FROM {$this->table} $where
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

    public function sumTotal(): int
    {
        return (int) $this->pdo->query("SELECT COALESCE(SUM(total),0) FROM {$this->table}")->fetchColumn();
    }

    public function playerCount(): int
    {
        return (int) $this->pdo->query("SELECT COUNT(*) FROM {$this->table}")->fetchColumn();
    }
}
