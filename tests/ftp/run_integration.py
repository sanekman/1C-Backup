"""Run only FTP integration tests against a disposable loopback server."""
import argparse
import logging
import os
from pathlib import Path
import subprocess
import tempfile
import threading

from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer


class TestHandler(FTPHandler):
    def on_file_received(self, filename):
        # Fault injection: corrupt one uploaded fixture before verification.
        if Path(filename).name.startswith("corrupt.bak."):
            with open(filename, "r+b") as stream:
                first = stream.read(1)
                stream.seek(0)
                stream.write(bytes([first[0] ^ 0xFF]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    logging.getLogger("pyftpdlib").setLevel(logging.CRITICAL)
    with tempfile.TemporaryDirectory(prefix="OneCBackup-ftp-server-") as root:
        authorizer = DummyAuthorizer()
        authorizer.add_user("backup-test", "local-fixture-password", root, perm="elradfmwMT")
        TestHandler.authorizer = authorizer
        server = FTPServer(("127.0.0.1", 0), TestHandler)
        port = server.socket.getsockname()[1]
        thread = threading.Thread(target=server.serve_forever, kwargs={"timeout": 0.1}, daemon=True)
        thread.start()
        try:
            environment = dict(os.environ)
            environment["ONECBACKUP_TEST_FTP_PORT"] = str(port)
            environment["ONECBACKUP_TEST_FTP_ROOT"] = root
            project = Path(__file__).resolve().parents[1] / "SPBackup.Core.Tests"
            return subprocess.run([
                args.dotnet, "test", str(project), "--no-build", "--no-restore",
                "-m:1", "-nr:false", "--filter", "Category=FtpIntegration"
            ], env=environment, check=False).returncode
        finally:
            server.close_all()
            thread.join(timeout=3)


if __name__ == "__main__":
    raise SystemExit(main())
