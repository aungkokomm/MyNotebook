# My Notebook (WinUI 3) — Build Plan

Local-first, portable Windows note app. C# .NET 8, WinUI 3 (Windows App SDK),
SQLite + FTS5, `Windows.Media.Ocr`. Self-contained portable **folder**, win-x64, Mica, Windows 11.

## Locked decisions
| Area | Decision |
|---|---|
| Packaging | Self-contained **folder** (not single .exe); `WindowsAppSDKSelfContained=true`, `WindowsPackageType=None` |
| Data root | `./Data/` next to exe; write-probe → fall back to `%LOCALAPPDATA%\MyNotebook` |
| Notes | `body_rtf` (RichEditBox) + derived `body_plain` → FTS |
| Delete | Soft-delete (`deleted=1`), PNGs retained; "empty trash" purges later |
| Min OS | Windows 11 (Mica native) |
| Tests | Services + DB + paste→save→open integration (no UI automation in MVP) |

## Architecture (layering enables verifiable tests)
```
MyNotebook.Core   (net8.0, NO WinUI)  ← models, interfaces, SqliteStorageService,
                                         NoteService, PathService, NullSyncService.
                                         Unit-testable on any runner.
MyNotebook.App    (WinUI 3, net8.0-windows)  ← UI + DI + WindowsOcrService
                                                 (Windows.Media.Ocr lives here only).
MyNotebook.Tests  (net8.0)            ← xUnit against Core (DB/services/integration).
```
OCR is isolated to the App so Core stays portable and fast to test.

## "Best of both" feature set (from OneNote + Apple Notes)
**In MVP (schema already supports):** folders hierarchy · OCR text searchable in FTS ·
forgiving ranked search (bm25 + prefix + diacritic folding) · pinning/sort · Smart Folders.
**Fast-follow (no migration needed):** Quick Note global hotkey · checklists + cross-note
task view · export MD/HTML/PDF · backlinks. **Later:** encryption · ink · version history · sync.

---

## Sprint 0 — De-risk spike (½ day) — *prove the scary things before committing*
- [ ] Self-contained **unpackaged** WinUI app launches (bootstrapper init OK)
- [ ] Clipboard image paste → `BitmapImage` round-trips
- [ ] `OcrEngine.TryCreateFromUserProfileLanguages()` returns non-null on dev box
- **Exit:** all three proven, or fallbacks decided.

## Sprint 1 — Data + services ✅ *(schema + Core delivered)*
- [x] Migrations `001_initial_schema.sql`, FTS5 + triggers
- [x] Seed DB: 10 notes + 10 screenshots, self-verifying builder
- [x] `IPathService` (portable root + write-probe fallback)
- [x] `IStorageService` (SQLite open/migrate), `INoteService` (CRUD + search)
- [x] `NullSyncService` stub
- [x] Unit tests: schema, CRUD, FTS sync, soft-delete, OCR-in-search
- **Acceptance:** all Core tests green; search is ranked, Unicode, prefix.

## Sprint 2 — UI shell + notes
- [ ] `MainWindow`: NavigationView sidebar (folders + Smart Folders), Mica
- [ ] Title-bar inline search box → instant filtered results (Unicode)
- [ ] Notes list (pinned first); `RichEditBox` editor
- [ ] Save path: RTF → extract `body_plain` → persist → FTS updates
- [ ] DI wiring in `App.xaml.cs` (Microsoft.Extensions.DependencyInjection)
- **Acceptance:** create/edit/search a note end-to-end; search filters instantly.

## Sprint 3 — Screenshot thread + OCR + packaging
- [ ] Thread view: vertical image cards w/ timestamp
- [ ] Ctrl+N / Ctrl+T new thread; Ctrl+V paste image → save PNG → Images row → card
- [ ] `WindowsOcrService`: resize when longest edge > 4096px (bilinear), OCR → `Images.ocr_text` → FTS
- [ ] `dotnet publish` self-contained single-folder profile; portable write-probe
- [ ] README: build/publish/run
- **Acceptance:** paste→card p95 ≤ 300ms (warm, SSD); OCR 1024×768 ~1s; portable run from USB.

---

## Acceptance highlights (measurable)
- **Paste:** p95 ≤ 300ms from Ctrl+V to card visible (warm app, local SSD).
- **Search:** Unicode + diacritic-folded, ranked, filters as-you-type.
- **OCR:** 1024×768 → text ~1s warm; images > 4096px longest-edge resized first;
  null engine → clear "install an OCR language pack" message.

## Risks & mitigations
| Risk | Mitigation |
|---|---|
| WinUI single-file unsupported | Ship self-contained **folder** (decided) |
| Unpackaged WinRT/OCR quirks | Sprint 0 spike validates before UI work |
| No OCR language pack on a machine | Graceful null-engine path + user message |
| FTS drift on CRUD | Triggers + tests assert sync (done in Sprint 1) |
| Paste latency budget | Save PNG + insert row sync; OCR runs async after card shows |
