#!/usr/bin/env python3
"""Create a fresh unprivileged Proxmox CT and install the published SP Backup server.

Only the Python standard library is needed on the Proxmox host. This same file is
copied into the new container; passwords are generated there and never cross pct.
"""
import argparse
import contextlib
import hashlib
import ipaddress
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import secrets
import shutil
import socket
import ssl
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = 'sanekman/1C-Backup'
VERSION = '0.1.0-preview.9'
CONFIG = Path('/etc/onec-backup-server')
APPLICATION = Path('/opt/onec-backup-server')
DATA = Path('/var/lib/onec-backup-server')
UNIT = Path('/etc/systemd/system/onec-backup-server.service')
ACCOUNT = 'onec-backup-server'
ARCHIVE = 'SP-Backup-server-linux-x64.tar.gz'
BOOTSTRAP = 'export DEBIAN_FRONTEND=noninteractive; apt-get update && apt-get install -y --no-install-recommends python3 ca-certificates'


def run(args, *, capture=False, input=None, env=None):
    # Secret SQL travels only through stdin; never put passwords in argv/logs.
    result = subprocess.run([str(a) for a in args], text=True, input=input, env=env,
                            stdout=subprocess.PIPE if capture else None,
                            stderr=subprocess.PIPE if input is not None else None)
    if result.returncode:
        raise RuntimeError(f'Команда {args[0]} завершилась с кодом {result.returncode}.')
    return result.stdout.strip() if capture else None


def json_command(args):
    return json.loads(run(args, capture=True))


def origin(value, port):
    uri = urllib.parse.urlsplit(value)
    if (uri.scheme != 'https' or not uri.hostname or uri.username is not None or uri.password is not None
            or uri.query or uri.fragment or uri.path not in ('', '/') or (uri.port if uri.port is not None else 443) != port):
        raise ValueError('Укажите HTTPS-адрес с выбранным портом, без пути, пароля и параметров.')
    host = uri.hostname.encode('idna').decode('ascii').lower()
    try:
        address = ipaddress.ip_address(host)
        if address.version != 4 or address.is_loopback or address.is_unspecified or address.is_multicast or address.is_link_local:
            raise ValueError('Для панели нужен доступный IPv4-адрес контейнера или DNS-имя.')
    except ValueError:
        if re.fullmatch(r'[0-9.]+', host) or ':' in host:
            raise ValueError('Нужен доступный IPv4-адрес контейнера.') from None
        if host == 'localhost' or len(host) > 253 or any(not re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', label) for label in host.split('.')):
            raise ValueError('Некорректное DNS-имя сервера.') from None
    return f'https://{host}:{port}/', host


def validate_request(request):
    if not isinstance(request, dict) or set(request) != {'version', 'address', 'name', 'port'}:
        raise ValueError('Некорректный запрос установки.')
    if not isinstance(request['version'], str) or not re.fullmatch(r'0\.1\.0-preview\.[1-9][0-9]*', request['version']):
        raise ValueError('Нужна конкретная версия сервера вида 0.1.0-preview.5.')
    if type(request['port']) is not int or not 1 <= request['port'] <= 65535 or request['port'] in (22, 5432):
        raise ValueError('Выберите HTTPS-порт 1–65535, кроме 22 и 5432.')
    if not isinstance(request['name'], str) or not request['name'].strip() or len(request['name']) > 120 or any(ord(c) < 32 or ord(c) == 127 for c in request['name']):
        raise ValueError('Имя системы мониторинга должно содержать 1–120 символов без управляющих знаков.')
    address, _ = origin(request['address'], request['port'])
    return {**request, 'address': address, 'name': request['name'].strip()}


def atomic_json(path, value, mode=0o600):
    temporary = path.with_name(path.name + '.' + secrets.token_hex(8) + '.tmp')
    try:
        with open(temporary, 'x', encoding='utf-8') as output:
            os.chmod(temporary, mode)
            json.dump(value, output, ensure_ascii=False, indent=2)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


class HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if urllib.parse.urlsplit(newurl).scheme != 'https':
            raise ValueError('Перенаправление загрузки без HTTPS запрещено.')
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def download(url, target, limit):
    opener = urllib.request.build_opener(HttpsRedirect())
    for attempt in range(3):
        try:
            with opener.open(url, timeout=60) as response, open(target, 'wb') as output:
                total = 0
                while block := response.read(1024 * 1024):
                    total += len(block)
                    if total > limit:
                        raise ValueError('Загружаемый файл больше допустимого размера.')
                    output.write(block)
            return
        except (urllib.error.URLError, TimeoutError) as error:
            target.unlink(missing_ok=True)
            if isinstance(error, urllib.error.HTTPError) and error.code not in (408, 429, 500, 502, 503, 504):
                raise
            if attempt == 2:
                raise
            time.sleep(2 * (attempt + 1))


def package_asset(manifest, version):
    if (manifest.get('version') != version or manifest.get('runtime') != '10.0.13'
            or not re.fullmatch(r'[a-f0-9]{40}', manifest.get('sourceCommit', ''))):
        raise ValueError('Манифест не соответствует версии или поддерживаемому runtime 10.0.13.')
    entries = [a for a in manifest.get('assets', []) if a.get('name') == ARCHIVE]
    if len(entries) != 1:
        raise ValueError('В выпуске должен быть один Linux x64 пакет сервера.')
    asset = entries[0]
    expected = f'https://github.com/{REPOSITORY}/releases/download/server-v{version}/{ARCHIVE}'
    if (asset.get('url') != expected or type(asset.get('bytes')) is not int or not 0 < asset['bytes'] <= 256 * 1024 * 1024
            or not re.fullmatch(r'[a-f0-9]{64}', asset.get('sha256', ''))):
        raise ValueError('Некорректные адрес, размер или SHA-256 серверного пакета.')
    return asset


def extract_package(archive, target, version):
    # No archive member may create links, devices, escape the root or replace a file.
    seen = set()
    total = 0
    with tarfile.open(archive, 'r:gz') as source:
        for member in source:
            parts = PurePosixPath(member.name).parts
            total += member.size
            if (not parts or parts[0] != 'SP-Backup-Server' or '..' in parts or member.name.startswith('/')
                    or '\\' in member.name or any(ord(c) < 32 for c in member.name)
                    or not (member.isfile() or member.isdir()) or member.name in seen
                    or len(seen) >= 10000 or total > 1024 * 1024 * 1024):
                raise ValueError('Небезопасное содержимое серверного архива.')
            seen.add(member.name)
            destination = target.joinpath(*parts[1:])
            if member.isdir():
                destination.mkdir(parents=True, exist_ok=True, mode=0o755)
            else:
                destination.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
                with source.extractfile(member) as input_file, open(destination, 'xb') as output:
                    shutil.copyfileobj(input_file, output)
                os.chmod(destination, 0o755 if member.mode & 0o111 else 0o644)
    if (target.joinpath('VERSION.txt').read_text().strip() != version
            or not target.joinpath('SPBackup.Server').is_file()
            or not os.access(target / 'SPBackup.Server', os.X_OK)):
        raise ValueError('В архиве нет исполняемого сервера нужной версии.')


def install_package(request, package_directory, activate=True):
    with tempfile.TemporaryDirectory(prefix='sp-backup-package-') as work:
        temporary = Path(work)
        manifest_path = temporary / 'server-release.json'
        version = request['version']
        if package_directory:
            source = Path(package_directory) / 'server-release.json'
            if source.is_symlink() or source.stat().st_size > 1024 * 1024:
                raise ValueError('Некорректный локальный манифест.')
            shutil.copyfile(source, manifest_path)
        else:
            download(f'https://github.com/{REPOSITORY}/releases/download/server-v{version}/server-release.json', manifest_path, 1024 * 1024)
        asset = package_asset(json.loads(manifest_path.read_text()), version)
        archive = temporary / ARCHIVE
        if package_directory:
            source = Path(package_directory) / ARCHIVE
            if source.is_symlink() or source.stat().st_size != asset['bytes']:
                raise ValueError('Размер локального пакета не совпадает с манифестом.')
            shutil.copyfile(source, archive)
        else:
            download(asset['url'], archive, asset['bytes'])
        with archive.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        if archive.stat().st_size != asset['bytes'] or digest != asset['sha256']:
            raise ValueError('Пакет не прошёл проверку размера / SHA-256; запуск запрещён.')
        APPLICATION.mkdir(mode=0o755, exist_ok=True)
        release = APPLICATION / version
        marker = {'version': version, 'sha256': digest}
        if release.exists():
            if release.is_symlink() or json.loads((release / 'installed-package.json').read_text()) != marker:
                raise ValueError('Существующая версия не соответствует проверенному пакету.')
        else:
            with tempfile.TemporaryDirectory(prefix='.staging-', dir=APPLICATION) as staging:
                extracted = Path(staging) / 'server'
                extract_package(archive, extracted, version)
                atomic_json(extracted / 'installed-package.json', marker, 0o644)
                os.rename(extracted, release)
        if not activate:
            return release
        link = APPLICATION / 'current'
        if link.is_symlink():
            if link.resolve() != release.resolve():
                raise ValueError('Активна другая версия сервера; обновление этим установщиком запрещено.')
        elif link.exists():
            raise ValueError('Путь current уже занят.')
        else:
            link.symlink_to(release, target_is_directory=True)


def secret_file(name, gid):
    path = CONFIG / name
    if path.is_symlink():
        raise ValueError('Файл секрета не должен быть ссылкой.')
    if not path.exists():
        with open(path, 'x') as output:
            os.chmod(path, 0o640)
            os.chown(path, 0, gid)
            output.write(secrets.token_urlsafe(32))
            output.flush()
            os.fsync(output.fileno())
    value = path.read_text().strip()
    if not re.fullmatch(r'[a-zA-Z0-9_-]{32,128}', value):
        raise ValueError('Некорректный сохранённый секрет; автоматический сброс запрещён.')
    return value


def certificates(host, gid):
    private = CONFIG / 'private'
    private.mkdir(mode=0o700, exist_ok=True)
    ca_key, ca, key, csr, cert = [private / n for n in ('ca.key', 'ca.crt', 'server.key', 'server.csr', 'server.crt')]
    if not ca_key.exists():
        run(['openssl', 'genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:3072', '-out', ca_key])
    if not ca.exists():
        run(['openssl', 'req', '-x509', '-new', '-key', ca_key, '-sha256', '-days', '3650', '-subj', '/CN=SP Backup local CA',
             '-addext', 'basicConstraints=critical,CA:TRUE,pathlen:0', '-addext', 'keyUsage=critical,keyCertSign,cRLSign', '-out', ca])
    if not key.exists():
        run(['openssl', 'genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:3072', '-out', key])
    if not csr.exists():
        run(['openssl', 'req', '-new', '-key', key, '-subj', '/CN=' + host, '-out', csr])
    if not cert.exists():
        try:
            ipaddress.IPv4Address(host)
            san = 'IP:' + host
        except ValueError:
            san = 'DNS:' + host
        extensions = private / 'extensions.cnf'
        extensions.write_text('basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=' + san + ',IP:127.0.0.1,DNS:localhost\n')
        run(['openssl', 'x509', '-req', '-in', csr, '-CA', ca, '-CAkey', ca_key, '-set_serial', str(secrets.randbits(159) or 1),
             '-days', '730', '-sha256', '-extfile', extensions, '-out', cert])
    public_ca = CONFIG / 'ca.crt'
    if not public_ca.exists():
        shutil.copyfile(ca, public_ca)
        os.chmod(public_ca, 0o644)
    pfx = CONFIG / 'server.pfx'
    if not pfx.exists():
        temporary = private / 'server.pfx.tmp'
        run(['openssl', 'pkcs12', '-export', '-inkey', key, '-in', cert, '-certfile', ca,
             '-passout', 'file:' + str(CONFIG / 'certificate-password.txt'), '-out', temporary])
        os.chmod(temporary, 0o640)
        os.chown(temporary, 0, gid)
        os.replace(temporary, pfx)


def sql(statement):
    return run(['runuser', '-u', 'postgres', '--', 'psql', '-X', '-qAt', '--set', 'ON_ERROR_STOP=1', 'postgres'],
               capture=True, input="SET log_statement='none'; SET log_min_error_statement='panic';\n" + statement)


def ensure_database_utf8(create=False):
    encoding = sql("SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname='onecbackup';").strip()
    if not encoding and create:
        # template1 may be SQL_ASCII on minimal Debian installations. template0
        # and the universally available C locale make this independent of it.
        sql("CREATE DATABASE onecbackup OWNER onecbackup TEMPLATE template0 ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C';")
        encoding = sql("SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname='onecbackup';").strip()
    if encoding != 'UTF8':
        raise ValueError('База onecbackup должна иметь кодировку UTF8; обнаружена: ' + (encoding or 'база отсутствует')
                         + '. Данные и пароли сохранены. Для существующей базы нужен отдельный перенос в UTF8 с резервной копией; повторная установка её не преобразует.')


def wait_ready(port):
    context = ssl.create_default_context(cafile=str(CONFIG / 'ca.crt'))
    # Only our own loopback health check bypasses HTTP proxies.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
    for _ in range(60):
        try:
            with opener.open(f'https://127.0.0.1:{port}/health/ready', timeout=2) as response:
                if response.status == 200:
                    return
        except (urllib.error.URLError, TimeoutError):
            pass
        time.sleep(1)
    raise RuntimeError('Сервер не подтвердил готовность по HTTPS. Сохранены все данные; проверьте journalctl -u onec-backup-server.')


def service_unit(port):
    # Dedicated unprivileged LXC already provides the container boundary. Do not
    # require nested mount namespaces (ProtectSystem/PrivateTmp/ReadWritePaths),
    # which depend on the hypervisor's AppArmor/nesting policy. Unix ownership,
    # the separate service UID and NoNewPrivileges remain in force.
    capabilities = 'AmbientCapabilities=CAP_NET_BIND_SERVICE\n' if port < 1024 else ''
    return f'''[Unit]
Description=SP Backup management server
After=network-online.target postgresql@17-main.service
Wants=network-online.target postgresql@17-main.service

[Service]
Type=notify
User=onec-backup-server
Group=onec-backup-server
WorkingDirectory=/opt/onec-backup-server/current
ExecStart=/opt/onec-backup-server/current/SPBackup.Server
EnvironmentFile=/etc/onec-backup-server/server.env
StateDirectory=onec-backup-server
StateDirectoryMode=0700
UMask=0077
NoNewPrivileges=true
Restart=on-failure
RestartSec=5
{capabilities}
[Install]
WantedBy=multi-user.target
'''


def guest_install(request, package_directory=None):
    import pwd
    request = validate_request(request)
    release = Path('/etc/os-release').read_text()
    if 'ID=debian' not in release or 'VERSION_ID="13"' not in release or platform.machine() != 'x86_64':
        raise ValueError('Установщик рассчитан на Debian 13 amd64.')
    if not shutil.which('systemd-detect-virt'):
        raise ValueError('Нужен standard-шаблон Debian 13 с systemd.')
    if subprocess.run(['systemd-detect-virt', '--container'], capture_output=True).returncode:
        raise ValueError('Внутренний режим разрешён только внутри контейнера, не на хосте Proxmox.')
    journal = CONFIG / 'installation.json'
    if CONFIG.is_symlink() or APPLICATION.is_symlink() or DATA.is_symlink():
        raise ValueError('Каталоги установки не должны быть ссылками.')
    if journal.exists():
        saved = json.loads(journal.read_text())
        if saved.get('request') != request or saved.get('format') != 1:
            raise ValueError('Установка уже настроена иначе. Автоматическая смена версии, адреса и паролей запрещена.')
        if saved.get('complete'):
            ensure_database_utf8()
            wait_ready(request['port'])
            print('Сервер уже установлен; настройки и пароли сохранены. ' + request['address'])
            return
    else:
        # This installer owns only a fresh CT; it never adopts an existing database.
        if any(p.exists() or p.is_symlink() for p in (CONFIG, APPLICATION, DATA, UNIT)) or shutil.which('psql'):
            raise ValueError('Нужен новый контейнер без установленного сервера / PostgreSQL. Существующие данные не изменены.')
        try:
            pwd.getpwnam(ACCOUNT)
        except KeyError:
            pass
        else:
            raise ValueError('Сервисный пользователь уже существует; установка остановлена.')
        with contextlib.closing(socket.socket()) as probe:
            probe.bind(('0.0.0.0', request['port']))
        CONFIG.mkdir(mode=0o700)
        saved = {'format': 1, 'request': request, 'complete': False}
        atomic_json(journal, saved)
    # Executable files and Debian package metadata must remain readable to services.
    # Secrets below are created only after switching back to a private umask.
    os.umask(0o022)
    install_package(request, package_directory)
    environment = dict(os.environ, DEBIAN_FRONTEND='noninteractive')
    run(['apt-get', 'update'], env=environment)
    run(['apt-get', 'install', '-y', '--no-install-recommends', 'ca-certificates', 'openssl', 'libicu76',
         'libssl3t64', 'libstdc++6', 'zlib1g', 'tzdata', 'libjson-perl', 'postgresql-17'], env=environment)
    os.umask(0o077)
    try:
        account = pwd.getpwnam(ACCOUNT)
    except KeyError:
        run(['useradd', '--system', '--user-group', '--home-dir', str(DATA), '--no-create-home', '--shell', '/usr/sbin/nologin', ACCOUNT])
        account = pwd.getpwnam(ACCOUNT)
    os.chown(CONFIG, 0, account.pw_gid)
    os.chmod(CONFIG, 0o750)
    DATA.mkdir(mode=0o700, exist_ok=True)
    os.chown(DATA, account.pw_uid, account.pw_gid)
    database_password = secret_file('database-password.txt', account.pw_gid)
    secret_file('administrator-password.txt', account.pw_gid)
    secret_file('certificate-password.txt', account.pw_gid)
    _, host = origin(request['address'], request['port'])
    certificates(host, account.pw_gid)
    configuration = {'Urls': f'https://0.0.0.0:{request["port"]}', 'AllowedHosts': host + ';localhost;127.0.0.1', 'Server': {
        'DisplayName': request['name'], 'PublicAddress': request['address'], 'DataDirectory': str(DATA),
        'ConnectionString': 'Host=127.0.0.1;Database=onecbackup;Username=onecbackup',
        'DatabasePasswordFile': str(CONFIG / 'database-password.txt'),
        'BootstrapUser': 'admin', 'BootstrapPasswordFile': str(CONFIG / 'administrator-password.txt'),
        'HttpsCertificatePath': str(CONFIG / 'server.pfx'), 'HttpsCertificatePasswordFile': str(CONFIG / 'certificate-password.txt')}}
    config_path = CONFIG / 'server.json'
    if config_path.exists() and json.loads(config_path.read_text()) != configuration:
        raise ValueError('Конфигурация была изменена; перезапись запрещена.')
    atomic_json(config_path, configuration, 0o640)
    os.chown(config_path, 0, account.pw_gid)
    clusters = json_command(['pg_lsclusters', '--json'])
    if len(clusters) != 1 or str(clusters[0]['version']) != '17' or clusters[0]['cluster'] != 'main':
        raise ValueError('Обнаружена нестандартная PostgreSQL. Существующие кластеры не изменены.')
    pg_config = Path('/etc/postgresql/17/main/conf.d/sp-backup.conf')
    pg_config.write_text("listen_addresses = 'localhost'\npassword_encryption = 'scram-sha-256'\n")
    os.chmod(pg_config, 0o644)
    run(['systemctl', 'enable', 'postgresql'])
    run(['systemctl', 'restart', 'postgresql@17-main'])
    if not sql("SELECT 1 FROM pg_roles WHERE rolname='onecbackup';"):
        sql("CREATE ROLE onecbackup LOGIN PASSWORD '" + database_password.replace("'", "''") + "';")
    ensure_database_utf8(create=True)
    run(['runuser', '-u', ACCOUNT, '--', APPLICATION / 'current/SPBackup.Server', '--Server:ConfigFile', config_path, '--provision'])
    env_path = CONFIG / 'server.env'
    env_path.write_text('ONECBACKUP_SERVER_CONFIG_FILE=/etc/onec-backup-server/server.json\n')
    os.chmod(env_path, 0o600)
    UNIT.write_text(service_unit(request['port']))
    os.chmod(UNIT, 0o644)
    run(['systemctl', 'daemon-reload'])
    run(['systemctl', 'enable', '--now', 'onec-backup-server'])
    wait_ready(request['port'])
    atomic_json(journal, {**saved, 'complete': True})
    print('Сервер готов: ' + request['address'])
    print('Логин: admin. Пароль: /etc/onec-backup-server/administrator-password.txt (только для администратора).')


def storage_names(content):
    rows = run(['pvesm', 'status', '--content', content, '--enabled', '1'], capture=True).splitlines()[1:]
    return [row.split()[0] for row in rows if len(row.split()) >= 3 and row.split()[2] == 'active']


def network(ip, gateway):
    if ip == 'dhcp':
        if gateway:
            raise ValueError('Шлюз задаётся только вместе со статическим IPv4/CIDR.')
        return 'ip=dhcp'
    interface = ipaddress.IPv4Interface(ip)
    router = ipaddress.IPv4Address(gateway)
    if (router not in interface.network or router == interface.ip
            or interface.ip.is_loopback or interface.ip.is_unspecified or interface.ip.is_multicast or interface.ip.is_link_local
            or router in (interface.network.network_address, interface.network.broadcast_address)
            or interface.ip in (interface.network.network_address, interface.network.broadcast_address)
            or interface.network.prefixlen > 30):
        raise ValueError('Нужны свободный IPv4/CIDR и другой шлюз в той же подсети.')
    return f'ip={interface},gw={router}'


def latest_update_version():
    with tempfile.TemporaryDirectory(prefix='sp-backup-release-') as work:
        target = Path(work) / 'releases.json'
        download(f'https://api.github.com/repos/{REPOSITORY}/releases?per_page=100', target, 2 * 1024 * 1024)
        versions = [r['tag_name'].removeprefix('server-v') for r in json.loads(target.read_text())
                    if not r.get('draft') and re.fullmatch(r'server-v0\.1\.0-preview\.[1-9][0-9]*', r.get('tag_name', ''))]
    if not versions:
        raise ValueError('Опубликованный совместимый серверный выпуск не найден.')
    return max(versions, key=lambda version: int(version.rsplit('.', 1)[1]))


def binary_command(args, *, output=None, input_file=None):
    result = subprocess.run([str(a) for a in args], stdin=input_file, stdout=output or subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        raise RuntimeError(f'Команда {args[0]} завершилась с кодом {result.returncode}.')
    return result.stdout


def reject_links(path):
    if path.is_symlink() or not path.is_dir():
        raise ValueError('Нужен обычный каталог штатной установки: ' + str(path))
    if any(p.is_symlink() or not (p.is_file() or p.is_dir()) for p in path.rglob('*')):
        raise ValueError('В каталоге установки есть ссылки или специальные файлы: ' + str(path))


def switch_release(release):
    link = APPLICATION / ('.current-' + secrets.token_hex(8))
    try:
        link.symlink_to(release, target_is_directory=True)
        os.replace(link, APPLICATION / 'current')
    finally:
        link.unlink(missing_ok=True)


def snapshot_tree(source, archive):
    reject_links(source)
    binary_command(['tar', '--numeric-owner', '-C', source, '-cpf', archive, '.'])


def restore_tree(archive, destination):
    if destination.is_symlink() or not destination.is_dir():
        raise ValueError('Каталог возврата был заменён.')
    for child in destination.iterdir():
        if child.is_dir() and not child.is_symlink():
            shutil.rmtree(child)
        else:
            child.unlink()
    binary_command(['tar', '--numeric-owner', '-C', destination, '-xpf', archive])


def connection_fields(value):
    """Read ADO-style quoting without exposing values (passwords may contain ';' or quotes)."""
    if not isinstance(value, str):
        raise ValueError('ConnectionString: отсутствует строка подключения.')
    fields = []
    position = 0
    while position < len(value):
        while position < len(value) and (value[position].isspace() or value[position] == ';'):
            position += 1
        if position == len(value):
            break
        start = position
        while position < len(value) and value[position] not in '=;':
            position += 1
        if position == len(value) or value[position] != '=':
            raise ValueError('ConnectionString: некорректный формат параметров.')
        key = ''.join(value[start:position].split()).casefold()
        if not key:
            raise ValueError('ConnectionString: пустое имя параметра.')
        position += 1
        while position < len(value) and value[position].isspace():
            position += 1
        if position < len(value) and value[position] in '\"\'':
            quote = value[position]
            position += 1
            text = []
            while True:
                if position == len(value):
                    raise ValueError('ConnectionString: не закрыты кавычки.')
                char = value[position]
                position += 1
                if char == quote:
                    if position < len(value) and value[position] == quote:
                        text.append(quote)
                        position += 1
                    else:
                        break
                else:
                    text.append(char)
            item = ''.join(text)
            while position < len(value) and value[position].isspace():
                position += 1
            if position < len(value) and value[position] != ';':
                raise ValueError('ConnectionString: лишний текст после кавычек.')
        else:
            start = position
            while position < len(value) and value[position] != ';':
                position += 1
            item = value[start:position].strip()
        fields.append((key, item))
    return fields


def update_database_target(value):
    aliases = {'host': 'host', 'server': 'host', 'port': 'port', 'database': 'database',
               'username': 'user', 'userid': 'user', 'uid': 'user'}
    options = {'password', 'pwd', 'sslmode', 'trustservercertificate', 'timeout', 'commandtimeout',
               'pooling', 'minimumpoolsize', 'minpoolsize', 'maximumpoolsize', 'maxpoolsize',
               'connectionidlelifetime', 'connectionpruninginterval', 'connectionlifetime',
               'keepalive', 'tcpkeepalive', 'tcpkeepalivetime', 'tcpkeepaliveinterval',
               'applicationname', 'includerrordetail', 'persistsecurityinfo', 'noresetonclose',
               'maxautoprepare', 'autoprepareminusages', 'readbuffersize', 'writebuffersize', 'multiplexing'}
    target = {}
    for key, value in connection_fields(value):
        if key in aliases:
            name = aliases[key]
            if name in target:
                raise ValueError('ConnectionString: повторяются параметры выбора БД.')
            target[name] = value
        elif key not in options:
            raise ValueError('ConnectionString: неподдерживаемый параметр; автоматическое обновление запрещено.')
    if target.get('host', '').casefold() not in {'localhost', '127.0.0.1', '::1'}:
        raise ValueError('ConnectionString: нужен локальный Host штатной PostgreSQL.')
    if target.get('port', '5432') != '5432':
        raise ValueError('ConnectionString: нужен штатный Port=5432.')
    database_name = target.get('database', '')
    if not re.fullmatch(r'onecbackup(?:_utf8_[0-9]{8}_[0-9]{6})?', database_name):
        raise ValueError('ConnectionString: нужна штатная БД onecbackup или её именованная UTF8-копия.')
    if target.get('user') != 'onecbackup':
        raise ValueError('ConnectionString: нужен Username=onecbackup.')
    return target['host'], 5432, database_name


def verify_update_database(host, port, database_name):
    clusters = json_command(['pg_lsclusters', '--json'])
    if len(clusters) != 1 or str(clusters[0]['version']) != '17' or clusters[0]['cluster'] != 'main' or int(clusters[0]['port']) != port:
        raise ValueError('Обновление требует штатного кластера PostgreSQL 17/main на порту 5432.')
    # database_name has already passed the restricted ASCII name check above.
    metadata = json.loads(sql("SELECT json_build_object('name',datname,'owner',pg_get_userbyid(datdba),"
                              "'encoding',pg_encoding_to_char(encoding),'collate',datcollate,'ctype',datctype,"
                              "'bytes',pg_database_size(oid)) FROM pg_database WHERE datname='" + database_name + "';") or 'null')
    if not metadata or metadata['owner'] != 'onecbackup':
        raise ValueError('Не подтверждена БД с владельцем onecbackup; сервер не остановлен.')
    if metadata['encoding'] != 'UTF8':
        raise ValueError('Подключённая БД должна иметь кодировку UTF8; сервер не остановлен.')
    expected = sql('SELECT extract(epoch FROM pg_catalog.pg_postmaster_start_time())::text;')
    environment = {key: value for key, value in os.environ.items() if not key.startswith('PG')}
    environment['PGPASSWORD'] = (CONFIG / 'database-password.txt').read_text().strip()
    environment['PGCONNECT_TIMEOUT'] = '5'
    query = ("SELECT json_build_object('database',current_database(),'user',current_user,"
             "'started',extract(epoch FROM pg_catalog.pg_postmaster_start_time())::text,"
             "'schema',to_regclass('public.server_schema') IS NOT NULL AND "
             "to_regclass('public.server_users') IS NOT NULL AND to_regclass('public.server_agents') IS NOT NULL)")
    result = subprocess.run(['psql', '-X', '-qAt', '-h', host, '-p', str(port), '-U', 'onecbackup', '-d', database_name, '-c', query],
                            env=environment, capture_output=True, text=True)
    if result.returncode:
        raise ValueError('ConnectionString: не подтверждён доступ к локальной PostgreSQL; сервер не остановлен.')
    observed = json.loads(result.stdout)
    if observed != {'database': database_name, 'user': 'onecbackup', 'started': expected, 'schema': True}:
        raise ValueError('ConnectionString: не подтверждены локальный кластер, выбранная БД и схема SP Backup; сервер не остановлен.')

    return metadata


def guest_update(version, package_directory=None, backup_directory=None):
    release_info = Path('/etc/os-release').read_text()
    if ('ID=debian' not in release_info or 'VERSION_ID="13"' not in release_info
            or platform.machine() != 'x86_64' or subprocess.run(['systemd-detect-virt', '--container'], capture_output=True).returncode):
        raise ValueError('Обновление поддерживает Debian 13 amd64 внутри LXC из мастера.')
    for path in [CONFIG, APPLICATION, DATA]:
        if path != APPLICATION:
            reject_links(path)
        if path.is_symlink() or not path.is_dir():
            raise ValueError('Не найдена штатная установка сервера.')
    journal = CONFIG / 'installation.json'
    saved = json.loads(journal.read_text())
    if saved.get('format') != 1 or saved.get('complete') is not True:
        raise ValueError('Сначала требуется завершённая установка из мастера.')
    previous = validate_request(saved['request'])
    request = validate_request({**previous, 'version': version})
    link = APPLICATION / 'current'
    old = link.resolve()
    if not link.is_symlink() or old.parent != APPLICATION.resolve() or old.name != previous['version']:
        raise ValueError('Активная версия не соответствует журналу установки; данные не изменены.')
    reject_links(old)
    if int(version.rsplit('.', 1)[1]) < int(previous['version'].rsplit('.', 1)[1]):
        raise ValueError('Понижение версии не выполняется.')
    configuration = json.loads((CONFIG / 'server.json').read_text())['Server']
    expected = {'DataDirectory': str(DATA),
                'DatabasePasswordFile': str(CONFIG / 'database-password.txt'), 'HttpsCertificatePath': str(CONFIG / 'server.pfx'),
                'HttpsCertificatePasswordFile': str(CONFIG / 'certificate-password.txt')}
    mismatches = [key for key, value in expected.items() if configuration.get(key) != value]
    if mismatches:
        raise ValueError('Не совпадают параметры штатной установки: ' + ', '.join(mismatches) + '. Сервер не остановлен.')
    if any(key.casefold() == 'connectionstringfile' and value is not None for key, value in configuration.items()):
        raise ValueError('ConnectionStringFile: отдельный файл подключения не поддерживается обновлением.')
    host, port, database_name = update_database_target(configuration.get('ConnectionString'))
    if UNIT.is_symlink() or not UNIT.is_file() or UNIT.with_name(UNIT.name + '.d').exists():
        raise ValueError('Нужна штатная служба без дополнительных override-файлов.')
    unit = UNIT.read_text()
    if f'User={ACCOUNT}\n' not in unit or f'ExecStart={APPLICATION}/current/SPBackup.Server\n' not in unit:
        raise ValueError('Служба настроена иначе; обновление не выполняется.')
    env_path = CONFIG / 'server.env'
    lines = [line.strip() for line in env_path.read_text().splitlines() if line.strip() and not line.lstrip().startswith('#')]
    if lines != ['ONECBACKUP_SERVER_CONFIG_FILE=' + str(CONFIG / 'server.json')] or any(line.startswith('Environment=') for line in unit.splitlines()):
        raise ValueError('Есть дополнительные переменные службы; эффективная конфигурация не подтверждена.')
    if [line for line in unit.splitlines() if line.startswith('EnvironmentFile=')] != ['EnvironmentFile=' + str(env_path)]:
        raise ValueError('EnvironmentFile службы отличается от штатного.')
    database = verify_update_database(host, port, database_name)
    run(['systemctl', 'is-active', '--quiet', 'onec-backup-server'])
    wait_ready(previous['port'])
    if version == previous['version']:
        print('Сервер уже обновлён: ' + version)
        return
    backup_root = Path(backup_directory or '/var/backups/onec-backup-server').resolve()
    if any(backup_root == p.resolve() or backup_root.is_relative_to(p.resolve()) or p.resolve().is_relative_to(backup_root)
           for p in [CONFIG, APPLICATION, DATA]):
        raise ValueError('Каталог копии не должен пересекаться с программой, конфигурацией или данными.')
    backup_root.mkdir(mode=0o700, parents=True, exist_ok=True)
    if backup_root.stat().st_uid != 0 or backup_root.stat().st_mode & 0o022:
        raise ValueError('Каталог комплектов должен принадлежать root и быть закрыт от записи другими пользователями.')
    required = int(database['bytes']) + sum(p.stat().st_size for root in [old, DATA, CONFIG] for p in root.rglob('*') if p.is_file()) + 1024 * 1024 * 1024
    if shutil.disk_usage(backup_root).free < required:
        raise ValueError('Недостаточно места для пакета и полного комплекта возврата; сервер не остановлен.')
    # Download/check/extract first. Existing installed versions and current stay untouched.
    target = install_package(request, package_directory, activate=False)
    reject_links(target)
    stamp = time.strftime('%Y%m%dT%H%M%SZ', time.gmtime()) + '-' + secrets.token_hex(4)
    backup = backup_root / stamp
    backup.mkdir(mode=0o700)
    state = {'format': 1, 'from': previous['version'], 'to': version, 'database': database, 'status': 'preparing'}
    def mark(status):
        state['status'] = status
        atomic_json(backup / 'state.json', state)
    mark('preparing')
    snapshot_tree(CONFIG, backup / 'config.tar')
    snapshot_tree(old, backup / 'program.tar')
    shutil.copy2(UNIT, backup / 'unit.service')
    stopped = False
    migrated = False
    try:
        stopped = True
        run(['systemctl', 'stop', 'onec-backup-server'])
        with (backup / 'database.dump').open('wb') as output:
            binary_command(['runuser', '-u', 'postgres', '--', 'pg_dump', '-Fc', '-d', database_name], output=output)
        snapshot_tree(DATA, backup / 'data.tar')
        state['files'] = {}
        for name in ['config.tar', 'program.tar', 'unit.service', 'database.dump', 'data.tar']:
            with (backup / name).open('rb') as stream:
                state['files'][name] = hashlib.file_digest(stream, 'sha256').hexdigest()
        mark('backed-up')
        migrated = True
        mark('migrating')
        run(['runuser', '-u', ACCOUNT, '--', target / 'SPBackup.Server', '--Server:ConfigFile', CONFIG / 'server.json', '--migrate'])
        switch_release(target)
        run(['systemctl', 'start', 'onec-backup-server'])
        wait_ready(previous['port'])
        atomic_json(journal, {**saved, 'request': request})
        mark('updated')
        print('Сервер обновлён: ' + version + '. Комплект возврата: ' + str(backup))
    except (Exception, KeyboardInterrupt):
        try:
            if stopped:
                run(['systemctl', 'stop', 'onec-backup-server'])
            if migrated:
                for name, digest in state['files'].items():
                    with (backup / name).open('rb') as stream:
                        if hashlib.file_digest(stream, 'sha256').hexdigest() != digest:
                            raise ValueError('Повреждён комплект возврата.')
                mark('rolling-back')
                run(['runuser', '-u', 'postgres', '--', 'dropdb', '--force', '--if-exists', database_name])
                run(['runuser', '-u', 'postgres', '--', 'createdb', '--template=template0', '--encoding=UTF8',
                     '--lc-collate=' + database['collate'], '--lc-ctype=' + database['ctype'], '--owner=onecbackup', database_name])
                with (backup / 'database.dump').open('rb') as dump:
                    binary_command(['runuser', '-u', 'postgres', '--', 'pg_restore', '--exit-on-error', '--no-owner', '--role=onecbackup', '-d', database_name], input_file=dump)
                restore_tree(backup / 'data.tar', DATA)
                restore_tree(backup / 'config.tar', CONFIG)
                restore_tree(backup / 'program.tar', old)
                shutil.copy2(backup / 'unit.service', UNIT)
                switch_release(old)
                run(['systemctl', 'daemon-reload'])
            if stopped:
                run(['systemctl', 'start', 'onec-backup-server'])
                wait_ready(previous['port'])
            mark('rolled-back' if migrated else 'unchanged')
        except (Exception, KeyboardInterrupt):
            with contextlib.suppress(Exception):
                run(['systemctl', 'stop', 'onec-backup-server'])
            with contextlib.suppress(Exception):
                mark('rollback-failed')
            raise RuntimeError('Возврат не завершён; состояние сервера не подтверждено. Комплект: ' + str(backup)) from None
        raise RuntimeError('Обновление не выполнено; прежняя версия восстановлена. Комплект: ' + str(backup)) from None


# Root-owned update broker. Only a fixed protocol crosses the Unix socket.
WEB_UPDATE_HOME = Path('/var/lib/onec-backup-server-updater')
WEB_UPDATE_SCRIPT = Path('/usr/local/lib/onec-backup-server-updater/update.py')
WEB_UPDATE_SOCKET = '/run/onec-backup-server-update.sock'
WEB_UPDATE_UNITS = {'checking': 'onec-backup-server-update-check.service', 'queued': 'onec-backup-server-update-run.service',
                    'updating': 'onec-backup-server-update-run.service'}


def web_update_version(value):
    return isinstance(value, str) and re.fullmatch(r'0\.1\.0-preview\.[1-9][0-9]*', value) is not None


def web_update_current():
    saved = json.loads((CONFIG / 'installation.json').read_text())
    value = saved['request']['version']
    if saved.get('complete') is not True or not web_update_version(value):
        raise ValueError('Не подтверждена штатная установка сервера.')
    return value


@contextlib.contextmanager
def web_update_lock():
    import fcntl
    descriptor = os.open(WEB_UPDATE_HOME / 'control.lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        yield


def web_update_read():
    path = WEB_UPDATE_HOME / 'state.json'
    if not path.exists():
        return {'phase': 'idle'}
    if path.is_symlink() or path.stat().st_size > 16384:
        raise ValueError('Неверное состояние обновления.')
    state = json.loads(path.read_text())
    if not isinstance(state, dict): raise ValueError('Неверное состояние обновления.')
    return state


def web_update_save(state):
    atomic_json(WEB_UPDATE_HOME / 'state.json', state)


def web_update_status(state):
    # No logs, configuration, exceptions or credentials are sent to the panel.
    keys = ['phase', 'latestVersion', 'checkedUtc', 'jobId', 'targetVersion', 'startedUtc', 'finishedUtc', 'backupDirectory']
    result = {**{key: state[key] for key in keys if key in state}, 'currentVersion': web_update_current()}
    if isinstance(state.get('lastResult'), dict):
        result['lastResult'] = {key: state['lastResult'][key] for key in ['phase', 'jobId', 'targetVersion', 'finishedUtc', 'backupDirectory'] if key in state['lastResult']}
    return result


def web_update_service(args):
    result = subprocess.run(['systemctl', *args], stdin=subprocess.DEVNULL, capture_output=True,
                            text=True, timeout=10, env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LANG': 'C', 'LC_ALL': 'C'})
    if result.returncode:
        raise ValueError('Не удалось запустить системную задачу обновления.')
    return result.stdout.strip()


def web_update_request(request):
    if not isinstance(request, dict) or set(request) - {'command', 'version'} or not isinstance(request.get('command'), str) or request['command'] not in {'status', 'check', 'start'}:
        return {'phase': 'invalidRequest'}
    command = request['command']
    if command != 'start' and 'version' in request:
        return {'phase': 'invalidRequest'}
    with web_update_lock():
        state = web_update_read()
        phase = state.get('phase')
        if phase in WEB_UPDATE_UNITS and time.time() - state.get('startedUtc', time.time()) > 20:
            try:
                active = web_update_service(['show', '--property=ActiveState', '--value', WEB_UPDATE_UNITS[phase]])
                if active not in {'active', 'activating', 'reloading'}:
                    state['phase'] = 'interrupted'; web_update_save(state)
            except (ValueError, subprocess.TimeoutExpired):
                pass  # Unknown unit state does not authorize a second update.
        if command == 'status' or state.get('phase') in WEB_UPDATE_UNITS:
            return web_update_status(state)
        if state.get('phase') == 'interrupted':
            return web_update_status(state)  # An operator must resolve an interrupted job first.
        if command == 'start':
            version = request.get('version')
            if (state.get('phase') != 'available' or not web_update_version(version) or version != state.get('latestVersion')
                    or time.time() - state.get('checkedUtc', 0) > 1800
                    or int(version.rsplit('.', 1)[1]) <= int(web_update_current().rsplit('.', 1)[1])):
                return {**web_update_status(state), 'requestError': 'checkRequired'}
        state = {**state, 'phase': 'checking' if command == 'check' else 'queued',
                 'jobId': secrets.token_hex(16), 'startedUtc': time.time()}
        for key in ['backupDirectory', 'finishedUtc']:
            state.pop(key, None)
        if command == 'start': state['targetVersion'] = request['version']
        else: state.pop('targetVersion', None)
        web_update_save(state)
        try:
            web_update_service(['start', '--no-block', WEB_UPDATE_UNITS[state['phase']]])
        except (ValueError, subprocess.TimeoutExpired):
            state['phase'] = 'dispatchFailed'; web_update_save(state)
        return web_update_status(state)


def web_update_control():
    import pwd
    import struct
    with socket.fromfd(0, socket.AF_UNIX, socket.SOCK_STREAM) as connection:
        connection.settimeout(10)
        _, uid, _ = struct.unpack('3i', connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize('3i')))
        if uid not in {0, pwd.getpwnam(ACCOUNT).pw_uid}:
            return
        data = bytearray()
        while b'\n' not in data and len(data) <= 4096:
            chunk = connection.recv(4097 - len(data))
            if not chunk: break
            data.extend(chunk)
        try:
            if len(data) > 4096 or not data.endswith(b'\n'): raise ValueError('invalid request')
            result = web_update_request(json.loads(data))
        except (ValueError, OSError, KeyError, TypeError):
            result = {'phase': 'controlFailed'}
        connection.sendall(json.dumps(result).encode('utf-8') + b'\n')


def web_update_check():
    with web_update_lock():
        state = web_update_read()
        if state.get('phase') != 'checking': return
        job = state['jobId']
    try:
        latest = latest_update_version()
        with tempfile.TemporaryDirectory(prefix='sp-backup-check-') as work:
            manifest = Path(work) / 'server-release.json'
            download(f'https://github.com/{REPOSITORY}/releases/download/server-v{latest}/server-release.json', manifest, 1024 * 1024)
            package_asset(json.loads(manifest.read_text()), latest)
        phase = 'available' if int(latest.rsplit('.', 1)[1]) > int(web_update_current().rsplit('.', 1)[1]) else 'upToDate'
    except Exception:
        phase = 'checkFailed'; latest = None
    with web_update_lock():
        current = web_update_read()
        if current.get('jobId') != job: return
        current.update(phase=phase, latestVersion=latest, checkedUtc=time.time(), finishedUtc=time.time())
        web_update_save(current)


def web_update_run():
    with web_update_lock():
        state = web_update_read()
        version = state.get('targetVersion')
        if state.get('phase') != 'queued' or not web_update_version(version): return
        state['phase'] = 'updating'; web_update_save(state)
        job = state['jobId']
    log_path = WEB_UPDATE_HOME / (job + '.log')
    phase = 'failed'; backup = None
    try:
        with log_path.open('x') as output:
            os.chmod(log_path, 0o600)
            result = subprocess.run([sys.executable, str(WEB_UPDATE_SCRIPT), '--inside-container', '--update', '--version', version],
                                    stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.STDOUT,
                                    env=dict(os.environ, PATH='/usr/sbin:/usr/bin:/sbin:/bin', LANG='C', LC_ALL='C'))
        if result.returncode == 0 and web_update_current() == version:
            phase = 'updated'
        # Paths are recognized only from our own updater output, never a web request.
        with log_path.open() as lines:
            for line in lines:
                match = re.search(r'/var/backups/onec-backup-server/[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}(?![0-9a-z/-])', line)
                if match: backup = match.group(0)
        if backup:
            saved = json.loads((Path(backup) / 'state.json').read_text())
            if saved.get('to') != version: backup = None
            elif saved.get('status') == 'rolled-back' and result.returncode != 0: phase = 'rolledBack'
            elif saved.get('status') == 'rollback-failed': phase = 'recoveryRequired'
    except Exception:
        phase = 'interrupted'
    with web_update_lock():
        state = web_update_read()
        if state.get('jobId') != job: return
        state.update(phase=phase, finishedUtc=time.time())
        if backup: state['backupDirectory'] = backup
        state['lastResult'] = {key: state[key] for key in ['phase', 'jobId', 'targetVersion', 'finishedUtc', 'backupDirectory'] if key in state}
        web_update_save(state)


def web_update_units():
    base = '# SP Backup web updater\n[Unit]\nDescription=SP Backup server update\n'
    service = '\n[Service]\nType=oneshot\nUser=root\nUMask=0077\nTimeoutStartSec=infinity\nEnvironment=LANG=C LC_ALL=C\n'
    command = 'ExecStart=/usr/bin/python3 ' + str(WEB_UPDATE_SCRIPT)
    return {
        'onec-backup-server-update.socket': base + '\n[Socket]\nListenStream=' + WEB_UPDATE_SOCKET + '\nAccept=yes\nSocketUser=root\nSocketGroup=' + ACCOUNT + '\nSocketMode=0660\n\n[Install]\nWantedBy=sockets.target\n',
        'onec-backup-server-update@.service': base + '\n[Service]\nUser=root\nUMask=0077\nStandardInput=socket\nStandardOutput=socket\nStandardError=journal\n' + command + ' --web-update-control\n',
        'onec-backup-server-update-check.service': base + service + command + ' --web-update-check\n',
        'onec-backup-server-update-run.service': base + service + command + ' --web-update-run\n',
    }


def enable_web_updates():
    # Reuse the complete read-only preflight (same version returns before making a kit).
    guest_update(web_update_current())
    for directory in [WEB_UPDATE_HOME, WEB_UPDATE_SCRIPT.parent]:
        if directory.is_symlink(): raise ValueError('Каталог обновлятора не должен быть ссылкой.')
        directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        if directory.stat().st_uid != 0 or directory.stat().st_mode & 0o077:
            raise ValueError('Каталог обновлятора должен принадлежать root с правами 0700.')
    with web_update_lock():
        if web_update_read().get('phase') in {*WEB_UPDATE_UNITS, 'interrupted'}:
            raise ValueError('Сначала завершите или проверьте текущую системную задачу обновления.')
        if Path(__file__).resolve() != WEB_UPDATE_SCRIPT:
            temporary = WEB_UPDATE_SCRIPT.with_suffix('.tmp')
            if temporary.exists() or temporary.is_symlink(): raise ValueError('Временный файл обновлятора уже существует.')
            shutil.copyfile(Path(__file__).resolve(), temporary); os.chmod(temporary, 0o700)
            os.replace(temporary, WEB_UPDATE_SCRIPT)
        units = web_update_units()
        for name, content in units.items():
            path = UNIT.parent / name
            if path.is_symlink() or path.with_name(name + '.d').exists(): raise ValueError('Нестандартная служба обновлятора.')
            if path.exists() and not path.read_text().startswith('# SP Backup web updater\n'):
                raise ValueError('Служба обновлятора уже настроена иначе.')
        for name, content in units.items():
            path = UNIT.parent / name
            path.write_text(content); os.chmod(path, 0o644)
        web_update_service(['daemon-reload'])
        web_update_service(['enable', '--now', 'onec-backup-server-update.socket'])
    print('Обновление из веб-панели подключено. Откройте Настройки → Обновление сервера.')


def host_update_step(label, args, *, capture=False):
    """Keep all pct descriptors off the console so lxc-attach does not allocate a PTY."""
    print(label + '…', flush=True)
    environment = dict(os.environ, LANG='C', LC_ALL='C')
    output = []
    # stderr must also be a pipe: lxc-attach checks all three standard FDs.
    with subprocess.Popen([str(a) for a in args], stdin=subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          env=environment, text=True, encoding='utf-8', errors='replace') as process:
        for line in process.stdout:
            if capture:
                output.append(line)
            else:
                print(line, end='', flush=True)
        code = process.wait()
    if code:
        raise RuntimeError(label + ': команда ' + str(args[0]) + ' завершилась с кодом ' + str(code) + '.')
    return ''.join(output).strip() if capture else None


def host_update(args):
    if (platform.machine() != 'x86_64' or any(not shutil.which(c) for c in ['pct', 'pvesh'])
            or args.id is None or not 100 <= args.id <= 999999999):
        raise ValueError('Запустите на узле Proxmox с --update --id <существующий CT>.')
    resources = json_command(['pvesh', 'get', '/cluster/resources', '--type', 'vm', '--output-format', 'json'])
    if not any(int(row['vmid']) == args.id and row.get('type') == 'lxc' for row in resources):
        raise ValueError('Указанный ID не принадлежит существующему LXC.')
    if host_update_step('Проверка состояния CT', ['pct', 'status', str(args.id)], capture=True) != 'status: running':
        raise ValueError('Сначала запустите существующий CT на этом узле.')
    # A unique private directory avoids replacing the original installation request/script.
    remote = '/root/sp-backup-update-' + secrets.token_hex(8)
    host_update_step('Подготовка каталога в CT', ['pct', 'exec', str(args.id), '--', 'install', '-d', '-m', '0700', remote])
    source = Path(__file__).resolve()
    host_update_step('Передача скрипта в CT', ['pct', 'push', str(args.id), str(source), remote + '/update.py', '--perms', '0600'])
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    observed = host_update_step('Проверка SHA-256 скрипта в CT',
                                ['pct', 'exec', str(args.id), '--', 'sha256sum', remote + '/update.py'], capture=True)
    if observed.split() != [digest, remote + '/update.py']:
        raise ValueError('SHA-256 переданного скрипта не совпадает; обновление не запущено.')
    command = ['pct', 'exec', str(args.id), '--', 'python3', '-u', remote + '/update.py', '--inside-container']
    command += ['--enable-web-updates'] if args.enable_web_updates else ['--update', '--version', args.version]
    if args.backup_directory:
        command += ['--backup-directory', str(args.backup_directory)]
    host_update_step('Запуск проверенного обновлятора в CT', command)


def host_create(args):
    if args.version is None:
        args.version = VERSION
    if platform.machine() != 'x86_64' or any(not shutil.which(c) for c in ('pct', 'pveam', 'pvesm', 'pvesh')):
        raise ValueError('Запустите скрипт от root в Shell хоста Proxmox VE amd64.')
    resources = json_command(['pvesh', 'get', '/cluster/resources', '--type', 'vm', '--output-format', 'json'])
    proposed = int(json_command(['pvesh', 'get', '/cluster/nextid', '--output-format', 'json']))
    pools = storage_names('rootdir')
    if not pools:
        raise ValueError('Нет активного хранилища Proxmox с поддержкой rootdir.')
    pool = args.rootfs_pool or ('local-lvm' if 'local-lvm' in pools else pools[0])
    ct_id = proposed if args.id is None else args.id
    name, port, ip, gateway = args.name, args.port, args.ip, args.gateway
    if not args.non_interactive:
        if not sys.stdin.isatty():
            raise ValueError('Для запуска без терминала укажите --non-interactive.')
        def prompt(label, default):
            return input(f'{label} [{default}]: ').strip() or default
        print('Новый сервер SP Backup. Существующие VM/CT и их диски не изменяются.')
        ct_id = int(prompt('Номер нового контейнера', str(ct_id)))
        name = prompt('Понятное имя системы мониторинга', name)
        port = int(prompt('HTTPS-порт панели', str(port)))
        pool = prompt('Хранилище системного диска (' + ', '.join(pools) + ')', pool)
        ip = prompt('IPv4/CIDR или dhcp (для DHCP закрепите адрес в маршрутизаторе)', ip)
        if ip != 'dhcp':
            gateway = prompt('IPv4-шлюз', gateway or '')
    if not 100 <= ct_id <= 999999999 or any(int(v['vmid']) == ct_id for v in resources):
        raise ValueError('Номер VM/CT уже занят в кластере либо находится вне диапазона 100–999999999.')
    if pool not in pools or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*', pool):
        raise ValueError('Выберите активное хранилище rootdir из списка.')
    if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_.-]{0,14}', args.bridge):
        raise ValueError('Некорректное имя сетевого моста.')
    run(['ip', 'link', 'show', 'dev', args.bridge], capture=True)
    if not re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', args.hostname):
        raise ValueError('Некорректное имя контейнера.')
    if not (1 <= args.cores <= 64 and 1024 <= args.memory <= 65536 and 8 <= args.disk <= 1024):
        raise ValueError('Ресурсы: 1–64 CPU, 1024–65536 МБ RAM, 8–1024 ГБ диска.')
    net = network(ip, gateway)
    for value in args.dns:
        ipaddress.IPv4Address(value)
    # Validate before downloading a template or creating any CT.
    validate_request({'version': args.version, 'address': args.address or f'https://192.0.2.1:{port}/', 'name': name, 'port': port})
    template = args.template
    if template:
        if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]*:vztmpl/debian-13-standard_[A-Za-z0-9_.+-]+_amd64\.tar\.(?:zst|gz|xz)', template):
            raise ValueError('Нужен локальный шаблон Debian 13 standard amd64 вида local:vztmpl/debian-13-standard_..._amd64.tar.zst.')
        if not Path(run(['pvesm', 'path', template], capture=True)).is_file():
            raise ValueError('Выбранный шаблон отсутствует в хранилище.')
    else:
        stores = storage_names('vztmpl')
        template_store = args.template_storage or ('local' if 'local' in stores else (stores[0] if stores else ''))
        if template_store not in stores:
            raise ValueError('Нет выбранного активного хранилища шаблонов vztmpl.')
        candidates = [row.split()[-1] for row in run(['pveam', 'available', '--section', 'system'], capture=True).splitlines()
                      if row.split() and re.fullmatch(r'debian-13-standard_[A-Za-z0-9_.+-]+_amd64\.tar\.(?:zst|gz|xz)', row.split()[-1])]
        if not candidates:
            raise ValueError('Debian 13 amd64 отсутствует в каталоге pveam. Обновите каталог: pveam update.')
        filename = max(candidates, key=lambda v: [int(p) if p.isdigit() else p for p in re.split(r'([0-9]+)', v)])
        template = template_store + ':vztmpl/' + filename
        path = run(['pvesm', 'path', template], capture=True)
        if not Path(path).is_file():
            run(['pveam', 'download', template_store, filename])
    command = ['pct', 'create', str(ct_id), template, '--hostname', args.hostname, '--unprivileged', '1', '--onboot', '1',
               '--cores', str(args.cores), '--memory', str(args.memory), '--swap', '512', '--rootfs', f'{pool}:{args.disk}',
               '--net0', f'name=eth0,bridge={args.bridge},{net}']
    if args.dns:
        command.extend(['--nameserver', ' '.join(args.dns)])
    run(command)
    print(f'Создан CT {ct_id}. При ошибке он сохранится; автоматического удаления нет.', flush=True)
    try:
        run(['pct', 'start', str(ct_id)])
        address = None
        for _ in range(90):
            try:
                interfaces = json_command(['pct', 'exec', str(ct_id), '--', 'ip', '-j', '-4', 'address', 'show', 'dev', 'eth0'])
                address = next((a['local'] for interface in interfaces for a in interface.get('addr_info', []) if a.get('scope') == 'global'), None)
                if address:
                    break
            except (RuntimeError, json.JSONDecodeError):
                pass
            time.sleep(1)
        if not address:
            raise RuntimeError('Контейнер не получил IPv4. Проверьте мост, DHCP или статический адрес.')
        request = validate_request({'version': args.version, 'address': args.address or f'https://{address}:{port}/', 'name': name, 'port': port})
        remote = '/root/sp-backup-setup'
        run(['pct', 'exec', str(ct_id), '--', 'install', '-d', '-m', '0700', remote])
        run(['pct', 'push', str(ct_id), str(Path(__file__).resolve()), remote + '/install.py', '--perms', '0600'])
        with tempfile.TemporaryDirectory(prefix='sp-backup-request-') as directory:
            path = Path(directory) / 'request.json'
            atomic_json(path, request)
            run(['pct', 'push', str(ct_id), path, remote + '/request.json', '--perms', '0600'])
        run(['pct', 'exec', str(ct_id), '--', 'sh', '-c', BOOTSTRAP])
        run(['pct', 'exec', str(ct_id), '--', 'python3', remote + '/install.py', '--inside-container', '--request', remote + '/request.json'])
        print(f'\nПанель: {request["address"]}\nЛогин: admin\nПолучить пароль: pct exec {ct_id} -- cat /etc/onec-backup-server/administrator-password.txt')
        print(f'Скопировать публичный сертификат: pct pull {ct_id} /etc/onec-backup-server/ca.crt ./SP-Backup-CA.crt')
        print('Установите этот сертификат на клиентские ПК в доверенные корневые центры сертификации. Закрытый ключ не копируйте.')
        print(f'Управление: pct enter {ct_id}; systemctl status onec-backup-server. PostgreSQL доступна только внутри CT.')
    except Exception:
        print(f'CT {ct_id} сохранён. Диагностика: pct enter {ct_id}. Если запрос уже скопирован, повторите:\n'
              f"pct exec {ct_id} -- sh -c '{BOOTSTRAP} && python3 /root/sp-backup-setup/install.py --inside-container --request /root/sp-backup-setup/request.json'", file=sys.stderr)
        raise


def parser():
    p = argparse.ArgumentParser(description='Установка SP Backup Server в новый непривилегированный Debian 13 LXC на Proxmox.')
    p.add_argument('--id', type=int, help='свободный VM/CT ID; по умолчанию следующий свободный в кластере')
    p.add_argument('--name', default='SP Backup', help='понятное имя системы мониторинга')
    p.add_argument('--hostname', default='sp-backup', help='сетевое имя контейнера')
    p.add_argument('--address', help='полный HTTPS-адрес панели с выбранным портом; по умолчанию IPv4 контейнера')
    p.add_argument('--port', type=int, default=7443)
    p.add_argument('--version', help='версия сервера; установка по умолчанию '+VERSION+', обновление — последний опубликованный preview')
    mode = p.add_mutually_exclusive_group()
    mode.add_argument('--update', action='store_true', help='обновить существующий LXC из мастера, без создания CT')
    mode.add_argument('--enable-web-updates', action='store_true', help='один раз подключить обновление из веб-панели штатного LXC')
    mode.add_argument('--web-update-control', action='store_true', help=argparse.SUPPRESS)
    mode.add_argument('--web-update-check', action='store_true', help=argparse.SUPPRESS)
    mode.add_argument('--web-update-run', action='store_true', help=argparse.SUPPRESS)
    p.add_argument('--backup-directory', type=Path, help='каталог комплектов возврата внутри CT')
    p.add_argument('--template', help='локальный Debian 13 amd64 шаблон; иначе скачивается через pveam')
    p.add_argument('--template-storage', help='хранилище шаблонов; по умолчанию local либо первое доступное')
    p.add_argument('--rootfs-pool', help='хранилище диска; по умолчанию local-lvm либо первое доступное')
    p.add_argument('--bridge', default='vmbr0')
    p.add_argument('--ip', default='dhcp', help='dhcp либо статический IPv4/CIDR')
    p.add_argument('--gateway', help='шлюз для статического IPv4')
    p.add_argument('--dns', action='append', default=[], help='IPv4 DNS, можно повторить')
    p.add_argument('--cores', type=int, default=2)
    p.add_argument('--memory', type=int, default=2048, help='RAM в МБ')
    p.add_argument('--disk', type=int, default=16, help='системный диск в ГБ')
    p.add_argument('--non-interactive', action='store_true', help='использовать параметры без диалога')
    p.add_argument('--inside-container', action='store_true', help=argparse.SUPPRESS)
    p.add_argument('--request', type=Path, help=argparse.SUPPRESS)
    p.add_argument('--package-directory', type=Path, help=argparse.SUPPRESS)
    return p


def main():
    args = parser().parse_args()
    if getattr(os, 'geteuid', lambda: -1)() != 0:
        raise ValueError('Запустите от root в Shell Proxmox.')
    if args.web_update_control or args.web_update_check or args.web_update_run:
        if args.web_update_control: web_update_control()
        elif args.web_update_check: web_update_check()
        else: web_update_run()
        return
    if (args.update or args.enable_web_updates) and not args.inside_container and args.id is None:
        raise ValueError('Для обновления укажите --id существующего CT.')
    if args.version is None:
        args.version = latest_update_version() if args.update else VERSION
    if args.inside_container:
        import fcntl
        if not args.update and not args.enable_web_updates and (args.request is None or args.request.is_symlink() or args.request.stat().st_size > 16384):
            raise ValueError('Нужен локальный файл запроса установки.')
        Path('/run/lock').mkdir(mode=0o755, exist_ok=True)
        descriptor = os.open('/run/lock/sp-backup-server-install.lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, 'a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            if args.enable_web_updates:
                enable_web_updates()
            elif args.update:
                guest_update(args.version, args.package_directory, args.backup_directory)
            else:
                guest_install(json.loads(args.request.read_text()), args.package_directory)
    elif args.update or args.enable_web_updates:
        host_update(args)
    else:
        host_create(args)


if __name__ == '__main__':
    try:
        main()
    except (Exception, KeyboardInterrupt) as error:
        # Never include SQL input, generated passwords, file contents or raw HTTP response bodies.
        detail = str(error) if isinstance(error, (ValueError, RuntimeError)) else type(error).__name__
        if isinstance(error, FileNotFoundError) and error.filename:
            detail += ': ' + str(error.filename)
        if isinstance(error, urllib.error.HTTPError):
            detail += ': HTTP ' + str(error.code)
        print('Установка остановлена: ' + detail, file=sys.stderr)
        sys.exit(1)
