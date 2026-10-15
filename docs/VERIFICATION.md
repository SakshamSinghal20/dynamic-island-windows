# Windows verification · 6 October 2026

The native C#/.NET 10 WPF self-contained x64 Release build was published and checked on Windows 11 build 26200, Intel Arc 130V graphics, a 1920 × 1200 display at 60 Hz and WPF rendering tier 2.

## Automated checks

| Check | Result |
| --- | --- |
| Core and Spotify contract tests | 39 / 39 passed |
| Self-contained ReadyToRun publish | Successful, no warnings |
| Rapid shape reversals / tab transitions | 48 / 32 exercised |
| Host position/size changes during motion | 0 |
| Frames with disconnected bezel shape | 0 |
| Idle hide, exact-edge activation, dismissal suppression, reentry, reduced-motion settling | All passed |
| Swift / macOS source and build products | Removed from active project; external recovery backup documented in MIGRATION.md |

The original tests cover spring interruption, 60/120/144 Hz invariance, missed-frame stability, drag inertia, settling, waveform continuity, hover, timer persistence, SSE framing, conversation context, PKCE and a real loopback callback exchange. Eight Spotify tests cover current and legacy response fields, null/local items, pagination, playlist playback, queue commands, device restrictions, transfer, volume clamping, library save/unsave, unknown-status rejection and rate-limit cooldown. Spotify HTTP tests use fixtures, not a live account.

## Live Computer Use checks

Hover lifecycle regression: Settings previously set the same modal flag as the file picker, bypassing the entire pointer probe. Keyboard focus also pinned the capsule open. Settings now leaves polling active, pointer exit collapses even after text focus, and only mouse capture or an actual file picker pauses collapse. Automatic collapse no longer applies manual-dismissal suppression. The drag strip now uses a normal arrow cursor. The real-window regression failed before the fix (`artifacts/hover-before.json`) and passed after it (`artifacts/hover-after.json`), covering entry/exit/reentry with Settings, focused input, and entry/exit without Settings. `test.ps1` now runs this regression and writes `artifacts/hover-report.json`. Live Computer Use confirmed entry expansion and exit collapse without clicking on the final published build, while Settings remained open; the header displayed a normal arrow.

Follow-up UI update: activity icons, volume/library icons and library tabs now navigate on hover, with click/keyboard fallback. Live pointer-only checks verified Music, opening the library and selecting Playlists. Ask AI was removed from the Music action row and remains accessible through the tray menu. Settings now separates Windows playback from library authorization and includes setup instructions. Successful library authorization refreshes the visible panel immediately. The current release passes 38 tests plus the window hover checks. The timing table below measures the current faster-motion build.

| Feature | Observed result |
| --- | --- |
| Relaunch / Settings | A second launch brings Settings forward |
| Activity buttons | Timer, media, clipboard and file tabs respond after removing whole-window click-through |
| Spotify Windows connection | Selected Spotify session persists and reconnects after app restart |
| Media title / artwork / play / pause | Verified with installed Spotify |
| Seeking | Changed paused track from about 1:49 to 0:47; restored near 1:49 |
| Native text entry | Corrected the TextBox template; timer entry and Settings input verified |
| Timer | Five-minute start, pause, add minute and stop verified; left stopped |
| Clipboard text | Harmless test text captured, copied back and pasted correctly; session history cleared and capture paused |
| Spotify library without credentials | Queue, Recent, Liked, Playlists and Devices panel renders with connection guidance |
| File picker | Open and cancel verified; corrected owner. Automation could not reliably address/type into the modal field, so adding a file is **not** marked passed |

No Spotify developer Client ID was available. The user deferred setup; see [SPOTIFY-SETUP.md](SPOTIFY-SETUP.md). Live OAuth, library requests, queue/library mutations and remote-device actions remain unverified. Chat account sign-in and real streaming, clipboard image/file history, file add/drop/open/reveal, Share targets, previous/next/shuffle/repeat, actual system-volume adjustment, startup registration, screen-reader use and mixed-DPI external displays also remain unverified end to end. Native UI inspection is not evidence that these integrations all work.

The checks fixed relaunch behavior, misleading media connection feedback, fast clicks passing through the capsule, missing/clipped text input, seek commit ordering, and file-dialog ownership. The old duplicate track browser was removed when the expanded Spotify panel was introduced. Library commands prevent concurrent selection changes during a pending action.

## Animation measurement

The latest packaged build was measured without concurrent Computer Use capture and with the ordinary Island instance stopped. Reports: `artifacts/test-results.json` and `artifacts/motion-report.json`. The latter timestamp is UTC; the test date above is local India time.

| Metric | Latest run |
| --- | ---: |
| Callbacks recorded | 836 |
| Mean callback rate | 60.28 / second |
| Mean render-timeline rate | 59.88 / second |
| 95th-percentile render interval | 16.6679 ms |
| 99th-percentile render interval | 16.6697 ms |
| Render intervals over 25 ms | 2 of 834 |
| 95th-percentile callback CPU work | 0.1360 ms |
| Maximum callback CPU work | 1.5445 ms |
| Maximum callback arrival interval | 21.7930 ms |

The preceding clean build measured 57.69 render-timeline frames/second and 24 intervals over 25 ms; the latest run measured 59.88 and two such intervals. These are separate runs under variable system load, not a controlled attribution of the improvement to one code change. The current build uses 40 ms entry dwell, 180 ms exit dwell, a 20 ms pointer probe, faster springs and cached settled-shell geometry. An earlier run during desktop capture measured 54.87 callbacks/second and 43 render intervals over 25 ms. It is retained in `artifacts/motion-report-during-capture.json`. The previous day's run measured 59.62 callbacks/second with eight long render intervals. These runs show that timing varies; the latest build does **not** establish locked 60 fps or zero jitter. The analytical spring solver and damping are retained, with faster frequencies, and callback CPU work fits within the frame budget, but scheduling, WPF composition and GPU/display presentation are not isolated by this measurement.

PresentMon 2.6.0 could not start its ETW trace because Windows denied access without administrative or Performance Log Users privileges. No privilege/group changes were made. GPU presentation timing remains unverified.

## Isolated renders

`artifacts/renders` contains timer, media, clipboard, files, compact, chat and the expanded Spotify library. These use local fixtures, and the Spotify library render was visually inspected for clipping and control placement. The blue-gray diagnostic backdrop is not part of the transparent app. Automated diagnostics do not access real clipboard data, media accounts or user credentials.


## Spotify callback registration update

The dashboard rejected the previous no-port URI in the user's screenshot. Spotify now uses the exact registered callback http://127.0.0.1:8888/spotify/callback. The listener binds IPv4 loopback exclusively, reports an occupied port, and retains state/PKCE checks. A real local callback test verified the exact URI, duplicate-bind rejection, invalid-state rejection and accepted callback. All 39 tests and the window hover regression pass. Dashboard acceptance and live Spotify authorization remain pending the user's setup. The frame-timing figures above were not rerun for this callback-only change.

## Spotify list interaction update

## Persistent clipboard history

Clipboard history now retains up to 50 distinct text/image/file copies within a 32 MB memory budget, saves them through Windows CurrentUser DPAPI, restores them on startup, and imports available Windows history on enable. Import and PNG encoding run on the clipboard worker. Pause retains history; Clear invalidates pending imports/captures, deletes Island's saved history, and records a cutoff so old Windows entries do not immediately return. Windows history itself is untouched.

All 42 tests and hover regressions pass. New checks cover chronological retention/deduplication, encrypted text/image/file serialization roundtrips and excluded formats. Fixture renders confirm the item count and Older navigation (`artifacts/clipboard-history/renders/clipboard-older.png`). The published app was restarted with history enabled. No real clipboard contents were printed for verification. Availability of pre-existing Windows history depends on Windows having retained those copies.

Compact music follow-up: the old missing-artwork branch displayed a timer icon regardless of activity, and the title inherited a 210-DIP shell. Music now uses a music fallback when Windows supplies no artwork. Title measurement runs on title changes and sizes the compact shell between 260 and 360 DIPs, adding 75 DIPs for a concurrent timer. The existing shell spring animates width changes. The renders in `artifacts/compact-fix/renders` verify music-only and music-plus-timer layouts with unavailable artwork; all 39 tests and the hover regression pass. Actual cover availability still depends on the Windows media session.

Spotify lists now use pixel scrolling: 24 DIPs per wheel notch, with short easing and a bounded pending distance. Reduced motion applies the offset directly. Hiding the panel, loading another view, keyboard input, or mouse input stops the animation. Playlist rows open their tracks on a single click or Enter; the separate Open playlist button is removed. A playlist double-click cannot start a newly loaded track.

The updated app was published and restarted. All 39 tests and the real-window hover regression passed. Live inspection now confirms a connected Spotify library and successful Queue, Liked, and Playlists reads. One wheel notch in Liked moved the rows approximately 24 DIPs instead of several rows. The playlist click check was interrupted by user pointer activity, so live single-click navigation remains unverified. No new frame-timing measurement was taken for this change.


## Scratchpad replaces Files

The fourth activity is now a multiline scratchpad. Clicking its pencil icon activates and focuses the editor; hovering selects the page. Notes autosave after a 300 ms typing pause and flush when the page collapses, changes, or Island closes normally. Saves run off the UI thread, in order, with CurrentUser DPAPI encryption. Copy and Clear are available; text edits retain standard WPF undo. The old file shelf, picker, drag/drop, previews and sharing implementation were removed.

All 44 tests and the hover checks pass. Scratchpad tests verify encrypted disk persistence across store instances, clear persistence, write ordering and recovery after a failed write. Empty and populated fixture renders were inspected in `artifacts/scratchpad-preview/renders`. The updated app was published and restarted. Live user typing/copying was not automated.
