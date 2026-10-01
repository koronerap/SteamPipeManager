# Steam Pipe Manager

Manage the SteamPipe builds of several Steam accounts, games and side apps (demos,
playtests, betas) from **one SteamCMD installation**.

With SteamPipe on its own you end up copying the Steamworks SDK's
`tools/ContentBuilder` folder once per game and hand-editing `app_*.vdf` and
`depot_*.vdf` in every copy. This app keeps a single SteamCMD installation, generates
the scripts for you, and never asks you to edit a `.vdf` by hand.

The same app also comes in two other editions, built from the same code:

| Edition | For |
|---|---|
| Steam Pipe Manager | Steam only |
| Epic Build Manager | Epic Games Store only, through Epic's BuildPatchTool |
| Pipe Manager Hub | Both, side by side, with each profile labelled Steam or Epic |

Each edition is published for every platform:

| Platform | Download | Notes |
|---|---|---|
| Windows 10/11 | `<Edition>-win-x64.zip` | |
| Linux (x64) | `<Edition>-linux-x64.tar.gz` | **Preview** — see [Linux and macOS](#linux-and-macos) |
| macOS, Apple Silicon | `<Edition>-osx-arm64.tar.gz` | **Preview** |
| macOS, Intel | `<Edition>-osx-x64.tar.gz` | **Preview** |

`<Edition>` is `SteamPipeManager`, `EpicBuildManager` or `PipeManagerHub`.

Epic support is new. It has been used against a real Epic Games Store account, and it stays
marked **experimental** in the app until more people have published with it.

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![Linux](https://img.shields.io/badge/Linux-preview-orange)
![macOS](https://img.shields.io/badge/macOS-preview-orange)
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

1. Download the package for your edition and platform from [Releases](../../releases),
   unpack it anywhere and run the app inside. No installation and no .NET runtime
   required. On Linux and macOS read [the notes below](#linux-and-macos) first.
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

## Linux and macOS

The Linux and macOS versions are new. They share all of their logic with the Windows app.
The test suite and a real SteamCMD session run on Linux and on macOS (Apple Silicon and
Intel) for every change, and the app is started on each, but few people have used them for
real uploads yet. Please report what you find.

**Linux**

```bash
tar -xzf SteamPipeManager-linux-x64.tar.gz
./SteamPipeManager/SteamPipeManager
```

- SteamCMD is a 32-bit program. On Debian and Ubuntu install `lib32gcc-s1`; on Fedora
  `glibc.i686` and `libstdc++.i686`.
- Epic client secrets go to your desktop's secret store (GNOME Keyring or KWallet) through
  `secret-tool` (package `libsecret-tools` on Debian/Ubuntu). Without it they are kept in a
  file only your user can read, and the app says so.
- "Sign in from a console" opens your terminal emulator (it tries `x-terminal-emulator`,
  GNOME Terminal, Konsole, Xfce Terminal, kitty, Alacritty and xterm).

**macOS** (12 or later)

1. Download `osx-arm64` for Apple Silicon or `osx-x64` for Intel Macs, unpack it and move
   the `.app` to Applications.
2. The app is not notarized by Apple, so the first launch is blocked. Control-click the app
   and choose **Open**; on macOS 15 and later, open **System Settings → Privacy & Security**
   and click **Open Anyway**. Alternatively:

   ```bash
   xattr -dr com.apple.quarantine "/Applications/Steam Pipe Manager.app"
   ```

- SteamCMD for macOS is an Intel program, so Apple Silicon Macs need Rosetta. If it is
  missing, the app says so; install it with
  `softwareupdate --install-rosetta --agree-to-license`.
- Epic client secrets are stored in your login Keychain.
- SteamCMD runs with its own home folder inside the app's data folder, so its log and
  sign-in cache stay separate from the Steam client's.

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

| Platform | Folder |
|---|---|
| Windows | `%AppData%\SteamPipeManager\` |
| Linux | `~/.local/share/SteamPipeManager/` (or `$XDG_DATA_HOME`) |
| macOS | `~/Library/Application Support/SteamPipeManager/` |

| File / folder | Contents |
|---|---|
| `profiles.json` | Profiles, games, build targets, depots |
| `settings.json` | SteamCMD and BuildPatchTool paths, language, timeouts, update check |
| `epic-secrets.json` | Windows: Epic client secrets, DPAPI-encrypted for your Windows user. Linux without a secret store: the secrets, readable only by you |
| `steamcmd-home/` | Linux and macOS: SteamCMD's own home folder (its logs and sign-in cache) |
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
dotnet run --project src/SteamPipeManager.Desktop   # the Linux/macOS front end; runs on Windows too
.\publish.ps1                                   # package everything (asks whether to skip tests)
```

### Publishing a release

```powershell
.\publish.ps1
```

This writes one package per edition and platform, plus `SHA256SUMS.txt`, to `publish/`.
`-Platform Windows|Linux|MacOS` and `-Product` narrow it down. Upload **all** of them to the
GitHub release, tagged `vX.Y.Z` to match `<Version>` in `src/Product.props`. The in-app
updater looks for its own edition and platform (for example `PipeManagerHub-osx-arm64.tar.gz`)
and refuses to install it unless `SHA256SUMS.txt` lists a matching checksum.

The Linux and macOS packages are written by `tools/Packager`, which keeps the execute bits
that a zip would lose and builds the macOS `.app`. If
[rcodesign](https://github.com/indygreg/apple-platform-rs) is on the `PATH` (or in the
`RCODESIGN` environment variable) the `.app` is ad-hoc signed as a whole; without it the
script warns that the macOS packages are unsigned.

The solution is split so the interesting parts are testable without a UI or a Steam account:

- `SteamPipeManager.Core` — VDF parser and writer, the importer, validation, the SteamCMD
  and BuildPatchTool process layers, the updater. No UI dependency.
- `SteamPipeManager.Presentation` — view models and their services, shared by both front ends.
- `SteamPipeManager.App` — WPF front end (Windows).
- `SteamPipeManager.Desktop` — Avalonia front end (Linux and macOS; also runs on Windows).
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
