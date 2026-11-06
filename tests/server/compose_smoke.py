"""Exercise the shipped Compose stack with trusted TLS, persisted state and no secret output."""
import contextlib
import html
import http.cookiejar
import json
import os
from pathlib import Path
import re
import secrets
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

def main():
    repository = Path(__file__).resolve().parents[2]
    project = "onec-server-smoke-" + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix="onec-compose-") as temporary:
        root = Path(temporary)
        os.chmod(root, 0o700)
        passwords = {name: secrets.token_urlsafe(32) for name in ["database", "administrator", "certificate"]}
        for name, password in passwords.items():
            (root / (name + "-password.txt")).write_text(password)
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-days", "2", "-nodes", "-subj", "/CN=localhost",
                        "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1", "-addext", "basicConstraints=critical,CA:TRUE",
                        "-keyout", str(root / "server.key"), "-out", str(root / "server.crt")], check=True, capture_output=True)
        subprocess.run(["openssl", "pkcs12", "-export", "-inkey", str(root / "server.key"), "-in", str(root / "server.crt"),
                        "-out", str(root / "server.pfx"), "-passout", "file:" + str(root / "certificate-password.txt")], check=True, capture_output=True)
        for name in ["database-password.txt", "administrator-password.txt", "certificate-password.txt", "server.pfx"]:
            os.chmod(root / name, 0o444)
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        environment = dict(os.environ, SERVER_SECRETS_DIRECTORY=str(root), SERVER_PORT=str(port), SERVER_ADMIN_NAME="admin")
        command = ["docker", "compose", "-f", str(repository / "deploy/server/compose.yaml"), "-p", project]
        authority = os.environ.get("SSL_CERT_FILE")
        if authority and Path(authority).is_file():
            if b"PRIVATE KEY" in Path(authority).read_bytes():
                raise RuntimeError("Build trust file must contain only public CA certificates.")
            override = root / "build-trust.json"
            override.write_text(json.dumps({"services": {"server": {"build": {"secrets": [{"source": "build-network-ca", "target": "build-ca"}]}}},
                                           "secrets": {"build-network-ca": {"file": authority}}}))
            command.extend(["-f", str(override)])
        def compose(*args, check=True):
            result = subprocess.run(command + list(args), cwd=repository, env=environment, text=True, capture_output=True)
            if check and result.returncode:
                # Credentials are never interpolated into args or compose output; leave logs outside the artifact set.
                safe = result.stderr + result.stdout
                for password in passwords.values():
                    safe = safe.replace(password, "[redacted]")
                if args[:1] == ("run",):
                    diagnostic = subprocess.run(command + ["run", "--rm", "--entrypoint", "sh", "server", "-c",
                        "id; ls -ld /var/lib/onec-backup-server /var/lib/onec-backup-server/keys /run/secrets; ls -l /run/secrets"], cwd=repository, env=environment, text=True, capture_output=True)
                    safe += "\nPermissions:\n" + diagnostic.stdout
                raise RuntimeError("Compose step failed: " + " ".join(args) + "\n" + safe[-4000:])
            return result
        try:
            compose("build", "server")
            compose("run", "--rm", "server", "--initialize")
            compose("up", "-d")
            assert int(compose("exec", "-T", "server", "id", "-u").stdout.strip()) > 0
            tls = ssl.create_default_context(cafile=str(root / "server.crt"))
            jar = http.cookiejar.CookieJar()
            opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar), urllib.request.HTTPSHandler(context=tls))
            base = f"https://127.0.0.1:{port}/"
            def request(path, body=None, headers=None, form=False):
                data = urllib.parse.urlencode(body).encode() if form else json.dumps(body).encode() if body is not None else None
                fields = dict(headers or {})
                if data is not None:
                    fields["Content-Type"] = "application/x-www-form-urlencoded" if form else "application/json"
                response = opener.open(urllib.request.Request(base + path, data=data, headers=fields), timeout=3)
                with response:
                    return response.status, response.read().decode()
            def ready():
                deadline = time.monotonic() + 45
                while time.monotonic() < deadline:
                    try:
                        if request("health/ready")[0] == 200:
                            return
                    except (urllib.error.URLError, TimeoutError, ConnectionError):
                        pass
                    time.sleep(0.3)
                raise RuntimeError("Compose server did not become ready.")
            ready()
            login = request("login")[1]
            anti = html.unescape(re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', login).group(1))
            status, panel = request("session/login", {"name": "admin", "password": passwords["administrator"], "__RequestVerificationToken": anti}, form=True)
            assert status == 200 and "Агенты" in panel
            session = json.loads(request("api/v1/management/session")[1])
            csrf = {"X-CSRF-TOKEN": session["csrfToken"]}
            invitation = json.loads(request("api/v1/management/invitations", {"name": "Compose smoke", "minutes": 5}, csrf)[1])
            agent = str(uuid.uuid4())
            enrollment = json.loads(request("api/v1/enrollment", {"invitationCode": invitation["code"], "agentId": agent, "name": "Compose smoke"})[1])
            try:
                request("api/v1/enrollment", {"invitationCode": invitation["code"], "agentId": agent, "name": "Compose smoke"})
                raise AssertionError("Invitation replay accepted")
            except urllib.error.HTTPError as error:
                assert error.code == 403
            bearer = {"Authorization": "Bearer " + enrollment["token"]}
            configuration = json.loads(request(f"api/v1/agents/{agent}/configuration", headers=bearer)[1])
            assert configuration["revision"] == 1 and configuration["jobs"] == []
            assert request(f"api/v1/agents/{agent}/heartbeat", {"agentId": agent, "agentVersion": "compose-test", "backupExecutionAvailable": False,
                           "configurationRevision": 1, "sentUtc": "2000-01-01T00:00:00Z"}, bearer)[0] == 204
            assert "Compose smoke" in request("")[1]
            compose("stop", "server")
            compose("up", "-d", "server")
            ready()
            assert request("api/v1/management/session")[0] == 200
            assert json.loads(request(f"api/v1/agents/{agent}/configuration", headers=bearer)[1])["agentId"] == agent
            assert request(f"api/v1/management/agents/{agent}/revoke", {}, csrf)[0] == 204
            try:
                request(f"api/v1/agents/{agent}/configuration", headers=bearer)
                raise AssertionError("Revoked agent still accepted")
            except urllib.error.HTTPError as error:
                assert error.code == 401
            target = repository / "artifacts/server-tests/compose-result.json"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(json.dumps({"success": True, "checks": ["trusted HTTPS", "non-root server", "first administrator", "CSRF login / invitation", "single-use registration",
                "configuration", "heartbeat", "persistent registration and session after restart", "revocation"]}, indent=2))
            print("PASS: actual Compose stack, PostgreSQL, trusted HTTPS, enrollment, heartbeat, restart and revocation.")
        finally:
            # Remove only this uniquely named disposable stack and its own volumes.
            compose("down", "--volumes", "--remove-orphans", check=False)

if __name__ == "__main__":
    main()
