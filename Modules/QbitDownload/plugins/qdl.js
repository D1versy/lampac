(function () {
    'use strict';

    var API = '{localhost}';
    var ICON = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M12 3v12m0 0l-4-4m4 4l4-4M5 19h14" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>';

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
            // режим «Загрузки»: в полной карточке прячем все кнопки, кроме нашей «Смотреть»
            '.qdl-only .full-start__buttons .full-start__button:not(.qdl-watch-btn),' +
            '.qdl-only .full-start-new__buttons .full-start__button:not(.qdl-watch-btn){display:none !important}';
        document.head.appendChild(st);
    }

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

    function cleanName(name) {
        var s = String(name || '');
        s = s.split(/[\[\(]/)[0];
        s = s.split('/')[0];
        s = s.replace(/[._]/g, ' ');
        s = s.replace(/\b(19|20)\d\d\b[\s\S]*$/, '');
        s = s.replace(/\b(WEB-?DL|BluRay|HDRip|WEBRip|2160p|1080p|720p|4K|HEVC|x26[45]|BDRip|DVDRip)\b[\s\S]*$/i, '');
        return s.trim();
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
    function baseName(p) { return String(p || '').split('/').pop().split('\\').pop(); }

    // ТВ (нативный плеер) тянет оригинал (EAC3 ок), всё остальное (десктоп/мобайл-браузер) — HLS (звук→AAC).
    // ВАЖНО: Platform.is('browser') слишком узок (на Linux-десктопе platform='' → false). Берём инверсию tv().
    function isBrowser() {
        try { if (Lampa.Platform && typeof Lampa.Platform.tv === 'function') return !Lampa.Platform.tv(); } catch (e) {}
        var ua = navigator.userAgent || '';
        return !/Tizen|Web0?S|webOS|SMART-TV|SmartTV|HbbTV|AppleTV|CrKey|Android TV|NetCast|VIDAA|MSX/i.test(ua);
    }
    // audio: 'o' (ориг) | 'eN' (встроенная) | 'fN' (внешняя озвучка). Внешняя → ВСЕГДА HLS (домешиваем).
    function streamUrl(hash, index, audio) {
        var ext = audio && audio.charAt(0) === 'f';
        if (ext || isBrowser()) {
            var k = hash + '_' + (index >= 0 ? index : -1) + (audio && audio !== 'o' ? '_' + audio : '');
            return API + '/qdl/hls/' + k + '/playlist.m3u8';
        }
        return API + '/qdl/stream?hash=' + hash + (index >= 0 ? '&index=' + index : '');
    }

    // выбор озвучки запоминается на сериал (по hash)
    function getAudioPref(hash) { try { return (Lampa.Storage.get('qdl_audio', {}) || {})[hash]; } catch (e) { return null; } }
    function setAudioPref(hash, id) { try { var m = Lampa.Storage.get('qdl_audio', {}) || {}; m[hash] = id; Lampa.Storage.set('qdl_audio', m); } catch (e) {} }

    // определить озвучку (из памяти или спросить один раз), затем cb(audioId)
    function ensureAudio(hash, index, cb) {
        var pref = getAudioPref(hash);
        if (pref) { cb(pref); return; }
        req(API + '/qdl/audio?hash=' + hash + '&index=' + (index >= 0 ? index : -1), function (opts) {
            opts = opts || [];
            if (opts.length <= 1) { cb(opts[0] && opts[0].id); return; }
            Lampa.Select.show({
                title: 'Озвучка',
                items: opts.map(function (o) { return { title: o.label, id: o.id }; }),
                onSelect: function (s) { setAudioPref(hash, s.id); cb(s.id); },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { cb(null); });
    }

    function rawPlay(hash, index, title, audio) {
        var url = streamUrl(hash, index, audio);
        Lampa.Player.play({ title: title || 'Загрузка', url: url });
        Lampa.Player.playlist([{ title: title || 'Загрузка', url: url }]);
    }

    // ───────── Воспроизведение локального файла (оффлайн) ─────────
    function playLocal(hash, index, title) {
        ensureAudio(hash, index, function (audio) { rawPlay(hash, index, title, audio); });
    }

    function chooseEpisode(hash, name) {
        req(API + '/qdl/files?hash=' + hash, function (files) {
            var vids = videoFiles(files);
            if (!vids.length) { Lampa.Noty.show('Видеофайлы не найдены'); return; }
            if (vids.length === 1) { playLocal(hash, vids[0].index, baseName(vids[0].name)); return; }

            ensureAudio(hash, vids[0].index, function (audio) {   // озвучку выбираем один раз на сериал
                var playlist = vids.map(function (f) {
                    return { title: baseName(f.name), url: streamUrl(hash, f.index, audio) };
                });
                Lampa.Select.show({
                    title: 'Серии — ' + (name || ''),
                    items: vids.map(function (f) { return { title: baseName(f.name), index: f.index }; }),
                    onSelect: function (a) {
                        Lampa.Player.play({ title: a.title, url: streamUrl(hash, a.index, audio) });
                        Lampa.Player.playlist(playlist);
                    },
                    onBack: function () { Lampa.Controller.toggle('content'); }
                });
            });
        }, function () { Lampa.Noty.show('Ошибка чтения файлов'); });
    }

    function watchByHash(hash, name) {
        req(API + '/qdl/files?hash=' + hash, function (files) {
            var vids = videoFiles(files);
            if (vids.length > 1) chooseEpisode(hash, name);
            else playLocal(hash, vids.length ? vids[0].index : -1, name);
        }, function () { playLocal(hash, -1, name); });
    }
    function watch(item) { watchByHash(item.hash, (item.meta && item.meta.title) || item.name); }

    // ───────── Открытие загрузки: НАСТОЯЩАЯ полная карточка (вся инфа), но в режиме «одна кнопка» ─────────
    function openDownload(item) {
        var m = item.meta || {};
        if (m.id) {
            Lampa.Activity.push({
                url: '', component: 'full', id: m.id,
                method: m.media_type === 'tv' ? 'tv' : 'movie',
                card: m, source: m.source || 'tmdb',
                qdl_hash: item.hash    // маркер: открыто из «Загрузок» → addButton оставит одну кнопку «Смотреть»
            });
        } else {
            watchByHash(item.hash, item.name);   // нет метаданных → просто играем
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

        this.create = function () {
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            network.silent(API + '/qdl/list', function (list) { comp.build(list || []); }, function () { comp.build([]); });
            return this.render();
        };

        this.build = function (list) {
            if (!list.length)
                body.append($('<div style="padding:2em;font-size:1.4em;opacity:.7">В «Загрузках» пока пусто. Нажми «Скачать» на карточке фильма.</div>'));

            list.forEach(function (t) { comp.append(t); });

            this.activity.loader(false);
            this.activity.toggle();
        };

        this.append = function (t) {
            var meta = t.meta || {};
            var pct = Math.round((t.progress || 0) * 100);

            // обычная ВЕРТИКАЛЬНАЯ карточка-постер (без card--collection!)
            var el = Lampa.Template.get('card', { title: meta.title || t.name, release_year: meta.year || '' });

            var img = el.find('.card__img');
            img.attr('src', posterUrl(t));
            img.on('error', function () { this.src = './img/img_broken.svg'; });

            var view = el.find('.card__view'); if (!view.length) view = el;
            view.append(pct < 100
                ? '<div style="position:absolute;left:.4em;top:.4em;background:rgba(0,0,0,.75);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">' + pct + '%</div>'
                : '<div style="position:absolute;left:.4em;top:.4em;background:rgba(20,160,40,.9);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">✓</div>');

            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:enter', function () { openDownload(t); });
            el.on('hover:long', function () { quickMenu(t); });

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

    function quickMenu(t) {
        Lampa.Select.show({
            title: (t.meta && t.meta.title) || t.name,
            items: [
                { title: 'Открыть карточку', act: 'page' },
                { title: '▶ Смотреть (оффлайн)', act: 'play' },
                { title: '🔊 Озвучка', act: 'audio' },
                { title: t.watched ? '🔔 Не следить за новыми сериями' : '🔔 Следить за новыми сериями', act: 'watch' },
                { title: '🗑 Удалить (с файлами)', act: 'del' }
            ],
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
                else if (b.act === 'del')
                    req(API + '/qdl/delete?hash=' + t.hash + '&deleteFiles=true', function () {
                        Lampa.Noty.show('Удалено');
                        Lampa.Activity.replace();
                    });
            },
            onBack: function () { Lampa.Controller.toggle('content'); }
        });
    }

    // ───────── Поиск раздач + кнопка «Скачать» ─────────
    function chooseAndDownload(movie) {
        movie = movie || {};
        var title = movie.title || movie.name || movie.original_title || movie.original_name || '';
        var year = ((movie.release_date || movie.first_air_date || '') + '').slice(0, 4);
        if (!title) { Lampa.Noty.show('Не удалось определить название'); return; }

        Lampa.Noty.show('Поиск раздач…');
        var url = API + '/qdl/search?query=' + encodeURIComponent(title) + (year ? '&year=' + year : '');

        req(url, function (list) {
            if (!list || !list.length) { Lampa.Noty.show('Раздачи не найдены'); return; }

            Lampa.Select.show({
                title: 'Выбери раздачу для загрузки на диск',
                items: list.slice(0, 60).map(function (t) {
                    return {
                        title: t.title,
                        subtitle: [t.size, t.tracker, (t.sid ? ('сидов: ' + t.sid) : '')].filter(Boolean).join('  •  '),
                        t: t
                    };
                }),
                onSelect: function (a) {
                    Lampa.Controller.toggle('content');
                    var q = a.t.magnet
                        ? ('magnet=' + encodeURIComponent(a.t.magnet))
                        : ('parselink=' + encodeURIComponent(a.t.parselink || ''));
                    Lampa.Noty.show('Добавляю в загрузки…');
                    req(API + '/qdl/add?' + q + '&title=' + encodeURIComponent(a.t.title || title) + '&query=' + encodeURIComponent(title), function (r) {
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
                    w.on('hover:enter', function () { watchByHash(active.qdl_hash, movie.title || movie.name); });
                    // удержание (long-press) на кнопке → меню управления (следить/удалить) — для дискаверабилити
                    w.on('hover:long', function () {
                        req(API + '/qdl/list', function (list) {
                            var it = (list || []).filter(function (x) { return x.hash === active.qdl_hash; })[0] || { hash: active.qdl_hash, meta: movie };
                            quickMenu(it);
                        }, function () { quickMenu({ hash: active.qdl_hash, meta: movie }); });
                    });
                    cont.prepend(w);
                }
                return;   // НЕ добавляем «Скачать», прочие кнопки скрыты
            }

            if (!$('.qdl-download', render).length) {
                var btn = $('<div class="full-start__button selector qdl-download">' + ICON + '<span>Скачать</span></div>');
                btn.on('hover:enter', function () { chooseAndDownload(movie); });
                cont.append(btn);
            }

            // фильм уже скачан → ЗЕЛЁНАЯ «Смотреть (загружено)» + привязка метаданных
            if (movie && movie.id && !$('.qdl-watch-btn', render).length) {
                req(API + '/qdl/list', function (list) {
                    list = list || [];
                    var titles = [movie.title, movie.original_title, movie.name, movie.original_name]
                        .filter(Boolean).map(function (s) { return String(s).toLowerCase().trim(); });

                    var hit = list.filter(function (x) { return x.meta && String(x.meta.id) === String(movie.id); })[0];
                    if (!hit) {
                        hit = list.filter(function (x) {
                            var n = cleanName(x.name).toLowerCase().trim();
                            return n && titles.some(function (t) { return t === n || t.indexOf(n) === 0 || n.indexOf(t) === 0; });
                        })[0];
                        if (hit && !hit.meta) saveMeta(hit.hash, movie);   // back-link карточка → загрузка
                    }
                    if (!hit || $('.qdl-watch-btn', render).length) return;

                    injectCss();
                    var play = $('<div class="full-start__button selector qdl-watch-btn">' + ICON + '<span>Смотреть (загружено)</span></div>');
                    play.on('hover:enter', function () { watch(hit); });
                    cont.prepend(play);
                });
            }
        } catch (err) { console.log('qdl: addButton', err); }
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
            if (!existing.length) { anchor.after(buildMenuItem()); return; }
            // уже есть — держим строго сразу после «Персоны» (меню могло пере-рендериться)
            if (existing.prev('.menu__item')[0] !== anchor[0]) {
                existing.detach();
                anchor.after(existing);
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
        var btn = document.createElement('div');
        btn.className = 'player-panel__fullscreen button selector qdl-fs';
        btn.innerHTML = '<svg><use xlink:href="#sprite-fullscreen"></use></svg>';
        try { $(btn).on('hover:enter', fsToggle); } catch (e) {}
        btn.addEventListener('click', function (e) { e.preventDefault(); fsToggle(); });
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
        Lampa.Listener.follow('full', addButton);
        startMenuWatcher();
        startPlayerFsWatcher();
    }

    if (window.appready) start();
    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
