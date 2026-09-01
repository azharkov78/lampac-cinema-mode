(function () {
    'use strict';

    var connect_host = '{localhost}';
    var state = window.cinema_mode_state || { installed: false, active: false, runId: 0 };
    window.cinema_mode_state = state;

    function notify(message) {
        if (Lampa.Noty && typeof Lampa.Noty.show === 'function') {
            Lampa.Noty.show(message);
        }
    }

    function fetchJson(url, done, fail) {
        try {
            if (!Lampa.Reguest) return fail(new Error('Lampa.Reguest unavailable'));
            var network = new Lampa.Reguest();
            network.timeout(30000);
            network.native(url, function (data) {
                try { done(typeof data === 'string' ? JSON.parse(data) : data); }
                catch (error) { fail(error); }
            }, fail, false, { dataType: 'text' });
        } catch (error) {
            fail(error);
        }
    }

    function isCinemaTrailer(options) {
        var url = options && options.url ? String(options.url) : '';
        return url.indexOf('/trailers/cinemamode/') !== -1;
    }

    function isPlayableMovie(options) {
        return !!(options && typeof options === 'object' && options.url
            && !isCinemaTrailer(options));
    }

    function normalizeTrailers(data) {
        if (!Array.isArray(data)) return [];
        return data.map(function (item) {
            if (typeof item === 'string') return { url: item, title: '' };
            return item && item.url ? { url: String(item.url), title: String(item.title || '') } : null;
        }).filter(Boolean);
    }

    function trailerLabel(item, index, total) {
        return item.title ? item.title + ' (' + (index + 1) + '/' + total + ')' : 'Cinema Mode — трейлер ' + (index + 1) + '/' + total;
    }

    function openOriginal(originalOpen, args) {
        state.active = false;
        state.pending = false;
        originalOpen.apply(Lampa.Player, args);
    }

    function playPlaylist(originalOpen, args, urls) {
        var movie = args[0] || {};
        var playlist = urls.map(function (item, index) {
            return { url: item.url, title: trailerLabel(item, index, urls.length), type: 'mp4', cinema_mode_bypass: true };
        });
        if (movie.url) playlist.push({ url: movie.url, title: movie.title || 'Фильм', type: movie.type || 'mp4', cinema_mode_bypass: true });
        var first = urls[0];
        var data = Object.assign({}, movie, {
            url: first.url,
            title: trailerLabel(first, 0, urls.length),
            type: 'mp4',
            playlist: playlist
        });
        state.active = false;
        state.pending = false;
        originalOpen.call(Lampa.Player, data);
    }

    function startPreRoll(originalOpen, args) {
        state.active = true;
        state.pending = true;
        var runId = ++state.runId;
        fetchJson(connect_host + '/cinemamode/random?n=3', function (data) {
            var urls = normalizeTrailers(data);
            if (runId !== state.runId) return;
            if (!urls || !urls.length) {
                notify('Cinema Mode: пул трейлеров пуст');
                openOriginal(originalOpen, args);
                return;
            }
            playPlaylist(originalOpen, args, urls);
        }, function (error) {
            if (runId !== state.runId) return;
            notify('Cinema Mode недоступен, запускаю фильм');
            openOriginal(originalOpen, args);
        });
    }

    function installPlayerHook() {
        if (!Lampa.Player || typeof Lampa.Player.play !== 'function') return;
        if (Lampa.Player.play.__cinemaModeWrapped) return;

        var originalOpen = Lampa.Player.play;
        var wrappedOpen = function () {
            var args = Array.prototype.slice.call(arguments);
            var options = args[0];

            if (options && options.cinema_mode_bypass) {
                return originalOpen.apply(Lampa.Player, args);
            }
            if (state.pending) {
                state.runId += 1;
                state.pending = false;
                state.active = false;
                return originalOpen.apply(Lampa.Player, args);
            }
            if (state.active || !isPlayableMovie(options)) {
                return originalOpen.apply(Lampa.Player, args);
            }

            startPreRoll(originalOpen, args);
        };
        wrappedOpen.__cinemaModeWrapped = true;
        wrappedOpen.__cinemaModeOriginal = originalOpen;
        Lampa.Player.play = wrappedOpen;
    }

    function manualCinemaMode() {
        fetchJson(connect_host + '/cinemamode/random?n=3', function (data) {
            var urls = normalizeTrailers(data);
            if (!urls || !urls.length) {
                notify('Cinema Mode: пул трейлеров пуст');
                return;
            }
            if (!Lampa.Player || typeof Lampa.Player.play !== 'function') {
                notify('Cinema Mode: плеер Lampa недоступен');
                return;
            }
            var play = Lampa.Player.play.__cinemaModeOriginal || Lampa.Player.play;
            var movie = { title: 'Cinema Mode', type: 'mp4', url: '' };
            playPlaylist(play, [movie], urls);
        }, function () {
            notify('Cinema Mode: ошибка получения пула');
        });
    }

    function installSettingsEntry() {
        if (!Lampa.SettingsApi || typeof Lampa.SettingsApi.addComponent !== 'function'
            || typeof Lampa.SettingsApi.addParam !== 'function') return;
        Lampa.SettingsApi.addComponent({
            component: 'cinemamode',
            name: 'Cinema Mode',
            after: 'more',
            icon: '<svg width="24" height="24" viewBox="0 0 24 24"><path d="M4 4h16v16H4z" fill="currentColor"/></svg>'
        });
        Lampa.SettingsApi.addParam({
            component: 'cinemamode',
            param: { name: 'cinema_mode_start', type: 'trigger' },
            field: { name: 'Запустить Cinema Mode', description: 'Ручной запуск трейлеров' },
            onChange: manualCinemaMode
        });
    }

    function startPlugin() {
        if (state.installed) return;
        state.installed = true;
        installPlayerHook();
        installSettingsEntry();
    }

    if (window.appready) startPlugin();
    else if (Lampa.Listener && typeof Lampa.Listener.follow === 'function') {
        Lampa.Listener.follow('app', function (event) {
            if (event.type === 'ready') startPlugin();
        });
    }
})();
