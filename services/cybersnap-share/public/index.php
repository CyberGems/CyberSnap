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
    $lang = preferred_language();
    $copy = $lang === 'es'
        ? [
            'nav_label' => 'Navegación',
            'eyebrow' => 'COMPARTIR IMÁGENES TEMPORALES',
            'title' => 'Comparte capturas sin fricción.',
            'lead' => 'CyberSnap Share le da a CyberSnap un lugar rápido y confiable para publicar una imagen y obtener un enlace que caduca automáticamente.',
            'primary' => 'Visitar CyberGems',
            'secondary' => 'Cómo funciona',
            'status' => 'Servicio listo para CyberSnap',
            'features_label' => 'Características',
            'features_title' => 'Diseñado para compartir rápido y por poco tiempo.',
            'feature_one_title' => 'Hecho para CyberSnap',
            'feature_one_body' => 'Optimizado para capturas y para el flujo de trabajo de escritorio de CyberSnap.',
            'feature_two_title' => 'Temporal por diseño',
            'feature_two_body' => 'Los enlaces públicos caducan automáticamente y luego se eliminan su imagen y metadata.',
            'feature_three_title' => 'Entrega sencilla',
            'feature_three_body' => 'Sube mediante la API, abre el enlace generado y descarga la imagen original cuando la necesites.',
            'how_label' => 'Flujo',
            'how_title' => 'De la captura al enlace',
            'step_one_title' => 'Captura',
            'step_one_body' => 'Toma una captura con CyberSnap y elige Compartir.',
            'step_two_title' => 'Sube',
            'step_two_body' => 'CyberSnap envía la imagen a este endpoint protegido.',
            'step_three_title' => 'Comparte',
            'step_three_body' => 'Recibes un enlace público listo para pegar donde quieras.',
            'technical_title' => 'Un endpoint enfocado, no una red social.',
            'technical_body' => 'Este servicio existe para entregar enlaces públicos de imágenes con vida limitada para CyberSnap. Sin cuenta, feed ni panel de seguimiento.',
            'health_label' => 'Estado del servicio',
            'health_body' => 'El estado en vivo está disponible en',
            'language' => 'Idioma',
            'footer' => 'Operado por CyberGems · Creado para CyberSnap',
            'ttl_note' => 'Los enlaces caducan después de',
            'hours' => 'horas',
        ]
        : [
            'nav_label' => 'Navigation',
            'eyebrow' => 'TEMPORARY IMAGE SHARING',
            'title' => 'Share screenshots without friction.',
            'lead' => 'CyberSnap Share gives CyberSnap a fast, reliable place to publish an image and get a link that expires automatically.',
            'primary' => 'Visit CyberGems',
            'secondary' => 'How it works',
            'status' => 'Service ready for CyberSnap',
            'features_label' => 'Features',
            'features_title' => 'Made for quick, temporary sharing.',
            'feature_one_title' => 'Built for CyberSnap',
            'feature_one_body' => 'Optimized for screenshots and the CyberSnap desktop workflow.',
            'feature_two_title' => 'Temporary by design',
            'feature_two_body' => 'Public links expire automatically, then their image and metadata are removed.',
            'feature_three_title' => 'Simple delivery',
            'feature_three_body' => 'Upload through the API, open the generated link, and download the original when needed.',
            'how_label' => 'Workflow',
            'how_title' => 'From capture to link',
            'step_one_title' => 'Capture',
            'step_one_body' => 'Take a screenshot in CyberSnap and choose Share.',
            'step_two_title' => 'Upload',
            'step_two_body' => 'CyberSnap sends the image to this protected endpoint.',
            'step_three_title' => 'Share',
            'step_three_body' => 'You get a public link ready to paste wherever you need it.',
            'technical_title' => 'A focused endpoint, not a social network.',
            'technical_body' => 'This service exists to deliver short-lived public image links for CyberSnap. No account, feed, or tracking dashboard.',
            'health_label' => 'Service health',
            'health_body' => 'Live status is available at',
            'language' => 'Language',
            'footer' => 'Operated by CyberGems · Built for CyberSnap',
            'ttl_note' => 'Links expire after',
            'hours' => 'hours',
        ];
    $text = static fn(string $value): string => htmlspecialchars($value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
    $copy = array_map($text, $copy);
    $ttlNote = $copy['ttl_note'] . ' <strong>' . $ttl . ' ' . $copy['hours'] . '</strong>';
    $enClass = $lang === 'en' ? 'active' : '';
    $esClass = $lang === 'es' ? 'active' : '';
    $healthUrl = '/health';
    $cyberGemsUrl = 'https://cybergems.org';
    header('Content-Type: text/html; charset=utf-8');
    header('Cache-Control: public, max-age=300, stale-while-revalidate=60');
    echo <<<HTML
<!DOCTYPE html>
<html lang="{$lang}">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <meta name="description" content="{$copy['lead']}"/>
  <meta name="theme-color" content="#0b111d"/>
  <link rel="icon" href="/logo.png" type="image/png"/>
  <title>CyberSnap Share</title>
  <style>
    :root{color-scheme:dark;--bg:#0b111d;--panel:rgba(20,31,48,.78);--panel-strong:#13243a;--line:#263a52;--text:#edf6ff;--muted:#9eb2c8;--accent:#69d5e8;--accent-strong:#35bcd6}
    *{box-sizing:border-box}
    html{scroll-behavior:smooth}
    body{margin:0;font-family:Segoe UI,Inter,system-ui,sans-serif;background:radial-gradient(circle at 15% 0%,#18304b 0,transparent 36%),radial-gradient(circle at 100% 20%,#123c4d 0,transparent 30%),var(--bg);color:var(--text);min-height:100vh}
    a{color:inherit}
    .site-header,.site-shell,.site-footer{width:min(1100px,calc(100% - 40px));margin:0 auto}
    .site-header{display:flex;align-items:center;justify-content:space-between;gap:20px;padding:24px 0}
    .brand{display:inline-flex;align-items:center;gap:11px;text-decoration:none;font-weight:700;letter-spacing:.01em;color:var(--text)}
    .brand img{width:38px;height:38px;border-radius:11px;display:block;box-shadow:0 8px 24px rgba(0,0,0,.28)}
    .brand-accent{color:var(--accent)}
    .brand em{font-style:normal;color:var(--text);font-weight:500}
    nav{display:flex;align-items:center;gap:15px;color:var(--muted);font-size:.9rem}
    nav a{text-decoration:none}
    nav a:hover,nav a:focus-visible{color:var(--text)}
    .language{display:inline-flex;gap:5px;padding:4px;border:1px solid var(--line);border-radius:999px}
    .language a{padding:4px 8px;border-radius:999px;font-size:.78rem}
    .language .active{background:var(--accent);color:#08202a;font-weight:700}
    .hero{padding:84px 0 76px;max-width:800px}
    .eyebrow,.section-label{margin:0 0 16px;color:var(--accent);font-size:.75rem;font-weight:700;letter-spacing:.16em}
    h1{margin:0;max-width:760px;font-size:clamp(2.8rem,7vw,5.7rem);line-height:.98;letter-spacing:-.055em}
    .lead{max-width:640px;margin:28px 0 0;color:var(--muted);font-size:clamp(1.05rem,2vw,1.3rem);line-height:1.65}
    .hero-actions{display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin-top:34px}
    .button{display:inline-flex;align-items:center;justify-content:center;min-height:46px;padding:0 18px;border-radius:10px;text-decoration:none;font-weight:700;transition:transform .18s ease,background .18s ease,border-color .18s ease}
    .button:hover{transform:translateY(-2px)}
    .button.primary{background:var(--accent);color:#08202a;box-shadow:0 10px 25px rgba(53,188,214,.2)}
    .button.primary:hover{background:#8ce5f2}
    .button.secondary{border:1px solid var(--line);color:var(--text)}
    .button.secondary:hover{border-color:var(--accent);background:rgba(105,213,232,.08)}
    .status{display:inline-flex;align-items:center;gap:8px;margin-top:30px;color:var(--muted);font-size:.9rem}
    .status-dot{width:8px;height:8px;border-radius:50%;background:#52df9a;box-shadow:0 0 0 5px rgba(82,223,154,.12)}
    .section{padding:28px 0 82px}
    .section h2{margin:0;font-size:clamp(1.8rem,4vw,3rem);letter-spacing:-.035em}
    .feature-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:14px;margin-top:26px}
    .feature{padding:26px;border:1px solid var(--line);border-radius:16px;background:var(--panel);backdrop-filter:blur(12px)}
    .feature-number{display:inline-flex;width:30px;height:30px;align-items:center;justify-content:center;border-radius:9px;background:rgba(105,213,232,.12);color:var(--accent);font-size:.8rem;font-weight:800}
    .feature h3{margin:22px 0 10px;font-size:1.08rem}
    .feature p,.step p,.technical p{margin:0;color:var(--muted);line-height:1.6}
    .steps{display:grid;grid-template-columns:repeat(3,1fr);gap:0;margin-top:28px;border-top:1px solid var(--line)}
    .step{position:relative;padding:25px 24px 0 0}
    .step:not(:last-child){margin-right:24px;border-right:1px solid var(--line)}
    .step-number{color:var(--accent);font-size:.85rem;font-weight:800}
    .step h3{margin:14px 0 8px;font-size:1.05rem}
    .technical{display:flex;align-items:center;justify-content:space-between;gap:32px;padding:28px;border:1px solid var(--line);border-radius:16px;background:linear-gradient(135deg,rgba(23,57,77,.75),rgba(16,25,39,.8))}
    .technical h2{font-size:1.35rem;margin-bottom:10px}
    .health{flex:0 0 235px;padding:16px;border:1px solid rgba(105,213,232,.28);border-radius:12px;background:rgba(6,17,29,.5);font-size:.88rem}
    .health strong{display:block;margin-bottom:8px;color:var(--accent)}
    .health a{color:var(--text);font-family:ui-monospace,SFMono-Regular,Consolas,monospace;text-decoration:none}
    .health a:hover{text-decoration:underline}
    .ttl{margin:0;color:var(--muted);font-size:.9rem}.ttl strong{color:var(--text)}
    .site-footer{padding:0 0 28px;color:#7890a8;font-size:.85rem;text-align:center}
    :focus-visible{outline:3px solid var(--accent);outline-offset:3px}
    @media (max-width:760px){.site-header,.site-shell,.site-footer{width:min(100% - 28px,620px)}.site-header{padding-top:16px}.hero{padding:62px 0 56px}.feature-grid,.steps{grid-template-columns:1fr}.feature{padding:22px}.step{padding:20px 0;border-bottom:1px solid var(--line)}.step:not(:last-child){margin-right:0;border-right:0}.step:last-child{border-bottom:0}.technical{align-items:stretch;flex-direction:column;gap:22px}.health{flex-basis:auto}}
    @media (prefers-reduced-motion:reduce){html{scroll-behavior:auto}.button{transition:none}.button:hover{transform:none}}
  </style>
</head>
<body>
  <header class="site-header">
    <a class="brand" href="/" aria-label="CyberSnap Share">
      <img src="/logo.png" width="38" height="38" alt="CyberSnap"/>
      <span>Cyber<span class="brand-accent">Snap</span> <em>Share</em></span>
    </a>
    <nav aria-label="{$copy['nav_label']}">
      <a href="{$cyberGemsUrl}">CyberGems</a>
      <span aria-hidden="true">·</span>
      <span>{$copy['language']}</span>
      <span class="language">
        <a href="?lang=en" class="{$enClass}" lang="en">EN</a>
        <a href="?lang=es" class="{$esClass}" lang="es">ES</a>
      </span>
    </nav>
  </header>
  <main class="site-shell">
    <section class="hero" aria-labelledby="hero-title">
      <p class="eyebrow">{$copy['eyebrow']}</p>
      <h1 id="hero-title">{$copy['title']}</h1>
      <p class="lead">{$copy['lead']}</p>
      <div class="hero-actions">
        <a class="button primary" href="{$cyberGemsUrl}">{$copy['primary']}</a>
        <a class="button secondary" href="#how">{$copy['secondary']}</a>
      </div>
      <p class="status"><span class="status-dot" aria-hidden="true"></span>{$copy['status']}</p>
      <p class="ttl">{$ttlNote}</p>
    </section>

    <section class="section" aria-labelledby="features-title">
      <p class="section-label">{$copy['features_label']}</p>
      <h2 id="features-title">{$copy['features_title']}</h2>
      <div class="feature-grid">
        <article class="feature"><span class="feature-number">01</span><h3>{$copy['feature_one_title']}</h3><p>{$copy['feature_one_body']}</p></article>
        <article class="feature"><span class="feature-number">02</span><h3>{$copy['feature_two_title']}</h3><p>{$copy['feature_two_body']}</p></article>
        <article class="feature"><span class="feature-number">03</span><h3>{$copy['feature_three_title']}</h3><p>{$copy['feature_three_body']}</p></article>
      </div>
    </section>

    <section class="section" id="how" aria-labelledby="how-title">
      <p class="section-label">{$copy['how_label']}</p>
      <h2 id="how-title">{$copy['how_title']}</h2>
      <div class="steps">
        <article class="step"><span class="step-number">01</span><h3>{$copy['step_one_title']}</h3><p>{$copy['step_one_body']}</p></article>
        <article class="step"><span class="step-number">02</span><h3>{$copy['step_two_title']}</h3><p>{$copy['step_two_body']}</p></article>
        <article class="step"><span class="step-number">03</span><h3>{$copy['step_three_title']}</h3><p>{$copy['step_three_body']}</p></article>
      </div>
    </section>

    <section class="section" aria-labelledby="technical-title">
      <div class="technical">
        <div>
          <h2 id="technical-title">{$copy['technical_title']}</h2>
          <p>{$copy['technical_body']}</p>
        </div>
        <div class="health">
          <strong>{$copy['health_label']}</strong>
          <span>{$copy['health_body']} </span><a href="{$healthUrl}">/health</a>
        </div>
      </div>
    </section>
  </main>
  <footer class="site-footer">{$copy['footer']} · <span>{$base}</span></footer>
</body>
</html>
HTML;
}

function preferred_language(): string
{
    $requested = strtolower((string)($_GET['lang'] ?? ''));
    if ($requested === 'es' || $requested === 'en') {
        return $requested;
    }

    return str_starts_with(strtolower((string)($_SERVER['HTTP_ACCEPT_LANGUAGE'] ?? '')), 'es') ? 'es' : 'en';
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

    $writtenBytes = file_put_contents($filePath, $raw, LOCK_EX);
    if ($writtenBytes === false || $writtenBytes !== strlen($raw)) {
        @unlink($filePath);
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

    $metaJson = json_encode($meta, JSON_UNESCAPED_SLASHES);
    $metaBytes = $metaJson === false ? false : file_put_contents($metaPath, $metaJson, LOCK_EX);
    if ($metaBytes === false || $metaBytes !== strlen((string)$metaJson)) {
        @unlink($filePath);
        @unlink($metaPath);
        json_error(500, 'write_failed', 'Could not finalize image metadata.');
    }
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
    $lang = preferred_language();

    if (!is_valid_id($id)) {
        render_missing_page($lang, false);
    }

    $meta = load_meta($metaDir, $id);
    if ($meta === null || is_expired($meta)) {
        if ($meta !== null) {
            delete_share($filesDir, $metaDir, $meta);
        }
        render_missing_page($lang, true);
    }

    $filePath = $filesDir . '/' . $meta['file'];
    if (!is_file($filePath)) {
        render_missing_page($lang, true);
    }

    $base = rtrim((string)$config['public_base_url'], '/');
    $fileUrl = htmlspecialchars($base . '/f/' . $id, ENT_QUOTES, 'UTF-8');
    $viewUrl = $base . '/' . $id;
    $w = (int)($meta['width'] ?? 0);
    $h = (int)($meta['height'] ?? 0);
    $bytes = (int)($meta['bytes'] ?? 0);
    $fmt = htmlspecialchars(strtoupper(pathinfo((string)$meta['file'], PATHINFO_EXTENSION)), ENT_QUOTES, 'UTF-8');
    $sizeLabel = htmlspecialchars(format_bytes($bytes), ENT_QUOTES, 'UTF-8');
    $dims = ($w > 0 && $h > 0) ? $w . ' × ' . $h . ' px' : '';
    $expiresAt = (int)$meta['expires_at'];
    $createdAt = (int)($meta['created_at'] ?? 0);
    $uploadedLabel = $createdAt > 0 ? gmdate('Y-m-d H:i', $createdAt) . ' UTC' : '';
    $exp = gmdate('Y-m-d H:i', $expiresAt) . ' UTC';
    // Computed once per page view from stored expires_at — no extra I/O or API.
    // The inline script below keeps the label fresh client-side (page cache is short).
    $remainingLabel = format_remaining(max(0, $expiresAt - time()));

    $rawCopy = viewer_copy($lang);
    $text = static fn(string $value): string => htmlspecialchars($value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
    $copy = array_map($text, $rawCopy);
    $appUrl = app_page_url($lang);
    $footer = share_footer($lang);
    $enClass = $lang === 'en' ? 'active' : '';
    $esClass = $lang === 'es' ? 'active' : '';
    $cfgJson = json_encode(
        [
            'expires_at' => $expiresAt,
            'url' => $viewUrl,
            'i18n' => [
                'expired' => $rawCopy['expired'],
                'less' => $rawCopy['less_than_a_minute'],
                'copied' => $rawCopy['copied'],
            ],
        ],
        JSON_UNESCAPED_UNICODE | JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT
    ) ?: '{}';

    $chipFormat = '<span class="chip"><strong>' . $fmt . '</strong></span>';
    $chipDims = $dims !== '' ? '<span class="chip">' . $dims . '</span>' : '';
    $chipSize = '<span class="chip">' . $sizeLabel . '</span>';
    $chipUploaded = $uploadedLabel !== '' ? '<span class="chip">' . $copy['uploaded'] . ' ' . $uploadedLabel . '</span>' : '';

    header('Content-Type: text/html; charset=utf-8');
    // Short cache so "hours left" stays roughly fresh without hammering origin.
    header('Cache-Control: public, max-age=120');
    echo <<<HTML
<!DOCTYPE html>
<html lang="{$lang}">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <meta name="robots" content="noindex,nofollow"/>
  <meta name="theme-color" content="#0b111d"/>
  <link rel="icon" href="/logo.png" type="image/png"/>
  <title>{$copy['title']}</title>
  <style>
    :root{color-scheme:dark;--bg:#0b111d;--panel:rgba(20,31,48,.78);--line:#263a52;--text:#edf6ff;--muted:#9eb2c8;--accent:#69d5e8}
    *{box-sizing:border-box}
    body{margin:0;font-family:Segoe UI,Inter,system-ui,sans-serif;background:radial-gradient(circle at 15% 0%,#18304b 0,transparent 36%),radial-gradient(circle at 100% 20%,#123c4d 0,transparent 30%),var(--bg);color:var(--text);min-height:100vh;display:flex;flex-direction:column;align-items:center;padding:20px 16px 40px}
    a{color:inherit}
    .site-header{width:min(1000px,100%);display:flex;align-items:center;justify-content:space-between;gap:16px;margin-bottom:22px;flex-wrap:wrap}
    .brand{display:inline-flex;align-items:center;gap:11px;text-decoration:none;font-weight:700;letter-spacing:.01em;color:var(--text)}
    .brand img{width:34px;height:34px;border-radius:10px;display:block;box-shadow:0 8px 24px rgba(0,0,0,.28)}
    .brand-accent{color:var(--accent)}
    .brand em{font-style:normal;color:var(--text);font-weight:500}
    .language{display:inline-flex;gap:5px;padding:4px;border:1px solid var(--line);border-radius:999px}
    .language a{padding:4px 8px;border-radius:999px;font-size:.78rem;text-decoration:none;color:var(--muted)}
    .language a:hover,.language a:focus-visible{color:var(--text)}
    .language .active{background:var(--accent);color:#08202a;font-weight:700}
    main{width:min(1000px,100%)}
    .toolbar{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;margin-bottom:14px}
    .chips{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
    .chip{padding:5px 11px;border:1px solid var(--line);border-radius:999px;color:var(--muted);font-size:.78rem;background:rgba(11,17,29,.45)}
    .chip strong{color:var(--text);font-weight:600}
    .actions{display:flex;gap:10px}
    .button{display:inline-flex;align-items:center;justify-content:center;min-height:46px;padding:0 18px;border:1px solid transparent;border-radius:10px;text-decoration:none;font-weight:700;font-size:.95rem;cursor:pointer;transition:transform .18s ease,background .18s ease,border-color .18s ease}
    .button.sm{min-height:40px;padding:0 14px;font-size:.88rem}
    .button.primary{background:var(--accent);color:#08202a;box-shadow:0 10px 25px rgba(53,188,214,.2)}
    .button.primary:hover{background:#8ce5f2}
    .button.secondary{border-color:var(--line);color:var(--text);background:rgba(11,17,29,.35)}
    .button.secondary:hover{border-color:var(--accent);background:rgba(105,213,232,.08)}
    .button:hover{transform:translateY(-2px)}
    .frame{display:block;border:1px solid var(--line);border-radius:16px;background:var(--panel);backdrop-filter:blur(12px);padding:12px;box-shadow:0 8px 40px rgba(0,0,0,.45)}
    .frame img{display:block;max-width:100%;height:auto;margin:0 auto;border-radius:6px}
    .expiry{margin:18px 0 0;color:var(--muted);font-size:.92rem;line-height:1.6}
    .expiry strong{color:var(--text)}
    .privacy{margin:6px 0 0;color:#7890a8;font-size:.85rem;line-height:1.5}
    .cta{display:flex;align-items:center;justify-content:space-between;gap:22px;margin-top:30px;padding:26px;border:1px solid var(--line);border-radius:16px;background:linear-gradient(135deg,rgba(23,57,77,.75),rgba(16,25,39,.8))}
    .cta h2{margin:0 0 8px;font-size:1.3rem;letter-spacing:-.02em}
    .cta p{margin:0;color:var(--muted);line-height:1.6}
    .cta-actions{display:flex;gap:10px;flex-wrap:wrap;flex-shrink:0}
    .site-footer{margin-top:34px;color:#7890a8;font-size:.85rem;text-align:center}
    .site-footer a{color:inherit}
    .site-footer a:hover,.site-footer a:focus-visible{color:var(--text)}
    :focus-visible{outline:3px solid var(--accent);outline-offset:3px}
    @media (max-width:760px){.toolbar{flex-direction:column;align-items:stretch}.chips{justify-content:center}.actions{width:100%}.actions .button{flex:1}.cta{flex-direction:column;align-items:stretch;text-align:center}.cta-actions{justify-content:center}}
    @media (prefers-reduced-motion:reduce){.button{transition:none}.button:hover{transform:none}}
  </style>
</head>
<body>
  <header class="site-header">
    <a class="brand" href="/" aria-label="CyberSnap Share">
      <img src="/logo.png" width="34" height="34" alt="CyberSnap"/>
      <span>Cyber<span class="brand-accent">Snap</span> <em>Share</em></span>
    </a>
    <nav aria-label="{$copy['language']}">
      <span class="language">
        <a href="?lang=en" class="{$enClass}" lang="en" hreflang="en">EN</a>
        <a href="?lang=es" class="{$esClass}" lang="es" hreflang="es">ES</a>
      </span>
    </nav>
  </header>
  <main>
    <div class="toolbar">
      <div class="chips" aria-label="{$copy['details']}">
        {$chipFormat}{$chipDims}{$chipSize}{$chipUploaded}
      </div>
      <div class="actions">
        <button class="button secondary sm" id="copy-link" type="button">{$copy['copy_link']}</button>
        <a class="button primary sm" href="{$fileUrl}" download>{$copy['download']}</a>
      </div>
    </div>
    <a class="frame" href="{$fileUrl}" target="_blank" rel="noopener" title="{$copy['open_original']}">
      <img src="{$fileUrl}" alt="{$copy['alt']}"/>
    </a>
    <p class="expiry">{$copy['expires_in']} <strong id="remaining">{$remainingLabel}</strong> · {$exp}</p>
    <p class="privacy">{$copy['privacy']}</p>
    <section class="cta" aria-labelledby="cta-title">
      <div>
        <h2 id="cta-title">{$copy['cta_title']}</h2>
        <p>{$copy['cta_body']}</p>
      </div>
      <div class="cta-actions">
        <a class="button primary" href="{$appUrl}" target="_blank" rel="noopener">{$copy['get_app']}</a>
        <a class="button secondary" href="/">{$copy['about_service']}</a>
      </div>
    </section>
  </main>
  <footer class="site-footer">{$footer}</footer>
  <script>
    (function () {
      var cfg = {$cfgJson};
      var remaining = document.getElementById('remaining');
      if (remaining) {
        var fmt = function (s) {
          if (s <= 0) return cfg.i18n.expired;
          var h = Math.floor(s / 3600);
          var m = Math.floor((s % 3600) / 60);
          if (h >= 1) return m > 0 ? h + ' h ' + m + ' min' : h + ' h';
          if (m >= 1) return m + ' min';
          return cfg.i18n.less;
        };
        var tick = function () {
          remaining.textContent = fmt(cfg.expires_at - Math.floor(Date.now() / 1000));
        };
        tick();
        setInterval(tick, 30000);
      }
      var btn = document.getElementById('copy-link');
      if (btn) {
        btn.addEventListener('click', function () {
          var label = btn.textContent;
          var done = function () {
            btn.textContent = cfg.i18n.copied;
            setTimeout(function () { btn.textContent = label; }, 2000);
          };
          var fallback = function () {
            var ta = document.createElement('textarea');
            ta.value = cfg.url;
            ta.setAttribute('readonly', '');
            ta.style.position = 'fixed';
            ta.style.left = '-9999px';
            document.body.appendChild(ta);
            ta.select();
            try { document.execCommand('copy'); done(); } catch (e) {}
            document.body.removeChild(ta);
          };
          if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(cfg.url).then(done, fallback);
          } else {
            fallback();
          }
        });
      }
    })();
  </script>
</body>
</html>
HTML;
}

function viewer_copy(string $lang): array
{
    return $lang === 'es'
        ? [
            'title' => 'Imagen compartida · CyberSnap Share',
            'language' => 'Idioma',
            'details' => 'Detalles de la imagen',
            'uploaded' => 'Subida',
            'copy_link' => 'Copiar enlace',
            'copied' => 'Copiado',
            'download' => 'Descargar',
            'open_original' => 'Abrir la imagen original a tamaño completo',
            'alt' => 'Imagen compartida',
            'expires_in' => 'Caduca en',
            'privacy' => 'Este enlace caduca automáticamente y la imagen se elimina de forma permanente después.',
            'cta_title' => 'Compartido con CyberSnap',
            'cta_body' => 'CyberSnap es una herramienta gratuita de capturas de pantalla para Windows. Captura, anota y comparte con enlaces que caducan solos.',
            'get_app' => 'Obtener CyberSnap',
            'about_service' => 'Sobre este servicio',
            'expired' => 'Caducado',
            'less_than_a_minute' => 'menos de 1 min',
        ]
        : [
            'title' => 'Shared image · CyberSnap Share',
            'language' => 'Language',
            'details' => 'Image details',
            'uploaded' => 'Uploaded',
            'copy_link' => 'Copy link',
            'copied' => 'Copied',
            'download' => 'Download',
            'open_original' => 'Open the original image at full size',
            'alt' => 'Shared image',
            'expires_in' => 'Expires in',
            'privacy' => 'This link expires automatically and the image is permanently deleted afterwards.',
            'cta_title' => 'Shared with CyberSnap',
            'cta_body' => 'CyberSnap is a free screenshot tool for Windows. Capture, annotate, and share with links that expire on their own.',
            'get_app' => 'Get CyberSnap',
            'about_service' => 'About this service',
            'expired' => 'Expired',
            'less_than_a_minute' => 'less than 1 min',
        ];
}

function missing_page_copy(string $lang, bool $expired): array
{
    if ($lang === 'es') {
        $copy = [
            'title' => 'Enlace no disponible · CyberSnap Share',
            'eyebrow' => $expired ? 'ENLACE CADUCADO' : 'ENLACE NO ENCONTRADO',
            'heading' => $expired ? 'Este enlace ha caducado.' : 'Este enlace no existe.',
            'body' => 'Los enlaces de CyberSnap Share son temporales: cuando un enlace caduca, su imagen y sus metadatos se eliminan de forma permanente.',
            'hint' => $expired
                ? 'Si aún necesitas la imagen, pide a quien la envió que la comparta de nuevo.'
                : 'Verifica que el enlace esté completo y no se haya cortado.',
            'language' => 'Idioma',
            'get_app' => 'Obtener CyberSnap',
            'about_service' => 'Sobre este servicio',
        ];
    } else {
        $copy = [
            'title' => 'Link unavailable · CyberSnap Share',
            'eyebrow' => $expired ? 'EXPIRED LINK' : 'LINK NOT FOUND',
            'heading' => $expired ? 'This link has expired.' : 'This link does not exist.',
            'body' => 'CyberSnap Share links are temporary: when a link expires, its image and metadata are permanently deleted.',
            'hint' => $expired
                ? 'If you still need the image, ask the sender to share it again.'
                : 'Check that the link is complete and was not truncated.',
            'language' => 'Language',
            'get_app' => 'Get CyberSnap',
            'about_service' => 'About this service',
        ];
    }
    return $copy;
}

/** CyberSnap app page on the main CyberGems site (separate origin from this service). */
function app_page_url(string $lang): string
{
    return $lang === 'es'
        ? 'https://cybergems.org/es/apps/cybersnap'
        : 'https://cybergems.org/apps/cybersnap';
}

function share_footer(string $lang): string
{
    $text = static fn(string $value): string => htmlspecialchars($value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
    $by = $text($lang === 'es' ? 'Operado por' : 'Operated by');
    $for = $text($lang === 'es' ? 'Creado para CyberSnap' : 'Built for CyberSnap');
    return $by . ' <a href="https://cybergems.org" target="_blank" rel="noopener">CyberGems</a> · ' . $for;
}

function render_missing_page(string $lang, bool $expired): void
{
    $copy = missing_page_copy($lang, $expired);
    $text = static fn(string $value): string => htmlspecialchars($value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
    $copy = array_map($text, $copy);
    $appUrl = app_page_url($lang);
    $footer = share_footer($lang);
    $enClass = $lang === 'en' ? 'active' : '';
    $esClass = $lang === 'es' ? 'active' : '';

    http_response_code(404);
    header('Content-Type: text/html; charset=utf-8');
    header('Cache-Control: no-store');
    echo <<<HTML
<!DOCTYPE html>
<html lang="{$lang}">
<head>
  <meta charset="utf-8"/>
  <meta name="viewport" content="width=device-width, initial-scale=1"/>
  <meta name="robots" content="noindex,nofollow"/>
  <meta name="theme-color" content="#0b111d"/>
  <link rel="icon" href="/logo.png" type="image/png"/>
  <title>{$copy['title']}</title>
  <style>
    :root{color-scheme:dark;--bg:#0b111d;--panel:rgba(20,31,48,.78);--line:#263a52;--text:#edf6ff;--muted:#9eb2c8;--accent:#69d5e8}
    *{box-sizing:border-box}
    body{margin:0;font-family:Segoe UI,Inter,system-ui,sans-serif;background:radial-gradient(circle at 15% 0%,#18304b 0,transparent 36%),radial-gradient(circle at 100% 20%,#123c4d 0,transparent 30%),var(--bg);color:var(--text);min-height:100vh;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:24px;padding:24px 16px}
    a{color:inherit}
    .brand{display:inline-flex;align-items:center;gap:11px;text-decoration:none;font-weight:700;letter-spacing:.01em;color:var(--text)}
    .brand img{width:38px;height:38px;border-radius:11px;display:block;box-shadow:0 8px 24px rgba(0,0,0,.28)}
    .brand-accent{color:var(--accent)}
    .brand em{font-style:normal;color:var(--text);font-weight:500}
    main{max-width:560px;width:100%;text-align:center;border:1px solid var(--line);border-radius:16px;background:var(--panel);backdrop-filter:blur(12px);padding:44px 34px}
    .eyebrow{margin:0 0 16px;color:var(--accent);font-size:.75rem;font-weight:700;letter-spacing:.16em}
    h1{margin:0 0 14px;font-size:clamp(1.9rem,5vw,2.8rem);letter-spacing:-.04em;line-height:1.1}
    .body{margin:0;color:var(--muted);line-height:1.65}
    .hint{margin:10px 0 0;color:#7890a8;font-size:.88rem;line-height:1.5}
    .actions{display:flex;gap:10px;justify-content:center;flex-wrap:wrap;margin-top:28px}
    .button{display:inline-flex;align-items:center;justify-content:center;min-height:46px;padding:0 18px;border:1px solid transparent;border-radius:10px;text-decoration:none;font-weight:700;font-size:.95rem;transition:transform .18s ease,background .18s ease,border-color .18s ease}
    .button.primary{background:var(--accent);color:#08202a;box-shadow:0 10px 25px rgba(53,188,214,.2)}
    .button.primary:hover{background:#8ce5f2}
    .button.secondary{border-color:var(--line);color:var(--text)}
    .button.secondary:hover{border-color:var(--accent);background:rgba(105,213,232,.08)}
    .button:hover{transform:translateY(-2px)}
    .language{display:inline-flex;gap:5px;padding:4px;border:1px solid var(--line);border-radius:999px;margin-top:20px}
    .language a{padding:4px 8px;border-radius:999px;font-size:.78rem;text-decoration:none;color:var(--muted)}
    .language a:hover,.language a:focus-visible{color:var(--text)}
    .language .active{background:var(--accent);color:#08202a;font-weight:700}
    .site-footer{color:#7890a8;font-size:.85rem;text-align:center}
    .site-footer a{color:inherit}
    .site-footer a:hover,.site-footer a:focus-visible{color:var(--text)}
    :focus-visible{outline:3px solid var(--accent);outline-offset:3px}
    @media (max-width:480px){main{padding:34px 22px}}
    @media (prefers-reduced-motion:reduce){.button{transition:none}.button:hover{transform:none}}
  </style>
</head>
<body>
  <a class="brand" href="/" aria-label="CyberSnap Share">
    <img src="/logo.png" width="38" height="38" alt="CyberSnap"/>
    <span>Cyber<span class="brand-accent">Snap</span> <em>Share</em></span>
  </a>
  <main>
    <p class="eyebrow">{$copy['eyebrow']}</p>
    <h1>{$copy['heading']}</h1>
    <p class="body">{$copy['body']}</p>
    <p class="hint">{$copy['hint']}</p>
    <div class="actions">
      <a class="button primary" href="{$appUrl}" target="_blank" rel="noopener">{$copy['get_app']}</a>
      <a class="button secondary" href="/">{$copy['about_service']}</a>
    </div>
    <nav aria-label="{$copy['language']}">
      <span class="language">
        <a href="?lang=en" class="{$enClass}" lang="en" hreflang="en">EN</a>
        <a href="?lang=es" class="{$esClass}" lang="es" hreflang="es">ES</a>
      </span>
    </nav>
  </main>
  <footer class="site-footer">{$footer}</footer>
</body>
</html>
HTML;
    exit;
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
    // Keep CDN/browser copies short-lived so they do not materially outlive the share TTL.
    header('Cache-Control: public, max-age=300, s-maxage=300');
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
