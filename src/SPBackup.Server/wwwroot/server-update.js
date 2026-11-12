(() => {
    const panel = document.querySelector('[data-server-update]');
    if (!panel || !['checking', 'queued', 'updating'].includes(panel.dataset.updatePhase)) return;
    const message = panel.querySelector('[data-update-message]');
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
            message.textContent = state.message;
        } catch {
            message.textContent = 'Связь с панелью временно недоступна. Системная задача работает отдельно; ожидаем подключения. Результат обновления пока не подтверждён.';
        }
        window.setTimeout(poll, 3000);
    };
    window.setTimeout(poll, 1500);
})();
