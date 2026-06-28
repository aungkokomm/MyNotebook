<h1 align="center">📓 My Notebook</h1>

<p align="center">
  <b>A fast, private, local-first notebook for Windows 11.</b><br>
  Rich notes and screenshot threads with OCR-searchable images, a Myanmar-aware search that
  actually works, color themes, export anywhere, and a built-in voice reader — all in a
  portable folder. No account. No cloud. Your notes stay yours.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2011-0078D6" alt="Windows 11">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8">
  <img src="https://img.shields.io/badge/UI-WinUI%203-5C2D91" alt="WinUI 3">
  <a href="../../releases"><img src="https://img.shields.io/github/v/release/aungkokomm/MyNotebook?label=download" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="MIT License"></a>
</p>

---

## ⬇️ Download
![GitHub all releases](https://img.shields.io/github/downloads/aungkokomm/MyNotebook/total)

Head to the [**Releases**](../../releases) page and run the latest
`MyNotebook_Setup_vX.Y.Z.exe`. It installs **without admin rights**, runs **portable**, and
**never touches your data** on upgrade. First launch even comes with a few sample notes.

## 🔒 Where your notes live (and why they're safe)

My Notebook is **fully portable and offline**. Every note, screenshot, tag, and setting is stored
in a plain **`Data/` folder right next to the app** — no cloud, no account, no database server.
That means:

- **Uninstalling or re-downloading the app does *not* delete your notes.** The uninstaller removes
  the program but **leaves your `Data/` folder intact** — reinstall to the same folder and everything
  is exactly where you left it.
- **Moving to a new PC?** Copy the app folder (or just its `Data/` folder) across — notes, images,
  and folders all come with it. You can even run it straight off a USB stick.
- **Back up anytime** from **Settings → Back up to `.zip`**, or simply copy the `Data/` folder to a
  USB drive or a cloud-synced folder.

> ⚠️ Because your notes live **only on your machine**, they are as safe as your machine. If your
> drive fails and you have no copy, they're gone — so keep the app (or its `Data/` folder) somewhere
> that gets backed up, or take a `.zip` backup now and then. Your notes are *yours* — which also means
> their safety is in your hands, not a company's servers.

## 🔄 Updates are manual (on purpose)

My Notebook **doesn't update itself** — and that's a deliberate choice, not a missing feature.

Auto-updaters work by silently **downloading and running a new executable** in the background. That's
exactly the behaviour antivirus engines and **VirusTotal** treat as suspicious, so self-updating apps
routinely get **false-positive malware flags** and scary SmartScreen warnings. To keep My Notebook
clean, transparent, and trustworthy, there's simply no auto-update machinery to flag.

Instead, updating is a 30-second manual step:
1. Open **About → Check for updates** (or the [Releases](../../releases) page).
2. Download the latest `MyNotebook_Setup_vX.Y.Z.exe` and run it **over your current install**.
3. Your `Data/` folder is never touched — all your notes carry straight over.

The app shows a clear in-app reminder of this, and keeps **rolling local backups** of your notes
(`Data/Backups/`) just in case.

## 🤔 Why another note app?

Because the popular ones each ask you to give something up:

- **Notion / Evernote** keep your notes on *their* servers, behind an account, syncing whether you
  like it or not. My Notebook has **no account, no cloud, no telemetry** — your notes are plain files
  in a folder you control.
- **Obsidian / Notion** are Electron apps that feel heavy on Windows. This is a **native WinUI 3 app**
  — it starts fast and stays light.
- **OneNote and Apple Notes** can search text in images too — but OneNote leans on the Microsoft cloud
  and account, and Apple's Live Text is locked to the Apple ecosystem. My Notebook OCR-indexes every
  screenshot **fully offline, no account**, in a screenshot-thread workflow built for Windows.
- **Almost none of them search Myanmar properly.** Myanmar text has no spaces between words, so normal
  full-text search misses it. My Notebook pairs a trigram index with full-text search so it finds a
  word **in the middle of a Myanmar sentence** — and partial words in any language.

If you want a notebook that is **fast, private, genuinely yours, and actually finds what you wrote**
— including in Burmese and inside screenshots — that gap is the reason this exists.

## ✨ Why you'll like it

- **It's yours.** Everything lives in a portable `Data/` folder next to the app — copy it to a
  USB stick, sync it with your own cloud folder, back it up as a `.zip`. No sign-in, no telemetry.
- **Search that finds things.** A hybrid full-text + trigram index finds a word **in the middle
  of spaceless Myanmar text**, partial words, and even **text inside your screenshots** (OCR).
  Ranked results, highlighted snippets, jump-to-match, and operators like `"phrase"`, `-exclude`,
  `title:`, `tag:`, `type:`.
- **A real editor.** Paste a screenshot and it lands inline at **full resolution**, clickable, where
  your cursor is — not a blurry thumbnail. **Paste a whole web article** and the formatting *and*
  images come with it — images are downloaded into the note so it stays offline. Headings, lists,
  checklists, highlight, color, fonts.
- **A workspace, not a list.** A **two-pane drawer** (folders rail + note list) you can collapse
  either side of, **multi-select** notes (Ctrl/Shift-click) to move, pin, or delete in bulk, drag
  notes onto folders, and a **Timeline** to browse everything by date.
- **Made beautiful.** Light/dark plus **eight color themes** that recolor the whole UI (accent +
  matching page tint), with an intensity dial. Distraction-free focus mode when you need it.

## 🧰 Everything it does

| | |
|---|---|
| 🗂️ **Workspace** | Two-pane drawer (folders + note list), collapsible panels, multi-select + bulk move/pin/delete, drag-to-folder, sort |
| ✍️ **Editor** | Inline full-res images, **rich web paste** (formatting + images, kept offline), **auto-titles a new note from the pasted heading**, fonts, checklists, page styles |
| 🖼️ **Screenshot threads** | Paste, drag-to-reorder, OCR-indexed, 100% floating viewer, export all |
| 🔎 **Search** | Hybrid FTS5 + trigram (Myanmar/substring/OCR), operators, highlighted results |
| 🔗 **Knowledge** | `[[wiki-links]]` + backlinks, OneNote-style web clipper (keeps the source URL) |
| 🎨 **Themes** | Light/dark + 8 color themes, intensity slider, custom page colors & rule lines |
| 📤 **Export** | PDF, Word `.docx`, self-contained HTML ("publish as a web page"), print |
| 🔊 **Voice** | Read a note aloud (TTS) and record audio into a note |
| 🕘 **Safety** | Automatic version history with restore, soft-delete trash, `.zip` backup |
| 🕓 **Timeline** | Browse every note by date — grouped into Today/Yesterday/this month/older, with zoom-out to jump between periods |
| ⚡ **Polish** | Focus mode, pin/tag/move, close-to-tray, global Quick Note hotkey, settings window |

## 🖼️ Screenshots

<p align="center">
 <img width="960" height="509" alt="image" src="https://github.com/user-attachments/assets/b19c7221-955b-4a22-9482-fd2f8acf2096" />

<img width="960" height="510" alt="image" src="https://github.com/user-attachments/assets/084f4d92-a1ec-4f5f-9a2b-56d6bde6cd4c" />


<img width="464" height="401" alt="image" src="https://github.com/user-attachments/assets/3a069e2a-129d-40d9-a4d0-49f36948c153" />

<img width="446" height="383" alt="image" src="https://github.com/user-attachments/assets/e5de20d1-087f-4de3-acdb-4e0b80f9bdb8" />



</p>

## 🛠️ Tech

**C# / .NET 8** · **WinUI 3** (Windows App SDK, self-contained & unpackaged) · **SQLite + FTS5**
with a trigram index · **WebView2** editor · `Windows.Media.Ocr` · QuestPDF / PdfSharp (PDF) and
OpenXML (`.docx`). Core logic lives in `MyNotebook.Core` (no WinUI dependency) and is covered by
xUnit tests.

```
MyNotebook/
├─ db/migrations/     SQLite schema + migrations (embedded at build)
├─ db/seed/           self-verifying seed-DB builder (sample notes + screenshots)
├─ installer/         Inno Setup script (portable, lowest-privilege)
├─ src/
│  ├─ MyNotebook.Core/   net8.0 — models, services, SQLite, search (unit-tested)
│  ├─ MyNotebook.App/    WinUI 3 — UI, editor, OCR, export
│  └─ MyNotebook.Tests/  xUnit — services, search, versioning, integration
└─ screenshots/
```

## 🏗️ Build from source

Requires **Windows 11 x64** and the **.NET 8 SDK**. Build the WinUI app with **Visual Studio
MSBuild**, not `dotnet build` — the .NET CLI MSBuild lacks the WinUI PRI-packaging task
(`MSB4062`).

```powershell
# Run the Core tests (works anywhere with .NET 8)
dotnet test src/MyNotebook.Tests/MyNotebook.Tests.csproj

# Build & publish the portable, self-contained app (Windows)
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
  src/MyNotebook.App/MyNotebook.App.csproj -t:Publish `
  -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
  -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None `
  -p:PublishDir=bin/publish/win-x64/

# Build the installer (optional — needs Inno Setup 6)
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer/MyNotebook.iss
```
The published folder is portable — copy it anywhere. On first run the app writes to `.\Data\`
next to the exe, falling back to `%LOCALAPPDATA%\MyNotebook` if that's read-only.

## ⌨️ Shortcuts

| Keys | Action |
|---|---|
| `Ctrl+F` / `Ctrl+K` | Focus search |
| `Ctrl+H` | Find & replace in the note |
| `Ctrl+N` | New note |
| `Ctrl+T` | New screenshot thread |
| `Ctrl+V` | Paste an image (inline in a note, or a card in a thread) |
| `F11` / `Esc` | Toggle / leave focus mode |
| `Win+Alt+N` | Quick Note (global, optional) |

## 📄 License

Released under the [MIT License](LICENSE).

---

<p align="center"><sub>Built for Windows 11 · local-first · made for notes that stay yours.</sub></p>
