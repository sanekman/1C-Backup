"""Read the WPF-produced AES ZIP with an independent implementation."""
import sys
from pathlib import Path
import pyzipper

path = Path(sys.argv[1])
password = "CI-only-архив-🗝-2026".encode("utf-8")
with pyzipper.AESZipFile(path) as archive:
    assert archive.namelist() == ["1Cv8.1CD"]
    assert archive.getinfo("1Cv8.1CD").wz_aes_strength == 3, "AES-256 required"
    try:
        archive.read("1Cv8.1CD", pwd=b"incorrect password")
    except RuntimeError:
        pass
    else:
        raise AssertionError("Wrong password was accepted")
    assert archive.read("1Cv8.1CD", pwd=password) == b"CI fixture bytes; not a real 1C database"
print("Independent AES-256 ZIP check passed, including UTF-8 password and exact file bytes.")
