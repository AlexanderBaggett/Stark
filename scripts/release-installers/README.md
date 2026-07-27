# Release installer sources

These four scripts are copied to the root of a target-specific release archive:

- `install.sh` and `uninstall.sh` for macOS/Linux
- `install.ps1` and `uninstall.ps1` for Windows

They are archive-local tools, not network bootstrap installers. Installation
copies the complete extracted SDK as one versioned unit. The scripts never
download Stark, .NET, System, Vendor, or compiler-private backend files.
`stark doctor --strict` performs payload and host-prerequisite preflight before
copying and verifies the installed copy before PATH changes are committed. The
installers first validate every `release-files.sha256` entry, reject unsafe or
duplicate paths and untracked files, and compare the exact SHA-256 before any
install location or PATH mutation.

Use the staging helper from release assembly:

```powershell
./scripts/stage-release-installers.ps1 `
  -StageRoot $stageRoot `
  -AssetSuffix $AssetSuffix
```

The installers default to per-user versioned roots, support explicit prefixes,
dry-run/noninteractive/no-PATH modes, and replace existing installations only
when a Stark receipt proves ownership. Uninstallers remove the receipt inventory
one file at a time and remove only the exact Stark-managed command/PATH entry.

Run the portable lifecycle tests directly on macOS or Linux:

```text
tests/release-installers/test-installers.sh
```

`ReleaseInstallerContractTests` runs that lifecycle through the .NET test suite
and parses the PowerShell sources when a PowerShell host is available.
