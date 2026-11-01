import os
import sys
import tempfile
import unittest
from unittest.mock import patch

ENGINE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from core import asset_registry


class TestAssetRegistry(unittest.TestCase):
    def test_get_record_database_first_and_fallback(self):
        existing = {"path": "C:\\data\\doc.txt", "status": "protected"}
        # 1. Database returns record -> JSON not read
        with patch.object(asset_registry.database, "get_protected_asset", return_value=existing), \
                patch.object(asset_registry, "_load_registry") as load_registry:
            rec = asset_registry._get_record("C:\\data\\doc.txt")
            self.assertEqual(rec, existing)
            load_registry.assert_not_called()

        # 2. Database returns None (not found) -> returns None, JSON not read
        with patch.object(asset_registry.database, "get_protected_asset", return_value=None), \
                patch.object(asset_registry, "_load_registry") as load_registry:
            rec = asset_registry._get_record("C:\\data\\nonexistent.txt")
            self.assertIsNone(rec)
            load_registry.assert_not_called()

        # 3. Database raises exception -> falls back to JSON
        with patch.object(asset_registry.database, "get_protected_asset", side_effect=OSError("db down")), \
                patch.object(asset_registry, "_load_registry", return_value={"c:\\data\\doc.txt": existing}):
            rec = asset_registry._get_record("C:\\data\\doc.txt")
            self.assertEqual(rec, existing)

    def test_empty_database_result_does_not_fall_back_to_json(self):
        with patch.object(asset_registry.database, "get_all_protected_assets", return_value=[]), \
                patch.object(asset_registry, "_load_registry") as load_registry:
            self.assertEqual(asset_registry.get_all_assets(), [])

        load_registry.assert_not_called()

    def test_database_error_falls_back_to_json(self):
        records = {
            "protected": {"path": "C:\\data\\one.txt", "status": "protected"},
            "removed": {"path": "C:\\data\\two.txt", "status": "unprotected"},
        }
        with patch.object(
            asset_registry.database,
            "get_all_protected_assets",
            side_effect=OSError("database unavailable"),
        ), patch.object(asset_registry, "_load_registry", return_value=records):
            self.assertEqual(asset_registry.get_all_assets(), [records["protected"]])

    def test_reprotecting_unprotected_database_asset_updates_same_record(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "DAAMS 2.txt")
            with open(path, "w", encoding="utf-8"):
                pass

            existing = {
                "path": path,
                "asset_name": "DAAMS 2.txt",
                "asset_type": "file",
                "classification": "Private",
                "protected_at": "2026-01-01 00:00:00",
                "protected_by": "admin",
                "status": "unprotected",
            }
            with patch.object(
                asset_registry.database, "get_protected_asset", return_value=existing
            ), patch.object(
                asset_registry.database, "upsert_protected_asset", return_value=True
            ) as upsert, patch.object(
                asset_registry, "_load_registry"
            ) as load_registry, patch.object(
                asset_registry, "_backup_to_json"
            ), patch.object(
                asset_registry.sync_service, "trigger_sync"
            ):
                ok, _ = asset_registry.protect_asset(path, "Confidential")

            self.assertTrue(ok)
            saved_record = upsert.call_args.args[0]
            self.assertEqual(saved_record["path"], existing["path"])
            self.assertEqual(saved_record["status"], "protected")
            self.assertEqual(saved_record["classification"], "Confidential")
            load_registry.assert_not_called()

    def test_mutations_do_not_read_json_when_database_works(self):
        existing = {
            "path": "C:\\data\\file.txt",
            "asset_name": "file.txt",
            "asset_type": "file",
            "classification": "Private",
            "protected_at": "2026-01-01 00:00:00",
            "protected_by": "admin",
            "status": "protected",
        }
        with patch.object(
            asset_registry.database, "get_protected_asset", return_value=existing.copy()
        ), patch.object(
            asset_registry.database, "upsert_protected_asset", return_value=True
        ) as upsert, patch.object(
            asset_registry, "_load_registry"
        ) as load_registry, patch.object(
            asset_registry, "_backup_to_json"
        ) as backup, patch.object(
            asset_registry.sync_service, "trigger_sync"
        ):
            self.assertTrue(asset_registry.change_classification(
                existing["path"], "Confidential"
            )[0])
            self.assertTrue(asset_registry.remove_protection(existing["path"])[0])

        self.assertEqual(upsert.call_count, 2)
        self.assertEqual(backup.call_count, 2)
        load_registry.assert_not_called()

    def test_unprotect_then_protect_again_works(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "Document.docx")
            with open(path, "w", encoding="utf-8"):
                pass

            db_records = {
                path: {
                    "path": path,
                    "asset_name": "Document.docx",
                    "asset_type": "file",
                    "classification": "Sensitive",
                    "protected_at": "2026-01-01 10:00:00",
                    "protected_by": "alice",
                    "status": "protected",
                }
            }

            def fake_get(p):
                return db_records.get(p)

            def fake_upsert(rec):
                db_records[rec["path"]] = rec.copy()
                return True

            with patch.object(asset_registry.database, "get_protected_asset", side_effect=fake_get), \
                 patch.object(asset_registry.database, "upsert_protected_asset", side_effect=fake_upsert), \
                 patch.object(asset_registry, "_load_registry") as mock_load, \
                 patch.object(asset_registry, "_backup_to_json"), \
                 patch.object(asset_registry.sync_service, "trigger_sync"):

                # 1. Unprotect
                ok, msg = asset_registry.remove_protection(path)
                self.assertTrue(ok)
                self.assertEqual(db_records[path]["status"], "unprotected")

                # 2. Protect again with different classification and user
                ok, msg = asset_registry.protect_asset(path, "Confidential", protected_by="bob")
                self.assertTrue(ok)
                self.assertEqual(db_records[path]["status"], "protected")
                self.assertEqual(db_records[path]["classification"], "Confidential")
                self.assertEqual(db_records[path]["protected_by"], "bob")

                # 3. Protecting while already protected fails with duplicate message
                ok, err_msg = asset_registry.protect_asset(path, "Public")
                self.assertFalse(ok)
                self.assertIn("already protected", err_msg)
                self.assertIn("Confidential", err_msg)

                # JSON was never read
                mock_load.assert_not_called()

    def test_removing_the_only_asset_leaves_get_all_assets_empty(self):
        path = "C:\\data\\only_asset.txt"
        db_records = {
            path: {
                "path": path,
                "asset_name": "only_asset.txt",
                "asset_type": "file",
                "classification": "Confidential",
                "protected_at": "2026-01-01 10:00:00",
                "protected_by": "admin",
                "status": "protected",
            }
        }

        # Stale JSON file has it still marked protected
        stale_json = {
            asset_registry._normalize(path): {
                "path": path,
                "asset_name": "only_asset.txt",
                "asset_type": "file",
                "classification": "Confidential",
                "status": "protected",
            }
        }

        def fake_get_all(only_protected=True):
            if only_protected:
                return [r for r in db_records.values() if r.get("status") == "protected"]
            return list(db_records.values())

        def fake_get(p):
            return db_records.get(p)

        def fake_upsert(rec):
            db_records[rec["path"]] = rec.copy()
            return True

        with patch.object(asset_registry.database, "get_all_protected_assets", side_effect=fake_get_all), \
             patch.object(asset_registry.database, "get_protected_asset", side_effect=fake_get), \
             patch.object(asset_registry.database, "upsert_protected_asset", side_effect=fake_upsert), \
             patch.object(asset_registry, "_load_registry", return_value=stale_json) as mock_load, \
             patch.object(asset_registry, "_backup_to_json"), \
             patch.object(asset_registry.sync_service, "trigger_sync"):

            # Initially 1 asset
            self.assertEqual(len(asset_registry.get_all_assets(only_protected=True)), 1)

            # Remove protection from the only asset
            ok, _ = asset_registry.remove_protection(path)
            self.assertTrue(ok)
            self.assertEqual(db_records[path]["status"], "unprotected")

            # get_all_assets is empty, does NOT fall back to stale_json
            remaining = asset_registry.get_all_assets(only_protected=True)
            self.assertEqual(remaining, [])
            mock_load.assert_not_called()

    def test_path_coverage_and_children_use_boundaries(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = os.path.join(directory, "parent")
            nested = os.path.join(parent, "nested")
            outside = os.path.join(directory, "parent-other")
            assets = [
                {"path": parent, "asset_type": "folder", "status": "protected"},
                {"path": nested, "asset_type": "folder", "status": "protected"},
                {"path": os.path.join(nested, "file.txt"), "asset_type": "file",
                 "status": "unprotected"},
                {"path": outside, "asset_type": "folder", "status": "protected"},
            ]
            with patch.object(asset_registry, "get_all_assets", return_value=assets):
                covering = asset_registry.get_covering_asset(
                    os.path.join(nested, "other.txt")
                )
                children = asset_registry.list_children(parent)

            self.assertEqual(covering["path"], nested)
            self.assertEqual(
                {asset["path"] for asset in children},
                {nested, os.path.join(nested, "file.txt")},
            )
            self.assertFalse(asset_registry._is_within(outside, parent))

    def test_python_and_control_center_folder_path_rules_agree(self):
        control_center_path = os.path.join(
            os.path.dirname(ENGINE_DIR),
            "DAAMS.ControlCenter",
            "DAAMS.ControlCenter",
            "ViewModels",
            "ProtectedAssetsViewModel.cs",
        )
        with open(control_center_path, encoding="utf-8") as file:
            control_center_source = file.read()
        self.assertIn("path.Replace('/', '\\\\').TrimEnd('\\\\')", control_center_source)
        self.assertIn("normalizedPath.StartsWith(folderPrefix", control_center_source)
        self.assertIn("StringComparison.OrdinalIgnoreCase", control_center_source)

        samples = [
            ("C:\\Protected\\Folder", "C:\\Protected\\Folder", True),
            ("c:/protected/folder/child.txt", "C:\\Protected\\Folder\\", True),
            ("C:\\Protected\\Folder\\Nested\\item.txt", "c:\\protected\\folder", True),
            ("C:\\Protected\\Folder-other\\item.txt", "C:\\Protected\\Folder", False),
            ("C:\\Protected\\Sibling\\item.txt", "C:\\Protected\\Folder", False),
        ]

        def control_center_rule(child, parent):
            normalized_child = child.replace("/", "\\").rstrip("\\")
            normalized_parent = parent.replace("/", "\\").rstrip("\\")
            return (
                normalized_child.casefold() == normalized_parent.casefold()
                or normalized_child.casefold().startswith(
                    (normalized_parent + "\\").casefold()
                )
            )

        for child, parent, expected in samples:
            with self.subTest(child=child, parent=parent):
                self.assertEqual(asset_registry._is_within(child, parent), expected)
                self.assertEqual(control_center_rule(child, parent), expected)


if __name__ == "__main__":
    unittest.main()
