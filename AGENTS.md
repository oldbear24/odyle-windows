# Working in this repository

Instructions for every agent (and person) changing this repository. `CLAUDE.md` imports this file.

## Versioning

Odyle for Windows uses semantic versions, **MAJOR.MINOR.PATCH**. The version is `<Version>` in `src/Odyle.Reader/Odyle.Reader.csproj`. Every push to `main` that changes what ships publishes a GitHub release tagged with exactly that version, and installed apps update to it.

**Every change that ships bumps the version once, in the same pull request, by the largest kind of change it contains:**

| Bump | When | Examples |
| --- | --- | --- |
| **MAJOR** (x.0.0) | Something that worked stops working without action from users or the server's operators | The app needs a newer backend than the one deployed; pairing or saved connections stop working; Windows or .NET requirements rise; settings are dropped |
| **MINOR** (0.x.0) | New behaviour that keeps everything that worked working | A feature, a new card line or window, a new optional field sent to or read from the backend, a new decoder adapter |
| **PATCH** (0.0.x) | No new behaviour | A bug fix, wording, a translation, styling, performance, a refactor, a dependency update |

- Reset the lower parts: 0.7.3 plus a feature is 0.8.0; 0.8.0 plus a breaking change is 1.0.0.
- One pull request with several changes bumps once, by the largest. A pull request stacked on another unmerged one bumps from the version on `main`, not from the other branch.
- What does not ship needs no bump and makes no release: `docs/`, Markdown files, `tests/`, `.github/` and `scripts/version.ps1` (`scripts/publish.ps1` and the installer do ship). `scripts/version.ps1` has the exact list.
- Name the bump in the pull request description ("Version 0.7.0 → 0.7.1: fixes").

`./scripts/version.ps1 check` runs in the checks of every pull request and branch: it fails when shipped files changed and the version is not higher than on `main`. The release workflow runs `./scripts/version.ps1 release`: nothing is released when nothing that ships changed since the latest release, and it fails when shipped code changed without a version above every earlier release. Releases before semantic versions were tagged `0.6.0.<run number>`, up to `v0.6.0.18`, so the first semantic version must be at least 0.6.1; installed apps compare versions with missing parts as zero, so they take 0.6.1 or 0.7.0 as newer.

The dashboard and backend (oldbear24/odyle-dashboard) version themselves the same way in their own repository; the two version independently.
