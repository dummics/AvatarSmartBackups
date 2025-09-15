# Versioning Progress (short)

Core Done:
- File-based versions + atomic index
- Extended metadata (guid, size, fileCount, pinned, incomplete)
- Auto create version after backup
- Pin / Rename / Integrity basic checks
- Unified window + diff + selective restore (grouping, filters)
- MD5 manifest compare (detect Changed when size same)

Pending UI / Features:
- Delete version from UI
- Export / Import version (zip)
- Expose cleanup policy (respect pinned)

Performance Roadmap:
1. HashCache in restore (enable) + optional xxHash64
2. Remove full Assets enumeration in diff
3. Idle parallel hash precompute
4. (If needed) UI virtualization >5k files

Dedup Roadmap (Git-lite):
- Phase 0 DONE: full copies
- Phase 1: Optional object store (`Objects/`) for large files (flag)
- Phase 2: Retroactive dedupe job
- Phase 3: Delta (maybe) for large text/YAML (low priority)
- Phase 4: GC unused objects

Design Notes:
- Linear versions only (no branches)
- Hash preference: xxHash64 (speed) + keep MD5 for legacy
- Reference format draft: stub file `ASB_REF:<hash>`

Diagnostics (planned):
- Rebuild index button
- Hash store scan + GC
- Log viewer shortcut

Next Immediate Steps:
1. Re-enable HashCache in restore
2. Add cleanup policy UI
3. Introduce experimental dedup flag

Status: Performance step 1 in progress.
