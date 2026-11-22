(() => {
    const dashboard = document.querySelector('[data-agent-directory]');
    const dialog = document.querySelector('#dashboard-action');
    if (!dashboard || !dialog) return;

    const compact = dashboard.dataset.dashboardCompact === 'true';
    const agentIndex = dashboard.querySelector('[data-agent-index]');
    const agentsByFolder = new Map();
    if (agentIndex) for (const agent of JSON.parse(agentIndex.textContent)) {
        const key = agent.folder || 'root';
        if (!agentsByFolder.has(key)) agentsByFolder.set(key, []);
        agentsByFolder.get(key).push(agent);
    }

    const search = dashboard.querySelector('[data-agent-search]');
    const attention = dashboard.querySelector('[data-agent-attention]');
    const reset = dashboard.querySelector('[data-agent-reset]');
    const paths = [...dashboard.querySelectorAll('[data-folder-key]')];
    const folders = new Map(paths.map(link => [link.dataset.folderKey, link]));
    const branches = [...dashboard.querySelectorAll('[data-agent-branch]')].reverse();
    const tableRows = [...dashboard.querySelectorAll('[data-dashboard-row]')];
    const leaves = dashboard.querySelectorAll('[data-agent-leaf]');
    const refreshLink = dashboard.querySelector('[data-dashboard-refresh]');
    const refreshStatus = dashboard.querySelector('[data-dashboard-refresh-status]');
    const matches = new Set();
    const storageKey = 'sp-backup-dashboard:' + new URL(document.baseURI).pathname + (compact ? ':compact' : '');
    let saved = {};
    try { saved = JSON.parse(sessionStorage.getItem(storageKey) ?? '{}') ?? {}; } catch { /* Optional state. */ }

    const expanded = new Set(Array.isArray(saved.open) ? saved.open : []);
    const requestedBranch = new URL(location.href).searchParams.get('branch') ?? saved.branch;
    let selected = folders.get(requestedBranch) ?? folders.get('all');
    let dragged = null;
    let refreshing = false;
    if (!compact) {
        search.value = typeof saved.query === 'string' ? saved.query : '';
        attention.checked = saved.attention === true;
    }

    const appliedFilter = search.value.trim() !== '' || attention.checked;
    const filtered = () => compact ? appliedFilter : search.value.trim() !== '' || attention.checked;
    const save = () => {
        try {
            sessionStorage.setItem(storageKey, JSON.stringify({
                branch: selected.dataset.folderKey, open: [...expanded], query: search.value, attention: attention.checked
            }));
        } catch { /* State is optional. */ }
    };
    if (compact || !filtered()) for (const branch of branches) {
        if (branch.firstElementChild.open) expanded.add(branch.firstElementChild.dataset.branchKey);
    }

    function belongs(row, folderKey) {
        if (folderKey === 'all') return true;
        for (let key = row.dataset.folderId || 'root'; key !== 'all'; key = folders.get(key)?.dataset.folderParent ?? 'all') {
            if (key === folderKey) return true;
        }
        return false;
    }

    function updateFolderActions() {
        dashboard.querySelectorAll('[data-folder-op]:not([data-folder-op="create"])')
            .forEach(button => { button.hidden = button.disabled = !selected.dataset.folderId; });
    }

    function render() {
        if (compact) {
            updateFolderActions();
            return;
        }
        const query = search.value.trim().normalize().toLocaleLowerCase('ru');
        const filtering = filtered();
        const key = selected.dataset.folderKey;
        matches.clear();
        let shown = 0;
        for (const row of tableRows) {
            const text = [row.dataset.agentName, row.dataset.computerName].join(' ').normalize().toLocaleLowerCase('ru');
            const match = (!attention.checked || row.dataset.attention === 'yes') && text.includes(query);
            if (match) matches.add(row.dataset.agentId);
            row.hidden = !(match && (filtering || belongs(row, key)));
            if (!row.hidden) shown++;
        }
        for (const leaf of leaves) leaf.hidden = filtering && !matches.has(leaf.dataset.agentId);
        for (const branch of branches) {
            const details = branch.firstElementChild;
            if (filtering) {
                branch.hidden = !branch.querySelector('[data-agent-leaf]:not([hidden])');
                details.open = !branch.hidden;
            } else {
                branch.hidden = false;
                details.open = expanded.has(details.dataset.branchKey);
            }
        }
        for (const link of paths) {
            if (link === selected) link.setAttribute('aria-current', 'true');
            else link.removeAttribute('aria-current');
        }
        const title = dashboard.querySelector('[data-dashboard-title]');
        title.textContent = filtering ? 'Результаты по всем папкам' : selected.dataset.folderPath;
        const count = dashboard.querySelector('[data-dashboard-count]');
        const countText = `${shown} из ${tableRows.length}`;
        if (count.textContent !== countText) count.textContent = countText;
        dashboard.querySelector('[data-agent-no-match]').hidden = shown !== 0;
        reset.disabled = !filtering;
        updateFolderActions();
        save();
    }

    function choose(link, event) {
        event.preventDefault();
        selected = link;
        const url = new URL(location.href);
        url.searchParams.set('branch', selected.dataset.folderKey);
        history.replaceState(null, '', url);
        if (filtered()) {
            search.value = '';
            attention.checked = false;
        }
        render();
    }

    const form = dialog.querySelector('form');
    const field = name => form.elements.namedItem('Change.' + name);
    const message = dialog.querySelector('[data-dialog-message]');
    const title = dialog.querySelector('h2');
    const submit = dialog.querySelector('[data-dialog-submit]');

    function open(action, row = null, dropTarget = null) {
        form.reset();
        const creates = action === 'create';
        const renames = action === 'rename';
        const movesAgent = action === 'agent-move';
        const removes = action === 'delete';
        const needsName = creates || renames;
        const chooseTarget = (movesAgent && !dropTarget) || action === 'folder-move';
        field('Kind').value = action;
        field('Id').value = movesAgent ? row.dataset.agentId : selected.dataset.folderId || '';
        field('Previous').value = movesAgent ? row.dataset.folderId || '' : '';
        field('Target').value = creates ? selected.dataset.folderId || ''
            : dropTarget ? dropTarget.dataset.folderId || '' : '';
        field('Name').value = renames ? selected.dataset.folderName : '';
        field('Name').required = needsName;
        dialog.querySelector('[data-dialog-name]').hidden = !needsName;
        dialog.querySelector('[data-dialog-target]').hidden = !chooseTarget;
        title.textContent = creates ? 'Новая папка' : renames ? 'Переименовать папку'
            : removes ? 'Удалить папку' : movesAgent ? 'Переместить агента' : 'Переместить папку';
        message.textContent = movesAgent
            ? row.dataset.agentName + ' · ' + row.dataset.folderPath + (dropTarget ? ' → ' + dropTarget.dataset.folderPath : '')
            : creates ? 'Родительская папка · ' + (selected.dataset.folderId ? selected.dataset.folderPath : 'Верхний уровень')
                : selected.dataset.folderPath;
        submit.textContent = creates ? 'Создать' : renames ? 'Сохранить' : removes ? 'Удалить' : 'Переместить';
        dialog.showModal();
        if (needsName) field('Name').select();
        else if (chooseTarget) field('Target').focus();
        else dialog.querySelector('[data-dialog-cancel]').focus();
    }

    function bindCompactBranch(details) {
        const key = details.dataset.branchKey;
        const folder = folders.get(key);
        const list = details.querySelector(':scope > ul');
        const agents = (agentsByFolder.get(key) ?? []).filter(agent => !appliedFilter || agent.matches);
        let offset = 0;
        const more = document.createElement('li');
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'dashboard-text-button';
        button.textContent = 'Показать ещё агентов';
        more.append(button);
        function appendAgents() {
            const page = agents.slice(offset, offset + 50);
            let firstLink;
            for (const agent of page) {
                const leaf = document.createElement('li');
                leaf.className = 'dashboard-leaf';
                leaf.dataset.agentLeaf = '';
                leaf.dataset.agentId = agent.id;
                leaf.dataset.folderId = agent.folder || '';
                leaf.dataset.folderPath = folder.dataset.folderPath;
                leaf.dataset.agentName = agent.name;
                leaf.draggable = true;
                const link = document.createElement('a');
                const url = new URL(location.href);
                url.searchParams.set('agent', agent.id);
                url.hash = 'agent-summary';
                link.href = url;
                link.textContent = agent.name;
                if (agent.id === new URL(location.href).searchParams.get('agent')) link.setAttribute('aria-current', 'page');
                leaf.append(link);
                list.insertBefore(leaf, more.parentElement ? more : null);
                firstLink ??= link;
            }
            offset += page.length;
            if (offset < agents.length) list.append(more);
            else more.remove();
            return firstLink;
        }
        button.addEventListener('click', () => appendAgents()?.focus());
        const open = () => {
            if (details.open) {
                expanded.add(key);
                if (offset === 0) appendAgents();
            } else expanded.delete(key);
            save();
        };
        details.open = expanded.has(key);
        if (details.open) open();
        details.addEventListener('toggle', open);
    }

    for (const branch of branches) {
        const details = branch.firstElementChild;
        if (compact) {
            bindCompactBranch(details);
            continue;
        }
        details.firstElementChild.addEventListener('click', event => {
            if (event.defaultPrevented || filtered()) return;
            if (details.open) expanded.delete(details.dataset.branchKey);
            else expanded.add(details.dataset.branchKey);
            save();
        });
    }
    for (const link of paths) {
        if (!compact) link.addEventListener('click', event => choose(link, event));
        if (link.dataset.folderKey === 'all') continue;
        const details = link.closest('details');
        let opening;
        link.addEventListener('dragover', event => {
            if (!dragged || (dragged.dataset.folderId || '') === (link.dataset.folderId || '')) return;
            event.preventDefault();
            event.dataTransfer.dropEffect = 'move';
            link.classList.add('dashboard-drop');
            if (details && !details.open && !opening) {
                opening = setTimeout(() => { details.open = true; }, 550);
            }
        });
        link.addEventListener('dragleave', () => {
            link.classList.remove('dashboard-drop');
            clearTimeout(opening);
            opening = null;
        });
        link.addEventListener('drop', event => {
            clearTimeout(opening);
            opening = null;
            link.classList.remove('dashboard-drop');
            if (!dragged || (dragged.dataset.folderId || '') === (link.dataset.folderId || '')) return;
            event.preventDefault();
            open('agent-move', dragged, link);
            dragged = null;
        });
    }
    dashboard.addEventListener('dragstart', event => {
        const row = event.target.closest('[data-agent-leaf], [data-dashboard-row]');
        if (!row) return;
        dragged = row;
        event.dataTransfer.setData('text/plain', row.dataset.agentId);
        event.dataTransfer.effectAllowed = 'move';
        for (const branch of branches) branch.hidden = false;
    });
    dashboard.addEventListener('dragend', () => {
        dragged = null;
        for (const link of paths) link.classList.remove('dashboard-drop');
        render();
    });
    dashboard.addEventListener('click', event => {
        const folderAction = event.target.closest('[data-folder-op]');
        if (folderAction) open(folderAction.dataset.folderOp);
        const move = event.target.closest('[data-agent-move]');
        if (move) open('agent-move', move.closest('[data-dashboard-row]'));
    });

    function replaceContent(target, incoming) {
        if (!target || !incoming || target.contains(document.activeElement)) return;
        const disclosures = new Map([...target.querySelectorAll('[data-dashboard-disclosure]')]
            .map(item => [item.dataset.dashboardDisclosure, item.open]));
        for (const item of incoming.querySelectorAll('[data-dashboard-disclosure]')) {
            if (disclosures.has(item.dataset.dashboardDisclosure)) item.open = disclosures.get(item.dataset.dashboardDisclosure);
        }
        if (target.innerHTML !== incoming.innerHTML) target.replaceChildren(...incoming.childNodes);
    }

    function showRefreshStatus(message) {
        if (refreshStatus.textContent !== message) refreshStatus.textContent = message;
        refreshStatus.hidden = message === '';
    }

    async function refresh(manual = false) {
        if (refreshing || dragged || (!manual && document.hidden)) return;
        refreshing = true;
        const url = location.href;
        try {
            const response = await fetch(url, { cache: 'no-store', signal: AbortSignal.timeout(10000) });
            if (dragged || url !== location.href) return;
            if (response.redirected && manual && !dialog.open) {
                location.reload();
                return;
            }
            if (!response.ok || response.redirected) throw new Error('Dashboard unavailable');
            const incoming = new DOMParser().parseFromString(await response.text(), 'text/html');
            if (dragged || url !== location.href) return;
            const next = incoming.querySelector('[data-agent-directory]');
            if (!next) throw new Error('Dashboard missing');
            const nextRows = new Map([...next.querySelectorAll('[data-dashboard-row]')].map(row => [row.dataset.agentId, row]));
            const nextFolders = new Map([...next.querySelectorAll('[data-folder-key]')].map(link => [link.dataset.folderKey, link]));
            const structureChanged = (compact && agentIndex.textContent !== next.querySelector('[data-agent-index]')?.textContent)
                || nextRows.size !== tableRows.length || nextFolders.size !== folders.size
                || tableRows.some(row => {
                    const other = nextRows.get(row.dataset.agentId);
                    return !other || ['folderId', 'agentName', 'computerName'].some(key => row.dataset[key] !== other.dataset[key]);
                })
                || paths.some(link => {
                    const other = nextFolders.get(link.dataset.folderKey);
                    return !other || ['folderParent', 'folderPath'].some(key => link.dataset[key] !== other.dataset[key]);
                });
            if (structureChanged && manual && !dialog.open) {
                location.reload();
                return;
            }

            for (const row of tableRows) {
                const other = nextRows.get(row.dataset.agentId);
                if (!other) continue;
                row.dataset.attention = other.dataset.attention;
                const cells = other.querySelectorAll('[data-dashboard-live]');
                row.querySelectorAll('[data-dashboard-live]').forEach((cell, index) => replaceContent(cell, cells[index]));
            }
            for (const selector of ['[data-dashboard-active]', '[data-dashboard-totals]', '#agent-summary'])
                replaceContent(document.querySelector(selector), incoming.querySelector(selector));
            render();
            if (compact) replaceContent(dashboard.querySelector('[data-dashboard-count]'), next.querySelector('[data-dashboard-count]'));
            showRefreshStatus(structureChanged
                ? compact ? 'Список агентов изменился. Нажмите «Обновить».' : 'Состав дерева изменился. Нажмите «Обновить».'
                : '');
        } catch {
            showRefreshStatus('Не удалось обновить данные.');
        } finally {
            refreshing = false;
        }
    }

    refreshLink.addEventListener('click', event => {
        event.preventDefault();
        void refresh(true);
    });
    setInterval(refresh, 15000);
    dialog.querySelector('[data-dialog-cancel]').addEventListener('click', () => dialog.close());
    if (!compact) {
        search.addEventListener('input', render);
        attention.addEventListener('change', render);
    }
    reset?.addEventListener('click', () => {
        search.value = '';
        attention.checked = false;
        render();
        search.focus();
    });
    dashboard.querySelectorAll('[data-agent-filter]').forEach(element => { element.hidden = false; });
    render();
})();
