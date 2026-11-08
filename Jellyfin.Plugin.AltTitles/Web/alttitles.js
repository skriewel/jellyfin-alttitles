/* Alternative Titles – "Also known as" line on movie and series pages (seasons and episodes: their series).
 * The search itself needs no script: the plugin adds a search provider on the server. */
(function () {
    'use strict';

    var cfg = window.AltTitlesConfig || {};
    if (window.AltTitles || cfg.enabled === false) return;

    var MAX_SHOWN = 4;
    var de = /^de\b/i.test(document.documentElement.lang || navigator.language || '');
    var T = de
        ? { aka: 'Auch bekannt als', more: function (n) { return '+' + n + ' weitere'; }, less: 'weniger' }
        : { aka: 'Also known as', more: function (n) { return '+' + n + ' more'; }, less: 'less' };

    var cache = {};
    var seq = 0;

    function api() { return window.ApiClient; }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function currentItemId() {
        var m = /^#\/?details\b.*[?&]id=([0-9a-f-]{32,36})/i.exec(location.hash || '');
        return m ? m[1].replace(/-/g, '').toLowerCase() : null;
    }

    function activePage() {
        var pages = document.querySelectorAll('.itemDetailPage');
        for (var i = pages.length - 1; i >= 0; i--) {
            if (!pages[i].classList.contains('hide')) return pages[i];
        }
        return null;
    }

    function load(id) {
        if (!cache[id]) {
            var c = api();
            cache[id] = c.getJSON(c.getUrl('AltTitles/Item/' + id)).catch(function (e) { delete cache[id]; throw e; });
        }
        return cache[id];
    }

    function render(page, id, titles) {
        page.querySelectorAll('.alt-titles').forEach(function (n) { n.remove(); });
        if (!titles.length) return;
        // above the overview, in the page's main column (not in the header next to the play buttons)
        var section = page.querySelector('.detailPagePrimaryContent .detailSection');
        if (!section) return;

        var box = document.createElement('div');
        box.className = 'alt-titles';
        box.setAttribute('data-alt-item', id);
        var shown = titles.slice(0, MAX_SHOWN);
        var rest = titles.slice(MAX_SHOWN);
        var item = function (t) { return '<span class="alt-title" title="' + esc(t.source) + '">' + esc(t.title) + '</span>'; };
        box.innerHTML = '<span class="alt-label">' + esc(T.aka) + ':</span> '
            + shown.map(item).join('<span class="alt-sep"> · </span>')
            + (rest.length
                ? '<span class="alt-rest" hidden><span class="alt-sep"> · </span>' + rest.map(item).join('<span class="alt-sep"> · </span>') + '</span>'
                  + ' <button type="button" class="alt-toggle">' + esc(T.more(rest.length)) + '</button>'
                : '');
        var toggle = box.querySelector('.alt-toggle');
        if (toggle) {
            toggle.addEventListener('click', function () {
                var r = box.querySelector('.alt-rest');
                r.hidden = !r.hidden;
                toggle.textContent = r.hidden ? T.more(rest.length) : T.less;
            });
        }
        section.insertBefore(box, section.firstChild);
    }

    function update() {
        var my = ++seq;
        var id = currentItemId();
        if (!id || !api() || !api().getCurrentUserId || !api().getCurrentUserId()) return;
        var tries = 0;
        (function wait() {
            if (my !== seq) return;
            var page = activePage();
            if (!page || !page.querySelector('.detailPagePrimaryContent .detailSection')) {
                if (++tries < 80) setTimeout(wait, 100);
                return;
            }
            var existing = page.querySelector('.alt-titles');
            if (existing && existing.getAttribute('data-alt-item') === id) return;
            load(id).then(function (d) {
                if (my === seq) render(page, id, (d && d.titles) || []);
            }).catch(function (e) { console.warn('[AltTitles]', e); });
        })();
    }

    document.addEventListener('viewshow', function () { setTimeout(update, 0); }, true);
    window.addEventListener('hashchange', function () { setTimeout(update, 0); });
    setInterval(function () { cache = {}; }, 10 * 60 * 1000);
    update();

    window.AltTitles = { version: cfg.version, refresh: function () { cache = {}; update(); } };
})();
