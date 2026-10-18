"""Offline regression tests for per-file checks on the actual bundled uploader."""
import hashlib
import os
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile
from unittest.mock import MagicMock

RUNTIME = Path(__file__).resolve().parents[2] / "src/PicHarbor.Core/GooglePhotos/Embedded"
sys.path.insert(0, str(RUNTIME))

from gpmc.client import Client
from rich.progress import Progress


class PreparedUploadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "photo.JPG"
        self.path.write_bytes(b"original")
        self.stat = self.path.stat()
        self.client = Client.__new__(Client)
        self.client.logger = MagicMock()
        self.client._log_failed_or_skipped = MagicMock()
        self.client.api = MagicMock()
        self.client.api.find_remote_media_by_hash.return_value = "existing-media-key"
        self.client.prepared_files = {
            os.path.normcase(os.path.abspath(self.path)): {
                "size": self.stat.st_size,
                "modified_ms": self.stat.st_mtime_ns // 1_000_000,
                "sha256": hashlib.sha256(b"original").hexdigest(),
            }
        }

    def upload(self, callback=None):
        with Progress(disable=True) as progress:
            return self.client._upload_file(
                self.path, None, progress, False, False, False,
                progress_callback=callback, auto_retries=0,
            )

    def test_unchanged_file_passes_and_uses_existing_remote_duplicate(self):
        self.assertEqual(self.upload(), {self.path.absolute().as_posix(): "existing-media-key"})
        self.client.api.find_remote_media_by_hash.assert_called_once()

    def test_size_change_is_rejected_before_network(self):
        self.path.write_bytes(b"different-size")
        with self.assertRaisesRegex(ValueError, "changed after preparation"):
            self.upload()
        self.assertEqual(self.client.api.mock_calls, [])

    def test_same_size_change_with_restored_timestamp_is_rejected(self):
        self.path.write_bytes(b"replaced")
        os.utime(self.path, ns=(self.stat.st_atime_ns, self.stat.st_mtime_ns))
        with self.assertRaisesRegex(ValueError, "content no longer matches"):
            self.upload()
        self.assertEqual(self.client.api.mock_calls, [])

    def test_missing_file_is_rejected(self):
        self.path.unlink()
        with self.assertRaises(FileNotFoundError):
            self.upload()
        self.assertEqual(self.client.api.mock_calls, [])

    def test_unselected_file_is_rejected(self):
        self.client.prepared_files = {}
        with self.assertRaisesRegex(ValueError, "not in the prepared upload plan"):
            self.upload()
        self.assertEqual(self.client.api.mock_calls, [])

    def test_change_after_hashing_is_rejected_before_upload(self):
        def change_after_lookup(_hash):
            self.path.write_bytes(b"changed after lookup")
            return None

        self.client.api.find_remote_media_by_hash.side_effect = change_after_lookup
        with self.assertRaisesRegex(ValueError, "changed after preparation"):
            self.upload()
        self.client.api.upload_file.assert_not_called()
        self.client.api.commit_upload.assert_not_called()

    def test_shipped_bundle_matches_tested_sources(self):
        with zipfile.ZipFile(RUNTIME.parent / "gpmc_bundle.zip") as bundle:
            for name in ("gpmc_bridge.py", "gpmc/client.py", "gpmc/hash_handler.py"):
                self.assertEqual(bundle.read(name), (RUNTIME / name).read_bytes())


if __name__ == "__main__":
    unittest.main()
