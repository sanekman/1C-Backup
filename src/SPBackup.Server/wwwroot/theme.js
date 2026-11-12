(() => {
    const root = document.documentElement;
    const key = 'onecbackup.server.theme:' + new URL(document.baseURI).pathname;
    const system = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    let selected = null;
    try {
        const stored = localStorage.getItem(key);
        if (stored === 'light' || stored === 'dark') selected = stored;
    } catch { /* Theme switching also works when browser storage is unavailable. */ }
    function apply(theme) {
        root.dataset.theme = theme;
        document.querySelectorAll('[data-theme-toggle]').forEach(button => {
            button.hidden = false;
            button.textContent = theme === 'dark' ? 'Светлая тема' : 'Тёмная тема';
            button.title = theme === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему';
            button.setAttribute('aria-label', button.title);
        });
    }
    const current = () => selected || (system?.matches ? 'dark' : 'light');
    apply(current()); // Runs before the stylesheet to avoid a flash of the other theme.
    document.addEventListener('DOMContentLoaded', () => apply(current()));
    document.addEventListener('click', event => {
        if (!(event.target instanceof Element) || !event.target.closest('[data-theme-toggle]')) return;
        selected = root.dataset.theme === 'dark' ? 'light' : 'dark';
        try { localStorage.setItem(key, selected); } catch { }
        apply(selected);
    });
    system?.addEventListener('change', () => { if (!selected) apply(current()); });
})();
