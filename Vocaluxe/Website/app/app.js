/*
 * Karaoke web queue — frontend.
 *
 * Plain ES2017, no build step and no external resources: the machine this runs on is a karaoke box
 * on a LAN that may have no internet at all during an event.
 */
(function () {
    'use strict';

    var SESSION_KEY = 'karaoke.session';
    var PROFILE_KEY = 'karaoke.profile';
    var PAGE_SIZE = 40;

    var state = {
        sessionId: localStorage.getItem(SESSION_KEY) || '',
        profileId: localStorage.getItem(PROFILE_KEY) || '',
        profileName: '',
        isAdmin: false,
        profiles: [],
        songOffset: 0,
        songTotal: 0,
        songQuery: '',
        selectedSong: null,
        selectedPartner: '',
        profileQuery: '',
        partnerQuery: '',
        difficulty: 1,
        entries: []
    };

    var el = {};

    function $(id) { return document.getElementById(id); }

    function cacheElements() {
        ['view-login', 'app', 'profileList', 'newProfileName', 'newProfileBtn', 'whoName', 'whoBtn',
         'nowPlaying', 'nowSong', 'nowSingers', 'upNext', 'nextSong', 'nextSingers', 'startNextBtn',
         'queueList', 'queueEmpty', 'queueBadge', 'clearFinishedBtn', 'searchInput', 'songCount',
         'songList', 'moreBtn', 'songSheet', 'sheetTitle', 'sheetArtist', 'sheetMeta', 'partnerBox',
         'partnerLabel', 'micHint', 'partnerSearch', 'partnerList', 'signUpBtn', 'toast', 'meName',
         'difficultyPicker', 'switchProfileBtn', 'profileSearch'].forEach(function (id) {
            el[id.replace(/-([a-z])/g, function (m, c) { return c.toUpperCase(); })] = $(id);
        });
    }

    /* ------------------------------------------------------------------ HTTP */

    function api(method, path, body) {
        var opts = {
            method: method,
            headers: {'Accept': 'application/json'}
        };
        if (state.sessionId) opts.headers['session'] = state.sessionId;
        if (body !== undefined) {
            opts.headers['Content-Type'] = 'application/json';
            opts.body = JSON.stringify(body);
        }

        return fetch(path, opts).then(function (res) {
            var isJson = (res.headers.get('content-type') || '').indexOf('json') >= 0;
            return (isJson ? res.json().catch(function () { return null; }) : res.text())
                .then(function (data) {
                    if (!res.ok) {
                        // The session expired (or the game restarted). Don't leave the guest staring
                        // at an error they cannot act on — send them back to the profile list.
                        if (res.status === 401 && state.sessionId) backToLogin();

                        var msg = (data && data.error) ? data.error : ('Fehler ' + res.status);
                        var err = new Error(msg);
                        err.status = res.status;
                        throw err;
                    }
                    return data;
                });
        });
    }

    function toast(message, isError) {
        el.toast.textContent = message;
        el.toast.className = 'toast is-visible' + (isError ? ' is-error' : '');
        clearTimeout(toast._t);
        toast._t = setTimeout(function () { el.toast.className = 'toast'; }, 3200);
    }

    function escapeHtml(text) {
        return String(text === null || text === undefined ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    /* ------------------------------------------------------------------ Anmeldung */

    function loadProfiles() {
        return api('GET', '/api/profiles').then(function (profiles) {
            state.profiles = profiles || [];
            renderProfiles();
            return state.profiles;
        }).catch(function (e) {
            el.profileList.innerHTML = '<div class="loading">Keine Verbindung zum Karaoke-Rechner.</div>';
            throw e;
        });
    }

    function matches(name, query) {
        return !query || String(name || '').toLowerCase().indexOf(query.toLowerCase()) >= 0;
    }

    function renderProfiles() {
        if (!state.profiles.length) {
            el.profileList.innerHTML = '<div class="loading">Noch keine Profile — leg unten eins an.</div>';
            return;
        }

        var shown = state.profiles.filter(function (p) { return matches(p.playerName, state.profileQuery); });
        if (!shown.length) {
            el.profileList.innerHTML = '<div class="loading">Kein Profil mit diesem Namen.</div>';
            return;
        }

        el.profileList.innerHTML = shown.map(function (p) {
            return '<button class="profile-btn" data-id="' + escapeHtml(p.profileId) + '">'
                + escapeHtml(p.playerName)
                + (p.needsPassword ? '<span class="locked">mit Passwort</span>' : '')
                + '</button>';
        }).join('');
    }

    function signIn(profileId) {
        return api('POST', '/api/session', {profileId: profileId}).then(function (res) {
            setSession(res.sessionId, res.profileId);
            enterApp();
        }).catch(function (e) {
            toast(e.status === 403 ? 'Dieses Profil ist passwortgeschützt.' : e.message, true);
        });
    }

    function setSession(sessionId, profileId) {
        state.sessionId = sessionId;
        state.profileId = profileId;
        localStorage.setItem(SESSION_KEY, sessionId);
        localStorage.setItem(PROFILE_KEY, profileId);
    }

    function clearSession() {
        state.sessionId = '';
        state.profileId = '';
        localStorage.removeItem(SESSION_KEY);
        localStorage.removeItem(PROFILE_KEY);
    }

    function currentProfileName() {
        var me = state.profiles.filter(function (p) { return p.profileId === state.profileId; })[0];
        return me ? me.playerName : '–';
    }

    function enterApp() {
        $('view-login').classList.remove('is-active');
        el.app.hidden = false;
        state.profileName = currentProfileName();
        el.whoName.textContent = state.profileName;

        loadMe();
        refreshStatus();
        refreshQueue();
        searchSongs('', true);
        connectEvents();
    }

    function backToLogin() {
        clearSession();
        el.app.hidden = true;
        $('view-login').classList.add('is-active');
        state.profileQuery = '';
        el.profileSearch.value = '';
        loadProfiles();
    }

    /* ------------------------------------------------------------------ Status & Warteliste */

    var DIFFICULTY_NAMES = ['Leicht', 'Normal', 'Schwer'];

    function renderMe() {
        el.meName.textContent = state.profileName || '–';
        Array.prototype.forEach.call(el.difficultyPicker.children, function (btn) {
            btn.classList.toggle('is-active', Number(btn.dataset.difficulty) === state.difficulty);
        });
    }

    function loadMe() {
        return api('GET', '/api/session').then(function (me) {
            if (me.playerName) state.profileName = me.playerName;
            if (typeof me.difficulty === 'number') state.difficulty = me.difficulty;
            el.whoName.textContent = state.profileName;
            renderMe();
        }).catch(function () { /* ohne Session zeigt die App ohnehin den Login */ });
    }

    function setDifficulty(value) {
        var previous = state.difficulty;
        state.difficulty = value;
        renderMe();

        api('POST', '/api/me/difficulty', {difficulty: value})
            .then(function () { toast('Schwierigkeit: ' + DIFFICULTY_NAMES[value]); })
            .catch(function (e) {
                state.difficulty = previous;
                renderMe();
                toast(e.message, true);
            });
    }

    function refreshStatus() {
        return api('GET', '/api/status').then(function (s) {
            state.isAdmin = !!s.isAdmin;
            document.querySelectorAll('.admin-only').forEach(function (node) {
                node.hidden = !state.isAdmin;
            });
            renderNowNext(s.playing, s.next);
        }).catch(function () { /* Status ist unkritisch — Warteliste rendert trotzdem */ });
    }

    function refreshQueue() {
        return api('GET', '/api/queue').then(function (data) {
            state.entries = (data && data.entries) || [];
            renderQueue();
        }).catch(function (e) {
            toast(e.message, true);
        });
    }

    function singerText(entry) {
        var names = entry.singerNames || [];
        if (names.length <= 1) return names[0] || '';
        return names.join(' & ');
    }

    function songText(entry) {
        return (entry.artist ? entry.artist + ' – ' : '') + entry.title;
    }

    function renderNowNext(playing, next) {
        if (playing) {
            el.nowPlaying.hidden = false;
            el.nowSong.textContent = songText(playing);
            el.nowSingers.textContent = singerText(playing);
        } else {
            el.nowPlaying.hidden = true;
        }

        if (next) {
            el.upNext.hidden = false;
            el.nextSong.textContent = songText(next);
            el.nextSingers.textContent = singerText(next);
            el.startNextBtn.hidden = false;
            el.startNextBtn.dataset.id = next.requestId;
        } else {
            el.upNext.hidden = true;
        }
    }

    function renderQueue() {
        var waiting = state.entries.filter(function (e) { return e.state === 'Waiting'; });
        el.queueBadge.hidden = waiting.length === 0;
        el.queueBadge.textContent = waiting.length;

        var visible = state.entries.filter(function (e) { return e.state !== 'Done' && e.state !== 'Skipped'; });
        el.queueEmpty.hidden = visible.length > 0;

        var position = 0;
        el.queueList.innerHTML = visible.map(function (entry) {
            var mine = (entry.singerProfileIds || []).indexOf(state.profileId) >= 0
                || entry.createdBy === state.profileId;
            var isPlaying = entry.state === 'Playing';
            if (!isPlaying) position++;

            var actions = '';
            if (mine || state.isAdmin) {
                actions += '<button class="icon-btn danger" data-remove="' + entry.requestId + '" title="Austragen">&times;</button>';
            }
            if (!isPlaying) {
                actions += '<button class="icon-btn" data-start="' + entry.requestId + '" title="Jetzt starten">&#9654;</button>';
            }

            return '<li class="queue-item' + (isPlaying ? ' is-done' : '') + '">'
                + '<span class="queue-pos">' + (isPlaying ? '&#9834;' : position) + '</span>'
                + '<span class="queue-main">'
                + '<span class="queue-song">' + escapeHtml(songText(entry)) + '</span>'
                + '<span class="queue-singers">' + escapeHtml(singerText(entry))
                + (entry.isDuet ? ' · Duett' : '') + '</span>'
                + '</span>'
                + '<span class="queue-actions">' + actions + '</span>'
                + '</li>';
        }).join('');
    }

    /* ------------------------------------------------------------------ Songs */

    function searchSongs(query, reset) {
        if (reset) {
            state.songOffset = 0;
            state.songQuery = query;
            el.songList.innerHTML = '';
        }

        var url = '/api/songs?q=' + encodeURIComponent(state.songQuery)
            + '&offset=' + state.songOffset + '&limit=' + PAGE_SIZE;

        return api('GET', url).then(function (result) {
            state.songTotal = result.total;
            state.songOffset += (result.items || []).length;

            el.songCount.textContent = result.total === 0
                ? 'Nichts gefunden.'
                : (state.songOffset + ' von ' + result.total + ' Songs');

            el.songList.insertAdjacentHTML('beforeend', (result.items || []).map(function (song) {
                return '<li><button class="song-item" data-song=\'' + escapeHtml(JSON.stringify(song)) + '\'>'
                    + '<span class="song-main">'
                    + '<span class="song-title">' + escapeHtml(song.title) + '</span>'
                    + '<span class="song-artist">' + escapeHtml(song.artist || '') + '</span>'
                    + '</span>'
                    + (song.isDuet ? '<span class="pill">Duett</span>' : '')
                    + '</button></li>';
            }).join(''));

            el.moreBtn.hidden = state.songOffset >= state.songTotal;
        }).catch(function (e) {
            toast(e.message, true);
        });
    }

    /* ------------------------------------------------------------------ Song-Dialog */

    function openSheet(song) {
        state.selectedSong = song;
        el.sheetTitle.textContent = song.title;
        el.sheetArtist.textContent = song.artist || '';

        var meta = [];
        if (song.year) meta.push(song.year);
        if (song.genre) meta.push(song.genre);
        if (song.language) meta.push(song.language);
        el.sheetMeta.textContent = meta.join(' · ');

        // Two people can share any song, not just a marked duet — Vocaluxe simply scores both on
        // the same voice. Only the wording changes.
        el.partnerLabel.textContent = song.isDuet
            ? 'Duett – wer singt die zweite Stimme?'
            : 'Zusammen singen mit';

        state.selectedPartner = '';
        state.partnerQuery = '';
        el.partnerSearch.value = '';
        renderPartnerList();

        el.songSheet.hidden = false;
    }

    function partnerCandidates() {
        return state.profiles.filter(function (p) { return p.profileId !== state.profileId; });
    }

    function renderPartnerList() {
        var candidates = partnerCandidates();
        var shown = candidates.filter(function (p) { return matches(p.playerName, state.partnerQuery); });

        // "Alone" stays pinned at the top and is never filtered away — it is the default, and
        // searching for a partner you then decide against should not hide the way back.
        var html = '<li><button class="picker-item' + (state.selectedPartner ? '' : ' is-selected')
            + '" data-partner="">Alleine singen</button></li>';

        html += shown.map(function (p) {
            return '<li><button class="picker-item'
                + (state.selectedPartner === p.profileId ? ' is-selected' : '')
                + '" data-partner="' + escapeHtml(p.profileId) + '">'
                + escapeHtml(p.playerName) + '</button></li>';
        }).join('');

        if (!shown.length && state.partnerQuery)
            html += '<li class="picker-empty">Kein Profil mit diesem Namen.</li>';

        el.partnerList.innerHTML = html;
    }

    function closeSheet() {
        el.songSheet.hidden = true;
        state.selectedSong = null;
    }

    function signUp() {
        if (!state.selectedSong) return;

        // Index 0 is player 1 / MIC 1 — the person signing up always takes the first microphone.
        var singers = [state.profileId];
        var partner = state.selectedPartner;
        if (partner) singers.push(partner);

        el.signUpBtn.disabled = true;
        api('POST', '/api/queue', {songId: state.selectedSong.songId, singers: singers})
            .then(function () {
                closeSheet();
                toast('Eingetragen — du stehst in der Warteliste.');
                switchView('queue');
                refreshQueue();
            })
            .catch(function (e) { toast(e.message, true); })
            .then(function () { el.signUpBtn.disabled = false; });
    }

    /* ------------------------------------------------------------------ Live-Updates */

    function connectEvents() {
        if (!window.EventSource || connectEvents._source) return;

        var source = new EventSource('/api/events');
        connectEvents._source = source;

        source.onmessage = function (event) {
            try {
                var data = JSON.parse(event.data);
                state.entries = data.entries || [];
                renderQueue();
                renderNowNext(data.playing, data.next);
            } catch (e) { /* kaputter Frame — beim nächsten Tick wieder gut */ }
        };

        source.onerror = function () {
            // EventSource reconnects on its own; fall back to a poll so the list cannot go stale.
            clearTimeout(connectEvents._retry);
            connectEvents._retry = setTimeout(refreshQueue, 5000);
        };
    }

    /* ------------------------------------------------------------------ Views */

    function switchView(name) {
        document.querySelectorAll('.content .view').forEach(function (view) {
            view.classList.toggle('is-active', view.id === 'view-' + name);
        });
        document.querySelectorAll('.tab').forEach(function (tab) {
            tab.classList.toggle('is-active', tab.dataset.view === name);
        });
    }

    /* ------------------------------------------------------------------ Events */

    function wire() {
        el.profileList.addEventListener('click', function (e) {
            var btn = e.target.closest('.profile-btn');
            if (btn) signIn(btn.dataset.id);
        });

        el.newProfileBtn.addEventListener('click', function () {
            var name = el.newProfileName.value.trim();
            if (!name) { toast('Bitte einen Namen eingeben.', true); return; }

            el.newProfileBtn.disabled = true;
            api('POST', '/api/profiles', {name: name})
                .then(function (res) {
                    setSession(res.sessionId, res.profileId);
                    return loadProfiles();
                })
                .then(function () { enterApp(); })
                .catch(function (e) { toast(e.message, true); })
                .then(function () { el.newProfileBtn.disabled = false; });
        });

        el.newProfileName.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') el.newProfileBtn.click();
        });

        // The pill in the header is the quickest way to "that's not me" — keep it, but the full
        // profile view is where switching and settings live.
        el.whoBtn.addEventListener('click', function () { switchView('me'); });
        el.switchProfileBtn.addEventListener('click', backToLogin);

        el.difficultyPicker.addEventListener('click', function (e) {
            var btn = e.target.closest('[data-difficulty]');
            if (btn) setDifficulty(Number(btn.dataset.difficulty));
        });

        document.querySelectorAll('.tab').forEach(function (tab) {
            tab.addEventListener('click', function () { switchView(tab.dataset.view); });
        });

        document.addEventListener('click', function (e) {
            var goto = e.target.closest('[data-goto]');
            if (goto) switchView(goto.dataset.goto);

            if (e.target.closest('[data-close-sheet]')) closeSheet();

            var songBtn = e.target.closest('.song-item');
            if (songBtn) {
                // Swallowing errors here once hid a broken sheet completely: the tap did nothing and
                // the console stayed empty. Report it instead.
                try {
                    openSheet(JSON.parse(songBtn.dataset.song));
                } catch (err) {
                    console.error('openSheet failed', err);
                    toast('Song konnte nicht geöffnet werden.', true);
                }
            }

            var remove = e.target.closest('[data-remove]');
            if (remove) {
                api('DELETE', '/api/queue/' + remove.dataset.remove)
                    .then(function () { toast('Ausgetragen.'); refreshQueue(); })
                    .catch(function (err) { toast(err.message, true); });
            }

            var start = e.target.closest('[data-start]');
            if (start) startRequest(start.dataset.start);
        });

        el.profileSearch.addEventListener('input', function () {
            state.profileQuery = el.profileSearch.value;
            renderProfiles();
        });

        el.partnerSearch.addEventListener('input', function () {
            state.partnerQuery = el.partnerSearch.value;
            renderPartnerList();
        });

        el.partnerList.addEventListener('click', function (e) {
            var item = e.target.closest('[data-partner]');
            if (!item) return;
            state.selectedPartner = item.dataset.partner;
            renderPartnerList();
        });

        el.signUpBtn.addEventListener('click', signUp);

        el.startNextBtn.addEventListener('click', function () {
            startRequest(el.startNextBtn.dataset.id);
        });

        el.clearFinishedBtn.addEventListener('click', function () {
            api('POST', '/api/queue/clear-finished')
                .then(function (res) { toast(res.removed + ' Einträge entfernt.'); refreshQueue(); })
                .catch(function (e) { toast(e.message, true); });
        });

        el.moreBtn.addEventListener('click', function () { searchSongs(state.songQuery, false); });

        var searchTimer = null;
        el.searchInput.addEventListener('input', function () {
            clearTimeout(searchTimer);
            var value = el.searchInput.value;
            searchTimer = setTimeout(function () { searchSongs(value, true); }, 220);
        });
    }

    function startRequest(id) {
        api('POST', '/api/queue/' + id + '/start')
            .then(function () { toast('Song wird gestartet.'); refreshQueue(); refreshStatus(); })
            .catch(function (e) { toast(e.message, true); });
    }

    /* ------------------------------------------------------------------ Start */

    function init() {
        cacheElements();
        wire();

        loadProfiles().then(function () {
            if (!state.sessionId || !state.profileId) return;

            // Resume a stored session, but only if the server still knows it.
            api('GET', '/api/session')
                .then(function () { enterApp(); })
                .catch(function () { clearSession(); });
        }).catch(function () { /* Fehlermeldung steht bereits in der Liste */ });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
