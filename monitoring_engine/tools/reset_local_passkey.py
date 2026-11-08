"""
reset_local_passkey.py

Removes this PC's LOCAL PIN copy, so the Monitoring Engine only ever uses a Passkey PIN
that it downloaded from the Control Center (Settings -> Passkey PIN).

Use it once to get rid of an old "normal" PIN (the one from before Passkey PIN existed).
It does NOT touch Firestore. After running it:
  - If the Control Center has a Passkey, this engine downloads it again within ~30 seconds
    (the PC must be online and the engine running).
  - Until then, Protect This File/Folder is blocked ("No Passkey PIN yet").

What it removes:
  1. data/security_settings.json            (local JSON copy; a .bak is kept)
  2. the 'pin_config' row in security_settings (SQLite)

Usage (close the Monitoring Engine first):
    python tools/reset_local_passkey.py          # asks for confirmation
    python tools/reset_local_passkey.py --yes    # no question
"""

import os
import shutil
import sys

_ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ENGINE_DIR not in sys.path:
    sys.path.insert(0, _ENGINE_DIR)

from cli import pin_auth
from core import database


def reset_local_passkey() -> dict:
    """Delete the local PIN copies. Returns what was removed. Never touches Firestore."""
    result = {"json_removed": False, "db_row_removed": False}

    json_path = pin_auth.SETTINGS_FILE
    if os.path.isfile(json_path):
        shutil.copy2(json_path, json_path + ".bak")
        os.remove(json_path)
        result["json_removed"] = True

    database.initialize_database()
    with database._DB_LOCK:
        conn = database.get_connection()
        try:
            with conn:
                cur = conn.execute("DELETE FROM security_settings WHERE key = 'pin_config';")
                result["db_row_removed"] = cur.rowcount > 0
        finally:
            conn.close()

    return result


def main(argv=None) -> int:
    argv = sys.argv[1:] if argv is None else argv

    if "--yes" not in argv:
        print("This removes the local PIN copy on this PC (Firestore is not touched).")
        print("Make sure the Monitoring Engine is closed.")
        if input("Type YES to continue: ").strip() != "YES":
            print("Cancelled. Nothing was changed.")
            return 1

    result = reset_local_passkey()
    print(f"security_settings.json removed : {result['json_removed']}")
    print(f"SQLite pin_config row removed  : {result['db_row_removed']}")
    print("Done. Start the engine online and it will download the Passkey from the Control Center.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
