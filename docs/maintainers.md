# Maintainer guide

**English** · [Español](maintainers.es.md)

Release and CI internals for CyberSnap maintainers. The user-facing build steps stay in the [README](../README.md).

## Installer verification

Run [Verify Windows installer](https://github.com/CyberGems/CyberSnap/actions/workflows/verify-installer.yml) manually to check installation, startup at a real Windows logon, and uninstallation in a disposable Windows VM. The checks use standard accounts with UAC credential prompts enabled and retain the installer and diagnostic logs for seven days as Actions artifacts, without creating a release. The optional `installer_run_id` reuses an earlier test installer only when packaging sources are unchanged. `scripts/Test-Installer.ps1` is intended exclusively for those hosted VMs and rejects normal local execution.

## About icon synchronization

Before compiling each release, CI refreshes the About icons from the official app repositories listed in `scripts/SuiteIconSources.json`. It checks catalog coverage, PNG decoding, file size, and square dimensions for the entire batch before replacing any image. A missing or invalid source stops the release; source URLs and SHA256 hashes appear in the job summary. The [icon verification workflow](https://github.com/CyberGems/CyberSnap/actions/workflows/verify-suite-icons.yml) tests this process without publishing a release.

When preparing a release locally, run `pwsh -NoProfile -File scripts/Sync-SuiteIcons.ps1` before building and tagging. Review the changed PNGs and include them in the release preparation commit. Add `-Check` to detect stale copies without changing files. For offline development, `-Source Local` reads the canonical assets in sibling app checkouts (or `-ProjectsRoot <folder>`).

The reusable Windows action at `.github/actions/sync-suite-icons` supports the other suite apps through a repository-relative JSON configuration (`config` input). Its `destinations` list names the icon folders, and `catalogs` lists source files and regular expressions with the app slug in capture group 1. Optional `start` / `end` markers scope a rule to the About list; `exclude` matches entries filtered out by the UI, such as the current app in a shared catalog. The shared source registry must cover every displayed app. Callers pin the action to a reviewed commit; all images are validated before any destination changes.
