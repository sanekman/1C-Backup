"""Open an alpha.40 SQL observation through the installed candidate's WPF controls."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

from pywinauto import Application, keyboard

LEGACY_INSTALLER = "SP-Backup-0.2.0-alpha.40-windows-x64-setup.exe"
LEGACY_SHA = "17d11e78170eeef95f5f92cd98b6420d77730a5c28d459ed655ea496c5e140b4"
INSTALL_OPTIONS = ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"]
FLAG_IDS = ("SqlTrustServerCertificate", "SqlAllowUnencrypted")
WARNINGS = ("MS SQL: отключение проверки сертификата", "MS SQL · шифрование по требованию сервера")


def run(*args):
    subprocess.run([str(arg) for arg in args], check=True, timeout=180)


def wait_for(check, seconds=30):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if value := check():
            return value
        time.sleep(0.2)
    raise RuntimeError("Timed out waiting for the expected application state.")


def control(window, name, states="exists enabled"):
    return window.child_window(auto_id=name).wait(states, timeout=20).wrapper_object()


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def assert_flags(job, value):
    assert all(job.get(name, False) is value for name in ("TrustServerCertificate", "AllowUnencryptedConnection"))


def verify_windows(executable, profile, expected, original_history, checks):
    path = profile / "external-monitors.json"
    ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    desktop = Application(backend="uia")
    def open_editor(app):
        main = app.window(title_re=r"^SP Backup.*")
        control(main, "ExternalMonitoringButton", "exists visible enabled").invoke()
        editor = app.window(title="Мониторинг внешних копий SQL").wait("exists visible enabled", timeout=25)
        hwnd = editor.wrapper_object().handle
        assert ctypes.windll.user32.SetWindowPos(hwnd, 0, 20, 20, 900, 580, 0x0004)
        control(editor, "SourceSettings").select()
        control(editor, "SqlCertificateSettings").expand()
        return editor, hwnd

    def state(editor, value):
        assert all(control(editor, name).get_toggle_state() == int(value) for name in FLAG_IDS)

    def toggle(editor, name):
        control(editor, name).set_focus()
        keyboard.send_keys("{SPACE}")

    def save(editor, value):
        control(editor, "SaveMonitorButton", "exists visible enabled").invoke()
        def persisted():
            saved = read_json(path)
            flags = tuple(saved[0].get(key, False) for key in ("TrustServerCertificate", "AllowUnencryptedConnection"))
            return saved if flags == (value, value) else None
        observed, = wait_for(persisted)
        for option in ("TrustServerCertificate", "AllowUnencryptedConnection"):
            observed.pop(option, None)
        assert observed == expected

    measurements = {}
    for phase in ("edit", "restart-and-restore"):
        process = subprocess.Popen([str(executable)])
        try:
            app = desktop.connect(process=process.pid, timeout=20)
            editor, hwnd = open_editor(app)
            box = editor.rectangle()
            measurements = {"width": box.width(), "height": box.height(), "dpi": ctypes.windll.user32.GetDpiForWindow(hwnd)}
            state(editor, phase != "edit")
            if phase == "edit":
                # Move with Tab from the login method to the credential action in the compact editor.
                control(editor, "SqlAuthentication").set_focus()
                keyboard.send_keys("{TAB}")
                password_action = control(editor, "SqlCredentialsButton", "exists visible enabled")
                assert password_action.has_keyboard_focus()
                password_action.invoke()
                password_dialog = app.window(title="Логин и пароль MS SQL").wait("exists visible", timeout=15)
                password_dialog.close()
                checks.append("sql-credentials-reachable-with-tab-at-900x580-and-cancelled")
                for name, caption in zip(FLAG_IDS, WARNINGS):
                    toggle(editor, name)
                    warning = app.window(title=caption).wait("exists visible", timeout=10)
                    warning.child_window(auto_id="6", control_type="Button").wrapper_object().invoke()
                save(editor, True)
                checks.append("old-all-databases-source-kept-identity-and-credential-reference")
            else:
                checks.append("both-security-options-survived-a-full-application-restart")
                control(editor, "CollectMonitorButton", "exists visible enabled").invoke()
                wait_for(lambda: "Ошибка чтения журнала. Прежняя история сохранена" in
                         control(editor, "Status", "exists").window_text(), 50)
                assert (profile / "external-backups.json").read_bytes() == original_history
                checks.append("connection-failure-kept-the-saved-history")
                control(editor, "SourceSettings").select()
                control(editor, "SqlCertificateSettings").expand()
                for name in FLAG_IDS:
                    toggle(editor, name)
                save(editor, False)
                checks.append("original-security-settings-restored-without-other-changes")
            editor.close()
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)
    return measurements


def main():
    if sys.platform != "win32" or os.environ.get("GITHUB_ACTIONS") != "true" or len(sys.argv) != 3:
        raise RuntimeError("Requires the isolated Windows installer workflow.")
    installer, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    output.mkdir(parents=True)
    profile = Path(os.environ["LOCALAPPDATA"]) / "OneCBackup"
    install = Path(os.environ["ProgramFiles"]) / "SP Backup"
    assert not profile.exists() and not install.exists()
    checks = []
    stage = "install-alpha40"
    with tempfile.TemporaryDirectory(prefix="spbackup-observation-") as directory, socket.socket() as reservation:
        lab = Path(directory)
        reservation.bind(("127.0.0.1", 0))
        credential = lab / "credential-key"
        probe = lab / "probe" / "WindowsLegacySqlFixture.dll"
        try:
            run("gh", "release", "download", "v0.2.0-alpha.40", "-p", LEGACY_INSTALLER, "-D", lab)
            old_installer = lab / LEGACY_INSTALLER
            with old_installer.open("rb") as stream:
                assert hashlib.file_digest(stream, "sha256").hexdigest() == LEGACY_SHA
            run(old_installer, *INSTALL_OPTIONS, "/DIR=" + str(install))
            stage = "seed-old-format"
            run("dotnet", "build", "tests/WindowsLegacySqlFixture", "-c", "Release",
                "-p:InstalledAgentDirectory=" + str(install), "-o", probe.parent)
            run("dotnet", probe, f"127.0.0.1,{reservation.getsockname()[1]}", profile, credential)
            settings = profile / "external-monitors.json"
            before = settings.read_bytes()
            history = (profile / "external-backups.json").read_bytes()
            original, = read_json(settings)
            assert_flags(original, False)
            stage = "upgrade-to-candidate"
            run(installer, *INSTALL_OPTIONS)
            version = (install / "VERSION.txt").read_text(encoding="utf-8-sig").strip()
            assert version != "0.2.0-alpha.40"
            assert settings.read_bytes() == before
            assert (profile / "external-backups.json").read_bytes() == history
            checks.append("published-alpha40-profile-and-history-survived-native-upgrade")
            stage = "exercise-installed-wpf"
            measurements = verify_windows(install / "SPBackup.Desktop.exe", profile, original, history, checks)
            stage = "passed"
            (output / "observation-upgrade-result.json").write_text(json.dumps({
                "success": True, "baseline": "0.2.0-alpha.40", "candidate": version, "measurements": measurements, "checks": checks
            }))
        except Exception as error:
            (output / "observation-upgrade-result.json").write_text(json.dumps({
                "success": False, "stage": stage, "errorType": type(error).__name__, "checks": checks
            }))
            raise
        finally:
            if credential.exists() and probe.exists():
                run("dotnet", probe, "cleanup", profile, credential)


if __name__ == "__main__":
    main()
