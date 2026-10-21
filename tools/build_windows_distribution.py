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


INSTRUCTIONS = """1C-Backup — Windows x64

НАЧНИТЕ ЗДЕСЬ
Откройте Help/user-guide.html двойным щелчком: это пошаговое руководство
с установкой, пробной копией, проверкой восстановления и расписанием.
Интернет для него не нужен. В программе: Настройки → Помощь → Пошаговая инструкция.
Сначала создайте копию тестовой папки по разделу 2 и восстановите файл по разделу 3.
Сервер мониторинга для первой копии не нужен.

ЗАПУСК
Можно использовать установщик 1C-Backup-windows-x64-setup.exe из релиза:
он установит программу в C:\\Program Files\\1C-Backup и создаст ярлык в «Пуск».
Windows запросит права администратора. После установки откройте программу
обычным запуском ярлыка: настройки хранятся отдельно у каждого пользователя.
Автозапуск включается отдельно в программе. Для переносного ZIP:
1. Распакуйте весь архив в отдельную папку, например C:\\Apps\\1C-Backup.
2. Запустите OneCBackup.Desktop.exe из распакованной папки.
Не запускайте программу непосредственно из ZIP и не переносите один EXE
отдельно от остальных файлов. Установка .NET не требуется.
Перед обновлением или удалением выберите «Выйти полностью» в меню значка
1C-Backup рядом с часами Windows (или в настройках). Программа дождётся
остановки агента. В старой версии нажмите «Остановить», дождитесь «Выключено»
и закройте окно. Удаление установленной программы сохраняет задания, историю,
пароли Windows и резервные копии.

Поддерживаемая система: Windows 10 22H2 или Windows 11, 64-разрядная x64.
Права администратора для самого приложения не требуются. Учётной записи
нужен доступ к базам, каталогам копий и используемым SQL-серверам.

ОФОРМЛЕНИЕ И ИМЕНА КОПИЙ
Настройки → Основное → Тема оформления: как в системе (по умолчанию), светлая или тёмная тема.
Выбор сохраняется. Даты в окне и расписание используют местное время Windows.
Файлы копий называются по имени задания, местной дате и времени создания
с уникальным суффиксом. Недопустимые для Windows знаки заменяются на подчёркивания.

ОПОВЕЩЕНИЯ TELEGRAM И MAX
Настройки → Подключения → Оповещения в Telegram и MAX → Настроить оповещения.
Выберите события, укажите токен бота и ID получателя, отправьте тест и сохраните.
Кнопка «Как подключить» открывает подробную инструкцию Help/notifications.txt.
Токены хранятся в защищённом хранилище Windows, включая режим службы.
При недоступности мессенджера отправка повторяется отдельно от копирования.

ДИАГНОСТИКА
При проблеме откройте Настройки → Диагностика → Сохранить диагностику…
Отправьте полученный ZIP для анализа вместе с примерным временем ошибки.
Логи окна и агента ведутся автоматически; пароли, настройки и копии
не включаются. Отправка выполняется вручную.

ФАЙЛЫ WEBDAV
Выберите «Файлы WebDAV», укажите конечный HTTPS-адрес папки и сохраните
логин / пароль приложения. Доступны полные и инкрементные ZIP, AES-256,
временная папка на HDD, несколько хранилищ и восстановление в новую папку.
Сильный ETag позволяет не скачивать неизменённые файлы; без него файлы
скачиваются и сравниваются по SHA-256. Это копия доступных файлов, не
единый снимок сервера, не серверные версии и не настройки Nextcloud.
Поддерживается Basic HTTPS без перенаправлений, HTTP и входа в браузере.

ФАЙЛЫ И ПАПКИ
Тип «Файлы и папки» копирует один файл или папку со вложенными файлами и пустыми каталогами.
Выберите полную копию или инкрементную: первая всегда полная, следующие ZIP содержат изменения.
«Архивов в цепочке» включает полную основу; после указанного числа создаётся новая полная.
Ошибка отдельного файла не перезапускает архив: после остальных файлов выполняется одна повторная
попытка, затем оставшиеся пропуски указываются в предупреждениях. Инкременты остаются инкрементами,
полные копии создаются по плану. Для пропущенного файла сохраняется прежняя версия из цепочки,
если она есть. Неполная копия не запускает очистку прежних архивов.
Лимит хранения сохраняет всю нужную цепочку, поэтому архивов может оказаться больше лимита.
Для восстановления выберите запись и нажмите «Восстановить файлы…»: цепочка проверяется
и собирается в новой папке, существующие файлы не заменяются. Отдельного инкремента недостаточно.
Сохраните все нужные архивы и каталог приложения. Пароль AES-256 задаётся как для копий баз.
Остановите приложения, меняющие исходные файлы. VSS, права NTFS и дополнительные потоки
не поддерживаются; ссылки и точки соединения отклоняются. Для баз используйте специальные типы.
Исходная папка не должна включать временную папку, профиль приложения или хранилища.

ПЕРВОЕ ЗАДАНИЕ
Откройте «Задания» и нажмите «+ Новое задание». Пройдите четыре шага:
источник, хранилище, расписание и проверка. Нажмите «Сохранить задание». Выберите задание в списке и нажмите «Создать копию».
Чтобы отправлять ту же копию в несколько мест, на шаге «Хранилище» раскройте
«Копировать также в другие хранилища» и добавьте папки или FTP / FTPS (до 8 мест всего).
Количество копий применяется отдельно в каждом месте.
Один готовый архив отправляется до трёх хранилищ одновременно; остальные ждут своей очереди.
Ошибка одного хранилища не останавливает остальные.
В окне дополнительного хранилища можно задать своё число последних успешных копий,
например 30 при 7 в основном. Пустое поле использует число основного хранилища
со шага «Расписание». Уменьшение числа применяется после новой успешной доставки.
На шаге «Расписание» проверьте три ближайших запуска по местному времени Windows.
Выбирать часовой пояс не нужно; план включён в итог перед сохранением.
Это плановое время. Для выполнения нужен работающий планировщик и включённый компьютер.
Итог «Частично» означает, что некоторые места получили копию, а другие — нет;
подробности доступны по кнопке «Результат».
В истории выберите запуск: пояснение подскажет дальнейшие действия.
«К заданию» откроет текущие настройки этой базы для исправления и повторного
ручного запуска. Сам переход копирование не запускает. Если задание недоступно,
его история сохраняется. Повтор частичной доставки создаёт новую копию для всех мест.
На шаге «Хранилище» выберите «Временная папка — где формировать копию» на HDD,
чтобы выгрузка, ZIP и временные файлы проверки FTP не записывались на SSD с базой.
Пустое поле использует стандартную рабочую папку приложения. После успешной
доставки рабочий подкаталог удаляется; при ошибке остаётся, его путь виден в истории.
Для SQL Server задайте HDD также в «Каталоге на SQL-сервере»: именно там создаётся .bak.
В списке заданий «Последний запуск» показывает результат каждой базы; наведите мышь для времени и причины.
Для полной истории выберите задание и нажмите «Результат».
«Проверить последние копии» сверяет размер и SHA-256 последнего файла в каждом хранилище выбранного задания.
Перед проверкой остановите автоматическое копирование; база не копируется, каталог не изменяется.
Ошибки, отсутствие копии и пути показаны в раскрываемых подробностях. Проверка не заменяет восстановление.
В «Резервных копиях» кнопка «Сохранить копию…» скачивает с FTP / FTPS или копирует выбранный файл на компьютер.
При сохранении видны этап, обработанный объём и прошедшее время.
Готовый файл появляется после проверки размера и SHA-256. Существующие файлы не перезаписываются.
Шифрование архива сохраняется; исходная копия и каталог не изменяются.
В «Резервных копиях» выберите задание и тип хранилища или найдите копию по пути.
«Последняя копия» у задания откроет список этой базы, «Сбросить» — общий список.
Полный путь, SHA256 и результат проверки доступны в раскрываемых подробностях.
У выбранного задания можно включить или выключить расписание кнопкой в списке.
Для ручных заданий сначала задайте время через «Изменить». Для исполнения расписания
включите автоматическое копирование. Уже принятые к выполнению копии не отменяются.
Меню «Файл» в заданиях сохраняет настройки и загружает их как новое задание в мастер.
Значения паролей не переносятся; на другом компьютере задайте их заново.
Импортированное расписание отключено до вашей проверки и включения.
Для нескольких баз с похожими настройками выберите задание и нажмите «На основе…».
Измените название и источник в мастере. Автоматические запуски нового задания
отключены: включите их после проверки. Хранение копий каждой базы независимо.
Перед первой копией выберите сохранённое задание и нажмите «Проверить источник»,
затем «Проверить хранилища». Проверка источника не создаёт копию.
Для проверки PostgreSQL нужен psql рядом с pg_dump; сам дамп использует
pg_dump / pg_restore и проверяется отдельным запуском.
При остановленном автоматическом копировании проверяются временная папка,
запись, чтение и удаление небольшого тестового файла в каждом назначении.
Результаты раскрываются под кнопками задания; ошибка одного места не мешает остальным.
Это проверка доступа. Создание и восстановление базы проверяются отдельно.
Для автоматических запусков нажмите «Включить автоматически» в верхней
панели. Для работы только при открытом окне есть режим в настройках агента. Отдельный агент
работает от текущего пользователя; окно можно закрыть. Настройка запуска
при входе в Windows находится в разделе «Настройки».
Службы Windows и работы без входа пользователя в этой версии нет.
Фоновый агент поддерживает автономный режим и автоматически опрашивает
включённые источники внешних SQL-копий с заданными интервалами.

Для ZIP файла 1Cv8.1CD платформа 1С не нужна, все сеансы базы должны быть
закрыты. Для выгрузки .dt требуется установленная платформа 1С.
Для PostgreSQL нужны отдельные pg_dump и pg_restore совместимых версий.
SQL Server использует Windows-аутентификацию и проверку сертификата TLS.

ПАРОЛЬ И ШИФРОВАНИЕ КОПИЙ
В редакторе задания нажмите «Задать новый пароль архива», введите пароль
дважды и сохраните задание. ZIP шифруется AES-256 и открывается в 7-Zip
или WinRAR. Пароль сохраняется в хранилище Windows текущего пользователя;
при смене пароля старые копии сохраняют прежний пароль. Сохраните пароли
отдельно: перенос настроек на другой компьютер не переносит пароли.
Без пароля содержимое восстановить невозможно. Исходная база,
временная выгрузка и копии на стороне SQL-сервера этим не шифруются.

ЗНАЧОК В ТРЕЕ
Крестик окна скрывает программу в трей рядом с часами Windows.
Если значок скрыт системой, откройте список значков стрелкой рядом с часами.
Двойной щелчок открывает окно, правый щелчок — меню остановки и полного выхода.
«Выйти полностью» отменяет текущую операцию и ждёт записи результата.
Если остановка не подтверждена, окно остаётся открытым с сообщением.
Повторный запуск ярлыка открывает уже работающее окно. Автозапуск в настройках
запускает агент со значком в трее; прежняя запись этой установки обновляется
при первом запуске новой программы.

НАСТРОЙКИ И ДАННЫЕ
Версия видна слева под названием программы. В разделе «Настройки» →
«О программе» нажмите «Скопировать сведения» для сообщения об ошибке:
копируются только версия, идентификатор сборки, .NET, архитектура и система.
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


def distribution_version(publish):
    versions = []
    for project in ["OneCBackup.Desktop", "OneCBackup.Agent"]:
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
    for project in ["OneCBackup.Desktop", "OneCBackup.Agent"]:
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
        assets = Path(__file__).resolve().parents[1] / "src/OneCBackup.Core/obj/project.assets.json"
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
    version = distribution_version(publish)
    verify_pkcs_asset(publish)
    from windows_signing import sign_and_verify
    signed = sign_and_verify([publish / name for name in
        ("OneCBackup.Desktop.exe", "OneCBackup.Agent.exe", "OneCBackup.Desktop.dll", "OneCBackup.Agent.dll", "OneCBackup.Core.dll")])
    files = [file for file in publish.rglob("*") if file.is_file() and file.suffix != ".pdb"]
    destination = artifacts / f"1C-Backup-{version}-windows-x64.zip"
    handle, temporary_name = tempfile.mkstemp(suffix=".zip.tmp", dir=artifacts)
    os.close(handle)
    temporary = Path(temporary_name)
    try:
        with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for file in sorted(files):
                archive.write(file, Path("1C-Backup") / file.relative_to(publish))
            instructions = INSTRUCTIONS.replace("Дистрибутив не подписан цифровой подписью.",
                "Программы подписаны проверенной цифровой подписью издателя.") if signed else INSTRUCTIONS
            archive.writestr("1C-Backup/START-HERE.txt", instructions.replace("1C-Backup-windows-x64-setup.exe", f"1C-Backup-{version}-windows-x64-setup.exe").replace("\n", "\r\n").encode("utf-8-sig"))
            archive.writestr("1C-Backup/VERSION.txt", (version + "\r\n").encode("utf-8"))
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
