# CyberSnap Share

Temporary public image hosting for CyberSnap.

**Production host:** `https://cybersnap.cybergems.org/{id}`  
**TTL:** 48 hours (configurable)  
**Stack:** PHP on cPanel / LiteSpeed first; same layout on VPS later.

Designed so CyberSnap needs **no client change** when you move hosts—only the optional **Share server URL** + API key in Settings (or OOTB vault).

---

## Features

| Route | Purpose |
|--------|---------|
| `GET /` | Landing page |
| `GET /health` | JSON liveness (`ok`, `ttl_hours`) |
| `POST /v1/upload` | Upload (Bearer or `X-CyberSnap-Key`) |
| `GET /{id}` | Viewer (logo, meta, remaining time, download) |
| `GET /f/{id}` | Raw image bytes |
| Cron `cleanup.php` | Deletes expired shares + old rate files |

- PNG / JPEG / WebP only (magic bytes)
- Opaque random ids (not sequential)
- Size + per-IP rate limits
- Static assets in `public/` (e.g. `logo.png`)

---

## Directory layout (critical)

```text
cybersnap.cybergems.org/          ← account folder (NOT the web root)
  config.php                      ← secrets (never commit, never web-serve)
  cleanup.php
  storage/                        ← files/, meta/, rate/ (writable by PHP)
  public/                         ← ★ Document Root of the subdomain
    index.php
    .htaccess
    logo.png
```

**Document Root must be `…/public`, not the parent folder.**

### cPanel Document Root (common pitfall)

In Domains → Document Root, use the path **relative to the account home**:

```text
cybersnap.cybergems.org/public
```

| Wrong | Why |
|--------|-----|
| `home/user/cybersnap…/public` | Doubles the home path → 404 everywhere |
| Tipographic dash `cybersnap—share` | Folder is `cybersnap-share` with ASCII `-` |
| Parent folder without `/public` | Serves wrong tree / empty vhost |

Full absolute form (if the UI accepts it):

```text
/home/USER/cybersnap.cybergems.org/public
```

---

## cPanel deploy checklist

1. **Subdomain** `cybersnap.cybergems.org` (or your host).
2. Upload this package so layout matches above (or `public_html/…` + point docroot at `public/`).
3. Copy `config.example.php` → **`config.php`** next to `public/` (not inside it).
4. Set in `config.php`:
   - `api_key` — long random secret  
     `php -r "echo bin2hex(random_bytes(32)), PHP_EOL;"`
   - `public_base_url` — `https://cybersnap.cybergems.org` (no trailing slash)
   - `storage_path` — usually `__DIR__ . '/storage'`
   - `ttl_hours` — `48`
5. **SSL:** AutoSSL / Let’s Encrypt for the subdomain (green padlock). Edge may cache old certs; Firefox is a good check.
6. **Permissions:** PHP must write `storage/` (and create `files/`, `meta/`, `rate/`).
7. **Cron (hourly)** — use the PHP path your host documents (cPanel often shows `/usr/local/bin/php`, not always `/usr/bin/php`):

   ```bash
   0 * * * * /usr/local/bin/php /home/USER/cybersnap.cybergems.org/cleanup.php
   ```

   Manual test (Terminal / SSH):

   ```bash
   /usr/local/bin/php /home/USER/cybersnap.cybergems.org/cleanup.php
   # expect: … cleanup: deleted=N
   ```

8. Smoke tests after deploy:

   | URL | Expected |
   |-----|----------|
   | `https://HOST/` | Landing “CyberSnap Share” + logo |
   | `https://HOST/health` | `{"ok":true,"service":"cybersnap-share",…}` |
   | `https://HOST/index.php?route=health` | Same (if rewrite fails, this still works) |
   | `https://HOST/logo.png` | Logo image |

If **everything** 404s (including `/` and `index.php`), Document Root is wrong.  
If only `/health` 404s but `index.php?route=health` works, fix rewrite / `.htaccess` / `AllowOverride`.

---

## Auth

Upload requires one of:

```http
Authorization: Bearer YOUR_API_KEY
```

or (when hosts strip Authorization):

```http
X-CyberSnap-Key: YOUR_API_KEY
```

CyberSnap sends **both**. Optional `api_keys_extra` in `config.php` allows rotation / multiple app builds without downtime.

---

## API

```http
POST /v1/upload
Authorization: Bearer YOUR_API_KEY
Content-Type: multipart/form-data

image=@file.png
```

Or raw body with `Content-Type: image/png` (or jpeg/webp).

**Success (200):**

```json
{
  "ok": true,
  "id": "E6LL4CvRTUe",
  "url": "https://cybersnap.cybergems.org/E6LL4CvRTUe",
  "file_url": "https://cybersnap.cybergems.org/f/E6LL4CvRTUe",
  "expires_at": 1710000000,
  "ttl_hours": 48
}
```

Viewer footer shows remaining time from stored `expires_at` (computed on each page view; no extra jobs).

---

## CyberSnap client

| Item | Detail |
|------|--------|
| UI name | **CyberSnap Share** |
| Settings tab | **CyberSnap** (Uploads) |
| Enum | `UploadProviderKind.CyberGems` |
| Default | CyberSnap Share when a key exists; else **ImgBB** |
| Official base | `https://cybersnap.cybergems.org` (empty “Share server URL”) |
| Env | `CYBERSNAP_SHARE_API_KEY` overrides embedded OOTB |
| Vault | `scripts/Protect-UploadVaultKey.ps1 -ApiKey "…" -ApiKeyEnv CYBERSNAP_SHARE_API_KEY` → paste into `EmbeddedCyberGemsCiphertextBase64` (PowerShell **7+** / `pwsh`) |
| Confirm dialog | Public link, ~48h, optional “don’t ask again” |

### Custom “Share server URL” (advanced / self-host)

Most users leave it **empty**. Power users / self-hosters set the **origin only** (no path):

| Correct | Wrong |
|---------|--------|
| `https://share.example.com` | `https://share.example.com/v1/upload` |
| `https://cybersnap.cybergems.org` | `https://cybersnap.cybergems.org/AbCd123` |

Then paste **that server’s** `api_key` (not the official one unless it is the same key).

Example: company deploys this package on `https://share.acme.com` → client URL `https://share.acme.com` + Acme’s key → links look like `https://share.acme.com/{id}`.

---

## VPS / later

- Point the same hostname at Nginx/Apache → `public/`
- Keep paths (`/v1/upload`, `/{id}`, `/f/{id}`, `/health`) so clients need no code change
- Prefer absolute `storage_path` if the process cwd is not the package root
- Optional Docker: mount `storage/`, inject `api_key` via env-generated `config.php`

---

## What to commit

**Safe:** `public/`, `cleanup.php`, `config.example.php`, README, `.gitignore`, empty `storage/.gitkeep`

**Never commit:**

- `config.php` (API key)
- `storage/files/`, `storage/meta/`, `storage/rate/` (uploads + rate data)

Ignored by `services/cybersnap-share/.gitignore` and root `.gitignore`.

---

## Ops / security checklist

- [x] API key required for upload  
- [x] Magic-byte image validation  
- [x] Max size + rate limits  
- [x] No directory listing; static logo allowed  
- [x] TTL + hourly cleanup  
- [x] Viewer remaining-time label (from `expires_at`)  
- [ ] Rotate `api_key` if leaked (update `config.php` + OOTB vault / Settings)  
- [ ] Monitor disk under `storage/files/` on shared hosting  
- [ ] Confirm SSL for the subdomain after DNS changes  
- [ ] After key rotation: re-test **Test CyberSnap Share** in the app  

---

## Quick recovery (from past incidents)

| Symptom | Likely cause |
|---------|----------------|
| 404 on `/`, `/health`, `index.php` | Document Root not `…/public` or wrong path spelling |
| 503 / missing config | No `config.php` next to `public/` |
| 401 on upload | App key ≠ `config.php` `api_key` |
| Cron silent / no deletes | Wrong PHP binary (`/usr/local/bin/php` vs `/usr/bin/php`) |
| Logo 404 | Missing `public/logo.png` or rewrite blocking static files |
| Edge “Not secure”, Firefox OK | Cached cert / chain; re-run AutoSSL, try InPrivate |
