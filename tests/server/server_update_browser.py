"""Browser acceptance against a loopback-only server and explicitly marked IPC fixture.

No real update runs: the broker fixture changes status JSON, while login/CSRF/forms
and polling use the actual ASP.NET application. Native updater acceptance is separate.
"""
import argparse
import json
from pathlib import Path
import time
import urllib.parse
from playwright.sync_api import sync_playwright


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url',required=True);parser.add_argument('--user',default='preview')
    parser.add_argument('--password-file',type=Path,required=True);parser.add_argument('--state-file',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    if urllib.parse.urlsplit(args.url).hostname not in ['localhost','127.0.0.1']:
        raise ValueError('Only isolated loopback fixtures are allowed')
    state=json.loads(args.state_file.read_text())
    if state.get('browserFixture') is not True:raise ValueError('The status file is not a browser fixture')
    args.output.mkdir(parents=True,exist_ok=True)
    checks=[]
    def save(phase,**fields):
        state.update(phase=phase,checkedUtc=time.time(),**fields)
        args.state_file.write_text(json.dumps(state))
    with sync_playwright() as playwright:
        browser=playwright.chromium.launch(executable_path='/usr/bin/chromium',headless=True,args=['--no-sandbox'])
        page=browser.new_page(viewport={'width':1366,'height':900},color_scheme='light')
        page.goto(args.url+'login');page.locator('input[name="name"]').fill(args.user)
        page.locator('input[name="password"]').fill(args.password_file.read_text().strip())
        page.locator('button[type="submit"]').click();page.wait_for_url(args.url)
        page.get_by_role('link',name='Настройки',exact=True).click()
        page.get_by_role('button',name='Обновить до 0.1.0-preview.10').wait_for()
        page.screenshot(path=str(args.output/'settings-light.png'))
        checks.append('authenticated-settings-offers-checked-release-with-submit-form')
        page.set_viewport_size({'width':360,'height':800})
        assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
        page.set_viewport_size({'width':1366,'height':900})
        checks.append('update-controls-fit-360-pixel-mobile-layout')
        page.get_by_role('button',name='Проверить обновления').click()
        page.get_by_text('Проверяем опубликованные версии…',exact=True).wait_for()
        assert page.get_by_role('button',name='Проверить обновления').is_disabled()
        save('available')
        install=page.get_by_role('button',name='Обновить до 0.1.0-preview.10');install.wait_for(timeout=12000)
        checks.append('check-status-polls-and-reloads-to-available-without-manual-refresh')
        install.focus();page.keyboard.press('Enter')
        page.get_by_text('Обновление выполняется.',exact=False).first.wait_for()
        assert page.get_by_role('button',name='Обновить до',exact=False).count()==0
        page.get_by_role('button',name='Включить тёмную тему',exact=True).click()
        page.set_viewport_size({'width':900,'height':580})
        assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
        page.screenshot(path=str(args.output/'settings-running-dark-900.png'))
        checks.append('keyboard-submit-disables-repeat-install-and-fits-dark-900x580')
        page.route('**/api/v1/management/server-update',lambda route:route.abort())
        page.get_by_text('Результат обновления пока не подтверждён.',exact=False).wait_for(timeout=12000)
        checks.append('lost-connection-is-not-reported-as-update-success-or-failure')
        page.unroute('**/api/v1/management/server-update')
        save('updated',currentVersion='0.1.0-preview.10',backupDirectory='/var/backups/onec-backup-server/20261112T000000Z-12345678')
        page.get_by_text('Сервер обновлён. Проверка готовности прошла.',exact=True).wait_for(timeout=12000)
        assert page.get_by_text('/var/backups/onec-backup-server/20261112T000000Z-12345678',exact=True).count()==1
        page.screenshot(path=str(args.output/'settings-updated-dark-900.png'))
        checks.append('reconnected-page-shows-confirmed-result-and-backup-directory')
        save('rolledBack');page.reload();page.get_by_text('Прежняя программа и данные восстановлены.',exact=False).wait_for()
        checks.append('rollback-result-is-distinct-from-success')
        save('interrupted');page.reload();page.get_by_text('Результат не подтверждён',exact=False).wait_for()
        assert page.get_by_role('button',name='Проверить обновления').count()==0
        assert page.get_by_role('button',name='Обновить до',exact=False).count()==0
        checks.append('interrupted-task-requires-operator-without-retry-button')
        browser.close()
    result={'success':True,'fixtureOnly':True,'realNativeUpdateTested':False,'checks':checks}
    (args.output/'server-update-browser-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2))
    print(json.dumps(result,ensure_ascii=False))

if __name__=='__main__':main()
