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