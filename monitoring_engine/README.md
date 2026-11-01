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

## Open detection

Open (file read) detection is powered by the Windows Security audit log (Event ID 4663).

- **Audit Setup**: Run `powershell -ExecutionPolicy Bypass -File tools/enable_open_audit.ps1 -Path <FileOrFolder>` as Administrator to enable File System auditing (`auditpol /set /subcategory:'File System' /success:enable`) and apply a `ReadData` Success audit rule for `Everyone` to the target path.
- **Administrator Requirement**: Querying the Windows Security log (`wevtutil qe Security`) requires elevated administrator rights. If the engine is launched without elevation, Open detection is disabled (`Open detection is off: run the engine as Administrator`) while all other monitoring continues running normally.
- **Reliability Note**: Open detection is the least certain detection feature. It depends on Windows audit policies, ACL inheritance, and event generation timings rather than direct filesystem hooks.

## Work Hours Configuration

Normal working hours (24-hour clock) can be customized via environment variables:
- `DAAMS_WORK_START`: Integer between 0 and 24 (inclusive) representing the start of work hours. Defaults to `7` (7:00 AM) if unset or invalid.
- `DAAMS_WORK_END`: Integer between 0 and 24 (exclusive) representing the end of work hours. Defaults to `19` (7:00 PM) if unset or invalid.

Activity outside these hours incurs after-hours risk points.

## Email Setup

Email notifications for High and Critical security alerts are configured via environment variables:
- `DAAMS_EMAIL_ENABLED`: Set to `true` to enable email sending (default: `false`).
- `DAAMS_SMTP_HOST`: Hostname of the SMTP relay.
- `DAAMS_SMTP_PORT`: SMTP port (e.g., `587` for STARTTLS or `465` for SSL/TLS; defaults to `587`).
- `DAAMS_SMTP_USERNAME`: Username for SMTP server authentication.
- `DAAMS_SMTP_PASSWORD`: App password or account password for SMTP server authentication.
- `DAAMS_NOTIFICATION_EMAIL`: Recipient email address for alert notifications.

Use `python tools/send_test_email.py` to test your SMTP configuration.
