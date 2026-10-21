# Winget packaging

The release build generates the three `CyberGems.CyberSnap*.yaml` manifests from
the published Inno Setup installer URL and SHA256, validates them, and attaches
them to the GitHub release alongside the installer and portable ZIP.

## Automatic submission

The **Submit CyberSnap to winget** workflow runs when a stable release is
published. It can also be run manually with a published release tag, without
rebuilding the application. It requires `WINGET_PAT` with `public_repo` access and
an existing fork of `microsoft/winget-pkgs` owned by the token's user.

The submission policy is shared with the manual helper:

- Keep the oldest open CyberSnap PR authored by the token's user and update its
  existing branch with the latest release manifests. Replace the old version's
  files so the PR contains exactly one version.
- Preserve the PR's creation date, commit history, and any notes outside the
  managed description section. Branch updates never force a ref.
- Verify the updated PR before commenting on and closing superseded CyberSnap
  submissions. Other packages are left untouched.
- Create a new PR only when there is no pending CyberSnap submission.
- Repeating a successful submission does not add another commit or PR. Older
  releases cannot replace a newer pending version. Releases superseded by a
  newer stable GitHub release are skipped.
- Stop on API errors, unexpected PR files, or concurrent branch changes rather
  than overwriting changes or creating a duplicate.

Workflow concurrency serializes submissions. Building and publishing a release
remains independent of WinGet moderation and submission.

## Manual submission

Install Node.js, GitHub CLI, and WinGet. Authenticate GitHub CLI, fork
`microsoft/winget-pkgs`, and download the three manifest assets from the latest
stable CyberSnap release into a dedicated directory. Then run:

```powershell
./scripts/winget/Submit-WingetSubmission.ps1 -ManifestDirectory ./winget-pkg -GitHubToken $env:WINGET_PAT
```

The helper validates the manifests with `winget validate` before submitting them.
The Node entry point also accepts `--defer-close` for a manual consolidation that
needs remote validation before closing older submissions. Re-run without that
option to finish consolidation.

Run the submission policy tests without network access or credentials:

```powershell
node --test .github/scripts/submit-winget.test.mjs
```

Official docs:

- https://learn.microsoft.com/en-us/windows/package-manager/package/manifest
- https://learn.microsoft.com/en-us/windows/package-manager/package/repository
- https://github.com/microsoft/winget-pkgs
