(() => {
    const dashboard = document.querySelector('[data-agent-directory]');
    const dialog = document.querySelector('#dashboard-action');
    if (!dashboard || !dialog) return;

    const search = dashboard.querySelector('[data-agent-search]');
    const attention = dashboard.querySelector('[data-agent-attention]');
    const reset = dashboard.querySelector('[data-agent-reset]');
    const paths = [...dashboard.querySelectorAll('[data-folder-key]')];
    const folders = new Map(paths.map(link => [link.dataset.folderKey, link]));
    const branches = [...dashboard.querySelectorAll('[data-agent-branch]')].reverse();
    const tableRows = [...dashboard.querySelectorAll('[data-dashboard-row]')];
    const leaves = [...dashboard.querySelectorAll('[data-agent-leaf]')];
    const matches = new Set();
    const storageKey = 'sp-backup-dashboard:' + new URL(document.baseURI).pathname;
    let saved = {};
    try { saved = JSON.parse(sessionStorage.getItem(storageKey) ?? '{}') ?? {}; } catch { /* Optional state. */ }

    const expanded = new Set(Array.isArray(saved.open) ? saved.open : []);
    const requestedBranch = new URL(location.href).searchParams.get('branch') ?? saved.branch;
    let selected = folders.get(requestedBranch) ?? folders.get('all');
    let dragged = null;
    search.value = typeof saved.query === 'string' ? saved.query : '';
    attention.checked = saved.attention === true;

    const filtered = () => search.value.trim() !== '' || attention.checked;
    const save = () => {
        try {
            sessionStorage.setItem(storageKey, JSON.stringify({
                branch: selected.dataset.folderKey, open: [...expanded], query: search.value, attention: attention.checked
            }));
        } catch { /* State is optional. */ }
    };
    if (!filtered()) for (const branch of branches) {
        if (branch.firstElementChild.open) expanded.add(branch.firstElementChild.dataset.branchKey);
    }

    function belongs(row, folderKey) {
        if (folderKey === 'all') return true;
        for (let key = row.dataset.folderId || 'root'; key !== 'all'; key = folders.get(key)?.dataset.folderParent ?? 'all') {
            if (key === folderKey) return true;
        }
        return false;
    }

    function render() {
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
        dashboard.querySelector('[data-dashboard-count]').textContent = `${shown} из ${tableRows.length}`;
        dashboard.querySelector('[data-agent-no-match]').hidden = shown !== 0;
        reset.disabled = !filtering;
        dashboard.querySelectorAll('[data-folder-op]:not([data-folder-op="create"])')
            .forEach(button => { button.hidden = button.disabled = !selected.dataset.folderId; });
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

    for (const branch of branches) {
        const details = branch.firstElementChild;
        details.firstElementChild.addEventListener('click', event => {
            if (event.defaultPrevented || filtered()) return;
            if (details.open) expanded.delete(details.dataset.branchKey);
            else expanded.add(details.dataset.branchKey);
            save();
        });
    }
    for (const link of paths) {
        link.addEventListener('click', event => choose(link, event));
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
    for (const row of [...leaves, ...tableRows]) {
        row.addEventListener('dragstart', event => {
            dragged = row;
            event.dataTransfer.setData('text/plain', row.dataset.agentId);
            event.dataTransfer.effectAllowed = 'move';
            for (const branch of branches) branch.hidden = false;
        });
        row.addEventListener('dragend', () => {
            dragged = null;
            for (const link of paths) link.classList.remove('dashboard-drop');
            render();
        });
    }
    dashboard.addEventListener('click', event => {
        const folderAction = event.target.closest('[data-folder-op]');
        if (folderAction) open(folderAction.dataset.folderOp);
        const move = event.target.closest('[data-agent-move]');
        if (move) open('agent-move', move.closest('[data-dashboard-row]'));
    });
    dashboard.querySelector('[data-dashboard-refresh]').addEventListener('click', event => {
        event.preventDefault();
        location.reload();
    });
    dialog.querySelector('[data-dialog-cancel]').addEventListener('click', () => dialog.close());
    search.addEventListener('input', render);
    attention.addEventListener('change', render);
    reset.addEventListener('click', () => {
        search.value = '';
        attention.checked = false;
        render();
        search.focus();
    });
    dashboard.querySelectorAll('[data-agent-filter]').forEach(element => { element.hidden = false; });
    render();
})();
