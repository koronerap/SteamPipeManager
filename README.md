# Steam Pipe Manager

Manage the SteamPipe builds of several Steam accounts, games and side apps (demos,
playtests, betas) from **one SteamCMD installation**.

With SteamPipe on its own you end up copying the Steamworks SDK's
`tools/ContentBuilder` folder once per game and hand-editing `app_*.vdf` and
`depot_*.vdf` in every copy. This app keeps a single SteamCMD installation, generates
the scripts for you, and never asks you to edit a `.vdf` by hand.

The same app also comes in two other editions, built from the same code:

| Download | For |
|---|---|
| `SteamPipeManager-win-x64.zip` | Steam only |
| `EpicBuildManager-win-x64.zip` | Epic Games Store only, through Epic's BuildPatchTool |
| `PipeManagerHub-win-x64.zip` | Both, side by side, with each profile labelled Steam or Epic |

Epic support is new. It has been used against a real Epic Games Store account, and it stays
marked **experimental** in the app until more people have published with it.

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-9-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

## Features

- **Multiple Steam accounts** — one profile per account, each with its own cached session
- **Multiple games per profile**, and multiple build targets per game (main, demo, playtest, beta)
- **Import your existing setup** — point at a `ContentBuilder` folder and it reads your
  `app_*.vdf` / `depot_*.vdf` files and turns them into profiles, games and depots
- **In-app sign-in** — no console window; Steam Guard codes and mobile confirmation are handled in the UI
- **Preview builds** — a dry run that uploads nothing
- **Live progress** with phase, percentage and a detailed log you can open when you need it
- **Build history** with the BuildID Steam returned and a link to the archived log
- **Script preview** — see the exact `.vdf` files that will be written before you build
- **Store art** — game capsules and profile avatars pulled from Steam's public endpoints
- **Epic Games Store** (Epic Build Manager and Pipe Manager Hub) — artifacts, build roots,
  launch settings and labels; uploads with `UploadBinary`, then sets the label live with
  `LabelBinary`, and reports "uploaded but not labelled" as its own state
- **Updates itself** from GitHub releases, verified against a published SHA-256 checksum
- **Eight languages** — English, Turkish, German, French, Russian, Japanese, Korean and
  Simplified Chinese, and language files are plain JSON you can add to

## Getting started

1. Download the zip for the edition you need from [Releases](../../releases), unpack it
   anywhere and run the exe inside. No installation and no .NET runtime required — the
   folder holds the exe and the five native libraries WPF needs beside it.
2. On first run, pick a language and let the app download SteamCMD (or point it at one
   you already have).
3. Create a profile for your Steam account and sign in once from its card.
4. Either add a game manually, or use **Import** to read an existing `ContentBuilder` folder.
5. Select a build target, try **Preview** first, then build.

The app checks GitHub for a newer release when it starts and offers to update itself.
Nothing is installed until you click **Update and restart**, a download is only used if it
matches the SHA-256 checksum published with the release, and if any file cannot be replaced
the current version is kept. The check can be turned off in Settings.

### Epic Games Store

1. Download **BuildPatchTool** from the Epic Developer Portal. The app cannot download it
   for you: it sits behind the portal and may not be redistributed. Point the app at it
   in Settings.
2. Create an Epic profile with your Organization ID, and the Client ID and Client Secret of
   a BuildPatchTool credential.
3. Add a game with its Product ID, then an artifact with its Artifact ID, build folder and
   launch executable.
4. Try **Preview** first. It checks the credentials and arguments with `-DryRun` and
   uploads nothing. Leave the label empty on your first real upload; with a label set,
   the build is set live on it after uploading.

> Preview is worth using the first time: it runs the whole pipeline and reports exactly
> what would be uploaded, without uploading anything.

## Safety

A few things the app deliberately refuses to do:

- **Empty depots are blocked.** Uploading an empty depot deletes the content currently
  live on Steam, so a build with a missing or empty content folder never starts.
- **`default` cannot be set live.** Steam does not allow it, and switching the default
  branch is done on the Steamworks site.
- **Setting a build live is confirmed** — you are told which branch and which AppID first.
- **Only one build runs at a time.** Two SteamCMD processes sharing one installation can
  corrupt the cached session.
- **The session is checked before every build.** Without that, an expired session leaves
  SteamCMD waiting forever at a password prompt the app cannot see.

## Passwords

Your password is **never written to disk** and never passed on the command line (where it
would show up in the process list). During sign-in it is held in memory only and handed to
SteamCMD over stdin; after that SteamCMD keeps the session in its own cache and the app
never needs the password again.

`profiles.json` contains no credentials — it is safe to back up, share or commit.

Epic client secrets are the exception that has to be stored: they are encrypted with
Windows DPAPI for your user account in `epic-secrets.json`, kept out of `profiles.json`,
and handed to BuildPatchTool through an environment variable, never on the command line.
Logs you export for a bug report have account IDs and user paths masked.

## Data location

`%AppData%\SteamPipeManager\`

| File / folder | Contents |
|---|---|
| `profiles.json` | Profiles, games, build targets, depots |
| `settings.json` | SteamCMD and BuildPatchTool paths, language, timeouts, update check |
| `epic-secrets.json` | Epic client secrets, DPAPI-encrypted for your Windows user |
| `history.json` | Build history (most recent 500 entries) |
| `lang\*.json` | Language files — drop your own here |
| `workspaces\` | Generated `.vdf` scripts and build output |
| `steamcmd\` | SteamCMD, if the app downloaded it |
| `covers\` | Cached Steam capsule art and avatars |
| `updates\` | A downloaded update, only while it is being installed |

Set the `SPM_DATA_DIR` environment variable to use a different folder, for example to try
the app against a copy of your profiles.

## Adding a language

Copy `%AppData%\SteamPipeManager\lang\en.json`, change `code` and `name`, and translate
the strings. Restart the app and it appears in the language list.

Untranslated keys fall back to English, so a partial translation works fine — you can
translate a bit at a time. Keys added by a later version of the app also fall back to
English automatically, so your file will not go stale.

## Building from source

Requires the .NET 9 SDK.

```bash
dotnet build                                    # build
dotnet test tests/SteamPipeManager.Core.Tests   # run the tests
.\publish.ps1                                   # zip all three products (asks whether to skip tests)
```

### Publishing a release

```powershell
.\publish.ps1
```

This writes one zip per product and a `SHA256SUMS.txt` to `publish/`. Upload **all** of them
to the GitHub release, tagged `vX.Y.Z` to match `<Version>` in the app project. The in-app
updater looks for `<Product>-win-x64.zip` and refuses to install it unless `SHA256SUMS.txt`
lists a matching checksum, so a release without that file is only offered as a manual
download.

The solution is split so the interesting parts are testable without a UI or a Steam account:

- `SteamPipeManager.Core` — VDF parser and writer, the importer, validation, the SteamCMD
  process layer. No WPF dependency.
- `SteamPipeManager.App` — WPF/MVVM front end.
- `tests/FakeSteamCmd` — a stand-in for `steamcmd.exe` that reproduces its measured
  behaviour, so the build engine can be tested end to end without signing in to Steam.
- `tests/fixtures/` — not in the repository. Reference ContentBuilder scripts and real
  tool logs used during development live there locally; tests that need them report as
  skipped when the folder is missing.

[docs/M0-FINDINGS.md](docs/M0-FINDINGS.md) records what SteamCMD actually does when you
drive it as a subprocess — output buffering, where the live log really comes from, how the
sign-in prompts behave. Several of the design decisions here only make sense next to those
measurements.

## Licence

MIT — see [LICENSE](LICENSE).

Not affiliated with Valve. Steam and SteamPipe are trademarks of Valve Corporation.
