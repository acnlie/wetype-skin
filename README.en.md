# WeType Skin Studio

[English](README.en.md) | [简体中文](README.md)

WeType Skin Studio is a community skin editor and companion pet for WeChat Input Method on Windows and macOS.

Windows can apply supported themes to the native candidate panel and toolbar. The macOS build edits and previews themes and can experiment with a user-installed copy of the input method. It does not modify the official macOS app.

> This is an independent community project. It is not affiliated with Tencent or DeepSeek. Native Windows patching is limited to the verified input method version listed below.

Project: [GitHub](https://github.com/acnlie/wetype-skin) | Author: [X / Tokupup](https://x.com/Tokupup)

## Features

| Feature | Windows | macOS |
| --- | --- | --- |
| Edit colors, borders, and corner radius | Yes | Yes |
| Add an image and adjust its position and scale | Yes | Edit and preview |
| Adjust transparency | Yes | Edit and preview; native behavior is experimental |
| Apply to a real candidate panel | WeChat Input Method 2.1.4.6 | User-installed input method copy only |
| Apply toolbar styling | Verified Windows resources | Not yet supported reliably |
| Import and export `.wtskin.json` | Yes | Yes; shared format |
| DeepSeek Chan companion pet | Independent process | Independent process |

The candidate panel in the editor is a preview. On Windows, click **Apply skin** to patch the supported input method files and restart the input method.

## Download and install

Download the Windows installer or Intel Mac DMG from [GitHub Releases](https://github.com/acnlie/wetype-skin/releases).

### Windows

1. Run `WeTypeSkinStudio-Windows-x64.exe`. It installs for the current user and includes the offline WebView2 installer.
2. Open **WeType Skin Studio** from the Start menu.
3. Choose a preset or edit a theme, then click **Apply skin**.
4. Test typing in another application. The editor itself should run without administrator privileges; only the file-writing helper requests elevation.

Requirements: 64-bit Windows 10 version 2004 or later, WeChat Input Method 2.1.4.6 in its default installation directory, and the .NET Framework 4.8 included with supported Windows releases.

### macOS

1. Open `WeTypeSkinStudio-macOS-Intel.dmg`.
2. Drag **WeType Skin Studio.app** to Applications and open it.

The release package supports Intel Macs running macOS 13 or later. The app uses an ad-hoc signature and is not notarized with Developer ID. Do not disable SIP or Gatekeeper to run it. Optional keyboard-state animations may require Accessibility permission for the installed app.

## Use and recovery

### Windows

Edit a preset or use the color, image, and pet panels. Images can be dragged to adjust their position. PNG, JPEG, BMP, and the first frame of GIF are supported. Images are limited to 8 MB and 4096 pixels per side.

The theme draft is saved in the current user's application data. Closing the editor does not remove an applied skin. Reopen the editor to inspect the installed state or click **Restore official** to recover the backed-up candidate panel and toolbar files.

The companion pet runs separately and can remain visible after the editor closes. It follows the candidate panel and hides while that panel is unavailable. It does not read candidate text or typed input.

### macOS

Editing, preview, import, and export work without changing the official input method. The native experiment installs a copy under the current user's `~/Library/Input Methods` directory. The system must recognize and select the **WeType Skin** input source before the experiment can be applied.

Restore from the editor with **Restore official**. The app switches back to the official WeChat input source before stopping the copy. The official input method bundle is not patched or re-signed. The companion pet runs independently and can remain open after the editor quits.

## Support and limitations

- Windows native patching supports only WeChat Input Method 2.1.4.6 in its default location. Other versions or modified files are rejected.
- The macOS package targets Intel. Apple Silicon requires a separate build and validation.
- macOS image rendering, transparency, toolbar styling, multi-display tracking, and some input-state animations are experimental or incomplete.
- Windows binaries are not commercially code-signed. The macOS app is not notarized.
- Save a copy of important themes before applying native changes. The app keeps an official backup and provides a restore operation.

See [release notes](docs/RELEASE-2026-10-06.md), [architecture](docs/ARCHITECTURE.md), [verification](docs/VERIFICATION.md), and the [macOS guide](macos/README.md) for implementation and test boundaries.

## Build from source

### Windows

Use Windows PowerShell and the .NET Framework C# compiler. The repository includes the WebView2 SDK files required to build the editor.

```powershell
.\build.ps1 -OutputDirectory build\local
```

To package the installer, provide an offline WebView2 installer signed by Microsoft Corporation:

```powershell
.\package-installer.ps1 `
  -WebView2Installer C:\Downloads\MicrosoftEdgeWebView2RuntimeInstallerX64.exe
```

### macOS

Use macOS 13 or later with Xcode or Command Line Tools that provide Swift 5.9 or later. The scripts build for the current Mac architecture and do not download build dependencies.

```bash
bash macos/build.sh
bash macos/check.sh
bash macos/package.sh
```

## License

The project code is licensed under the [MIT License](LICENSE). Third-party components and animation assets have their own licenses and notices; see `vendor/webview2/` and `assets/pet/`.
