"""Build and package the standalone Windows x64 desktop application."""

import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import tempfile
import zipfile


INSTRUCTIONS = """SP Backup — Windows x64

НАЧНИТЕ ЗДЕСЬ
Откройте Help/user-guide.html двойным щелчком. Это подробное руководство:
установка, первая копия с примером, восстановление, расписание и служба.
Интернет для него не нужен. В программе: Настройки → Помощь → Пошаговая инструкция.
Сервер мониторинга для первой копии не нужен.

1. УСТАНОВКА
Скачайте из выпуска установщик 1C-Backup-windows-x64-setup.exe.
Запустите его и разрешите установку. Затем откройте SP Backup через «Пуск»
под своей обычной учётной записью Windows: у каждого пользователя свой профиль.
Устанавливать .NET отдельно не нужно.
Для переносного варианта распакуйте весь ZIP в отдельную папку и запустите
SPBackup.Desktop.exe. Не запускайте EXE внутри ZIP и не переносите его отдельно.
Поддерживаемая система: Windows 10 22H2 или Windows 11, x64.
Дистрибутив не подписан цифровой подписью.

2. ПЕРВАЯ КОПИЯ И ВОССТАНОВЛЕНИЕ
На пустом экране нажмите «Первая копия: инструкция с примером».
Начните с небольшой тестовой папки, выберите отдельную папку для копий
и ручной запуск. Сохраните задание и запустите копирование.
Дождитесь результата в истории. Проверьте копию и восстановите файл
в другую пустую папку по разделу 3 руководства. Откройте восстановленный файл.
Только после этой проверки настройте копирование рабочих данных.
Для инкрементного восстановления сохраняйте полную основу и всю цепочку.
Предупреждение о неполной копии означает, что часть данных могла не попасть в архив.
Посмотрите результат и список пропусков; не удаляйте прежние архивы вручную.

3. РАСПИСАНИЕ И РАБОТА БЕЗ ОКНА
В задании выберите расписание и проверьте показанный следующий запуск.
Включите автоматическое копирование. Компьютер должен быть включён и не спать.
Настройки → Фоновая работа: автозапуск при входе или служба Windows.
Для работы до входа пользователя установите службу по разделу 5 руководства.
Потребуются права администратора; службе нужен доступ к источникам и хранилищам.
Для сетевых папок используйте UNC-путь и проверьте доступ учётной записи службы.
Ручной запуск доступен и при включённом расписании, в том числе у заданий без него.

4. БАЗЫ ДАННЫХ И ПАРОЛИ
Для файловой 1С прочитайте раздел 7 руководства, для MS SQL и PostgreSQL — раздел 9.
MS SQL поддерживает учётную запись Windows или отдельный SQL-логин.
Для PostgreSQL можно найти утилиты на компьютере и задать пароль в программе.
Прежние настройки PostgreSQL продолжают работать. Сохранение пароля само по себе
не проверяет подключение: после сохранения задания нажмите «Проверить источник».
Пароли хранятся отдельно от JSON в защищённом хранилище Windows.
Для службы используется защищённый DPAPI-кэш; при переносе на другой компьютер
пароли нужно задать заново. Проверка источника не заменяет пробную копию и восстановление.

5. ОБНОВЛЕНИЕ И ПОМОЩЬ
Перед обновлением завершите приложение и агент через «Выйти полностью»
или остановите службу. Дождитесь окончания текущего копирования.
Запустите новый установщик. Для ZIP заменяйте весь комплект файлов.
После обновления запустите агент/службу, проверьте следующий запуск
и сделайте пробную копию. Задания, история, пароли Windows и архивы сохраняются;
не удаляйте профиль приложения и папки копий.
Настройки → Обновления: проверка и скачивание новой версии.
Настройки → Помощь: версия, пошаговая инструкция и «Сохранить диагностику…».
При проблеме сохраните ZIP диагностики и укажите время и ожидаемый результат.
Перед удалением программы сначала удалите установленную службу в настройках.

СЕРВЕР МОНИТОРИНГА
Устанавливается отдельно. Показывает состояние агентов, прогресс и историю.
В клиенте: Настройки → Подключения → Мониторинг на сервере Windows.
Используйте приглашение из панели сервера: HTTPS-адрес, одноразовый код
и SHA-256 сертификата. Режим Managed для мониторинга не требуется.
Сервер мониторинга и хранилище архивов — разные подключения.
Управляемые сервером задания ещё разрабатываются.
Оповещения: Настройки → Подключения; подробности в Help/notifications.txt.
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet", help="Path to the .NET 10 SDK command")
    parser.add_argument("--skip-publish", action="store_true", help="Package an existing artifacts/windows-x64 publish directory")
    args = parser.parse_args()
    from windows_signing import configured
    configured()  # Fail before publishing when release signing is required.
    root = Path(__file__).resolve().parents[1]
    subprocess.run([sys.executable, str(root / "tools/render_user_guide.py"), "--check"], check=True)
    artifacts = root / "artifacts"
    artifacts.mkdir(exist_ok=True)
    publish = artifacts / "windows-x64"
    if not args.skip_publish:
        # Publish into a fresh directory so removed dependencies cannot enter a release.
        with tempfile.TemporaryDirectory(prefix="windows-publish-", dir=artifacts) as stage:
            for project in ["SPBackup.Desktop", "SPBackup.Agent"]:
                subprocess.run([
                    args.dotnet, "publish", str(root / f"src/{project}/{project}.csproj"),
                    "--configuration", "Release", "--runtime", "win-x64", "--self-contained", "true",
                    "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "--output", stage,
                    "-m:1", "-nr:false",
                ], cwd=root, check=True)
            package(Path(stage), artifacts)
    else:
        package(publish, artifacts)


def distribution_version(publish):
    versions = []
    for project in ["SPBackup.Desktop", "SPBackup.Agent"]:
        dependencies = json.loads((publish / f"{project}.deps.json").read_text())
        versions.append(next(key.split("/", 1)[1] for key in dependencies["libraries"] if key.startswith(project + "/")))
    if versions[0] != versions[1]:
        raise RuntimeError("Desktop and agent versions differ")
    if os.environ.get("GITHUB_REF_TYPE") == "tag" and versions[0] != os.environ["GITHUB_REF_NAME"][1:]:
        raise RuntimeError("The distribution version does not match the release tag")
    return versions[0]


def verify_pkcs_asset(publish, package_folders=None):
    """Verify the selected DLL against its patched NuGet package or Microsoft runtime pack."""
    package = "System.Security.Cryptography.Pkcs"
    asset = package + ".dll"
    owners = []
    for project in ["SPBackup.Desktop", "SPBackup.Agent"]:
        deps = json.loads((publish / (project + ".deps.json")).read_text())
        names = {name for target in deps.get("targets", {}).values() for name, entry in target.items()
                 if any(Path(path).name == asset for kind in ["runtime", "runtimeTargets"] for path in entry.get(kind, {}))}
        if len(names) != 1:
            raise RuntimeError(project + " does not load a single PKCS runtime asset: " + str(sorted(names)))
        owner = next(iter(names))
        identifier, version = owner.split("/", 1)
        allowed = [package, "runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64",
                   "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64", "runtimepack.Microsoft.NETCore.App.Runtime.win-x64"]
        if identifier not in allowed or owner not in deps["libraries"]:
            raise RuntimeError("Unknown PKCS runtime owner: " + owner)
        match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)", version)
        if match is None or tuple(map(int, match.groups())) < (10, 0, 13):
            raise RuntimeError("PKCS package must be at least 10.0.13")
        owners.append(owner)
    if len({owner.split("/", 1)[1] for owner in owners}) != 1:
        raise RuntimeError("Desktop and agent PKCS runtime versions differ")
    if package_folders is None:
        assets = Path(__file__).resolve().parents[1] / "src/SPBackup.Core/obj/project.assets.json"
        package_folders = json.loads(assets.read_text())["packageFolders"]
    restored = [path for owner in set(owners) for folder in package_folders
                for path in (Path(folder) / owner.removeprefix("runtimepack.").lower()).rglob(asset)
                if path.is_file()]
    if not restored:
        raise RuntimeError("Restore the patched PKCS package before packaging")
    packaged_hash = hashlib.sha256((publish / asset).read_bytes()).digest()
    if not any(hashlib.sha256(path.read_bytes()).digest() == packaged_hash for path in restored):
        raise RuntimeError("Packaged PKCS DLL differs from the restored patched package")
    print("Verified packaged PKCS DLL:", ", ".join(sorted(set(owners))))


def package(publish, artifacts):
    required = ["SPBackup.Desktop.exe", "SPBackup.Desktop.dll", "SPBackup.Core.dll",
                "SPBackup.Agent.exe", "SPBackup.Agent.dll", "SPBackup.Agent.runtimeconfig.json", "SPBackup.Agent.deps.json",
                "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll",
                "PresentationFramework.dll", "PresentationCore.dll", "wpfgfx_cor3.dll"]
    for name in required:
        file = publish / name
        if not file.is_file() or file.stat().st_size == 0:
            raise RuntimeError(f"Missing distribution component: {name}")
    with (publish / "SPBackup.Desktop.exe").open("rb") as executable:
        if executable.read(2) != b"MZ":
            raise RuntimeError("Application host is not a Windows executable")
    config = json.loads((publish / "SPBackup.Desktop.runtimeconfig.json").read_text())
    frameworks = {framework["name"] for framework in config["runtimeOptions"].get("includedFrameworks", [])}
    if not {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}.issubset(frameworks):
        raise RuntimeError("The distribution does not include the .NET and Windows Desktop runtimes")
    version = distribution_version(publish)
    verify_pkcs_asset(publish)
    from windows_signing import sign_and_verify
    signed = sign_and_verify([publish / name for name in
        ("SPBackup.Desktop.exe", "SPBackup.Agent.exe", "SPBackup.Desktop.dll", "SPBackup.Agent.dll", "SPBackup.Core.dll")])
    from legacy_apphosts import copy_legacy_apphosts
    copy_legacy_apphosts(publish, ["Desktop", "Agent"], windows=True)
    files = [file for file in publish.rglob("*") if file.is_file() and file.suffix != ".pdb"]
    destination = artifacts / f"SP-Backup-{version}-windows-x64.zip"
    handle, temporary_name = tempfile.mkstemp(suffix=".zip.tmp", dir=artifacts)
    os.close(handle)
    temporary = Path(temporary_name)
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for file in sorted(files):
                archive.write(file, Path("SP-Backup") / file.relative_to(publish))
            instructions = INSTRUCTIONS.replace("Дистрибутив не подписан цифровой подписью.",
                "Программы подписаны проверенной цифровой подписью издателя.") if signed else INSTRUCTIONS
            archive.writestr("SP-Backup/START-HERE.txt", instructions.replace("1C-Backup-windows-x64-setup.exe", f"SP-Backup-{version}-windows-x64-setup.exe").replace("\n", "\r\n").encode("utf-8-sig"))
            archive.writestr("SP-Backup/VERSION.txt", (version + "\r\n").encode("utf-8"))
        with zipfile.ZipFile(temporary) as archive:
            corrupt = archive.testzip()
            if corrupt:
                raise RuntimeError(f"Corrupt archive member: {corrupt}")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)
    with destination.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    (artifacts / "distribution-version.txt").write_text(version + "\n", encoding="ascii")
    destination.with_suffix(".zip.sha256").write_text(f"{digest}  {destination.name}\n", encoding="ascii")
    print(f"Distribution: {destination}")
    print(f"Version: {version}")
    print(f"Size: {destination.stat().st_size / (1024 * 1024):.1f} MiB; files: {len(files) + 2}")
    print(f"SHA256: {digest}")


if __name__ == "__main__":
    main()
