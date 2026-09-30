# Architecture

```text
WinForms UI
  |
  +-- SettingsStore
  |     +-- %LOCALAPPDATA%/CodexUsageHub/settings.json
  |
  +-- CodexRuntimeManager
        |
        +-- Account A -> CODEX_HOME/accounts/<id-a>/.codex -> codex app-server
        +-- Account B -> CODEX_HOME/accounts/<id-b>/.codex -> codex app-server
        +-- Account C -> CODEX_HOME/accounts/<id-c>/.codex -> codex app-server
```

For each account the client performs:

```text
initialize
  -> initialized
  -> account/read
  -> account/rateLimits/read
```

New account login uses:

```text
account/login/start { type: "chatgpt" }
  -> open returned authUrl in default browser
  -> wait for account/login/completed
  -> account/read
```

The application's own `settings.json` contains profile metadata and the Codex executable path, not OAuth access tokens.
