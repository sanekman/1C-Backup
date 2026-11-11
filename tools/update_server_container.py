"""Update the bundled Compose server, retaining an image + database + data rollback bundle."""
import argparse
import contextlib
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.request
import uuid

try:
    import fcntl
except ImportError:
    fcntl = None

ROOT = Path(__file__).resolve().parents[1]
TAG = re.compile(r"server-v\d+\.\d+\.\d+(?:-preview\.\d+)?\Z")
DATA = "/var/lib/onec-backup-server"


class UpdateError(RuntimeError):
    pass


def command(args, *, output=None, input_file=None, cwd=None):
    """Never echo command output: effective Compose config/build logs may contain secrets."""
    result = subprocess.run(args, cwd=cwd, stdin=input_file, stdout=output or subprocess.PIPE,
                            stderr=subprocess.PIPE, check=False)
    if result.returncode:
        raise UpdateError("Не выполнена операция: " + args[0] + " (код " + str(result.returncode) + ").")
    return result.stdout.decode() if output is None else ""


def latest_version():
    request = urllib.request.Request("https://api.github.com/repos/sanekman/1C-Backup/releases?per_page=100",
                                     headers={"User-Agent": "SPBackup-ContainerUpdater", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        raw = response.read(2 * 1024 * 1024 + 1)
    if len(raw) > 2 * 1024 * 1024:
        raise UpdateError("Слишком большой ответ списка выпусков.")
    versions = [release["tag_name"] for release in json.loads(raw)
                if not release.get("draft") and TAG.fullmatch(release.get("tag_name", ""))]
    if not versions:
        raise UpdateError("Опубликованный серверный выпуск не найден.")
    def key(version):
        numbers = list(map(int, re.findall(r"\d+", version)))
        return (*numbers[:3], 0 if "-preview." in version else 1, numbers[3] if len(numbers) > 3 else 0)
    return max(versions, key=key)


def source_revision(repository, version, offline=False):
    if not TAG.fullmatch(version):
        raise UpdateError("Нужен серверный тег server-v<версия>, а не тег Windows-агента или ветка.")
    ref = "refs/tags/" + version
    if not offline:
        command(["git", "fetch", "--no-tags", "origin", ref + ":" + ref], cwd=repository)
    return command(["git", "rev-parse", "--verify", ref + "^{commit}"], cwd=repository).strip()


def build_image(repository, revision, version, image):
    with tempfile.TemporaryDirectory(prefix="spbackup-update-source-") as temporary:
        worktree = Path(temporary) / "source"
        command(["git", "worktree", "add", "--quiet", "--detach", str(worktree), revision], cwd=repository)
        try:
            args = ["docker", "build", "--file", str(worktree / "deploy/server/Dockerfile"), "--tag", image,
                    "--label", "org.opencontainers.image.revision=" + revision,
                    "--label", "org.opencontainers.image.version=" + version,
                    "--build-arg", "SERVER_VERSION=" + version.removeprefix("server-v"),
                    "--build-arg", "SERVER_REVISION=" + revision]
            ca = os.environ.get("SSL_CERT_FILE")
            if ca and Path(ca).is_file():
                if b"PRIVATE KEY" in Path(ca).read_bytes():
                    raise UpdateError("Файл доверия сборки должен содержать только публичные сертификаты.")
                args += ["--secret", "id=build-ca,src=" + ca]
            command(args + [str(worktree)], cwd=repository)
        finally:
            command(["git", "worktree", "remove", "--force", str(worktree)], cwd=repository)


class ComposeUpdate:
    def __init__(self, compose, *, project=None, env_file=None, backup_root=None, health_host=None):
        self.compose_file = Path(compose).resolve()
        base = ["docker", "compose", "-f", str(self.compose_file)]
        if env_file:
            base += ["--env-file", str(Path(env_file).resolve())]
        if project:
            base += ["-p", project]
        self.config = json.loads(command(base + ["config", "--format", "json"]))
        self.project = self.config["name"]
        if not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", self.project):
            raise UpdateError("Некорректное имя Compose-проекта.")
        self.base = base
        self.server = self.one_container("server")
        self.db = self.one_container("db")
        self.old_image = self.inspect(self.server)["Image"]
        server = self.inspect(self.server)
        database = self.inspect(self.db)
        if not server["State"]["Running"] or not database["State"]["Running"]:
            raise UpdateError("Перед обновлением сервер и PostgreSQL должны работать.")
        if not database["Config"]["Image"].startswith("postgres:17"):
            raise UpdateError("Команда поддерживает штатную контейнерную PostgreSQL 17.")
        mounts = [m for m in server["Mounts"] if m["Destination"] == DATA and m["Type"] == "volume"]
        if len(mounts) != 1:
            raise UpdateError("Нужен штатный именованный том server-data.")
        self.volume = mounts[0]["Name"]
        environment = self.config["services"]["server"].get("environment", {})
        live_environment = dict(item.split("=", 1) for item in server["Config"]["Env"])
        if any(live_environment.get(key) != value for key, value in environment.items()):
            raise UpdateError("Настройки Compose отличаются от работающего контейнера. Передайте прежние --env-file и --project-name.")
        volumes = self.config["services"]["server"].get("volumes", [])
        if (len(volumes) != 1 or volumes[0].get("type") != "volume" or volumes[0].get("target") != DATA
                or self.config["volumes"][volumes[0]["source"]].get("name") != self.volume):
            raise UpdateError("Том данных в Compose отличается от работающего сервера.")
        for name in ["database-password", "administrator-password", "server-certificate", "certificate-password"]:
            actual = [mount for mount in server["Mounts"] if mount["Destination"] == "/run/secrets/" + name]
            expected = self.config.get("secrets", {}).get(name, {}).get("file")
            if len(actual) != 1 or expected is None or Path(actual[0]["Source"]).resolve() != Path(expected).resolve():
                raise UpdateError("Файлы секретов в Compose отличаются от работающего сервера.")
        connection = dict(part.split("=", 1) for part in environment.get("Server__ConnectionString", "").split(";") if "=" in part)
        db_env = dict(item.split("=", 1) for item in database["Config"]["Env"])
        self.user = db_env.get("POSTGRES_USER", "postgres")
        self.database = db_env.get("POSTGRES_DB", self.user)
        if (connection.get("Host") != "db" or connection.get("Database") != self.database
                or self.database in {"postgres", "template0", "template1"}
                or not all(re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,62}", value) for value in [self.user, self.database])):
            raise UpdateError("Команда поддерживает отдельную БД штатного Compose, без внешней PostgreSQL.")
        bindings = server["NetworkSettings"]["Ports"].get("7443/tcp") or []
        if len(bindings) != 1:
            raise UpdateError("Нужен один опубликованный порт HTTPS контейнера 7443.")
        self.host = bindings[0]["HostIp"]
        if self.host in {"0.0.0.0", "::"}:
            self.host = "127.0.0.1"
        self.port = int(bindings[0]["HostPort"])
        ports = self.config["services"]["server"].get("ports", [])
        if (len(ports) != 1 or ports[0].get("target") != 7443 or str(ports[0].get("published")) != str(self.port)
                or ports[0].get("host_ip", "0.0.0.0") != bindings[0]["HostIp"]):
            raise UpdateError("Порт HTTPS в Compose отличается от работающего контейнера.")
        allowed = environment.get("AllowedHosts", "localhost").split(";")
        self.health_host = health_host or next((h for h in allowed if h != "*"), "localhost")
        self.health_path = environment.get("Server__PathBase", "").rstrip("/") + "/health/ready"
        if any(char in self.health_host + self.health_path for char in "\r\n"):
            raise UpdateError("Некорректный адрес проверки HTTPS.")
        self.backup_root = Path(backup_root or self.compose_file.parent / "backups").resolve()
        self.backup = None
        self.runtime = None

    @staticmethod
    def inspect(container):
        return json.loads(command(["docker", "inspect", container]))[0]

    def one_container(self, service):
        ids = command(self.base + ["ps", "--all", "--quiet", service]).split()
        if len(ids) != 1:
            raise UpdateError("Нужен ровно один контейнер службы " + service + ".")
        return ids[0]

    @contextlib.contextmanager
    def lock(self):
        if fcntl is None:
            raise UpdateError("Команда предназначена для Docker Compose на Linux.")
        with (self.compose_file.parent / (".update-" + self.project + ".lock")).open("a") as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise UpdateError("Обновление этого Compose-проекта уже выполняется.") from None
            yield

    def certificate(self, directory):
        secrets = self.config["secrets"]
        certificate = secrets["server-certificate"]["file"]
        password = secrets["certificate-password"]["file"]
        public = directory / "server-public.pem"
        command(["openssl", "pkcs12", "-in", certificate, "-passin", "file:" + password,
                 "-clcerts", "-nokeys", "-out", str(public)])
        context = ssl.create_default_context(cafile=str(public))
        context.verify_flags |= ssl.VERIFY_X509_PARTIAL_CHAIN
        return context

    def ready(self, context, timeout=60):
        deadline = time.monotonic() + timeout
        while True:
            try:
                with socket.create_connection((self.host, self.port), timeout=3) as connection:
                    with context.wrap_socket(connection, server_hostname=self.health_host) as tls:
                        request = f"GET {self.health_path} HTTP/1.1\r\nHost: {self.health_host}\r\nConnection: close\r\n\r\n"
                        tls.sendall(request.encode("ascii"))
                        response = b""
                        while b"\r\n" not in response and len(response) < 8192:
                            chunk = tls.recv(1024)
                            if not chunk:
                                break
                            response += chunk
                        if response.split(b"\r\n", 1)[0].split()[1:2] == [b"200"]:
                            return
            except (OSError, ssl.SSLError, UnicodeError):
                pass
            if time.monotonic() >= deadline:
                raise UpdateError("Сервер не подтвердил готовность по доверенному HTTPS. Проверьте --health-host, сертификат и журнал сервера.")
            time.sleep(0.5)

    def image_command(self, image, *args):
        override = self.backup / ("image-" + uuid.uuid4().hex + ".json")
        override.write_text(json.dumps({"services": {"server": {"image": image}}}))
        return self.runtime + ["-f", str(override)] + list(args)

    def mark(self, status, **fields):
        self.state.update(status=status, **fields)
        temporary = self.backup / "state.json.tmp"
        temporary.write_text(json.dumps(self.state, ensure_ascii=False, indent=2))
        temporary.replace(self.backup / "state.json")

    def check_schema(self, repository, revision):
        text = command(["git", "show", revision + ":src/SPBackup.Server/ServerStore.cs"], cwd=repository)
        match = re.search(r"public const int SchemaVersion\s*=\s*(\d+)", text)
        if match is None:
            raise UpdateError("Не удалось определить версию схемы выбранного выпуска.")
        current = command(["docker", "exec", self.db, "psql", "-U", self.user, "-d", self.database, "-At", "-c",
                           "SELECT COALESCE(max(version),0) FROM server_schema"]).strip()
        if int(current) > int(match[1]):
            raise UpdateError("Выбранный выпуск старше схемы работающей БД. Понижение версии не выполняется.")

    def update(self, image, version, revision):
        self.backup_root.mkdir(parents=True, exist_ok=True)
        stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
        self.backup = self.backup_root / stamp
        self.backup.mkdir(mode=0o700)
        self.state = dict(version=version, revision=revision, project=self.project, previous_image=self.old_image,
                          volume=self.volume, database=self.database, database_user=self.user)
        self.mark("preflight")
        effective = self.backup / "compose.json"
        effective.write_text(json.dumps(self.config))
        self.runtime = ["docker", "compose", "-p", self.project, "-f", str(effective)]
        context = self.certificate(self.backup)
        self.ready(context, timeout=5)
        database_size = int(command(["docker", "exec", self.db, "psql", "-U", self.user, "-d", self.database, "-At", "-c",
                                     "SELECT pg_database_size(current_database())"]).strip())
        data_size = int(command(["docker", "run", "--rm", "--network", "none", "--user", "0", "--mount",
                                 "type=volume,src=" + self.volume + ",dst=/snapshot,readonly", "--entrypoint", "du", self.old_image,
                                 "-sb", "/snapshot"]).split()[0])
        image_size = json.loads(command(["docker", "image", "inspect", self.old_image]))[0]["Size"]
        if shutil.disk_usage(self.backup).free < database_size + data_size + image_size + 256 * 1024 * 1024:
            raise UpdateError("Недостаточно свободного места для полного комплекта возврата; сервер не остановлен.")
        (self.backup / "secrets").mkdir(mode=0o700)
        for name, secret in self.config.get("secrets", {}).items():
            if "file" in secret:
                shutil.copyfile(secret["file"], self.backup / "secrets" / name)
        retained = "spbackup-server-rollback:" + stamp.lower()
        command(["docker", "image", "tag", self.old_image, retained])
        command(["docker", "image", "save", "-o", str(self.backup / "previous-image.tar"), retained])
        stopped = False
        migrated = False
        try:
            print("Сохраняю БД и данные; сервер временно остановлен.", flush=True)
            stopped = True
            command(self.base + ["stop", "server"])
            with (self.backup / "database.dump").open("wb") as output:
                command(["docker", "exec", self.db, "pg_dump", "-U", self.user, "-d", self.database, "-Fc"], output=output)
            with (self.backup / "server-data.tar").open("wb") as output:
                command(["docker", "run", "--rm", "--network", "none", "--user", "0", "--mount",
                         "type=volume,src=" + self.volume + ",dst=/snapshot,readonly", "--entrypoint", "tar", self.old_image,
                         "--numeric-owner", "-C", "/snapshot", "-cpf", "-", "."], output=output)
            checksums = {}
            for path in self.backup.rglob("*"):
                if path.is_file() and path.name != "state.json":
                    with path.open("rb") as stream:
                        checksums[str(path.relative_to(self.backup))] = hashlib.file_digest(stream, "sha256").hexdigest()
            self.mark("backed-up", files=checksums)
            migrated = True
            self.mark("migrating")
            command(self.image_command(image, "run", "--rm", "--no-deps", "server", "--migrate"))
            command(self.image_command(image, "up", "-d", "--no-deps", "--no-build", "--pull", "never", "server"))
            self.ready(context)
            self.mark("updated", image=image)
            print("Сервер обновлён: " + version + ". Комплект возврата: " + str(self.backup), flush=True)
        except (Exception, KeyboardInterrupt):
            if stopped:
                try:
                    if migrated:
                        self.rollback(context)
                    else:
                        command(["docker", "start", self.server])
                        self.ready(context)
                        self.mark("unchanged")
                except (Exception, KeyboardInterrupt):
                    with contextlib.suppress(Exception):
                        command(self.runtime + ["stop", "server"])
                    self.mark("rollback-failed")
                    raise UpdateError("Возврат не завершён; состояние сервера не подтверждено. Сохраните комплект: " + str(self.backup)) from None
            raise UpdateError("Обновление не выполнено; прежний сервер восстановлен. Комплект: " + str(self.backup)) from None

    def rollback(self, context):
        self.mark("rolling-back")
        command(self.runtime + ["stop", "server"])
        for name, expected in self.state["files"].items():
            with (self.backup / name).open("rb") as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != expected:
                    raise UpdateError("Комплект возврата повреждён.")
        # Drop/recreate the dedicated database so objects introduced by the new migration cannot survive.
        command(["docker", "exec", self.db, "dropdb", "--force", "--if-exists", "-U", self.user, "--", self.database])
        command(["docker", "exec", self.db, "createdb", "-U", self.user, "-O", self.user, "--", self.database])
        with (self.backup / "database.dump").open("rb") as dump:
            command(["docker", "exec", "-i", self.db, "pg_restore", "--exit-on-error", "--no-owner", "-U", self.user,
                     "-d", self.database], input_file=dump)
        with (self.backup / "server-data.tar").open("rb") as data:
            command(["docker", "run", "--rm", "-i", "--network", "none", "--user", "0", "--mount",
                     "type=volume,src=" + self.volume + ",dst=/snapshot", "--entrypoint", "sh", self.old_image,
                     "-c", "find /snapshot -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + && tar --numeric-owner -C /snapshot -xpf -"], input_file=data)
        command(self.image_command(self.old_image, "up", "-d", "--no-deps", "--no-build", "--pull", "never", "server"))
        self.ready(context)
        self.mark("rolled-back")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", help="Server release tag; by default use the newest published server release")
    parser.add_argument("--offline", action="store_true", help="Use an existing local tag without GitHub/fetch")
    parser.add_argument("--compose-file", type=Path, default=ROOT / "deploy/server/compose.yaml")
    parser.add_argument("--project-name")
    parser.add_argument("--env-file", type=Path)
    parser.add_argument("--backup-directory", type=Path)
    parser.add_argument("--health-host", help="Certificate DNS name / AllowedHosts used for the local HTTPS readiness check")
    args = parser.parse_args()
    if sys.version_info < (3, 11):
        parser.error("Нужен Python 3.11 или новее.")
    if args.offline and not args.version:
        parser.error("--offline requires --version")
    os.umask(0o077)
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(InterruptedError("Обновление прервано.")))
    try:
        update = ComposeUpdate(args.compose_file, project=args.project_name, env_file=args.env_file,
                               backup_root=args.backup_directory, health_host=args.health_host)
        with update.lock():
            version = args.version or latest_version()
            revision = source_revision(ROOT, version, args.offline)
            update.check_schema(ROOT, revision)
            # The source revision belongs to the image, never a mutable container label.
            labels = json.loads(command(["docker", "image", "inspect", update.old_image]))[0]["Config"].get("Labels") or {}
            if labels.get("org.opencontainers.image.revision") == revision and labels.get("org.opencontainers.image.version") == version:
                print("Уже установлена версия " + version + ".")
                return 0
            image = "spbackup-server-update:" + uuid.uuid4().hex
            print("Собираю " + version + "; работающий сервер пока не меняется.", flush=True)
            build_image(ROOT, revision, version, image)
            update.update(image, version, revision)
        return 0
    except (UpdateError, OSError, ValueError, KeyError, KeyboardInterrupt):
        # Do not print untrusted process output, config values or credentials.
        error = sys.exc_info()[1]
        print(str(error) if isinstance(error, UpdateError) else "Обновление не выполнено: проверьте Docker, Git, Python, OpenSSL и закрытые файлы конфигурации.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
