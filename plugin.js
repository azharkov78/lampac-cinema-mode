(function () {
    'use strict';

    var connect_host = '{localhost}';
    var state = window.cinema_mode_state || { installed: false, active: false, runId: 0, playlistNextPrevious: null };
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

    function restorePlaylistAutonext() {
        if (state.playlistNextPrevious === null) return;
        if (Lampa.Storage && typeof Lampa.Storage.set === 'function') {
            Lampa.Storage.set('playlist_next', state.playlistNextPrevious, true);
        }
        state.playlistNextPrevious = null;
    }

    function enablePlaylistAutonext() {
        if (!Lampa.Storage || typeof Lampa.Storage.field !== 'function'
            || typeof Lampa.Storage.set !== 'function') return;
        if (state.playlistNextPrevious === null) {
            state.playlistNextPrevious = !!Lampa.Storage.field('playlist_next');
        }
        Lampa.Storage.set('playlist_next', true, true);
    }

    function openOriginal(originalOpen, args) {
        restorePlaylistAutonext();
        state.active = false;
        state.pending = false;
        originalOpen.apply(Lampa.Player, args);
    }

    function playPlaylist(originalOpen, args, urls) {
        var movie = args[0] || {};
        var playlist = urls.map(function (item, index) {
            return { url: item.url, title: trailerLabel(item, index, urls.length), type: 'mp4', cinema_mode_bypass: true };
        });
        if (movie.url) playlist.push({ url: movie.url, title: movie.title || 'Фильм', type: movie.type || 'mp4', cinema_mode_bypass: true, cinema_mode_movie: true });
        var first = urls[0];
        var data = Object.assign({}, movie, {
            url: first.url,
            title: trailerLabel(first, 0, urls.length),
            type: 'mp4',
            playlist: playlist
        });
        state.active = false;
        state.pending = false;
        enablePlaylistAutonext();
        originalOpen.call(Lampa.Player, data);
    }

    function startPreRoll(originalOpen, args) {
        state.active = true;
        state.pending = true;
        var runId = ++state.runId;
        fetchJson(connect_host + '/cinemamode/random', function (data) {
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
        if (Lampa.Player.listener && typeof Lampa.Player.listener.follow === 'function'
            && !state.playerListenerInstalled) {
            state.playerListenerInstalled = true;
            Lampa.Player.listener.follow('start', function (data) {
                if (data && data.cinema_mode_movie) restorePlaylistAutonext();
            });
        }
    }

    function manualCinemaMode() {
        fetchJson(connect_host + '/cinemamode/random', function (data) {
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
            icon: '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 7h18v13H3z"/><path d="m3 7 3-4 3 4 3-4 3 4 3-4 3 4"/><path d="M7 11h10M7 15h6"/></svg>'
        });
        Lampa.SettingsApi.addParam({
            component: 'cinemamode',
            param: { name: 'cinema_mode_start', type: 'button' },
            field: { name: 'Запустить трейлеры' },
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
