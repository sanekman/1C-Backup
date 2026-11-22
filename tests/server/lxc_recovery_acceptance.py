"""Update and roll back only an isolated disposable clone of a Proxmox container."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import uuid

VERSION = '0.1.0-preview.900001'
SCHEMA = 8
TABLES = {
    'server_users': 'id', 'server_agents': 'id', 'server_agent_folders': 'id',
    'server_configurations': 'agent_id', 'server_runs': 'agent_id,run_id',
}


def guest(directory):
    updater = directory / 'create-proxmox-lxc.py'
    spec = importlib.util.spec_from_file_location('clone_updater', updater)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if subprocess.check_output(['systemd-detect-virt', '--container'], text=True).strip() != 'lxc':
        raise ValueError('Нужен отдельный тестовый LXC.')
    subprocess.run(['sha256sum', '--check', 'SHA256SUMS'], cwd=directory, check=True)
    target = json.loads((directory / 'server-release.json').read_text())
    module.package_asset(target, VERSION)
    candidate_schema = target.get('schema', SCHEMA)
    if type(candidate_schema) is not int or not 1 <= candidate_schema <= 100:
        raise ValueError('Некорректная схема тестового пакета.')
    initial = (module.APPLICATION / 'current').resolve().name
    if not re.fullmatch(r'0\.1\.0-preview\.[1-9][0-9]*', initial) or int(initial.rsplit('.', 1)[1]) >= 900001:
        raise ValueError('Исходная версия не подходит для тестового обновления.')
    config = json.loads((module.CONFIG / 'server.json').read_text())['Server']
    host, port, database = module.update_database_target(config['ConnectionString'])
    module.verify_update_database(host, port, database)
    layout = module.validate_request(json.loads((module.CONFIG / 'installation.json').read_text())['request'])
    https_port = layout['port']
    root = module.UPDATE_BACKUPS
    root.mkdir(mode=0o700, parents=True, exist_ok=True)
    marker = 'sp_recovery_fixture_' + uuid.uuid4().hex

    def sql(query):
        return subprocess.check_output(['runuser', '-u', 'postgres', '--', 'psql', '-X', '-qAt',
                                        '-v', 'ON_ERROR_STOP=1', '-d', database, '-c', query], text=True).strip()

    def rows(*, migration=False):
        result = {}
        for table, order in TABLES.items():
            data = "(to_jsonb(t) - 'role' - 'enabled')" if migration and table == 'server_users' else 'to_jsonb(t)'
            result[table] = sql(f"SELECT count(*) || ':' || md5(coalesce(string_agg({data}::text,'|' ORDER BY {order}),'')) FROM {table} t")
        return result

    def health():
        subprocess.run(['systemctl', 'is-active', '--quiet', 'onec-backup-server'], check=True)
        module.wait_ready(https_port)

    def active():
        return (module.APPLICATION / 'current').resolve().name

    def certificate():
        return hashlib.sha256((module.CONFIG / 'server.pfx').read_bytes()).hexdigest()

    def new_backup(before, source, destination, status):
        matches = []
        for item in set(root.iterdir()) - before:
            if not module.BACKUP_NAME.fullmatch(item.name):
                continue
            if status == 'updated':
                state = module.update_backup_state(item, verify_hashes=True)
            else:
                state = json.loads((item / 'state.json').read_text())
                if state.get('format') != 1 or set(state.get('files', {})) != set(module.BACKUP_FILES):
                    raise RuntimeError('Неполное описание комплекта перед возвратом.')
                for name, digest in state['files'].items():
                    path = item / name
                    if path.is_symlink() or path.stat().st_uid != 0 or path.stat().st_mode & 0o022:
                        raise RuntimeError('Неверные права файла комплекта перед возвратом.')
                    with path.open('rb') as stream:
                        if hashlib.file_digest(stream, 'sha256').hexdigest() != digest:
                            raise RuntimeError('Файл комплекта перед возвратом повреждён.')
            if (state['from'], state['to'], state['status']) == (source, destination, status):
                matches.append(item)
        if len(matches) != 1:
            raise RuntimeError('Не найден единственный проверенный комплект текущей операции.')
        return matches[0]

    def update(*args):
        subprocess.run([sys.executable, str(updater), '--inside-container', '--update', *args], check=True)

    health()
    original = rows()
    comparable = rows(migration=True)
    schema = int(sql('SELECT max(version) FROM server_schema'))
    fingerprint = certificate()
    before = set(root.iterdir())
    update('--version', VERSION, '--package-directory', str(directory))
    saved = new_backup(before, initial, VERSION, 'updated')
    marked = False
    before_rollback = set(root.iterdir())
    try:
        health()
        if active() != VERSION or int(sql('SELECT max(version) FROM server_schema')) != candidate_schema:
            raise RuntimeError('Версия и схема после обновления отличаются от ожидаемых.')
        if rows(migration=True) != comparable or certificate() != fingerprint:
            raise RuntimeError('Содержимое БД или HTTPS-сертификат изменились при обновлении.')
        sql(f"CREATE TABLE {marker}(value text); INSERT INTO {marker} VALUES ('after-update');")
        marked = True
    finally:
        print(f'Проверенный комплект для возврата клона: {saved}', flush=True)
        update('--version', initial, '--rollback-from', str(saved))
    preserved = new_backup(before_rollback, VERSION, initial, 'manually-restored')
    health()
    if active() != initial or int(sql('SELECT max(version) FROM server_schema')) != schema:
        raise RuntimeError('После возврата версия или схема не совпала с исходной.')
    if rows() != original or certificate() != fingerprint or sql(f"SELECT to_regclass('public.{marker}') IS NULL") != 't':
        raise RuntimeError('После возврата БД, маркер или сертификат не соответствуют исходному состоянию.')
    catalog = subprocess.check_output(['pg_restore', '-l', str(preserved / 'database.dump')], text=True)
    if not marked or marker not in catalog:
        raise RuntimeError('Комплект перед возвратом не сохранил тестовое изменение.')
    print(json.dumps(dict(success=True, initialVersion=initial, updatedVersion=VERSION, restoredVersion=active(),
                          startingSchema=schema, updatedSchema=candidate_schema, restoredSchema=schema, realSystemd=True,
                          certificatePreserved=True, rowsPreserved=True, testMarkerRemoved=True,
                          preRollbackStatePreserved=True, updateBackup=saved.name, rollbackBackup=preserved.name),
                     ensure_ascii=False, indent=2), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--clone', required=True, type=int)
    parser.add_argument('--inside-test-lxc', action='store_true', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.clone < 100 or args.clone == 107:
        parser.error('Укажите ID тестового клона. CT 107 защищён от запуска.')
    directory = Path(__file__).resolve().parent
    if args.inside_test_lxc:
        guest(directory)
        return
    subprocess.run(['sha256sum', '--check', 'SHA256SUMS'], cwd=directory, check=True)
    identity = str(args.clone)
    status = subprocess.check_output(['pct', 'status', identity], text=True).strip()
    if status != 'status: running':
        raise ValueError('Сначала запустите изолированный тестовый клон.')
    location = '/root/sp-backup-recovery-fixture-' + uuid.uuid4().hex
    command = ['pct', 'exec', identity, '--']
    subprocess.run([*command, 'install', '-d', '-m', '0700', location], check=True)
    for name in ['SHA256SUMS', 'server-release.json', 'SP-Backup-server-linux-x64.tar.gz',
                 'create-proxmox-lxc.py', Path(__file__).name]:
        subprocess.run(['pct', 'push', identity, str(directory / name), f'{location}/{name}', '--perms', '0600'], check=True)
    subprocess.run([*command, 'python3', '-u', f'{location}/{Path(__file__).name}', '--clone', identity, '--inside-test-lxc'], check=True)


if __name__ == '__main__':
    main()
