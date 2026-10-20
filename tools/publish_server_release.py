"""Publish a new immutable server prerelease after both native installer/package checks passed."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = 'sanekman/1C-Backup'
BASE = 'https://api.github.com/repos/' + REPOSITORY + '/'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, required=True)
    args = parser.parse_args()
    tag = os.environ['GITHUB_REF_NAME']
    source = os.environ['GITHUB_SHA']
    if not re.fullmatch(r'server-v\d+\.\d+\.\d+-preview\.\d+', tag):
        raise RuntimeError('An explicit new server preview tag is required.')
    version = tag.removeprefix('server-v')
    secret = os.environ['GITHUB_TOKEN']
    headers = {'Authorization': 'Bearer ' + secret, 'Accept': 'application/vnd.github+json', 'Content-Type': 'application/json'}

    def api(path, body=None, method=None):
        request = urllib.request.Request(BASE + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
        with urllib.request.urlopen(request, timeout=120) as response:
            return json.load(response)

    def require_absent(path):
        try:
            api(path)
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return
            raise
        raise FileExistsError('Refusing to overwrite existing GitHub object: ' + path)

    reference = api('git/ref/tags/' + tag)
    if reference['object']['sha'] != source:
        raise RuntimeError('Tag does not point to the tested source commit.')
    require_absent('releases/tags/' + tag)
    branch = 'distribution/server-' + version
    require_absent('git/ref/heads/' + urllib.parse.quote(branch, safe=''))
    expected = [f'1C-Backup-Server-{version}-windows-x64-setup.exe', '1C-Backup-server-win-x64.zip', '1C-Backup-server-linux-x64.tar.gz']
    files = []
    assets = []
    for name in expected:
        matches = list(args.artifacts.rglob(name))
        if len(matches) != 1:
            raise RuntimeError('Expected exactly one tested distribution: ' + name)
        path = matches[0]
        with path.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        checksum = path.with_name(path.name + '.sha256')
        if checksum.read_text().split() != [digest, name]:
            raise RuntimeError('Distribution checksum mismatch: ' + name)
        files.extend([path, checksum])
        assets.append({'name': name, 'bytes': path.stat().st_size, 'sha256': digest,
                       'url': f'https://github.com/{REPOSITORY}/releases/download/{tag}/{name}'})
    # Upload publication is allowed only with the actual Windows install/reinstall verification artifact.
    results = list(args.artifacts.rglob('windows-installer-result.json'))
    if len(results) != 1 or not all(json.loads(results[0].read_text()).values()):
        raise RuntimeError('Successful native Windows installation verification is required.')
    manifest = {'version': version, 'sourceCommit': source, 'runtime': '10.0.13', 'minimumMonitoringAgent': '0.2.0-alpha.20', 'assets': assets}
    content = json.dumps(manifest, ensure_ascii=False, indent=2) + '\n'
    manifest_path = args.artifacts / 'server-release.json'
    manifest_path.write_text(content)
    files.append(manifest_path)
    # Each distribution branch is created once, containing the commit/checksum manifest.
    # Large offline installers stay in release assets (not Git blobs).
    tree = api('git/trees', {'tree': [{'path': 'server-release.json', 'mode': '100644', 'type': 'blob', 'content': content}]})
    commit = api('git/commits', {'message': 'Immutable server distribution ' + version, 'tree': tree['sha'], 'parents': []})
    notes = ('Windows: скачайте setup.exe, укажите имя сервера и пароль администратора; PostgreSQL 17, .NET 10.0.13, HTTPS и службы Windows входят в пакет. '
             'В панели: связь с агентами, задания, прогресс, история, неполные копии, результаты хранилищ и внешние SQL-копии. '
             'Для подключения требуется клиент 0.2.0-alpha.20 или новее. Локальное расписание сохраняется. '
             'Проверены установка с нуля, вход, переустановка, перезапуск служб и сохранение БД при удалении. '
             'Управление заданиями с сервера остаётся следующим этапом.\n\n'
             'Инструкция: https://github.com/' + REPOSITORY + '/blob/' + source + '/docs/server-start.md\n\n'
             'Исходный коммит: `' + source + '`. Контрольные суммы находятся рядом с файлами и в server-release.json.')
    release = api('releases', {'tag_name': tag, 'target_commitish': source, 'name': '1C-Backup Server ' + version,
                              'body': notes, 'draft': True, 'prerelease': True, 'make_latest': 'false'})
    upload_base = release['upload_url'].split('{')[0]
    for path in files:
        url = upload_base + '?name=' + urllib.parse.quote(path.name)
        request = urllib.request.Request(url, data=path.read_bytes(), headers={**headers, 'Content-Type': 'application/octet-stream'}, method='POST')
        with urllib.request.urlopen(request, timeout=300) as response:
            uploaded = json.load(response)
        if uploaded['size'] != path.stat().st_size:
            raise RuntimeError('Uploaded asset size mismatch.')
    api('git/refs', {'ref': 'refs/heads/' + branch, 'sha': commit['sha']})
    result = api('releases/' + str(release['id']), {'draft': False}, method='PATCH')
    print('Published new immutable server release:', result['html_url'])


if __name__ == '__main__':
    main()
