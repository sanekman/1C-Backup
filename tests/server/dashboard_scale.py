"""Measure the Dashboard in the isolated PostgreSQL fixture."""
import json
import math
import re
import statistics
import sys
from pathlib import Path
from time import perf_counter
from urllib.parse import urlsplit

from playwright.sync_api import sync_playwright, expect

url, credentials, output, count = sys.argv[1:]
assert urlsplit(url).hostname in ('127.0.0.1', 'localhost')
with sync_playwright() as playwright:
    browser = playwright.chromium.launch(executable_path='/usr/bin/chromium', args=['--no-sandbox'])
    page = browser.new_page(ignore_https_errors=True, viewport={'width': 1366, 'height': 900})
    page.goto(url + 'login')
    page.locator('input[name="name"]').fill('administrator')
    page.locator('input[name="password"]').fill(Path(credentials).read_text().strip())
    page.get_by_role('button', name='Войти', exact=True).click()
    samples = []
    for attempt in range(9):
        started = perf_counter()
        page.goto(url + '?branch=all')
        rows = page.locator('[data-dashboard-row]')
        expect(rows).to_have_count(int(count))
        ready = perf_counter()
        rows.filter(has_text='Scale agent').first.get_by_role('link', name=re.compile(r'^Scale agent ')).click()
        expect(page.locator('#agent-summary')).to_be_visible()
        selected = perf_counter()
        if attempt:
            samples.append((1000 * (ready - started), 1000 * (selected - started)))
    def statistics_for(values):
        return {'median': round(statistics.median(values), 2), 'p95': round(sorted(values)[math.ceil(len(values) * .95) - 1], 2)}
    result = {'agents': int(count), 'dashboardDomNodes': page.locator('[data-agent-directory] *').count(),
              'rowsReadyMs': statistics_for([ready for ready, _ in samples]),
              'firstAgentSelectedMs': statistics_for([selected for _, selected in samples])}
    Path(output).write_text(json.dumps(result, indent=2))
    browser.close()
