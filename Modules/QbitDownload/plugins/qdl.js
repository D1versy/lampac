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

    // ───────── Метаданные TMDB загрузок (хранятся в браузере, работают оффлайн) ─────────
    function getCards() { try { return Lampa.Storage.get('qdl_cards', {}) || {}; } catch (e) { return {}; } }
    function setCard(hash, card) { if (!hash || !card) return; var m = getCards(); m[hash] = card; Lampa.Storage.set('qdl_cards', m); }
    function cardByHash(hash) { return getCards()[hash] || null; }
    function hashById(id) { var m = getCards(); for (var h in m) { if (m[h] && String(m[h].id) === String(id)) return h; } return null; }

    function slimCard(m) {
        if (!m) return null;
        var isTv = m.media_type === 'tv' || (!m.media_type && (m.name || m.first_air_date) && !m.title && !m.release_date) || (!!m.first_air_date && !m.release_date);
        return {
            id: m.id,
            title: m.title || m.name,
            original_title: m.original_title || m.original_name,
            name: m.name || m.title,
            poster_path: m.poster_path,
            backdrop_path: m.backdrop_path,
            release_date: m.release_date || m.first_air_date,
            first_air_date: m.first_air_date,
            vote_average: m.vote_average,
            overview: m.overview,
            media_type: isTv ? 'tv' : 'movie',
            number_of_seasons: m.number_of_seasons,
            source: m.source || 'tmdb'
        };
    }

    function poster(card) {
        if (card && card.poster_path && window.Lampa && Lampa.TMDB) {
            try { return Lampa.TMDB.image('t/p/w300' + card.poster_path); } catch (e) {}
        }
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

    // выбор серии для сериала (играет локальный файл)
    function chooseEpisode(hash, name) {
        req(API + '/qdl/files?hash=' + hash, function (files) {
            var vids = videoFiles(files);
            if (!vids.length) { Lampa.Noty.show('Видеофайлы не найдены'); return; }
            if (vids.length === 1) { playLocal(hash, vids[0].index, baseName(vids[0].name)); return; }

            var playlist = vids.map(function (f) {
                return { title: baseName(f.name), url: API + '/qdl/stream?hash=' + hash + '&index=' + f.index };
            });
            var items = vids.map(function (f) { return { title: baseName(f.name), index: f.index }; });

            Lampa.Select.show({
                title: 'Серии — ' + (name || ''),
                items: items,
                onSelect: function (a) {
                    Lampa.Player.play({ title: a.title, url: API + '/qdl/stream?hash=' + hash + '&index=' + a.index });
                    Lampa.Player.playlist(playlist);
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { Lampa.Noty.show('Ошибка чтения файлов'); });
    }

    // открыть «как с главной» (страница фильма TMDB) либо, без метаданных, играть локально
    function openDownload(t) {
        var card = cardByHash(t.hash);
        if (card && card.id) {
            Lampa.Activity.push({
                url: '', component: 'full',
                id: card.id,
                method: card.media_type === 'tv' ? 'tv' : 'movie',
                card: card, source: card.source || 'tmdb'
            });
            return;
        }
        // нет метаданных → сразу оффлайн-воспроизведение
        req(API + '/qdl/files?hash=' + t.hash, function (files) {
            var vids = videoFiles(files);
            if (vids.length > 1) chooseEpisode(t.hash, t.name);
            else playLocal(t.hash, vids.length ? vids[0].index : -1, t.name);
        }, function () { playLocal(t.hash, -1, t.name); });
    }

    function quickMenu(t) {
        var card = cardByHash(t.hash);
        var isTv = card && card.media_type === 'tv';
        var menu = [];
        if (card && card.id) menu.push({ title: 'Открыть страницу фильма', act: 'page' });
        menu.push({ title: isTv ? '▶ Смотреть (выбрать серию)' : '▶ Смотреть (оффлайн)', act: 'play' });
        menu.push({ title: '🗑 Удалить (с файлами)', act: 'del' });

        Lampa.Select.show({
            title: t.name,
            items: menu,
            onSelect: function (b) {
                if (b.act === 'page') openDownload(t);
                else if (b.act === 'play') {
                    req(API + '/qdl/files?hash=' + t.hash, function (files) {
                        var vids = videoFiles(files);
                        if (vids.length > 1) chooseEpisode(t.hash, t.name);
                        else playLocal(t.hash, vids.length ? vids[0].index : -1, t.name);
                    }, function () { playLocal(t.hash, -1, t.name); });
                } else if (b.act === 'del') {
                    req(API + '/qdl/delete?hash=' + t.hash + '&deleteFiles=true', function () {
                        var m = getCards(); delete m[t.hash]; Lampa.Storage.set('qdl_cards', m);
                        Lampa.Noty.show('Удалено');
                        Lampa.Activity.replace();
                    });
                }
            },
            onBack: function () { Lampa.Controller.toggle('content'); }
        });
    }

    // ───────── Компонент «Загрузки» — грид карточек ─────────
    function ComponentDownloads(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div class="category-full"></div>');
        var items = [];
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
            if (!list.length) {
                body.append($('<div style="padding:2em;font-size:1.4em;opacity:.7">В «Загрузках» пока пусто. Нажми «Скачать» на карточке фильма.</div>'));
            }
            list.forEach(function (t) { comp.append(t); });

            this.activity.loader(false);
            this.activity.toggle();
            if (Lampa.Activity.active().activity === this.activity) Lampa.Controller.toggle('content');
        };

        this.append = function (t) {
            var card = cardByHash(t.hash);
            var pct = Math.round((t.progress || 0) * 100);
            var title = (card && card.title) || t.name;
            var year = ((card && (card.release_date || card.first_air_date)) || '').slice(0, 4);

            var el = Lampa.Template.get('card', { title: title, release_year: year });
            el.addClass('card--collection selector');

            var img = el.find('.card__img');
            img.attr('src', poster(card));
            img.on('error', function () { this.src = './img/img_broken.svg'; });

            // бейдж прогресса
            var view = el.find('.card__view');
            if (!view.length) view = el;
            if (pct < 100) view.append('<div style="position:absolute;left:.4em;top:.4em;background:rgba(0,0,0,.75);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">' + pct + '%</div>');
            else view.append('<div style="position:absolute;left:.4em;top:.4em;background:rgba(20,160,40,.9);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">✓</div>');

            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:enter', function () { openDownload(t); });
            el.on('hover:long', function () { quickMenu(t); });

            body.append(el);
            items.push(el);
        };

        this.start = function () {
            if (Lampa.Activity.active().activity !== this.activity) return;
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

        this.render = function () { return html; };
        this.pause = function () {};
        this.stop = function () {};
        this.destroy = function () { network.clear(); scroll.destroy(); items = []; html.remove(); };
    }

    // ───────── Поиск раздач и добавление в qBittorrent (кнопка «Скачать») ─────────
    function chooseAndDownload(movie) {
        movie = movie || {};
        var title = movie.title || movie.name || movie.original_title || movie.original_name || '';
        var year = ((movie.release_date || movie.first_air_date || '') + '').slice(0, 4);
        if (!title) { Lampa.Noty.show('Не удалось определить название'); return; }

        Lampa.Noty.show('Поиск раздач…');
        var url = API + '/qdl/search?query=' + encodeURIComponent(title) + (year ? '&year=' + year : '');

        req(url, function (list) {
            if (!list || !list.length) { Lampa.Noty.show('Раздачи не найдены'); return; }

            var items = list.slice(0, 60).map(function (t) {
                return {
                    title: t.title,
                    subtitle: [t.size, t.tracker, (t.sid ? ('сидов: ' + t.sid) : '')].filter(Boolean).join('  •  '),
                    t: t
                };
            });

            Lampa.Select.show({
                title: 'Выбери раздачу для загрузки на диск',
                items: items,
                onSelect: function (a) {
                    Lampa.Controller.toggle('content');
                    var q = a.t.magnet
                        ? ('magnet=' + encodeURIComponent(a.t.magnet))
                        : ('parselink=' + encodeURIComponent(a.t.parselink || ''));
                    Lampa.Noty.show('Добавляю в загрузки…');
                    req(API + '/qdl/add?' + q + '&title=' + encodeURIComponent(a.t.title || title), function (r) {
                        if (r && r.success) {
                            if (r.hash) setCard(r.hash, slimCard(movie));   // запоминаем метаданные для «Загрузок»
                            Lampa.Noty.show('✓ Добавлено в «Загрузки»');
                        } else Lampa.Noty.show('Ошибка: ' + ((r && r.error) || 'qBittorrent'));
                    }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { Lampa.Noty.show('Ошибка поиска раздач'); });
    }

    // ───────── Кнопки на карточке фильма: «Скачать» + «Смотреть (загружено)» если уже скачано ─────────
    function addButton(e) {
        try {
            if (e.type !== 'complite' || !e.object || !e.object.activity) return;
            var render = e.object.activity.render();
            if (!render) return;

            var movie = (e.data && e.data.movie) ? e.data.movie : (e.object.card || {});
            var cont = $('.full-start__buttons', render);
            if (!cont.length) cont = $('.full-start-new__buttons', render);
            if (!cont.length) return;

            // «Скачать»
            if (!$('.qdl-download', render).length) {
                var btn = $('<div class="full-start__button selector qdl-download">' + ICON + '<span>Скачать</span></div>');
                btn.on('hover:enter', function () { chooseAndDownload(movie); });
                cont.append(btn);
            }

            // «Смотреть (загружено)» — если этот фильм уже скачан
            if (movie && movie.id && !$('.qdl-watch', render).length) {
                var hash = hashById(movie.id);
                if (hash) {
                    var play = $('<div class="full-start__button selector qdl-watch">' + ICON + '<span>Смотреть (загружено)</span></div>');
                    play.on('hover:enter', function () {
                        req(API + '/qdl/files?hash=' + hash, function (files) {
                            var vids = videoFiles(files);
                            if (vids.length > 1) chooseEpisode(hash, movie.title || movie.name);
                            else playLocal(hash, vids.length ? vids[0].index : -1, movie.title || movie.name);
                        }, function () { playLocal(hash, -1, movie.title || movie.name); });
                    });
                    cont.prepend(play);
                }
            }
        } catch (err) { console.log('qdl: addButton', err); }
    }

    // ───────── Пункт меню «Загрузки» (под «Персоны») ─────────
    function addMenu() {
        try {
            if ($('.menu .qdl-menu').length) return;
            var item = $('<li class="menu__item selector qdl-menu"><div class="menu__ico">' + ICON + '</div><div class="menu__text">Загрузки</div></li>');
            item.on('hover:enter', function () {
                Lampa.Activity.push({ url: '', title: 'Загрузки', component: 'qdl_downloads', page: 1 });
            });

            var list = $('.menu .menu__list').eq(0);
            var after = list.find('.menu__item').filter(function () {
                return $(this).find('.menu__text').text().trim().toLowerCase() === 'персоны';
            }).first();

            if (after.length) after.after(item);
            else list.append(item);

            if (window.Lampa && Lampa.Controller && Lampa.Controller.enabled().name === 'menu')
                Lampa.Controller.toggle('menu');
        } catch (err) { console.log('qdl: addMenu', err); }
    }

    function start() {
        Lampa.Component.add('qdl_downloads', ComponentDownloads);
        Lampa.Listener.follow('full', addButton);
        addMenu();
    }

    if (window.appready) start();
    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
