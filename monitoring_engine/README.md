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
