"""Build and package the standalone Windows x64 desktop application."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import zipfile


INSTRUCTIONS = """1C-Backup — Windows x64

ЗАПУСК
1. Распакуйте весь архив в отдельную папку, например C:\\Apps\\1C-Backup.
2. Запустите OneCBackup.Desktop.exe из распакованной папки.
Не запускайте программу непосредственно из ZIP и не переносите один EXE
отдельно от остальных файлов. Установка .NET не требуется.

Поддерживаемая система: Windows 10 22H2 или Windows 11, 64-разрядная x64.
Права администратора для самого приложения не требуются. Учётной записи
нужен доступ к базам, каталогам копий и используемым SQL-серверам.

ПЕРВОЕ ЗАДАНИЕ
Откройте «Задания», нажмите «+ Новое задание», заполните настройки справа
и нажмите «Сохранить». Выберите задание в списке и нажмите «Создать копию».
Для расписания в открытом окне нажмите «Запустить» на панели планировщика.
Для работы после закрытия окна нажмите «Включить фон». Отдельный агент
работает от текущего пользователя; окно можно закрыть. Настройка запуска
при входе в Windows находится в разделе «Настройки агента».
Службы Windows и работы без входа пользователя в этой версии нет.
Фоновый агент поддерживает автономный режим и автоматически опрашивает
включённые источники внешних SQL-копий с заданными интервалами.

Для ZIP файла 1Cv8.1CD платформа 1С не нужна, все сеансы базы должны быть
закрыты. Для выгрузки .dt требуется установленная платформа 1С.
Для PostgreSQL нужны отдельные pg_dump и pg_restore совместимых версий.
SQL Server использует Windows-аутентификацию и проверку сертификата TLS.

НАСТРОЙКИ И ДАННЫЕ
Настройки, история и журнал копий: %LOCALAPPDATA%\\OneCBackup.
Логин и пароль FTP: диспетчер учётных данных текущего пользователя Windows.
Удаление папки программы не удаляет эти данные или резервные копии.

Это начальная версия для проверки. Связь с сервером управления в интерфейсе
ещё не включена. Архив не содержит платформу 1С, СУБД или их утилиты.
Дистрибутив не подписан цифровой подписью. Работа интерфейса и реальное
копирование баз требуют проверки на Windows.
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet", help="Path to the .NET 10 SDK command")
    parser.add_argument("--skip-publish", action="store_true", help="Package an existing artifacts/windows-x64 publish directory")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    artifacts = root / "artifacts"
    artifacts.mkdir(exist_ok=True)
    publish = artifacts / "windows-x64"
    if not args.skip_publish:
        # Publish into a fresh directory so removed dependencies cannot enter a release.
        with tempfile.TemporaryDirectory(prefix="windows-publish-", dir=artifacts) as stage:
            for project in ["OneCBackup.Desktop", "OneCBackup.Agent"]:
                subprocess.run([
                    args.dotnet, "publish", str(root / f"src/{project}/{project}.csproj"),
                    "--configuration", "Release", "--runtime", "win-x64", "--self-contained", "true",
                    "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "--output", stage,
                    "-m:1", "-nr:false",
                ], cwd=root, check=True)
            package(Path(stage), artifacts)
    else:
        package(publish, artifacts)


def package(publish, artifacts):
    required = ["OneCBackup.Desktop.exe", "OneCBackup.Desktop.dll", "OneCBackup.Core.dll",
                "OneCBackup.Agent.exe", "OneCBackup.Agent.dll", "OneCBackup.Agent.runtimeconfig.json", "OneCBackup.Agent.deps.json",
                "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll",
                "PresentationFramework.dll", "PresentationCore.dll", "wpfgfx_cor3.dll"]
    for name in required:
        file = publish / name
        if not file.is_file() or file.stat().st_size == 0:
            raise RuntimeError(f"Missing distribution component: {name}")
    with (publish / "OneCBackup.Desktop.exe").open("rb") as executable:
        if executable.read(2) != b"MZ":
            raise RuntimeError("Application host is not a Windows executable")
    config = json.loads((publish / "OneCBackup.Desktop.runtimeconfig.json").read_text())
    frameworks = {framework["name"] for framework in config["runtimeOptions"].get("includedFrameworks", [])}
    if not {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}.issubset(frameworks):
        raise RuntimeError("The distribution does not include the .NET and Windows Desktop runtimes")
    files = [file for file in publish.rglob("*") if file.is_file() and file.suffix != ".pdb"]
    destination = artifacts / "1C-Backup-windows-x64.zip"
    handle, temporary_name = tempfile.mkstemp(suffix=".zip.tmp", dir=artifacts)
    os.close(handle)
    temporary = Path(temporary_name)
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for file in sorted(files):
                archive.write(file, Path("1C-Backup") / file.relative_to(publish))
            archive.writestr("1C-Backup/START-HERE.txt", INSTRUCTIONS.replace("\n", "\r\n").encode("utf-8-sig"))
        with zipfile.ZipFile(temporary) as archive:
            corrupt = archive.testzip()
            if corrupt:
                raise RuntimeError(f"Corrupt archive member: {corrupt}")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)
    with destination.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    destination.with_suffix(".zip.sha256").write_text(f"{digest}  {destination.name}\n", encoding="ascii")
    print(f"Distribution: {destination}")
    print(f"Size: {destination.stat().st_size / (1024 * 1024):.1f} MiB; files: {len(files) + 1}")
    print(f"SHA256: {digest}")


if __name__ == "__main__":
    main()
