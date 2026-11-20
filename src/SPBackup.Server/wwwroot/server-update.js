(() => {
    const panel = document.querySelector('[data-server-update]');
    if (!panel) return;
    const copyButton = panel.querySelector('[data-update-copy]');
    if (copyButton && navigator.clipboard) {
        copyButton.hidden = false;
        copyButton.addEventListener('click', async () => {
            const backup = copyButton.closest('[data-update-backup]');
            const path = backup.querySelector('[data-update-backup-path]').textContent;
            const status = backup.querySelector('[data-update-copy-status]');
            try {
                await navigator.clipboard.writeText(path);
                status.textContent = 'Путь скопирован.';
            } catch {
                status.textContent = 'Скопируйте путь вручную.';
            }
        });
    }
    let phase = panel.dataset.updatePhase;
    if (!['checking', 'queued', 'updating'].includes(phase)) return;
    const message = panel.querySelector('[data-update-message]');
    const connection = panel.querySelector('[data-update-connection]');
    const poll = async () => {
        try {
            const response = await fetch(new URL('api/v1/management/server-update', document.baseURI), {
                credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'application/json' }
            });
            if (response.status === 401) { window.location.assign(new URL('login', document.baseURI)); return; }
            if (response.redirected && new URL(response.url).pathname.endsWith('/login')) {
                window.location.assign(response.url); return;
            }
            if (!response.ok) throw new Error('unavailable');
            const state = await response.json();
            if (state.phase === 'unavailable' || state.phase === 'notConfigured') throw new Error('unavailable');
            if (!state.busy) { window.location.reload(); return; }
            phase = state.phase;
            message.textContent = state.message;
            connection.hidden = true;
        } catch {
            connection.textContent = phase === 'checking'
                ? 'Ждём связь с панелью. Последнее подтверждённое действие — поиск доступной версии.'
                : 'Ждём связь с панелью. Результат обновления пока не подтверждён.';
            connection.hidden = false;
        }
        window.setTimeout(poll, 3000);
    };
    window.setTimeout(poll, 1500);
})();
