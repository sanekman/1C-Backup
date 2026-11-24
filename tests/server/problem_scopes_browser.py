"""Follow the two problem links for a shared job/source ID on the isolated HTTPS fixture."""
import argparse
from pathlib import Path
from urllib.parse import urlsplit

from playwright.sync_api import expect, sync_playwright

parser = argparse.ArgumentParser(description=__doc__)
for name in ('url', 'password-file', 'agent', 'task'):
    parser.add_argument('--' + name, required=True)
args = parser.parse_args()
assert urlsplit(args.url).hostname in ('127.0.0.1', 'localhost')

with sync_playwright() as playwright:
    with playwright.chromium.launch(executable_path='/usr/bin/chromium') as browser:
        page = browser.new_page(ignore_https_errors=True, viewport={'width': 1366, 'height': 900})
        page.goto(args.url + 'login')
        page.locator('input[name="name"]').fill('administrator')
        page.locator('input[name="password"]').fill(Path(args.password_file).read_text().strip())
        page.get_by_role('button', name='Войти', exact=True).click()
        page.wait_for_url(args.url)
        problems = args.url + 'problems?agent=' + args.agent
        for group, link, prefix in [
            ('Local backup', 'Открыть задание', 'job'),
            ('External SQL observation', 'Открыть наблюдение', 'observation'),
        ]:
            page.goto(problems)
            page.locator('details > summary').filter(has_text=group).click()
            page.get_by_role('link', name=link, exact=True).click()
            anchor = f'{prefix}-{args.task}'
            page.wait_for_url(args.url + 'agents/' + args.agent + '#' + anchor)
            expect(page.locator('#' + anchor)).to_be_in_viewport()
            expect(page.locator('#' + anchor)).to_contain_text(group)
