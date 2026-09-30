# Codex Usage Hub

A small Windows desktop utility for keeping multiple Codex/ChatGPT logins isolated and viewing their Codex rate-limit usage in one place.

## V1 features

- Multiple Codex accounts, each in its own `CODEX_HOME`.
- ChatGPT browser login handled by the local `codex app-server`.
- Reads `account/read` and `account/rateLimits/read` instead of scraping UI pages.
- Shows remaining percentage, window length, and reset time for the main Codex quota windows.
- Auto refresh every 60 seconds.
- System tray mode.
- Always-on-top mini overlay.
- No access tokens are copied into this app's settings file. Codex owns its credentials inside each isolated `CODEX_HOME`.

## Requirements

### To run the published app

- Windows 10/11 x64.
- Codex CLI installed, or a path to `codex.exe` / `codex.cmd` selected from **Browse Codex**.
- No Visual Studio required.
- No .NET runtime required for the self-contained release build.

### To build locally

- .NET 8 SDK only. Visual Studio is optional.

Run:

```powershell
./publish-win-x64.ps1
```

The standalone build is written to:

```text
publish/win-x64/CodexUsageHub.exe
```

## GitHub Actions build

Every push to `main` builds a self-contained `win-x64` zip. Open the repository's **Actions** tab, select **Build Windows Desktop**, then download the `CodexUsageHub-win-x64` artifact.

If you create and push a tag such as:

```bash
git tag v1.0.0
git push origin v1.0.0
```

the workflow also creates/updates a GitHub Release and attaches the zip.

## First run

1. Launch `CodexUsageHub.exe`.
2. If Codex is not detected, click **Browse Codex** and select `codex.exe` or `codex.cmd`.
3. Click **+ Add account**.
4. Give it a short display name such as `Main` or `Backup`.
5. Your browser opens the official ChatGPT/Codex login flow.
6. Sign in to the account you want to associate with that profile.
7. Repeat for additional accounts.

Profiles and settings live under:

```text
%LOCALAPPDATA%\CodexUsageHub\
```

Each login has an isolated home similar to:

```text
%LOCALAPPDATA%\CodexUsageHub\accounts\<profile-id>\.codex\
```

## Why there is no Docker image in V1

This is a WinForms desktop/tray/overlay application. Docker is a poor fit for an interactive Windows GUI and would make login/browser/tray integration harder. The produced self-contained `.exe` is the intended deployment artifact.

A future headless collector + web dashboard could be containerized separately.

## Notes

Codex quota buckets are not assumed to always be exactly "5 hour" and "weekly". The app labels each returned bucket from its `windowDurationMins` value and tolerates missing windows.

The app depends on the local Codex app-server protocol. If that protocol changes in a future Codex release, this utility may need an update.
