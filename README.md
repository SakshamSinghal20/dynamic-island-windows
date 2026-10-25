# Island for Windows

A native C# desktop app built with .NET 10 and WPF. The original analytical spring equations drive the bezel shape, expansion, interruption, drag release, and vertical activity transitions.

## Run

Open `dist/Island/Island.exe`, or double-click `Start Island.cmd`. The portable build includes .NET; you do not need development tools to run it. Requires Windows 10 version 2004 or newer, or Windows 11, on x64.

**Ctrl+Alt+Space** opens or collapses the running Island. A keyboard-opened panel stays open until you press the shortcut again, press Escape, or click outside. Windows startup keeps Island available in the background; after choosing Quit, launch Island again to restore the shortcut. Hover at the center of the top screen edge to open it. The notification-area icon opens Settings, connections, diagnostics, and Quit. Windows may put the icon in the overflow area.

Hover the activity icons to switch between Music, Timer, Clipboard and Scratchpad. Hover the volume or library icon to open that panel, and hover the library tabs to browse. Click and keyboard activation remain available. Playback and library mutations require deliberate activation. Ask AI is available from the tray menu, outside the Music controls.

## Activities

- **Timer:** 5/15/25-minute presets, custom duration, pause/resume, add a minute, and persistent deadlines. Reopening or waking the PC reconciles elapsed time. Alerts require Island to be running.
- **Media:** opt-in Windows media sessions for compatible Spotify, Apple Music, browser, and other players. Play/pause, previous/next, seeking, shuffle, repeat, artwork, extrapolated progress, and a continuous playback indicator. Controls depend on what the player exposes. The volume strip controls the Windows output volume.
- **Spotify library:** optional PKCE sign-in for queue, recent tracks, paginated search and Liked Songs, save/unsave, playlists, device transfer and device volume. Configure your Spotify Client ID in Settings using [the setup steps](docs/SPOTIFY-SETUP.md). Spotify's account and application restrictions apply. The main transport controls remain attached to the selected Windows media session.
- **Ask AI:** ChatGPT sign-in, account selection, available models, streamed responses, Stop, Retry, New conversation, and local sign-out with remote revocation. Chat histories stay in memory. Completed turns provide follow-up context. Assistant text supports headings, lists, bold, inline code, fenced code, and links. Sending a message uses the connected account's shared limits.
- **Clipboard:** text, image, and file history with up to 50 distinct copies and a 32 MB memory budget. Enabled history survives restarts in a local file encrypted for your Windows user. Enabling imports available Windows clipboard history; older copies that Windows did not retain cannot be recovered. Older/Newer buttons browse copies, Pause keeps history, and Clear removes Island history without clearing Windows history. Windows exclusion/confidential/password markers are skipped; marker detection cannot identify every secret.
- **Scratchpad:** click the pencil icon to start typing a multiline note. Autosave stores it locally with Windows-user encryption and restores it after restart. Copy, Clear, and standard text undo are available. Replaces the previous file shelf.

Settings include display selection, reduced motion, and optional start at Windows sign-in. Tokens use Windows DPAPI encryption for the current user. Preferences live in `%LOCALAPPDATA%\Island`. Island does not read another application's OAuth credentials.

Windows startup uses `Island.lnk` in the current user's Startup folder, with the `--startup` argument. This laptop's shortcut targets `%LOCALAPPDATA%\Programs\Island\Island.exe`, outside OneDrive. Startup launches keep a compact Island visible even when no music or timer is active. After publishing an update, close the installed app, copy `dist\Island` contents into that installation folder, and restart the installed executable with `--startup`.

## Motion

The transparent host window stays fixed throughout each morph. The animated shape uses the original curved shoulders and two-point bezel overlap. The compact body is 210 × 32 device-independent pixels; the expanded body is 348 × 208. Timer plus media uses a 244-pixel compact width. Chat opens at 620 × 560 or 820 × 720, clamped to the selected display.

Spring retargeting preserves position and velocity. The solver advances from WPF's render timestamp, not an interval timer or callback arrival time. The shape springs now run at 20 rad/s, with the same damping, for faster transitions; reveal and tab springs run at 22 rad/s. Content pages move with retained render transforms; their control layout stays fixed. Rounded progress tracks draw without arranging a native thumb each frame. The settled shell reuses its clipping geometry during playback. The frame subscription stops when the shape and visible playback indicator settle. A separate 50 Hz cursor probe keeps the invisible top-edge trigger available, with 40 ms entry and 180 ms exit dwell. Actual response also depends on Windows scheduling.

The build targets 60 fps. Recorded measurements and their limits are in [docs/VERIFICATION.md](docs/VERIFICATION.md). Callback rates alone do not establish GPU presentation rate or guarantee zero hitches on every PC.

## Build and verify

Install the .NET 10 SDK, then use PowerShell:

```powershell
.\build.ps1
.\test.ps1
.\build.ps1 -Publish
.\test.ps1 -Motion
```

`build.ps1 -Publish` produces a self-contained Release build with ReadyToRun compilation. `test.ps1` runs the spring, timer, SSE, conversation, PKCE, loopback and Spotify API contract tests. `-Motion` also exercises 48 rapid shape reversals, 32 tab switches, waveform playback, idle hiding, exact-edge activation, dismissal suppression, and reduced motion. It writes reports and renders to `artifacts`. Diagnostics use local fixtures and do not access real media, clipboard, or account credentials.

## Windows differences and remaining validation

The scratchpad replaces the file shelf. Windows media sessions replace AppleScript and macOS Automation permissions. Apple Music does not expose its macOS scripting library/playlist API on Windows; its available transport controls work through Windows media sessions, while the track browser offers local session history and the optional Spotify library.

The app uses Windows typography and vector controls, so font rasterization and glyphs differ from macOS. Live ChatGPT/Spotify authorization, player-specific capabilities, Share targets, screen-reader interaction, and mixed-DPI multi-monitor behavior need testing with those accounts/devices. This portable build is unsigned.

The old Swift project, Mac build products, and resource forks are removed from the active project. A verified recovery archive is stored outside it; `docs/MIGRATION.md` records its location.
