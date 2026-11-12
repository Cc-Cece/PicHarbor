"""Confirm narrow public-metadata exceptions do not disable credential detection."""
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[1]
binary = Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="picharbor-secret-scan-") as temporary:
    source = Path(temporary)
    # Generated test data only: never a credential for any real service.
    (source / "fixture.txt").write_text('API_TOKEN = "' + secrets.token_hex(32) + '"\n')
    result = subprocess.run([str(binary), "detect", "--source", str(source), "--no-git", "--redact",
                             "--config", str(root / ".gitleaks.toml"), "--exit-code", "1"], capture_output=True)
    if result.returncode != 1 or b"leaks found" not in result.stderr:
        raise RuntimeError("Secret scanner did not reject the generated credential-shaped fixture")
print("PASS: public metadata exceptions preserve credential detection")
