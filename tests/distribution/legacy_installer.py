"""Download a fixed, published migration baseline; never execute unverified bytes."""
import hashlib
import os
from pathlib import Path
import tempfile
import urllib.request

VERSION = '0.2.0-alpha.25'
SIZE = 65_433_793
SHA256 = '6895fd1000900f2bd8f391e4384e1b834e9f449f091a6a6152191d3e0a8f028c'
URL = ('https://api.github.com/repositories/1413316996/contents/windows/'
       '1C-Backup-' + VERSION + '-windows-x64-setup.exe?ref=distribution%2Fwindows-x64-' + VERSION)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, file, code, message, headers, new_url):
        # A baseline is fetched from a fixed API route; never forward its token.
        return None


def verify(path):
    if path.stat().st_size != SIZE:
        raise ValueError('Published baseline size differs from the pinned release')
    with path.open('rb') as source:
        if hashlib.file_digest(source, 'sha256').hexdigest() != SHA256:
            raise ValueError('Published baseline checksum differs from the pinned release')


def download(directory, *, opener=None, token=None):
    if opener is None:
        opener = urllib.request.build_opener(NoRedirect()).open
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    destination = directory / ('1C-Backup-' + VERSION + '-windows-x64-setup.exe')
    if destination.exists():
        verify(destination)
        return destination
    headers = {'User-Agent': 'SPBackup-UpgradeVerification/1.0', 'Accept': 'application/vnd.github.raw'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    handle, name = tempfile.mkstemp(prefix='baseline-', suffix='.partial', dir=directory)
    os.close(handle)
    temporary = Path(name)
    try:
        with opener(urllib.request.Request(URL, headers=headers), timeout=120) as response, temporary.open('wb') as output:
            if response.status != 200:
                raise ValueError('Published baseline did not return HTTP 200')
            total = 0
            while chunk := response.read(1024 * 1024):
                total += len(chunk)
                if total > SIZE:
                    raise ValueError('Published baseline exceeds pinned size')
                output.write(chunk)
        verify(temporary)
        # Same-directory hard link atomically refuses an existing destination.
        os.link(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)
    return destination
