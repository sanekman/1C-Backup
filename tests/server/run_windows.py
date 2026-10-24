"""Run HTTPS server integration against a PostgreSQL cluster owned only by this test process."""
import argparse
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import tempfile
import uuid

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--package", action="store_true")
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[2]
    pg = Path(os.environ.get("PGBIN", ""))
    if not (pg / "initdb.exe").exists():
        raise RuntimeError("PGBIN with PostgreSQL tools is required.")
    # Python's Windows TemporaryDirectory uses a restricted DACL. PostgreSQL launches
    # initdb with a restricted token; use the same inherited TEMP permissions as the native SQL fixture.
    root = Path(tempfile.gettempdir()) / ("onec-server-postgres-" + uuid.uuid4().hex)
    root.mkdir()
    try:
        password = secrets.token_urlsafe(32)
        password_file = root / "password.txt"
        password_file.write_text(password)
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        data = root / "data"
        def run_tool(arguments, capture=True):
            result = subprocess.run(arguments, capture_output=capture, text=True, encoding="utf-8", errors="replace", timeout=90)
            if result.returncode:
                details = ((result.stdout or "") + (result.stderr or "")).replace(password, "[redacted]")
                raise RuntimeError("PostgreSQL fixture tool failed: " + Path(arguments[0]).name + "\n" + details[-5000:])
            return result
        run_tool([str(pg / "initdb.exe"), "-D", str(data), "--username=onecbackup", "--auth=scram-sha-256",
                  "--encoding=UTF8", "--locale=C", "--pwfile=" + str(password_file)])
        started = False
        try:
            # A background postgres can inherit pipe handles on Windows even with -l.
            # pg_ctl performs its own readiness wait; use no redirected pipe for startup.
            run_tool([str(pg / "pg_ctl.exe"), "-D", str(data), "-l", str(root / "postgres.log"), "-o", f"-h 127.0.0.1 -p {port}", "-w", "start"], capture=False)
            started = True
            connection = root / "connection.txt"
            connection.write_text(f"Host=127.0.0.1;Port={port};Database=postgres;Username=onecbackup;Password={password}")
            environment = dict(os.environ, ONECBACKUP_SERVER_TEST_CONNECTION_FILE=str(connection), ONECBACKUP_TEST_REPOSITORY=str(repository))
            name = "server-package-windows.trx" if args.package else "server-windows.trx"
            category = "Category=ServerPackage" if args.package else "Category!=ServerPackage"
            result = subprocess.run([args.dotnet, "test", "tests/OneCBackup.Server.Tests", "--no-build", "--no-restore", "-m:1", "-nr:false", "--filter", category,
                                     "--logger", "trx;LogFileName=" + name, "--results-directory", "artifacts/server-tests"], cwd=repository, env=environment)
            if result.returncode:
                raise RuntimeError("Server integration tests failed.")
        finally:
            if started:
                run_tool([str(pg / "pg_ctl.exe"), "-D", str(data), "-m", "fast", "-w", "stop"])
    finally:
        shutil.rmtree(root)

if __name__ == "__main__":
    main()
