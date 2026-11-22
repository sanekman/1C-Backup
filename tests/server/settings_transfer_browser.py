"""Exercise the settings import and export using a disposable HTTPS server."""
import sys
from pathlib import Path
from urllib.parse import urlsplit

from playwright.sync_api import expect, sync_playwright

url, password_file, settings, screenshot = sys.argv[1:]
assert urlsplit(url).hostname == '127.0.0.1'

with sync_playwright() as playwright:
    browser = playwright.chromium.launch(executable_path='/usr/bin/chromium', args=['--no-sandbox'])
    page = browser.new_page(ignore_https_errors=True, viewport={'width': 900, 'height': 580})
    page.goto(url + 'login')
    page.locator('input[name="name"]').fill('administrator')
    page.locator('input[name="password"]').fill(Path(password_file).read_text())
    page.get_by_role('button', name='Войти', exact=True).click()
    page.get_by_role('link', name='Настройки', exact=True).click()
    page.get_by_role('link', name='Перенос настроек', exact=True).click()
    page.get_by_label('Файл настроек').set_input_files(settings)
    page.get_by_role('checkbox', name='Применить настройки к этой установке').check()
    page.get_by_role('button', name='Импортировать настройки').click()
    expect(page.get_by_role('status').filter(has_text='Настройки импортированы.')).to_be_visible()
    expect(page.get_by_role('link', name='Создать приглашение')).to_have_count(1)
    expect(page.get_by_text('Organisation / Department', exact=True)).to_be_visible()
    with page.expect_download() as captured:
        page.get_by_role('link', name='Скачать настройки').click()
    assert captured.value.suggested_filename == 'SP-Backup-server-settings.json'
    assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
    page.screenshot(path=screenshot, full_page=True)
    page.get_by_role('link', name='Создать приглашение').click()
    expect(page.get_by_label('Понятное имя агента')).to_have_value('Backup workstation')
    browser.close()
