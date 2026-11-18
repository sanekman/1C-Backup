"""Browser acceptance of seeded isolated ServerFixture; never connects outside loopback."""
import argparse
import json
from pathlib import Path
from urllib.parse import urlsplit
from playwright.sync_api import sync_playwright, expect
from first_use_flow import first_use

p = argparse.ArgumentParser(description=__doc__)
for name in ['url', 'password-file', 'agent', 'job', 'output']:
    p.add_argument('--' + name, required=True)
a = p.parse_args()
assert urlsplit(a.url).hostname in ['127.0.0.1', 'localhost']
out = Path(a.output)
out.mkdir(parents=True, exist_ok=True)
checks = []
with sync_playwright() as pw:
    browser = pw.chromium.launch(executable_path='/usr/bin/chromium', args=['--no-sandbox'])
    page = browser.new_page(ignore_https_errors=True, viewport={'width': 1366, 'height': 900})
    page.goto(a.url + 'login')
    page.locator('input[name="name"]').fill('administrator')
    page.locator('input[name="password"]').fill(Path(a.password_file).read_text().strip())
    page.get_by_role('button', name='Войти', exact=True).click()
    page.wait_for_url(a.url)
    search = page.get_by_role('searchbox', name='Найти агента')
    search.fill('УНФ')
    leaf = page.locator(f'[data-agent-leaf][data-agent-id="{a.agent}"]')
    assert leaf.is_visible()
    page.get_by_role('checkbox', name='Требуют внимания').check()
    leaf.get_by_role('link', name='Office <УНФ>', exact=True).click()
    summary = page.locator('#agent-summary')
    assert 'Частичная доставка · 1 из 2' in summary.inner_text()
    assert 'Нет связи' in summary.inner_text()
    assert 'Последний отчёт' in summary.inner_text()
    assert search.input_value() == 'УНФ'
    page.get_by_role('button', name='Сбросить').click()
    assert not leaf.is_visible()
    root_summary = page.locator('details[data-branch-key="root"] > summary')
    root_summary.focus()
    page.keyboard.press('Enter')
    assert leaf.is_visible()
    assert page.locator(f'[data-dashboard-row][data-agent-id="{a.agent}"]').is_visible()
    checks.append('tree-filter-selection-restores-disclosures-and-separates-connection-and-delivery')
    for theme in ['light', 'dark']:
        if page.locator('html').get_attribute('data-theme') != theme:
            page.locator('[data-theme-toggle]').click()
        for width, height in [(900, 580), (360, 800)]:
            page.set_viewport_size({'width': width, 'height': height})
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
            page.screenshot(path=str(out / f'tree-{theme}-{width}.png'), full_page=True)
    page.goto(a.url + '?view=list')
    row = page.locator(f'tr[data-agent="{a.agent}"]')
    assert 'Нет связи' in row.inner_text()
    assert 'Отчёт:' in row.inner_text()
    assert 'Частичная доставка' in row.inner_text()
    checks.append('offline-partial-delivery-and-report-time-visible-together')
    for theme in ['light', 'dark']:
        if page.locator('html').get_attribute('data-theme') != theme:
            page.locator('[data-theme-toggle]').click()
        for width, height in [(1366, 900), (900, 580), (360, 800)]:
            page.set_viewport_size({'width': width, 'height': height})
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
            if width == 900:
                assert page.locator('.agents-main .table-scroll').evaluate('(e) => e.scrollWidth <= e.clientWidth')
            page.screenshot(path=str(out / f'overview-{theme}-{width}.png'), full_page=True)
            checks.append(f'{theme}-{width}-no-page-overflow')
    row.get_by_role('link', name='Проблемы агента', exact=False).focus()
    page.keyboard.press('Enter')
    page.wait_for_url('**/problems?agent=' + a.agent)
    assert page.locator('input[name="agent"]').input_value() == a.agent
    assert 'Найдено причин: 68' in page.inner_text('body')
    page.get_by_role('link', name='Далее →', exact=True).click()
    assert ('agent=' + a.agent) in page.url
    page.get_by_role('link', name='← Назад', exact=True).click()
    page.locator(f'a[href="agents/{a.agent}#job-{a.job}"]').first.click()
    expect(page.locator(f'[id="job-{a.job}"]')).to_be_visible()
    checks.append('keyboard-drilldown-pagination-retains-agent-and-job-anchor')
    first_use(pw, page, a.url, out, checks)
    browser.close()
(out / 'result.json').write_text(json.dumps({'success': True, 'checks': checks}, indent=2))
