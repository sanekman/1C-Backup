"""Exercise the read-only notification preview on the isolated HTTPS fixture."""
import sys
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from playwright.sync_api import expect, sync_playwright

base, password_file, folder, agent = sys.argv[1:]
assert urlsplit(base).hostname in ('localhost', '127.0.0.1')

with sync_playwright() as playwright:
    with playwright.chromium.launch(executable_path='/usr/bin/chromium') as browser:
        page = browser.new_page(ignore_https_errors=True, viewport={'width': 900, 'height': 580})
        page.goto(base + 'login')
        page.locator('input[name="name"]').fill('administrator')
        page.locator('input[name="password"]').fill(Path(password_file).read_text().strip())
        page.get_by_role('button', name='Войти', exact=True).click()
        page.wait_for_url(base)
        page.get_by_role('link', name='Настройки', exact=True).click()
        page.get_by_role('link', name='Предпросмотр правил оповещений').click()
        page.get_by_label('Папка').select_option(folder)
        page.get_by_label('Причина').select_option('AccessRequired')
        page.get_by_role('button', name='Показать совпадения').click()
        expect(page.get_by_role('status')).to_contain_text('Совпадений: 1')
        page.get_by_role('link', name='Current agent').click()
        assert parse_qs(urlsplit(page.url).query)['agent'] == [agent]
        expect(page.get_by_role('status')).to_contain_text('Совпадений: 1')
        page.get_by_role('link', name='Снять фильтр по агенту').click()
        assert 'agent' not in parse_qs(urlsplit(page.url).query)
        assert page.get_by_label('Папка').input_value() == folder
        assert page.get_by_label('Причина').input_value() == 'AccessRequired'
        assert page.evaluate('document.documentElement.scrollWidth === innerWidth')
