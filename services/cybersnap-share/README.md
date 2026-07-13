# CyberSnap Share

Temporary public image hosting for CyberSnap at **`https://cybersnap.cybergems.org/{id}`**.

Designed for **cPanel shared hosting first**, same layout on **VPS** later (no CyberSnap client changes beyond base URL if needed).

## Features

- `POST /v1/upload` — Bearer API key, PNG/JPEG/WebP only, size + rate limits
- `GET /{id}` — viewer page (format, size, download)
- `GET /f/{id}` — raw image
- Cron cleanup after **48 hours** (configurable)
- Opaque random ids (not sequential)

## cPanel deploy

1. Create subdomain **`cybersnap.cybergems.org`** → document root must be the **`public/`** folder of this service.
2. Upload this directory (e.g. `~/cybersnap-share/`) so that:
   - `public/` = subdomain docroot  
   - `config.php`, `cleanup.php`, `storage/` sit **outside** the web root if possible (or protected).
3. Copy `config.example.php` → `config.php` (parent of `public/`).
4. Set a long `api_key` and correct `public_base_url`.
5. Enable **SSL** (Let’s Encrypt).
6. Cron (hourly):
   ```bash
   /usr/bin/php /home/USER/cybersnap-share/cleanup.php
   ```
7. Ensure `storage/` is writable by PHP.

### Apache note

`public/.htaccess` routes all traffic. If Authorization header is stripped, the client also sends `X-CyberSnap-Key`.

## VPS later

- Point the same subdomain at Nginx/Apache → `public/`
- Or Docker: mount `storage/`, env for `api_key`
- Keep the same URL paths so CyberSnap needs no code change

## API

```http
POST /v1/upload
Authorization: Bearer YOUR_API_KEY
Content-Type: multipart/form-data

image=@file.png
```

Or raw body with `Content-Type: image/png`.

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

## CyberSnap client

- UI name: **CyberSnap Share** (provider enum: `UploadProviderKind.CyberGems`)
- Default when a share key is available; falls back to **ImgBB** otherwise
- Settings → Uploads → **CyberGems** tab: base URL + optional custom API key + test
- Env override: `CYBERSNAP_SHARE_API_KEY`
- Embed OOTB key: `.\scripts\Protect-UploadVaultKey.ps1 -ApiKey "…" -ApiKeyEnv CYBERSNAP_SHARE_API_KEY`
  → paste into `EmbeddedCyberGemsCiphertextBase64` in `DefaultCredentialVault.cs`
- Share confirm dialog: public link, 48h TTL, optional don't-ask-again

## What to commit

Safe to push this folder to GitHub (`config.example.php`, `public/`, `cleanup.php`, README).

**Never commit:**

- `config.php` (API key)
- `storage/files/`, `storage/meta/`, `storage/rate/` (user uploads)

Root `.gitignore` and `services/cybersnap-share/.gitignore` ignore those.

## Security checklist

- [x] API key required for upload  
- [x] Magic-byte image validation  
- [x] Max size  
- [x] Rate limit per IP  
- [x] No directory listing  
- [x] TTL deletion  
- [ ] Rotate API key if leaked  
- [ ] Monitor disk usage on shared hosting  
