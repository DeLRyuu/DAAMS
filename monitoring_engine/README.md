# DAAMS Monitoring Engine

Windows filesystem monitoring engine for DAAMS.

## Launch

Run the monitor from this directory with `python main.py`. Windows context menu integration and CLI utilities are run via:
- `python windows/context_menu_setup.py [install|uninstall]`
- `python cli/change_pin_cli.py`
- Explorer context menu actions run `cli/protect_asset_cli.py` and `cli/remove_protection_cli.py`.

## Layout

- `config/` contains settings.
- `core/` contains monitoring, asset registry, and risk assessment logic.
- `services/` contains logging, notification, and incident services.
- `ui/`, `cli/`, and `windows/` contain presentation and platform entry points.
- `data/` contains the local asset registry and PIN settings.
- `logs/` contains activity, alert, incident, and CLI diagnostic logs.

Persistent state is resolved relative to the engine package, not the process working directory. The existing `serviceAccountKey.json` credential remains at the engine root and is ignored by Git. Configure an alternate credential path with `DAAMS_FIREBASE_CREDENTIALS`.

Install dependencies with `python -m pip install -r requirements.txt`.

## Activity Detection Table

The monitoring engine captures file and directory actions on protected assets through specialized platform mechanisms:

| Action | Detection Mechanism | Implementation Component | Technical Details |
| :--- | :--- | :--- | :--- |
| **Open** | Windows Security Audit Log (Event ID 4663) | `core/open_audit_watcher.py` | Queries security audit log for `AccessMask` ReadData events. Requires Administrator privileges and SACL audit policy. Ignores system indexer, antivirus, and Explorer thumbnail generation. |
| **Modify** | Win32 Directory Change Notifications | `watchdog` + `core/event_aggregator.py` | Captures native `FileModifiedEvent` / `DirModifiedEvent` via Win32 `ReadDirectoryChangesW`. Coalesces sequential write bursts during quiet window (`COALESCE_QUIET_SECONDS`). Also confirmed via periodic disk scans. |
| **Rename** | Win32 Directory Change Notifications | `watchdog` + `core/event_aggregator.py` | Matches native `FileMovedEvent` / `DirMovedEvent` source and destination paths within the same parent folder or tracked root hierarchy. |
| **Copy** | Content Hash Matching & Windows Clipboard | `core/event_aggregator.py` & `core/clipboard_watcher.py` | Detected via dual pathways: (1) Windows Clipboard watcher intercepts `CF_HDROP` with `Preferred DropEffect` `DROPEFFECT_COPY` (1); (2) Event aggregator identifies newly created files matching known SHA-256 hashes of tracked assets. |
| **Move** | File Relocation & Windows Clipboard Cut | `core/event_aggregator.py` & `core/clipboard_watcher.py` | Detected via: (1) Windows Clipboard watcher intercepting `CF_HDROP` with `DROPEFFECT_MOVE` (2) upon user Cut (`Ctrl+X`); (2) Event aggregator temporal pairing matching a Delete and Create event across paths with matching SHA-256 hash. |
| **Delete** | Win32 Directory Change Notifications | `watchdog` + `core/event_aggregator.py` | Captures native `FileDeletedEvent` / `DirDeletedEvent`. Aggregator debounces against potential move pairing before emitting. Disappeared files are also detected via periodic registry reconciliation scans. |

## Open Detection Setup

Open (file read) detection is powered by the Windows Security audit log (Event ID 4663).

- **Audit Setup**: Run `powershell -ExecutionPolicy Bypass -File tools/enable_open_audit.ps1 -Path <FileOrFolder>` as Administrator to enable File System auditing (`auditpol /set /subcategory:'File System' /success:enable`) and apply a `ReadData` Success audit rule for `Everyone` to the target path.
- **Administrator Requirement**: Querying the Windows Security log (`wevtutil qe Security`) requires elevated administrator rights. If the engine is launched without elevation, Open detection is disabled (`Open detection is off: run the engine as Administrator`) while all other monitoring continues running normally.
- **Reliability Note**: Open detection depends on Windows audit policies, ACL inheritance, and event generation timings rather than direct filesystem hooks.

## Known Limitations

1. **Cut Reporting Timing**: When a file is cut (`Ctrl+X`), the action is detected and recorded as `Move` at the moment it is placed on the clipboard, before the user executes paste into a destination.
2. **Administrator Privilege for Open Detection**: Querying Windows Event ID 4663 requires running the engine with elevated administrator permissions. Non-elevated execution monitors Create, Modify, Rename, Delete, Copy, and Move, but disables Open detection.
3. **Protected Roots Boundary**: Only files and subdirectories located within explicitly registered protected assets are monitored. Outside filesystem activity is ignored.
4. **Authorization / Session Status Unscored**: `session_status` is accepted by the data schema but contributes 0 risk points, as standalone Windows workstations lack authoritative identity-provider session claims.
5. **In-Memory Burst Tracking**: Velocity tracking (3 actions within 60 seconds adding +20 risk points) is maintained in-memory per user session and resets if the engine process restarts.

## Work Hours Configuration

Normal working hours (24-hour clock) can be customized via environment variables:
- `DAAMS_WORK_START`: Integer between 0 and 24 (inclusive) representing the start of work hours. Defaults to `7` (7:00 AM) if unset or invalid.
- `DAAMS_WORK_END`: Integer between 0 and 24 (exclusive) representing the end of work hours. Defaults to `19` (7:00 PM) if unset or invalid.

Activity outside these hours incurs after-hours risk points (+25).

## Email Setup

Email notifications for High and Critical security alerts are configured via environment variables:
- `DAAMS_EMAIL_ENABLED`: Set to `true` to enable email sending (default: `false`).
- `DAAMS_SMTP_HOST`: Hostname of the SMTP relay.
- `DAAMS_SMTP_PORT`: SMTP port (e.g., `587` for STARTTLS or `465` for SSL/TLS; defaults to `587`).
- `DAAMS_SMTP_USERNAME`: Username for SMTP server authentication.
- `DAAMS_SMTP_PASSWORD`: App password or account password for SMTP server authentication.
- `DAAMS_NOTIFICATION_EMAIL`: Recipient email address for alert notifications.

Use `python tools/send_test_email.py` to test your SMTP configuration.
