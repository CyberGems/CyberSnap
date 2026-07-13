<?php
declare(strict_types=1);

/**
 * Delete expired shares. Run via cPanel cron, e.g. every hour:
 *   /usr/bin/php /home/USER/cybersnap-share/cleanup.php
 *
 * Or from the repo on VPS:
 *   php services/cybersnap-share/cleanup.php
 */

$configPath = __DIR__ . '/config.php';
if (!is_file($configPath)) {
    fwrite(STDERR, "Missing config.php\n");
    exit(1);
}

/** @var array $config */
$config = require $configPath;
$storage = rtrim((string)$config['storage_path'], '/\\');
$filesDir = $storage . '/files';
$metaDir = $storage . '/meta';
$rateDir = $storage . '/rate';

$now = time();
$deleted = 0;

if (is_dir($metaDir)) {
    foreach (glob($metaDir . '/*.json') ?: [] as $metaPath) {
        $meta = json_decode((string)file_get_contents($metaPath), true);
        if (!is_array($meta)) {
            @unlink($metaPath);
            continue;
        }
        $exp = (int)($meta['expires_at'] ?? 0);
        if ($exp > 0 && $now >= $exp) {
            $file = (string)($meta['file'] ?? '');
            if ($file !== '') {
                @unlink($filesDir . '/' . $file);
            }
            @unlink($metaPath);
            $deleted++;
        }
    }
}

// Prune old rate-limit files (> 2 days)
if (is_dir($rateDir)) {
    foreach (glob($rateDir . '/*.json') ?: [] as $ratePath) {
        if (filemtime($ratePath) < $now - 172800) {
            @unlink($ratePath);
        }
    }
}

echo date('c') . " cleanup: deleted={$deleted}\n";
