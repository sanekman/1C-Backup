"""Compare Dashboard views against the same isolated PostgreSQL fixture."""
import json
import math
import re
import statistics
import sys
from pathlib import Path
from time import perf_counter
from urllib.parse import urlsplit

from playwright.sync_api import sync_playwright, expect

url, credentials, output, count, view = sys.argv[1:]
count = int(count)
compact = view == 'compact'
suffix = '?view=compact&branch=all' if compact else '?branch=all'
assert urlsplit(url).hostname in ('127.0.0.1', 'localhost')


def statistics_for(values):
    return {'median': round(statistics.median(values), 2),
            'p95': round(sorted(values)[math.ceil(len(values) * .95) - 1], 2)}


with sync_playwright() as playwright:
    browser = playwright.chromium.launch(executable_path='/usr/bin/chromium', args=['--no-sandbox'])
    page = browser.new_page(ignore_https_errors=True, viewport={'width': 1366, 'height': 900})
    page.goto(url + 'login')
    page.locator('input[name="name"]').fill('administrator')
    page.locator('input[name="password"]').fill(Path(credentials).read_text().strip())
    page.get_by_role('button', name='Войти', exact=True).click()
    samples = []
    for attempt in range(9):
        page.evaluate('sessionStorage.clear()')
        started = perf_counter()
        page.goto(url + suffix)
        rows = page.locator('[data-dashboard-row]')
        expect(rows).to_have_count(min(50, count) if compact else count)
        expect(page.locator('[data-dashboard-count]')).to_have_text(f'1–50 из {count}' if compact else f'{count} из {count}')
        ready = perf_counter()
        dom_nodes = page.locator('[data-agent-directory] *').count()
        rows.filter(has_text='Scale agent').first.get_by_role('link', name=re.compile(r'^Scale agent ')).click()
        expect(page.locator('#agent-summary')).to_be_visible()
        selected = perf_counter()

        page.evaluate('sessionStorage.clear()')
        page.goto(url + suffix)
        for path in ['Нагрузка', 'Нагрузка / Филиал']:
            page.locator(f'summary:has(> [data-folder-path="{path}"])').focus()
            page.keyboard.press('Space')
        leaf_summary = page.locator('summary:has(> [data-folder-path="Нагрузка / Филиал / Отдел"])')
        leaf_summary.focus()
        opening = perf_counter()
        page.keyboard.press('Space')
        expect(page.locator('[data-agent-leaf]').filter(has_text='Scale agent').first).to_be_visible()
        opened = perf_counter()
        if compact:
            expect(page.locator('[data-agent-leaf]')).to_have_count(50)

        page.evaluate('sessionStorage.clear()')
        page.goto(url + suffix)
        target = f'Scale agent {count:04d}'
        search = page.get_by_role('searchbox', name='Найти агента')
        searching = perf_counter()
        search.fill(target)
        if compact:
            search.press('Enter')
        expect(rows.filter(visible=True)).to_have_count(1)
        expect(rows.filter(visible=True)).to_contain_text(target)
        found = perf_counter()
        if attempt:
            samples.append(tuple(1000 * duration for duration in (ready-started, selected-started, opened-opening, found-searching)))

    result = {'agents': count, 'view': view, 'dashboardDomNodes': dom_nodes}
    for key, values in zip(('rowsReadyMs', 'firstAgentSelectedMs', 'folderOpenedMs', 'globalSearchMs'), zip(*samples)):
        result[key] = statistics_for(values)
    output = Path(output)
    output.write_text(json.dumps(result, indent=2))

    if compact:
        page.goto(url + suffix)
        next_page = page.get_by_role('link', name='Следующая страница агентов')
        next_page.focus()
        next_page.press('Enter')
        expect(page.locator('[data-dashboard-count]')).to_have_text(f'51–100 из {count}')
        page.get_by_role('link', name='Предыдущая страница агентов').click()
        expect(page.locator('[data-dashboard-count]')).to_have_text(f'1–50 из {count}')

    if count == 1000:
        page.set_viewport_size({'width': 900, 'height': 580})
        for theme in ['dark', 'light']:
            page.evaluate('sessionStorage.clear()')
            page.goto(url + suffix)
            if page.locator('html').get_attribute('data-theme') != theme:
                page.locator('[data-theme-toggle]').click()
            expect(page.locator('html')).to_have_attribute('data-theme', theme)
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
            page.screenshot(path=output.with_name(f'{view}-{theme}-900.png'))
            page.locator('[data-dashboard-row] td:first-child a').first.evaluate(
                "(link) => link.textContent = 'ДлинноеИмяКомпьютера'.repeat(6)")
            page.locator('[data-folder-name="Нагрузка"]').evaluate(
                "(link) => link.firstChild.textContent = 'ДлинноеНазваниеПапки'.repeat(6)")
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
    browser.close()
