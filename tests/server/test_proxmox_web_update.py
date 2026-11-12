"""Root broker accepts only fixed update operations; never touches real systemd or user data."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('web_update',Path(__file__).resolve().parents[2]/'deploy/server/create-proxmox-lxc.py')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)

@unittest.skipIf(os.name=='nt','Native Linux update broker')
class WebUpdateBrokerTests(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory();self.root=Path(self.temporary.name)
        self.home=self.root/'private';self.home.mkdir(mode=0o700)
        self.home_patch=patch.object(m,'WEB_UPDATE_HOME',self.home);self.home_patch.start()
        self.current_patch=patch.object(m,'web_update_current',return_value='0.1.0-preview.8');self.current_patch.start()
        self.service=patch.object(m,'web_update_service',return_value='active');self.systemd=self.service.start()
    def tearDown(self):
        self.service.stop();self.current_patch.stop();self.home_patch.stop();self.temporary.cleanup()
    def available(self):
        m.web_update_save({'phase':'available','latestVersion':'0.1.0-preview.9','checkedUtc':time.time()})
    def test_untrusted_payloads_cannot_choose_a_command_path_url_or_older_release(self):
        self.available()
        for request in [{'command':'shell','version':'0.1.0-preview.9'}, {'command':'start','version':'0.1.0-preview.9','url':'https://other'},
                        {'command':'status','version':'anything'}, {'command':[]}, [],
                        {'command':'start','version':'0.1.0-preview.9;id'}, {'command':'start','version':'0.1.0-preview.7'}]:
            with self.subTest(request=request):
                result=m.web_update_request(request)
                self.assertNotIn(result['phase'],['queued','updating'])
        self.systemd.assert_not_called()
    def test_check_queues_only_fixed_systemd_unit_without_stopping_server(self):
        state=m.web_update_request({'command':'check'})
        self.assertEqual('checking',state['phase'])
        self.systemd.assert_called_once_with(['start','--no-block','onec-backup-server-update-check.service'])
        self.assertEqual('checking',m.web_update_request({'command':'check'})['phase'])
        self.assertEqual(1,self.systemd.call_count)
    def test_start_requires_fresh_check_and_duplicate_click_does_not_dispatch_twice(self):
        self.assertNotEqual('queued',m.web_update_request({'command':'start','version':'0.1.0-preview.9'})['phase'])
        self.available()
        state=m.web_update_request({'command':'start','version':'0.1.0-preview.9'})
        self.assertEqual('queued',state['phase']);self.assertEqual('0.1.0-preview.9',state['targetVersion'])
        self.assertEqual(state,m.web_update_request({'command':'start','version':'0.1.0-preview.9'}))
        self.systemd.assert_called_once_with(['start','--no-block','onec-backup-server-update-run.service'])
    def test_stale_check_and_dispatch_failure_are_not_reported_as_running_or_success(self):
        m.web_update_save({'phase':'available','latestVersion':'0.1.0-preview.9','checkedUtc':time.time()-1801})
        self.assertEqual('checkRequired',m.web_update_request({'command':'start','version':'0.1.0-preview.9'})['requestError'])
        self.systemd.assert_not_called()
        self.available();self.systemd.side_effect=ValueError('private details')
        self.assertEqual('dispatchFailed',m.web_update_request({'command':'start','version':'0.1.0-preview.9'})['phase'])
    def test_dead_worker_is_unknown_and_cannot_be_started_automatically(self):
        m.web_update_save({'phase':'updating','jobId':'a'*32,'startedUtc':time.time()-30})
        self.systemd.return_value='failed'
        self.assertEqual('interrupted',m.web_update_request({'command':'status'})['phase'])
        self.assertEqual('interrupted',m.web_update_request({'command':'check'})['phase'])
        self.assertEqual(1,self.systemd.call_count)
    def test_status_does_not_export_logs_credentials_or_arbitrary_state_fields(self):
        result=m.web_update_status({'phase':'failed','Password':'private fixture','log':'private fixture','jobId':'a'*32})
        self.assertNotIn('private fixture',json.dumps(result))
        self.assertEqual({'phase','currentVersion','jobId'},set(result))
    def test_new_check_preserves_last_result_and_backup_without_exporting_private_fields(self):
        result={'phase':'updated','jobId':'a'*32,'targetVersion':'0.1.0-preview.8','finishedUtc':time.time(),
                'backupDirectory':'/var/backups/onec-backup-server/fixture','log':'private fixture'}
        m.web_update_save({'phase':'updated','lastResult':result})
        status=m.web_update_request({'command':'check'})
        self.assertEqual('checking',status['phase'])
        self.assertEqual('updated',status['lastResult']['phase'])
        self.assertEqual(result['backupDirectory'],status['lastResult']['backupDirectory'])
        self.assertNotIn('private fixture',json.dumps(status))

    def test_check_failure_does_not_leak_exception_or_retain_old_available_status(self):
        m.web_update_request({'command':'check'})
        with patch.object(m,'latest_update_version',side_effect=ValueError('private network details')):
            m.web_update_check()
        result=m.web_update_request({'command':'status'})
        self.assertEqual('checkFailed',result['phase']);self.assertNotIn('private',json.dumps(result))
    def test_checker_validates_public_manifest_before_offering_release(self):
        m.web_update_request({'command':'check'})
        def download(url,path,limit):path.write_text('{}')
        with patch.object(m,'latest_update_version',return_value='0.1.0-preview.9'),patch.object(m,'download',side_effect=download), \
             patch.object(m,'package_asset',side_effect=ValueError('bad manifest')):
            m.web_update_check()
        self.assertEqual('checkFailed',m.web_update_read()['phase'])
    def test_worker_uses_private_root_script_and_fixed_arguments_and_never_exposes_raw_logs(self):
        self.available();m.web_update_request({'command':'start','version':'0.1.0-preview.9'})
        def run(args,**kwargs):
            self.assertEqual([m.sys.executable,str(m.WEB_UPDATE_SCRIPT),'--inside-container','--update','--version','0.1.0-preview.9'],args)
            kwargs['stdout'].write('private fixture log\n');return subprocess.CompletedProcess(args,1)
        with patch.object(m.subprocess,'run',side_effect=run):m.web_update_run()
        result=m.web_update_request({'command':'status'})
        self.assertEqual('failed',result['phase']);self.assertNotIn('private fixture',json.dumps(result))
        log=next(self.home.glob('*.log'));self.assertEqual(0,log.stat().st_mode & 0o077)
    def test_worker_requires_success_exit_and_target_version_before_reporting_updated(self):
        for code,current,phase in [(0,'0.1.0-preview.9','updated'),(0,'0.1.0-preview.8','failed'),(1,'0.1.0-preview.9','failed')]:
            self.available();m.web_update_request({'command':'start','version':'0.1.0-preview.9'})
            with patch.object(m.subprocess,'run',return_value=subprocess.CompletedProcess([],code)),patch.object(m,'web_update_current',return_value=current):
                m.web_update_run()
            self.assertEqual(phase,m.web_update_read()['phase'])

if __name__=='__main__':unittest.main()
