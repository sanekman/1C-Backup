"""Authenticode signing with a certificate already installed in the Windows store.

The private key remains in its provider (including hardware tokens). No PFX password
is accepted in command arguments. All publication hashes must be computed after signing.
"""
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import urlsplit


def configured():
    thumbprint = os.environ.get('ONECBACKUP_SIGNING_THUMBPRINT', '').strip()
    required = os.environ.get('ONECBACKUP_REQUIRE_SIGNING', '').lower() in ('1', 'true')
    if required and not thumbprint:
        raise RuntimeError('Signing is required but ONECBACKUP_SIGNING_THUMBPRINT is missing')
    if thumbprint and not re.fullmatch(r'[0-9a-fA-F]{40}', thumbprint):
        raise ValueError('Signing certificate thumbprint must contain 40 hexadecimal characters')
    return thumbprint.upper() or None


def sign_and_verify(files):
    thumbprint = configured()
    if not thumbprint:
        return False
    if os.name != 'nt':
        raise RuntimeError('Configured Windows certificate signing requires a Windows machine')
    timestamp = os.environ.get('ONECBACKUP_SIGNING_TIMESTAMP', 'https://timestamp.digicert.com')
    url = urlsplit(timestamp)
    if url.scheme != 'https' or not url.hostname or url.username or url.password or url.fragment or any(c.isspace() for c in timestamp):
        raise ValueError('Signing timestamp server must be an HTTPS URL without credentials')
    signtool = os.environ.get('ONECBACKUP_SIGNTOOL', 'signtool.exe')
    store = os.environ.get('ONECBACKUP_SIGNING_STORE', 'CurrentUser')
    if store not in ('CurrentUser', 'LocalMachine'):
        raise ValueError('Signing store must be CurrentUser or LocalMachine')
    paths = [Path(file).resolve() for file in files]
    if not paths or any(not file.is_file() for file in paths):
        raise FileNotFoundError('A signing input is missing')
    for file in paths:
        command = [signtool, 'sign', '/s', 'My', '/sha1', thumbprint, '/fd', 'SHA256', '/tr', timestamp, '/td', 'SHA256']
        if store == 'LocalMachine':
            command.append('/sm')
        subprocess.run(command + [str(file)], check=True)
        subprocess.run([signtool, 'verify', '/pa', '/all', '/tw', str(file)], check=True)
        # SignTool verification validates trust; also require the exact selected signer
        # and a timestamp. This prevents successful validation of another preexisting signature.
        script = """$ErrorActionPreference = 'Stop'
$s = Get-AuthenticodeSignature -LiteralPath $env:ONECBACKUP_VERIFY_SIGNED_FILE
if ($s.Status -ne 'Valid' -or $null -eq $s.SignerCertificate -or
    $s.SignerCertificate.Thumbprint -ne $env:ONECBACKUP_VERIFY_SIGNER -or
    $null -eq $s.TimeStamperCertificate) { throw 'Authenticode signer, trust or timestamp verification failed' }
"""
        environment = dict(os.environ, ONECBACKUP_VERIFY_SIGNED_FILE=str(file), ONECBACKUP_VERIFY_SIGNER=thumbprint)
        subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', script], env=environment, check=True)
    return True
