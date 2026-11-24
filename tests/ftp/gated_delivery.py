"""Hold the first upload to an isolated loopback destination until the test releases it."""
import json
import logging
from pathlib import Path
import sys
import time

from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer


def main():
    lab = Path(sys.argv[1])
    config = json.load(sys.stdin)
    root = lab / "ftp"
    root.mkdir()
    logging.getLogger("pyftpdlib").setLevel(logging.CRITICAL)

    class Handler(FTPHandler):
        def ftp_STOR(self, file, mode="w"):
            signal = lab / "upload-started"
            if not signal.exists():
                signal.touch()
                deadline = time.monotonic() + 45
                while not (lab / "release-upload").exists():
                    if time.monotonic() >= deadline:
                        self.respond("451 Fixture upload gate timed out.")
                        return None
                    time.sleep(0.1)
            return super().ftp_STOR(file, mode)

    authorizer = DummyAuthorizer()
    authorizer.add_user("fixture", config["ftpPassword"], str(root), perm="elradfmwMT")
    Handler.authorizer = authorizer
    server = FTPServer(("127.0.0.1", 0), Handler)
    temporary = lab / "port.tmp"
    temporary.write_text(str(server.socket.getsockname()[1]), encoding="ascii")
    temporary.replace(lab / "port")
    server.serve_forever()


if __name__ == "__main__":
    main()
