"""
open_audit_watcher.py

Detects file Open (read) events using the Windows Security Audit Log (Event ID 4663).
Requires Administrator rights to read the Security log via wevtutil.
"""

import base64
import logging
import ntpath
import os
import subprocess
import threading
import time
from typing import Any, Callable, Dict, List, Optional, Tuple
import xml.etree.ElementTree as ET

from core.clipboard_watcher import find_covering_asset

logger = logging.getLogger(__name__)

IGNORED_PROCESSES = {
    "explorer.exe",
    "searchindexer.exe",
    "searchprotocolhost.exe",
    "searchfilterhost.exe",
    "msmpeng.exe",
    "python.exe",
    "pythonw.exe",
    # Installed (PyInstaller) builds: the engine reads protected files itself to infer
    # Copy/Move, and the right-click helper touches them too. Never log those as user Opens.
    "daamsengine.exe",
    "daamsprotect.exe",
}

EVENT_XML_NS = {"e": "http://schemas.microsoft.com/win/2004/08/events/event"}

# "File System" audit subcategory GUID. The GUID works on every Windows language;
# the display name "File System" is translated on non-English Windows.
_AUDIT_FILE_SYSTEM_GUID = "{0CCE921D-69AE-11D9-BED3-505054503030}"

# Applies a ReadData/Success audit rule for Everyone (SID S-1-1-0, language independent)
# to one file or folder. Same rule as tools/enable_open_audit.ps1, built into the exe so
# installed PCs do not depend on running that script by hand.
_SACL_SCRIPT = """
$ErrorActionPreference = 'Stop'
$p = '{path}'
$item = Get-Item -LiteralPath $p -Force
if ($item -is [System.IO.DirectoryInfo]) {{
    $inh = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
}} else {{
    $inh = [System.Security.AccessControl.InheritanceFlags]::None
}}
$sid = New-Object System.Security.Principal.SecurityIdentifier('S-1-1-0')
$rule = New-Object System.Security.AccessControl.FileSystemAuditRule($sid, [System.Security.AccessControl.FileSystemRights]::ReadData, $inh, [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AuditFlags]::Success)
$acl = Get-Acl -LiteralPath $p -Audit
$acl.AddAuditRule($rule)
Set-Acl -LiteralPath $p -AclObject $acl
"""

_wevtutil_warned = False


def enable_file_system_auditing() -> bool:
    """Turns on Success auditing for the Windows "File System" subcategory. Needs Administrator."""
    for subcategory in (_AUDIT_FILE_SYSTEM_GUID, "File System"):
        try:
            res = subprocess.run(
                ["auditpol", "/set", f"/subcategory:{subcategory}", "/success:enable"],
                capture_output=True, text=True, check=False, timeout=30,
            )
        except Exception as e:
            print(f"[OPEN AUDIT WARNING] Could not run auditpol: {e}")
            continue
        if res.returncode == 0:
            return True
        print(f"[OPEN AUDIT WARNING] auditpol failed ({res.returncode}): {(res.stderr or res.stdout).strip()}")
    return False


def apply_read_audit_rule(path: str) -> bool:
    """Adds the ReadData/Success audit rule (SACL) to one protected file or folder. Needs Administrator."""
    script = _SACL_SCRIPT.format(path=path.replace("'", "''"))
    encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
    try:
        res = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
             "-EncodedCommand", encoded],
            capture_output=True, text=True, check=False, timeout=60,
        )
    except Exception as e:
        print(f"[OPEN AUDIT WARNING] Could not set audit rule on '{path}': {e}")
        return False
    if res.returncode != 0:
        print(f"[OPEN AUDIT WARNING] Audit rule failed for '{path}': {(res.stderr or res.stdout).strip()[:300]}")
        return False
    return True


def _has_read_data_access(mask_val: Any) -> bool:
    """Check if the AccessMask string/int has bit 0x1 (ReadData) set."""
    if mask_val is None:
        return False
    try:
        if isinstance(mask_val, str):
            mask_val = mask_val.strip()
            mask_int = int(mask_val, 16) if mask_val.lower().startswith("0x") else int(mask_val)
        else:
            mask_int = int(mask_val)
        return bool(mask_int & 0x1)
    except (ValueError, TypeError):
        return False


def parse_events_xml(xml_text: str) -> List[Tuple[Any, Dict[str, str]]]:
    """
    Parses Windows Event XML fragments returned by wevtutil and extracts
    (EventRecordID, dict of EventData fields) for each Event.
    """
    results = []
    if not xml_text:
        return results

    wrapped = f"<root>{xml_text}</root>"
    try:
        root = ET.fromstring(wrapped)
    except Exception as e:
        logger.debug("Error parsing wevtutil XML: %s", e)
        return results

    events = root.findall(".//e:Event", EVENT_XML_NS)
    if not events:
        events = [elem for elem in root if elem.tag.endswith("Event")]

    for event in events:
        record_id = None
        system = event.find("e:System", EVENT_XML_NS)
        if system is None:
            system = next((c for c in event if c.tag.endswith("System")), None)
        if system is not None:
            rec_elem = system.find("e:EventRecordID", EVENT_XML_NS)
            if rec_elem is None:
                rec_elem = next((c for c in system if c.tag.endswith("EventRecordID")), None)
            if rec_elem is not None and rec_elem.text:
                try:
                    record_id = int(rec_elem.text.strip())
                except ValueError:
                    record_id = rec_elem.text.strip()

        event_data: Dict[str, str] = {}
        data_elem = event.find("e:EventData", EVENT_XML_NS)
        if data_elem is None:
            data_elem = next((c for c in event if c.tag.endswith("EventData")), None)
        if data_elem is not None:
            for item in data_elem:
                name = item.attrib.get("Name")
                if name:
                    event_data[name] = item.text or ""

        if record_id is not None or event_data:
            results.append((record_id, event_data))

    return results


def read_recent_reads(seconds: float = 5.0) -> List[Tuple[Any, Dict[str, str]]]:
    """
    Runs wevtutil qe Security with an XPath query for EventID 4663 in the
    last N seconds, format XML, parses it and returns
    (EventRecordID, dict of EventData fields) per event.
    """
    ms = int(seconds * 1000)
    query = f"*[System[(EventID=4663) and TimeCreated[timediff(@SystemTime) <= {ms}]]]"
    cmd = ["wevtutil", "qe", "Security", f"/q:{query}", "/f:xml"]

    try:
        res = subprocess.run(cmd, capture_output=True, text=True, check=False)
        if res.returncode != 0:
            global _wevtutil_warned
            if not _wevtutil_warned:
                _wevtutil_warned = True
                print(f"[OPEN AUDIT WARNING] Cannot read the Windows Security log "
                      f"(wevtutil code {res.returncode}): {res.stderr.strip()}. "
                      "Open detection needs the engine to run as Administrator.")
            logger.debug("wevtutil failed (code %d): %s", res.returncode, res.stderr.strip())
            return []
        output = res.stdout.strip()
        if not output:
            return []
        return parse_events_xml(output)
    except Exception as e:
        logger.debug("Could not query Windows Security audit log: %s", e)
        return []


class OpenAuditWatcher(threading.Thread):
    """
    Polls the Windows Security Audit Log for file Open (ReadData) events (Event 4663).
    Requires Administrator rights. Swallows errors to ensure engine stability.
    """

    def __init__(
        self,
        get_assets: Optional[Callable[[], list]] = None,
        record: Optional[Callable] = None,
        read: Optional[Callable[..., List[Tuple[Any, Dict[str, str]]]]] = None,
        interval: float = 2.0,
        daemon: bool = True,
        auto_setup: bool = False,
    ):
        super().__init__(name="DAAMS-OpenAuditWatcher", daemon=daemon)
        self.get_assets = get_assets or (lambda: [])
        self._record_func = record
        self._read_func = read or read_recent_reads
        self._interval = interval
        self._stop_event = threading.Event()
        self._seen_ids = set()
        self._recent_opens: Dict[str, float] = {}
        # auto_setup: the engine turns on Windows file auditing and the per-asset audit rule
        # itself (needed on installed PCs). Off by default so unit tests never touch Windows.
        self._auto_setup = auto_setup
        self._policy_ok = False
        self._last_setup = 0.0
        self._audited: set = set()
        self._audit_failures: Dict[str, int] = {}

    def _ensure_audit_setup(self) -> None:
        """Makes sure Windows is actually logging Opens for every protected asset (throttled, retried)."""
        now = time.monotonic()
        if now - self._last_setup < 5.0:
            return
        self._last_setup = now

        if not self._policy_ok:
            self._policy_ok = enable_file_system_auditing()
            if not self._policy_ok:
                return
            print("[OPEN AUDIT] Windows File System auditing is enabled.")

        for asset in self.get_assets():
            path = asset.get("path") if isinstance(asset, dict) else None
            if not path or path in self._audited or not os.path.exists(path):
                continue
            if self._audit_failures.get(path, 0) >= 3:
                continue
            if apply_read_audit_rule(path):
                self._audited.add(path)
                print(f"[OPEN AUDIT] Open detection armed for: {path}")
            else:
                self._audit_failures[path] = self._audit_failures.get(path, 0) + 1

    def _get_record_function(self):
        if self._record_func is not None:
            return self._record_func
        from services.activity_logger import record_activity
        return record_activity

    def check_once(self) -> None:
        """
        Polls recent audit events, filters read events on protected assets,
        and records them. Swallows any internal errors.
        """
        try:
            events = self._read_func(seconds=5)
        except TypeError:
            try:
                events = self._read_func()
            except Exception as e:
                print(f"[OPEN AUDIT WARNING] Could not read audit events: {e}")
                return
        except Exception as e:
            print(f"[OPEN AUDIT WARNING] Could not read audit events: {e}")
            return

        if not events:
            return

        assets = self.get_assets()
        record_fn = self._get_record_function()

        for event_id, event_data in events:
            if event_id is not None:
                if event_id in self._seen_ids:
                    continue
                self._seen_ids.add(event_id)
                # Cap the seen set size
                if len(self._seen_ids) > 10000:
                    self._seen_ids.clear()

            # Filter AccessMask bit 0x1 (ReadData)
            mask_val = event_data.get("AccessMask")
            if not _has_read_data_access(mask_val):
                continue

            # Filter ignored processes
            proc_path = event_data.get("ProcessName") or ""
            proc_basename = os.path.basename(proc_path.replace("\\", "/")).lower()
            if proc_basename in IGNORED_PROCESSES:
                continue

            # Match ObjectName against protected assets
            object_name = event_data.get("ObjectName") or ""
            if not object_name:
                continue

            covering = find_covering_asset(object_name, assets)
            if not covering:
                continue

            # Debounce repeated reads of the same file within 2.0 seconds
            norm_obj = ntpath.normcase(ntpath.normpath(object_name))
            now = time.monotonic()
            last_seen = self._recent_opens.get(norm_obj)
            if last_seen is not None and (now - last_seen) < 2.0:
                continue
            self._recent_opens[norm_obj] = now

            # Clean up stale recent opens cache
            if len(self._recent_opens) > 500:
                cutoff = now - 5.0
                self._recent_opens = {k: v for k, v in self._recent_opens.items() if v >= cutoff}

            classification = covering.get("classification")
            proc_display = proc_basename if proc_basename else "unknown"
            note = f"Detection: Windows audit log (Event 4663), opened by {proc_display}"

            try:
                record_fn(
                    asset_path=object_name,
                    action="Open",
                    classification=classification,
                    detection_note=note,
                )
            except Exception as e:
                print(f"[OPEN AUDIT WARNING] Could not record Open activity: {e}")

    def run(self) -> None:
        while not self._stop_event.wait(self._interval):
            try:
                if self._auto_setup:
                    self._ensure_audit_setup()
                self.check_once()
            except Exception as e:
                print(f"[OPEN AUDIT WARNING] Error in open audit check: {e}")

    def stop(self) -> None:
        self._stop_event.set()
