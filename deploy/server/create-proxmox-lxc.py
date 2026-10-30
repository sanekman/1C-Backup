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
VERSION = '0.1.0-preview.5'
CONFIG = Path('/etc/onec-backup-server')
APPLICATION = Path('/opt/onec-backup-server')
DATA = Path('/var/lib/onec-backup-server')
UNIT = Path('/etc/systemd/system/onec-backup-server.service')
ACCOUNT = 'onec-backup-server'
DATABASE = 'onecbackup'
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


def install_package(request, package_directory):
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
    if not sql("SELECT 1 FROM pg_database WHERE datname='onecbackup';"):
        sql('CREATE DATABASE onecbackup OWNER onecbackup;')
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


def host_create(args):
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
    p.add_argument('--version', default=VERSION)
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
    if args.inside_container:
        import fcntl
        if args.request is None or args.request.is_symlink() or args.request.stat().st_size > 16384:
            raise ValueError('Нужен локальный файл запроса установки.')
        Path('/run/lock').mkdir(mode=0o755, exist_ok=True)
        descriptor = os.open('/run/lock/sp-backup-server-install.lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, 'a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            guest_install(json.loads(args.request.read_text()), args.package_directory)
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
