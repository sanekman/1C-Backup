"""Ensure required integration actually ran; only named platform-specific skips are allowed."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
SERVER_TEST_COUNT = 191
WINDOWS_SKIPS = {
    'SPBackup.Server.Tests.ServerUpdateClientTests.AbsentSocketDoesNotClaimThatTheServerIsUpdated',
    'SPBackup.Server.Tests.ServerUpdateClientTests.ClientUsesFixedJsonProtocolAndCannotSendACommandOrUrl',
    'SPBackup.Server.Tests.ServerUpdateAdministrationTests.SettingsOfferOnlyCheckedReleaseAndShowRunningTaskAfterRedirectAndRestart',
}


def verify_suite(path, total, allowed_skips=frozenset()):
    tree = ET.parse(path)
    counters = tree.find('.//' + NS + 'Counters')
    results = tree.findall('.//' + NS + 'UnitTestResult')
    skipped = {result.attrib['testName'] for result in results if result.attrib.get('outcome') == 'NotExecuted'}
    expected = total - len(allowed_skips)
    if (counters is None or int(counters.attrib.get('total', 0)) != total
            or int(counters.attrib.get('passed', 0)) != expected or int(counters.attrib.get('failed', 0)) != 0
            or len(results) != total or skipped != allowed_skips
            or sum(result.attrib.get('outcome') == 'Passed' for result in results) != expected):
        raise RuntimeError(f'{path.name}: expected {expected}/{total} passing tests and exactly {len(allowed_skips)} named platform skips')
    print(path.name, expected, 'passed;', len(skipped), 'named platform skips')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', choices=['linux-x64', 'win-x64'], required=True)
    parser.add_argument('--maintenance-only', action='store_true', help='Windows package/upgrade/installer checks; integration already accepted on unchanged C# sources')
    parser.add_argument('--snapshot-only', action='store_true', help='Windows package and installed snapshot checks')
    parser.add_argument('--results-dir', type=Path, default=Path(__file__).resolve().parents[2] / 'artifacts/server-tests')
    parser.add_argument('--integration-only', action='store_true', help='Verify integration and built package; no unchanged native installer scenarios')
    args = parser.parse_args()
    if sum([args.integration_only, args.maintenance_only, args.snapshot_only]) > 1:
        parser.error('only one acceptance scope may be chosen')
    if (args.maintenance_only or args.snapshot_only) and args.runtime != 'win-x64':
        parser.error('maintenance and snapshot acceptance are restricted to Windows')
    platform = 'windows' if args.runtime == 'win-x64' else 'linux'
    root = args.results_dir
    if not args.maintenance_only and not args.snapshot_only:
        verify_suite(root / f'server-{platform}.trx', SERVER_TEST_COUNT, WINDOWS_SKIPS if platform == 'windows' else frozenset())
    verify_suite(root / f'server-package-{platform}.trx', 1)
    if platform == 'windows' and not args.integration_only:
        reports = {
            'windows-installer-result.json': {'installed', 'administratorLogin', 'reinstallPreservesData', 'coldSnapshotRoundTrip'},
            'windows-snapshot-result.json': {'coldBackup', 'checksumVerification', 'startupFailureRollback', 'restoredDatabase'},
            'windows-upgrade-result.json': {'baselinePreview8', 'schema5To6', 'fullProgramInventory', 'fullProgramAndDataRollback', 'failedRollbackRestoresBothTrees', 'migrationFailureRecovery', 'stoppedServicesDiagnosedReadOnly'},
        }
        if args.snapshot_only:
            del reports['windows-upgrade-result.json']
            reports['windows-snapshot-result.json'].update({'newHostFreshTargetGuard', 'newHostRotatesKeys'})
        for name, required in reports.items():
            report = json.loads((root / name).read_text(encoding='utf-8-sig'))
            if not required <= report.keys() or not all(value is True for value in report.values()):
                raise RuntimeError(f'{name}: required native Windows checks did not pass')
            print(name, len(report), 'native checks passed')


if __name__ == '__main__': main()
