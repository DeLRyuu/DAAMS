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
                asset_registry.sync_service, "trigger_sync"
            ):
                ok, _ = asset_registry.protect_asset(path, "Confidential")

            self.assertTrue(ok)
            saved_record = upsert.call_args.args[0]
            self.assertEqual(saved_record["path"], existing["path"])
            self.assertEqual(saved_record["status"], "protected")
            self.assertEqual(saved_record["classification"], "Confidential")
            load_registry.assert_not_called()

    def test_mutations_do_not_read_or_write_json_when_database_works(self):
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
            asset_registry.sync_service, "trigger_sync"
        ):
            self.assertTrue(asset_registry.change_classification(
                existing["path"], "Confidential"
            )[0])
            self.assertTrue(asset_registry.remove_protection(existing["path"])[0])

        self.assertEqual(upsert.call_count, 2)
        load_registry.assert_not_called()

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
