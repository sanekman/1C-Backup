"""First-use UI flow with a protocol-only agent fixture; no backup files are created."""
from datetime import datetime, timezone, timedelta
from urllib.parse import urlsplit
from playwright.sync_api import expect
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

    def monitor(revision, jobs, progress=None):
        ok(api.post(base + '/heartbeat', data={'agentId': agent_id, 'agentVersion': 'browser-fixture',
            'backupExecutionAvailable': True, 'configurationRevision': revision, 'sentUtc': now()}))
        ok(api.put(base + '/monitoring', data={'sentUtc': now(), 'agentVersion': 'browser-fixture',
            'jobs': jobs, 'progress': progress, 'pendingRuns': 0, 'executionMode': 'Managed',
            'configurationRevision': revision, 'backupExecutionAvailable': True}))

    monitor(1, [])
    page.goto(url + '?folder=' + parent)
    assert 'Первый агент ' + suffix in page.inner_text('body')
    page.goto(url)
    dashboard = page.locator('[data-agent-directory]')
    search = page.get_by_role('searchbox', name='Найти агента')
    search.fill('Первый агент ' + suffix)
    leaf = page.locator(f'[data-agent-leaf][data-agent-id="{agent_id}"]')
    row = page.locator(f'[data-dashboard-row][data-agent-id="{agent_id}"]')
    assert leaf.is_visible()
    assert row.is_visible()
    page.get_by_role('button', name='Сбросить').click()
    org = page.locator(f'[data-folder-name="{organization}"]')
    org.click()
    dashboard.get_by_role('button', name='Новая папка', exact=True).click()
    dialog = page.get_by_role('dialog')
    assert organization in dialog.inner_text()
    dialog.get_by_label('Имя папки').fill('Архив')
    dialog.get_by_role('button', name='Создать', exact=True).click()
    assert 'folder' not in urlsplit(page.url).path
    created = page.locator('[data-folder-name="Архив"]')
    try:
        expect(created).to_be_visible(timeout=3500)
    except AssertionError:
        raise AssertionError(f"Folder missing at {urlsplit(page.url).path}: {page.locator('[role=alert]').all_text_contents()}")
    dashboard.get_by_role('button', name='Переименовать', exact=True).click()
    dialog.get_by_label('Имя папки').fill('Архив копий')
    dialog.get_by_role('button', name='Сохранить', exact=True).click()
    created = page.locator('[data-folder-name="Архив копий"]')
    expect(created).to_be_visible()
    checks.append('dashboard-create-and-rename-subfolder-in-place')
    search.fill('Первый агент ' + suffix)
    root = page.locator('[data-folder-key="root"]')
    if not root.is_visible():
        # Search hides branches without a matching agent; clear it before dragging.
        page.get_by_role('button', name='Сбросить').click()
        page.locator('[data-folder-key="all"]').click()
    row.drag_to(root)
    assert 'Без папки' in dialog.inner_text()
    dialog.get_by_role('button', name='Отмена', exact=True).click()
    expect(row).to_have_attribute('data-folder-path', f'Приёмка {suffix} / Филиал / Отдел')
    row.drag_to(root)
    dialog.get_by_role('button', name='Переместить', exact=True).click()
    expect(row).to_have_attribute('data-folder-path', 'Без папки')
    row.get_by_role('button', name='Переместить', exact=False).click()
    dialog.get_by_label('Папка назначения').select_option(parent)
    dialog.get_by_role('button', name='Переместить', exact=True).click()
    expect(row).to_have_attribute('data-folder-path', f'Приёмка {suffix} / Филиал / Отдел')
    checks.append('dashboard-drag-cancel-confirm-and-keyboard-move-fallback')
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
    monitor(2, jobs, {'runId': str(uuid.uuid4()), 'jobId': job['id'], 'stage': 'CopyingToStorage',
                      'percent': 42, 'updatedUtc': now()})
    page.goto(url + '?branch=all')
    expect(page.locator(f'[data-dashboard-row][data-agent-id="{agent_id}"]')).to_contain_text('Доставка · 42%')
    page.screenshot(path=str(out / 'dashboard-running-900.png'))
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
    page.goto(url + '?branch=all')
    expect(page.locator(f'[data-dashboard-row][data-agent-id="{agent_id}"]')).to_contain_text('Доставлено в хранилища: 2')
    checks.append('dashboard-current-stage-and-last-completed-run-visible-in-table')
    page.goto(url + '?view=list')
    row = page.locator(f'tr[data-agent="{agent_id}"]')
    assert 'По отчёту проблем не найдено.' in row.inner_text()
    page.goto(url + 'agents/' + agent_id)
    assert page.locator('summary').filter(has_text='Частичная доставка').count() == 1
    assert page.locator('summary').filter(has_text='Успешно').count() == 1
    page.screenshot(path=str(out / 'first-use-recovered-history.png'), full_page=True)
    checks.append('first-use-new-success-clears-summary-but-retains-partial-history-protocol-fixture')
    api.dispose()
