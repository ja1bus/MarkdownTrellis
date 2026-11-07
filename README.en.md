# MarkdownTrellis

English | [日本語](README.md)

MarkdownTrellis is a Notepad++ plugin that makes Markdown easier to edit. It adds formatting buttons and a live preview, and copies a document either exactly as written or with formatting for pasting into Teams and similar apps. It also helps you review and touch up Markdown produced by tools such as ChatGPT.

## Features

- Preview: updates as you type and appears in a Notepad++ docking panel.
- Formatting buttons: bold, italic, strikethrough, inline code, headings, bulleted list, numbered list, checklist, quote, code block, table, link, image, horizontal rule
- Copy Source: copies the whole document exactly as written, including unsaved changes.
- Copy Formatted: copies the preview with its formatting. Paste it into Teams, an email, and so on to keep headings, tables, bold text, and links.
- Base syntax: switch between GFM (default) and CommonMark. Frontmatter is not shown in the preview.
- Extra syntax: enable alerts, footnotes, math (shown as text), containers, highlight, wiki links, and definition lists per tab.
- Outer code fence detection: notifies you when the whole document is wrapped in ```` ```markdown ````. You can remove just the two outer lines (undoable).
- Display language: English and Japanese (follows the Windows display language by default)

Editing, saving, and undo use Notepad++ itself. The preview is read-only. Editing in the preview, rendering math or Mermaid, and exporting to Word or PDF are not supported.

## Privacy and safety

- Document content is not sent anywhere. There is no automatic update or update check.
- The preview runs no scripts and shows only allowed tags and attributes. Tags that are not allowed are shown as text.
- External images are not loaded. Local images are shown only from under the document's folder.
- Links open in your default browser only when you click them.

## Requirements

- Windows 11 (64-bit, x64)
- Notepad++ 64-bit (installer or portable). Tested version: 8.9.8.1
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- .NET does not need to be installed.

### PCs where WebView2 cannot start

If WebView2 cannot start, the plugin automatically switches to a fallback preview that uses the IE engine. The fallback differs as follows:

- Links and images are shown as text.
- The panel uses standard Windows buttons. Formatting, inserting, copying, base syntax, and extra syntax remain available.
- Notifications such as outer code fence detection, syntax normalization, and language switching are not available from the panel. Removing an outer code fence is still available from the menu.
- The IE engine is less protected than WebView2 and may be removed from Windows in the future.

On PCs that switch to the fallback preview, each startup waits while WebView2 is tried and fails. Check **Plugins** > **MarkdownTrellis** > **Use IE Engine** to use the fallback preview without trying WebView2 (applies after restarting Notepad++).

## Installation

Download `MarkdownTrellis-<version>-x64.zip` from [Releases](https://github.com/ja1bus/MarkdownTrellis/releases/latest). For step-by-step instructions with screenshots, see the [user guide](https://www.ja1bus.com/markdowntrellis/en/manual/#install).

The DLL is not digitally signed, so note the following:

- Security software or organization policies may block it from loading.
- To confirm that your download matches the Release, compare the output of this command with the value in the `.sha256` file.

```powershell
Get-FileHash .\MarkdownTrellis-<version>-x64.zip -Algorithm SHA256
```

- Unblock the ZIP before extracting it. Right-click the ZIP, open **Properties**, select **Unblock**, and click **OK**. You can also use this command:

```powershell
Unblock-File .\MarkdownTrellis-<version>-x64.zip
```

### Portable Notepad++

No administrator rights are required.

1. Close Notepad++.
2. In the `plugins` folder next to `notepad++.exe`, create a folder named `MarkdownTrellis`.
3. Extract everything in the ZIP into that folder.

```text
<folder of notepad++.exe>\plugins\MarkdownTrellis\MarkdownTrellis.dll
```

### Installed Notepad++

Administrator rights are required only to copy files into `Program Files`, not to use the plugin afterward.

1. Close Notepad++.
2. Extract the ZIP to a location you can write to.
3. In File Explorer, open `C:\Program Files\Notepad++\plugins` and create a folder named `MarkdownTrellis` (allow the prompt if one appears).
4. Copy all extracted files into that folder.

```text
C:\Program Files\Notepad++\plugins\MarkdownTrellis\MarkdownTrellis.dll
```

### Updating and returning to an earlier version

Close Notepad++ and overwrite the same folder with the contents of the ZIP for the version you want. There is no automatic update. To check the installed version, look at the panel title or open **Plugins** > **MarkdownTrellis** > **About MarkdownTrellis**.

### Uninstalling

Close Notepad++ and delete the `plugins\MarkdownTrellis` folder. To also remove settings and WebView2 data, delete `%LOCALAPPDATA%\MarkdownTrellis`.

## Basic usage

1. Start Notepad++. Show the panel with the toolbar icon (a white M on a green square) or from **Plugins** > **MarkdownTrellis**.
2. The preview appears automatically for Markdown files such as `.md` and for untitled documents.
3. Select text and press a panel button to apply formatting. Editing and copy commands are also on the menu.
4. Copy with **Copy Source** or **Copy Formatted**.

The plugin assigns no default shortcuts. You can assign them in the Notepad++ Shortcut Mapper.

### Settings

Settings are stored in `%LOCALAPPDATA%\MarkdownTrellis\settings.json`. Errors are recorded in `error.log` in the same folder.

| Setting | Description |
|---|---|
| `PreviewEngine` | `auto` (default) or `ie` (always use the fallback preview). You can also switch it with **Use IE Engine** on the menu |
| `ExtraHtmlTags` | Additional HTML tags to allow in the preview. Tags forbidden for safety cannot be allowed |

## Building

### Requirements

- Windows 11 x64
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) (`global.json` selects 10.0.403 or a later 10.0 patch)
- Publishing the DLL (`dotnet publish`) also requires the Desktop development with C++ workload of [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/) ([Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)). `dotnet build` and `dotnet test` do not.
- NuGet packages are restored from nuget.org on the first build.

### Commands

Run these from the repository root.

```powershell
dotnet build MarkdownTrellis.csproj -c Release
dotnet test Tests/MarkdownTrellis.Tests.csproj -c Release

# Publish the DLL to publish\
pwsh -File tools\publish-plugin.ps1

# Publish the DLL and deploy it to a Notepad++ folder (close Notepad++ first)
pwsh -File tools\publish-plugin.ps1 -NppDir '<folder of notepad++.exe>'

# Create the Release ZIP and SHA-256 in publish\release\
pwsh -File tools\package-release.ps1
```

### Layout

| Path | Description |
|---|---|
| `MarkdownTrellis.csproj` | The plugin (NativeAOT shared library) |
| `Plugin/` | Integration with Notepad++, Scintilla, WebView2, the IE engine, and the clipboard |
| `Core/` | Logic independent of Notepad++ (Markdown conversion, HTML checks, formatting operations, display strings) |
| `Assets/shell.html` | Panel toolbar and preview frame (embedded in the DLL) |
| `Tests/` | Unit tests for `Core` (xUnit) |
| `tools/` | Publishing the DLL and creating the Release ZIP |

Comments in the source code are mainly in Japanese.

## License

Released under the [MIT License](LICENSE).

Copyright (c) 2026 ja1bus (© ja1bus)

For third-party software, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Notepad++ and Microsoft Teams are products of their respective owners. This plugin is not provided or endorsed by their developers.

## No warranty

This software is provided "as is", without warranty of any kind, express or implied. The author is not liable for any damages arising from its use (see [LICENSE](LICENSE) for details).

## Support

This project does not provide a contact point for questions or bug reports.

## Links

- Releases: https://github.com/ja1bus/MarkdownTrellis/releases
- User guide: https://www.ja1bus.com/markdowntrellis/en/manual/
- Microsoft Edge WebView2: https://developer.microsoft.com/microsoft-edge/webview2/
