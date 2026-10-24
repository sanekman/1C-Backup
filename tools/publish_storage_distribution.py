"""Publish a verified Linux package to a new immutable Git branch, without changing the checkout."""
import argparse,hashlib,json,re,subprocess,tarfile,tempfile,os
from pathlib import Path

def main():
 repository=Path(__file__).resolve().parents[1]
 parser=argparse.ArgumentParser();parser.add_argument('--version',default=(repository/'deploy/storage/VERSION').read_text().strip());args=parser.parse_args()
 if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+',args.version):raise ValueError('Invalid version')
 artifacts=repository/'artifacts'
 archive=artifacts/('OneCBackup-Storage-'+args.version+'-linux-x64.tar.gz');checksum=archive.with_suffix('.gz.sha256')
 with archive.open('rb') as file:digest=hashlib.file_digest(file,'sha256').hexdigest()
 if digest!=checksum.read_text().split()[0] or archive.stat().st_size>95*1024*1024:raise ValueError('Package checksum/size mismatch')
 with tarfile.open(archive) as file:
  members=file.getmembers()
  if any(not member.name.startswith('OneCBackup-Storage/') or '..' in member.name.split('/') or not (member.isfile() or member.isdir()) for member in members):raise ValueError('Invalid archive')
  if file.extractfile('OneCBackup-Storage/VERSION.txt').read().decode().strip()!=args.version:raise ValueError('Wrong package version')
  runtime=json.load(file.extractfile('OneCBackup-Storage/OneCBackup.StorageBridge.runtimeconfig.json'))
  frameworks={item['name']:item['version'] for item in runtime['runtimeOptions']['includedFrameworks']}
  if frameworks!={'Microsoft.NETCore.App':'10.0.13','Microsoft.AspNetCore.App':'10.0.13'}:raise ValueError('Unexpected package runtime')
 branch='distribution/storage-linux-x64-'+args.version
 def git(*command,environment=None,input=None):return subprocess.check_output(['git',*command],cwd=repository,env=environment,input=input).decode().strip()
 if git('ls-remote','--heads','origin','refs/heads/'+branch):raise RuntimeError('Distribution branch already exists; no overwrite permitted')
 source=git('rev-parse','HEAD')
 report={'version':args.version,'sha256':digest,'bytes':archive.stat().st_size,'source_revision':source,'platform':'linux-x64','component':'storage-gateway','dotnet_runtime':'10.0.13','sdk':'10.0.402'}
 metadata=artifacts/'storage-manifest.json';metadata.write_text(json.dumps(report,indent=2)+'\n')
 files=[archive,checksum,metadata,repository/'docs/storage-bridge.md']
 with tempfile.TemporaryDirectory(prefix='storage-index-') as directory:
  environment=dict(os.environ,GIT_INDEX_FILE=str(Path(directory)/'index'))
  git('read-tree','--empty',environment=environment)
  for path in files:
   blob=git('hash-object','-w',str(path));git('update-index','--add','--cacheinfo','100644,'+blob+',storage/'+path.name,environment=environment)
  tree=git('write-tree',environment=environment)
  commit=git('commit-tree',tree,input=('Storage gateway '+args.version+'\n').encode())
  git('push','origin',commit+':refs/heads/'+branch)
 root='https://github.com/sanekman/1C-Backup/blob/'+branch+'/storage/'
 notes='''# Шлюз хранилища 1C-Backup '''+args.version+'''

SDK 10.0.402; самодостаточный пакет и Docker-образ используют .NET / ASP.NET Core 10.0.13. Предыдущие теги и дистрибутивы сохраняются; версия образа совпадает с версией выпуска.

Агент-посредник для диска/NAS в локальной сети за NAT: Debian 12 LXC Proxmox или Docker внутри Linux VM. Исходящее WSS, отдельный взаимный TLS, проверка размера/SHA-256, квота, скачивание и безопасное удаление. Публичный узел может работать в Windows-программе alpha.13 или отдельным контейнером; HTTPS публикуется через Caddy, IIS, Apache. Включены инструкции Let’s Encrypt и установки из GitHub.

'''
 notes+='[Linux x64 пакет]('+root+archive.name+'?raw=true) · [SHA-256]('+root+checksum.name+'?raw=true)\n\n'
 notes+='[Установка в Proxmox и настройка HTTPS](https://github.com/sanekman/1C-Backup/blob/storage-v'+args.version+'/docs/storage-bridge.md)\n\n'
 notes+='Проверены реальные контейнеры, HTTPS-прокси с доверенным тестовым сертификатом, взаимный TLS, загрузка/скачивание/удаление и сохранность после перезапуска. Установщик проверен в Debian 12; настоящий Proxmox/systemd и IIS не запускались, реальный выпуск Let’s Encrypt не выполнялся. Продолжение прерванной передачи по блокам пока отсутствует. Образ Docker можно собрать из исходного тега; публикация GHCR зависит от успешного CI.\n'
 (artifacts/'storage-release-notes.md').write_text(notes)
 print(json.dumps({'branch':branch,'commit':commit,**report}))

if __name__=='__main__':main()
