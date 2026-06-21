(function () {
    'use strict';

    var API = '{localhost}';

    function req(url, cb, err) {
        try {
            var net = new Lampa.Reguest();
            net.timeout(45000);
            net.silent(url, function (json) { cb(json); }, function (a, c) { if (err) err(a, c); });
        } catch (e) {
            // fallback на fetch
            fetch(url).then(function (r) { return r.json(); }).then(cb).catch(function () { if (err) err(); });
        }
    }

    // ───────── Скачивание: поиск раздач и добавление в qBittorrent ─────────
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
                        if (r && r.success) Lampa.Noty.show('✓ Добавлено в «Загрузки»');
                        else Lampa.Noty.show('Ошибка: ' + ((r && r.error) || 'qBittorrent'));
                    }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { Lampa.Noty.show('Ошибка поиска раздач'); });
    }

    // ───────── Кнопка «Скачать» на карточке фильма ─────────
    function addButton(e) {
        try {
            if (e.type !== 'complite' || !e.object || !e.object.activity) return;
            var render = e.object.activity.render();
            if (!render || $('.qdl-download', render).length) return;

            var movie = (e.data && e.data.movie) ? e.data.movie : (e.object.card || {});

            var btn = $(
                '<div class="full-start__button selector qdl-download">' +
                '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">' +
                '<path d="M12 3v12m0 0l-4-4m4 4l4-4M5 19h14" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>' +
                '<span>Скачать</span></div>'
            );

            btn.on('hover:enter', function () { chooseAndDownload(movie); });

            var cont = $('.full-start__buttons', render);
            if (!cont.length) cont = $('.full-start-new__buttons', render);
            if (cont.length) cont.append(btn);
        } catch (err) { console.log('qdl: addButton', err); }
    }

    // ───────── Раздел «Загрузки» ─────────
    function playItem(t) {
        var url = API + '/qdl/stream?hash=' + t.hash;
        Lampa.Player.play({ title: t.name, url: url });
        Lampa.Player.playlist([{ title: t.name, url: url }]);
    }

    function openDownloads() {
        Lampa.Noty.show('Загрузки…');
        req(API + '/qdl/list', function (list) {
            if (!list || !list.length) { Lampa.Noty.show('Загрузок пока нет'); return; }

            var items = list.map(function (t) {
                var pct = Math.round((t.progress || 0) * 100);
                return { title: t.name, subtitle: pct + '%  •  ' + t.state, t: t };
            });

            Lampa.Select.show({
                title: 'Загрузки',
                items: items,
                onSelect: function (a) {
                    if ((a.t.progress || 0) < 1)
                        Lampa.Noty.show('Качается: ' + Math.round(a.t.progress * 100) + '% (можно подождать)');
                    playItem(a.t);
                },
                onLong: function (a) {
                    Lampa.Select.show({
                        title: a.t.name,
                        items: [
                            { title: '▶ Играть', act: 'play' },
                            { title: '🗑 Удалить (с файлами)', act: 'del' }
                        ],
                        onSelect: function (b) {
                            if (b.act === 'play') playItem(a.t);
                            if (b.act === 'del')
                                req(API + '/qdl/delete?hash=' + a.t.hash + '&deleteFiles=true', function () {
                                    Lampa.Noty.show('Удалено');
                                });
                        },
                        onBack: function () { Lampa.Controller.toggle('content'); }
                    });
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { Lampa.Noty.show('Ошибка получения списка'); });
    }

    function addMenu() {
        try {
            if ($('.menu .qdl-menu').length) return;
            var item = $(
                '<li class="menu__item selector qdl-menu">' +
                '<div class="menu__ico"><svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">' +
                '<path d="M12 3v12m0 0l-4-4m4 4l4-4M5 19h14" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg></div>' +
                '<div class="menu__text">Загрузки</div></li>'
            );
            item.on('hover:enter', openDownloads);
            $('.menu .menu__list').eq(0).append(item);
            if (window.Lampa && Lampa.Controller && Lampa.Controller.enabled().name === 'menu')
                Lampa.Controller.toggle('menu');
        } catch (err) { console.log('qdl: addMenu', err); }
    }

    function start() {
        Lampa.Listener.follow('full', addButton);
        addMenu();
    }

    if (window.appready) start();
    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
