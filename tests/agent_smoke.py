"""Run the actual headless agent, a scheduled 1CD ZIP, duplicate-owner rejection and graceful stop."""
import argparse
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import subprocess
import tempfile
import time
import uuid
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    assembly = root / "src/OneCBackup.Agent/bin/Debug/net10.0/OneCBackup.Agent.dll"
    with tempfile.TemporaryDirectory(prefix="onec-agent-smoke-") as fixture:
        data = Path(fixture)
        source = data / "base"
        source.mkdir()
        content = b"smoke database content\n" * 4096
        (source / "1Cv8.1CD").write_bytes(content)
        temporary = data / "HDD temporary"
        temporary.mkdir()
        marker = temporary / "unrelated.txt"
        marker.write_text("keep unrelated files")
        job_id = str(uuid.uuid4())
        # Leave startup margin even when this fixture begins just before a minute boundary.
        # The product intentionally skips past slots after a fresh start.
        due = (datetime.now(timezone.utc) + timedelta(seconds=10)).replace(second=0, microsecond=0) + timedelta(minutes=1)
        jobs = [{"Id": job_id, "Name": "Agent smoke", "Source": str(source), "Kind": "OneCFile",
                 "OneCMethod": "FileArchive", "DestinationDirectory": str(data / "copies"),
                 "TemporaryDirectory": str(temporary),
                 "ScheduleKind": "Daily", "DailyAt": due.strftime("%H:%M:%S"), "TimeZoneId": "UTC", "KeepLast": 1}]
        (data / "jobs.json").write_text(json.dumps(jobs))
        base = [args.dotnet, str(assembly), "--data-dir", str(data)]
        process = subprocess.Popen(base + ["--run", "--poll-seconds", "1"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        print("Agent process started; scheduled slot:", due.isoformat(), flush=True)
        try:
            deadline = time.monotonic() + 10
            while not (data / "local-agent.json").exists():
                if process.poll() is not None:
                    raise RuntimeError(process.communicate()[1])
                if time.monotonic() > deadline:
                    raise RuntimeError("Agent startup timed out")
                time.sleep(0.1)
            initial = json.loads((data / "local-agent.json").read_text())
            duplicate = subprocess.run(base + ["--run-job", job_id], capture_output=True, text=True, timeout=10)
            assert duplicate.returncode == 1, "Second owner was allowed"
            assert json.loads((data / "local-agent.json").read_text())["SessionId"] == initial["SessionId"]
            print("Duplicate owner rejected; waiting for the scheduled backup.", flush=True)
            deadline = time.monotonic() + 90
            while True:
                if process.poll() is not None:
                    raise RuntimeError(process.communicate()[1])
                if (data / "runs.json").exists():
                    runs = json.loads((data / "runs.json").read_text())
                    if runs and runs[0]["Status"] == "Succeeded":
                        break
                    if runs and runs[0]["Status"] == "Failed":
                        raise RuntimeError(runs[0]["Error"])
                if time.monotonic() > deadline:
                    raise RuntimeError("Scheduled backup timed out")
                time.sleep(0.2)
            record = json.loads((data / "copies.json").read_text())[0]
            assert Path(runs[0]["WorkDirectory"]).parent == temporary
            assert not Path(runs[0]["WorkDirectory"]).exists()
            assert not (data / "work").exists()
            assert marker.read_text() == "keep unrelated files"
            with zipfile.ZipFile(record["Backup"]["Location"]) as archive:
                assert archive.namelist() == ["1Cv8.1CD"]
                assert archive.read("1Cv8.1CD") == content
            subprocess.run(base + ["--stop"], capture_output=True, text=True, check=True, timeout=10)
            output, error = process.communicate(timeout=10)
            assert process.returncode == 0, error
            assert json.loads((data / "local-agent.json").read_text())["Phase"] == "Stopped"
            assert (source / "1Cv8.1CD").read_bytes() == content
            print("PASS: scheduled ZIP content, duplicate-owner rejection, terminal history and graceful stop.", flush=True)
            files = data / "ordinary-source"
            files.mkdir()
            (files / "stable.txt").write_text("unchanged")
            (files / "changed.txt").write_text("before")
            (files / "removed.txt").write_text("remove after full")
            file_job = dict(jobs[0], Id=str(uuid.uuid4()), Name="Files agent smoke", Kind="FilesAndFolders",
                            Source=str(files), DestinationDirectory=str(data / "ordinary-copies"),
                            ScheduleKind="Manual", FileBackupMode="Incremental", FullBackupEvery=2, Enabled=False)
            (data / "jobs.json").write_text(json.dumps([*jobs, file_job]))
            def run_files():
                result = subprocess.run(base + ["--run-job", file_job["Id"]], capture_output=True, text=True, timeout=30)
                assert result.returncode == 0, result.stderr + result.stdout
                return [record for record in json.loads((data / "copies.json").read_text()) if record["JobId"] == file_job["Id"]]
            first = run_files()
            assert len(first) == 1 and first[0]["Backup"]["FileSnapshot"]["IsFull"]
            (files / "changed.txt").write_text("after")
            (files / "removed.txt").unlink()
            second = run_files()
            assert len(second) == 2, "Retention removed the incremental basis"
            increment = next(record for record in second if record["Backup"]["FileSnapshot"]["Depth"] == 1)
            with zipfile.ZipFile(increment["Backup"]["Location"]) as archive:
                assert "data/stable.txt" not in archive.namelist()
                assert archive.read("data/changed.txt") == b"after"
                manifest = json.loads(archive.read("onec-backup-manifest.json"))
                assert manifest["DeletedFiles"] == ["removed.txt"]
            third = run_files()
            assert len(third) == 1 and third[0]["Backup"]["FileSnapshot"]["IsFull"]
            assert not any(Path(record["Backup"]["Location"]).exists() for record in second)
            assert (files / "stable.txt").read_text() == "unchanged" and marker.read_text() == "keep unrelated files"
            print("PASS: actual agent full/incremental file jobs, changed/deleted index and safe chain rotation.", flush=True)
        finally:
            if process.poll() is None:
                subprocess.run(base + ["--stop"], capture_output=True, timeout=10)
                try:
                    process.communicate(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.communicate()


if __name__ == "__main__":
    main()
