# Windows migration

The active project contains C#/.NET source, Windows build scripts, tests, and documentation. Swift/AppKit source, Mac build scripts and products, `.DS_Store` files, and `__MACOSX` resource forks are outside the workspace.

The original project was archived before cleanup. Verification compared all 6,425 file entries and their total uncompressed size, 871,070,014 bytes, against the original directory.

Recovery archive on this computer:

`C:\Users\ASUS\AppData\Local\IslandBuild\backups\Island-macOS-original-20261005-172706.zip`

The approval layer blocked recursive deletion. A reversible move removed both old directories from the project instead. The uncompressed recovery copy is here:

`C:\Users\ASUS\AppData\Local\IslandBuild\backups\removed-macOS-original`

## Implementation mapping

| Original | Windows implementation |
| --- | --- |
| Swift | C# 14 / .NET 10 |
| AppKit, Core Animation | WPF retained rendering and render transforms |
| CADisplayLink | CompositionTarget.Rendering, using RenderingTime |
| Analytical spring oscillator | Same equations, frequency, damping, thresholds and geometry |
| Menu bar, Carbon shortcut | Notification-area icon, RegisterHotKey |
| AppleScript media control | Windows system media sessions |
| Keychain | Windows DPAPI for the current user |
| NSPasteboard | Windows clipboard notification listener; opt-in capture |
| Finder / Quick Look | Explorer / associated apps, asynchronous image previews and file icons |
| AirDrop | Windows Share chooser |
| Launch at login | Current-user Windows startup entry |
| DMG | Portable self-contained Windows distribution |

The original Mac verification reports do not validate this build. Windows measurements belong in `artifacts` and `docs/VERIFICATION.md`.

## Platform references

- [WPF per-frame rendering](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget)
- [WPF rendering and hardware](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-taking-advantage-of-hardware)
- [Windows media session manager](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- [Sign in with ChatGPT](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [ChatGPT models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- [PresentMon capture permissions](https://github.com/GameTechDev/PresentMon#troubleshooting)
