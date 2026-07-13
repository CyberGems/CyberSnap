<?php
declare(strict_types=1);

/**
 * CyberSnap Share front controller for cybersnap.cybergems.org
 * Shared hosting (cPanel) → same code on VPS later.
 */

header('X-Content-Type-Options: nosniff');
header('Referrer-Policy: no-referrer');
header('X-Frame-Options: DENY');

$configPath = dirname(__DIR__) . '/config.php';
if (!is_file($configPath)) {
    http_response_code(503);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode(['ok' => false, 'error' => 'Server not configured (missing config.php).']);
    exit;
}

/** @var array $config */
$config = require $configPath;
$storage = rtrim((string)$config['storage_path'], '/\\');
$filesDir = $storage . '/files';
$metaDir = $storage . '/meta';
$rateDir = $storage . '/rate';

foreach ([$storage, $filesDir, $metaDir, $rateDir] as $dir) {
    if (!is_dir($dir) && !mkdir($dir, 0750, true) && !is_dir($dir)) {
        json_error(500, 'storage_unavailable', 'Cannot create storage directories.');
    }
}

$route = $_GET['route'] ?? 'home';
$id = isset($_GET['id']) ? (string)$_GET['id'] : '';

switch ($route) {
    case 'health':
        json_ok(['ok' => true, 'service' => 'cybersnap-share', 'ttl_hours' => (int)$config['ttl_hours']]);

    case 'upload':
        handle_upload($config, $filesDir, $metaDir, $rateDir);
        break;

    case 'view':
        handle_view($config, $filesDir, $metaDir, $id);
        break;

    case 'file':
        handle_file($config, $filesDir, $metaDir, $id);
        break;

    case 'home':
    default:
        handle_home($config);
        break;
}

// ── Handlers ──────────────────────────────────────────────────────────────

function handle_home(array $config): void
{
    $base = htmlspecialchars((string)$config['public_base_url'], ENT_QUOTES, 'UTF-8');
    $ttl = (int)$config['ttl_hours'];
    header('Content-Type: text/html; charset=utf-8');
    echo <<<HTML
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <link rel="icon" href="/logo.png" type="image/png"/>
  <title>CyberSnap Share</title>
  <style>
    body{margin:0;font-family:Segoe UI,system-ui,sans-serif;background:#0d0f17;color:#e8eaef;
      display:flex;min-height:100vh;align-items:center;justify-content:center}
    .card{max-width:420px;padding:28px 32px;border-radius:12px;background:#161a24;border:1px solid #2a3142}
    .brand-row{display:flex;align-items:center;gap:12px;margin:0 0 12px}
    .brand-row img{width:40px;height:40px;border-radius:10px;display:block}
    h1{font-size:1.25rem;margin:0;color:#00e5ff;font-weight:600}
    p{margin:0;opacity:.75;line-height:1.5;font-size:.95rem}
    a{color:#00e5ff}
  </style>
</head>
<body>
  <div class="card">
    <div class="brand-row">
      <img src="/logo.png" width="40" height="40" alt="CyberSnap"/>
      <h1>CyberSnap Share</h1>
    </div>
    <p>Temporary public image hosting for <a href="https://cybergems.org">CyberGems</a> / CyberSnap.</p>
    <p style="margin-top:12px">Links expire after {$ttl} hours.</p>
    <p style="margin-top:12px;font-size:.85rem;opacity:.55">{$base}</p>
  </div>
</body>
</html>
HTML;
}

function handle_upload(array $config, string $filesDir, string $metaDir, string $rateDir): void
{
    if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') {
        json_error(405, 'method_not_allowed', 'POST required.');
    }

    require_api_key($config);
    enforce_rate_limit($config, $rateDir);

    $max = (int)$config['max_bytes'];
    $raw = null;
    $contentType = '';

    if (!empty($_FILES['image']) && is_uploaded_file($_FILES['image']['tmp_name'])) {
        $err = (int)($_FILES['image']['error'] ?? UPLOAD_ERR_NO_FILE);
        if ($err !== UPLOAD_ERR_OK) {
            json_error(400, 'upload_error', 'Upload failed (code ' . $err . ').');
        }
        $size = (int)$_FILES['image']['size'];
        if ($size <= 0 || $size > $max) {
            json_error(413, 'payload_too_large', 'Image exceeds size limit.');
        }
        $raw = file_get_contents($_FILES['image']['tmp_name']);
        $contentType = (string)($_FILES['image']['type'] ?? '');
    } else {
        $raw = file_get_contents('php://input');
        $contentType = (string)($_SERVER['CONTENT_TYPE'] ?? '');
    }

    if ($raw === false || $raw === '') {
        json_error(400, 'empty_body', 'No image data received.');
    }
    if (strlen($raw) > $max) {
        json_error(413, 'payload_too_large', 'Image exceeds size limit.');
    }

    $detected = detect_image($raw);
    if ($detected === null) {
        json_error(415, 'unsupported_media', 'Only PNG, JPEG, and WebP are allowed.');
    }

    [$ext, $mime] = $detected;
    $id = generate_id();
    $fileName = $id . '.' . $ext;
    $filePath = $filesDir . '/' . $fileName;
    $metaPath = $metaDir . '/' . $id . '.json';

    if (file_put_contents($filePath, $raw, LOCK_EX) === false) {
        json_error(500, 'write_failed', 'Could not store image.');
    }
    @chmod($filePath, 0640);

    $ttlHours = max(1, (int)$config['ttl_hours']);
    $now = time();
    $meta = [
        'id' => $id,
        'file' => $fileName,
        'mime' => $mime,
        'bytes' => strlen($raw),
        'created_at' => $now,
        'expires_at' => $now + ($ttlHours * 3600),
        'width' => null,
        'height' => null,
    ];

    if (function_exists('getimagesizefromstring')) {
        $info = @getimagesizefromstring($raw);
        if (is_array($info)) {
            $meta['width'] = $info[0] ?? null;
            $meta['height'] = $info[1] ?? null;
        }
    }

    file_put_contents($metaPath, json_encode($meta, JSON_UNESCAPED_SLASHES), LOCK_EX);
    @chmod($metaPath, 0640);

    $base = rtrim((string)$config['public_base_url'], '/');
    $url = $base . '/' . $id;
    $fileUrl = $base . '/f/' . $id;

    json_ok([
        'ok' => true,
        'id' => $id,
        'url' => $url,
        'file_url' => $fileUrl,
        'expires_at' => $meta['expires_at'],
        'ttl_hours' => $ttlHours,
        'mime' => $mime,
        'bytes' => $meta['bytes'],
        'width' => $meta['width'],
        'height' => $meta['height'],
    ]);
}

function handle_view(array $config, string $filesDir, string $metaDir, string $id): void
{
    if (!is_valid_id($id)) {
        http_response_code(404);
        echo 'Not found';
        exit;
    }

    $meta = load_meta($metaDir, $id);
    if ($meta === null || is_expired($meta)) {
        if ($meta !== null) {
            delete_share($filesDir, $metaDir, $meta);
        }
        http_response_code(404);
        header('Content-Type: text/html; charset=utf-8');
        echo '<!DOCTYPE html><html><head><title>Expired</title></head><body style="font-family:system-ui;background:#0d0f17;color:#ccc;display:flex;min-height:100vh;align-items:center;justify-content:center"><p>This link has expired or does not exist.</p></body></html>';
        exit;
    }

    $filePath = $filesDir . '/' . $meta['file'];
    if (!is_file($filePath)) {
        http_response_code(404);
        echo 'Not found';
        exit;
    }

    $base = rtrim((string)$config['public_base_url'], '/');
    $fileUrl = htmlspecialchars($base . '/f/' . $id, ENT_QUOTES, 'UTF-8');
    $w = (int)($meta['width'] ?? 0);
    $h = (int)($meta['height'] ?? 0);
    $bytes = (int)($meta['bytes'] ?? 0);
    $mime = htmlspecialchars((string)($meta['mime'] ?? 'image'), ENT_QUOTES, 'UTF-8');
    $fmt = strtoupper(pathinfo((string)$meta['file'], PATHINFO_EXTENSION));
    $sizeLabel = format_bytes($bytes);
    $dims = ($w > 0 && $h > 0) ? "{$w} × {$h} px" : '';
    $expiresAt = (int)$meta['expires_at'];
    $exp = date('Y-m-d H:i', $expiresAt) . ' UTC';
    // Computed once per page view from stored expires_at — no extra I/O or API.
    $remainingLabel = format_remaining(max(0, $expiresAt - time()));

    header('Content-Type: text/html; charset=utf-8');
    // Short cache so "hours left" stays roughly fresh without hammering origin.
    header('Cache-Control: public, max-age=120');
    echo <<<HTML
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <meta name="robots" content="noindex,nofollow"/>
  <link rel="icon" href="/logo.png" type="image/png"/>
  <title>CyberSnap Share</title>
  <style>
    *{box-sizing:border-box}
    body{margin:0;font-family:Segoe UI,system-ui,sans-serif;background:#0d0f17;color:#e8eaef;
      min-height:100vh;display:flex;flex-direction:column;align-items:center;padding:24px 16px 48px}
    header{width:100%;max-width:960px;display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:20px;flex-wrap:wrap}
    .brand{display:flex;align-items:center;gap:10px;font-weight:600;font-size:1.05rem;color:#00e5ff}
    .brand img{width:32px;height:32px;border-radius:8px;display:block;flex-shrink:0}
    .meta{opacity:.65;font-size:.9rem}
    .actions{display:flex;gap:8px}
    a.btn{display:inline-block;padding:8px 16px;border-radius:999px;border:1px solid #3b82f6;color:#93c5fd;
      text-decoration:none;font-size:.9rem}
    a.btn:hover{background:#1e293b}
    .frame{background:#111;border-radius:8px;padding:12px;max-width:100%;box-shadow:0 8px 40px rgba(0,0,0,.45)}
    .frame img{display:block;max-width:min(920px,100%);height:auto;margin:0 auto}
    footer{margin-top:18px;opacity:.55;font-size:0.95rem;line-height:1.45;text-align:center}
  </style>
</head>
<body>
  <header>
    <div class="brand">
      <img src="/logo.png" width="32" height="32" alt="CyberSnap"/>
      <span>CyberSnap Share</span>
    </div>
    <div class="meta">{$fmt} &nbsp; {$dims} &nbsp; {$sizeLabel}</div>
    <div class="actions">
      <a class="btn" href="{$fileUrl}" download>Download</a>
    </div>
  </header>
  <div class="frame"><img src="{$fileUrl}" alt="Shared image" /></div>
  <footer>Expires in {$remainingLabel} · {$exp} · Public link · CyberGems</footer>
</body>
</html>
HTML;
}

function handle_file(array $config, string $filesDir, string $metaDir, string $id): void
{
    if (!is_valid_id($id)) {
        http_response_code(404);
        exit;
    }

    $meta = load_meta($metaDir, $id);
    if ($meta === null || is_expired($meta)) {
        if ($meta !== null) {
            delete_share($filesDir, $metaDir, $meta);
        }
        http_response_code(404);
        exit;
    }

    $filePath = $filesDir . '/' . $meta['file'];
    if (!is_file($filePath)) {
        http_response_code(404);
        exit;
    }

    $mime = (string)($meta['mime'] ?? 'application/octet-stream');
    header('Content-Type: ' . $mime);
    header('Content-Length: ' . (string)filesize($filePath));
    header('Cache-Control: public, max-age=3600');
    header('X-Content-Type-Options: nosniff');
    // Inline for <img>; download attribute on viewer uses same URL
    header('Content-Disposition: inline; filename="' . basename((string)$meta['file']) . '"');
    readfile($filePath);
    exit;
}

// ── Security helpers ──────────────────────────────────────────────────────

function require_api_key(array $config): void
{
    $auth = $_SERVER['HTTP_AUTHORIZATION'] ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? '';
    if ($auth === '' && function_exists('apache_request_headers')) {
        $headers = apache_request_headers();
        foreach ($headers as $k => $v) {
            if (strcasecmp($k, 'Authorization') === 0) {
                $auth = $v;
                break;
            }
        }
    }

    $token = '';
    if (preg_match('/^\s*Bearer\s+(\S+)\s*$/i', $auth, $m)) {
        $token = $m[1];
    } elseif (!empty($_SERVER['HTTP_X_CYBERSNAP_KEY'])) {
        $token = (string)$_SERVER['HTTP_X_CYBERSNAP_KEY'];
    }

    $valid = [(string)$config['api_key']];
    if (!empty($config['api_keys_extra']) && is_array($config['api_keys_extra'])) {
        foreach ($config['api_keys_extra'] as $extra) {
            if (is_string($extra) && $extra !== '') {
                $valid[] = $extra;
            }
        }
    }

    $ok = false;
    foreach ($valid as $key) {
        if ($key !== '' && $key !== 'CHANGE_ME_TO_A_LONG_RANDOM_SECRET' && hash_equals($key, $token)) {
            $ok = true;
            break;
        }
    }

    if (!$ok) {
        json_error(401, 'unauthorized', 'Invalid or missing API key.');
    }
}

function enforce_rate_limit(array $config, string $rateDir): void
{
    $ip = client_ip();
    $ipKey = hash('sha256', $ip);
    $path = $rateDir . '/' . $ipKey . '.json';
    $now = time();
    $minute = (int)$config['rate_limit_per_minute'];
    $day = (int)$config['rate_limit_per_day'];

    $data = ['minute' => [], 'day' => []];
    if (is_file($path)) {
        $decoded = json_decode((string)file_get_contents($path), true);
        if (is_array($decoded)) {
            $data = array_merge($data, $decoded);
        }
    }

    $data['minute'] = array_values(array_filter($data['minute'] ?? [], static fn($t) => is_int($t) && $t > $now - 60));
    $data['day'] = array_values(array_filter($data['day'] ?? [], static fn($t) => is_int($t) && $t > $now - 86400));

    if (count($data['minute']) >= $minute || count($data['day']) >= $day) {
        json_error(429, 'rate_limited', 'Too many uploads. Try again later.');
    }

    $data['minute'][] = $now;
    $data['day'][] = $now;
    file_put_contents($path, json_encode($data), LOCK_EX);
}

function client_ip(): string
{
    // Prefer direct connection; shared hosts often set REMOTE_ADDR only.
    return (string)($_SERVER['REMOTE_ADDR'] ?? '0.0.0.0');
}

/**
 * @return array{0:string,1:string}|null [ext, mime]
 */
function detect_image(string $raw): ?array
{
    if (str_starts_with($raw, "\x89PNG\r\n\x1a\n")) {
        return ['png', 'image/png'];
    }
    if (str_starts_with($raw, "\xff\xd8\xff")) {
        return ['jpg', 'image/jpeg'];
    }
    // WebP: RIFF....WEBP
    if (strlen($raw) >= 12 && str_starts_with($raw, 'RIFF') && substr($raw, 8, 4) === 'WEBP') {
        return ['webp', 'image/webp'];
    }
    return null;
}

function generate_id(): string
{
    // ~11 chars base62-ish from 8 random bytes
    $bin = random_bytes(8);
    return rtrim(strtr(base64_encode($bin), '+/', 'Aa'), '=');
}

function is_valid_id(string $id): bool
{
    return (bool)preg_match('/^[A-Za-z0-9_-]{6,24}$/', $id);
}

function load_meta(string $metaDir, string $id): ?array
{
    $path = $metaDir . '/' . $id . '.json';
    if (!is_file($path)) {
        return null;
    }
    $data = json_decode((string)file_get_contents($path), true);
    return is_array($data) ? $data : null;
}

function is_expired(array $meta): bool
{
    return isset($meta['expires_at']) && time() >= (int)$meta['expires_at'];
}

function delete_share(string $filesDir, string $metaDir, array $meta): void
{
    $id = (string)($meta['id'] ?? '');
    $file = (string)($meta['file'] ?? '');
    if ($file !== '') {
        @unlink($filesDir . '/' . $file);
    }
    if ($id !== '') {
        @unlink($metaDir . '/' . $id . '.json');
    }
}

function format_bytes(int $bytes): string
{
    if ($bytes < 1024) {
        return $bytes . ' B';
    }
    if ($bytes < 1024 * 1024) {
        return round($bytes / 1024, 1) . ' KB';
    }
    return round($bytes / (1024 * 1024), 1) . ' MB';
}

/** Human remaining TTL from seconds (no DB, pure math). */
function format_remaining(int $seconds): string
{
    if ($seconds <= 0) {
        return '0 min';
    }
    $hours = intdiv($seconds, 3600);
    $mins = intdiv($seconds % 3600, 60);
    if ($hours >= 1) {
        return $mins > 0 ? "{$hours} h {$mins} min" : "{$hours} h";
    }
    if ($mins >= 1) {
        return "{$mins} min";
    }
    return 'less than 1 min';
}

function json_ok(array $payload): void
{
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode($payload, JSON_UNESCAPED_SLASHES);
    exit;
}

function json_error(int $status, string $code, string $message): void
{
    http_response_code($status);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode(['ok' => false, 'error' => $code, 'message' => $message], JSON_UNESCAPED_SLASHES);
    exit;
}
