# Versioning Progress (concise)

Schema & Storage:
- [x] File-based versions folder structure (Versions/vNNN, versions.json index)
- [x] Extended VersionInfo (guid, size, fileCount, pinned, incomplete, toolVersion, description)
- [x] Migration + schemaVersion guard, atomic save (.tmp -> replace)

Core API:
- [x] CreateVersion auto after backup
- [x] List/Get, size+fileCount calc
- [x] Pin / Unpin, Rename (description update)
- [x] Incomplete lifecycle flag (cleared on finalize)

UI Integration:
- [x] Single main window tabs (Backup, Versions)
- [x] Versions tab: list, pin/unpin, rename, open folder, manual create (gated by changes)
- [x] (Replaced) Highlight latest → now inline selectable cards (no reorder) with in-place Restore button
- [x] Latest version summary shown in Backup tab
- [x] Manual snapshot & benchmark only visible in Debug mode
- [x] Backup Now button gated (debug only)
- [x] Centered large Automatic Backups toggle + cleaner interval UI
- [x] Restore action consolidated (Preview & Restore) per card; latest just another selectable entry
- [x] Removed legacy standalone versions button
- [x] Menu cleanup: spostati test in "Avatar Smart Backup Debug" e aggiunta voce "Open"
- [x] In-place selection via click sul box (background highlight); ordine lista preservato
- [ ] Future: diff view, selective restore UI, delete/export, pinned-aware cleanup settings

Next Focus (ordered):
1. Restore service (safe snapshot + bulk restore)
2. Diff service (basic changed/new/deleted detection) + UI
3. Cleanup policy (respect pinned + max count setting)
4. Diagnostics (rebuild index, verify folders)
5. Optional hash/cached integrity (deferred)

Notes:
- Legacy 1.0.4 window archived in worktree, used for layout parity.
- Snapshot policy now adds Manual option gating button enable state.
 - Root menu poteva risultare categoria a causa dei sotto-menu; aggiunta entry "Open" per robustezza.
