"""Generate the offline user guide from the task-based Markdown source (stdlib only)."""
import argparse
import html
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'docs/user-guide.md'
OUTPUT = ROOT / 'src/OneCBackup.Desktop/Help/user-guide.html'
TOKEN = re.compile(r'(!?\[[^\]]+\]\([^)]+\)|`[^`]+`|\*\*[^*]+\*\*)')

def inline(text):
    parts = []; cursor = 0
    for match in TOKEN.finditer(text):
        parts.append(html.escape(text[cursor:match.start()])); token = match.group()
        if token.startswith('`'):
            parts.append('<code>' + html.escape(token[1:-1]) + '</code>')
        elif token.startswith('**'):
            parts.append('<strong>' + html.escape(token[2:-2]) + '</strong>')
        else:
            link = re.fullmatch(r'(!?)\[([^\]]+)\]\(([^)]+)\)', token)
            image, label, address = link.groups()
            if image:
                address = Path(address).name
                parts.append('<img src="' + html.escape(address, quote=True) + '" alt="' + html.escape(label, quote=True) + '">')
            elif address.startswith(('https://', 'http://', '#')):
                parts.append('<a href="' + html.escape(address, quote=True) + '">' + html.escape(label) + '</a>')
            else:
                raise ValueError('Offline guide links must be HTTPS URLs or internal anchors: ' + address)
        cursor = match.end()
    parts.append(html.escape(text[cursor:])); return ''.join(parts)

def render(source):
    blocks = []; toc = []; paragraph = []; list_kind = None
    def flush():
        nonlocal list_kind
        if paragraph:
            blocks.append('<p>' + inline(' '.join(paragraph)) + '</p>'); paragraph.clear()
        if list_kind:
            blocks.append('</' + list_kind + '>'); list_kind = None
    for line in source.splitlines():
        if not line.strip(): flush(); continue
        heading = re.match(r'^(#{1,2}) (.+)$', line)
        if heading:
            flush(); level = len(heading[1]); label = heading[2]
            if level == 2:
                anchor = 'section-' + str(len(toc) + 1); toc.append((anchor, label))
                blocks.append('<h2 id="' + anchor + '">' + inline(label) + '</h2>')
            else: blocks.append('<h1>' + inline(label) + '</h1>')
            continue
        item = re.match(r'^(?:(\d+)\. |(-) )(.+)$', line)
        if item:
            kind = 'ol' if item[1] else 'ul'
            if list_kind != kind:
                flush(); list_kind = kind; blocks.append('<' + kind + '>')
            blocks.append('<li>' + inline(item[3]) + '</li>'); continue
        if list_kind: flush()
        paragraph.append(line.strip())
    flush()
    navigation = '<nav aria-label="Содержание"><h2>Выберите свою задачу</h2><ul>' + ''.join(
        '<li><a href="#' + key + '">' + html.escape(label) + '</a></li>' for key, label in toc) + '</ul></nav>'
    body = '\n'.join(blocks)
    first_section = body.index('<h2 ')
    body = body[:first_section] + navigation + body[first_section:]
    return '''<!doctype html>
<!-- Generated from docs/user-guide.md by tools/render_user_guide.py. -->
<html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Пошаговая инструкция — 1C-Backup</title>
<style>
body{margin:0;background:#eef2f7;color:#172b4d;font:18px/1.65 system-ui,Segoe UI,sans-serif}
main{max-width:920px;margin:32px auto;padding:36px 44px;background:white;border-radius:16px}
h1{font-size:32px;line-height:1.25}h2{font-size:25px;line-height:1.35;margin-top:40px;padding-top:24px;border-top:1px solid #dbe3ef;scroll-margin-top:20px}
p,li{overflow-wrap:anywhere}li{margin:10px 0}a{color:#1559b7;text-underline-offset:3px}a:focus{outline:3px solid #1559b7;outline-offset:3px}
nav{padding:16px 24px;background:#f3f6fb;border-radius:12px}nav h2{margin:0;padding:0;border:0;font-size:22px}nav ul{padding-left:22px;columns:2;column-gap:28px}nav li{break-inside:avoid;font-size:16px}
code{background:#edf1f6;padding:2px 5px;border-radius:4px}img{display:block;max-width:100%;height:auto;border:1px solid #dbe3ef;border-radius:8px}
@media(max-width:650px){main{margin:0;padding:22px 18px;border-radius:0}nav ul{columns:1}h1{font-size:28px}}
@media print{body{background:white;font-size:12pt}main{margin:0;padding:0;max-width:none}nav ul{columns:1}h2{break-after:avoid}img{max-height:12cm;width:auto}}
</style></head><body><main>''' + body + '</main></body></html>\n'

if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--check', action='store_true'); args = parser.parse_args()
    result = render(SOURCE.read_text(encoding='utf-8'))
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding='utf-8') != result:
            raise SystemExit('Offline guide is stale. Run python tools/render_user_guide.py')
        print('Offline user guide matches Markdown source.')
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True); OUTPUT.write_text(result, encoding='utf-8'); print(OUTPUT)
