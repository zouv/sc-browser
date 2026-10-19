<div align="center">
  <img src="docs/icons/sc-browser-128.png" width="104" alt="ScBrowser" />
  <h1>ScBrowser</h1>
  <p>A Windows desktop browser built with <b>WPF + Microsoft Edge WebView2</b>, with Chrome extension support</p>

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4?style=flat-square&logo=windows&logoColor=white)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](#build-from-source)
[![Release](https://img.shields.io/github/v/release/zouv/sc-browser?style=flat-square&color=success&label=release)](../../releases/latest)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)

<img src="docs/images/screenshot-newtab.png" width="880" alt="ScBrowser new tab page" />

</div>

ScBrowser is a general-purpose browser shell written from scratch: the **chrome is native WPF**
(instead of hosting another WebView for the UI), while web content and internal pages are rendered by
**WebView2** — one WebView2 instance per tab. It loads **Chrome extensions** (including automatic
`.crx` unpacking) and propagates the **light/dark theme into web content**.

> Chinese documentation is the primary source: [`README.md`](README.md) ·
> [`docs/`](docs/) · [`ARCHITECTURE.md`](ARCHITECTURE.md).

## Features

**Browsing**
- Multiple tabs: open / close (middle-click too) / switch / **drag to reorder** (the dragged tab follows
  the pointer while the others make way) / per-tab context menu
- Borderless custom shell: window buttons merged into the tab strip; drag, snap (Aero Snap) and
  double-click-to-maximize all behave natively; window size and position are remembered
- **`Ctrl+Shift+T` reopens the last closed tab**
- Back / forward / reload-stop / home / page zoom / in-page error bar

**Address bar**
- Automatic URL vs. search-term detection; focus selects all and opens the suggestion list
- **History drop-down**: live filtering (URL prefix > title prefix > contains, then by visit count),
  ↑/↓ to choose, Enter to open, Esc to dismiss, plus a "search with X / open directly" row
- Page security indicator (HTTPS / HTTP / dangerous) with a human-readable tooltip

**Windows**
- `Ctrl+N` new window, `Ctrl+Shift+N` **incognito window** (badged in the tab strip)
- Incognito windows keep no history, save no session and record no downloads; cookies and site data
  are discarded when the window closes

**Data**
- History (deduplicated, visit counts, keyword search), bookmarks (`Ctrl+D`), downloads (live progress)
- Internal pages: new tab / history / bookmarks / downloads / extensions / settings — all local WebUI
  shipped with the app
- Session save & restore

**Extensions & theme**
- Load unpacked extensions, install `.crx` packages (auto-unpacked), enable/disable, uninstall
- Hot-swappable light/dark theme that also drives the page's `prefers-color-scheme` (sites that ship a
  dark theme go dark; nothing is force-inverted) and the WebView2 default background — no white flash
  when opening tabs in dark mode

## Download

Grab the latest package from [Releases](../../releases/latest)
(`ScBrowser-vX.Y.Z-win-x64-*.zip`, just unzip and run):

| Package | Size | Prerequisites |
|---|---|---|
| `selfcontained` | ~145 MB | **None** (bundles the .NET runtime) |
| `frameworkdep` | a few MB | Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |

The binaries are not code-signed, so Windows SmartScreen may warn on first launch:
choose "More info" → "Run anyway".

### Requirements

- **Windows 10 1809+ / Windows 11**
- **WebView2 Runtime** — usually preinstalled with Edge on Windows 10/11; otherwise install the
  Evergreen runtime from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/)

## Keyboard shortcuts

| Shortcut | Action | Shortcut | Action |
|---|---|---|---|
| `Ctrl+T` | New tab | `Ctrl+Shift+T` | Reopen closed tab |
| `Ctrl+W` | Close tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab |
| `Ctrl+N` | New window | `Ctrl+Shift+N` | New incognito window |
| `Ctrl+L` / `Alt+D` | Focus address bar | `Ctrl+R` / `F5` | Reload |
| `Ctrl+Shift+R` | Reload ignoring cache | `Ctrl+D` | Bookmark this page |
| `Ctrl+H` | History | `Ctrl+J` | Downloads |
| `Alt+←` / `Alt+→` | Back / forward | `Ctrl+1..8` / `Ctrl+9` | Nth / last tab |
| `Ctrl++` / `Ctrl+-` / `Ctrl+0` | Zoom in / out / reset | `Esc` | Stop loading / discard address bar edit |
| `Ctrl+F` | Find in page | `F3` / `Shift+F3` | Next / previous match |

> Shortcuts keep working while the focus is inside the page.

## Build from source

Prerequisite: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
sh manager.sh build     # build (Debug)
sh manager.sh run       # run in the foreground
sh manager.sh test      # unit tests
sh manager.sh verify    # pre-commit gate: build (-warnaserror) + tests + doc check
sh manager.sh publish   # package into packages/ (dev zip + release setup/unpack zip)
```

**No bash?** Equivalent Windows batch scripts live in the repository root (`build.bat`, `run.bat`,
`test.bat`, `verify.bat`, `clean.bat`, `publish.bat`, `ci.bat` — plain cmd + dotnet; `ci.bat` runs
clean → gate → package in one go). `manager.sh` is the single source of truth for
command semantics; the two stay in sync.

**Package output** (`packages/` is git-ignored; `clean` preserves it):

- `packages/dev/ScBrowser_<version>_x64-unpack-debug.zip`: Debug portable package
  (framework-dependent) with `ScBrowser.pdb`, `logs/` and `build-info.txt`.
- `packages/release/ScBrowser_<version>_x64-unpack.zip`: Release portable package
  (self-contained; no .NET runtime required on the target).
- `packages/release/ScBrowser_<version>_x64-setup.exe`: per-user Windows installer
  (installs to `%LOCALAPPDATA%\Programs\ScBrowser`, no admin rights, includes an uninstaller).
  The Inno Setup toolchain is restored from the `Tools.InnoSetup` NuGet package — no manual install needed.

> Both zips contain a single root directory (`ScBrowser_<version>_x64-unpack[-debug]/`);
> extract and run `ScBrowser.exe`. The layout matches `sc-code/packages`.

You can also open `sc-browser.slnx` in Visual Studio 2022 (17.13+).

## Project layout

```
src/ScBrowser/            # WPF application
  Browser/                #   engine: WebView2 environment & tabs, navigation, extensions
  Services/               #   services: window manager, settings, theme, session, downloads, suggestions
  Storage/                #   SQLite (history / bookmarks / downloads)
  ViewModels/ Views/      #   shell: view models and WPF views
  Assets/ui/              #   internal pages (WebUI: HTML/CSS/JS)
  Interop/                #   Win32 interop (DWM, maximize clamping)
tests/ScBrowser.Tests/    # xUnit unit tests
samples/extensions/       # sample Chrome extension
docs/                     # user/maintainer docs; icons/ holds the vector icon source
```

## Design notes

C# / .NET 10 (`net10.0-windows`) · WPF shell · Microsoft Edge WebView2 · SQLite · vanilla HTML/JS/CSS.

A few deliberate choices (see `ARCHITECTURE.md` §7 and `docs/01-技术栈说明.md` for the full reasoning):

- **Native WPF shell** rather than hosting the UI in a second WebView — one less WebView environment,
  and better focus / IME / accessibility / DPI behaviour.
- **One WebView2 per tab** (no reuse): zero-latency switching and preserved page state, at the cost of
  memory — the same trade-off Chrome makes.
- **A single `CoreWebView2Environment`** for the whole app: all tabs share cookies, cache and installed
  extensions. Incognito windows reuse that environment and only switch to an InPrivate profile.
- **Internal pages talk over a message bridge** instead of `fetch`ing the host directly.

## Documentation

| Document | Contents |
|---|---|
| [`docs/01-技术栈说明.md`](docs/01-技术栈说明.md) | Tech stack rationale and component responsibilities (Chinese) |
| [`docs/02-使用说明.md`](docs/02-使用说明.md) | User manual and troubleshooting (Chinese) |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) · [`arch/flows.md`](arch/flows.md) | Code map: file responsibilities, task→code lookup, key flows, pitfalls |
| [`ROADMAP.md`](ROADMAP.md) | What is planned and what is done |
| [`CHANGELOG.md`](CHANGELOG.md) | Changes per release |
| [`AGENTS.md`](AGENTS.md) | Contribution conventions for this repository |
