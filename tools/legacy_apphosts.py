"""Keep pre-rename SCM and update handoff commands executable across upgrades."""
import shutil


def copy_legacy_apphosts(directory, components, *, windows):
    extension = ".exe" if windows else ""
    for component in components:
        source = directory / ("SPBackup." + component + extension)
        if not source.is_file() or not source.stat().st_size:
            raise RuntimeError("Missing application host: " + source.name)
        # Apphost embeds SPBackup.<component>.dll; the legacy filename launches
        # the new assembly. Copy after signing so both names have the signature.
        shutil.copy2(source, directory / ("OneCBackup." + component + extension))
