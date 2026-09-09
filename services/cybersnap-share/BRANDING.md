# CyberSnap Share branding

This service has a deployed copy of the CyberSnap logo. Keep it synchronized with the application brand asset whenever the icon or logo changes.

## Source of truth

| Role | Path |
|------|------|
| Canonical CyberSnap brand asset | `src/CyberSnap/Assets/CyberSnap_square.png` |
| CyberSnap Share web copy | `services/cybersnap-share/public/logo.png` |

The web service intentionally keeps the stable filename `logo.png`: the landing page, viewer, favicon, and deployment checklist all depend on it. Do not replace it with a second, service-specific logo.

## When the CyberSnap icon changes

Update the canonical application asset first, then refresh the service copy in the same change:

```powershell
Copy-Item -LiteralPath src/CyberSnap/Assets/CyberSnap_square.png `
  -Destination services/cybersnap-share/public/logo.png -Force
./scripts/Test-CyberSnapShareBrand.ps1
```

Before committing, confirm that `public/index.php` still references `/logo.png` in both the landing page and viewer. If the asset changes format or filename, update those references, `.htaccess`, and this document together.

## Agent guardrail

Any task that changes CyberSnap branding must check this file and include the synchronized `public/logo.png` copy. A brand change is incomplete if the desktop application is updated but CyberSnap Share still shows the previous mark.
