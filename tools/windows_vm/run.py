"""Provision an owned, private Windows evaluation VM and prove an actual interrupted-backup reboot.

Control uses an authenticated private hypervisor serial socket. VM disks, seed credentials and the downloaded OS are never artifacts.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import time
import urllib.request
import uuid
import zipfile
from xml.sax.saxutils import escape


ISO_URL = ('https://software-static.download.prss.microsoft.com/dbazure/'
           '998969d5-f34g-4e03-ac9d-1f9786c66749/'
           '26100.32230.260111-0550.lt_release_svc_refresh_SERVER_EVAL_x64FRE_en-us.iso')
HERE = Path(__file__).resolve().parent


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def download(url, destination):
    with urllib.request.urlopen(url, timeout=60) as response, destination.open('xb') as output:
        shutil.copyfileobj(response, output, 1024 * 1024)
    return sha256(destination)


class VM:
    def __init__(self, root, iso, seed):
        self.root, self.iso, self.seed = root, iso, seed
        self.process = None
        self.qmp_path = root / 'qmp.sock'
        self.control_path = root / 'control.sock'
        self.log = (root / 'qemu.log').open('ab')
        subprocess.run(['qemu-img', 'create', '-f', 'qcow2', str(root / 'windows.qcow2'), '80G'], check=True)
        shutil.copyfile('/usr/share/OVMF/OVMF_VARS_4M.fd', root / 'uefi-vars.fd')

    def start(self, install=False):
        setup_log = self.root / 'setup.log'
        self.serial_offset = setup_log.stat().st_size if setup_log.exists() else 0
        self.qmp_path.unlink(missing_ok=True)
        self.control_path.unlink(missing_ok=True)
        args = ['qemu-system-x86_64', '-enable-kvm', '-machine', 'q35', '-cpu', 'host,hv_relaxed,hv_vapic,hv_spinlocks=0x1fff',
                '-m', '6144', '-smp', '4', '-display', 'none', '-rtc', 'base=utc',
                '-drive', 'if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd',
                '-drive', f'if=pflash,format=raw,file={self.root / "uefi-vars.fd"}',
                '-device', 'ich9-ahci,id=sata',
                '-drive', f'if=none,id=osdisk,format=qcow2,file={self.root / "windows.qcow2"}',
                '-device', 'ide-hd,drive=osdisk,bus=sata.0',
                '-netdev', 'user,id=private,restrict=on',
                '-device', 'e1000e,netdev=private',
                '-qmp', f'unix:{self.qmp_path},server=on,wait=off',
                '-chardev', f'file,id=setup,path={self.root / "setup.log"},append=on',
                '-device', 'isa-serial,chardev=setup,index=0',
                '-chardev', f'socket,id=control,path={self.control_path},server=on,wait=off',
                '-device', 'isa-serial,chardev=control,index=1', '-boot', 'order=c,once=d' if install else 'order=c']
        if install:
            args += ['-drive', f'if=none,id=install,media=cdrom,format=raw,readonly=on,file={self.iso}',
                     '-device', 'ide-cd,drive=install,bus=sata.1',
                     '-drive', f'if=none,id=seed,media=cdrom,format=raw,readonly=on,file={self.seed}',
                     '-device', 'ide-cd,drive=seed,bus=sata.2']
        self.process = subprocess.Popen(args, stdout=self.log, stderr=self.log)

    def command(self, name, arguments=None):
        with socket.socket(socket.AF_UNIX) as connection:
            connection.settimeout(10)
            connection.connect(str(self.qmp_path))
            stream = connection.makefile('rwb', buffering=0)
            json.loads(stream.readline())
            def call(command, values):
                stream.write((json.dumps({'execute': command, 'arguments': values or {}}) + '\n').encode())
                while True:
                    result = json.loads(stream.readline())
                    if 'error' in result:
                        raise RuntimeError(result['error'])
                    if 'return' in result:
                        return result['return']
            call('qmp_capabilities', {})
            return call(name, arguments)

    def stop(self):
        if self.process and self.process.poll() is None:
            try:
                self.command('quit')
                self.process.wait(timeout=20)
            except (OSError, ValueError, subprocess.TimeoutExpired):
                self.process.kill()
                self.process.wait(timeout=10)


def answer(password):
    return f'''<?xml version="1.0" encoding="utf-8"?>
<!-- SPBackup isolated VM -->
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
<settings pass="windowsPE">
<component name="Microsoft-Windows-International-Core-WinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><SetupUILanguage><UILanguage>en-US</UILanguage></SetupUILanguage><InputLocale>en-US</InputLocale><SystemLocale>en-US</SystemLocale><UILanguage>en-US</UILanguage><UserLocale>en-US</UserLocale></component>
<component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
<DiskConfiguration><Disk wcm:action="add"><DiskID>0</DiskID><WillWipeDisk>true</WillWipeDisk><CreatePartitions>
<CreatePartition wcm:action="add"><Order>1</Order><Type>EFI</Type><Size>260</Size></CreatePartition>
<CreatePartition wcm:action="add"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition>
<CreatePartition wcm:action="add"><Order>3</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
</CreatePartitions><ModifyPartitions><ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition><ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Label>SPBWindows</Label><Letter>C</Letter></ModifyPartition></ModifyPartitions></Disk></DiskConfiguration>
<ImageInstall><OSImage><InstallFrom><MetaData wcm:action="add"><Key>/IMAGE/INDEX</Key><Value>2</Value></MetaData></InstallFrom><InstallTo><DiskID>0</DiskID><PartitionID>3</PartitionID></InstallTo></OSImage></ImageInstall><UserData><AcceptEula>true</AcceptEula><FullName>SP Backup Lab</FullName><Organization>Isolated test</Organization></UserData></component></settings>
<settings pass="specialize"><component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><ComputerName>SPB-VM</ComputerName><TimeZone>UTC</TimeZone></component>
<component name="Microsoft-Windows-Deployment" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><RunSynchronous><RunSynchronousCommand wcm:action="add"><Order>1</Order><Path>cmd /c for %d in (D E F G H I) do if exist %d:\\SPBLAB.marker powershell.exe -NoProfile -ExecutionPolicy Bypass -File %d:\\bootstrap.ps1 -StageOnly</Path></RunSynchronousCommand></RunSynchronous></component></settings>
<settings pass="oobeSystem"><component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><OOBE><HideEULAPage>true</HideEULAPage><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE><ProtectYourPC>3</ProtectYourPC><SkipMachineOOBE>true</SkipMachineOOBE><SkipUserOOBE>true</SkipUserOOBE></OOBE><UserAccounts><AdministratorPassword><Value>{escape(password)}</Value><PlainText>true</PlainText></AdministratorPassword></UserAccounts></component></settings></unattend>'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    root = output.parent / ('private-vm-' + uuid.uuid4().hex)
    root.mkdir(mode=0o700)
    password = 'SpB!9-' + secrets.token_urlsafe(24)
    control_secret = secrets.token_urlsafe(32)
    print('::add-mask::' + password, flush=True)
    print('::add-mask::' + control_secret, flush=True)
    provenance = {'source': os.environ['GITHUB_SHA'], 'iso_url': ISO_URL, 'success': False, 'control_transport': 'private-hypervisor-serial'}
    vm = None
    try:
        bundle = args.bundle.resolve()
        manifest = json.loads((bundle / 'bundle.json').read_text(encoding='utf-8-sig'))
        if manifest['revision'] != provenance['source'] or not manifest['runnerVerified']:
            raise RuntimeError('The bundle does not belong to this tested source.')
        for item in manifest['files']:
            if sha256(bundle / item['path'].replace('\\', '/')) != item['sha256'].lower():
                raise RuntimeError('Bundle SHA-256 mismatch: ' + item['path'])
        print('Downloading official Windows evaluation ISO.', flush=True)
        iso = root / 'windows.iso'
        provenance['iso_sha256'] = download(ISO_URL, iso)
        seed = root / 'seed'
        seed.mkdir()
        (seed / 'SPBLAB.marker').write_text('owned VM seed')
        (seed / 'owner.secret').write_text(password)
        (seed / 'control.secret').write_text(control_secret)
        (seed / 'Autounattend.xml').write_text(answer(password), encoding='utf-8')
        for name in ('bootstrap.ps1', 'owner-task.ps1', 'control-agent.ps1'):
            shutil.copyfile(HERE / name, seed / name)
        download('https://aka.ms/vs/17/release/vc_redist.x64.exe', seed / 'vc_redist.x64.exe')
        with zipfile.ZipFile(seed / 'bundle.zip', 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
            for path in bundle.rglob('*'):
                relative = path.relative_to(bundle)
                if path.is_file() and (relative.parts[0] in ('agent', 'desktop', 'probe', 'postgresql') or relative.as_posix() == 'bundle.json'):
                    archive.write(path, relative.as_posix())
        subprocess.run(['genisoimage', '-quiet', '-J', '-R', '-V', 'SPBLAB', '-o', str(root / 'seed.iso'), str(seed)], check=True)
        vm = VM(root, iso, root / 'seed.iso')
        vm.start(install=True)
        time.sleep(2)
        for _ in range(12):
            vm.command('send-key', {'keys': [{'type': 'qcode', 'data': 'ret'}]})
            time.sleep(1)
        def ps(script):
            request_id = uuid.uuid4().hex
            script = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" + script
            request = {'protocol': 1, 'id': request_id, 'token': control_secret,
                       'script': base64.b64encode(script.encode()).decode()}
            with socket.socket(socket.AF_UNIX) as connection:
                connection.settimeout(40)
                connection.connect(str(vm.control_path))
                connection.sendall((json.dumps(request) + '\n').encode())
                with connection.makefile('rb') as stream:
                    while True:
                        line = stream.readline(4 * 1024 * 1024 + 1)
                        if not line: raise ConnectionError('The private guest control channel closed.')
                        if len(line) > 4 * 1024 * 1024: raise ValueError('Oversized private guest response.')
                        try: result = json.loads(line)
                        except (ValueError, UnicodeError): continue  # Firmware text is never a protocol response.
                        if result.get('protocol') != 1 or result.get('id') != request_id: continue
                        if result['code']:
                            raise RuntimeError(str(result['error']).replace(password, '[redacted]').replace(control_secret, '[redacted]')[-5000:])
                        return result['output'].strip()
        def wait_guest(seconds):
            deadline = time.monotonic() + seconds
            last_setup_line = ''
            last_error = ''
            while time.monotonic() < deadline:
                if vm.process.poll() is not None: raise RuntimeError('The owned VM exited before readiness.')
                setup_log = root / 'setup.log'
                channel_ready = False
                if setup_log.exists():
                    lines = setup_log.read_bytes()[vm.serial_offset:].decode(errors='replace').splitlines()
                    line = lines[-1] if lines else ''
                    if line and line != last_setup_line:
                        print('Windows setup: ' + line.replace(password, '[redacted]'), flush=True)
                        last_setup_line = line
                    if any('FAILED:' in line for line in lines): raise RuntimeError('Windows bootstrap failed: ' + line)
                    channel_ready = any('Ready; waiting for Windows Setup' in line or 'SPBackup control channel ready' in line for line in lines)
                if not channel_ready:
                    time.sleep(5)
                    continue
                try:
                    value = ps("if((Get-ItemProperty HKLM:\\SYSTEM\\Setup).SystemSetupInProgress -ne 0){throw 'setup'};Get-Content C:\\SPBLab\\ready.json -Raw")
                    if json.loads(value)['ready']: return
                except Exception as error:
                    message = (type(error).__name__ + ': ' + str(error)).replace(password, '[redacted]').replace(control_secret, '[redacted]')[-1500:]
                    provenance['last_control_error'] = message
                    if message != last_error:
                        print('Guest readiness: ' + message, flush=True)
                        last_error = message
                    time.sleep(5)
            raise TimeoutError('Windows did not complete setup and private hypervisor control: ' + last_error)
        print('Waiting for Windows setup.', flush=True)
        wait_guest(1200)
        vm.command('eject', {'device': 'seed', 'force': True})
        def launch(phase):
            name = 'SPBackupOwnedVM-' + phase
            ps(f"if(Get-ScheduledTask -TaskName '{name}' -ErrorAction SilentlyContinue){{throw 'task exists'}};"
               f"$a=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\\SPBLab\\owner-task.ps1 -Phase {phase}';"
               f"Register-ScheduledTask -TaskName '{name}' -Action $a -User Administrator -Password (Get-Content C:\\SPBLab\\owner.secret -Raw) -RunLevel Highest | Out-Null;"
               f"Start-ScheduledTask -TaskName '{name}'")
        def read_json(path):
            text = ps(f"if(Test-Path '{path}'){{Get-Content '{path}' -Raw}}")
            return json.loads(text) if text else None
        def wait_result(phase, path, timeout=600):
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                result = read_json(path)
                if result:
                    (output / Path(path.replace('\\', '/')).name).write_text(json.dumps(result, indent=2))
                    if not result.get('success') or result.get('revision') != provenance['source']:
                        raise RuntimeError(phase + ' acceptance failed: ' + json.dumps(result))
                    return result
                exited = ps(f"if(Test-Path 'C:\\SPBLab\\results\\{phase}-exit.txt'){{Get-Content 'C:\\SPBLab\\results\\{phase}-exit.txt'}}")
                if exited:
                    raise RuntimeError(phase + ' exited without a result: ' + ps(f"Get-Content 'C:\\SPBLab\\results\\{phase}.log' -Tail 45"))
                time.sleep(3)
            raise TimeoutError(phase + ' verification did not finish.')
        print('Verifying real NTFS quota, full volume and independent destinations.', flush=True)
        launch('volumes')
        wait_result('volumes', r'C:\SPBLab\results\volumes\volume-result.json')
        print('Verifying service-account switch without startup replay.', flush=True)
        launch('switch')
        wait_result('switch', r'C:\SPBLab\results\switch\service-switch-result.json')
        launch('prepare')
        deadline = time.monotonic() + 900
        while True:
            ready = read_json(r'C:\SPBLab\results\reboot\reboot-ready.json')
            if ready: break
            if ps("if(Test-Path C:\\SPBLab\\results\\prepare-exit.txt){Get-Content C:\\SPBLab\\results\\prepare-exit.txt}"):
                raise RuntimeError('Reboot preparation exited: ' + ps('Get-Content C:\\SPBLab\\results\\prepare.log -Tail 45'))
            if time.monotonic() > deadline: raise TimeoutError('The real dump did not reach the reboot barrier.')
            time.sleep(2)
        provenance['before_reboot'] = ready
        print('Interrupting the real blocked dump by powering off the owned VM.', flush=True)
        vm.stop()
        vm.start()
        wait_guest(300)
        provenance['interactive_user_after_reboot'] = ps('(Get-CimInstance Win32_ComputerSystem).UserName')
        if provenance['interactive_user_after_reboot']:
            raise RuntimeError('An interactive user logged in before the service reboot verification.')
        launch('resume')
        result = wait_result('resume', r'C:\SPBLab\results\reboot\reboot-result.json')
        if result['previousBootId'] == result['bootId']:
            raise RuntimeError('Reboot acceptance reused the old Windows boot.')
        print('Preparing a separate real boot with interactive owner startup.', flush=True)
        launch('user-prepare')
        deadline = time.monotonic() + 120
        while not ps("if(Test-Path C:\\SPBLab\\results\\user-prepare-exit.txt){Get-Content C:\\SPBLab\\results\\user-prepare-exit.txt}"):
            if time.monotonic() > deadline: raise TimeoutError('User startup preparation did not finish.')
            time.sleep(2)
        if ps('Get-Content C:\\SPBLab\\results\\user-prepare-exit.txt') != '0':
            raise RuntimeError('User startup preparation failed: ' + ps('Get-Content C:\\SPBLab\\results\\user-prepare.log -Tail 45'))
        # Disposable isolated guest only; the generated credential never leaves its private disk.
        ps("$k='HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon';"
           "Set-ItemProperty $k AutoAdminLogon '1';Set-ItemProperty $k DefaultUserName 'Administrator';"
           "Set-ItemProperty $k DefaultDomainName $env:COMPUTERNAME;"
           "Set-ItemProperty $k DefaultPassword (Get-Content C:\\SPBLab\\owner.secret -Raw)")
        vm.stop()
        vm.start()
        wait_guest(300)
        deadline = time.monotonic() + 180
        while not ps('(Get-CimInstance Win32_ComputerSystem).UserName'):
            if time.monotonic() > deadline: raise TimeoutError('The owned interactive owner did not log in.')
            time.sleep(3)
        ps("$k='HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon';"
           "Set-ItemProperty $k AutoAdminLogon '0';Remove-ItemProperty $k DefaultPassword -ErrorAction SilentlyContinue")
        launch('user-verify')
        user_result = wait_result('user-verify', r'C:\SPBLab\results\user-startup\user-startup-result.json')
        if user_result['previousBootId'] == user_result['bootId']:
            raise RuntimeError('User startup acceptance reused the old Windows boot.')
        provenance['success'] = True
        print('Actual Windows boot changed; backups, identity, queue and restored bytes verified.', flush=True)
    except Exception as error:
        provenance['error'] = str(error).replace(password, '[redacted]').replace(control_secret, '[redacted]')
        if vm and vm.process and vm.process.poll() is None:
            try:
                from PIL import Image
                vm.command('screendump', {'filename': str(root / 'failure.ppm')})
                Image.open(root / 'failure.ppm').save(output / 'windows-screen.png')
            except Exception: pass
        raise
    finally:
        if vm: vm.stop()
        (output / 'vm-result.json').write_text(json.dumps(provenance, indent=2))
        for log_name in ('qemu.log', 'setup.log'):
            if (root / log_name).exists(): shutil.copyfile(root / log_name, output / log_name)
        shutil.rmtree(root)


if __name__ == '__main__':
    main()
