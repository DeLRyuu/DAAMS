# DAAMS

## Digital Asset Access Monitoring and Risk Assessment System

DAAMS is a Windows-based security monitoring utility designed to monitor administrator-selected protected digital assets, assess potential security risks, record activity, and generate security alerts.

## Project Status
🚧 Currently in Development

## Main Features
- Protected File and Folder Monitoring
- Digital Asset Classification (Public, Sensitive, Private, Confidential)
- File/Folder Activity Logging
- Risk Assessment and Explainable Scoring
- Security Alerts & Incident Reporting
- Email Notifications for High/Critical Alerts
- Offline-First Local Storage (SQLite) with Cloud Sync (Firestore)
- Windows Context Menu Integration & Administrative Control Center

## Activity Detection Table

DAAMS monitors file interactions across protected assets using dedicated platform hooks:

| Action | Detection Mechanism | Implementation Component | Technical Details |
| :--- | :--- | :--- | :--- |
| **Open** | Windows Security Audit Log (Event ID 4663) | `core/open_audit_watcher.py` | Queries security audit log for `AccessMask` ReadData events. Requires Administrator privileges and SACL audit policy. Ignores system indexer, antivirus, and Explorer thumbnail generation. |
| **Modify** | Win32 Directory Change Notifications | `watchdog` + `core/event_aggregator.py` | Captures native `FileModifiedEvent` / `DirModifiedEvent` via Win32 `ReadDirectoryChangesW`. Coalesces sequential write bursts during quiet window (`COALESCE_QUIET_SECONDS`). Also confirmed via periodic disk scans. |
| **Rename** | Win32 Directory Change Notifications | `watchdog` + `core/event_aggregator.py` | Matches native `FileMovedEvent` / `DirMovedEvent` source and destination paths within the same parent folder or tracked root hierarchy. |
| **Copy** | Content Hash Matching & Windows Clipboard | `core/event_aggregator.py` & `core/clipboard_watcher.py` | Detected via dual pathways: (1) Windows Clipboard watcher intercepts `CF_HDROP` with `Preferred DropEffect` `DROPEFFECT_COPY` (1); (2) Event aggregator identifies newly created files matching known SHA-256 hashes of tracked assets. |
| **Move** | File Relocation & Windows Clipboard Cut | `core/event_aggregator.py` & `core/clipboard_watcher.py` | Detected via: (1) Windows Clipboard watcher intercepting `CF_HDROP` with `DROPEFFECT_MOVE` (2) upon user Cut (`Ctrl+X`); (2) Event aggregator temporal pairing matching a Delete and Create event across paths with matching SHA-256 hash. |
| **Delete** | Win32 Directory Change Notifications | `watchdog` + `core/event_aggregator.py` | Captures native `FileDeletedEvent` / `DirDeletedEvent`. Aggregator debounces against potential move pairing before emitting. Disappeared files are also detected via periodic registry reconciliation scans. |

## Known Limitations

1. **Cut Reporting Timing**: When a file is cut (`Ctrl+X`), the action is detected and recorded as `Move` at the moment it is placed on the clipboard, before the user executes paste into a destination.
2. **Administrator Privilege for Open Detection**: Querying Windows Event ID 4663 requires running the engine with elevated administrator permissions. Non-elevated execution monitors Create, Modify, Rename, Delete, Copy, and Move, but disables Open detection.
3. **Protected Roots Boundary**: Only files and subdirectories located within explicitly registered protected assets are monitored. Outside filesystem activity is ignored.
4. **Authorization / Session Status Unscored**: `session_status` is accepted by the data schema but contributes 0 risk points, as standalone Windows workstations lack authoritative identity-provider session claims.
5. **In-Memory Burst Tracking**: Velocity tracking (3 actions within 60 seconds adding +20 risk points) is maintained in-memory per user session and resets if the engine process restarts.

## Demonstration Runbook

A complete 5-minute step-by-step presentation script, scoring calculations, pre-demo checklist, and fallback procedures are available in [`monitoring_engine/DEMO.md`](monitoring_engine/DEMO.md).

## Technology Stack
- Python — Monitoring Engine (`watchdog`, `pywin32`)
- C# WPF (.NET 10) — Administrator Control Center
- SQLite (WAL Mode) — Offline-First Local Database
- Firebase Firestore — Cloud Sync
- Windows — Target Platform

## Development
The system is being developed incrementally. Features and components are documented through Git commits.

## Team
- Project Leader / Developer — Whindell
- UI/UX Designer — Cyy Opiz
- Researcher — Joy Aguirre Jasar eno
- Documentation Lead — Elna
- Testing / QA — Melisa Mae Franes
