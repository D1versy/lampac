(function () {
    'use strict';

    var API = '{localhost}';
    var ICON = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M12 3v12m0 0l-4-4m4 4l4-4M5 19h14" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    var BELL = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M6 8a6 6 0 1112 0c0 7 3 9 3 9H3s3-2 3-9z" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/><path d="M10.3 21a1.94 1.94 0 003.4 0" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    var CAM = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M2.8 7.4l14.4-3.3 1.3 5.6L4.1 13 2.8 7.4z" stroke="currentColor" stroke-width="2" stroke-linejoin="round"/><path d="M6.5 12.2V15a3 3 0 003 3h1.2" stroke="currentColor" stroke-width="2" stroke-linecap="round"/><circle cx="18.5" cy="18" r="2.6" stroke="currentColor" stroke-width="2"/><path d="M18 9.9l3.2 1.5" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';
    var REC = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><circle cx="12" cy="12" r="9" stroke="currentColor" stroke-width="2"/><circle cx="12" cy="12" r="3.5" fill="currentColor"/></svg>';

    function req(url, cb, err) {
        try {
            var net = new Lampa.Reguest();
            net.timeout(45000);
            net.silent(url, function (json) { cb(json); }, function (a, c) { if (err) err(a, c); });
        } catch (e) {
            fetch(url).then(function (r) { return r.json(); }).then(cb).catch(function () { if (err) err(); });
        }
    }

    function post(url, data, cb, err) {
        try {
            var body = Object.keys(data).map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(data[k]); }).join('&');
            fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: body })
                .then(function (r) { return r.json(); }).then(function (j) { if (cb) cb(j); })
                .catch(function () { if (err) err(); });
        } catch (e) { if (err) err(); }
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
        });
    }

    function tmdbImg(path) { try { return Lampa.TMDB.image(path); } catch (e) { return ''; } }
    function tmdbKey() { try { return Lampa.TMDB.key(); } catch (e) { return ''; } }   // key() — функция!

    function injectCss() {
        if (document.getElementById('qdl-css')) return;
        var st = document.createElement('style');
        st.id = 'qdl-css';
        st.textContent =
            '.qdl-watch.focus{background:#fff !important;color:#000 !important;transform:scale(1.03)}' +
            '.qdl-watch-btn{background:rgba(20,160,40,.92) !important;color:#fff !important}' +
            '.qdl-watch-btn.focus{background:#19b531 !important;color:#fff !important}' +
            '.qdl-watch-btn span{color:#fff !important}' +
            // режим «Загрузки»: в полной карточке прячем все кнопки, кроме наших «Смотреть»/«Продолжить»
            '.qdl-only .full-start__buttons .full-start__button:not(.qdl-watch-btn):not(.qdl-continue-btn),' +
            '.qdl-only .full-start-new__buttons .full-start__button:not(.qdl-watch-btn):not(.qdl-continue-btn){display:none !important}' +
            // «Продолжить: Серия N» — синяя, чтобы отличалась от зелёной «Смотреть»
            '.qdl-continue-btn{background:rgba(25,100,210,.92) !important;color:#fff !important}' +
            '.qdl-continue-btn.focus{background:#2b7de9 !important;color:#fff !important}' +
            '.qdl-continue-btn span{color:#fff !important}' +
            // DMCA-карточка (CUB блокирует): остаются только «Скачать» и «Смотреть (загружено)»
            '.qdl-dmca .full-start__buttons .full-start__button:not(.qdl-download):not(.qdl-watch-btn),' +
            '.qdl-dmca .full-start-new__buttons .full-start__button:not(.qdl-download):not(.qdl-watch-btn){display:none !important}' +
            // своя кнопка фуллскрина в плеере (НЕ класс player-panel__fullscreen — иначе Lampa её прячет на моб.)
            '.qdl-fs{display:inline-flex !important;align-items:center;justify-content:center;padding:.6em;margin:0 .2em;cursor:pointer;opacity:.85;vertical-align:middle}' +
            '.qdl-fs.focus{opacity:1;transform:scale(1.12)}' +
            '.qdl-fs svg{width:1.8em;height:1.8em}' +
            // карточка коллекции в «Загрузках»: эффект стопки постеров (дёшево, без доп. DOM)
            '.qdl-col-card .card__view{box-shadow:.3em -.3em 0 -.08em rgba(255,255,255,.28),.6em -.6em 0 -.16em rgba(255,255,255,.14);border-radius:.3em}' +
            // тайл сетки эфира: без своего focus-правила он никак не подсвечивается пультом
            '.qdl-watch-tile{transition:transform .1s}' +
            '.qdl-watch-tile.focus{box-shadow:0 0 0 .22em #fff;transform:scale(1.04);z-index:1}' +
            // бейдж непрочитанных на нашей иконке уведомлений в хедере (красный кружок с числом)
            '.qdl-noti-head{position:relative}' +
            '.qdl-noti-head-badge{position:absolute;top:-0.1em;right:-0.1em;min-width:1.5em;height:1.5em;padding:0 0.35em;box-sizing:border-box;background:#d33;color:#fff;border:0.12em solid #fff;border-radius:1em;font-size:0.62em;line-height:1.26em;font-weight:700;text-align:center}';
        document.head.appendChild(st);
    }

    // ───────── DMCA-фолбек (см. claude/06 в Media-server) ─────────
    // CUB на заблокированные правообладателем карточки отдаёт {"blocked":true} вместо JSON →
    // Lampa рисует экран «Контент заблокирован» без единой кнопки. Обход в два слоя:
    //  1) XHR-перехват: детали карточек (tmdb.<cub>/3/movie|tv/<id>) заворачиваем на свой
    //     TMDB-прокси lampac (/tmdb/api) — карточка рендерится ВСЕГДА, каталог/поиск не трогаем;
    //  2) DMCA-список CUB (/blocked) — на таких карточках прячем всё, кроме «Скачать» (.qdl-dmca).
    // Основной патч ставит lampainit-invc.js (синхронно, до старта приложения — deep-link!);
    // здесь дубль-фолбек для клиентов, подключивших только /qdl.js. Guard — window.qdl_xhr_patch.
    var dmcaList = null;         // null — ещё не загружен; [] — загружен (возможно, пуст)
    var dmcaWaiters = [];
    var dmcaLoading = false;

    function noteCubDomain(u) {
        var m = /^https?:\/\/tmdb\.([^\/]+)\//.exec(String(u));
        if (m) window.qdl_cub_domain = m[1];   // общий с lampainit-invc канал: домен CUB для /blocked
    }

    // Детали карточки/сезона у CUB → наш TMDB-прокси. null = запрос не трогаем.
    // Две формы: прямая https://tmdb.<cub>/3/... и через серверный CubProxy
    // (плагин cubproxy.js на request_before превращает её в <host>/cub/tmdb.<cub>/3/...)
    function rewriteCubUrl(u) {
        var m = /^https?:\/\/(?:[^\/]+\/cub\/)?tmdb\.[^\/]*\/(3\/(?:movie|tv)\/\d+(?:\/[^?]*)?)(\?.*)$/.exec(String(u));
        if (!m) return null;
        if (m[2].indexOf('api_key=') === -1) return null;   // прямому TMDB без ключа нельзя (401)
        return API + '/tmdb/api/' + m[1] + m[2];
    }

    function isDmca(media, id) {
        if (!dmcaList || !id) return false;
        for (var i = 0; i < dmcaList.length; i++) {
            var a = dmcaList[i];
            if (a && a.id && a.id == id && a.cat == media) return true;
        }
        return false;
    }

    function setDmcaList(list) {
        dmcaList = Object.prototype.toString.call(list) === '[object Array]' ? list : [];
        var w = dmcaWaiters; dmcaWaiters = [];
        w.forEach(function (cb) { try { cb(); } catch (e) {} });
    }

    function loadDmcaList() {
        var cached = null;
        try { cached = Lampa.Storage.get('qdl_dmca_cache', null); } catch (e) {}
        if (cached && cached.ts && (Date.now() - cached.ts) < 6 * 3600 * 1000 && cached.list) {
            setDmcaList(cached.list);
            return;
        }
        req('https://tmdb.' + (window.qdl_cub_domain || 'cub.rip') + '/blocked', function (list) {
            if (Object.prototype.toString.call(list) !== '[object Array]') list = (cached && cached.list) || [];
            else { try { Lampa.Storage.set('qdl_dmca_cache', { ts: Date.now(), list: list }); } catch (e) {} }
            setDmcaList(list);
        }, function () { setDmcaList((cached && cached.list) || []); });
    }

    // дождаться DMCA-списка (лениво инициирует загрузку); если уже есть — колбэк сразу
    function whenDmca(cb) {
        if (dmcaList) return cb();
        dmcaWaiters.push(cb);
        if (!dmcaLoading) { dmcaLoading = true; loadDmcaList(); }
    }

    // XHR-перехват — на уровне прототипа, т.к. запрос карточки в app.min.js минифицирован
    // и идёт напрямую на tmdb.<cub_domain> мимо Lampa.TMDB.api
    try {
        if (!window.qdl_xhr_patch && window.XMLHttpRequest && window.XMLHttpRequest.prototype) {
            window.qdl_xhr_patch = 1;
            var xhrOpen = window.XMLHttpRequest.prototype.open;
            window.XMLHttpRequest.prototype.open = function (method, url) {
                try {
                    if (String(method).toUpperCase() === 'GET') {
                        noteCubDomain(url);
                        var ru = rewriteCubUrl(url);
                        if (ru) arguments[1] = ru;
                    }
                } catch (e) {}
                return xhrOpen.apply(this, arguments);
            };
        }
    } catch (e) {}

    // ───────── Метаданные TMDB (богатый набор полей) ─────────
    function names(arr) { return (arr || []).map(function (x) { return (x && x.name) ? x.name : x; }).filter(Boolean); }

    function slimCard(m) {
        if (!m) return null;
        // тип: сначала явный media_type/method, затем СТРУКТУРНЫЕ признаки сериала (сезоны/серии),
        // и только потом эвристика по полям. ВАЖНО: у TMDB id в movie и tv — РАЗНЫЕ объекты!
        var isTv = m.media_type === 'tv' || m.method === 'tv'
            || !!(m.number_of_seasons || m.number_of_episodes || m.seasons || m.episode_run_time)
            || (!!m.first_air_date && !m.release_date)
            || (!!m.name && !m.title);
        var date = m.release_date || m.first_air_date || '';
        return {
            id: m.id,
            media_type: isTv ? 'tv' : 'movie',
            title: m.title || m.name,
            original_title: m.original_title || m.original_name,
            overview: m.overview,
            tagline: m.tagline,
            release_date: date,
            year: (date + '').slice(0, 4),
            vote_average: m.vote_average,
            poster_path: m.poster_path,
            backdrop_path: m.backdrop_path,
            genres: names(m.genres),
            runtime: m.runtime || (m.episode_run_time && m.episode_run_time[0]) || 0,
            countries: names(m.production_countries).concat(m.origin_country || []),
            status: m.status,
            number_of_seasons: m.number_of_seasons,
            number_of_episodes: m.number_of_episodes,
            age: m.age || m.certification || '',
            source: m.source || 'tmdb'
        };
    }

    function saveMeta(hash, movie, cb) {
        if (!hash || !movie) { if (cb) cb(null); return; }
        var purl = movie.poster_path ? tmdbImg('t/p/w500' + movie.poster_path) : '';
        post(API + '/qdl/save', { hash: hash, card: JSON.stringify(slimCard(movie)), poster_url: purl }, cb, function () { if (cb) cb(null); });
    }

    // Самолечение постера: мета есть, а img/<hash>.jpg на сервере нет (скачивание при /qdl/save
    // сорвалось — сеть/DMCA-прокси). Дёргаем повторное сохранение ТОЛЬКО постера (без card —
    // мету не перезаписываем) и обновляем карточку на месте. Дёшево: только для битых карточек.
    function healPoster(t, img) {
        if (!t || t.has_poster || !t.meta || !t.meta.poster_path) return;
        post(API + '/qdl/save', { hash: t.hash, poster_url: tmdbImg('t/p/w500' + t.meta.poster_path) }, function (r) {
            if (r && r.has_poster) {
                t.has_poster = true;
                img.attr('src', API + '/qdl/poster?hash=' + t.hash + '&t=' + Date.now());
            }
        });
    }

    function cleanName(name) {
        var s = String(name || '');
        s = s.split(/[\[\(]/)[0];
        s = s.split('/')[0];
        s = s.replace(/[._]/g, ' ');
        s = s.replace(/\b(19|20)\d\d\b[\s\S]*$/, '');
        s = s.replace(/\b(WEB-?DL|BluRay|HDRip|WEBRip|2160p|1080p|720p|4K|HEVC|x26[45]|BDRip|DVDRip)\b[\s\S]*$/i, '');
        return s.trim();
    }

    // нормализация названия для сравнения: нижний регистр, ё→е, всё кроме букв/цифр → один пробел
    function normTitle(s) {
        return String(s || '').toLowerCase().replace(/ё/g, 'е').replace(/[^a-zа-яё0-9]+/g, ' ').trim();
    }

    // в имени раздачи есть маркер сериала (S01/S01E05/Season/сезон)?
    // ⚠️ \b в JS не работает с кириллицей (\w = только ASCII) → проверяем токены нормализованной строки
    function isSerialName(name) {
        var toks = normTitle(name).split(' ');
        for (var i = 0; i < toks.length; i++) {
            var t = toks[i];
            if (t === 'season' || t === 'seasons' || /^сезон(ы|а|ов)?$/.test(t) || /^s\d{1,2}(e\d{1,3})?$/.test(t)) return true;
        }
        return false;
    }

    // хвост после названия начинается с сезонного маркера? («s01…», «season 3…», «3 сезон…»)
    function isSeasonTail(rest) {
        return /^(\d{1,2}\s+)?(s\d{1,2}(e\d{1,3})?|seasons?|сезон(ы|а|ов)?)(\s|$)/.test(rest);
    }

    // Найти загрузку для карточки. Проход A: строгий матч TMDB id + media_type — у TMDB id
    // movie и tv живут в РАЗНЫХ пространствах, совпадение номера без типа = чужой объект.
    // Проход B (back-link): только раздачи БЕЗ меты — раздачу, чья мета указывает на другую
    // карточку, по имени не матчим и не трогаем; сравнение — ТОЧНОЕ равенство нормализованных
    // названий (для tv допускается «название + сезонный хвост»), сериальные раздачи к фильмам
    // не цепляем.
    function findDownload(list, movie) {
        list = list || [];
        movie = movie || {};
        var type = movie.media_type === 'tv' ? 'tv' : 'movie';
        var i, j, x;

        if (movie.id != null && movie.id !== '') {
            for (i = 0; i < list.length; i++) {
                x = list[i];
                if (x && x.meta && String(x.meta.id) === String(movie.id)
                    && (!x.meta.media_type || x.meta.media_type === type)) return x;   // старые меты без media_type — толерантно
            }
        }

        var titles = [];
        var src = [movie.title, movie.original_title, movie.name, movie.original_name];
        for (i = 0; i < src.length; i++) {
            var t = normTitle(src[i]);
            if (t && titles.indexOf(t) === -1) titles.push(t);
        }
        if (!titles.length) return null;

        for (i = 0; i < list.length; i++) {
            x = list[i];
            if (!x || (x.meta && x.meta.id)) continue;             // уже привязана к какой-то карточке
            var raw = String(x.name || '');
            if (type === 'movie' && isSerialName(raw)) continue;   // по СЫРОМУ имени: cleanName режет «(Season 3)» по скобке
            var n = normTitle(cleanName(raw.replace(/\.(mkv|mp4|avi|ts|m4v|webm|mov)$/i, '')));
            if (!n) continue;
            for (j = 0; j < titles.length; j++) {
                if (n === titles[j]) return x;
                if (type === 'tv' && n.indexOf(titles[j] + ' ') === 0 && isSeasonTail(n.slice(titles[j].length + 1))) return x;
            }
        }
        return null;
    }

    // можно ли предложить транскод в MP4: завершённая раздача, ещё не заменённая локальным файлом
    function canTranscode(t) {
        return !!t && !t.local && t.state !== 'local' && (t.progress || 0) >= 1;
    }

    // поллинг прогресса транскода: «в очереди (N)» один раз, тост каждые ~10%, финал по done/error.
    // ⚠ ветка queued обязана продолжать поллинг, иначе полл тихо умрёт на стоящей в очереди задаче
    var tcPolls = {};
    function pollTranscode(hash, title) {
        if (tcPolls[hash]) return;
        var lastDecile = 0, toldQueued = false, sawAlive = false;
        tcPolls[hash] = setInterval(function () {
            req(API + '/qdl/transcode/status?hash=' + hash, function (s) {
                s = s || {};
                if (s.state === 'queued') {
                    sawAlive = true;
                    if (!toldQueued) { toldQueued = true; Lampa.Noty.show('🎬 ' + (title || 'Транскодирование') + ': в очереди (' + (s.position || 1) + ')'); }
                } else if (s.state === 'running') {
                    sawAlive = true;
                    var d = Math.floor((s.progress || 0) * 10);
                    if (d > lastDecile) {
                        lastDecile = d;
                        var msg = '🎬 ' + (title || 'Транскодирование') + ': ';
                        if (s.filesTotal > 1) msg += 'серия ' + Math.min((s.fileDone || 0) + 1, s.filesTotal) + '/' + s.filesTotal + ' — ' + (d * 10) + '%';
                        else msg += (d * 10) + '%';
                        Lampa.Noty.show(msg);
                    }
                } else {
                    clearInterval(tcPolls[hash]); delete tcPolls[hash];
                    if (s.state === 'done') {
                        if (s.filesTotal > 1) Lampa.Noty.show('✓ ' + (title || 'Сериал') + ' — серии теперь MP4 (' + s.filesTotal + ')');
                        else Lampa.Noty.show('✓ ' + (title || 'Загрузка') + ' — теперь MP4, торрент удалён');
                    }
                    else if (s.state === 'error') Lampa.Noty.show('Транскодирование не удалось: ' + (s.error || 'ошибка'));
                    else if (s.state === 'none' && sawAlive) Lampa.Noty.show('Транскодирование прервано (перезапуск сервера) — запусти ещё раз');
                }
            });
        }, 5000);
    }

    function tmdbSearch(name, cb) {
        try {
            var url = Lampa.TMDB.api('search/multi?api_key=' + tmdbKey() + '&language=ru-RU&query=' + encodeURIComponent(name));
            req(url, function (r) {
                var list = (r && r.results) ? r.results.filter(function (x) {
                    return (x.media_type === 'movie' || x.media_type === 'tv') && x.poster_path;
                }) : [];
                cb(list[0] || null);
            }, function () { cb(null); });
        } catch (e) { cb(null); }
    }

    // полные детали (жанры, хронометраж, страны, статус, слоган…)
    function tmdbDetails(id, mt, cb) {
        try {
            var url = Lampa.TMDB.api(mt + '/' + id + '?api_key=' + tmdbKey() + '&language=ru-RU');
            req(url, function (d) { if (d && d.id) { d.media_type = mt; cb(d); } else cb(null); }, function () { cb(null); });
        } catch (e) { cb(null); }
    }

    function enrich(name, cb) {   // имя раздачи → полная карточка TMDB
        tmdbSearch(cleanName(name), function (found) {
            if (!found) { cb(null); return; }
            tmdbDetails(found.id, found.media_type, function (d) { cb(d || found); });
        });
    }

    function posterUrl(item) {
        if (item && item.has_poster) return API + '/qdl/poster?hash=' + item.hash;
        return './img/img_broken.svg';
    }

    function videoFiles(files) {
        return (files || []).filter(function (f) { return /\.(mkv|mp4|avi|ts|m4v|webm|mov)$/i.test(f.name || ''); })
            .sort(function (a, b) { return String(a.name).localeCompare(String(b.name), undefined, { numeric: true }); });
    }

    // объединённый список /qdl/episodes уже отсортирован сервером (season, ep) — имена донора и
    // основной вперемешку сортируются НЕПРАВИЛЬНО, поэтому при наличии epkey порядок не трогаем
    function mergedVideoFiles(files) {
        files = files || [];
        var hasEp = false;
        for (var i = 0; i < files.length; i++) if (files[i] && files[i].epkey) { hasEp = true; break; }
        if (!hasEp) return videoFiles(files);
        return files.filter(function (f) { return /\.(mkv|mp4|avi|ts|m4v|webm|mov)$/i.test((f && f.name) || ''); });
    }

    // серия может лежать в раздаче-доноре (охота) — стрим/аудио строим от её hash
    function srcHash(f, hash) { return (f && f.hash) || hash; }

    // объединённый плейлист сериала; фолбэк на /qdl/files (старый сервер / ошибка)
    function fetchEpisodes(hash, cb, err) {
        req(API + '/qdl/episodes?hash=' + hash, function (files) {
            if (files && files.length !== undefined) cb(files);
            else req(API + '/qdl/files?hash=' + hash, cb, err);
        }, function () { req(API + '/qdl/files?hash=' + hash, cb, err); });
    }
    function baseName(p) { return String(p || '').split('/').pop().split('\\').pop(); }

    // ───────── Прогресс просмотра серий (штатный Lampa.Timeline, локально устройству) ─────────
    // Ключ стабилен до/после транскода: infohash сохраняется (маркер наследует hash),
    // база имени сохраняется (меняется только расширение mkv→mp4)
    function stripExt(n) { return String(n || '').replace(/\.(mkv|mp4|avi|ts|m4v|webm|mov)$/i, ''); }
    function epTimelineHash(hash, fileName) { return Lampa.Utils.hash(hash + ':' + stripExt(baseName(fileName))); }
    // {percent 0-100, time, duration, handler} или заглушка, если Timeline недоступен
    function epView(hash, fileName) {
        try { return Lampa.Timeline.view(epTimelineHash(hash, fileName)); }
        catch (e) { return { percent: 0, time: 0, duration: 0 }; }
    }

    // Новый стабильный ключ таймлайна: f.tl (seriesKey:sSeE с сервера /qdl/episodes) не зависит
    // ни от hash раздачи, ни от имени файла → прогресс переживает замещение донор→основная и re-grab.
    // Файлы без tl (экстры, старый сервер) — легаси-ключ hash:имя.
    function epTimelineKey(f, hash) {
        return f && f.tl ? Lampa.Utils.hash('qdltl:' + f.tl) : epTimelineHash(srcHash(f, hash), f && f.name);
    }
    // Миграция без потери: если по новому ключу прогресса ещё нет, а по легаси есть — берём легаси.
    function pickTimeline(hash, f) {
        try {
            var nv = Lampa.Timeline.view(epTimelineKey(f, hash));
            if (f && f.tl && !(nv.percent > 0)) {
                var legacy = Lampa.Timeline.view(epTimelineHash(srcHash(f, hash), f.name));
                if (legacy.percent > 0) return legacy;
            }
            return nv;
        } catch (e) { return { percent: 0, time: 0, duration: 0 }; }
    }

    // короткое имя серии для кнопки «Продолжить»
    function epShort(name) {
        var b = stripExt(baseName(name)), m;
        m = /S(\d+)[\s._-]*E(\d+)/i.exec(b);
        if (m) return 'S' + parseInt(m[1], 10) + ' · Серия ' + parseInt(m[2], 10);
        m = /(?:серия|episode|ep)[\s._#-]*(\d{1,4})/i.exec(b);
        if (m) return 'Серия ' + parseInt(m[1], 10);
        m = /(?:^|[\s._[(-])(\d{1,3})(?:[\s._\])-]|$)/.exec(b);
        if (m) return 'Серия ' + parseInt(m[1], 10);
        return b.length > 24 ? b.slice(0, 24) + '…' : b;
    }

    // Что продолжать: (1) ПОСЛЕДНЯЯ серия на паузе (5–90%) — досмотреть её;
    // (2) иначе серия после последней досмотренной (≥90%), которая ещё не досмотрена;
    // (3) прогресса нет / всё досмотрено → null (кнопка не показывается)
    function chooseContinue(vids, viewFn) {
        var i, p;
        for (i = vids.length - 1; i >= 0; i--) {
            p = (viewFn(vids[i]) || {}).percent || 0;
            if (p >= 5 && p < 90) return vids[i];
        }
        var last = -1;
        for (i = vids.length - 1; i >= 0; i--) {
            if (((viewFn(vids[i]) || {}).percent || 0) >= 90) { last = i; break; }
        }
        if (last >= 0) {
            for (i = last + 1; i < vids.length; i++)
                if (((viewFn(vids[i]) || {}).percent || 0) < 90) return vids[i];
        }
        return null;
    }

    // плейлист сериала: у каждого элемента свой timeline → плеер пишет прогресс сам,
    // «следующая серия» внутри плеера продолжает вести отметки. Серии доноров играют со своего hash.
    // audioHash — раздача, для которой выбрана озвучка: audio-id ('eN' встроенная / 'd<id>' студия)
    // специфичен для КОНКРЕТНОГО рипа, поэтому к файлам ДРУГОЙ раздачи (донор) его не применяем —
    // иначе несуществующая дорожка → не тот язык или отказ HLS. Чужим — дефолтная дорожка (null).
    function buildPlaylist(hash, vids, audio, audioHash) {
        return vids.map(function (f) {
            var a = (audioHash == null || srcHash(f, hash) === audioHash) ? audio : null;
            var item = { title: baseName(f.name) + (f.source === 'donor' ? ' · врем.' : ''), url: streamUrl(srcHash(f, hash), f.index, a) };
            try { item.timeline = pickTimeline(hash, f); } catch (e) {}
            return item;
        });
    }

    // ТВ (нативный плеер) тянет оригинал (EAC3 ок), всё остальное (десктоп/мобайл-браузер) — HLS (звук→AAC).
    // ВАЖНО: Platform.is('browser') слишком узок (на Linux-десктопе platform='' → false). Берём инверсию tv().
    function isBrowser() {
        try { if (Lampa.Platform && typeof Lampa.Platform.tv === 'function') return !Lampa.Platform.tv(); } catch (e) {}
        var ua = navigator.userAgent || '';
        return !/Tizen|Web0?S|webOS|SMART-TV|SmartTV|HbbTV|AppleTV|CrKey|Android TV|NetCast|VIDAA|MSX/i.test(ua);
    }
    // «Мобильный» профиль (_m): live-720p с капом битрейта — телефон на сотовой сети.
    // Флаг сети ставит нативная iOS-оболочка (window.d1vision_network = 'cellular'|'wifi',
    // обновляется при смене сети); остальные платформы флага не имеют → всегда старый путь.
    // qdl_mobile_quality: 'auto' (дефолт) | 'off' | 'always' — страховка/ручной форс через Lampa.Storage.
    function mobileHls() {
        var mode = 'auto';
        try { mode = Lampa.Storage.get('qdl_mobile_quality', 'auto') || 'auto'; } catch (e) {}   // Storage упал → авто; платформенную ветку не глушим
        if (mode === 'off') return false;
        if (mode === 'always') return true;
        return window.d1vision_platform === 'ios' && window.d1vision_network === 'cellular';
    }
    // audio: 'o' (ориг) | 'eN' (встроенная) | 'd<id>' (озвучка по студии). Внешняя → ВСЕГДА HLS (домешиваем).
    function streamUrl(hash, index, audio) {
        var ext = audio && (audio.charAt(0) === 'f' || audio.charAt(0) === 'd');
        var mob = mobileHls();   // пересиливает и ТВ-ветку оригинала: нативу на сотовой — 720p-HLS
        if (ext || mob || isBrowser()) {
            var k = hash + '_' + (index >= 0 ? index : -1) + (audio && audio !== 'o' ? '_' + audio : '') + (mob ? '_m' : '');
            return API + '/qdl/hls/' + k + '/playlist.m3u8';
        }
        return API + '/qdl/stream?hash=' + hash + (index >= 0 ? '&index=' + index : '');
    }

    // выбор озвучки запоминается на сериал (по hash)
    function getAudioPref(hash) { try { return (Lampa.Storage.get('qdl_audio2', {}) || {})[hash]; } catch (e) { return null; } }
    function setAudioPref(hash, id) { try { var m = Lampa.Storage.get('qdl_audio2', {}) || {}; m[hash] = id; Lampa.Storage.set('qdl_audio2', m); } catch (e) {} }
    function dropAudioPref(hash) { try { var m = Lampa.Storage.get('qdl_audio2', {}) || {}; if (m[hash] !== undefined) { delete m[hash]; Lampa.Storage.set('qdl_audio2', m); } } catch (e) {} }

    // определить озвучку (из памяти или спросить один раз), затем cb(audioId)
    function ensureAudio(hash, index, cb) {
        req(API + '/qdl/audio?hash=' + hash + '&index=' + (index >= 0 ? index : -1), function (opts) {
            opts = opts || [];
            if (opts.length <= 1) { cb(opts[0] && opts[0].id); return; }
            var pref = getAudioPref(hash);
            // показываем меню КАЖДЫЙ раз (можно сменить), запомненную озвучку — первой, с галочкой
            var ordered = opts.slice().sort(function (a, b) { return (b.id === pref ? 1 : 0) - (a.id === pref ? 1 : 0); });
            Lampa.Select.show({
                title: 'Озвучка',
                items: ordered.map(function (o) { return { title: (o.id === pref ? '✓ ' : '') + o.label, id: o.id }; }),
                onSelect: function (s) { setAudioPref(hash, s.id); cb(s.id); },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { cb(null); });
    }

    function rawPlay(hash, index, title, audio, fileName) {
        var item = { title: title || 'Загрузка', url: streamUrl(hash, index, audio) };
        try { item.timeline = Lampa.Timeline.view(epTimelineHash(hash, fileName || title)); } catch (e) {}
        Lampa.Player.play(item);
        Lampa.Player.playlist([item]);
    }

    // ───────── Воспроизведение локального файла (оффлайн) ─────────
    function playLocal(hash, index, title, fileName) {
        ensureAudio(hash, index, function (audio) { rawPlay(hash, index, title, audio, fileName); });
    }

    // сыграть конкретную серию с полным плейлистом (элемент плейлиста — тот же инстанс timeline)
    function playEpisode(hash, vids, target) {
        ensureAudio(srcHash(target, hash), target.index, function (audio) {
            var playlist = buildPlaylist(hash, vids, audio, srcHash(target, hash));
            for (var i = 0; i < vids.length; i++)
                if (vids[i] === target || (vids[i].index === target.index && srcHash(vids[i], hash) === srcHash(target, hash))) { Lampa.Player.play(playlist[i]); break; }
            Lampa.Player.playlist(playlist);
        });
    }

    function chooseEpisode(hash, name) {
        fetchEpisodes(hash, function (files) {
            var vids = mergedVideoFiles(files);
            if (!vids.length) { Lampa.Noty.show('Видеофайлы не найдены'); return; }
            if (vids.length === 1) { playLocal(srcHash(vids[0], hash), vids[0].index, baseName(vids[0].name), vids[0].name); return; }

            ensureAudio(srcHash(vids[0], hash), vids[0].index, function (audio) {   // озвучку выбираем один раз на сериал
                var playlist = buildPlaylist(hash, vids, audio, srcHash(vids[0], hash));
                var view = function (f) { return pickTimeline(hash, f); };
                var cur = chooseContinue(vids, view);
                Lampa.Select.show({
                    title: 'Серии — ' + (name || ''),
                    items: vids.map(function (f, i) {
                        var p = (view(f) || {}).percent || 0, pre = '';
                        if (p >= 90) pre = '✓ ';                                   // досмотрена
                        else if (p >= 5) pre = '► ' + Math.round(p) + '% · ';      // на паузе
                        var suf = f.source === 'donor' ? ' · врем.' : '';          // серия с раздачи-донора (охота)
                        return { title: pre + baseName(f.name) + suf, i: i, selected: cur ? vids[i] === cur : i === 0 };
                    }),
                    onSelect: function (a) {
                        Lampa.Player.play(playlist[a.i]);   // сам элемент плейлиста: его timeline пишет прогресс
                        Lampa.Player.playlist(playlist);
                    },
                    onBack: function () { Lampa.Controller.toggle('content'); }
                });
            });
        }, function () { Lampa.Noty.show('Ошибка чтения файлов'); });
    }

    function watchByHash(hash, name) {
        fetchEpisodes(hash, function (files) {
            var vids = mergedVideoFiles(files);
            if (vids.length > 1) chooseEpisode(hash, name);
            else playLocal(vids.length ? srcHash(vids[0], hash) : hash, vids.length ? vids[0].index : -1, name, vids.length ? vids[0].name : null);
        }, function () { playLocal(hash, -1, name); });
    }

    // гейт недокачанного: раздача с progress<1 — предупредить, что видна только скачанная часть.
    // Для сериала предупреждение покажется даже если выбранная серия скачана (per-file прогресс вне скоупа)
    function confirmPartial(item, run) {
        var p = (item && typeof item.progress === 'number') ? item.progress : 1;   // нет данных → fail-open
        if (p >= 1 || (item && (item.local || item.state === 'local'))) { run(); return; }
        var pct = Math.round(p * 100);
        Lampa.Select.show({
            title: 'Ещё качается (' + pct + '%) — показана будет только скачанная часть',
            items: [{ title: 'Смотреть всё равно', ok: true }, { title: 'Отмена' }],
            onSelect: function (a) { if (a.ok) run(); else Lampa.Controller.toggle('content'); },
            onBack: function () { Lampa.Controller.toggle('content'); }
        });
    }

    function watch(item) {
        confirmPartial(item, function () { watchByHash(item.hash, (item.meta && item.meta.title) || item.name); });
    }

    // ───────── Открытие загрузки: НАСТОЯЩАЯ полная карточка (вся инфа), но в режиме «одна кнопка» ─────────
    function openDownload(item) {
        var m = item.meta || {};
        if (m.id) {
            Lampa.Activity.push({
                url: '', component: 'full', id: m.id,
                method: m.media_type === 'tv' ? 'tv' : 'movie',
                card: m, source: m.source || 'tmdb',
                qdl_hash: item.hash,   // маркер: открыто из «Загрузок» → addButton оставит одну кнопку «Смотреть»
                qdl_progress: (typeof item.progress === 'number' ? item.progress : 1)   // для гейта недокачанного на карточке
            });
        } else {
            confirmPartial(item, function () { watchByHash(item.hash, item.name); });   // нет метаданных → просто играем
        }
    }

    function badge(val, label) {
        return '<span style="display:inline-flex;align-items:center;gap:.35em;background:rgba(255,255,255,.14);padding:.3em .65em;border-radius:.45em;margin:0 .5em .5em 0;font-size:1em"><b>' + esc(val) + '</b><span style="opacity:.55;font-size:.78em">' + esc(label) + '</span></span>';
    }
    function chip(txt) {
        return '<span style="display:inline-block;background:rgba(255,255,255,.1);padding:.3em .65em;border-radius:.45em;margin:0 .5em .5em 0;font-size:1em">' + esc(txt) + '</span>';
    }

    function ComponentCard(object) {
        var item = object.qdl || {};
        var scroll = new Lampa.Scroll({ mask: true, over: true });
        var html = $('<div></div>');

        this.create = function () {
            var m = item.meta || {};
            var pct = Math.round((item.progress || 0) * 100);
            var kind = m.media_type === 'tv' ? 'Сериал' : 'Фильм';
            var rt = m.runtime ? (Math.floor(m.runtime / 60) + ':' + ('0' + (m.runtime % 60)).slice(-2)) : '';
            var meta1 = [m.year, (m.countries && m.countries.length ? m.countries.slice(0, 2).join(', ') : ''), kind, rt].filter(Boolean).join('   ·   ');
            var genres = (m.genres && m.genres.length) ? m.genres.slice(0, 4).join(', ') : '';

            var badges = '';
            if (m.vote_average) badges += badge((Math.round(m.vote_average * 10) / 10), 'TMDB');
            if (m.age) badges += chip(/\d$/.test(String(m.age)) ? m.age + '+' : m.age);
            if (m.status) badges += chip(m.status === 'Released' ? 'Выпущен' : (m.status === 'Ended' ? 'Завершён' : (m.status === 'Returning Series' ? 'Идёт' : m.status)));
            if (m.number_of_seasons) badges += chip('Сезонов: ' + m.number_of_seasons);
            badges += chip(pct < 100 ? pct + '% загружено' : '✓ загружено');

            var bg = m.backdrop_path ? tmdbImg('t/p/w1280' + m.backdrop_path) : '';
            var bgDiv = bg
                ? '<div style="position:absolute;inset:0;background:url(' + bg + ') center top/cover no-repeat;opacity:.22"></div>' +
                  '<div style="position:absolute;inset:0;background:linear-gradient(90deg,rgba(0,0,0,.65),rgba(0,0,0,.15))"></div>'
                : '';

            var body = $(
                '<div style="position:relative;min-height:100%">' +
                  bgDiv +
                  '<div style="position:relative;display:flex;gap:2.5em;padding:2.5em">' +
                    '<img class="qdl-poster" src="' + posterUrl(item) + '" style="width:17em;height:25.5em;object-fit:cover;border-radius:1em;background:#222;flex:none;box-shadow:0 1em 3em rgba(0,0,0,.5)">' +
                    '<div style="flex:1;min-width:0">' +
                      '<div style="font-size:2.6em;font-weight:700;line-height:1.05">' + esc(m.title || item.name) + '</div>' +
                      (m.original_title && m.original_title !== m.title ? '<div style="opacity:.5;font-size:1.3em;margin-top:.2em">' + esc(m.original_title) + '</div>' : '') +
                      (m.tagline ? '<div style="opacity:.6;font-style:italic;font-size:1.2em;margin-top:.5em">«' + esc(m.tagline) + '»</div>' : '') +
                      '<div style="margin:1.1em 0 .5em">' + badges + '</div>' +
                      (meta1 ? '<div style="opacity:.7;font-size:1.15em;margin-bottom:.3em">' + esc(meta1) + '</div>' : '') +
                      (genres ? '<div style="opacity:.7;font-size:1.15em;margin-bottom:1em">' + esc(genres) + '</div>' : '') +
                      '<div style="font-size:1.2em;line-height:1.55;opacity:.92;max-width:46em;margin-bottom:1.7em">' + esc(m.overview || 'Нет описания.') + '</div>' +
                      '<div class="qdl-watch selector" style="display:inline-flex;align-items:center;gap:.4em;padding:.75em 2em;background:rgba(255,255,255,.16);border-radius:.6em;font-size:1.4em">▶&nbsp;Смотреть</div>' +
                    '</div>' +
                  '</div>' +
                '</div>'
            );
            body.find('.qdl-poster').on('error', function () { this.src = './img/img_broken.svg'; });
            body.find('.qdl-watch').on('hover:enter', function () { watch(item); });

            scroll.append(body);
            html.append(scroll.render());

            // если метаданных нет — дотянем и перерисуем карточку (с защитой от replace после ухода)
            var self = this;
            if (!item.meta) {
                enrich(item.name, function (card) {
                    if (!card || self.destroyed) return;
                    saveMeta(item.hash, card, function (r) {
                        if (self.destroyed) return;
                        item.meta = slimCard(card);
                        if (r && r.has_poster) item.has_poster = true;
                        try { if (Lampa.Activity.own && !Lampa.Activity.own(self)) return; Lampa.Activity.replace(); } catch (e) {}
                    });
                });
            }
            return this.render();
        };

        this.render = function () { return html; };
        this.start = function () {
            injectCss();
            Lampa.Controller.add('content', {
                toggle: function () {
                    Lampa.Controller.collectionSet(scroll.render());
                    Lampa.Controller.collectionFocus(false, scroll.render());
                },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };
        this.pause = function () {};
        this.stop = function () {};
        this.destroy = function () { scroll.destroy(); html.remove(); };
    }

    // ───────── Грид «Загрузки» (вертикальные карточки-постеры) ─────────
    function ComponentDownloads(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div class="category-full"></div>');
        var last;
        var builtStamp = -1;   // colStamp на момент build: разошёлся — грид устарел

        this.create = function () {
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            // список и коллекции грузим параллельно; ошибка любого — мягкая деградация в []
            var list = null, cols = null, done = 0;
            function ready() { if (++done === 2) comp.build(list || [], cols || []); }
            network.silent(API + '/qdl/list', function (r) { list = r; ready(); }, function () { ready(); });
            network.silent(API + '/qdl/collections', function (r) { cols = r; ready(); }, function () { ready(); });
            return this.render();
        };

        this.build = function (list, collections) {
            builtStamp = colStamp;
            var g = groupDownloads(list || [], collections || []);

            if (object.collection_id) {
                // под-грид коллекции: только её фильмы, в порядке добавления
                var cg = g.cols.filter(function (c) { return c.col.id === object.collection_id; })[0];
                if (!cg)
                    body.append($('<div style="padding:2em;font-size:1.4em;opacity:.7">Коллекции больше нет.</div>'));
                else
                    cg.items.forEach(function (t) { comp.append(t, { collection: cg.col }); });
            } else {
                // главный грид: коллекции и фильмы вперемешку, по дате загрузки (новое сверху)
                if (!g.cols.length && !g.singles.length)
                    body.append($('<div style="padding:2em;font-size:1.4em;opacity:.7">В «Загрузках» пока пусто. Нажми «Скачать» на карточке фильма.</div>'));
                gridOrder(g).forEach(function (e) {
                    if (e.col) comp.appendCollection(e.col);
                    else comp.append(e.item);
                });
            }

            this.activity.loader(false);
            this.activity.toggle();
        };

        // карточка-папка коллекции: обложка = постер фильма-обложки, бейдж с количеством
        this.appendCollection = function (c) {
            var el = Lampa.Template.get('card', { title: c.col.title || 'Коллекция', release_year: '' });
            el.addClass('qdl-col-card');

            var img = el.find('.card__img');
            img.attr('src', posterUrl(c.cover));
            img.on('error', function () { this.src = './img/img_broken.svg'; });
            healPoster(c.cover, img);   // обложка коллекции тоже лечится

            var view = el.find('.card__view'); if (!view.length) view = el;
            view.append('<div style="position:absolute;left:.4em;top:.4em;background:rgba(110,60,220,.9);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">📁 ' + c.items.length + '</div>');

            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:enter', function () { openCollection(c.col); });
            el.on('hover:long', function () { collectionMenu(c.col, c.items); });

            body.append(el);
        };

        this.append = function (t, ctx) {
            var meta = t.meta || {};
            var pct = Math.round((t.progress || 0) * 100);

            // обычная ВЕРТИКАЛЬНАЯ карточка-постер (без card--collection!)
            var el = Lampa.Template.get('card', { title: meta.title || t.name, release_year: meta.year || '' });

            var img = el.find('.card__img');
            img.attr('src', posterUrl(t));
            img.on('error', function () { this.src = './img/img_broken.svg'; });

            var view = el.find('.card__view'); if (!view.length) view = el;
            view.append(t.local || t.state === 'local'
                ? '<div style="position:absolute;left:.4em;top:.4em;background:rgba(30,120,220,.9);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">MP4</div>'
                : pct < 100
                    ? '<div style="position:absolute;left:.4em;top:.4em;background:rgba(0,0,0,.75);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">' + pct + '%</div>'
                    : '<div style="position:absolute;left:.4em;top:.4em;background:rgba(20,160,40,.9);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">✓</div>');

            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:enter', function () { openDownload(t); });
            el.on('hover:long', function () { quickMenu(t, ctx); });

            body.append(el);

            // нет метаданных → ищем в TMDB + тянем полные детали, кэшируем, обновляем карточку
            if (!t.meta) {
                enrich(t.name, function (card) {
                    if (!card) return;
                    saveMeta(t.hash, card, function (r) {
                        t.meta = slimCard(card);
                        el.find('.card__title').text(card.title || card.name || t.name);
                        if (r && r.has_poster) { t.has_poster = true; el.find('.card__img').attr('src', API + '/qdl/poster?hash=' + t.hash + '&t=' + Date.now()); }
                    });
                });
            }
            else healPoster(t, img);   // мета есть, но постер на сервере не скачался → ретрай
        };

        this.render = function () { return html; };
        this.start = function () {
            // коллекции менялись, пока грид был в фоне (мутация в под-гриде и т.п.) → перерисовать
            if (builtStamp !== -1 && builtStamp !== colStamp) { Lampa.Activity.replace(); return; }
            Lampa.Controller.add('content', {
                toggle: function () {
                    Lampa.Controller.collectionSet(scroll.render());
                    Lampa.Controller.collectionFocus(last || false, scroll.render());
                },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };
        this.pause = function () {};
        this.stop = function () {};
        this.destroy = function () { network.clear(); scroll.destroy(); html.remove(); };
    }

    // ───────── Уведомления о скачанных сериях (тост + центр уведомлений) ─────────
    function relTime(iso) {
        try {
            var d = new Date(iso), now = new Date();
            var pad = function (n) { return (n < 10 ? '0' : '') + n; };
            var hm = pad(d.getHours()) + ':' + pad(d.getMinutes());
            if (d.toDateString() === now.toDateString()) return 'сегодня ' + hm;
            var y = new Date(now); y.setDate(now.getDate() - 1);
            if (d.toDateString() === y.toDateString()) return 'вчера ' + hm;
            var days = Math.floor((now - d) / 86400000);
            if (days >= 0 && days < 7) return days + ' дн назад';
            return pad(d.getDate()) + '.' + pad(d.getMonth() + 1) + '.' + d.getFullYear();
        } catch (e) { return ''; }
    }

    function updateNotiBadge(unread) {
        var txt = unread > 99 ? '99+' : String(unread);
        // левое меню (пункт «Уведомления»)
        try {
            var b = $('.menu .qdl-noti-menu .qdl-noti-badge');
            if (b.length) {
                if (unread > 0) b.text(txt).css('display', '');
                else b.css('display', 'none');
            }
        } catch (e) {}
        // хедер (наша иконка) — обновляем независимо от меню
        try {
            var head = $('.qdl-noti-head');
            if (head.length) {
                head.toggleClass('active', unread > 0);
                var hb = head.find('.qdl-noti-head-badge');
                if (unread > 0) hb.text(txt).css('display', '');
                else hb.css('display', 'none');
            }
        } catch (e) {}
    }

    // опрос ленты: бейдж непрочитанных + тост для появившихся с прошлого опроса
    function pollNotifications() {
        req(API + '/qdl/notifications', function (r) {
            if (!r) return;
            var items = r.items || [];
            updateNotiBadge(r.unread || 0);

            var lastId = 0;
            try { lastId = Lampa.Storage.get('qdl_noti_lastid', 0) || 0; } catch (e) {}
            var fresh = items.filter(function (x) { return x.id > lastId; });
            if (!fresh.length) return;

            var maxId = items.reduce(function (mx, x) { return Math.max(mx, x.id); }, lastId);
            try { Lampa.Storage.set('qdl_noti_lastid', maxId); } catch (e) {}

            // на самом первом опросе (lastId===0) не спамим историей — только запоминаем точку отсчёта
            if (lastId > 0) {
                // SWITCH (предложение сменить раздачу) / INFO — это НЕ «скачанная серия»: свой тост без «скачана»
                var special = fresh.filter(function (x) { return x.kind === 'SWITCH' || x.kind === 'INFO'; });
                var dl = fresh.filter(function (x) { return x.kind !== 'SWITCH' && x.kind !== 'INFO'; });
                special.forEach(function (x) { Lampa.Noty.show((x.kind === 'SWITCH' ? '🔀 ' : '📺 ') + esc(x.title) + ' — ' + esc(x.label)); });
                if (dl.length === 1) Lampa.Noty.show('📺 ' + esc(dl[0].title) + ' — ' + esc(dl[0].label) + ' скачана');
                else if (dl.length > 1) Lampa.Noty.show('📺 Скачано новых серий: ' + dl.length);
            }
        });
    }

    // открыть карточку загрузки из уведомления (по hash);
    // kind=SWITCH — предложение переключить заброшенную раздачу на более полную (подтверждение)
    function openNotification(n) {
        if (n && n.kind === 'SWITCH') {
            Lampa.Select.show({
                title: (n.title ? n.title + ': ' : '') + (n.label || 'Переключить на более полную раздачу?'),
                items: [
                    { title: 'Переключить (сезон перекачается)', ok: true },
                    { title: 'Оставить как есть' }
                ],
                onSelect: function (a) {
                    Lampa.Controller.toggle('content');
                    req(API + '/qdl/watch/switch?hash=' + n.hash + '&accept=' + (a.ok ? 1 : 0), function (r) {
                        if (a.ok) Lampa.Noty.show(r && r.success ? '✓ Переключено — сезон перекачивается' : 'Не вышло: ' + ((r && r.error) || 'ошибка'));
                        else Lampa.Noty.show('Оставили текущую раздачу');
                    }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
            return;
        }
        req(API + '/qdl/list', function (list) {
            var it = (list || []).filter(function (x) { return x.hash === n.hash; })[0];
            if (it) openDownload(it);
            else if (n.hash) watchByHash(n.hash, n.title);
            else Lampa.Noty.show('Загрузка не найдена');
        }, function () { if (n.hash) watchByHash(n.hash, n.title); });
    }

    // Центр уведомлений (история): постер · сериал · серия · время
    function ComponentNotifications(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div class="category-full"></div>');
        var last;

        this.create = function () {
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            network.silent(API + '/qdl/notifications', function (r) { comp.build((r && r.items) || []); }, function () { comp.build([]); });
            return this.render();
        };

        this.build = function (items) {
            if (!items.length)
                body.append($('<div style="padding:2em;font-size:1.4em;opacity:.7">Пока нет уведомлений. Включи «🔔 Следить за новыми сериями» в «Загрузках».</div>'));

            items.forEach(function (n) { comp.append(n); });

            // открыли центр → помечаем всё прочитанным, бейдж гаснет
            req(API + '/qdl/notifications/read', function () { updateNotiBadge(0); });

            this.activity.loader(false);
            this.activity.toggle();
        };

        this.append = function (n) {
            var poster = n.hash ? (API + '/qdl/poster?hash=' + n.hash) : './img/img_broken.svg';
            var el = $(
                '<div class="qdl-noti-row selector" style="display:flex;align-items:center;gap:1em;padding:1em;margin:.35em .6em;background:rgba(255,255,255,.05);border-radius:.7em">' +
                  '<img src="' + poster + '" style="width:3.6em;height:5.4em;object-fit:cover;border-radius:.4em;background:#222;flex:none">' +
                  '<div style="flex:1;min-width:0">' +
                    '<div style="font-size:1.3em;font-weight:600">' + esc(n.title || 'Сериал') + '</div>' +
                    '<div style="opacity:.85;font-size:1.15em;margin-top:.25em">' + esc(n.label || '') + '</div>' +
                    '<div style="opacity:.5;font-size:.95em;margin-top:.25em">' + esc(relTime(n.created)) + '</div>' +
                  '</div>' +
                '</div>'
            );
            el.find('img').on('error', function () { this.src = './img/img_broken.svg'; });
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:enter', function () { openNotification(n); });
            body.append(el);
        };

        this.render = function () { return html; };
        this.start = function () {
            Lampa.Controller.add('content', {
                toggle: function () {
                    Lampa.Controller.collectionSet(scroll.render());
                    Lampa.Controller.collectionFocus(last || false, scroll.render());
                },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };
        this.pause = function () {};
        this.stop = function () {};
        this.destroy = function () { network.clear(); scroll.destroy(); html.remove(); };
    }

    function buildNotiMenuItem() {
        var item = $('<li class="menu__item selector qdl-noti-menu"><div class="menu__ico">' + BELL + '</div><div class="menu__text">Уведомления<span class="qdl-noti-badge" style="display:none;margin-left:.6em;background:#d33;color:#fff;border-radius:1em;padding:0 .55em;font-size:.8em;font-weight:700">0</span></div></li>');
        item.on('hover:enter', function () {
            Lampa.Activity.push({ url: '', title: 'Уведомления', component: 'qdl_notifications', page: 1 });
        });
        return item;
    }

    // ───────── Коллекции в «Загрузках» (стакинг фильмов, серверное хранение) ─────────
    // Инвариант сервера: фильм максимум в одной коллекции; пустая коллекция удаляется.
    // colStamp — счётчик мутаций: каждый живой грид запоминает свой стамп при build
    // и в start() перерисовывается, если коллекции менялись (например в под-гриде).
    var colStamp = 0;
    function touchCollections() { colStamp++; }

    function itemTitle(t) { return (t && t.meta && t.meta.title) || (t && t.name) || ''; }

    // list (/qdl/list) + collections (/qdl/collections) → { cols: [{col, items, cover}], singles: [...] }
    // мёртвые хэши (удалены мимо нашего API) отбрасываются, коллекция без живых фильмов не рендерится,
    // cover — объект фильма-обложки (фолбек: первый живой)
    function groupDownloads(list, collections) {
        list = list || []; collections = collections || [];
        var byHash = {}, inCol = {}, cols = [];
        list.forEach(function (t) { if (t && t.hash) byHash[t.hash] = t; });
        collections.forEach(function (col) {
            var items = ((col && col.hashes) || []).map(function (h) { return byHash[h]; }).filter(Boolean);
            if (!items.length) return;
            items.forEach(function (t) { inCol[t.hash] = true; });
            var cover = items.filter(function (t) { return t.hash === col.cover; })[0] || items[0];
            var added = 0;
            items.forEach(function (t) { var a = +t.added || 0; if (a > added) added = a; });
            cols.push({ col: col, items: items, cover: cover, added: added });
        });
        var singles = list.filter(function (t) { return t && t.hash && !inCol[t.hash]; });
        return { cols: cols, singles: singles };
    }

    // коллекции и одиночки вперемешку, по дате загрузки desc (дата коллекции = самая свежая её серия);
    // тай-брейк — прежний порядок (коллекции, затем одиночки): не полагаемся на стабильность sort старых TV
    function gridOrder(g) {
        var entries = [];
        g.cols.forEach(function (c) { entries.push({ col: c, added: +c.added || 0, idx: entries.length }); });
        g.singles.forEach(function (t) { entries.push({ item: t, added: +t.added || 0, idx: entries.length }); });
        entries.sort(function (a, b) { return (b.added - a.added) || (a.idx - b.idx); });
        return entries;
    }

    // автоимя коллекции: общий пословный префикс («Дюна» + «Дюна: Часть вторая» → «Дюна»)
    function commonPrefixTitle(a, b) {
        a = String(a || '').trim(); b = String(b || '').trim();
        function norm(w) {
            return w.toLowerCase().replace(/ё/g, 'е')
                .replace(/^[\s:.,!?«»"'()\[\]\-–—]+/, '').replace(/[\s:.,!?«»"'()\[\]\-–—]+$/, '');
        }
        var wa = a.split(/\s+/), wb = b.split(/\s+/), out = [], n = Math.min(wa.length, wb.length);
        for (var i = 0; i < n; i++) {
            if (norm(wa[i]) && norm(wa[i]) === norm(wb[i])) out.push(wa[i]); else break;
        }
        var title = out.join(' ').replace(/[\s:.,\-–—]+$/, '');
        return title || a || b || 'Коллекция';
    }

    // пункты пикера «Добавить в коллекцию»: сверху существующие коллекции (📁 + счётчик),
    // ниже одиночные фильмы (выбор фильма = создать новую коллекцию из двух)
    function buildCollectionPicker(current, collections, list) {
        var g = groupDownloads(list, collections), items = [];
        g.cols.forEach(function (c) {
            items.push({ title: '📁 ' + (c.col.title || 'Коллекция'), subtitle: 'фильмов: ' + c.items.length, col: c.col });
        });
        g.singles.forEach(function (t) {
            if (current && t.hash === current.hash) return;
            var year = t.meta && t.meta.year ? ' (' + t.meta.year + ')' : '';
            items.push({ title: '🎬 ' + itemTitle(t) + year, subtitle: 'новая коллекция из двух фильмов', item: t });
        });
        return items;
    }

    function openCollection(col) {
        Lampa.Activity.push({ url: '', title: col.title || 'Коллекция', component: 'qdl_downloads', collection_id: col.id, page: 1 });
    }

    function colPost(url, data, ok) {
        post(API + url, data, function (r) {
            if (r && r.success) { touchCollections(); ok(r); }
            else Lampa.Noty.show('Не получилось — попробуй ещё раз');
        }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
    }

    function addToCollection(t) {
        req(API + '/qdl/collections', function (collections) {
            req(API + '/qdl/list', function (list) {
                var items = buildCollectionPicker(t, collections || [], list || []);
                if (!items.length) { Lampa.Noty.show('Нет других фильмов или коллекций — скачай что-нибудь ещё'); return; }
                Lampa.Select.show({
                    title: 'Куда добавить «' + itemTitle(t) + '»',
                    items: items,
                    onSelect: function (b) {
                        if (b.col)
                            colPost('/qdl/collections/add', { id: b.col.id, hash: t.hash }, function () {
                                Lampa.Noty.show('✓ Добавлено в «' + (b.col.title || 'Коллекция') + '»');
                                Lampa.Activity.replace();
                            });
                        else if (b.item)
                            colPost('/qdl/collections/create', { title: commonPrefixTitle(itemTitle(t), itemTitle(b.item)), hashes: t.hash + ',' + b.item.hash }, function (r) {
                                Lampa.Noty.show('✓ Коллекция «' + ((r.collection && r.collection.title) || '') + '» создана');
                                Lampa.Activity.replace();
                            });
                    },
                    onBack: function () { Lampa.Controller.toggle('content'); }
                });
            }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
        }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
    }

    function renameCollection(col, items) {
        function save(name) {
            name = String(name || '').trim();
            if (!name) return;
            colPost('/qdl/collections/update', { id: col.id, title: name }, function () {
                col.title = name;
                Lampa.Noty.show('✓ Переименовано');
                Lampa.Activity.replace();
            });
        }
        // фронт Lampa качается в рантайме — текстовый ввод может отсутствовать, feature-detect
        if (Lampa.Input && Lampa.Input.edit) {
            Lampa.Input.edit({ title: 'Название коллекции', value: col.title || '', free: true, nosave: true }, function (v) { if (v) save(v); });
        } else {
            // fallback (и вообще удобнее на ТВ): варианты — общий префикс + названия фильмов внутри
            var seen = {}, opts = [];
            function add(name) {
                name = String(name || '').trim();
                if (name && !seen[name.toLowerCase()]) { seen[name.toLowerCase()] = 1; opts.push(name); }
            }
            if (items.length > 1) add(commonPrefixTitle(itemTitle(items[0]), itemTitle(items[1])));
            items.forEach(function (t) { add(itemTitle(t)); });
            if (!opts.length) { Lampa.Noty.show('Нет вариантов названия'); return; }
            Lampa.Select.show({
                title: 'Название коллекции',
                items: opts.map(function (n) { return { title: n, name: n }; }),
                onSelect: function (b) { save(b.name); },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }
    }

    function chooseCover(col, items) {
        Lampa.Select.show({
            title: 'Обложка коллекции',
            items: items.map(function (t) {
                return { title: (t.hash === col.cover ? '✓ ' : '') + itemTitle(t), hash: t.hash };
            }),
            onSelect: function (b) {
                colPost('/qdl/collections/update', { id: col.id, cover: b.hash }, function () {
                    col.cover = b.hash;
                    Lampa.Noty.show('✓ Обложка обновлена');
                    Lampa.Activity.replace();
                });
            },
            onBack: function () { Lampa.Controller.toggle('content'); }
        });
    }

    // long-press по карточке коллекции. «Удалить с файлами» тут сознательно НЕ даём:
    // расформирование только разгруппировывает, файлы не трогает
    function collectionMenu(col, items) {
        Lampa.Select.show({
            title: (col.title || 'Коллекция') + ' · фильмов: ' + items.length,
            items: [
                { title: 'Открыть', act: 'open' },
                { title: '✏️ Переименовать', act: 'rename' },
                { title: '🖼 Сменить обложку', act: 'cover' },
                { title: '📤 Расформировать', act: 'dissolve' }
            ],
            onSelect: function (b) {
                if (b.act === 'open') openCollection(col);
                else if (b.act === 'rename') renameCollection(col, items);
                else if (b.act === 'cover') chooseCover(col, items);
                else if (b.act === 'dissolve') {
                    Lampa.Select.show({
                        title: 'Расформировать «' + (col.title || 'Коллекция') + '»? Фильмы останутся в «Загрузках»',
                        items: [{ title: 'Расформировать', ok: true }, { title: 'Отмена' }],
                        onSelect: function (a) {
                            if (!a.ok) { Lampa.Controller.toggle('content'); return; }
                            colPost('/qdl/collections/dissolve', { id: col.id }, function () {
                                Lampa.Noty.show('Коллекция расформирована');
                                Lampa.Activity.replace();
                            });
                        },
                        onBack: function () { Lampa.Controller.toggle('content'); }
                    });
                }
            },
            onBack: function () { Lampa.Controller.toggle('content'); }
        });
    }

    // Запуск транскода. Сериал под слежением — выбор режима: оверлей (торрент и слежение
    // живут, новые серии транскодятся автоматически) или финализация (как фильм).
    function startTranscode(t) {
        var title = (t.meta && t.meta.title) || t.name;
        var run = function (mode) {
            req(API + '/qdl/transcode?hash=' + t.hash + (mode ? '&mode=' + mode : ''), function (r) {
                if (!r || !r.success) { Lampa.Noty.show('Транскодирование: ' + ((r && r.error) || 'ошибка')); return; }
                if (r.queued > 1) Lampa.Noty.show('🎬 В очереди (' + r.queued + ') — сообщу о прогрессе');
                else if (r.files > 1) Lampa.Noty.show('🎬 Транскодирование запущено (' + r.files + ' серий) — сообщу о прогрессе');
                else Lampa.Noty.show('🎬 Транскодирование запущено — это займёт заметное время, сообщу о прогрессе');
                pollTranscode(t.hash, title);
            }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
        };
        if (!t.watched) { run(null); return; }
        req(API + '/qdl/files?hash=' + t.hash, function (files) {
            if (videoFiles(files).length < 2) { run(null); return; }
            Lampa.Select.show({
                title: 'Сериал под слежением — как транскодировать?',
                items: [
                    { title: '🔔 Оставить слежение: новые серии транскодятся сами', subtitle: 'торрент остаётся (место ×2, пока идёт сериал)', mode: 'overlay' },
                    { title: '✔ Завершить: торрент удалится, слежение снимется', subtitle: 'новые серии перестанут приходить', mode: 'finalize' },
                    { title: 'Отмена' }
                ],
                onSelect: function (a) { if (a.mode) run(a.mode); else Lampa.Controller.toggle('content'); },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { run(null); });
    }

    function quickMenu(t, ctx) {
        var items = [
            { title: 'Открыть карточку', act: 'page' },
            { title: '▶ Смотреть (оффлайн)', act: 'play' },
            { title: '🔊 Озвучка', act: 'audio' }
        ];
        if (canTranscode(t)) items.push({ title: '🎬 Транскодировать в MP4 (для браузера)', act: 'mp4' });
        if (!t.local && t.state !== 'local')
            items.push({ title: t.watched ? '🔔 Не следить за новыми сериями' : '🔔 Следить за новыми сериями', act: 'watch' });
        // в под-гриде коллекции — «Убрать», в общем гриде/карточке — «Добавить»
        if (ctx && ctx.collection) items.push({ title: '📁 Убрать из коллекции', act: 'uncol' });
        else items.push({ title: '📁 Добавить в коллекцию', act: 'addcol' });
        items.push({ title: '🗑 Удалить (с файлами)', act: 'del' });

        Lampa.Select.show({
            title: (t.meta && t.meta.title) || t.name,
            items: items,
            onSelect: function (b) {
                if (b.act === 'page') openDownload(t);
                else if (b.act === 'play') watch(t);
                else if (b.act === 'audio') {
                    req(API + '/qdl/audio?hash=' + t.hash + '&index=-1', function (opts) {
                        opts = opts || [];
                        if (!opts.length) { Lampa.Noty.show('Аудиодорожек не найдено'); return; }
                        Lampa.Select.show({
                            title: 'Озвучка',
                            items: opts.map(function (o) { return { title: o.label, id: o.id }; }),
                            onSelect: function (s) { setAudioPref(t.hash, s.id); Lampa.Noty.show('Озвучка: ' + s.title); },
                            onBack: function () { Lampa.Controller.toggle('content'); }
                        });
                    });
                }
                else if (b.act === 'watch') {
                    if (t.watched)
                        req(API + '/qdl/watch/remove?hash=' + t.hash, function () { t.watched = false; Lampa.Noty.show('Слежение выключено'); });
                    else
                        req(API + '/qdl/watch?hash=' + t.hash, function (r) {
                            if (r && r.success) { t.watched = true; Lampa.Noty.show('✓ Слежу за новыми сериями'); }
                            else Lampa.Noty.show('Не вышло — перекачай раздачу и попробуй снова');
                        });
                }
                else if (b.act === 'mp4') startTranscode(t);
                else if (b.act === 'addcol') addToCollection(t);
                else if (b.act === 'uncol') {
                    colPost('/qdl/collections/remove', { id: ctx.collection.id, hash: t.hash }, function (r) {
                        if (r.deleted) { Lampa.Noty.show('Коллекция удалена — это был последний фильм'); Lampa.Activity.backward(); }
                        else { Lampa.Noty.show('Убрано из коллекции'); Lampa.Activity.replace(); }
                    });
                }
                else if (b.act === 'del') {
                    // подтверждение: одно случайное нажатие не должно безвозвратно удалять файлы
                    Lampa.Select.show({
                        title: 'Удалить «' + ((t.meta && t.meta.title) || t.name) + '» с файлами?',
                        items: [{ title: 'Удалить', ok: true }, { title: 'Отмена' }],
                        onSelect: function (a) {
                            if (!a.ok) { Lampa.Controller.toggle('content'); return; }
                            req(API + '/qdl/delete?hash=' + t.hash + '&deleteFiles=true', function () {
                                dropAudioPref(t.hash);   // подчистить запомненную озвучку (localStorage)
                                Lampa.Noty.show('Удалено');
                                Lampa.Activity.replace();
                            });
                        },
                        onBack: function () { Lampa.Controller.toggle('content'); }
                    });
                }
            },
            onBack: function () { Lampa.Controller.toggle('content'); }
        });
    }

    // ───────── Поиск раздач + кнопка «Скачать» ─────────
    // короткая дата раздачи для списка: дд.мм.гг
    function shortDate(iso) {
        try {
            var d = new Date(iso);
            if (isNaN(d.getTime())) return '';
            var y = d.getFullYear();
            if (y < 2000 || y > 2100) return '';   // битые PublishDate не показываем
            var p = function (n) { return (n < 10 ? '0' : '') + n; };
            return p(d.getDate()) + '.' + p(d.getMonth() + 1) + '.' + String(y).slice(2);
        } catch (e) { return ''; }
    }

    // строка под раздачей: ⭐-рекомендуемая получает серверное «почему» (why), остальные — факты.
    // Порядок списка — серверный (умный скоринг), клиент НЕ пересортировывает.
    function torrentSubtitle(t, isSerial) {
        var codecBad = t.codec === 'hevc' || t.codec === 'av1';   // браузер такое не декодирует (§Y)
        var parts = [];
        if (codecBad) parts.push('⚠ ' + t.codec.toUpperCase());
        if (isSerial === 2 && t.watchable) parts.push('🔔');       // login-трекер: работает докачка/слежение
        if (t.rec && t.why) {
            parts.push(t.why);
            if (t.size) parts.push(t.size);
            if (t.tracker) parts.push(t.tracker);
        } else {
            if (t.ep && t.ep.total) parts.push('серии: ' + t.ep.have + ' из ' + t.ep.total + (t.ep.ongoing ? ' ▶' : ''));
            if (t.quality) parts.push(t.quality + 'p');
            if (t.size) parts.push(t.size);
            if (t.tracker) parts.push(t.tracker);
            if (t.sid) parts.push('сидов: ' + t.sid);
            var d = t.date ? shortDate(t.date) : '';
            if (d) parts.push(d);
        }
        return parts.filter(Boolean).join('  •  ');
    }

    function chooseAndDownload(movie) {
        movie = movie || {};
        var title = movie.title || movie.name || '';
        var original = movie.original_title || movie.original_name || '';
        var year = ((movie.release_date || movie.first_air_date || '') + '').slice(0, 4);
        // сериал → is_serial=2, фильм → 1 (как в нативном поиске Lampa)
        var isSerial = (movie.media_type === 'tv' || movie.original_name || movie.number_of_seasons) ? 2 : 1;
        var season = movie.number_of_seasons || '';
        var search = title || original;
        if (!search) { Lampa.Noty.show('Не удалось определить название'); return; }

        var apikey = '';
        try { apikey = Lampa.Storage.get('jackett_key', '') || ''; } catch (e) {}

        Lampa.Noty.show('Поиск раздач…');
        // ПОЛНЫЙ контекст → бэкенд бьёт в тот же индексатор, что нативный «через торрент»:
        // правильный фильм (а не саундтрек/однофамилец) + все трекеры
        var url = API + '/qdl/search?query=' + encodeURIComponent(search)
            + (title ? '&title=' + encodeURIComponent(title) : '')
            + (original ? '&title_original=' + encodeURIComponent(original) : '')
            + (year ? '&year=' + year : '')
            + '&is_serial=' + isSerial
            + (season ? '&season=' + season : '')
            + (apikey ? '&apikey=' + encodeURIComponent(apikey) : '');

        req(url, function (list) {
            if (!list || !list.length) { Lampa.Noty.show('Раздачи не найдены'); return; }

            Lampa.Select.show({
                title: 'Выбери раздачу для загрузки на диск',
                items: list.slice(0, 60).map(function (t) {
                    return {
                        title: (t.rec ? '⭐ ' : '') + t.title,
                        subtitle: torrentSubtitle(t, isSerial),
                        t: t
                    };
                }),
                onSelect: function (a) {
                    Lampa.Controller.toggle('content');
                    if (a.t.codec === 'hevc' || a.t.codec === 'av1')
                        Lampa.Noty.show(a.t.codec.toUpperCase() + ': в браузере без транскода не заиграет (после загрузки — долгое нажатие → «Транскодировать в MP4»)');
                    var q = a.t.magnet
                        ? ('magnet=' + encodeURIComponent(a.t.magnet))
                        : ('parselink=' + encodeURIComponent(a.t.parselink || ''));
                    Lampa.Noty.show('Добавляю в загрузки…');
                    // TMDB-контекст уезжает в links/<hash>.json (ctx) — фундамент охоты за сериями
                    req(API + '/qdl/add?' + q + '&title=' + encodeURIComponent(a.t.title || title) + '&query=' + encodeURIComponent(title)
                        + (original ? '&title_original=' + encodeURIComponent(original) : '')
                        + (year ? '&year=' + year : '')
                        + '&is_serial=' + isSerial
                        + (season ? '&season=' + season : ''), function (r) {
                        if (r && r.success) {
                            if (r.hash) saveMeta(r.hash, movie);   // кэшируем метаданные+постер
                            Lampa.Noty.show(r.duplicate ? 'Уже в «Загрузках»' : '✓ Добавлено в «Загрузки»');
                        } else Lampa.Noty.show('Ошибка: ' + ((r && r.error) || 'qBittorrent'));
                    }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { Lampa.Noty.show('Ошибка поиска раздач'); });
    }

    // «▶ Продолжить: Серия N» на карточке сериала — только когда есть что продолжать
    // (недосмотренная серия или следующая после досмотренных). Прогресс — Lampa.Timeline (это устройство).
    function addContinueButton(render, cont, hash, name, gateItem) {
        fetchEpisodes(hash, function (files) {
            var vids = mergedVideoFiles(files);
            if (vids.length < 2) return;
            var target = chooseContinue(vids, function (f) { return pickTimeline(hash, f); });
            if (!target || $('.qdl-continue-btn', render).length) return;
            var label = 'Продолжить · ' + epShort(target.name);
            var b = $('<div class="full-start__button selector qdl-continue-btn">' + ICON + '<span>' + esc(label) + '</span></div>');
            b.on('hover:enter', function () {
                confirmPartial(gateItem, function () { playEpisode(hash, vids, target); });
            });
            cont.prepend(b);
        });
    }

    function addButton(e) {
        try {
            if (e.type !== 'complite' || !e.object || !e.object.activity) return;
            var render = e.object.activity.render();
            if (!render) return;

            var movie = (e.data && e.data.movie) ? e.data.movie : (e.object.card || {});
            var cont = $('.full-start__buttons', render);
            if (!cont.length) cont = $('.full-start-new__buttons', render);
            if (!cont.length) return;

            // тип/источник берём С ОТКРЫТОЙ КАРТОЧКИ (method/source активности), а не угадываем —
            // у TMDB id в movie и tv это РАЗНЫЕ объекты, ошибка типа = другой фильм
            var active = (function () { try { return Lampa.Activity.active() || {}; } catch (e) { return {}; } })();
            if (movie) {
                if (!movie.media_type && active.method) movie.media_type = active.method;
                if (!movie.source && active.source) movie.source = active.source;
            }

            // открыто из «Загрузок» (полная карточка, режим одной кнопки)
            if (active.qdl_hash) {
                injectCss();
                render.addClass('qdl-only');                 // CSS прячет все прочие кнопки
                if (!$('.qdl-watch-btn', render).length) {
                    var w = $('<div class="full-start__button selector qdl-watch-btn">' + ICON + '<span>Смотреть</span></div>');
                    w.on('hover:enter', function () {
                        // progress прокинут из openDownload; нет поля (восстановленная активность) → fail-open
                        confirmPartial({ hash: active.qdl_hash, progress: (typeof active.qdl_progress === 'number' ? active.qdl_progress : 1) },
                            function () { watchByHash(active.qdl_hash, movie.title || movie.name); });
                    });
                    // удержание (long-press) на кнопке → меню управления (следить/удалить) — для дискаверабилити
                    w.on('hover:long', function () {
                        req(API + '/qdl/list', function (list) {
                            var it = (list || []).filter(function (x) { return x.hash === active.qdl_hash; })[0] || { hash: active.qdl_hash, meta: movie };
                            quickMenu(it);
                        }, function () { quickMenu({ hash: active.qdl_hash, meta: movie }); });
                    });
                    cont.prepend(w);
                }
                // сериал с прогрессом просмотра → вторая кнопка «Продолжить: Серия N»
                addContinueButton(render, cont, active.qdl_hash, movie.title || movie.name,
                    { hash: active.qdl_hash, progress: (typeof active.qdl_progress === 'number' ? active.qdl_progress : 1) });
                return;   // НЕ добавляем «Скачать», прочие кнопки скрыты
            }

            if (!$('.qdl-download', render).length) {
                var btn = $('<div class="full-start__button selector qdl-download">' + ICON + '<span>Скачать</span></div>');
                btn.on('hover:enter', function () { chooseAndDownload(movie); });
                cont.append(btn);
            }

            // DMCA-карточка → режим «только Скачать»: прячем онлайн и прочие кнопки (.qdl-dmca),
            // нашу кнопку — в начало ряда. Список грузится лениво, класс навешивается по готовности.
            if (movie && movie.id) {
                var cat = movie.media_type || (movie.first_air_date || movie.name ? 'tv' : 'movie');
                whenDmca(function () {
                    if (!isDmca(cat, movie.id)) return;
                    injectCss();
                    render.addClass('qdl-dmca');
                    var dl = $('.qdl-download', render);
                    if (dl.length) cont.prepend(dl);
                });
            }

            // фильм уже скачан → ЗЕЛЁНАЯ «Смотреть (загружено)» + привязка метаданных.
            // Матчинг строгий — findDownload (id+media_type; имя — только для раздач без меты)
            if (movie && movie.id && !$('.qdl-watch-btn', render).length) {
                req(API + '/qdl/list', function (list) {
                    var hit = findDownload(list, movie);
                    if (hit && !hit.meta) saveMeta(hit.hash, movie);   // back-link карточка → безымянная загрузка
                    if (!hit || $('.qdl-watch-btn', render).length) return;

                    injectCss();
                    var play = $('<div class="full-start__button selector qdl-watch-btn">' + ICON + '<span>Смотреть (загружено)</span></div>');
                    play.on('hover:enter', function () { watch(hit); });
                    cont.prepend(play);
                    addContinueButton(render, cont, hit.hash, (hit.meta && hit.meta.title) || hit.name, hit);
                });
            }
        } catch (err) { console.log('qdl: addButton', err); }
    }

    // ───────── D1versy Rec: записи домашнего видеорегистратора ─────────
    // Сервер (Live.cs модуля) проксирует регистратор из LAN: каталог дня + сами mp4
    // (клиенту LAN-адрес не виден, снаружи всё идёт через наш origin).
    // Экран рассчитан на пульт: сверху день (по умолчанию сегодня), ниже — ТОЛЬКО те камеры,
    // у которых за этот день реально есть записи.

    function livePlural(n, one, few, many) {
        var a = Math.abs(n) % 100, b = a % 10;
        if (a > 10 && a < 20) return many;
        if (b === 1) return one;
        if (b > 1 && b < 5) return few;
        return many;
    }

    function liveDur(sec) {
        sec = Math.max(0, Math.round(sec || 0));
        var h = Math.floor(sec / 3600), m = Math.round((sec % 3600) / 60);
        if (h) return h + ' ч ' + ('0' + m).slice(-2) + ' мин';
        if (m) return m + ' мин';
        return sec + ' сек';
    }

    function liveSize(b) {
        if (!b) return '';
        var gb = b / 1073741824;
        return gb >= 1 ? (Math.round(gb * 10) / 10) + ' ГБ' : Math.round(b / 1048576) + ' МБ';
    }

    // YYYY-MM-DD ± дни (через локальный Date — без UTC-сдвигов на парсинге строки)
    function liveShift(ds, delta) {
        var p = String(ds || '').split('-');
        var d = p.length === 3 ? new Date(+p[0], +p[1] - 1, +p[2]) : new Date();
        d.setDate(d.getDate() + delta);
        return d.getFullYear() + '-' + ('0' + (d.getMonth() + 1)).slice(-2) + '-' + ('0' + d.getDate()).slice(-2);
    }

    function liveMsg(text) {
        return $('<div style="padding:2em 1.6em;font-size:1.4em;opacity:.7;line-height:1.5">' + esc(text) + '</div>');
    }

    function liveTimeline(rec) {
        try { return Lampa.Timeline.view(Lampa.Utils.hash('qdllive:' + rec.id)); } catch (e) { return null; }
    }

    // ── «Весь день одной записью» ──
    // Сервер склеивает куски суток в ОДИН HLS-поток (склейка регистратора: сегменты + DISCONTINUITY),
    // поэтому у дня один таймлайн и нет «следующего файла». Пока задние куски ремуксятся, плейлист
    // растёт сам — смотреть можно с первого готового. Ждём именно первый кусок, а не весь день.
    // Токен отменяет и «ушёл с экрана», и повторное нажатие: без него оставшийся жить setTimeout
    // через минуту открывал бы плеер поверх того, чем зритель уже занят.
    var liveDayToken = 0;
    function liveDayCancel() { liveDayToken++; }

    function livePlayDay(cam, date, label) {
        var my = ++liveDayToken;
        var tries = 0;

        // Первый ответ обычно приходит быстро; сообщение показываем, только если готовка затянулась.
        setTimeout(function () {
            if (my === liveDayToken) Lampa.Noty.show('Готовлю запись за день…');
        }, 700);

        // Каждый терминальный выход ЗАКРЫВАЕТ токен: иначе отложенный тост «Готовлю…» перетирал
        // бы финальное сообщение (на LAN ответ приходит быстрее 700 мс) и врал, что что-то идёт.
        function stop(msg) {
            if (my === liveDayToken) liveDayToken++;
            if (msg) Lampa.Noty.show(msg);
        }

        function fire(info) {
            if (my === liveDayToken) liveDayToken++;
            var item = {
                title: (cam.name || 'Камера') + (label ? '   ·   ' + label : ''),
                url: API + info.path
            };
            try {
                var tl = Lampa.Timeline.view(Lampa.Utils.hash('qdlliveday:' + cam.id + ':' + info.date));
                if (tl) {
                    // За текущий день запись РАСТЁТ (доезжают новые куски), а процент Lampa посчитала
                    // от прежней длины: досмотренный «конец дня» становился 98–100% и потом блокировал
                    // продолжение (Lampa не предлагает докрутку при percent ≥ 90). Позиция в секундах
                    // остаётся верной — пересчитываем процент от новой длины.
                    if (info.seconds && tl.time > 0) {
                        tl.duration = info.seconds;
                        tl.percent = Math.min(100, Math.round(tl.time / info.seconds * 100));
                    }
                    item.timeline = tl;
                }
            } catch (e) {}
            Lampa.Player.play(item);
            Lampa.Player.playlist([item]);
        }

        function poll() {
            if (my !== liveDayToken) return;
            req(API + '/qdl/live/day?camera=' + encodeURIComponent(cam.id) + (date ? '&date=' + encodeURIComponent(date) : ''),
                function (info) {
                    if (my !== liveDayToken) return;
                    if (!info || info.error) { stop((info && info.error) || 'Не вышло собрать запись'); return; }
                    if (info.empty) { stop('За этот день записей нет'); return; }
                    if (info.ready > 0) { fire(info); return; }
                    // всё готово, но играть нечего — все куски битые
                    if (info.complete) { stop('Записи за этот день не читаются'); return; }

                    if (++tries > 45) { stop('Регистратор слишком долго готовит запись'); return; }
                    if (tries % 10 === 0) Lampa.Noty.show('Ещё готовлю: ' + info.ready + ' из ' + info.total);
                    setTimeout(poll, 2000);
                },
                function () {
                    // разовый сетевой сбой — не приговор: считаем попыткой и продолжаем
                    if (my !== liveDayToken) return;
                    if (++tries > 45) { stop('Видеорегистратор не отвечает'); return; }
                    setTimeout(poll, 2000);
                });
        }

        poll();
    }

    // Плейлист = все записи камеры за день по отдельности (запасной путь: «Фрагменты»).
    function livePlay(cam, items, index) {
        if (!items || !items.length) { Lampa.Noty.show('Записей нет'); return; }
        var playlist = items.map(function (r) {
            var item = { title: r.start + ' – ' + r.end + '   ·   ' + (cam.name || 'Камера'), url: API + '/qdl/live/stream?id=' + r.id };
            var tl = liveTimeline(r);
            if (tl) item.timeline = tl;
            return item;
        });
        index = Math.max(0, Math.min(index || 0, playlist.length - 1));
        Lampa.Player.play(playlist[index]);
        Lampa.Player.playlist(playlist);
    }

    // ── D1versy Live: ЭФИР — сетка подключённых камер ──
    // Тайл = живой кадр (обновляется сам) + имя + статус. Enter — эфир камеры фуллскрином
    // в плеере (rolling-HLS через наш прокси /qdl/live/watch/*). Поток на регистраторе общий
    // на всех зрителей, stop не зовём никогда.
    function liveWatchPlay(cam) {
        var my = ++liveDayToken;   // общий токен отмены с готовкой дня: уход с экрана/повторное нажатие глушит опрос
        var tries = 0;

        setTimeout(function () {
            if (my === liveDayToken) Lampa.Noty.show('Включаю эфир…');
        }, 700);

        // Терминальный выход закрывает токен — иначе отложенный тост «Включаю эфир…» перетирает
        // финальное сообщение (оффлайн mac-камера отвечает за десятки мс, куда быстрее 700 мс тоста).
        function stop(msg) {
            if (my === liveDayToken) liveDayToken++;
            if (msg) Lampa.Noty.show(msg);
        }

        function poll() {
            if (my !== liveDayToken) return;
            req(API + '/qdl/live/watch/start?camera=' + encodeURIComponent(cam.id),
                function (st) {
                    if (my !== liveDayToken) return;
                    if (!st || st.error) { stop((st && st.error) || 'Не вышло включить эфир'); return; }
                    if (st.ready && st.path) {
                        liveDayToken++;   // опрос завершён — токен закрываем сами
                        var item = { title: (cam.name || 'Камера') + '   ·   Эфир', url: API + st.path };
                        Lampa.Player.play(item);
                        Lampa.Player.playlist([item]);
                        return;
                    }
                    // mac-рекордер без активной сессии: ждать нечего, приложение на маке не пушит
                    if (!st.running) { stop('Камера сейчас не в эфире'); return; }
                    if (++tries > 20) { stop('Эфир не поднялся — камера не отвечает'); return; }
                    setTimeout(poll, 1500);
                },
                function () {
                    // разовый сетевой сбой — считаем попыткой, не обрываем прогрев
                    if (my !== liveDayToken) return;
                    if (++tries > 20) { stop('Видеорегистратор не отвечает'); return; }
                    setTimeout(poll, 1500);
                });
        }

        poll();
    }

    function ComponentLiveWatch(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div style="display:flex;flex-wrap:wrap;gap:1em;padding:1.2em 1.4em"></div>');
        var last;
        var timer = null;
        var haveTiles = false;

        // Таймер живёт только пока экран активен: Lampa на forward-навигации НЕ зовёт destroy
        // (компонент висит в стеке до pages_save_total), и без stop в pause() каждая копия сетки
        // продолжала бы дёргать регистратор каждые 12 с из фона.
        function startTimer() { if (!timer && haveTiles) timer = setInterval(refresh, 12000); }
        function stopTimer() { if (timer) { clearInterval(timer); timer = null; } }

        this.create = function () {
            injectCss();
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            network.silent(API + '/qdl/live/watch',
                function (r) { comp.build(r || {}); },
                function () { comp.build({ error: 'Видеорегистратор не отвечает' }); });
            return this.render();
        };

        this.build = function (r) {
            if (comp.destroyed) return;
            var cams = r.cameras || [];

            if (r.error)
                body.append(liveMsg('⚠️ ' + r.error));
            else if (!cams.length)
                body.append(liveMsg('Камер не найдено.'));
            else
                cams.forEach(function (c) { body.append(tile(c)); });

            // статусы и кадры дышат сами; DOM не перестраиваем — фокус пульта не теряется
            haveTiles = cams.length > 0;
            startTimer();

            this.activity.loader(false);
            this.activity.toggle();
        };

        function badgeHtml(c) {
            return c.live
                ? '<span style="background:rgba(200,30,30,.92);color:#fff;padding:.12em .55em;border-radius:.35em;font-size:.85em;font-weight:700">● LIVE</span>'
                : '<span style="background:rgba(255,255,255,.16);color:#ddd;padding:.12em .55em;border-radius:.35em;font-size:.85em">не в эфире</span>';
        }

        function tile(c) {
            var el = $(
                '<div class="selector qdl-watch-tile" data-cam="' + c.id + '" style="position:relative;width:24em;border-radius:.8em;overflow:hidden;background:#111">' +
                  '<img style="display:block;width:100%;aspect-ratio:16/9;object-fit:cover;background:#0a0a0a">' +
                  '<div style="position:absolute;left:0;right:0;bottom:0;padding:.6em .8em;background:linear-gradient(0deg,rgba(0,0,0,.85),rgba(0,0,0,0));display:flex;align-items:center;gap:.6em">' +
                    '<div class="qdl-watch-name" style="flex:1;min-width:0;font-size:1.25em;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">' + esc(c.name) + '</div>' +
                    '<div class="qdl-watch-badge">' + badgeHtml(c) + '</div>' +
                  '</div>' +
                '</div>'
            );
            var img = el.find('img');
            img.attr('src', API + '/qdl/live/watch/thumb?camera=' + c.id + '&t=' + Date.now());
            img.on('error', function () { this.src = './img/img_broken.svg'; });
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:enter', function () { liveWatchPlay(c); });
            return el;
        }

        function refresh() {
            // плеер открыт оверлеем (activity остаётся «активной») — сетку под ним не обновляем
            try { if (Lampa.Player.opened && Lampa.Player.opened()) return; } catch (e) {}
            network.silent(API + '/qdl/live/watch', function (r) {
                if (comp.destroyed || !r || !r.cameras) return;
                r.cameras.forEach(function (c) {
                    var el = body.find('.qdl-watch-tile[data-cam="' + c.id + '"]');
                    if (!el.length) return;
                    el.find('.qdl-watch-badge').html(badgeHtml(c));
                    // живой кадр дышит только у эфирных — остальным нечего обновлять
                    if (c.live) el.find('img').attr('src', API + '/qdl/live/watch/thumb?camera=' + c.id + '&t=' + Date.now());
                });
            }, function () {});
        }

        this.render = function () { return html; };
        this.start = function () {
            startTimer();   // вернулись на экран (в т.ч. Back с другого) — сетка снова дышит
            Lampa.Controller.add('content', {
                toggle: function () {
                    Lampa.Controller.collectionSet(scroll.render());
                    Lampa.Controller.collectionFocus(last || false, scroll.render());
                },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };
        // pause = ушли ВПЕРЁД на другой экран: глушим таймер и висящий прогрев эфира —
        // иначе доживший опрос открыл бы плеер поверх того, чем зритель уже занят
        this.pause = function () { stopTimer(); liveDayCancel(); };
        this.stop = function () { stopTimer(); liveDayCancel(); };
        this.destroy = function () {
            comp.destroyed = true;
            liveDayCancel();
            stopTimer();
            network.clear(); scroll.destroy(); html.remove();
        };
    }

    // Экран 1 (D1versy Rec): день + камеры, писавшие в этот день
    function ComponentLive(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div></div>');
        var last;
        var date = object.qdl_date || '';   // пусто — сервер сам возьмёт сегодняшний день
        var today = '';
        var currentLabel = '';              // «Сегодня» / «23 июля, чт» — в заголовок записи дня
        var keepDayFocus = false;           // после смены дня фокус возвращаем на кнопку дня
        var reqId = 0;                      // быстро щёлкают днями → рисуем только последний ответ

        this.create = function () {
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            load();
            return this.render();
        };

        function load() {
            var my = ++reqId;
            comp.activity.loader(true);
            network.silent(API + '/qdl/live/cameras' + (date ? '?date=' + encodeURIComponent(date) : ''),
                function (r) { if (my === reqId) draw(r || {}); },
                function () { if (my === reqId) draw({ error: 'Видеорегистратор не отвечает' }); });
        }

        function draw(r) {
            if (comp.destroyed) return;
            if (r.today) today = r.today;
            if (r.date) date = r.date;
            if (r.label) currentLabel = r.label;

            body.empty();
            last = null;

            var bar = dayBar(r);
            body.append(bar);

            if (r.error)
                body.append(liveMsg('⚠️ ' + r.error));
            else if (!r.cameras || !r.cameras.length)
                body.append(liveMsg('За этот день записей нет' + (r.total ? ' (камер всего: ' + r.total + ')' : '') + '. Выбери другой день кнопкой сверху.'));
            else {
                if (r.total && r.cameras.length < r.total)
                    body.append($('<div style="padding:.2em 1.6em 0;font-size:1.15em;opacity:.5">Писали ' + r.cameras.length + ' из ' + r.total + ' камер</div>'));
                r.cameras.forEach(function (c) { body.append(camRow(c)); });
            }

            if (keepDayFocus) { last = bar.find('.qdl-live-day')[0]; keepDayFocus = false; }

            comp.activity.loader(false);
            comp.activity.toggle();   // пере-собрать коллекцию фокуса после перерисовки
        }

        function reload() { keepDayFocus = true; body.empty(); load(); }

        function dayBar(r) {
            var canNext = !!(date && today && date < today);
            var bar = $('<div style="display:flex;align-items:center;gap:.7em;padding:1.2em 1.4em .5em"></div>');
            var prev = $('<div class="selector" style="padding:.65em 1.1em;background:rgba(255,255,255,.08);border-radius:.6em;font-size:1.4em">◀</div>');
            var day = $('<div class="selector qdl-live-day" style="flex:1;text-align:center;padding:.65em 1.2em;background:rgba(255,255,255,.13);border-radius:.6em;font-size:1.5em;font-weight:600">📅 ' + esc(r.label || 'Выбрать день') + '</div>');
            var next = $('<div class="selector" style="padding:.65em 1.1em;background:rgba(255,255,255,' + (canNext ? '.08' : '.03') + ');border-radius:.6em;font-size:1.4em;opacity:' + (canNext ? '1' : '.35') + '">▶</div>');

            prev.on('hover:enter', function () { date = liveShift(date || today, -1); reload(); });
            next.on('hover:enter', function () {
                if (!canNext) { Lampa.Noty.show('Это самый свежий день'); return; }
                date = liveShift(date, 1);
                reload();
            });
            day.on('hover:enter', pickDay);

            [prev, day, next].forEach(function (el) {
                el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            });

            return bar.append(prev).append(day).append(next);
        }

        function pickDay() {
            network.silent(API + '/qdl/live/days', function (r) {
                var days = (r && r.days) || [];
                if (!days.length) { Lampa.Noty.show('Список дней пуст'); return; }
                Lampa.Select.show({
                    title: 'Какой день показать?',
                    items: days.map(function (d) {
                        return {
                            title: d.label + (d.count
                                ? '   ·   ' + d.count + ' ' + livePlural(d.count, 'запись', 'записи', 'записей') + ' с ' + d.cameras + ' ' + livePlural(d.cameras, 'камеры', 'камер', 'камер')
                                : '   ·   записей нет'),
                            date: d.date,
                            selected: d.date === date
                        };
                    }),
                    onSelect: function (a) { Lampa.Controller.toggle('content'); date = a.date; reload(); },
                    onBack: function () { Lampa.Controller.toggle('content'); }
                });
            }, function () { Lampa.Noty.show('Видеорегистратор не отвечает'); });
        }

        function camRow(c) {
            var el = $(
                '<div class="selector" style="display:flex;align-items:center;gap:1.2em;padding:.9em;margin:.45em 1.4em;background:rgba(255,255,255,.06);border-radius:.8em">' +
                  '<img style="width:12em;height:6.8em;object-fit:cover;border-radius:.5em;background:#111;flex:none">' +
                  '<div style="flex:1;min-width:0">' +
                    '<div style="font-size:1.7em;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">' + esc(c.name) + '</div>' +
                    '<div style="opacity:.75;font-size:1.25em;margin-top:.35em">' + esc(c.first + ' – ' + c.last) + '   ·   ' + liveDur(c.seconds) + '</div>' +
                  '</div>' +
                  '<div style="opacity:.45;font-size:1.8em;padding-right:.4em">▶</div>' +
                '</div>'
            );
            var img = el.find('img');
            img.attr('src', API + '/qdl/live/thumb?id=' + c.thumb);
            img.on('error', function () { this.src = './img/img_broken.svg'; });
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            // Обычный вход = весь день одной записью. Разбивка на куски осталась запасным путём
            // (долгое нажатие) — на случай, если склейка почему-то не собралась.
            el.on('hover:enter', function () { livePlayDay(c, date, currentLabel); });
            el.on('hover:long', function () {
                Lampa.Select.show({
                    title: c.name || 'Камера',
                    items: [
                        { title: '▶ Смотреть весь день', day: true },
                        { title: 'Фрагменты по отдельности (' + c.count + ')' }
                    ],
                    onSelect: function (a) {
                        Lampa.Controller.toggle('content');
                        if (a.day) livePlayDay(c, date, currentLabel);
                        else Lampa.Activity.push({ url: '', title: c.name, component: 'qdl_live_camera', qdl_camera: c, qdl_date: date, page: 1 });
                    },
                    onBack: function () { Lampa.Controller.toggle('content'); }
                });
            });
            return el;
        }

        this.render = function () { return html; };
        this.start = function () {
            Lampa.Controller.add('content', {
                toggle: function () {
                    Lampa.Controller.collectionSet(scroll.render());
                    Lampa.Controller.collectionFocus(last || false, scroll.render());
                },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };
        // уход с экрана (вперёд или назад) глушит висящую готовку дня — см. коммент у liveDayToken
        this.pause = function () { liveDayCancel(); };
        this.stop = function () { liveDayCancel(); };
        this.destroy = function () { comp.destroyed = true; liveDayCancel(); network.clear(); scroll.destroy(); html.remove(); };
    }

    // Экран 2: записи одной камеры за выбранный день
    function ComponentLiveCamera(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div></div>');
        var last;
        var cam = object.qdl_camera || {};
        var date = object.qdl_date || '';

        this.create = function () {
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            network.silent(API + '/qdl/live/recordings?camera=' + encodeURIComponent(cam.id) + (date ? '&date=' + encodeURIComponent(date) : ''),
                function (r) { comp.build(r || {}); },
                function () { comp.build({ error: 'Видеорегистратор не отвечает' }); });
            return this.render();
        };

        this.build = function (r) {
            var items = r.items || [];
            var name = (r.camera && r.camera.name) || cam.name || 'Камера';

            body.append($('<div style="padding:1.2em 1.6em .4em"><div style="font-size:2em;font-weight:700">' + esc(name) + '</div>' +
                '<div style="opacity:.6;font-size:1.25em;margin-top:.25em">' + esc(r.label || '') + (items.length ? '   ·   ' + items.length + ' ' + livePlural(items.length, 'запись', 'записи', 'записей') : '') + '</div></div>'));

            if (r.error)
                body.append(liveMsg('⚠️ ' + r.error));
            else if (!items.length)
                body.append(liveMsg('За этот день записей с этой камеры нет.'));
            else {
                body.append(playAll(items, r.label));
                items.forEach(function (rec, i) { body.append(recRow(rec, items, i)); });
            }

            this.activity.loader(false);
            this.activity.toggle();
        };

        function playAll(items, label) {
            var total = 0;
            items.forEach(function (r) { total += r.seconds || 0; });
            var box = $('<div></div>');

            // Основной путь — та же склеенная запись дня, что и по обычному входу в камеру.
            var day = $('<div class="selector" style="margin:.6em 1.4em;padding:1em 1.2em;background:rgba(20,160,40,.85);border-radius:.8em;font-size:1.5em;font-weight:600">▶ Весь день одной записью   ·   ' + liveDur(total) + '</div>');
            day.on('hover:focus', function () { last = day[0]; scroll.update(day, true); });
            day.on('hover:enter', function () { livePlayDay(cam, date, label); });

            // Запасной: куски по очереди (каждый со своим таймлайном) — если склейка не собралась.
            var seq = $('<div class="selector" style="margin:.4em 1.4em;padding:.8em 1.2em;background:rgba(255,255,255,.1);border-radius:.8em;font-size:1.3em">Фрагменты подряд, по одному</div>');
            seq.on('hover:focus', function () { last = seq[0]; scroll.update(seq, true); });
            seq.on('hover:enter', function () { livePlay(cam, items, 0); });

            return box.append(day).append(seq);
        }

        function recRow(rec, items, i) {
            var tl = liveTimeline(rec);
            var pct = (tl && tl.percent) || 0;
            var mark = pct >= 90 ? '✓ ' : (pct >= 5 ? '► ' + Math.round(pct) + '%   ·   ' : '');
            var meta = [liveDur(rec.seconds), liveSize(rec.size), rec.trigger === 'motion' ? 'движение' : (rec.trigger === 'human' ? 'человек' : '')].filter(Boolean).join('   ·   ');

            var el = $(
                '<div class="selector" style="display:flex;align-items:center;gap:1.2em;padding:.8em;margin:.4em 1.4em;background:rgba(255,255,255,.06);border-radius:.8em">' +
                  '<img style="width:10em;height:5.65em;object-fit:cover;border-radius:.5em;background:#111;flex:none">' +
                  '<div style="flex:1;min-width:0">' +
                    '<div style="font-size:1.6em;font-weight:600">' + esc(mark + rec.start + ' – ' + rec.end) + '</div>' +
                    '<div style="opacity:.7;font-size:1.2em;margin-top:.3em">' + esc(meta) + '</div>' +
                  '</div>' +
                  '<div style="opacity:.45;font-size:1.6em;padding-right:.4em">▶</div>' +
                '</div>'
            );
            var img = el.find('img');
            img.attr('src', API + '/qdl/live/thumb?id=' + rec.id);
            img.on('error', function () { this.src = './img/img_broken.svg'; });
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:enter', function () { livePlay(cam, items, i); });
            return el;
        }

        this.render = function () { return html; };
        this.start = function () {
            Lampa.Controller.add('content', {
                toggle: function () {
                    Lampa.Controller.collectionSet(scroll.render());
                    Lampa.Controller.collectionFocus(last || false, scroll.render());
                },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };
        this.pause = function () { liveDayCancel(); };
        this.stop = function () { liveDayCancel(); };
        this.destroy = function () { liveDayCancel(); network.clear(); scroll.destroy(); html.remove(); };
    }

    // Эфир (сетка камер) — D1versy Live
    function buildWatchMenuItem() {
        var item = $('<li class="menu__item selector qdl-watch-menu"><div class="menu__ico">' + CAM + '</div><div class="menu__text">D1versy Live</div></li>');
        item.on('hover:enter', function () {
            Lampa.Activity.push({ url: '', title: 'D1versy Live', component: 'qdl_live_watch', page: 1 });
        });
        return item;
    }

    // Записи (день одной записью) — D1versy Rec
    function buildLiveMenuItem() {
        var item = $('<li class="menu__item selector qdl-live-menu"><div class="menu__ico">' + REC + '</div><div class="menu__text">D1versy Rec</div></li>');
        item.on('hover:enter', function () {
            Lampa.Activity.push({ url: '', title: 'D1versy Rec', component: 'qdl_live', page: 1 });
        });
        return item;
    }

    // ───────── Пункт меню «Загрузки» строго под «Персоны» (data-action="myperson") ─────────
    function buildMenuItem() {
        var item = $('<li class="menu__item selector qdl-menu"><div class="menu__ico">' + ICON + '</div><div class="menu__text">Загрузки</div></li>');
        item.on('hover:enter', function () {
            Lampa.Activity.push({ url: '', title: 'Загрузки', component: 'qdl_downloads', page: 1 });
        });
        return item;
    }

    function ensureMenu() {
        try {
            var anchor = $('.menu .menu__item[data-action="myperson"]').first();
            if (!anchor.length) return;                 // «Персоны» ещё не отрисованы — ждём
            var existing = $('.menu .qdl-menu');
            if (!existing.length) { anchor.after(buildMenuItem()); }
            // уже есть — держим строго сразу после «Персоны» (меню могло пере-рендериться)
            else if (existing.prev('.menu__item')[0] !== anchor[0]) {
                existing.detach();
                anchor.after(existing);
            }

            // «Уведомления» — строго сразу после «Загрузки»
            var dl = $('.menu .qdl-menu');
            var noti = $('.menu .qdl-noti-menu');
            if (dl.length) {
                if (!noti.length) { dl.after(buildNotiMenuItem()); setTimeout(pollNotifications, 200); }   // подтянуть бейдж сразу после появления пункта
                else if (noti.prev('.menu__item')[0] !== dl[0]) { noti.detach(); dl.after(noti); }
            }

            // «D1versy Live» (эфир) — строго сразу после «Уведомления»
            var nt = $('.menu .qdl-noti-menu');
            var watch = $('.menu .qdl-watch-menu');
            if (nt.length) {
                if (!watch.length) nt.after(buildWatchMenuItem());
                else if (watch.prev('.menu__item')[0] !== nt[0]) { watch.detach(); nt.after(watch); }
            }

            // «D1versy Rec» (записи) — строго сразу после «D1versy Live»
            var w = $('.menu .qdl-watch-menu');
            var live = $('.menu .qdl-live-menu');
            if (w.length) {
                if (!live.length) w.after(buildLiveMenuItem());
                else if (live.prev('.menu__item')[0] !== w[0]) { live.detach(); w.after(live); }
            }
        } catch (e) {}
    }

    function startMenuWatcher() {
        ensureMenu();
        var deb = null;
        function onMut() { if (deb) return; deb = setTimeout(function () { deb = null; ensureMenu(); }, 300); }
        try {
            var menuEl = document.querySelector('.menu') || document.body;   // узкий observer (не весь body)
            new MutationObserver(onMut).observe(menuEl, { childList: true, subtree: true });
        } catch (e) {}
        try { if (Lampa.Listener && Lampa.Listener.follow) Lampa.Listener.follow('menu', function () { ensureMenu(); }); } catch (e) {}
        [500, 1500, 3000, 6000].forEach(function (t) { setTimeout(ensureMenu, t); });
    }

    // ───────── Иконка уведомлений в хедере (рядом со штатными; клик → наш центр «Уведомления») ─────────
    function buildHeaderNoti() {
        var item = $('<div class="head__action selector open--qdl-noti qdl-noti-head">' + BELL + '<span class="qdl-noti-head-badge" style="display:none"></span></div>');
        item.data('controller', 'head');   // поздняя иконка в хедере иначе не фокусируется пультом
        item.on('hover:enter', function () {
            Lampa.Activity.push({ url: '', title: 'Уведомления', component: 'qdl_notifications', page: 1 });
        });
        return item;
    }

    function ensureHeaderNoti() {
        try {
            var actions = $('.head .head__actions');
            if (!actions.length) return;                    // хедер ещё не отрисован / иная сборка
            if ($('.head .qdl-noti-head').length) return;   // уже стоит
            injectCss();
            var bell = actions.find('.open--notice').first();
            if (bell.length) bell.before(buildHeaderNoti());   // перед штатным «звонком»
            else actions.append(buildHeaderNoti());
            pollNotifications();                            // сразу подтянуть текущий бейдж
        } catch (e) {}
    }

    function startHeaderNotiWatcher() {
        ensureHeaderNoti();
        var deb = null;
        function onMut() { if (deb) return; deb = setTimeout(function () { deb = null; ensureHeaderNoti(); }, 300); }
        try {
            var headEl = document.querySelector('.head') || document.body;   // узкий observer
            new MutationObserver(onMut).observe(headEl, { childList: true, subtree: true });
        } catch (e) {}
        [500, 1500, 3000, 6000].forEach(function (t) { setTimeout(ensureHeaderNoti, t); });
    }

    // ───────── Кнопка фуллскрина в плеере на мобильном (Lampa прячет свою на android/iOS) ─────────
    function isMobile() {
        try { if (Lampa.Platform && typeof Lampa.Platform.is === 'function' && Lampa.Platform.is('android')) return true; } catch (e) {}
        return /Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent || '');
    }

    function fsToggle() {
        var cont = document.querySelector('.player') || document.documentElement;
        var v = document.querySelector('.player-video video') || document.querySelector('.player video') || document.querySelector('video');
        try {
            if (document.fullscreenElement || document.webkitFullscreenElement) {
                (document.exitFullscreen || document.webkitExitFullscreen || function () {}).call(document);
                return;
            }
            if (cont && cont.requestFullscreen) { cont.requestFullscreen(); return; }            // Android/десктоп: весь плеер (UI Lampa остаётся)
            if (cont && cont.webkitRequestFullscreen) { cont.webkitRequestFullscreen(); return; }
            if (v && v.webkitEnterFullscreen) { v.webkitEnterFullscreen(); return; }              // iOS: нативный фуллскрин видео
            if (v && v.requestFullscreen) { v.requestFullscreen(); return; }
        } catch (e) {}
    }

    function ensurePlayerFs() {
        if (!isMobile()) return;
        var panel = document.querySelector('.player-panel');
        if (!panel || panel.querySelector('.qdl-fs')) return;
        injectCss();
        var btn = document.createElement('div');
        btn.className = 'button selector qdl-fs';   // БЕЗ player-panel__fullscreen — иначе Lampa скрывает его на моб.
        btn.innerHTML = '<svg><use xlink:href="#sprite-fullscreen"></use></svg>';
        try { $(btn).on('hover:enter', fsToggle); } catch (e) {}
        btn.addEventListener('click', function (e) { e.preventDefault(); fsToggle(); });
        // вставляем рядом со скрытой штатной кнопкой фуллскрина (или в конец панели)
        var anchor = panel.querySelector('.player-panel__fullscreen');
        if (anchor && anchor.parentNode) anchor.parentNode.insertBefore(btn, anchor.nextSibling);
        else panel.appendChild(btn);
    }

    function startPlayerFsWatcher() {
        if (!isMobile()) return;
        var deb = null;
        try {
            new MutationObserver(function () { if (deb) return; deb = setTimeout(function () { deb = null; ensurePlayerFs(); }, 300); })
                .observe(document.body, { childList: true, subtree: true });
        } catch (e) {}
    }

    function start() {
        Lampa.Component.add('qdl_downloads', ComponentDownloads);
        Lampa.Component.add('qdl_card', ComponentCard);
        Lampa.Component.add('qdl_notifications', ComponentNotifications);
        Lampa.Component.add('qdl_live', ComponentLive);
        Lampa.Component.add('qdl_live_camera', ComponentLiveCamera);
        Lampa.Component.add('qdl_live_watch', ComponentLiveWatch);
        Lampa.Listener.follow('full', addButton);
        startMenuWatcher();
        startHeaderNotiWatcher();
        startPlayerFsWatcher();
        pollNotifications();
        try { whenDmca(function () {}); } catch (e) {}   // прогрев DMCA-списка до первого открытия карточки
        try { setInterval(pollNotifications, 90000); } catch (e) {}
    }

    if (window.appready) start();
    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
