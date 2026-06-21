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

    function injectCss() {
        if (document.getElementById('qdl-css')) return;
        var st = document.createElement('style');
        st.id = 'qdl-css';
        st.textContent = '.qdl-watch.focus{background:#fff !important;color:#000 !important;transform:scale(1.03)}';
        document.head.appendChild(st);
    }

    // ───────── Метаданные TMDB ─────────
    function slimCard(m) {
        if (!m) return null;
        return {
            id: m.id,
            title: m.title || m.name,
            name: m.name || m.title,
            original_title: m.original_title || m.original_name,
            original_name: m.original_name,
            overview: m.overview,
            release_date: m.release_date,
            first_air_date: m.first_air_date,
            vote_average: m.vote_average,
            poster_path: m.poster_path,
            media_type: m.media_type || ((m.name || m.first_air_date) && !m.title && !m.release_date ? 'tv' : 'movie'),
            source: m.source || 'tmdb'
        };
    }

    // сохранить метаданные + постер на бэкенд (SSD-кэш)
    function saveMeta(hash, movie, cb) {
        if (!hash || !movie) { if (cb) cb(null); return; }
        var purl = '';
        try { if (movie.poster_path) purl = Lampa.TMDB.image('t/p/w500' + movie.poster_path); } catch (e) {}
        post(API + '/qdl/save', { hash: hash, card: JSON.stringify(slimCard(movie)), poster_url: purl }, cb, function () { if (cb) cb(null); });
    }

    // поиск метаданных по имени раздачи (для загрузок без привязки)
    function cleanName(name) {
        var s = String(name || '');
        s = s.split(/[\[\(]/)[0];                  // до первой [ или (
        s = s.split('/')[0];                       // "Рус / Eng" → первое
        s = s.replace(/[._]/g, ' ');
        s = s.replace(/\b(19|20)\d\d\b[\s\S]*$/, '');
        s = s.replace(/\b(WEB-?DL|BluRay|HDRip|WEBRip|2160p|1080p|720p|4K|HEVC|x26[45]|BDRip|DVDRip)\b[\s\S]*$/i, '');
        return s.trim();
    }

    function tmdbSearch(name, cb) {
        try {
            var key = (Lampa.TMDB && Lampa.TMDB.key) ? Lampa.TMDB.key : '';
            var url = Lampa.TMDB.api('search/multi?api_key=' + key + '&language=ru-RU&query=' + encodeURIComponent(name));
            req(url, function (r) {
                var list = (r && r.results) ? r.results.filter(function (x) {
                    return (x.media_type === 'movie' || x.media_type === 'tv') && x.poster_path;
                }) : [];
                cb(list[0] || null);
            }, function () { cb(null); });
        } catch (e) { cb(null); }
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

    // ───────── Воспроизведение локального файла (оффлайн) ─────────
    function playLocal(hash, index, title) {
        var url = API + '/qdl/stream?hash=' + hash + (index >= 0 ? '&index=' + index : '');
        Lampa.Player.play({ title: title || 'Загрузка', url: url });
        Lampa.Player.playlist([{ title: title || 'Загрузка', url: url }]);
    }

    function chooseEpisode(hash, name) {
        req(API + '/qdl/files?hash=' + hash, function (files) {
            var vids = videoFiles(files);
            if (!vids.length) { Lampa.Noty.show('Видеофайлы не найдены'); return; }
            if (vids.length === 1) { playLocal(hash, vids[0].index, baseName(vids[0].name)); return; }

            var playlist = vids.map(function (f) {
                return { title: baseName(f.name), url: API + '/qdl/stream?hash=' + hash + '&index=' + f.index };
            });
            Lampa.Select.show({
                title: 'Серии — ' + (name || ''),
                items: vids.map(function (f) { return { title: baseName(f.name), index: f.index }; }),
                onSelect: function (a) {
                    Lampa.Player.play({ title: a.title, url: API + '/qdl/stream?hash=' + hash + '&index=' + a.index });
                    Lampa.Player.playlist(playlist);
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { Lampa.Noty.show('Ошибка чтения файлов'); });
    }

    // «Смотреть»: сериал → выбор серии, фильм → самый большой файл
    function watch(item) {
        var name = (item.meta && item.meta.title) || item.name;
        req(API + '/qdl/files?hash=' + item.hash, function (files) {
            var vids = videoFiles(files);
            if (vids.length > 1) chooseEpisode(item.hash, name);
            else playLocal(item.hash, vids.length ? vids[0].index : -1, name);
        }, function () { playLocal(item.hash, -1, name); });
    }

    // ───────── Простая карточка загрузки (постер + описание + 1 кнопка) ─────────
    function openDownload(item) {
        Lampa.Activity.push({ url: '', title: (item.meta && item.meta.title) || item.name, component: 'qdl_card', qdl: item });
    }

    function ComponentCard(object) {
        var item = object.qdl || {};
        var meta = item.meta || {};
        var scroll = new Lampa.Scroll({ mask: true, over: true });
        var html = $('<div></div>');

        this.create = function () {
            var pct = Math.round((item.progress || 0) * 100);
            var kind = meta.media_type === 'tv' ? 'Сериал' : (meta.media_type === 'movie' ? 'Фильм' : '');
            var info = [
                meta.year, kind,
                (meta.vote_average ? ('★ ' + (Math.round(meta.vote_average * 10) / 10)) : ''),
                (pct < 100 ? (pct + '% загружено') : '✓ загружено')
            ].filter(Boolean).join('   ·   ');

            var body = $(
                '<div style="padding:2.5em">' +
                  '<div style="display:flex;gap:2.5em;align-items:flex-start">' +
                    '<img class="qdl-poster" src="' + posterUrl(item) + '" style="width:17em;height:25.5em;object-fit:cover;border-radius:1em;background:#222;flex:none">' +
                    '<div style="flex:1;min-width:0">' +
                      '<div style="font-size:2.4em;font-weight:600;line-height:1.1">' + esc(meta.title || item.name) + '</div>' +
                      '<div style="opacity:.6;font-size:1.2em;margin:.8em 0 1.2em">' + esc(info) + '</div>' +
                      '<div style="font-size:1.25em;line-height:1.55;opacity:.9;max-width:42em;margin-bottom:1.6em">' + esc(meta.overview || 'Нет описания.') + '</div>' +
                      '<div class="qdl-watch selector" style="display:inline-flex;align-items:center;gap:.4em;padding:.7em 1.7em;background:rgba(255,255,255,.14);border-radius:.6em;font-size:1.3em">▶&nbsp;Смотреть</div>' +
                    '</div>' +
                  '</div>' +
                '</div>'
            );
            body.find('.qdl-poster').on('error', function () { this.src = './img/img_broken.svg'; });
            body.find('.qdl-watch').on('hover:enter', function () { watch(item); });

            scroll.append(body);
            html.append(scroll.render());
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

    // ───────── Грид «Загрузки» ─────────
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
            var title = meta.title || t.name;
            var year = meta.year || '';

            var el = Lampa.Template.get('card', { title: title, release_year: year });
            el.addClass('card--collection selector');

            var img = el.find('.card__img');
            img.attr('src', posterUrl(t));
            img.on('error', function () { this.src = './img/img_broken.svg'; });

            var view = el.find('.card__view'); if (!view.length) view = el;
            var badge = pct < 100
                ? '<div style="position:absolute;left:.4em;top:.4em;background:rgba(0,0,0,.75);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">' + pct + '%</div>'
                : '<div style="position:absolute;left:.4em;top:.4em;background:rgba(20,160,40,.9);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">✓</div>';
            view.append(badge);

            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:enter', function () { openDownload(t); });
            el.on('hover:long', function () { quickMenu(t); });

            body.append(el);

            // нет метаданных → дотягиваем поиском по TMDB и кэшируем
            if (!t.meta) {
                tmdbSearch(cleanName(t.name), function (found) {
                    if (!found) return;
                    saveMeta(t.hash, found, function (r) {
                        t.meta = slimCard(found);
                        el.find('.card__title').text(found.title || found.name || t.name);
                        if (r && r.has_poster) el.find('.card__img').attr('src', API + '/qdl/poster?hash=' + t.hash + '&t=' + Date.now());
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
                { title: '🗑 Удалить (с файлами)', act: 'del' }
            ],
            onSelect: function (b) {
                if (b.act === 'page') openDownload(t);
                else if (b.act === 'play') watch(t);
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
                    req(API + '/qdl/add?' + q + '&title=' + encodeURIComponent(a.t.title || title), function (r) {
                        if (r && r.success) {
                            if (r.hash) saveMeta(r.hash, movie);   // кэшируем метаданные+постер на SSD
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

            if (!$('.qdl-download', render).length) {
                var btn = $('<div class="full-start__button selector qdl-download">' + ICON + '<span>Скачать</span></div>');
                btn.on('hover:enter', function () { chooseAndDownload(movie); });
                cont.append(btn);
            }

            // если фильм уже скачан — кнопка «Смотреть (загружено)»
            if (movie && movie.id && !$('.qdl-watch-btn', render).length) {
                req(API + '/qdl/list', function (list) {
                    var hit = (list || []).filter(function (x) { return x.meta && String(x.meta.id) === String(movie.id); })[0];
                    if (!hit || $('.qdl-watch-btn', render).length) return;
                    var play = $('<div class="full-start__button selector qdl-watch-btn">' + ICON + '<span>Смотреть (загружено)</span></div>');
                    play.on('hover:enter', function () { watch(hit); });
                    cont.prepend(play);
                });
            }
        } catch (err) { console.log('qdl: addButton', err); }
    }

    // ───────── Пункт меню «Загрузки» (под «Персоны» = data-action="myperson") ─────────
    function addMenu() {
        if ($('.menu .qdl-menu').length) return;
        var item = $('<li class="menu__item selector qdl-menu"><div class="menu__ico">' + ICON + '</div><div class="menu__text">Загрузки</div></li>');
        item.on('hover:enter', function () {
            Lampa.Activity.push({ url: '', title: 'Загрузки', component: 'qdl_downloads', page: 1 });
        });

        // меню может дорисовываться после app:ready — ретраим, пока не появится «Персоны»
        var tries = 0;
        var timer = setInterval(function () {
            tries++;
            if ($('.menu .qdl-menu').length) { clearInterval(timer); return; }
            var list = $('.menu .menu__list').eq(0);
            var after = list.find('[data-action="myperson"]').closest('.menu__item').first();
            if (after.length) { after.after(item); clearInterval(timer); }
            else if (tries >= 30) { clearInterval(timer); if (list.length) list.append(item); }   // ~12с → вниз
        }, 400);
    }

    function start() {
        Lampa.Component.add('qdl_downloads', ComponentDownloads);
        Lampa.Component.add('qdl_card', ComponentCard);
        Lampa.Listener.follow('full', addButton);
        addMenu();
    }

    if (window.appready) start();
    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
