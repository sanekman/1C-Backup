(() => {
    const directory = document.querySelector('[data-agent-directory]');
    const filters = document.querySelector('[data-agent-filter]');
    if (!directory || !filters) return;

    const search = filters.querySelector('[data-agent-search]');
    const attention = filters.querySelector('[data-agent-attention]');
    const reset = filters.querySelector('[data-agent-reset]');
    const noMatch = directory.querySelector('[data-agent-no-match]');
    const branches = [...directory.querySelectorAll('[data-agent-branch]')].map(row => {
        const details = row.firstElementChild;
        return { row, details, count: details.querySelector('[data-agent-count]') };
    }).reverse();
    const leaves = [...directory.querySelectorAll('[data-agent-leaf]')].map(row => {
        const link = row.firstElementChild;
        return { row, name: [link.dataset.agentName, link.dataset.computerName].join(' ').normalize().toLocaleLowerCase('ru') };
    });
    const key = 'sp-backup-agent-directory:' + new URL(document.baseURI).pathname;
    let saved = {};
    try { saved = JSON.parse(sessionStorage.getItem(key) ?? '{}') ?? {}; } catch { /* Storage can be disabled. */ }

    search.value = typeof saved.query === 'string' ? saved.query : '';
    attention.checked = saved.attention === true;
    const filtering = () => search.value.trim() !== '' || attention.checked;
    const expanded = new Set(Array.isArray(saved.open) ? saved.open : []);
    if (!filtering()) {
        for (const { details } of branches) {
            if (details.open) expanded.add(details.dataset.branchKey);
        }
    }
    const save = () => {
        try {
            sessionStorage.setItem(key, JSON.stringify({ open: [...expanded], query: search.value, attention: attention.checked }));
        } catch { /* The tree remains usable when storage is blocked. */ }
    };

    function update() {
        const query = search.value.trim().normalize().toLocaleLowerCase('ru');
        const filtered = filtering();
        let matches = 0;
        const counts = new Map(branches.map(({ row }) => [row, 0]));
        for (const { row, name } of leaves) {
            const match = (!attention.checked || row.dataset.attention === 'yes') && name.includes(query);
            row.hidden = !match;
            if (match) {
                matches++;
                const parent = row.parentElement.closest('[data-agent-branch]');
                if (parent) counts.set(parent, counts.get(parent) + 1);
            }
        }
        for (const { row, details, count } of branches) {
            const visible = counts.get(row);
            const parent = row.parentElement.closest('[data-agent-branch]');
            if (parent) counts.set(parent, counts.get(parent) + visible);
            row.hidden = filtered && visible === 0;
            count.textContent = String(visible);
            details.open = filtered ? visible > 0 : expanded.has(details.dataset.branchKey);
        }
        noMatch.hidden = !filtered || matches !== 0;
        reset.disabled = !filtered;
        save();
    }

    // Native details/summary supplies the disclosure action for both pointer and keyboard.
    for (const { details } of branches) {
        details.firstElementChild.addEventListener('click', () => {
            if (filtering()) return;
            if (details.open) expanded.delete(details.dataset.branchKey);
            else expanded.add(details.dataset.branchKey);
            save();
        });
    }
    search.addEventListener('input', update);
    attention.addEventListener('change', update);
    reset.addEventListener('click', () => {
        search.value = '';
        attention.checked = false;
        update();
        search.focus();
    });
    filters.hidden = false;
    update();
})();
