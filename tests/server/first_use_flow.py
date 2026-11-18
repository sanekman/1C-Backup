"""First-use UI flow with a protocol-only agent fixture; no backup files are created."""
from datetime import datetime, timezone, timedelta
import uuid


def first_use(pw, page, url, out, checks):
    def now(delta=0):
        return (datetime.now(timezone.utc) + timedelta(seconds=delta)).isoformat()

    def ok(response):
        assert response.ok, f'Fixture API status {response.status}'
        return response

    suffix = uuid.uuid4().hex[:8]
    organization = 'Приёмка ' + suffix
    page.set_viewport_size({'width': 900, 'height': 580})
    page.goto(url + 'folders')
    parent = ''
    for name in [organization, 'Филиал', 'Отдел']:
        page.locator('input[name="Model.Name"]').fill(name)
        page.locator('select[name="Model.ParentId"]').select_option(parent)
        page.get_by_role('button', name='Создать папку', exact=True).click()
        path = name if not parent else path + ' / ' + name
        parent = page.locator('select[name="Model.ParentId"] option').filter(has_text=path).last.get_attribute('value')
        assert parent
    page.goto(url + 'connect')
    page.locator('input[name="Model.Name"]').fill('Первый агент ' + suffix)
    page.locator('select[name="Model.FolderId"]').select_option(parent)
    page.get_by_role('button', name='Создать приглашение', exact=True).click()
    # Never save invitation screenshots or tokens in the report.
    invitation = page.get_by_label('Код приглашения', exact=True).input_value()
    anonymous = pw.request.new_context(ignore_https_errors=True)
    agent = ok(anonymous.post(url + 'api/v1/enrollment', data={
        'invitationCode': invitation, 'agentId': str(uuid.uuid4()), 'name': 'FIRST-USE-PC'})).json()
    anonymous.dispose()
    agent_id = agent['agentId']
    caps = 'configuration-v1,strict-configuration-v1,configuration-receipts-v1,managed-execution-v1,remote-runs-v1,managed-secrets-v1,backup-age-v1'
    api = pw.request.new_context(ignore_https_errors=True, extra_http_headers={
        'Authorization': 'Bearer ' + agent['token'], 'SP-Backup-Capabilities': caps})
    base = url + 'api/v1/agents/' + agent_id

    def monitor(revision, jobs):
        ok(api.post(base + '/heartbeat', data={'agentId': agent_id, 'agentVersion': 'browser-fixture',
            'backupExecutionAvailable': True, 'configurationRevision': revision, 'sentUtc': now()}))
        ok(api.put(base + '/monitoring', data={'sentUtc': now(), 'agentVersion': 'browser-fixture',
            'jobs': jobs, 'progress': None, 'pendingRuns': 0, 'executionMode': 'Managed',
            'configurationRevision': revision, 'backupExecutionAvailable': True}))

    monitor(1, [])
    page.goto(url + '?folder=' + parent)
    assert 'Первый агент ' + suffix in page.inner_text('body')
    page.goto(url)
    page.get_by_role('searchbox', name='Найти агента').fill('Первый агент ' + suffix)
    leaf = page.locator(f'[data-agent-id="{agent_id}"]')
    assert leaf.is_visible()
    page.get_by_role('button', name='Сбросить').click()
    org = page.locator('summary').filter(has_text=organization)
    if org.locator('..').get_attribute('open') is None:
        org.focus()
        page.keyboard.press('Enter')
    actions = org.locator('..').locator(':scope > .agent-folder-menu')
    actions.locator('summary').click()
    actions.get_by_role('link', name='Добавить подпапку').click()
    page.locator('input[name="Model.Name"]').fill('Архив')
    page.get_by_role('button', name='Создать папку', exact=True).click()
    created = page.locator('summary').filter(has_text='Архив').first
    created.click()
    menu = created.locator('..').locator(':scope > .agent-folder-menu')
    menu.locator('summary').click()
    menu.get_by_role('link', name='Переименовать').click()
    page.locator('input[name="Model.Name"]').fill('Архив копий')
    page.get_by_role('button', name='Сохранить папку', exact=True).click()
    assert page.locator('summary').filter(has_text='Архив копий').is_visible()
    checks.append('agent-tree-create-subfolder-and-rename')
    page.get_by_role('searchbox', name='Найти агента').fill('Первый агент ' + suffix)
    leaf.get_by_role('link', name='Переместить в папку…').click()
    page.locator('select[name="Model.FolderId"]').select_option('')
    page.get_by_role('button', name='Переместить', exact=True).click()
    assert 'Без папки' in page.locator('#agent-summary').inner_text()
    leaf.get_by_role('link', name='Переместить в папку…').click()
    page.locator('select[name="Model.FolderId"]').select_option(parent)
    page.get_by_role('button', name='Переместить', exact=True).click()
    assert 'Филиал / Отдел' in page.locator('#agent-summary').inner_text()
    page.get_by_role('button', name='Сбросить').click()
    checks.append('agent-tree-search-and-move-to-root-and-back')
    page.goto(url + 'agents/' + agent_id)
    page.get_by_role('link', name='Настроить задания на сервере', exact=True).click()
    page.get_by_role('link', name='Создать задание', exact=True).click()
    page.locator('input[name="Model.Name"]').fill('Первая тестовая копия')
    page.locator('input[name="Model.Source"]').fill('relative-path')
    page.locator('button[name="Command"][value="next"]:not([aria-hidden="true"])').click()
    assert page.locator('[role="alert"]').count() > 0
    assert ok(api.get(base + '/configuration')).json()['revision'] == 1
    page.locator('input[name="Model.Source"]').fill('C:\\SPBackup-Acceptance\\Source')
    page.locator('button[name="Command"][value="next"]:not([aria-hidden="true"])').click()
    page.get_by_role('button', name='Назад', exact=True).click()
    assert page.locator('input[name="Model.Source"]').input_value().endswith('Source')
    page.locator('button[name="Command"][value="next"]:not([aria-hidden="true"])').click()
    page.locator('select[name="Model.Schedule"]').select_option('Manual')
    page.locator('input[type="checkbox"][name="Model.Enabled"]').uncheck()
    page.locator('button[name="Command"][value="next"]:not([aria-hidden="true"])').click()
    page.locator('input[name="Model.Destination"]').fill('D:\\SPBackup-Acceptance\\Copies')
    page.get_by_role('button', name='Добавить ещё одну папку копий', exact=True).click()
    page.locator('input[name="Model.AdditionalDestinations[0].Destination"]').fill('E:\\SPBackup-Acceptance\\Copies')
    page.locator('button[name="Command"][value="next"]:not([aria-hidden="true"])').click()
    assert '4. Проверьте задание' in page.inner_text('body')
    page.get_by_role('button', name='Сохранить задание', exact=True).click()
    assert 'ещё не результат копирования' in page.inner_text('body')
    offered = ok(api.get(base + '/configuration'))
    config = offered.json()
    assert config['revision'] == 2
    job = config['jobs'][0]
    ok(api.post(base + '/configuration-receipt', data={'agentId': agent_id, 'revision': 2,
        'offerId': offered.headers['sp-backup-configuration-offer'], 'status': 'Accepted', 'reason': None, 'observedUtc': now()}))
    jobs = [{'jobId': job['id'], 'name': job['name'], 'sourceKind': 'FilesAndFolders', 'enabled': False,
             'schedule': 'Manual', 'nextRunUtc': None, 'destinationCount': 2}]
    monitor(2, jobs)
    checks.append('first-use-hierarchy-invitation-enrollment-invalid-path-back-navigation-and-revision-acceptance')
    page.goto(url + 'agents/' + agent_id + '/jobs')
    page.get_by_role('link', name='Запустить…', exact=True).click()
    page.get_by_role('button', name='Отправить команду запуска', exact=True).click()
    assert 'ещё не подтверждение запуска' in page.inner_text('body')
    page.get_by_role('link', name='Состояние и история команд', exact=True).click()
    assert 'Ожидает агента' in page.inner_text('body')
    command = ok(api.get(base + '/commands')).json()[0]
    command_id = command.get('commandId')
    assert command_id
    for status in ['Accepted', 'Running']:
        ok(api.post(base + '/commands/receipt', data={'commandId': command_id, 'status': status, 'runId': None, 'reason': None}))
    page.reload()
    assert 'Выполняется' in page.inner_text('body')
    # These reports are synthetic fixture input; no copy or restore is performed.
    run_id = str(uuid.uuid4())
    ok(api.post(base + '/runs', data={'runId': run_id, 'jobId': job['id'], 'jobName': job['name'],
        'startedUtc': now(-10), 'finishedUtc': now(-5), 'status': 'PartiallySucceeded', 'warningCount': 0,
        'incomplete': False, 'copyKind': 'Full', 'configurationRevision': 2, 'destinations': [
            {'index': 0, 'succeeded': True, 'bytes': 100, 'incomplete': False},
            {'index': 1, 'succeeded': False, 'bytes': 0, 'incomplete': False}],
        'issues': [{'code': 'NetworkUnavailable', 'severity': 'Error', 'stage': 'Storage', 'destinationIndex': 1}]}))
    ok(api.post(base + '/commands/receipt', data={'commandId': command_id, 'status': 'Failed', 'runId': run_id, 'reason': None}))
    page.goto(url + 'agents/' + agent_id)
    assert 'Частичная доставка' in page.inner_text('body')
    page.locator('summary').filter(has_text='Первая тестовая копия').click()
    assert 'Хранилище 1' in page.inner_text('body') and 'Хранилище 2' in page.inner_text('body')
    page.screenshot(path=str(out / 'first-use-partial-delivery.png'), full_page=True)
    checks.append('first-use-queued-running-and-partial-result-with-two-destinations-protocol-fixture')
    ok(api.post(base + '/runs', data={'runId': str(uuid.uuid4()), 'jobId': job['id'], 'jobName': job['name'],
        'startedUtc': now(-3), 'finishedUtc': now(-1), 'status': 'Succeeded', 'warningCount': 0,
        'incomplete': False, 'copyKind': 'Full', 'configurationRevision': 2, 'destinations': [
            {'index': 0, 'succeeded': True, 'bytes': 100, 'incomplete': False},
            {'index': 1, 'succeeded': True, 'bytes': 100, 'incomplete': False}]}))
    monitor(2, jobs)
    page.goto(url + '?view=list')
    row = page.locator(f'tr[data-agent="{agent_id}"]')
    assert 'По отчёту проблем не найдено.' in row.inner_text()
    page.goto(url + 'agents/' + agent_id)
    assert page.locator('summary').filter(has_text='Частичная доставка').count() == 1
    assert page.locator('summary').filter(has_text='Успешно').count() == 1
    page.screenshot(path=str(out / 'first-use-recovered-history.png'), full_page=True)
    checks.append('first-use-new-success-clears-summary-but-retains-partial-history-protocol-fixture')
    api.dispose()
