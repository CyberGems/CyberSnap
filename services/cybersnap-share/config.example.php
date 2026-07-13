<?php
/**
 * CyberSnap Share — copy to config.php on the server and fill in secrets.
 * config.php must NOT be web-accessible (place outside public/ or deny via .htaccess).
 */
return [
    // Required: long random secret. CyberSnap sends it as Bearer token.
    // Generate: php -r "echo bin2hex(random_bytes(32)), PHP_EOL;"
    'api_key' => 'CHANGE_ME_TO_A_LONG_RANDOM_SECRET',

    // Public base URL (no trailing slash). Example: https://cybersnap.cybergems.org
    'public_base_url' => 'https://cybersnap.cybergems.org',

    // Storage root (absolute path recommended on VPS; relative to this file on cPanel).
    'storage_path' => __DIR__ . '/storage',

    // TTL hours (24 or 48).
    'ttl_hours' => 48,

    // Max upload size in bytes (15 MiB).
    'max_bytes' => 15 * 1024 * 1024,

    // Rate limits (per client IP, rolling window).
    'rate_limit_per_minute' => 10,
    'rate_limit_per_day' => 80,

    // Optional extra allowed app keys (rotation / multi-build).
    'api_keys_extra' => [
        // 'another-key-here',
    ],
];
