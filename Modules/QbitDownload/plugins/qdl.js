(function () {
    'use strict';

    // Синглтон: при двойном подключении qdl.js (нативная оболочка + инжект, горячая переподгрузка)
    // второй экземпляр дублировал observers/interval/Listener — дубли пунктов меню и красного бейджа
    if (window.qdl_plugin_loaded) return;
    window.qdl_plugin_loaded = 1;

    var API = '{localhost}';
    var ICON = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M12 3v12m0 0l-4-4m4 4l4-4M5 19h14" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    // 2.30: у каждой кнопки карточки СВОЯ иконка — одна стрелка на всех читалась как три «Скачать» разных цветов
    var WATCH_ICON = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><circle cx="12" cy="12" r="9" stroke="currentColor" stroke-width="2.2"/><path d="M10 8.7l5.4 3.3-5.4 3.3V8.7z" fill="currentColor" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/></svg>';
    var CONTINUE_ICON = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M21.5 4.5v5h-5" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/><path d="M20 14.8A8.5 8.5 0 1 1 18 6l3.5 3.5" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/><path d="M10.2 9.2l4.4 2.8-4.4 2.8V9.2z" fill="currentColor" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/></svg>';
    var BOX_ICON = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M21 16.5v-9L12 3 3 7.5v9L12 21l9-4.5z" stroke="currentColor" stroke-width="2" stroke-linejoin="round"/><path d="M3.3 7.6L12 12l8.7-4.4" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/><path d="M12 12v9" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';
    var BELL = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M6 8a6 6 0 1112 0c0 7 3 9 3 9H3s3-2 3-9z" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/><path d="M10.3 21a1.94 1.94 0 003.4 0" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    var CAM = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M2.8 7.4l14.4-3.3 1.3 5.6L4.1 13 2.8 7.4z" stroke="currentColor" stroke-width="2" stroke-linejoin="round"/><path d="M6.5 12.2V15a3 3 0 003 3h1.2" stroke="currentColor" stroke-width="2" stroke-linecap="round"/><circle cx="18.5" cy="18" r="2.6" stroke="currentColor" stroke-width="2"/><path d="M18 9.9l3.2 1.5" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';
    var REC = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><circle cx="12" cy="12" r="9" stroke="currentColor" stroke-width="2"/><circle cx="12" cy="12" r="3.5" fill="currentColor"/></svg>';
    var ANIME = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><rect x="3" y="4" width="18" height="14" rx="2.5" stroke="currentColor" stroke-width="2"/><path d="M10 8.5l4.5 2.5L10 13.5v-5z" fill="currentColor"/><path d="M8 21h8" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';

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
            // зелёная «Смотреть» карточки загрузки без TMDB — та же семантика, что у .qdl-watch-btn
            '.qdl-watch{background:rgba(20,160,40,.92);color:#fff}' +
            '.qdl-watch.focus{background:#19b531 !important;color:#fff !important;transform:scale(1.03)}' +
            '.qdl-watch svg{width:1.15em;height:1.15em;flex:none}' +
            // «Продолжить» на том же экране — синяя, как на полной карточке (.qdl-continue-btn)
            '.qdl-continue{background:rgba(25,100,210,.92);color:#fff}' +
            '.qdl-continue.focus{background:#2b7de9 !important;color:#fff !important;transform:scale(1.03)}' +
            '.qdl-continue svg{width:1.15em;height:1.15em;flex:none}' +
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
            // 2.30: подписи наших кнопок видны ВСЕГДА — Lampa прячет span у кнопок без .focus,
            // а на touch класс .focus не ставится вовсе (иконки без текста и вызвали путаницу).
            // Кап ширины: epShort на непарсящемся имени отдаёт до 24 символов — без него одна
            // кнопка «Продолжить» съедала пол-ряда.
            '.full-start__buttons .qdl-continue-btn span,.full-start__buttons .qdl-watch-btn span,.full-start__buttons .qdl-download span,' +
            '.full-start-new__buttons .qdl-continue-btn span,.full-start-new__buttons .qdl-watch-btn span,.full-start-new__buttons .qdl-download span' +
            '{display:inline !important;max-width:13em;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            // ⚠️ Постоянные подписи ломают допущение upstream «ряд всегда влезает» (он прятал текст
            // у несфокусированных именно для этого). ПЕРЕНОС, а не overflow: ряд НИКТО не скроллит
            // (это не Lampa.Scroll, фокус-движок не зовёт scrollIntoView), и обрезанный хвост
            // означал бы фокус на невидимой кнопке — «пульт не работает» (та же грабля, что §AK.3).
            '.full-start__buttons,.full-start-new__buttons{flex-wrap:wrap;row-gap:.5em}' +
            // DMCA-карточка (CUB блокирует): остаются только наши кнопки — «Скачать», зелёная
            // «Смотреть» и «Продолжить» (2.30: до этого правило прятало и «Продолжить»,
            // т.е. на скачанном заблокированном сериале первоклассная кнопка исчезала)
            '.qdl-dmca .full-start__buttons .full-start__button:not(.qdl-download):not(.qdl-watch-btn):not(.qdl-continue-btn),' +
            '.qdl-dmca .full-start-new__buttons .full-start__button:not(.qdl-download):not(.qdl-watch-btn):not(.qdl-continue-btn){display:none !important}' +
            // своя кнопка фуллскрина в плеере (НЕ класс player-panel__fullscreen — иначе Lampa её прячет на моб.)
            '.qdl-fs{display:inline-flex !important;align-items:center;justify-content:center;padding:.6em;margin:0 .2em;cursor:pointer;opacity:.85;vertical-align:middle}' +
            '.qdl-fs.focus{opacity:1;transform:scale(1.12)}' +
            '.qdl-fs svg{width:1.8em;height:1.8em}' +
            // карточка коллекции в «Загрузках»: эффект стопки постеров (дёшево, без доп. DOM)
            '.qdl-col-card .card__view{box-shadow:.3em -.3em 0 -.08em rgba(255,255,255,.28),.6em -.6em 0 -.16em rgba(255,255,255,.14);border-radius:.3em}' +
            // сетка эфира: по центру и во всю ширину (рут-em клампится на 10.6px → телефон ~36em:
            // фикс-ширина давала одну колонку слева с мёртвым полем справа). flex-grow растягивает
            // колонки, кап 38em не даёт одинокой плитке распухнуть на весь ТВ.
            '.qdl-watch-grid{display:flex;flex-wrap:wrap;gap:1em;padding:1.2em 1.4em;justify-content:center}' +
            '.qdl-watch-grid>div:not(.qdl-watch-tile){flex:1 1 100%}' +
            '.qdl-watch-tile{flex:1 1 20em;max-width:38em;min-width:0;transition:transform .1s}' +
            // тайл сетки эфира: без своего focus-правила он никак не подсвечивается пультом
            '.qdl-watch-tile.focus{box-shadow:0 0 0 .22em #fff;transform:scale(1.04);z-index:1}' +
            // фокус пульта в наших списках/кнопках: Lampa вешает класс .focus только на ТВ/десктопе,
            // а генерического .selector.focus в её CSS нет — без этих правил фокус невидим
            '.qdl-row-focus{transition:box-shadow .1s}' +
            '.qdl-row-focus.focus{box-shadow:0 0 0 .2em #fff}' +
            // экран серий: текущая (продолжаемая) серия подсвечена синим
            '.qdl-ep--cur{background:rgba(25,100,210,.22) !important}' +
            '.qdl-btn-focus.focus{background:#fff !important;color:#000 !important;opacity:1 !important}' +
            '.qdl-btn-green.focus{background:#19b531 !important;box-shadow:0 0 0 .15em #fff}' +
            // svg-иконки в наших плоских кнопках (jut.su и т.п.) и в строках экрана серий
            '.qdl-btn-focus svg,.qdl-btn-green svg{width:1.15em;height:1.15em;flex:none}' +
            '.qdl-ep-play svg{display:block;width:1.5em;height:1.5em}' +
            // 2.32: экраны jut.su жили БЕЗ горизонтальных полей — постер и текст лежали впритык
            // к краю (замер: left=0, описание во всю ширину экрана; штатные экраны Lampa держат
            // отступ). Поля em-ные: рут-em Lampa пропорционален ширине окна, т.е. процент от
            // экрана одинаков на ТВ и мониторе; на телефоне рут-em клампится (шире в долях
            // экрана) — там поля срезаются медиазапросом.
            '.qdl-jut-page{padding:0 3em 2.5em}' +
            '.qdl-jut-head{display:flex;flex-wrap:wrap;gap:2em;padding:1.6em 0 1.4em;align-items:flex-start}' +
            '.qdl-jut-poster{width:14em;border-radius:.8em;flex:0 0 auto;background:#222;box-shadow:0 1em 3em rgba(0,0,0,.5)}' +
            '.qdl-jut-info{flex:1 1 20em;min-width:0}' +
            '.qdl-jut-title{font-size:2em;font-weight:600;line-height:1.15}' +
            // строка во всю ширину ТВ не читается — кап по длине строки, как в карточке загрузки
            '.qdl-jut-descr{opacity:.85;font-size:1.15em;line-height:1.55;max-width:52em}' +
            '@media screen and (max-width:580px){.qdl-jut-page{padding:0 1.2em 1.5em}' +
            '.qdl-jut-head{gap:1.2em;padding:1em 0}.qdl-jut-poster{width:9.5em}}' +
            // бейдж непрочитанных на нашей иконке уведомлений в хедере (красный кружок с числом)
            '.qdl-noti-head{position:relative}' +
            '.qdl-noti-head-badge{position:absolute;top:-0.1em;right:-0.1em;min-width:1.5em;height:1.5em;padding:0 0.35em;box-sizing:border-box;background:#d33;color:#fff;border:0.12em solid #fff;border-radius:1em;font-size:0.62em;line-height:1.26em;font-weight:700;text-align:center}';
        document.head.appendChild(st);
    }

    // ───────── Фикс высоты селектбокса (upstream-баг Lampa) ─────────
    // Select.show() кладёт шапку в jQuery .data('mheight'), а Layer.frameUpdate читает
    // СЫРОЕ DOM-свойство elem.mheight (так пишет Scroll.minus) → высота шапки селектбокса
    // никогда не вычитается: инлайн-height у .selectbox__body больше доступного места,
    // низ длинных списков (серии, 60 раздач, озвучки) уезжает за экран и не докручивается.
    // Чиним свойством (канон Scroll.minus) + немедленный Layer.update; страховкой — явный
    // px max-height (формула frameUpdate) на десктопе/ТВ: переживает standards mode и старый
    // flex. Мобилу ≤480 не трогаем — там родной кап 60vh (iPhone-шит).
    function fixSelectHeight(e) {
        try {
            var root = (e && e.html) || (Lampa.Select && Lampa.Select.render && Lampa.Select.render());
            if (!root || !root.find) return;
            var body = root.find('.selectbox__body');
            var head = root.find('.selectbox__head');
            if (!body.length || !head.length) return;
            body[0].mheight = head[0];                        // то, что реально читает Layer.frameUpdate
            try { Lampa.Layer.update(body); } catch (e1) {}   // пересчитать height сейчас, не ждать resize
            if (window.innerWidth > 480) {
                var hd = document.querySelector('.head');
                var nv = document.querySelector('.navigation-bar');
                var landscape = window.innerWidth > window.innerHeight && window.innerHeight < 768;
                var h = window.innerHeight
                    - (hd ? hd.getBoundingClientRect().height : 0)
                    - (nv && !landscape ? nv.getBoundingClientRect().height : 0)
                    - head[0].getBoundingClientRect().height;
                if (h > 0) body.css('max-height', h);         // следующий show() сбросит в 'unset' → пересчитаем заново
            }
        } catch (e2) {}
    }

    function initSelectFix() {
        if (window.__qdl_selectfix) return;   // повторная загрузка qdl.js → одна подписка
        if (Lampa.Select && Lampa.Select.listener && typeof Lampa.Select.listener.follow === 'function') {
            window.__qdl_selectfix = true;
            Lampa.Select.listener.follow('fullshow', fixSelectHeight);
        } else if (Lampa.Select && typeof Lampa.Select.show === 'function') {
            // бандл без listener: оборачиваем show (fullshow в show синхронный — паритет)
            window.__qdl_selectfix = true;
            var orig = Lampa.Select.show;
            Lampa.Select.show = function () { var r = orig.apply(this, arguments); fixSelectHeight(null); return r; };
        }
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

    // Пинг «просмотрено» (qdl 2.15): $.get('https://tmdb.<cub>/watch?id=…') — raw jQuery МИМО
    // Lampa.Reguest, request_before не стреляет и cubproxy.js его не видит; единственный ловец — XHR.
    // → локальная заглушка /cub/api/checker (CubProxy отвечает 'ok' без похода наружу), ответ игнорируется.
    function rewriteWatchUrl(u) {
        return /^https?:\/\/(?:[^\/]+\/cub\/)?tmdb\.[^\/]*\/watch\?/.test(String(u))
            ? API + '/cub/api/checker' : null;
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
        // URL строим СРАЗУ локальным (qdl 2.19). Раньше тут был внешний https://tmdb.<cub>/blocked,
        // а на наш /cub/ его заворачивал cubproxy.js на request_before — но у этого два зазора:
        //   1) фолбэк req(): если `new Lampa.Reguest()` бросит, ветка catch уходит голым fetch(url)
        //      МИМО request_before — запрос ушёл бы прямо на cub.rip;
        //   2) cubproxy.js переписывает, только если домен есть в Lampa.Manifest.cub_mirrors —
        //      незнакомое зеркало из qdl_cub_domain пролетало бы наружу.
        // Итоговый адрес тот же, что получался после cubproxy (CubProxy: catch-all cub/{*suffix}).
        req(API + '/cub/tmdb.' + (window.qdl_cub_domain || 'cub.rip') + '/blocked', function (list) {
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
                        var ru = rewriteCubUrl(url) || rewriteWatchUrl(url);
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

    // Карточка «Загрузок», пришедшая с jut.su: сервер кладёт {jut:{slug}} в /qdl/list,
    // когда у локального маркера есть поле jut. Признак нужен, чтобы развести слежение
    // (у торрентов — по infohash, здесь — по slug в отдельном контуре).
    function isJut(t) {
        return !!(t && t.jut && t.jut.slug);
    }

    // Режим слежения jut-карточки: 'off' | 'notify' (только уведомления) | 'grab' (ещё и качаем).
    // Фолбэк на t.watched — старый сервер режим не отдаёт, а трактовать подписку как
    // «только уведомления» нельзя: у неё автоскачивание уже работало.
    function jutMode(t) {
        var m = t && t.jut && t.jut.watch;
        if (m === 'off' || m === 'notify' || m === 'grab') return m;
        return (t && t.watched) ? 'grab' : 'off';
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

    // Год из имени раздачи: cleanName его срезает, а для сверки кандидата он нужен.
    function releaseYear(name) {
        var m = String(name || '').match(/\b(19|20)\d\d\b/);
        return m ? parseInt(m[0], 10) : 0;
    }

    // Ключ сверки: имя раздачи без хвоста сезона/серий («Холод S01», «Лаки Сезон 1»).
    function matchKey(cleaned) {
        return normTitle(String(cleaned || '')
            .replace(/\bs\d{1,2}(\s*e\d{1,3})?\b/ig, ' ')
            .replace(/\bсезон\s*\d+/ig, ' '));
    }

    // Строгая сверка кандидата TMDB с именем раздачи: название должно СОВПАСТЬ (или быть началом),
    // и если год в имени раздачи есть — сойтись ±1.
    // Без этого search/multi по «Holod…» отдавал «Голод-33» (1991) с постером, и чужая карточка
    // прилипала к загрузке навсегда. Точную привязку (btih → tmdb) делает сервер — MetaHeal.cs;
    // здесь остаются только очевидные совпадения, всё сомнительное лучше оставить без карточки.
    function cardMatches(key, year, c) {
        if (!key || !c) return false;

        var cand = [c.title, c.name, c.original_title, c.original_name], hit = false;
        for (var i = 0; i < cand.length; i++) {
            var t = normTitle(cand[i]);
            if (t && (t === key || t.indexOf(key + ' ') === 0 || key.indexOf(t + ' ') === 0)) { hit = true; break; }
        }
        if (!hit) return false;

        if (year) {
            var cy = parseInt(String(c.release_date || c.first_air_date || '').slice(0, 4), 10);
            if (cy && Math.abs(cy - year) > 1) return false;
        }
        return true;
    }

    function tmdbSearch(name, year, cb) {
        try {
            var url = Lampa.TMDB.api('search/multi?api_key=' + tmdbKey() + '&language=ru-RU&query=' + encodeURIComponent(name));
            req(url, function (r) {
                var key = matchKey(name);
                var list = (r && r.results) ? r.results.filter(function (x) {
                    return (x.media_type === 'movie' || x.media_type === 'tv') && x.poster_path && cardMatches(key, year, x);
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

    function enrich(name, cb) {   // имя раздачи → полная карточка TMDB (только при строгом совпадении)
        tmdbSearch(cleanName(name), releaseYear(name), function (found) {
            if (!found) { cb(null); return; }
            tmdbDetails(found.id, found.media_type, function (d) { cb(d || found); });
        });
    }

    function posterUrl(item) {
        if (item && item.has_poster) return API + '/qdl/poster?hash=' + item.hash;
        return './img/img_broken.svg';
    }

    // 1×1 прозрачный GIF. «Постера нет» в ленте уведомлений — ШТАТНЫЙ случай (строка DIAG «Поиск
    // раздач», вычищенная раздача), а img_broken.svg читается как поломка приложения. У <img> уже
    // есть background:#222, поэтому прозрачный пиксель поверх него даёт нейтральную плитку.
    // src='' резолвится в URL документа и сам стреляет error, а <img> вовсе без src на части
    // ТВ-движков всё равно рисует битую иконку — поэтому именно data-URI.
    var PX1 = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==';

    // Постер строки уведомления. Решает СЕРВЕР (n.posterUrl, qdl 2.46): только он знает, лежит ли
    // файл на диске, есть ли апгрейженная обложка и какой хеш у раздачи СЕЙЧАС. У jut-уведомления
    // hash ПСЕВДО (sha1("jutsu:"+slug)), и файла img/<hash>.jpg для НЕ скачанного тайтла не бывает —
    // /qdl/poster отвечал 404, и на каждой отслеживаемой, но не скачанной серии висела заглушка.
    // ⚠️ Ветки-фолбэки нужны на время выкатки (клиент 2.46 против сервера 2.45) — не убирать раньше 2.47.
    function notiPoster(n) {
        if (!n) return PX1;
        if (n.posterUrl) return API + n.posterUrl;
        if (n.slug) return jutPosterUrl(n.slug);
        if (n.hash) return API + '/qdl/poster?hash=' + n.hash;
        return PX1;
    }

    function videoFiles(files) {
        return (files || []).filter(function (f) { return /\.(mkv|mp4|avi|ts|m4v|webm|mov)$/i.test(f.name || ''); })
            .sort(function (a, b) { return String(a.name).localeCompare(String(b.name), undefined, { numeric: true }); });
    }

    // Сортировать по ИМЕНИ можно только когда номеров серий нет: имена донора и основной
    // вперемешку сортируются неправильно. Есть epkey — раскладываем по (вид, сезон, номер):
    // локальная (jut) ветка /qdl/episodes отдаёт файлы лексикографически по пути, да и
    // 45-секундный _epCache может держать ответ старого сервера.
    function mergedVideoFiles(files) {
        files = files || [];
        var hasEp = false;
        for (var i = 0; i < files.length; i++) if (files[i] && files[i].epkey) { hasEp = true; break; }
        if (!hasEp) return videoFiles(files);
        return sortEpisodes(files.filter(function (f) { return /\.(mkv|mp4|avi|ts|m4v|webm|mov)$/i.test((f && f.name) || ''); }));
    }

    // серия может лежать в раздаче-доноре (охота) — стрим/аудио строим от её hash
    function srcHash(f, hash) { return (f && f.hash) || hash; }

    // объединённый плейлист сериала; фолбэк на /qdl/files (старый сервер / ошибка).
    // Мемоизация: путь «карточка → Смотреть» дёргал /qdl/episodes до 3 раз подряд
    // (addContinueButton → watchByHash → chooseEpisode). TTL короткий: докачка/охота меняют
    // список. Параллельные запросы одного hash коалесцируются в один сетевой вызов.
    var _epCache = {};    // hash -> {t, files}
    var _epPending = {};  // hash -> [{cb, err}]
    var EP_TTL = 45000;
    function dropEpCache(hash) { if (hash) delete _epCache[hash]; else _epCache = {}; }

    function fetchEpisodes(hash, cb, err) {
        var c = _epCache[hash];
        if (c && Date.now() - c.t < EP_TTL) { try { cb(c.files); } catch (e) {} return; }   // cb в try: на хите он зовётся синхронно
        if (_epPending[hash]) { _epPending[hash].push({ cb: cb, err: err }); return; }
        _epPending[hash] = [{ cb: cb, err: err }];
        var done = function (ok, files) {
            var subs = _epPending[hash] || []; delete _epPending[hash];
            if (ok) _epCache[hash] = { t: Date.now(), files: files };
            for (var i = 0; i < subs.length; i++)
                try { if (ok) subs[i].cb(files); else if (subs[i].err) subs[i].err(); } catch (e) {}
        };
        try {
            req(API + '/qdl/episodes?hash=' + hash, function (files) {
                if (files && files.length !== undefined) done(true, files);
                else req(API + '/qdl/files?hash=' + hash, function (f) { done(true, f); }, function () { done(false); });
            }, function () {
                req(API + '/qdl/files?hash=' + hash, function (f) { done(true, f); }, function () { done(false); });
            });
        } catch (e) { done(false); }   // синхронный throw из req не должен заклинить _epPending навсегда
    }

    // ───────── Прогрев кеша сервера (голова+хвост файла в page cache + ffprobe-кеш) ─────────
    // fire-and-forget: голый fetch, не req (ответ не важен, Reguest и 45-с таймаут — лишние)
    function warmup(hash, index) {
        try { fetch(API + '/qdl/warmup?hash=' + hash + '&index=' + (index >= 0 ? index : -1)).catch(function () {}); } catch (e) {}
    }

    // прогрев при открытии карточки: серия «Продолжить» (или единственный файл / первая серия)
    // греется, ПОКА полная карточка ждёт свои TMDB/CUB-запросы; попутно наполняет кеш fetchEpisodes,
    // так что последующий addContinueButton сетевого вызова не делает
    function prewarmForCard(hash) {
        fetchEpisodes(hash, function (files) {
            var vids = mergedVideoFiles(files);
            if (!vids.length) return;
            var target = vids.length === 1 ? vids[0] : (chooseContinue(vids, function (f) { return pickTimeline(hash, f); }) || vids[0]);
            warmup(srcHash(target, hash), target.index);
        });
    }

    // при старте серии греем следующую по плейлисту — авто-переход N→N+1 стартует из RAM.
    // (N+2 при автопереходе не греется — хук на Lampa.Player.listener('start') оставлен как задел)
    function warmupNext(hash, vids, current) {
        for (var i = 0; i < vids.length; i++)
            if (vids[i] === current || (vids[i].index === current.index && srcHash(vids[i], hash) === srcHash(current, hash))) {
                if (vids[i + 1]) warmup(srcHash(vids[i + 1], hash), vids[i + 1].index);
                return;
            }
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

    // номер серии для бейджа на экране серий: epkey сервера (s1e4) → 4, иначе парс имени
    // (та же цепочка, что epShort), иначе null (вызывающий подставит порядковый).
    // Хвостовые цифры, а не «e(\d+)»: epkey бывает и у экстр (film1/ova2/sp3) — им тоже нужен
    // номер, иначе экстры сортируются как «без номера» и валятся в конец кучей.
    function epNumber(f) {
        var m = /(\d+)$/.exec(String((f && f.epkey) || ''));
        if (m) return parseInt(m[1], 10);
        var b = stripExt(baseName((f && f.name) || ''));
        m = /S(\d+)[\s._-]*E(\d+)/i.exec(b);
        if (m) return parseInt(m[2], 10);
        m = /(?:серия|episode|ep)[\s._#-]*(\d{1,4})/i.exec(b);
        if (m) return parseInt(m[1], 10);
        m = /(?:^|[\s._[(-])(\d{1,3})(?:[\s._\])-]|$)/.exec(b);
        if (m) return parseInt(m[1], 10);
        return null;
    }

    // Вид серии: обычные идут первыми, экстры (фильм/OVA/спецвыпуск) — после них.
    // Источник — epkey сервера (s1e7 / film1 / ova2 / gameova1 / sp3). Файлы без epkey
    // (торренты, старый сервер) считаем обычными сериями — порядок и поведение как раньше.
    function epKindRank(f) {
        var k = String((f && f.epkey) || '');
        if (!k || /^s\d+e\d+$/i.test(k)) return 0;
        if (/^film\d*$/i.test(k)) return 1;
        if (/^ova\d*$/i.test(k)) return 2;
        if (/^gameova\d*$/i.test(k)) return 3;
        return 4;
    }

    // Сезон серии: поле сервера → epkey → имя файла → 1 (односезонные аниме сезон не пишут)
    function epSeason(f) {
        if (f && f.season > 0) return f.season;
        var m = /^s(\d+)e\d+$/i.exec(String((f && f.epkey) || ''));
        if (m) return parseInt(m[1], 10);
        m = /S(\d+)[\s._-]*E\d+/i.exec(stripExt(baseName((f && f.name) || '')));
        return m ? parseInt(m[1], 10) : 1;
    }

    // 🔥 Порядок массива ≠ порядок серий. Локальная (jut) ветка /qdl/episodes отдаёт файлы
    // лексикографически по пути: s1e100 попадает между s1e10 и s1e11, а film/ova встают В НАЧАЛО.
    // «Продолжить» же считалась по ИНДЕКСАМ — отсюда промахи на длинных тайтлах и на тайтлах
    // с экстрами. Сортируем копию, но ТЕМИ ЖЕ ссылками: vids.indexOf(cur) и r.f === cur
    // у вызывающих обязаны продолжать работать.
    // ⚠️ Исходный индекс — часть ключа сортировки: стабильность Array.prototype.sort старые
    // движки ТВ не гарантируют (тот же приём в orderButtons).
    var EP_NO_NUM = 1e9;   // серии без номера (экстры без цифры, RANGE) — в конец своей группы
    function sortEpisodes(list) {
        var keyed = (list || []).map(function (f, i) {
            var n = epNumber(f);
            return { f: f, i: i, k: epKindRank(f), s: epSeason(f), n: n === null ? EP_NO_NUM : n };
        });
        keyed.sort(function (a, b) { return (a.k - b.k) || (a.s - b.s) || (a.n - b.n) || (a.i - b.i); });
        return keyed.map(function (x) { return x.f; });
    }

    // Что продолжать (список ОБЯЗАН быть отсортирован — см. sortEpisodes):
    // (1) ПОСЛЕДНЯЯ серия на паузе (5–90%), но НЕ РАНЬШЕ последней досмотренной;
    // (2) иначе первая недосмотренная ОБЫЧНАЯ серия после последней досмотренной;
    // (3) прогресса нет / всё досмотрено → null (кнопка не показывается).
    // 🔥 Ограничение «не раньше последней досмотренной» — фикс жалобы 14.08.2026: старый надкус
    // первой серии (открыл на пару минут месяц назад, 12%) навсегда перебивал свежий досмотр,
    // и «Продолжить» на аниме с jut.su вечно вела на 1-ю серию. Брошенный хвост слева — это
    // не «продолжить», это «когда-то начинал».
    // Экстры в (2) не предлагаем: после финала сезона «Продолжить · 1 фильм» — не то, чего ждут;
    // но НАЧАТУЮ экстру (1) вернёт, она попадает в общий скан справа.
    function pickContinue(sorted, viewFn) {
        var i, p, last = -1;
        for (i = sorted.length - 1; i >= 0; i--)
            if (((viewFn(sorted[i]) || {}).percent || 0) >= 90) { last = i; break; }
        for (i = sorted.length - 1; i > last; i--) {
            p = (viewFn(sorted[i]) || {}).percent || 0;
            if (p >= 5 && p < 90) return sorted[i];
        }
        if (last >= 0)
            for (i = last + 1; i < sorted.length; i++)
                if (epKindRank(sorted[i]) === 0 && ((viewFn(sorted[i]) || {}).percent || 0) < 90) return sorted[i];
        return null;
    }

    function chooseContinue(vids, viewFn) { return pickContinue(sortEpisodes(vids), viewFn); }

    // Первая непросмотренная (для автоплея, когда продолжать нечего) — тоже по номерам, не по индексу
    function firstUnwatched(vids, viewFn) {
        var sorted = sortEpisodes(vids);
        for (var i = 0; i < sorted.length; i++)
            if (epKindRank(sorted[i]) === 0 && ((viewFn(sorted[i]) || {}).percent || 0) < 90) return sorted[i];
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

    // ───────── Зеркало прогресса устройства: file_view ↔ нативное KV AndroidJS (см. claude/06 §AM) ─────────
    // localStorage привязан к origin, а нативные клиенты гоняются между хостами (LAN ↔ tv.d1versy.com) —
    // у каждого origin был СВОЙ file_view («прогресс то есть, то нет»). Мост AndroidJS даёт per-app KV,
    // общее для всех origin (Apple: BridgeStorage/storage.json, Android: SharedPreferences "storage");
    // сама Lampa AndroidJS.set/get не использует — ключ qdl_file_view целиком наш.
    // Зеркало: {hash: {…road, t: epoch-ms записи}}. Арбитр конфликтов — qdl_tl_meta (per-origin,
    // Lampa.Storage): {hash: t последнего принятия/отправки ЭТИМ origin}.
    // В браузере/Tizen (нет AndroidJS) фича молчит. Выключатель: Lampa.Storage 'qdl_tl_mirror'='off'.

    function hasOwn(o, k) { return Object.prototype.hasOwnProperty.call(o, k); }

    // строка из KV → объект-зеркало; null/''/битый JSON/не-объект → {}
    function mirrorParse(raw) {
        try {
            var o = JSON.parse(raw);
            return (o && typeof o === 'object' && !Array.isArray(o)) ? o : {};
        } catch (e) { return {}; }
    }

    // валидный road: percent/time/duration — конечные числа ≥ 0 (легаси-число «просто percent» не зеркалим)
    function mirrorValidRoad(r) {
        return !!(r && typeof r === 'object' &&
            typeof r.percent === 'number' && isFinite(r.percent) && r.percent >= 0 &&
            typeof r.time === 'number' && isFinite(r.time) && r.time >= 0 &&
            typeof r.duration === 'number' && isFinite(r.duration) && r.duration >= 0);
    }

    // копия road без служебной метки t; addT — проставить новую метку
    function mirrorRoad(src, addT) {
        var o = {}, k;
        for (k in src) if (hasOwn(src, k) && k !== 't') o[k] = src[k];
        if (addT !== undefined) o.t = addT;
        return o;
    }

    var MIRROR_CAP = 1000;            // кап зеркала И бюджет посева (иначе local>капа → вечный re-seed-churn)
    var MIRROR_SEED_T = 1;            // сентинел-метка посева: «древнее всего» — старый local не выдаёт себя
                                      // за свежий и НИКОГДА не перебивает чужой прогресс (ревью §AM: клоббер на выкате)
    var MIRROR_CLOCK_SLACK = 300000;  // кламп меток из будущего (увод часов вперёд + NTP-починка): max now+5мин

    // Слияние (чистая функция, входы не мутирует). Инварианты по каждому hash (m=зеркало, known=мета):
    //   метка из будущего → кламп + переписать в зеркале (де-пойзон);
    //   нет локально: known==0 или m.t>known → принять; иначе (мы это знали, локально стёрто) →
    //     УДАЛИТЬ из зеркала — уважаем очистку истории этим origin;
    //   есть локально: реальный апдейт (m.t>SEED_T) новее меты → принять; мета новее зеркала → ремонт
    //     (перезалить local с t=меты — лечит упавший set() и вайп «Мигрировать»); m.t==known → no-op;
    //     меты нет и в зеркале сид → NO-OP: до-фичевый рассинхрон origin'ов не разрушаем, ждём просмотра;
    //   нет в зеркале → посеять с t=SEED_T, пока зеркало под капом; мусор — пропустить.
    // Мета на выходе содержит только живые ключи (гигиена).
    function mirrorMerge(local, mirror, meta, now, cap) {
        var outLocal = {}, outMirror = {}, outMeta = {}, changedLocal = false, changedMirror = false;
        var h, m, mt, known, room = 0, cutoff = now + MIRROR_CLOCK_SLACK;
        cap = cap || MIRROR_CAP;
        local = local || {}; mirror = mirror || {}; meta = meta || {};
        for (h in local) if (hasOwn(local, h)) outLocal[h] = local[h];
        for (h in mirror) if (hasOwn(mirror, h)) outMirror[h] = mirror[h];

        for (h in mirror) {
            if (!hasOwn(mirror, h)) continue;
            m = mirror[h];
            if (!mirrorValidRoad(m) || typeof m.t !== 'number' || !isFinite(m.t)) continue;
            mt = m.t;
            if (mt > cutoff) {                                   // де-пойзон будущих меток
                mt = cutoff;
                outMirror[h] = mirrorRoad(m, mt);
                changedMirror = true;
            }
            known = (typeof meta[h] === 'number' && isFinite(meta[h])) ? Math.min(meta[h], cutoff) : 0;
            if (!hasOwn(outLocal, h)) {
                if (known === 0 || mt > known) {                 // новое для origin / свежее принятого
                    outLocal[h] = mirrorRoad(m);
                    outMeta[h] = mt;
                    changedLocal = true;
                } else {                                          // знали (mt<=known), локально стёрто → удаление
                    delete outMirror[h];
                    changedMirror = true;
                }
            } else if (mt > known && mt > MIRROR_SEED_T) {       // реальный апдейт новее меты → принять
                outLocal[h] = mirrorRoad(m);
                outMeta[h] = mt;
                changedLocal = true;
            } else if (known > mt && mirrorValidRoad(outLocal[h])) {   // зеркало отстало → ремонт из local
                outMirror[h] = mirrorRoad(outLocal[h], known);
                outMeta[h] = known;
                changedMirror = true;
            } else if (known > 0) {
                outMeta[h] = known;                              // steady state (mt === known)
            }
            // known==0 && mt<=SEED_T && локально есть → no-op (см. инварианты выше)
        }
        for (h in outMirror) if (hasOwn(outMirror, h)) room++;
        for (h in local) {
            if (!hasOwn(local, h)) continue;
            if (!hasOwn(outMirror, h) && mirrorValidRoad(local[h]) && room < cap) {
                outMirror[h] = mirrorRoad(local[h], MIRROR_SEED_T);
                outMeta[h] = MIRROR_SEED_T;
                changedMirror = true;
                room++;
            }
        }
        return { local: outLocal, mirror: outMirror, meta: outMeta, changedLocal: changedLocal, changedMirror: changedMirror };
    }

    // кап зеркала: cap самых свежих по t (запись без метки считается самой старой; сиды t=1 вылетают первыми)
    function mirrorPrune(mirror, cap) {
        cap = cap || MIRROR_CAP;
        var keys = [], out = {}, h, i;
        for (h in mirror) if (hasOwn(mirror, h)) keys.push(h);
        if (keys.length <= cap) return mirror;
        keys.sort(function (a, b) {
            return (Number(mirror[b] && mirror[b].t) || 0) - (Number(mirror[a] && mirror[a].t) || 0);
        });
        for (i = 0; i < cap; i++) out[keys[i]] = mirror[keys[i]];
        return out;
    }

    function mirrorHasKV() {
        var a = window.AndroidJS;
        return !!(a && typeof a.get === 'function' && typeof a.set === 'function');
    }

    function mirrorRead() {
        try { return mirrorParse(window.AndroidJS.get('qdl_file_view')); }   // Android → null, Apple → '' — parse покрывает оба
        catch (e) { return {}; }
    }

    // false = записать не удалось (Android set() бросает после одноразового dump() потока «Мигрировать») —
    // мета к этому моменту уже впереди зеркала, на следующем старте ветка «ремонт» в mirrorMerge дольёт
    function mirrorWrite(m) {
        try { window.AndroidJS.set('qdl_file_view', JSON.stringify(mirrorPrune(m, MIRROR_CAP))); return true; }
        catch (e) { return false; }
    }

    var mirrorCache = null, mirrorTimer = null;

    // Timeline.listener 'update' — прилетает и от нативного VLC (натив зовёт Lampa.Timeline.update
    // при закрытии/конце плеера). Дебаунс коалесцирует пачки (авто-next, отметки серий).
    function onTimelineUpdate(e) {
        try {
            var d = e && e.data;
            if (!mirrorCache || !d || !d.hash || !mirrorValidRoad(d.road)) return;
            var now = Date.now();
            mirrorCache[d.hash] = mirrorRoad(d.road, now);
            var meta = Lampa.Storage.get('qdl_tl_meta', {}) || {};   // мету — СРАЗУ, до записи зеркала:
            meta[d.hash] = now;                                      // упавший set() починится ремонтом на старте
            Lampa.Storage.set('qdl_tl_meta', meta);
            if (mirrorTimer) clearTimeout(mirrorTimer);
            mirrorTimer = setTimeout(function () { mirrorTimer = null; mirrorWrite(mirrorCache); }, 400);
        } catch (e2) {}
    }

    // Собственно запуск зеркала: слияние при старте + подписка на Timeline 'update'.
    // Вызывается ТОЛЬКО после решения о режиме (initTimelineMirror) — нельзя сначала
    // смёржить, а потом «передумать»: слияние двигает мету.
    function mirrorStart() {
        var local = Lampa.Storage.get('file_view', {}) || {};
        var meta = Lampa.Storage.get('qdl_tl_meta', {}) || {};
        var r = mirrorMerge(local, mirrorRead(), meta, Date.now());

        // Применять слияние локально ТОЛЬКО вместе с Timeline.read() в том же тике: иначе in-memory
        // кэш Timeline при следующем update() перезапишет file_view своим старым снимком (клоббер).
        // Без read (upstream убрал API) — деградация: мету не двигаем, принятие отложится до его появления.
        var canApply = !!(Lampa.Timeline && typeof Lampa.Timeline.read === 'function');
        if (r.changedLocal && canApply) {
            Lampa.Storage.set('file_view', r.local);
            Lampa.Timeline.read();
        }
        Lampa.Storage.set('qdl_tl_meta', canApply ? r.meta : meta);
        mirrorCache = r.mirror;
        if (r.changedMirror) mirrorWrite(mirrorCache);

        try {
            if (Lampa.Timeline && Lampa.Timeline.listener && typeof Lampa.Timeline.listener.follow === 'function')
                Lampa.Timeline.listener.follow('update', onTimelineUpdate);
        } catch (e) {}
    }

    // qdl 2.18: истина о прогрессе переехала на сервер (Modules/Sync/TimeCode, per-device uid —
    // сеятель в lampainit-invc.js). Зеркало qdl_file_view остаётся аварийным фолбэком.
    // Режимы qdl_tl_mirror: 'auto' (деф.) — ждём до 15 с появления серверного таймкод-плагина
    // (window.lampac_timecode_plugin, грузится через sync.js); появился → зеркало НЕ стартует;
    // таймаут → сервер сломан/выключен → старое зеркало, клиент не деградирует.
    // 'on' — форс-зеркало (аварийный тумблер), 'off' — совсем выключить.
    function initTimelineMirror() {
        if (window.__qdl_mirror_started) return;   // повторный 'app ready' → одна подписка
        window.__qdl_mirror_started = true;
        if (!mirrorHasKV()) return;                // браузер/Tizen: ни зеркала, ни нужды в нём
        var mode = 'auto';
        try { mode = String(Lampa.Storage.get('qdl_tl_mirror', 'auto')); } catch (e) {}
        if (mode === 'off') return;
        if (mode === 'on') { mirrorStart(); return; }
        var waited = 0;
        var t = setInterval(function () {
            if (window.lampac_timecode_plugin) { clearInterval(t); return; }   // сервер главный
            waited += 500;
            if (waited >= 15000) { clearInterval(t); try { mirrorStart(); } catch (e) {} }
        }, 500);
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
    // Скрытый функционал (настройки Lampa, транскод/удаление в карточке, экран «Хелс-чеки»)
    // виден только при куке qdl_unlock=1 — владелец сетит её один раз скриптом в консоли
    // браузера (рецепт: медиасервер claude/04-operations.md). Приложения куку не имеют →
    // скрыто у всех по умолчанию, включая веб. Тот же однострочник продублирован
    // в lampainit-invc.js (CSS-гейт шестерёнки/пункта меню) — общего кода между файлами нет.
    function qdlUnlocked() {
        try { return /(?:^|;\s*)qdl_unlock=1/.test(document.cookie || ''); } catch (e) { return false; }
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

    // ── Язык дорожки (qdl 2.24). По умолчанию русский: сервер отдаёт нормализованный lang2,
    // клиент сам ничего не угадывает. Преф ГЛОБАЛЬНЫЙ, а не на сериал: язык — свойство зрителя,
    // в отличие от озвучки (qdl_audio2), которая осмысленна только внутри конкретной раздачи.
    function audioLang(o) { return (o && o.lang2) ? String(o.lang2) : null; }   // нет поля → null, не падаем
    function getLangPref() { try { return Lampa.Storage.get('qdl_audio_lang', 'ru') || 'ru'; } catch (e) { return 'ru'; } }
    function setLangPref(c) { try { Lampa.Storage.set('qdl_audio_lang', c || 'ru'); } catch (e) {} }

    // языки, реально представленные в дорожках, в порядке появления
    function audioLangs(opts) {
        var seen = {}, res = [];
        (opts || []).forEach(function (o) {
            var c = audioLang(o);
            if (!c || seen[c]) return;
            seen[c] = 1;
            res.push({ code: c, name: o.langName || c });
        });
        return res;
    }

    // ⚠️ НИКОГДА не возвращает пусто: нет дорожек нужного языка → отдаём все.
    // Фича не имеет права спрятать контент — иначе «нет русской» выглядело бы как поломка.
    function filterByLang(opts, code) {
        if (!code || code === 'all') return opts || [];
        var hit = (opts || []).filter(function (o) { return audioLang(o) === code; });
        return hit.length ? hit : (opts || []);
    }

    function langLabel(opts, code) {
        if (!code || code === 'all') return 'Все языки';
        var langs = audioLangs(opts), hit = null;
        langs.forEach(function (l) { if (l.code === code) hit = l; });
        if (hit) return hit.name;
        // язык выбран, но таких дорожек нет — честно говорим, что показаны все
        return (code === 'ru' ? 'Русский' : code) + ' (нет — показаны все)';
    }

    // определить озвучку (из памяти или спросить один раз), затем cb(audioId)
    function ensureAudio(hash, index, cb) {
        req(API + '/qdl/audio?hash=' + hash + '&index=' + (index >= 0 ? index : -1), function (opts) {
            opts = opts || [];
            // сузили до языка зрителя → одна русская дорожка играется без единого вопроса
            opts = filterByLang(opts, getLangPref());
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

    // f (объект файла с сервера) опционален: с ним ключ таймлайна — стабильный tl (переживает re-grab
    // и замену донор→основная), без него — прежний легаси-ключ hash:имя (pickTimeline для {name}
    // даёт бинарно тот же ключ, что старый epTimelineHash)
    function rawPlay(hash, index, title, audio, fileName, f) {
        var item = { title: title || 'Загрузка', url: streamUrl(hash, index, audio) };
        try { item.timeline = pickTimeline(hash, f || { name: fileName || title }); } catch (e) {}
        Lampa.Player.play(item);
        Lampa.Player.playlist([item]);
    }

    // ───────── Воспроизведение локального файла (оффлайн) ─────────
    function playLocal(hash, index, title, fileName, f) {
        ensureAudio(hash, index, function (audio) { rawPlay(hash, index, title, audio, fileName, f); });
    }

    // Список серий — своя активность (qdl_episodes), а не Select: см. ComponentEpisodes.
    // autoplay=true — сразу играть продолжаемую серию («Продолжить» с карточки): «назад»
    // из плеера при этом возвращает на экран серий, а не на карточку.
    function chooseEpisode(hash, name, autoplay) {
        Lampa.Activity.push({
            url: '', component: 'qdl_episodes', title: 'Серии — ' + (name || ''),
            qdl_hash: hash, qdl_name: name || '', qdl_autoplay: !!autoplay
        });
    }

    function watchByHash(hash, name) {
        fetchEpisodes(hash, function (files) {
            var vids = mergedVideoFiles(files);
            if (vids.length > 1) chooseEpisode(hash, name);
            else playLocal(vids.length ? srcHash(vids[0], hash) : hash, vids.length ? vids[0].index : -1, name, vids.length ? vids[0].name : null, vids.length ? vids[0] : null);
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
        prewarmForCard(item.hash);   // прогрев стартует сразу, не дожидаясь TMDB/CUB-запросов полной карточки
        var m = item.meta || {};
        // ⚠️ id === 0 у сервера означает «TMDB id нет» (так помечены jut-маркеры: у аниме с jut.su
        // его и не может быть). Проверка на truthy отправляла такие карточки в ветку «просто играем»,
        // и вход из «Загрузок» сразу открывал плеер вместо экрана — с позиции, сохранённой при
        // онлайн-просмотре, т.е. выглядело как «продолжается онлайн» (жалоба владельца, 2.33)
        if (m.id > 0) {
            Lampa.Activity.push({
                url: '', component: 'full', id: m.id,
                method: m.media_type === 'tv' ? 'tv' : 'movie',
                card: m, source: m.source || 'tmdb',
                qdl_hash: item.hash,   // маркер: открыто из «Загрузок» → addButton оставит одну кнопку «Смотреть»
                qdl_progress: (typeof item.progress === 'number' ? item.progress : 1)   // для гейта недокачанного на карточке
            });
        } else {
            // нет TMDB id (jut.su, безымянные раздачи) → свой экран карточки: постер, описание,
            // прогресс загрузки, «Продолжить»/«Смотреть». Он всегда был написан и зарегистрирован
            // как qdl_card, но его никто не открывал — путь был мёртвым
            Lampa.Activity.push({
                url: '', component: 'qdl_card', qdl: item,
                title: m.title || item.name || 'Загрузка'
            });
        }
    }

    function badge(val, label) {
        return '<span style="display:inline-flex;align-items:center;gap:.35em;background:rgba(255,255,255,.14);padding:.3em .65em;border-radius:.45em;margin:0 .5em .5em 0;font-size:1em"><b>' + esc(val) + '</b><span style="opacity:.55;font-size:.78em">' + esc(label) + '</span></span>';
    }
    function chip(txt) {
        return '<span style="display:inline-block;background:rgba(255,255,255,.1);padding:.3em .65em;border-radius:.45em;margin:0 .5em .5em 0;font-size:1em">' + esc(txt) + '</span>';
    }

    // ───────── Экран серий (замена селектбокса) ─────────
    // Почему активность, а не Lampa.Select: (1) длинные списки скроллятся по канону
    // (Scroll + minus + update на фокусе) и не зависят от upstream-бага высоты селектбокса;
    // (2) «назад» из плеера возвращает СЮДА, а не на карточку сериала; (3) озвучка видна
    // кнопкой и не переспрашивается перед каждым входом (преф qdl_audio2 применяется молча).

    // qdl 2.18: бакет серверных таймкодов для наших экранов. timecode/plugin.js берёт card_id
    // из activity (movie/card) — на qdl_episodes карточки нет, всё падало бы в ведро '0_movie',
    // а ЧТЕНИЕ /timecode/all идёт per card_id. Наш форк-дифф в plugin.js читает
    // window.qdl_timecode_card. Бакет — по seriesKey из f.tl (стабилен через донор-замещение
    // и re-grab, как и сам ключ таймлайна qdltl:*), фолбэк — infohash раздачи.
    var activeEpisodesComp = null;   // текущий экран серий — для перерисовки по 'timecode_updated'

    // ⚠️ Регулярка обязана резать ВСЕ виды epkey, а не только сериал: сервер выдаёт tl и экстрам
    // (jut:<slug>:film1). Список отсортирован, но у тайтла из одних фильмов первым будет film1 —
    // без film|ova|gameova|sp бакет стал бы 'qdl_jut:<slug>:film1', т.е. ВТОРЫМ ведром таймкодов
    // на тот же тайтл, и прогресс разъехался бы молча.
    function tlBucket(vids, hash) {
        try {
            var f = (vids || []).filter(function (v) { return v && v.tl; })[0];
            if (f) return 'qdl_' + String(f.tl).replace(/:(?:s\d+e\d+|film\d*|ova\d*|gameova\d*|sp\d*)$/i, '');
        } catch (e) {}
        return 'qdl_' + hash;
    }

    // Тот же бакет со стороны онлайна: экраны jut.su не знают ни hash раздачи, ни f.tl,
    // но ключ таймлайна у них тот же (qdltl:jut:<slug>:…), значит и ведро обязано совпасть —
    // иначе онлайн и скачанное синкались бы в разные вёдра одного и того же тайтла.
    function jutBucket(slug) { return 'qdl_jut:' + slug; }

    function setTlBucket(bucket) {
        window.qdl_timecode_card = bucket;
        // pull таймкодов бакета с сервера (хук нашего диффа в timecode/plugin.js; без синка — no-op)
        try { Lampa.Listener.send('lampac', { type: 'timecode_pullFromServer' }); } catch (e) {}
    }

    function clearTlBucket(bucket) {
        // снимать только СВОЙ бакет: destroy активности зовётся с задержкой 200 мс — новый экран
        // мог уже выставить свой (Activity.backward → setTimeout(destroy, 200))
        if (window.qdl_timecode_card === bucket) { try { delete window.qdl_timecode_card; } catch (e) { window.qdl_timecode_card = undefined; } }
    }

    function initTimecodeBridge() {
        if (window.__qdl_tc_bridge) return;
        window.__qdl_tc_bridge = true;
        try {
            Lampa.Listener.follow('lampac', function (e) {
                if (e && e.type === 'timecode_updated' && activeEpisodesComp) {
                    try { activeEpisodesComp.refreshMarks(); } catch (e2) {}
                }
            });
        } catch (e) {}
    }
    function epMark(pct) { return pct >= 90 ? '✓ ' : (pct >= 5 ? '► ' + Math.round(pct) + '% · ' : ''); }
    function epMeta(f) {
        return [liveSize(f && f.size), (f && f.source === 'donor') ? 'временная — заменится основной' : '']
            .filter(Boolean).join('   ·   ');
    }

    function ComponentEpisodes(object) {
        var comp = this;
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div></div>');
        var last;
        var rows = [];        // [{el, f}] — отметки обновляются на месте, DOM не перестраивается (фокус жив)
        var audioBtn = null;
        var langBtn = null;
        var hash = object.qdl_hash;
        var name = object.qdl_name || '';

        this.vids = [];
        this.audioOpts = [];
        this.audio = null;
        this.audioReady = false;

        this.tlbucket = null;   // бакет серверных таймкодов этого экрана (qdl 2.18)

        this.create = function () {
            injectCss();
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            fetchEpisodes(hash, function (files) {
                comp.vids = mergedVideoFiles(files);
                comp.tlbucket = tlBucket(comp.vids, hash);
                activeEpisodesComp = comp;
                setTlBucket(comp.tlbucket);   // запись прогресса из плеера и pull с сервера — в этот бакет
                if (!comp.vids.length) { comp.build(); return; }
                req(API + '/qdl/audio?hash=' + srcHash(comp.vids[0], hash) + '&index=' + comp.vids[0].index,
                    function (opts) { comp.audioOpts = opts || []; comp.build(); },
                    function () { comp.build(); });
            }, function () { comp.build(); });
            return this.render();
        };

        // дорожки языка зрителя (или все, если таких нет) — на них работают и автовыбор, и меню озвучки
        function langOpts() { return filterByLang(comp.audioOpts, getLangPref()); }

        // озвучка без вопросов: единственная дорожка, валидный преф или единственная дорожка
        // нужного языка; иначе спросим при первом плее
        function resolveAudioSilent() {
            var opts = comp.audioOpts;
            if (opts.length <= 1) { comp.audio = opts[0] && opts[0].id; comp.audioReady = true; return; }

            // Преф на сериал ВЫИГРЫВАЕТ у языка: человек уже выбрал руками, переучивать его нельзя.
            var pref = getAudioPref(srcHash(comp.vids[0], hash));
            if (pref && opts.some(function (o) { return o.id === pref; })) { comp.audio = pref; comp.audioReady = true; return; }

            // ⚠️ Автовыбор строго при ОДНОЙ подходящей дорожке. Три русские (дубляж/многоголоска/
            // оригинал) — это выбор, который нельзя делать за зрителя молча.
            var cand = langOpts();
            if (cand.length === 1) { comp.audio = cand[0].id; comp.audioReady = true; }
        }

        function audioLabel() {
            var hit = comp.audioOpts.filter(function (o) { return o.id === comp.audio; })[0];
            return comp.audioReady ? ((hit && hit.label) || 'по умолчанию') : 'выбрать';
        }

        function askLang() {
            var cur = getLangPref();
            var items = audioLangs(comp.audioOpts).map(function (l) {
                return { title: (l.code === cur ? '✓ ' : '') + l.name, code: l.code };
            });
            items.push({ title: (cur === 'all' ? '✓ ' : '') + 'Все языки', code: 'all' });
            Lampa.Select.show({
                title: 'Язык дорожки',
                items: items,
                onSelect: function (s) {
                    setLangPref(s.code);
                    // язык сменился — прежний выбор озвучки мог оказаться из другого языка
                    comp.audio = null; comp.audioReady = false;
                    resolveAudioSilent();
                    if (langBtn) langBtn.text('Язык: ' + langLabel(comp.audioOpts, getLangPref()));
                    if (audioBtn) audioBtn.text('Озвучка: ' + audioLabel());
                    Lampa.Controller.toggle('content');
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }

        function askAudio(cb) {
            var ahash = srcHash(comp.vids[0], hash);
            var pref = getAudioPref(ahash);
            // меню — только по дорожкам выбранного языка (сужение, о котором просил владелец)
            var ordered = langOpts().slice().sort(function (a, b) { return (b.id === pref ? 1 : 0) - (a.id === pref ? 1 : 0); });
            Lampa.Select.show({
                title: 'Озвучка',
                items: ordered.map(function (o) { return { title: (o.id === pref ? '✓ ' : '') + o.label, id: o.id }; }),
                onSelect: function (s) {
                    setAudioPref(ahash, s.id);
                    comp.audio = s.id; comp.audioReady = true;
                    if (audioBtn) audioBtn.text('Озвучка: ' + audioLabel());
                    if (cb) cb(s.id); else Lampa.Controller.toggle('content');
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }

        this.play = function (i) {
            var vids = comp.vids, f = vids[i];
            if (!f) return;
            var go = function (audio) {
                // озвучка выбрана для основной раздачи (vids[0]) — донорским файлам buildPlaylist даст дефолт
                var playlist = buildPlaylist(hash, vids, audio, srcHash(vids[0], hash));
                Lampa.Player.play(playlist[i]);      // сам элемент плейлиста: его timeline пишет прогресс
                Lampa.Player.playlist(playlist);
                warmupNext(hash, vids, f);           // следующая серия — в page cache, пока смотрят эту
            };
            if (comp.audioReady) go(comp.audio); else askAudio(go);
        };

        function rowEl(f, i) {
            var meta = epMeta(f);
            var num = epNumber(f);   // номер серии крупно слева: длинные имена файлов обрезаются (репорт с iPhone)
            var el = $(
                '<div class="selector qdl-row-focus" style="display:flex;align-items:center;gap:1.2em;padding:.9em 1.2em;margin:.35em 1.4em;background:rgba(255,255,255,.06);border-radius:.8em">' +
                  '<div class="qdl-ep-num" style="flex:none;min-width:1.7em;text-align:center;font-size:1.8em;font-weight:700;opacity:.9">' + (num !== null ? num : i + 1) + '</div>' +
                  '<div style="flex:1;min-width:0">' +
                    '<div style="font-size:1.5em;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"><span class="qdl-ep-mark"></span><span class="qdl-ep-name"></span></div>' +
                    (meta ? '<div style="opacity:.65;font-size:1.15em;margin-top:.25em">' + esc(meta) + '</div>' : '') +
                  '</div>' +
                  '<div class="qdl-ep-play" style="opacity:.45;padding-right:.3em">' + WATCH_ICON + '</div>' +
                '</div>'
            );
            el.find('.qdl-ep-name').text(baseName(f.name) + (f.source === 'donor' ? ' · врем.' : ''));
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:enter', function () { comp.play(i); });
            rows.push({ el: el, f: f });
            return el;
        }

        this.build = function () {
            var vids = comp.vids;
            rows = [];
            resolveAudioSilent();

            body.append($('<div style="padding:1.2em 1.6em .4em"><div class="qdl-ep-title" style="font-size:2em;font-weight:700"></div>' +
                (vids.length ? '<div style="opacity:.6;font-size:1.25em;margin-top:.25em">' + vids.length + ' ' + livePlural(vids.length, 'серия', 'серии', 'серий') + '</div>' : '') +
                '</div>'));
            body.find('.qdl-ep-title').text(name || 'Серии');

            if (!vids.length) body.append(liveMsg('Видеофайлы не найдены'));
            else {
                // Кнопки в одном ряду. Классы РАЗНЫЕ: общий .qdl-btn-focus остаётся ради стиля фокуса,
                // но селектить их надо точечно (иначе .text() на коллекции склеит подписи).
                var btnCss = 'display:inline-block;margin:.2em 0 .6em .0;padding:.7em 1.2em;background:rgba(255,255,255,.1);border-radius:.8em;font-size:1.3em';
                var bar = $('<div style="padding:0 1.4em;display:flex;gap:.8em;flex-wrap:wrap"></div>');

                // язык показываем только когда он реально ЕСТЬ из чего выбрать — иначе лишний шум
                if (audioLangs(comp.audioOpts).length > 1) {
                    langBtn = $('<div class="selector qdl-btn-focus qdl-lang-btn" style="' + btnCss + '"></div>');
                    langBtn.text('Язык: ' + langLabel(comp.audioOpts, getLangPref()));
                    langBtn.on('hover:focus', function () { last = langBtn[0]; scroll.update(langBtn, true); });
                    langBtn.on('hover:enter', function () { askLang(); });
                    bar.append(langBtn);
                }
                if (comp.audioOpts.length > 1) {
                    audioBtn = $('<div class="selector qdl-btn-focus qdl-audio-btn" style="' + btnCss + '"></div>');
                    audioBtn.text('Озвучка: ' + audioLabel());
                    audioBtn.on('hover:focus', function () { last = audioBtn[0]; scroll.update(audioBtn, true); });
                    audioBtn.on('hover:enter', function () { askAudio(null); });
                    bar.append(audioBtn);
                }
                if (bar.children().length) body.append(bar);

                vids.forEach(function (f, i) { body.append(rowEl(f, i)); });
                comp.refreshMarks();   // отметки + подсветка текущей + стартовый фокус на ней
            }

            this.activity.loader(false);
            this.activity.toggle();

            if (object.qdl_autoplay && vids.length) {
                object.qdl_autoplay = false;   // одноразово: возврат из плеера не перезапускает плей
                var vw = function (f) { return pickTimeline(hash, f); };
                // 🔥 Было `comp.play(cur ? vids.indexOf(cur) : 0)`: когда продолжать нечего,
                // «Продолжить» МОЛЧА запускала первый файл списка — ровно симптом жалобы
                // 14.08.2026. Нечего продолжать → первая НЕпросмотренная (по номеру, не по
                // индексу); досмотрено всё → начинаем сначала, но вслух.
                var cur = chooseContinue(vids, vw) || firstUnwatched(vids, vw);
                if (!cur) {
                    cur = sortEpisodes(vids)[0];
                    try { Lampa.Noty.show('Всё просмотрено — включаю с начала'); } catch (e) {}
                }
                comp.play(vids.indexOf(cur));
            }
        };

        // свежие ✓/►N% и подсветка «текущей» — зовётся из start() при каждом возврате (в т.ч. из плеера)
        this.refreshMarks = function () {
            if (!rows.length) return;
            var view = function (f) { return pickTimeline(hash, f); };
            var cur = chooseContinue(comp.vids, view);
            rows.forEach(function (r) {
                r.el.find('.qdl-ep-mark').text(epMark((view(r.f) || {}).percent || 0));
                r.el.toggleClass('qdl-ep--cur', !!cur && r.f === cur);
            });
            if (cur && !last) {   // стартовый фокус — на продолжаемой серии; выбор пользователя не перебиваем
                var hit = rows.filter(function (r) { return r.f === cur; })[0];
                if (hit) last = hit.el[0];
            }
        };

        this.render = function () { return html; };
        this.start = function () {
            // возврат на экран (в т.ч. из плеера/другой активности) — восстановить бакет таймкодов
            if (comp.tlbucket) { activeEpisodesComp = comp; setTlBucket(comp.tlbucket); }
            comp.refreshMarks();
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
        this.destroy = function () {
            if (activeEpisodesComp === comp) activeEpisodesComp = null;
            if (comp.tlbucket) clearTlBucket(comp.tlbucket);
            scroll.destroy(); html.remove();
        };
    }

    function ComponentCard(object) {
        var comp = this;
        var item = object.qdl || {};
        var scroll = new Lampa.Scroll({ mask: true, over: true });
        var html = $('<div></div>');

        this.create = function () {
            scroll.minus();   // без этого на ТВ у .scroll нет высоты — контент не скроллится
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
                      // 2.34: кнопки ВЫШЕ описания. Пока экран был мёртвым, этого никто не видел;
                      // как только он открылся (2.33), «Смотреть» оказалась под простынёй текста —
                      // на ТВ до неё приходилось доскроливать (жалоба владельца)
                      '<div class="qdl-card-btns" style="display:flex;flex-wrap:wrap;gap:.7em;align-items:center;margin-bottom:1.6em">' +
                        '<div class="qdl-watch selector" style="display:inline-flex;align-items:center;gap:.55em;padding:.75em 2em;border-radius:.6em;font-size:1.4em">' + WATCH_ICON + '<span>Смотреть</span></div>' +
                      '</div>' +
                      '<div class="qdl-card-descr" style="font-size:1.2em;line-height:1.55;opacity:.92;max-width:46em">' + esc(m.overview || 'Нет описания.') + '</div>' +
                    '</div>' +
                  '</div>' +
                '</div>'
            );
            body.find('.qdl-poster').on('error', function () { this.src = './img/img_broken.svg'; });
            body.find('.qdl-watch').on('hover:enter', function () { watch(item); });
            body.find('.qdl-watch').on('hover:focus', function (e) { scroll.update($(e.target), true); });

            scroll.append(body);
            html.append(scroll.render());

            // если метаданных нет — дотянем и перерисуем карточку (с защитой от replace после ухода)
            var self = this;

            // «Продолжить · S1 · Серия N» — та же логика, что на полной карточке (chooseContinue).
            // Приезжает async: без collectionAppend кнопка была бы недостижима пультом (см. 06 §BE).
            // 🔥 Пересчёт живёт отдельным методом и зовётся ещё и из start(): экран создаётся ОДИН
            // раз, а возврат из плеера/списка серий его только показывает. Без пересчёта подпись
            // застывала на серии, с которой начинали (жалоба 14.08.2026 — «ведёт на первую»).
            comp.refreshContinue = function () {
                fetchEpisodes(item.hash, function (files) {
                    if (comp.destroyed) return;
                    var bar = body.find('.qdl-card-btns');
                    if (!bar.length) return;
                    var btn = bar.find('.qdl-continue');
                    var vids = mergedVideoFiles(files);
                    // фильм/одна серия — продолжать нечего, хватит «Смотреть»
                    var target = vids.length < 2 ? null
                        : chooseContinue(vids, function (f) { return pickTimeline(item.hash, f); });
                    // Всё досмотрели → кнопка обязана уйти. Узел удаляем без починки навигатора:
                    // toggle() этого экрана пересобирает коллекцию через collectionSet.
                    if (!target) { if (btn.length) btn.remove(); return; }
                    // Кнопка уже есть → правим ТОЛЬКО подпись: тот же DOM-узел = живой фокус пульта
                    if (btn.length) { btn.children('span').text('Продолжить · ' + epShort(target.name)); return; }
                    var b = $('<div class="qdl-continue selector" style="display:inline-flex;align-items:center;gap:.55em;padding:.75em 2em;border-radius:.6em;font-size:1.4em">' + CONTINUE_ICON + '<span></span></div>');
                    b.children('span').text('Продолжить · ' + epShort(target.name));
                    b.on('hover:enter', function () {
                        confirmPartial(item, function () { chooseEpisode(item.hash, (m.title || item.name), true); });
                    });
                    b.on('hover:focus', function (e) { scroll.update($(e.target), true); });
                    bar.prepend(b);
                    navAppend(bar, b);
                });
            };
            comp.refreshContinue();
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
            // ДО Controller.add: toggle() пересобирает коллекцию навигатора из DOM, поэтому
            // добавленная/удалённая здесь кнопка попадает в неё сама — navAppend не нужен
            if (comp.refreshContinue) comp.refreshContinue();
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
        // destroyed — не косметика: на него смотрят три async-колбэка (кнопка «Продолжить»,
        // enrich→saveMeta→Activity.replace). Флаг никогда не выставлялся, и гарды были мертвы.
        this.destroy = function () { comp.destroyed = true; scroll.destroy(); html.remove(); };
    }

    // ───────── Грид «Загрузки» (вертикальные карточки-постеры) ─────────
    function ComponentDownloads(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        // mapping--grid + cols--6 — ровно то, чем штатный каталог Lampa раскладывает карточки
        // (ItemsLine добавляет их из params.items.mapping/cols). Без них .card остаётся при своей
        // фиксированной width:12.75em, ряд не добирает ширину контейнера и справа зияет пустота
        // (на 1280 — 100 px против 15 px слева). Правила .cols--N > * дают долю 100/N % и несут
        // свои медиазапросы, поэтому узкие экраны раскладываются как у штатных экранов.
        var body = $('<div class="category-full mapping--grid cols--6"></div>');
        var last;
        var builtStamp = -1;   // colStamp на момент build: разошёлся — грид устарел

        this.create = function () {
            injectCss();   // .qdl-col-card: экран, открытый первым после загрузки, иначе без стилей
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
                    // width:100% — иначе .cols--6 > * сожмёт сообщение до ширины одной карточки
                    body.append($('<div style="width:100%;padding:2em;font-size:1.4em;opacity:.7">Коллекции больше нет.</div>'));
                else
                    cg.items.forEach(function (t) { comp.append(t, { collection: cg.col }); });
            } else {
                // главный грид: коллекции и фильмы вперемешку, по актуальности последней загрузки (новое сверху)
                if (!g.cols.length && !g.singles.length)
                    body.append($('<div style="width:100%;padding:2em;font-size:1.4em;opacity:.7">В «Загрузках» пока пусто. Нажми «Скачать» на карточке фильма.</div>'));
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

    // Виды уведомлений, означающие «файл лёг на диск» (JutNotifyDone и торрентный сканер серий).
    // 🔥 БЕЛЫЙ список, а не «всё остальное»: раньше «скачана» была ветвью по остатку, и туда
    // проваливались и «вышла новая серия» (kind=null), и SEASON, и DIAG — пользователь читал
    // «скачана» про серию, которой на диске нет. Любой НОВЫЙ серверный вид теперь попадает
    // в нейтральную корзину, а не врёт про скачивание.
    var NOTI_DONE_KINDS = { OVA: 1, ONA: 1, OAD: 1, SP: 1, SPECIAL: 1, FILM: 1, GAMEOVA: 1, 'GAME-OVA': 1, RANGE: 1 };

    function notiBucket(n) {
        var k = (n && n.kind) ? String(n.kind).toUpperCase() : '';
        if (!k || NOTI_DONE_KINDS[k]) return 'done';       // серия лежит на диске
        if (k === 'NEW') return 'new';                     // вышла на сайте (слежение jut.su)
        // TITLE — «пачка тайтла скачана» (qdl 2.38). Своя корзина, а не 'done': текст уже готовый
        // («Скачано серий: 60»), и приписывать к нему «скачана» значило бы говорить про серию.
        if (k === 'TITLE') return 'title';
        if (k === 'SEASON') return 'season';
        if (k === 'START') return 'started';
        if (k === 'NOSPACE') return 'warn';
        if (k === 'SWITCH' || k === 'INFO') return 'special';
        return 'other';
    }

    // опрос ленты: бейдж непрочитанных + тост для появившихся с прошлого опроса.
    // In-flight флаг обязателен: вызывается из 4 точек (старт, вставка иконки/меню, interval) —
    // без него два параллельных ответа читали один qdl_noti_lastid и дублировали тосты/бейдж
    var notiPollBusy = false;
    function pollNotifications() {
        if (notiPollBusy) return;
        notiPollBusy = true;
        req(API + '/qdl/notifications', function (r) {
            notiPollBusy = false;
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
                // SWITCH (предложение сменить раздачу) / INFO — это НЕ «скачанная серия»: свой тост без «скачана»;
                // при пачке событий — один агрегированный тост (детали в центре «Уведомления»), как у скачанных серий.
                // START (qdl 2.19) — «началась загрузка серии»: своя корзина со своим текстом и своей агрегацией,
                // чтобы не слиться со «скачана» (иначе пользователь решит, что серия уже готова к просмотру).
                // NEW (qdl 2.35) — «вышла новая серия» у подписки jut.su; в режиме «только уведомления»
                // файла не будет вовсе. SEASON — вышел новый сезон. NOSPACE — не хватило места.
                var special = fresh.filter(function (x) { return notiBucket(x) === 'special'; });
                var started = fresh.filter(function (x) { return notiBucket(x) === 'started'; });
                var dl = fresh.filter(function (x) { return notiBucket(x) === 'done'; });
                var titles = fresh.filter(function (x) { return notiBucket(x) === 'title'; });
                var came = fresh.filter(function (x) { return notiBucket(x) === 'new'; });
                var season = fresh.filter(function (x) { return notiBucket(x) === 'season'; });
                var warn = fresh.filter(function (x) { return notiBucket(x) === 'warn'; });
                var other = fresh.filter(function (x) { return notiBucket(x) === 'other'; });
                if (special.length === 1) Lampa.Noty.show((special[0].kind === 'SWITCH' ? '🔀 ' : '📺 ') + esc(special[0].title) + ' — ' + esc(special[0].label));
                else if (special.length > 1) Lampa.Noty.show('🔔 Новых уведомлений: ' + special.length);
                if (started.length === 1) Lampa.Noty.show('⏬ ' + esc(started[0].title) + ' — ' + esc(started[0].label) + ' качается');
                else if (started.length > 1) Lampa.Noty.show('⏬ Начата загрузка серий: ' + started.length);
                if (dl.length === 1) Lampa.Noty.show('📺 ' + esc(dl[0].title) + ' — ' + esc(dl[0].label) + ' скачана');
                else if (dl.length > 1) Lampa.Noty.show('📺 Скачано новых серий: ' + dl.length);
                // label уже несёт «Скачано серий: N» — дописывать нечего
                if (titles.length === 1) Lampa.Noty.show('📦 ' + esc(titles[0].title) + ' — ' + esc(titles[0].label));
                else if (titles.length > 1) Lampa.Noty.show('📦 Скачано тайтлов: ' + titles.length);
                if (came.length === 1) Lampa.Noty.show('🆕 ' + esc(came[0].title) + ' — ' + esc(came[0].label));
                else if (came.length > 1) Lampa.Noty.show('🆕 Вышли новые серии: ' + came.length);
                if (season.length === 1) Lampa.Noty.show('🗓 ' + esc(season[0].title) + ' — ' + esc(season[0].label));
                else if (season.length > 1) Lampa.Noty.show('🗓 Новых сезонов: ' + season.length);
                if (warn.length === 1) Lampa.Noty.show('⚠️ ' + esc(warn[0].title) + ' — ' + esc(warn[0].label));
                else if (warn.length > 1) Lampa.Noty.show('⚠️ Новые серии не скачаны: ' + warn.length);
                if (other.length === 1) Lampa.Noty.show('🔔 ' + esc(other[0].title) + ' — ' + esc(other[0].label));
                else if (other.length > 1) Lampa.Noty.show('🔔 Новых уведомлений: ' + other.length);
            }
        }, function () { notiPollBusy = false; });
    }

    // Мгновенный пуш уведомлений (qdl 2.19). WS-клиент синка (Modules/Sync, sync_v2/invc-ws.js)
    // ретранслирует серверные события DOM-событием 'lwsEvent' с detail = {uid, name, data};
    // сервер шлёт name='qdl_noti', и мы просто дёргаем обычный опрос — единый источник правды
    // (бейдж + тосты + qdl_noti_lastid) остаётся один, дубли невозможны: pollNotifications
    // защищён in-flight флагом notiPollBusy. 90-секундный setInterval в start() ОСТАВЛЕН фолбэком:
    // сокет может быть не поднят (Sync-плагин не загружен / старая сборка) или оборваться.
    // Регистрируем на уровне модуля, а не в start(): подписка ничего не стоит и не зависит от того,
    // долетело ли событие app 'ready'. Если document без addEventListener (старый WebView) — try/catch.
    try {
        document.addEventListener('lwsEvent', function (e) {
            try { if (e && e.detail && e.detail.name === 'qdl_noti') pollNotifications(); } catch (err) {}
        });
    } catch (e) {}

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
                        if (a.ok) {
                            dropEpCache();   // раздача заменяется целиком (новый hash) — сбросить весь кеш серий
                            Lampa.Noty.show(r && r.success ? '✓ Переключено — сезон перекачивается' : 'Не вышло: ' + ((r && r.error) || 'ошибка'));
                        }
                        else Lampa.Noty.show('Оставили текущую раздачу');
                    }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
                },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
            return;
        }
        // jut-уведомление: сервер отдаёт slug (только для своих ключей). В режиме «только
        // уведомления» карточки в «Загрузках» НЕТ вовсе — открываем экран тайтла (там
        // «Смотреть» онлайн и «Скачать»), а не плеер по мёртвому /qdl/stream-URL.
        var fallback = function () {
            if (n.slug) openJutTitle(n.slug, n.title);
            else if (n.hash) watchByHash(n.hash, n.title);
            else Lampa.Noty.show('Загрузка не найдена');
        };
        req(API + '/qdl/list', function (list) {
            var it = (list || []).filter(function (x) { return x.hash === n.hash; })[0];
            if (it) openDownload(it);   // скачанное открываем как раньше — оффлайн-серии там
            else fallback();
        }, fallback);
    }

    function openJutTitle(slug, title) {
        Lampa.Activity.push({ url: '', title: title || 'jut.su', component: 'jut_title', jut_slug: slug });
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
            injectCss();   // фокус-стили строк: не полагаемся на то, что другой экран уже инъецировал
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            network.silent(API + '/qdl/notifications', function (r) { comp.build((r && r.items) || []); }, function () { comp.build([]); });
            return this.render();
        };

        this.build = function (items) {
            if (!items.length)
                body.append($('<div style="padding:2em;font-size:1.4em;opacity:.7">Пока нет уведомлений. «🔔 Следить» в карточке тайтла jut.su — буду сообщать о новых сериях; то же в «Загрузках» — буду ещё и качать их.</div>'));

            items.forEach(function (n) { comp.append(n); });

            // открыли центр → помечаем всё прочитанным, бейдж гаснет
            req(API + '/qdl/notifications/read', function () { updateNotiBadge(0); });

            this.activity.loader(false);
            this.activity.toggle();
        };

        this.append = function (n) {
            var poster = notiPoster(n);
            var el = $(
                '<div class="qdl-noti-row selector qdl-row-focus" style="display:flex;align-items:center;gap:1em;padding:1em;margin:.35em .6em;background:rgba(255,255,255,.05);border-radius:.7em">' +
                  '<img src="' + poster + '" style="width:3.6em;height:5.4em;object-fit:cover;border-radius:.4em;background:#222;flex:none">' +
                  '<div style="flex:1;min-width:0">' +
                    '<div style="font-size:1.3em;font-weight:600">' + esc(n.title || 'Сериал') + '</div>' +
                    '<div style="opacity:.85;font-size:1.15em;margin-top:.25em">' + esc(n.label || '') + '</div>' +
                    '<div style="opacity:.5;font-size:.95em;margin-top:.25em">' + esc(relTime(n.created)) + '</div>' +
                  '</div>' +
                '</div>'
            );
            el.find('img').on('error', function () { this.src = PX1; });   // нейтральная плитка, не рваная заглушка
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

    // «актуальность» карточки: activity сервера (новая серия от охоты/докачка двигают её вверх),
    // фолбэк — added; мусор/отрицательное не протекает в сортировку
    function itemActivity(t) {
        var a = +(t && t.activity);
        if (!isFinite(a) || a <= 0) a = +(t && t.added) || 0;
        return a;
    }

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
            items.forEach(function (t) { var a = itemActivity(t); if (a > added) added = a; });
            cols.push({ col: col, items: items, cover: cover, added: added });
        });
        var singles = list.filter(function (t) { return t && t.hash && !inCol[t.hash]; });
        return { cols: cols, singles: singles };
    }

    // коллекции и одиночки вперемешку, по актуальности последней загрузки desc (дата коллекции =
    // самая свежая активность её серий: новая серия/докачка поднимает коллекцию наверх);
    // тай-брейк — прежний порядок (коллекции, затем одиночки): не полагаемся на стабильность sort старых TV
    function gridOrder(g) {
        var entries = [];
        g.cols.forEach(function (c) { entries.push({ col: c, added: +c.added || 0, idx: entries.length }); });
        g.singles.forEach(function (t) { entries.push({ item: t, added: itemActivity(t), idx: entries.length }); });
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
                dropEpCache(t.hash);   // имена файлов сменятся (mkv→mp4) — кеш списка серий устареет
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
        if (canTranscode(t) && qdlUnlocked()) items.push({ title: '🎬 Транскодировать в MP4 (для браузера)', act: 'mp4' });
        // Слежение: у торрентов — по infohash, у jut.su — по slug в своём контуре.
        // ⚠️ Торрентная ветка гейтится по !local (ей нужен живой торрент и links/<hash>.json),
        // а jut-карточка ВСЕГДА local — из-за этого пункт был не виден вообще (жалоба владельца).
        // ⚠️ У jut.su «Следить» ЗДЕСЬ означает «качать новые серии» (решение владельца):
        // карточка тайтла включает только уведомления, «Загрузки» — скачивание.
        if (isJut(t)) {
            var jm = jutMode(t);
            items.push({
                title: jm === 'grab' ? '🔔 Не следить за новыми сериями'
                     : jm === 'notify' ? '🔔 Слежу: только уведомления…'
                     : '🔔 Следить: качать новые серии',
                act: 'jutwatch'
            });
        }
        else if (!t.local && t.state !== 'local')
            items.push({ title: t.watched ? '🔔 Не следить за новыми сериями' : '🔔 Следить за новыми сериями', act: 'watch' });
        // в под-гриде коллекции — «Убрать», в общем гриде/карточке — «Добавить»
        if (ctx && ctx.collection) items.push({ title: '📁 Убрать из коллекции', act: 'uncol' });
        else items.push({ title: '📁 Добавить в коллекцию', act: 'addcol' });
        if (qdlUnlocked()) items.push({ title: '🗑 Удалить (с файлами)', act: 'del' });

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
                else if (b.act === 'jutwatch') {
                    // «Загрузки» — ЕДИНСТВЕННАЯ точка, где включается автоскачивание.
                    // Понижение до «только уведомления» живёт на экране карточки тайтла.
                    var jslug = t.jut.slug;
                    var applyJm = function (now) {
                        t.jut.watch = now;
                        t.watched = now !== 'off';   // отметка в гриде — поле, общее с торрентами
                    };
                    var jmNow = jutMode(t);
                    if (jmNow === 'off') jutWatchSet(jslug, 'off', 'grab', applyJm);
                    else if (jmNow === 'grab') jutWatchSet(jslug, 'grab', 'off', applyJm);
                    else Lampa.Select.show({
                        // подписка сделана с карточки тайтла (только уведомления) — из «Загрузок»
                        // её можно поднять до скачивания или снять совсем
                        title: 'Новые серии — jut.su',
                        items: [
                            { title: '⬇ Качать новые серии', subtitle: 'сейчас только уведомления', want: 'grab' },
                            { title: '🔕 Не следить', want: 'off' },
                            { title: 'Отмена' }
                        ],
                        onSelect: function (a) {
                            if (a.want) jutWatchSet(jslug, 'notify', a.want, applyJm);
                            else Lampa.Controller.toggle('content');
                        },
                        onBack: function () { Lampa.Controller.toggle('content'); }
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
                                dropEpCache(t.hash);     // и кеш списка серий
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
            // TMDB id — для локального индекса bitmagnet: поиск по id, а не по названию,
            // поэтому чужой фильм в выдачу попасть не может
            + (movie.id ? '&tmdb_id=' + encodeURIComponent(movie.id) : '')
            + (apikey ? '&apikey=' + encodeURIComponent(apikey) : '');

        req(url, function (list) {
            if (!list || !list.length) { Lampa.Noty.show('Раздачи не найдены'); return; }

            // qdl 2.45: сервер отдал снимок из кеша (старше 6 ч) и обновляет его в фоне — говорим
            // об этом честно, потому что сиды в снимке могли устареть. Пометка приходит полем на
            // самих элементах: /qdl/search обязан оставаться массивом (клиенты со старым
            // закешированным qdl.js делают list.length и list.slice сразу по ответу).
            var stale = !!(list[0] && list[0].stale);

            Lampa.Select.show({
                title: 'Выбери раздачу для загрузки на диск' + (stale ? ' (из кеша, обновляю)' : ''),
                items: list.slice(0, 60).map(function (t) {
                    return {
                        title: (t.rec ? '⭐ ' : '') + t.title,
                        subtitle: torrentSubtitle(t, isSerial),
                        t: t
                    };
                }),
                onSelect: function (a) {
                    Lampa.Controller.toggle('content');
                    if (qdlUnlocked() && (a.t.codec === 'hevc' || a.t.codec === 'av1'))
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

    // ───────── Порядок кнопок полной карточки (2.30) ─────────
    // [Продолжить][Смотреть][Скачать] … родные (закладки/«…», в исходном отн. порядке) … [priority][Онлайн].
    // Вставки идут вразнобой (complite синхронно, /qdl/list и /qdl/episodes асинхронно), а Lampa на каждом
    // входе в контроллер full_start может prepend'ить клон .button--priority (onGroupButtons) — поэтому
    // порядок держит идемпотентная сортировка + observer, а не место вставки.
    // Флаг лишь метит собственные мутации; от зацикливания реально защищает ранний выход
    // «порядок уже верен → ни одной мутации» (колбэк observer — микротаск, флаг к тому
    // моменту всегда сброшен, так что полагаться на него нельзя).
    var qdlOrdering = false;

    function buttonRank(el) {
        var c = el.classList;
        if (!c) return 3;
        if (c.contains('qdl-continue-btn')) return 0;
        if (c.contains('qdl-watch-btn')) return 1;
        if (c.contains('qdl-download')) return 2;
        if (c.contains('button--play')) return 5;      // «Онлайн» — всегда последняя (просьба владельца)
        if (c.contains('button--priority')) return 4;  // пин-клон источника — рядом с «Онлайн»
        return 3;
    }

    function orderButtons(cont) {
        try {
            if (!cont || !cont.length) return;
            var box = cont[0];
            var kids = [].filter.call(box.children, function (n) {
                return n.classList && n.classList.contains('full-start__button');
            });
            if (kids.length < 2) return;
            // составной ключ rank*100+index: стабильность сортировки не зависит от движка (старые ТВ)
            var sorted = kids.map(function (n, i) { return { n: n, k: buttonRank(n) * 100 + i }; })
                .sort(function (a, b) { return a.k - b.k; })
                .map(function (x) { return x.n; });
            var same = true;
            for (var i = 0; i < kids.length; i++) if (kids[i] !== sorted[i]) { same = false; break; }
            if (!same) {   // порядок верен → НИ ОДНОЙ мутации, иначе observer зациклится
                qdlOrdering = true;
                for (var j = 0; j < sorted.length; j++) box.appendChild(sorted[j]);   // appendChild ПЕРЕМЕЩАЕТ узел, слушатели живы
                qdlOrdering = false;
            }
            // фокус правим и без перемещений: кнопка могла приехать сразу в нужное место
            // (prepend «Смотреть» в ряд, где из наших только «Скачать»), а фокус остался на ней
            fixFocus(box);
        } catch (e) { qdlOrdering = false; }
    }

    // 🔥 Коллекция SpatialNavigator СТАТИЧНА: Navigator.focus(el) возвращает false, если элемента
    // в ней нет, а move() его не видит. Наши кнопки приезжают на ответ /qdl/list и /qdl/episodes —
    // то есть ПОСЛЕ collectionSet (он один раз на входе в контроллер full_start), поэтому пультом
    // до «Смотреть»/«Продолжить» было не дойти до следующего входа в карточку. collectionAppend —
    // штатный механизм добора (им же цепляются доскроленные карточки каталога).
    function navAppend(cont, btn) {
        try {
            var act = document.querySelector('.activity--active');
            if (act && !act.contains(cont[0])) return;   // карточку уже закрыли — коллекцию не пачкаем
            Lampa.Controller.collectionAppend(btn);
        } catch (e) {}
    }

    // Пока пользователь не нажал ни одной клавиши, фокус наш: слушатели глобальные и вешаются один раз
    var focusFree = false;
    function inputSeen() { focusFree = false; }
    function armFocus() {
        if (!armFocus.bound) {
            armFocus.bound = 1;
            var evs = ['keydown', 'mousedown', 'touchstart', 'wheel'];
            for (var i = 0; i < evs.length; i++)
                try { window.addEventListener(evs[i], inputSeen, true); } catch (e) {}
        }
        focusFree = true;
    }

    // Фокус — на главном действии. Lampa фокусирует первый .selector в момент activity.toggle(),
    // когда в ряду есть только «Скачать»; «Смотреть»/«Продолжить» приезжают асинхронно и встают
    // перед ней — переводим фокус на новую первую кнопку (жалоба владельца: на ТВ фокус садился
    // на «Скачать»). Отдельный случай — артефакт пина источника: onGroupButtons СИНХРОННО делает
    // prepend клона .button--priority и фокусирует его, а мы увозим клон в конец ряда; это не выбор
    // пользователя, поэтому лечится даже после нажатий.
    function fixFocus(box) {
        try {
            var first = box.children[0];
            var foc = box.querySelector('.full-start__button.focus');
            if (!first || !foc || foc === first) return;
            if (!focusFree && !foc.classList.contains('button--priority')) return;   // кнопку выбрали руками
            if (buttonRank(first) >= buttonRank(foc)) return;   // новая первая не «важнее» — не трогаем
            Lampa.Controller.collectionFocus(first, box);
        } catch (e) {}
    }

    function ensureOrderObserver(cont) {
        try {
            if (!cont.length || cont.data('qdl-order-obs')) return;   // маркер пер-контейнерный: back-навигация
            cont.data('qdl-order-obs', 1);                            // возвращает СТАРУЮ активность без 'complite'
            new MutationObserver(function () {
                if (qdlOrdering) return;
                orderButtons(cont);
            }).observe(cont[0], { childList: true });
        } catch (e) {}   // нет MutationObserver (старый Tizen) → остаются явные вызовы в точках вставки
    }

    // .button--play не играет сам — открывает меню источников (горячие торренты / серверы CUB / трейлер):
    // честное имя «Онлайн» + иконка-коробка, play-треугольник теперь у зелёной «Смотреть».
    // Трогаем ТОЛЬКО детей (svg/span): onGroupButtons делает play.unbind().on(...) на самом узле,
    // а хэш full_btn_priority считается по кнопкам пула .buttons--container — обе механики целы.
    function ensureOnlineButton(cont) {
        try {
            var play = cont.find('.button--play');
            if (!play.length || play.hasClass('qdl-online-btn')) return;
            play.addClass('qdl-online-btn');
            var sv = play.children('svg');   // и <use xlink:href="#sprite-play">, и инлайн-SVG от cardify
            if (sv.length) sv.first().replaceWith(BOX_ICON); else play.prepend(BOX_ICON);
            var sp = play.children('span');
            if (sp.length) sp.first().text('Онлайн'); else play.append('<span>Онлайн</span>');
        } catch (e) {}
    }

    // «▶ Продолжить: Серия N» на карточке сериала — только когда есть что продолжать
    // (недосмотренная серия или следующая после досмотренных). Прогресс — Lampa.Timeline (это устройство).
    // Кнопка живёт дольше одного захода на карточку, поэтому создание и ОБНОВЛЕНИЕ разведены:
    // подпись обязана меняться после просмотра, а узел при этом обязан оставаться тем же.
    function addContinueButton(render, cont, hash, name, gateItem) {
        fetchEpisodes(hash, function (files) {
            var vids = mergedVideoFiles(files);
            if (!vids.length) return;
            // прогрев и здесь: покрывает восстановленную активность и зелёную «Смотреть»
            // на обычной карточке — пути, где openDownload/prewarmForCard не звались.
            // Дубль с prewarmForCard безвреден: сервер дедупит и очередь, и «уже прогрет».
            if (vids.length === 1) { warmup(srcHash(vids[0], hash), vids[0].index); return; }   // фильм/один файл
            var target = chooseContinue(vids, function (f) { return pickTimeline(hash, f); });
            var warm = target || vids[0];
            warmup(srcHash(warm, hash), warm.index);
            var exist = $('.qdl-continue-btn', render);
            if (!target) { if (exist.length) { exist.remove(); orderButtons(cont); } return; }
            if (exist.length) { exist.children('span').text('Продолжить · ' + epShort(target.name)); return; }
            var label = 'Продолжить · ' + epShort(target.name);
            var b = $('<div class="full-start__button selector qdl-continue-btn">' + CONTINUE_ICON + '<span>' + esc(label) + '</span></div>');
            b.on('hover:enter', function () {
                // через экран серий с автоплеем: «назад» из плеера вернёт в список серий
                confirmPartial(gateItem, function () { chooseEpisode(hash, name, true); });
            });
            cont.prepend(b);
            navAppend(cont, b);   // приехала async → в коллекции навигатора её ещё нет
            orderButtons(cont);
        });
    }

    // 🔥 Полная карточка строит кнопки по событию 'full' → 'complite', а Activity.backward()
    // его НЕ шлёт: вернувшись из плеера/экрана серий, зритель видел подпись, посчитанную при
    // первом открытии. Досматриваем серию — «Продолжить» обязана переехать на следующую.
    // Ловим два момента возврата: старт восстановленной активности и закрытие плеера
    // (плеер активность не меняет, поэтому одного 'activity' мало).
    function initContinueRefresh() {
        if (window.__qdl_continue_refresh) return;
        window.__qdl_continue_refresh = true;
        var sync = function () {
            try {
                var act = Lampa.Activity.active() || {};
                var hash = act.qdl_hash;
                if (!hash || !act.activity) return;
                var render = act.activity.render();
                if (!render) return;
                var cont = $('.full-start__buttons', render);
                if (!cont.length) cont = $('.full-start-new__buttons', render);
                if (!cont.length) return;
                var movie = act.card || {};
                addContinueButton(render, cont, hash, movie.title || movie.name || '',
                                  { progress: (typeof act.qdl_progress === 'number' ? act.qdl_progress : 1) });
            } catch (e) {}
        };
        try { Lampa.Listener.follow('activity', function (e) { if (e && e.type === 'start') sync(); }); } catch (e) {}
        try { Lampa.Player.listener.follow('destroy', function () { sync(); }); } catch (e) {}
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

            // 2.30: на ЛЮБОЙ карточке — наш CSS (постоянные подписи), ребрендинг родной
            // кнопки-агрегатора в «Онлайн» (коробка) и страж порядка кнопок
            injectCss();
            ensureOnlineButton(cont);
            ensureOrderObserver(cont);
            armFocus();   // до первого нажатия фокус наш: async-кнопки перетянут его на себя

            // тип/источник берём С ОТКРЫТОЙ КАРТОЧКИ (method/source активности), а не угадываем —
            // у TMDB id в movie и tv это РАЗНЫЕ объекты, ошибка типа = другой фильм
            var active = (function () { try { return Lampa.Activity.active() || {}; } catch (e) { return {}; } })();
            if (movie) {
                if (!movie.media_type && active.method) movie.media_type = active.method;
                if (!movie.source && active.source) movie.source = active.source;
            }

            // открыто из «Загрузок» (полная карточка, режим одной кнопки)
            if (active.qdl_hash) {
                render.addClass('qdl-only');                 // CSS прячет все прочие кнопки
                if (!$('.qdl-watch-btn', render).length) {
                    var w = $('<div class="full-start__button selector qdl-watch-btn">' + WATCH_ICON + '<span>Смотреть</span></div>');
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
                    orderButtons(cont);
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
                orderButtons(cont);   // без наших кнопок просмотра «Скачать» встаёт первой
            }

            // DMCA-карточка → режим «только Скачать»: прячем онлайн и прочие кнопки (.qdl-dmca).
            // Список грузится лениво, класс навешивается по готовности.
            if (movie && movie.id) {
                var cat = movie.media_type || (movie.first_air_date || movie.name ? 'tv' : 'movie');
                whenDmca(function () {
                    if (!isDmca(cat, movie.id)) return;
                    render.addClass('qdl-dmca');
                    orderButtons(cont);
                });
            }

            // фильм уже скачан → ЗЕЛЁНАЯ «Смотреть» + привязка метаданных.
            // Матчинг строгий — findDownload (id+media_type; имя — только для раздач без меты)
            if (movie && movie.id && !$('.qdl-watch-btn', render).length) {
                req(API + '/qdl/list', function (list) {
                    var hit = findDownload(list, movie);
                    if (hit && !hit.meta) saveMeta(hit.hash, movie);   // back-link карточка → безымянная загрузка
                    if (!hit || $('.qdl-watch-btn', render).length) return;

                    var play = $('<div class="full-start__button selector qdl-watch-btn">' + WATCH_ICON + '<span>Смотреть</span></div>');
                    play.on('hover:enter', function () { watch(hit); });
                    cont.prepend(play);
                    navAppend(cont, play);   // приехала async → в коллекции навигатора её ещё нет
                    orderButtons(cont);
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
    // ── iPhone: эфир в НАТИВНОМ iOS-плеере (без VLC и без обновления приложения) ──
    // Приложение не перехватывает HTML5-видео (в VLC ведёт только мост Lampa.Player.play →
    // AndroidJS.openPlayer), а WKWebView собран с allowsInlineMediaPlayback. Поэтому свой <video>
    // БЕЗ playsinline при удачном play() сам открывает штатный фуллскрин-плеер iOS (нормальные
    // контролы, AirPlay, PiP — и тап по экрану больше не мгновенная пауза).
    // ⚠️ play() обязан жить в НАСТОЯЩЕМ жесте: hover:enter у Lampa диспатчится из setTimeout(20мс),
    // жест там мёртв → зовём это ТОЛЬКО из прямого addEventListener('click') (паттерн qdl-fs).
    // qdl_ios_live: 'auto' (дефолт) | 'off' — аварийный откат на старый VLC-путь через Storage.
    function iosLiveNative() {
        var mode = 'auto';
        try { mode = Lampa.Storage.get('qdl_ios_live', 'auto') || 'auto'; } catch (e) {}
        if (mode === 'off') return false;
        return window.d1vision_platform === 'ios';
    }

    var iosLiveVideo = null;
    var iosLiveTapAt = 0;

    // Снести КОНКРЕТНУЮ попытку (её <video> + оверлей). Чужой текущий элемент не трогаем:
    // хвост убитой попытки (reject её play(), поздний watchdog) раньше сносил видео преемника —
    // отсюда и был баг «двойной тап открывает VLC».
    function iosLiveDrop(v) {
        if (!v) return;
        if (iosLiveVideo === v) iosLiveVideo = null;
        try { v.pause(); } catch (e) {}
        try { v.removeAttribute('src'); v.load(); } catch (e) {}   // канонический release потока в WebKit
        var w = v.__qdlWrap || v;
        try { if (w.parentNode) w.parentNode.removeChild(w); } catch (e) {}
    }

    function iosLiveStop() { iosLiveDrop(iosLiveVideo); }

    // Вызывать ТОЛЬКО синхронно из прямого click. true = взялись (hover:enter этого тапа подавить).
    function liveWatchPlayIOS(cam) {
        if (!iosLiveNative()) return false;
        if (Date.now() - iosLiveTapAt < 800) return true;   // двойной тап = ОДНА попытка (второй клик молча гасим)
        iosLiveTapAt = Date.now();
        iosLiveStop();
        var my = ++liveDayToken;                // pause/stop экрана (liveDayCancel) глушит фолбек и тост
        var started = false, settled = false;

        // ВИДИМЫЙ оверлей на весь экран: WebKit авто-фуллскринит только «заметное» видео —
        // невидимый микро-элемент срабатывал через раз (первый тап ок, после Done — уже нет).
        // Заодно мгновенная обратная связь: экран сразу чёрный, не «ничего не происходит».
        var wrap = document.createElement('div');
        wrap.style.cssText = 'position:fixed;left:0;top:0;right:0;bottom:0;background:#000;z-index:9999';

        var v = document.createElement('video');
        v.controls = true;                      // БЕЗ playsinline → play() уводит в нативный фуллскрин iOS
        v.style.cssText = 'width:100%;height:100%;object-fit:contain';
        v.__qdlWrap = wrap;
        v.src = API + '/qdl/live/watch/hls/' + encodeURIComponent(cam.id) + '/index.m3u8';
        wrap.appendChild(v);

        // ✕ — на случай, если фуллскрин так и не поднялся, а эфир играет инлайн в оверлее
        var x = document.createElement('div');
        x.textContent = '✕';
        x.style.cssText = 'position:absolute;right:.6em;top:.6em;z-index:2;width:2.2em;height:2.2em;display:flex;align-items:center;justify-content:center;background:rgba(0,0,0,.55);color:#fff;border-radius:50%;font-size:1.6em';
        x.addEventListener('click', function (e) { e.stopPropagation(); close(); iosLiveDrop(v); });
        wrap.appendChild(x);

        document.body.appendChild(wrap);
        iosLiveVideo = v;

        var toast = setTimeout(function () { if (!settled && my === liveDayToken) Lampa.Noty.show('Включаю эфир…'); }, 700);
        var watchdog = setTimeout(fail, 12000);   // сервер сам ждёт поток до ~8с → 12с потолок

        function close() { settled = true; clearTimeout(toast); clearTimeout(watchdog); if (my === liveDayToken) liveDayToken++; }
        function fail() {
            if (settled) return;
            var alive = (my === liveDayToken);
            close(); iosLiveDrop(v);            // только СВОЙ элемент — см. коммент у iosLiveDrop
            if (alive) liveWatchPlay(cam);      // фолбек: старый прогрев /start → VLC-мост (жест не нужен)
        }

        v.addEventListener('playing', function () { started = true; v.__qdlStarted = true; if (!settled) close(); });
        // страховка: авто-презентация не случилась → просим нативный фуллскрин явно
        v.addEventListener('loadedmetadata', function () {
            try { if (!v.webkitDisplayingFullscreen && v.webkitEnterFullscreen) v.webkitEnterFullscreen(); } catch (e) {}
        });
        v.addEventListener('error', function () {
            if (!started) { fail(); return; }
            iosLiveDrop(v); Lampa.Noty.show('Эфир прервался');
        });
        v.addEventListener('webkitendfullscreen', function () {
            // кнопка PiP тоже закрывает фуллскрин — элемент не убивать, пока он в PiP
            setTimeout(function () {
                try { if (v.webkitPresentationMode === 'picture-in-picture') return; } catch (e) {}
                iosLiveDrop(v);
            }, 0);
        });
        v.addEventListener('webkitpresentationmodechanged', function () {
            try { if (started && v.webkitPresentationMode === 'inline' && v === iosLiveVideo) iosLiveDrop(v); } catch (e) {}
        });

        try {
            var p = v.play();                   // ← здесь живёт user-gesture
            if (p && p.catch) p.catch(function () { fail(); });
        } catch (e) { fail(); }
        return true;
    }

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
        var body = $('<div class="qdl-watch-grid"></div>');   // раскладка — в injectCss (центр + растягивание)
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
                '<div class="selector qdl-watch-tile" data-cam="' + c.id + '" style="position:relative;border-radius:.8em;overflow:hidden;background:#111">' +
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

            // iPhone: нативный плеер требует ЖИВОГО жеста → прямой click (hover:enter Lampa
            // диспатчит через setTimeout — жест мёртв). На один тап прилетают ОБА события
            // (click, затем hover:enter через ~20мс) — метка времени гасит второй.
            // data-live держит refresh() — замыкание c устаревает после build.
            el.attr('data-live', c.live ? '1' : '0');
            var iosTapAt = 0;
            el[0].addEventListener('click', function () {
                if (el.attr('data-live') === '1' && liveWatchPlayIOS(c)) iosTapAt = Date.now();
            });
            el.on('hover:enter', function () {
                if (Date.now() - iosTapAt < 1000) return;   // этот тап уже забрал нативный путь
                liveWatchPlay(c);
            });
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
                    el.attr('data-live', c.live ? '1' : '0');   // свежий статус для iOS-пути (замыкание тайла — снимок)
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
        // pause = ушли ВПЕРЁД на другой экран: глушим таймер, висящий прогрев эфира и
        // ещё НЕ заигравший скрытый iOS-<video> (иначе он заиграл бы и открыл фуллскрин поверх
        // нового экрана). Уже играющий/PiP — не трогаем: зритель им пользуется.
        function killPendingIosVideo() { if (iosLiveVideo && !iosLiveVideo.__qdlStarted) iosLiveStop(); }
        this.pause = function () { stopTimer(); liveDayCancel(); killPendingIosVideo(); };
        this.stop = function () { stopTimer(); liveDayCancel(); killPendingIosVideo(); };
        this.destroy = function () {
            comp.destroyed = true;
            liveDayCancel();
            stopTimer();
            killPendingIosVideo();   // играющий/PiP живёт на body и переживает экран — закроется сам по Done/выходу из PiP
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
        var autoJumped = false;             // авто-прыжок на последний день с записями — один раз
        var userTouched = false;            // зритель сам менял день → в его выбор не вмешиваемся

        this.create = function () {
            injectCss();   // фокус-стили dayBar/camRow
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
            else if (!r.cameras || !r.cameras.length) {
                // Сегодня пусто, а зритель день не выбирал → сами прыгаем на последний день
                // с записями. Иначе на ТВ экран выглядит мёртвым: три кнопки сверху, стрелке
                // «вниз» некуда идти — читается как «навигация не работает».
                if (!autoJumped && !userTouched && !object.qdl_date) {
                    autoJumped = true;
                    body.append(liveMsg('За сегодня записей нет — ищу последний день с записями…'));
                    network.silent(API + '/qdl/live/days', function (dr) {
                        if (comp.destroyed) return;
                        var target = null;
                        ((dr && dr.days) || []).forEach(function (d) { if (!target && d.count > 0) target = d; });
                        if (target && target.date !== date) { date = target.date; reload(); }
                        else { body.empty(); body.append(dayBar(r)); body.append(emptyMsg(r)); comp.activity.toggle(); }
                    }, function () {});
                }
                else body.append(emptyMsg(r));
            }
            else {
                if (autoJumped && r.today && date !== r.today)
                    body.append($('<div style="padding:.2em 1.6em 0;font-size:1.15em;opacity:.55">За сегодня записей пока нет — показан последний день с записями</div>'));
                if (r.total && r.cameras.length < r.total)
                    body.append($('<div style="padding:.2em 1.6em 0;font-size:1.15em;opacity:.5">Писали ' + r.cameras.length + ' из ' + r.total + ' камер</div>'));
                r.cameras.forEach(function (c) { body.append(camRow(c)); });
            }

            if (keepDayFocus) { last = bar.find('.qdl-live-day')[0]; keepDayFocus = false; }

            comp.activity.loader(false);
            comp.activity.toggle();   // пере-собрать коллекцию фокуса после перерисовки
        }

        function emptyMsg(r) {
            return liveMsg('За этот день записей нет' + (r.total ? ' (камер всего: ' + r.total + ')' : '') + '. Выбери другой день кнопкой сверху.');
        }

        function reload() { keepDayFocus = true; body.empty(); load(); }

        function dayBar(r) {
            var canNext = !!(date && today && date < today);
            var bar = $('<div style="display:flex;align-items:center;gap:.7em;padding:1.2em 1.4em .5em"></div>');
            var prev = $('<div class="selector qdl-btn-focus" style="padding:.65em 1.1em;background:rgba(255,255,255,.08);border-radius:.6em;font-size:1.4em">◀</div>');
            var day = $('<div class="selector qdl-btn-focus qdl-live-day" style="flex:1;text-align:center;padding:.65em 1.2em;background:rgba(255,255,255,.13);border-radius:.6em;font-size:1.5em;font-weight:600">📅 ' + esc(r.label || 'Выбрать день') + '</div>');
            var next = $('<div class="selector qdl-btn-focus" style="padding:.65em 1.1em;background:rgba(255,255,255,' + (canNext ? '.08' : '.03') + ');border-radius:.6em;font-size:1.4em;opacity:' + (canNext ? '1' : '.35') + '">▶</div>');

            prev.on('hover:enter', function () { userTouched = true; date = liveShift(date || today, -1); reload(); });
            next.on('hover:enter', function () {
                if (!canNext) { Lampa.Noty.show('Это самый свежий день'); return; }
                userTouched = true;
                date = liveShift(date, 1);
                reload();
            });
            day.on('hover:enter', function () { userTouched = true; pickDay(); });

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
                '<div class="selector qdl-row-focus" style="display:flex;align-items:center;gap:1.2em;padding:.9em;margin:.45em 1.4em;background:rgba(255,255,255,.06);border-radius:.8em">' +
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
            injectCss();   // фокус-стили кнопок/строк записей
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
            var day = $('<div class="selector qdl-btn-green" style="margin:.6em 1.4em;padding:1em 1.2em;background:rgba(20,160,40,.85);border-radius:.8em;font-size:1.5em;font-weight:600">▶ Весь день одной записью   ·   ' + liveDur(total) + '</div>');
            day.on('hover:focus', function () { last = day[0]; scroll.update(day, true); });
            day.on('hover:enter', function () { livePlayDay(cam, date, label); });

            // Запасной: куски по очереди (каждый со своим таймлайном) — если склейка не собралась.
            var seq = $('<div class="selector qdl-btn-focus" style="margin:.4em 1.4em;padding:.8em 1.2em;background:rgba(255,255,255,.1);border-radius:.8em;font-size:1.3em">Фрагменты подряд, по одному</div>');
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
                '<div class="selector qdl-row-focus" style="display:flex;align-items:center;gap:1.2em;padding:.8em;margin:.4em 1.4em;background:rgba(255,255,255,.06);border-radius:.8em">' +
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
    // Текст пункта — ровно «jut.su» (решение владельца), не «Аниме»
    function buildJutMenuItem() {
        var item = $('<li class="menu__item selector qdl-jut-menu"><div class="menu__ico">' + ANIME + '</div><div class="menu__text">jut.su</div></li>');
        item.on('hover:enter', function () {
            Lampa.Activity.push({ url: '', title: 'jut.su', component: 'jut_catalog', page: 1 });
        });
        return item;
    }

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

    // Одна нода на селектор: jQuery-НАБОР из >1 элемента ломает вставки (.after/.append на наборе
    // КЛОНИРУЕТ узел в каждый элемент) — так плодились дубли пунктов меню и красного бейджа.
    // Лишние экземпляры (наследие прошлых версий/двойных вставок) сносим — self-heal.
    function dedupe(sel) {
        var n = $(sel);
        if (n.length > 1) n.slice(1).remove();
        return n.first();
    }

    function ensureMenu() {
        try {
            var anchor = $('.menu .menu__item[data-action="myperson"]').first();
            if (!anchor.length) return;                 // «Персоны» ещё не отрисованы — ждём
            var existing = dedupe('.menu .qdl-menu');
            if (!existing.length) { anchor.after(buildMenuItem()); }
            // уже есть — держим строго сразу после «Персоны» (меню могло пере-рендериться)
            else if (existing.prev('.menu__item')[0] !== anchor[0]) {
                existing.detach();
                anchor.after(existing);
            }

            // «Уведомления» — строго сразу после «Загрузки»
            var dl = dedupe('.menu .qdl-menu');
            var noti = dedupe('.menu .qdl-noti-menu');
            if (dl.length) {
                if (!noti.length) { dl.after(buildNotiMenuItem()); setTimeout(pollNotifications, 200); }   // подтянуть бейдж сразу после появления пункта
                else if (noti.prev('.menu__item')[0] !== dl[0]) { noti.detach(); dl.after(noti); }
            }

            // «D1versy Live» (эфир) — строго сразу после «Уведомления»
            var nt = dedupe('.menu .qdl-noti-menu');
            var watch = dedupe('.menu .qdl-watch-menu');
            if (nt.length) {
                if (!watch.length) nt.after(buildWatchMenuItem());
                else if (watch.prev('.menu__item')[0] !== nt[0]) { watch.detach(); nt.after(watch); }
            }

            // «D1versy Rec» (записи) — строго сразу после «D1versy Live»
            var w = dedupe('.menu .qdl-watch-menu');
            var live = dedupe('.menu .qdl-live-menu');
            if (w.length) {
                if (!live.length) w.after(buildLiveMenuItem());
                else if (live.prev('.menu__item')[0] !== w[0]) { live.detach(); w.after(live); }
            }

            // «jut.su» — строго сразу после «D1versy Rec» (класс .qdl-live-menu, имена обманчивы:
            // .qdl-watch-menu = Live/эфир, .qdl-live-menu = Rec/записи)
            var lv = dedupe('.menu .qdl-live-menu');
            var jut = dedupe('.menu .qdl-jut-menu');
            if (lv.length) {
                if (!jut.length) lv.after(buildJutMenuItem());
                else if (jut.prev('.menu__item')[0] !== lv[0]) { jut.detach(); lv.after(jut); }
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
            // гард в том же scope, что updateNotiBadge ($('.qdl-noti-head') по всему документу):
            // раньше гард смотрел только внутрь .head, а копию вне него не видел — и вставлял ещё одну
            if (dedupe('.qdl-noti-head').length) return;
            var actions = $('.head .head__actions').first();   // .first(): вставка в НАБОР клонирует узел
            if (!actions.length) return;                       // хедер ещё не отрисован / иная сборка
            injectCss();
            actions.append(buildHeaderNoti());                 // штатный колокольчик вырезан AppPatch'ем — наш единственный
            pollNotifications();                               // сразу подтянуть текущий бейдж
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

    // ═════════════════════════════════════════════════════════════════════════
    // Вкладка jut.su: каталог → тайтл → серии → плеер, плюс «Скачать» и «Следить».
    // Сервер уже выбрал максимальное качество и отдал готовый относительный URL —
    // клиенту выбирать нечего (streamUrl() трогать не нужно, он про torrent-hash).
    // Устройство и грабли ТВ-интерфейса: E:\Media-server\claude\jut\05-client.md
    // ═════════════════════════════════════════════════════════════════════════

    function jutErrText(r) {
        var m = r && (r.message || r.error);
        return m || 'jut.su недоступен';
    }

    // pv приходит с сервера и означает «постер уже апгрейжен» (Shikimori → MAL id → AniList,
    // 460×690 вместо квадрата 186×186 с jut.su). Версия в URL нужна как жёсткий сброс кеша:
    // без неё уже показанная карточка держала бы миниатюру сутками.
    function jutPosterUrl(slug, pv) {
        return API + '/qdl/jut/poster?slug=' + encodeURIComponent(slug) + (pv ? '&v=' + pv : '');
    }

    function jutBackdropUrl(slug) { return API + '/qdl/jut/backdrop?slug=' + encodeURIComponent(slug); }

    // Ключ таймлайна строится в формате СЕРВЕРНОГО tl (qdltl:jut:<slug>:s1e7), чтобы прогресс
    // онлайн-просмотра не потерялся после скачивания тайтла в «Загрузки».
    function jutTl(slug, key) {
        try { return Lampa.Timeline.view(Lampa.Utils.hash('qdltl:jut:' + slug + ':' + key)); }
        catch (e) { return null; }
    }

    // Правило «что продолжить» обязано быть ОДНО для онлайна и для скачанного: иначе карточка
    // тайтла jut.su и карточка в «Загрузках» спорят между собой на одном и том же прогрессе.
    // Онлайн-item приводим к виду, который понимают sortEpisodes/pickContinue (epkey = e.key).
    function jutAsFile(e) { return { epkey: e.key, season: e.season, name: e.key, _jut: e }; }
    function jutChooseContinue(items, viewFn) {
        var cur = chooseContinue((items || []).map(jutAsFile), function (f) { return viewFn(f._jut) || {}; });
        return cur ? cur._jut : null;
    }

    function jutEpTitle(e, titleName) {
        var base = e.kind === 'film' ? (e.ep + ' фильм')
                 : e.kind === 'ova' ? ('OVA ' + e.ep)
                 : e.kind === 'gameova' ? ('Игровая OVA ' + e.ep)
                 : (e.ep + ' серия');
        if (e.name) base += ' — ' + e.name;
        return (titleName ? titleName + ' · ' : '') + base;
    }

    // Плеер запускается ТОЛЬКО после resolve: прямую ссылку на CDN отдавать нельзя —
    // hash в ней вяжется с UA, а у плеера он свой → 403.
    function jutPlay(slug, e, titleName, siblings) {
        Lampa.Noty.show('Готовлю ' + jutEpTitle(e, ''));
        req(API + '/qdl/jut/resolve?slug=' + encodeURIComponent(slug) +
            '&season=' + e.season + '&ep=' + e.ep + '&kind=' + encodeURIComponent(e.kind === 'gameova' ? 'game-ova' : e.kind),
            function (r) {
                if (!r || !r.ok) { Lampa.Noty.show(jutErrText(r)); return; }
                var item = { title: jutEpTitle(e, titleName), url: API + r.url };
                var tl = jutTl(slug, e.key); if (tl) item.timeline = tl;
                try {
                    Lampa.Player.play(item);
                    // Плейлист сезона — чтобы работал автопереход к следующей серии.
                    // 🔥 Токен обязателен: /qdl/jut/stream резолвит ссылку ПО НЕМУ, а раньше всем
                    // элементам, кроме текущего, подставлялся пустой t= → автопереход упирался
                    // в NotFound. Серия без tok (ответ старого сервера из кеша) в плейлист не
                    // попадает вовсе: «нет автоперехода» честнее битой ссылки.
                    // Порядок — общий (sortEpisodes): список сервера отсортирован, но плейлист
                    // не должен зависеть от этого.
                    var list = (siblings || [e]).filter(function (x) {
                        return x.kind === e.kind && x.season === e.season && (x.key === e.key || x.tok);
                    });
                    list = sortEpisodes(list.map(jutAsFile)).map(function (f) { return f._jut; });
                    Lampa.Player.playlist(list.map(function (x) {
                        var pi = { title: jutEpTitle(x, titleName) };
                        pi.url = (x.key === e.key) ? (API + r.url)
                                                   : (API + '/qdl/jut/stream?t=' + encodeURIComponent(x.tok));
                        var t2 = jutTl(slug, x.key); if (t2) pi.timeline = t2;
                        return pi;
                    }));
                } catch (err) { Lampa.Noty.show('Плеер не запустился'); }
            },
            function () { Lampa.Noty.show('jut.su недоступен'); });
    }

    function jutDownload(slug, scope, e) {
        var u = API + '/qdl/jut/download?slug=' + encodeURIComponent(slug) + '&scope=' + scope;
        if (e) u += '&season=' + e.season + '&ep=' + e.ep + '&kind=' + encodeURIComponent(e.kind === 'gameova' ? 'game-ova' : e.kind);
        req(u, function (r) {
            if (!r || !r.ok) { Lampa.Noty.show(jutErrText(r)); return; }
            // Текст готовит СЕРВЕР: он один знает, что именно произошло — «поставлено N»,
            // «всё уже скачано» или «уже в очереди, осталось N». Раньше все три исхода
            // печатались как «В очереди на скачивание: 0», и повторное добавление выглядело
            // так, будто ничего не произошло (жалоба владельца 12.08.2026).
            // ⚠️ Фолбэк на старый текст обязателен: у клиента может быть закешированный
            // ответ сервера без поля message.
            Lampa.Noty.show((r.message || ('В очереди на скачивание: ' + (r.queued || 0)))
                + ((r.queued || 0) > 0 ? ' — смотри «Загрузки»' : ''));
        }, function () { Lampa.Noty.show('Не удалось поставить в очередь'); });
    }

    function jutDownloadMenu(slug, e, seasonNo) {
        var items = [];
        if (e) items.push({ title: 'Только ' + jutEpTitle(e, ''), scope: 'one' });
        items.push({ title: 'Весь сезон' + (seasonNo ? ' ' + seasonNo : ''), scope: 'season' });
        items.push({ title: 'Весь тайтл (может быть очень много)', scope: 'all' });
        Lampa.Select.show({
            title: 'Скачать с jut.su',
            items: items,
            onSelect: function (s) { jutDownload(slug, s.scope, s.scope === 'one' ? e : null); },
            onBack: function () { Lampa.Controller.toggle('content'); }
        });
    }

    // Подписи кнопки слежения по режиму — карточка тайтла обязана честно показывать
    // текущий режим: подписку могли поднять до «качаю» из «Загрузок».
    var JUT_MODE_LABEL = { off: '🔔 Следить', notify: '🔔 Слежу · уведомления', grab: '🔔 Слежу · качаю' };

    // Смена состояния подписки. from — текущий режим, want — желаемый ('off'|'notify'|'grab').
    //
    // ⚠️ Существующей подписке режим меняем через /qdl/jut/watch/mode, а НЕ повторной
    // подпиской: /qdl/jut/watch сбрасывает baseline на текущее состояние сайта, и серия,
    // вышедшая между тиком и нажатием, ушла бы в baseline — в режиме «качаю» её уже никто
    // не скачает. Плюс /watch/mode не ходит в сеть и работает, когда jut.su лежит.
    //
    // ⚠️ season НЕ передаём: сервер берёт ПОСЛЕДНИЙ вышедший сезон. Раньше карточка слала
    // сезон первой серии списка и у многосезонного тайтла подписывала сезон 1, где новых
    // серий не будет никогда.
    function jutWatchSet(slug, from, want, done) {
        var q = encodeURIComponent(slug);
        var grabFlag = want === 'grab' ? 1 : 0;
        var u = want === 'off' ? (API + '/qdl/jut/watch/remove?slug=' + q)
              : from === 'off' ? (API + '/qdl/jut/watch?slug=' + q + '&autoGrab=' + grabFlag)
              : (API + '/qdl/jut/watch/mode?slug=' + q + '&autoGrab=' + grabFlag);
        var viaMode = u.indexOf('/watch/mode') !== -1;
        // старый сервер про /watch/mode не знает, а подписки могло уже не быть (NOT_WATCHED) —
        // добираем полной подпиской, режим всё равно проставится
        var repair = function () { jutWatchSet(slug, 'off', want, done); };

        req(u, function (r) {
            if (want !== 'off' && !(r && r.ok)) {
                if (viaMode) { repair(); return; }
                Lampa.Noty.show(jutErrText(r));
                return;
            }
            Lampa.Noty.show(want === 'off' ? 'Слежение снято'
                          : (r && r.message) ? r.message
                          : want === 'grab' ? '⬇ Новые серии буду качать сам'
                          : '🔔 Сообщу о новых сериях, качать не буду');
            if (done) done(want);
        }, function () {
            if (viaMode) repair();
            else Lampa.Noty.show('Не удалось изменить слежение');
        });
    }

    // Меню слежения на КАРТОЧКЕ ТАЙТЛА. Пункта «качать новые серии» здесь нет и быть
    // не должно: автоскачивание включается только из «Загрузок» (решение владельца).
    // Меню, а не мгновенное снятие: случайное нажатие не должно молча убивать подписку.
    function jutWatchMenuCard(slug, mode, done) {
        var items = [];
        if (mode === 'grab')
            items.push({ title: '🔔 Только уведомлять', subtitle: 'новые серии перестанут качаться', want: 'notify' });
        items.push({ title: '🔕 Не следить', want: 'off' });
        items.push({ title: 'Отмена' });
        Lampa.Select.show({
            title: mode === 'grab' ? 'Новые серии — сейчас качаю' : 'Новые серии — только уведомления',
            items: items,
            onSelect: function (a) {
                if (a.want) jutWatchSet(slug, mode, a.want, done);
                else Lampa.Controller.toggle('content');
            },
            onBack: function () { Lampa.Controller.toggle('content'); }
        });
    }

    // ───────── Каталог jut.su (витрина order-by-add) ─────────
    function ComponentJutCatalog(object) {
        var comp = this;
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div class="category-full mapping--grid cols--6"></div>');
        var last, page = 1, loading = false, hasNext = true, total = 0;
        var query = object.jut_query || '';

        function url(p) {
            return query
                ? API + '/qdl/jut/search?query=' + encodeURIComponent(query) + '&page=' + p
                : API + '/qdl/jut/catalog?page=' + p + '&order=add';
        }

        this.create = function () {
            injectCss();                          // фокус-стили: см. §AK.3 (без них фокус на ТВ невидим)
            this.activity.loader(true);
            scroll.minus();                       // ⚠️ без этого на ТВ у .scroll нет высоты
            html.append(scroll.render());
            scroll.body().append(body);
            // Догрузка следующей страницы по достижению конца ленты (46 страниц — кнопки не годятся).
            // Это страховка для мыши/тача; на пульте раньше срабатывает prefetch по фокусу.
            scroll.onEnd = function () { comp.load(page + 1); };
            this.load(1);
            return this.render();
        };

        this.load = function (p) {
            if (loading || (p > 1 && !hasNext)) return;
            loading = true;
            req(url(p), function (r) {
                loading = false;
                comp.activity.loader(false);
                if (!r || !r.ok) {
                    if (p === 1) comp.empty(jutErrText(r));
                    return;
                }
                page = r.page || p;
                hasNext = !!r.hasNext;
                if (p === 1 && !query) comp.appendSearchTile();
                if (p === 1 && r.stale) Lampa.Noty.show('jut.su недоступен — показываю сохранённое');
                (r.items || []).forEach(comp.append);
                total += (r.items || []).length;
                // 🔥 Обязательно и прокруткой НЕ заменяется: Scroll.startScroll при неизменившейся
                // позиции уходит в ранний return и зовёт scrollEnded() только если !isFilled(),
                // а 30 карточек экран заведомо заполняют. Без этого вызова первая страница не
                // получит ни одного события visible и грид останется с заглушками img_load.svg.
                // Это НЕ Controller.toggle и не activity.toggle — правило «toggle только при
                // p === 1» ниже не затрагивается, вызов безопасен на любой странице.
                try { Lampa.Layer.visible(scroll.render(true)); } catch (e) {}
                if (p === 1 && total === 0) comp.empty(query ? 'Ничего не найдено' : 'Каталог пуст');
                // 🔥 toggle ТОЛЬКО на первой странице. На догрузке он через Activity.start →
                // Controller.toggle('content') → collectionFocus(last || false) ставил фокус на
                // первую карточку, а её hover:focus утаскивал скролл в самое начало ленты:
                // быстро долистал до недогруженного дна — и тебя выбросило наверх. Upstream
                // (InteractionCategory.next) при догрузке toggle тоже не зовёт.
                if (p === 1) comp.activity.toggle();
            }, function () {
                loading = false;
                comp.activity.loader(false);
                if (p === 1) comp.empty('jut.su недоступен');
            });
        };

        // Догружаем ЗАРАНЕЕ — за два ряда до конца ленты, а не на самой последней карточке.
        // На пульте иначе приходится доскроллить до упора и ТАМ ждать ответа сервера (просьба
        // владельца). Считаем в карточках, а не в рядах: сетка cols--6, но ряд может быть уже.
        // Повторные вызовы безопасны — load() отсекает их по флагу loading и по hasNext.
        var PREFETCH_AHEAD = 12;

        this.prefetch = function (el) {
            if (loading || !hasNext) return;
            var kids = body.children();
            var i = kids.index(el);
            if (i >= 0 && i >= kids.length - PREFETCH_AHEAD) comp.load(page + 1);
        };

        // 🔥 На таче hover:focus не приходит ВООБЩЕ (палец не «фокусит»), поэтому last оставался
        // пустым и любой collectionFocus(last || false) уводил на первую карточку. Штатные
        // компоненты Lampa пишут last именно по hover:touch/hover:hover; Navigator.focused
        // (в отличие от focus) только помечает элемент активным и скролл не двигает.
        function markLast(el) {
            return function () {
                last = el[0];
                try { Navigator.focused(el[0]); } catch (e) {}
            };
        }

        // ⚠️ width:100% — правило .cols--N > * даёт долю ширины ЛЮБОМУ прямому потомку,
        // иначе сообщение сожмётся до ширины одной карточки
        this.empty = function (txt) {
            body.append($('<div style="width:100%;padding:2em;font-size:1.4em;opacity:.7">' + esc(txt) + '</div>'));
            comp.activity.toggle();
        };

        // Ввод текста с пульта — штатной экранной клавиатурой Lampa
        this.appendSearchTile = function () {
            var el = Lampa.Template.get('card', { title: 'Поиск', release_year: 'по названию' });
            el.addClass('qdl-jut-search');
            var img = el.find('.card__img');
            img.attr('src', './img/img_broken.svg');
            var view = el.find('.card__view'); if (!view.length) view = el;
            view.append('<div style="position:absolute;left:0;top:0;right:0;bottom:0;display:flex;align-items:center;justify-content:center;font-size:3em">🔍</div>');
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:touch hover:hover', markLast(el));
            el.on('hover:enter', function () {
                Lampa.Input.edit({ value: '', title: 'Поиск на jut.su', free: true }, function (q) {
                    q = (q || '').trim();
                    if (q) Lampa.Activity.push({ url: '', title: 'jut.su — ' + q, component: 'jut_catalog', jut_query: q, page: 1 });
                });
            });
            body.append(el);
            try { Lampa.Controller.collectionAppend(el); } catch (e) {}
        };

        var seenSlugs = {};

        this.append = function (c) {
            // Дедуп: витрина отсортирована по дате добавления, и новинка, приехавшая между
            // двумя подгрузками, сдвигает ленту — тот же тайтл прилетел бы второй карточкой.
            if (c && c.slug) {
                if (seenSlugs[c.slug]) return;
                seenSlugs[c.slug] = 1;
            }
            var el = Lampa.Template.get('card', {
                title: c.title || c.slug,
                release_year: (c.years && c.years.length ? c.years[c.years.length - 1] : '') + ''
            });
            var img = el.find('.card__img');
            img.on('error', function () { this.src = './img/img_broken.svg'; });

            // 🔥 Постер грузим ТОЛЬКО когда карточка показалась. Раньше src ставился прямо тут,
            // и страница из 30 карточек заказывала 30 постеров разом — 6.1 МБ при лимите браузера
            // в 6 соединений на origin. Свой lazy писать не нужно: шаблон 'card' уже несёт класс
            // layer--visible, а Lampa.Scroll сам зовёт Layer.visible(html) в ветке else от
            // onScroll — событие уже летит нашим карточкам, на него просто никто не подписывался.
            // Порог Layer — ±2 экрана, то есть картинка приезжает заранее, а не в момент показа.
            // ⚠️ Отсюда запрет: в этом компоненте НЕЛЬЗЯ задавать scroll.onScroll — это отключит
            // ту самую ветку и убьёт ленивую загрузку молча.
            var psrc = jutPosterUrl(c.slug, c.pv);
            var loadPoster = function () { if (img.attr('src') !== psrc) img.attr('src', psrc); };
            el.on('visible', loadPoster);

            var view = el.find('.card__view'); if (!view.length) view = el;
            if (c.ongoing)
                view.append('<div style="position:absolute;left:.4em;top:.4em;background:rgba(40,160,80,.92);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.85em;z-index:5">онгоинг</div>');
            if (c.episodes)
                view.append('<div style="position:absolute;right:.4em;top:.4em;background:rgba(0,0,0,.65);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.85em;z-index:5">' + c.episodes + '</div>');

            el.on('hover:focus', function () {
                // Страховка к ленивой загрузке: сфокусированная карточка обязана быть с постером,
                // даже если пульт обогнал Layer (зажатый ArrowDown) или событие не пришло вовсе.
                loadPoster();
                last = el[0]; scroll.update(el, true); comp.prefetch(el);
            });
            el.on('hover:touch hover:hover', markLast(el));
            el.on('hover:enter', function () {
                Lampa.Activity.push({ url: '', title: c.title || c.slug, component: 'jut_title', jut_slug: c.slug, jut_card: c });
            });
            el.on('hover:long', function () { jutDownloadMenu(c.slug, null, 0); });

            body.append(el);
            // ⚠️ Без регистрации в коллекции фокуса стрелки пульта не дойдут до 2-й страницы:
            // элементы в DOM есть, а навигатор о них не знает.
            // ⚠️ Только пока контроллер наш: ответ мог прийти, когда пользователь уже ушёл
            // в меню или на карточку — тогда карточки уехали бы в ЧУЖУЮ коллекцию фокуса.
            // При возврате toggle пересоберёт всё через collectionSet, ничего не теряется.
            try { if (!Lampa.Controller.own || Lampa.Controller.own(comp)) Lampa.Controller.collectionAppend(el); } catch (e) {}
        };

        this.render = function () { return html; };
        this.start = function () {
            Lampa.Controller.add('content', {
                // link обязателен: по нему Controller.own(comp) отличает «активны мы» от
                // «активен кто-то другой» — на этом держится безопасная догрузка карточек
                link: comp,
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
        this.destroy = function () { scroll.destroy(); html.remove(); };
    }

    // ───────── Карточка тайтла: здесь живут «Смотреть», «Скачать», «Следить» ─────────
    function ComponentJutTitle(object) {
        var comp = this;
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div class="qdl-jut-page"></div>');   // поля по краям: без них контент лежал впритык
        var last;
        var slug = object.jut_slug;
        // mode: 'off' | 'notify' | 'grab' | null — null значит «не знаю» (список подписок
        // не ответил). Разница принципиальна: слать autoGrab=0 при неизвестном режиме нельзя,
        // это молча понизило бы уже качающую подписку и сбросило её baseline.
        var data = null, mode = 'off';

        function readMode(w) {
            var rec = ((w && w.items) || []).filter(function (x) { return x.slug === slug; })[0];
            if (!rec) return 'off';
            return rec.mode || (rec.autoGrab === false ? 'notify' : 'grab');   // фолбэк на старый сервер
        }

        this.create = function () {
            injectCss();   // фокус-стили кнопок: не полагаемся, что другой экран уже инъецировал
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            req(API + '/qdl/jut/title?slug=' + encodeURIComponent(slug), function (r) {
                if (!r || !r.ok) { comp.fail(jutErrText(r)); return; }
                data = r;
                req(API + '/qdl/jut/watch/list', function (w) {
                    try { mode = readMode(w); } catch (e) { mode = null; }
                    comp.build();
                }, function () { mode = null; comp.build(); });
            }, function () { comp.fail('jut.su недоступен'); });
            return this.render();
        };

        this.fail = function (txt) {
            body.append($('<div style="width:100%;padding:2em;font-size:1.4em;opacity:.7">' + esc(txt) + '</div>'));
            this.activity.loader(false);
            this.activity.toggle();
        };

        function firstPlayable() {
            var eps = (data.items || []).filter(function (e) { return e.kind === 'episode'; });
            return eps.length ? eps[0] : (data.items || [])[0];
        }

        this.build = function () {
            var head = $('<div class="qdl-jut-head"></div>');
            var poster = $('<img class="qdl-jut-poster" src="' + jutPosterUrl(slug, data.pv) + '">');
            poster.on('error', function () { this.src = './img/img_broken.svg'; });
            head.append(poster);

            // Фон 2560×1440 с самой страницы тайтла. Ошибиться тут нечем — это картинка того же
            // тайтла, а не результат сопоставления с внешней базой.
            if (data.backdrop) {
                try { Lampa.Background.change(jutBackdropUrl(slug)); } catch (e) {}
            }

            var info = $('<div class="qdl-jut-info"></div>');
            info.append('<div class="qdl-jut-title">' + esc(data.title || slug) + '</div>');
            if (data.original) info.append('<div style="opacity:.6;font-size:1.15em;margin-top:.2em">' + esc(data.original) + '</div>');

            var facts = [];
            if (data.years && data.years.length) facts.push(data.years.join(', '));
            if (data.rating) facts.push('★ ' + data.rating);
            if (data.ongoing) facts.push('онгоинг');
            if (data.count) facts.push(data.count + ' эп.');
            if (facts.length) info.append('<div style="margin-top:.5em;opacity:.8;font-size:1.1em">' + esc(facts.join('  ·  ')) + '</div>');

            if (data.genres && data.genres.length) {
                var g = $('<div style="margin-top:.7em"></div>');
                data.genres.forEach(function (x) { g.append(chip(x)); });
                info.append(g);
            }
            head.append(info);
            body.append(head);

            // кнопки — под метаданными, рядом с названием (как в родной карточке Lampa):
            // главные действия у заголовка, а не отдельным блоком под постером
            var btns = $('<div style="display:flex;flex-wrap:wrap;gap:.7em;margin-top:1.4em"></div>');
            function mkBtn(label, onEnter, opts) {
                opts = opts || {};
                // qdl-btn-focus/qdl-btn-green обязательны: Lampa вешает класс .focus, но генерического
                // .selector.focus в её CSS НЕТ — без своего правила фокус на ТВ невидим,
                // и это читается как «пульт не работает» (та же грабля, что была в Rec, §AK.3)
                var b = $('<div class="selector ' + (opts.green ? 'qdl-btn-green' : 'qdl-btn-focus') + '" style="display:inline-flex;align-items:center;gap:.5em;background:' + (opts.green ? 'rgba(20,160,40,.85)' : 'rgba(255,255,255,.12)') + ';padding:.8em 1.3em;border-radius:.5em;font-size:1.2em">' + (opts.icon || '') + '<span></span></div>');
                b.children('span').text(label);
                b.on('hover:focus', function () { last = b[0]; scroll.update(b, true); });
                b.on('hover:enter', function () { onEnter(b); });
                btns.append(b);
                return b;
            }

            var ep0 = firstPlayable();
            // «Продолжить» ПЕРЕД «Смотреть» — как на карточке «Загрузок» (канон кнопок, 2.30).
            // До 2.42 экран тайтла таймлайн не читал вовсе и всегда стартовал с первой серии,
            // хотя claude/jut/05-client.md обещал обратное. Зелёная «Смотреть» остаётся
            // «с начала» — это осознанно: два разных действия, а не одно с сюрпризом.
            var curEp = jutChooseContinue(data.items, function (e) { return jutTl(slug, e.key); });
            if (curEp) mkBtn('Продолжить · ' + jutEpTitle(curEp, ''), function () {
                // через экран серий с автоплеем: «назад» из плеера вернёт в список, а не на тайтл
                Lampa.Activity.push({ url: '', title: 'Серии — ' + (data.title || slug),
                                      component: 'jut_episodes', jut_slug: slug, jut_data: data, jut_autoplay: true });
            }, { icon: CONTINUE_ICON }).addClass('qdl-jut-continue');
            // 2.30: единый язык кнопок — зелёная «Смотреть» с play-иконкой, «Скачать» со стрелкой
            if (ep0) mkBtn('Смотреть', function () { jutPlay(slug, ep0, data.title, data.items); }, { icon: WATCH_ICON, green: true });
            mkBtn('📄 Серии', function () {
                Lampa.Activity.push({ url: '', title: 'Серии — ' + (data.title || slug),
                                      component: 'jut_episodes', jut_slug: slug, jut_data: data });
            });
            mkBtn('Скачать', function () { jutDownloadMenu(slug, ep0, ep0 ? ep0.season : 1); }, { icon: ICON });
            // «Следить» ЗДЕСЬ = только уведомления: серий на диске может не быть вовсе.
            // Автоскачивание включается исключительно из «Загрузок» (решение владельца),
            // поэтому ни одна ветка этой кнопки не отправляет autoGrab=1.
            mkBtn(JUT_MODE_LABEL[mode] || JUT_MODE_LABEL.off, function (b) {
                var apply = function (now) {
                    mode = now;
                    b.children('span').text(JUT_MODE_LABEL[mode]);
                };
                // Режим неизвестен (список подписок не ответил) — переспрашиваем. Слепая
                // подписка здесь понизила бы режим «качаю» до «уведомлений» и сбросила baseline.
                if (mode === null) {
                    req(API + '/qdl/jut/watch/list', function (w) {
                        try { mode = readMode(w); } catch (e) { mode = null; }
                        if (mode === null) { Lampa.Noty.show('Не удалось узнать состояние слежения'); return; }
                        b.children('span').text(JUT_MODE_LABEL[mode]);
                        if (mode === 'off') jutWatchSet(slug, 'off', 'notify', apply);
                        else jutWatchMenuCard(slug, mode, apply);
                    }, function () { Lampa.Noty.show('Не удалось узнать состояние слежения'); });
                    return;
                }
                if (mode === 'off') jutWatchSet(slug, 'off', 'notify', apply);
                else jutWatchMenuCard(slug, mode, apply);
            });
            info.append(btns);

            if (data.descr)
                body.append($('<div class="qdl-jut-descr">' + esc(data.descr) + '</div>'));

            this.activity.loader(false);
            this.activity.toggle();
        };

        // Подпись «Продолжить» после возврата из плеера/списка серий: экран строится один раз,
        // start() зовётся на каждом возврате. Нет цели — кнопку убираем (досмотрели всё).
        this.refreshContinue = function () {
            if (!data) return;
            var cur = jutChooseContinue(data.items, function (e) { return jutTl(slug, e.key); });
            var btn = body.find('.qdl-jut-continue');
            if (!btn.length) return;                      // кнопки не было при build — не плодим её тут
            if (!cur) { btn.remove(); return; }
            btn.children('span').text('Продолжить · ' + jutEpTitle(cur, ''));
        };

        this.render = function () { return html; };
        this.start = function () {
            // Ведро серверных таймкодов тайтла. Без него онлайн-просмотры jut.su писались в ведро
            // ПОСЛЕДНЕЙ открытой TMDB-карточки (в боевой БД так и лежит — 1275779_movie),
            // а читать их оттуда никто никогда не приходил.
            setTlBucket(jutBucket(slug));
            comp.refreshContinue();
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
        this.destroy = function () { clearTlBucket(jutBucket(slug)); scroll.destroy(); html.remove(); };
    }

    // ───────── Экран серий jut.su ─────────
    // Копия структуры ComponentEpisodes (тот жёстко завязан на torrent-hash и /qdl/audio).
    // Инварианты, которые нельзя терять, перечислены в claude/jut/05-client.md §3.
    function ComponentJutEpisodes(object) {
        var comp = this;
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div class="qdl-jut-page"></div>');   // тот же контейнер с полями, что у экрана тайтла
        var last;
        var rows = [];        // отметки обновляются на месте — DOM не перестраивается, фокус жив
        var slug = object.jut_slug;
        var data = object.jut_data || null;

        this.create = function () {
            injectCss();
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            if (data) { this.build(); return this.render(); }
            req(API + '/qdl/jut/title?slug=' + encodeURIComponent(slug), function (r) {
                if (r && r.ok) data = r;
                comp.build();
            }, function () { comp.build(); });
            return this.render();
        };

        function view(e) { return jutTl(slug, e.key); }

        this.build = function () {
            rows = [];   // повторный build (перерисовка) иначе копил бы строки и обновлял мёртвые узлы
            if (!data || !(data.items || []).length) {
                body.append($('<div style="width:100%;padding:2em;font-size:1.4em;opacity:.7">Серий не найдено</div>'));
                this.activity.loader(false); this.activity.toggle(); return;
            }

            var groups = {};
            (data.items || []).forEach(function (e) {
                var g = e.kind === 'episode' ? ('Сезон ' + e.season)
                      : e.kind === 'film' ? 'Фильмы'
                      : e.kind === 'ova' ? 'OVA' : 'Прочее';
                (groups[g] = groups[g] || []).push(e);
            });

            Object.keys(groups).forEach(function (g) {
                body.append($('<div style="font-size:1.3em;opacity:.7;margin:1.2em 0 .6em">' + esc(g) + '</div>'));
                groups[g].forEach(function (e) {
                    // qdl-row-focus — см. комментарий у mkBtn: без своего focus-правила
                    // строка на ТВ никак не подсвечивается (§AK.3)
                    var el = $('<div class="selector qdl-ep qdl-row-focus" style="padding:.9em 1.1em;margin-bottom:.4em;background:rgba(255,255,255,.07);border-radius:.5em;font-size:1.2em">' +
                        '<span class="qdl-ep-mark"></span>' + esc(jutEpTitle(e, '')) + '</div>');
                    el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
                    el.on('hover:enter', function () { jutPlay(slug, e, data.title, data.items); });
                    el.on('hover:long', function () { jutDownloadMenu(slug, e, e.season); });
                    body.append(el);
                    rows.push({ el: el, e: e });
                });
            });

            this.activity.loader(false);
            this.activity.toggle();
        };

        // Свежие ✓/►N% — зовётся из start() при каждом возврате, в том числе из плеера.
        // «Текущая» считается ОБЩИМ правилом (jutChooseContinue), а не местной эвристикой
        // «первая на паузе сверху»: иначе экран серий подсвечивал одну серию, а кнопка
        // «Продолжить» на тайтле и в «Загрузках» вела на другую.
        this.refreshMarks = function () {
            if (!rows.length) return;
            var cur = jutChooseContinue(data && data.items, view);
            rows.forEach(function (r) {
                r.el.find('.qdl-ep-mark').text(epMark((view(r.e) || {}).percent || 0));
                r.el.toggleClass('qdl-ep--cur', !!cur && r.e === cur);
            });
            // Стартовый фокус на продолжаемой серии; явный выбор пользователя не перебиваем
            if (cur && !last) {
                var hit = rows.filter(function (r) { return r.e === cur; })[0];
                if (hit) last = hit.el[0];
            }
        };

        // Автоплей с кнопки «Продолжить» на карточке тайтла — та же семантика, что у qdl_autoplay
        // на скачанном: одноразово, возврат из плеера сюда же и без перезапуска.
        this.autoplay = function () {
            if (!object.jut_autoplay || !data || !(data.items || []).length) return;
            object.jut_autoplay = false;
            var cur = jutChooseContinue(data.items, view);
            if (!cur) {
                var all = (data.items || []).map(jutAsFile);
                var f = firstUnwatched(all, function (x) { return view(x._jut) || {}; });
                cur = f ? f._jut : null;
            }
            if (cur) jutPlay(slug, cur, data.title, data.items);
        };

        this.render = function () { return html; };
        this.start = function () {
            // одно ведро таймкодов на тайтл — общее с экраном тайтла и с «Загрузками»
            setTlBucket(jutBucket(slug));
            activeEpisodesComp = comp;   // мост 'timecode_updated' перерисует отметки после pull
            comp.refreshMarks();
            comp.autoplay();
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
        this.destroy = function () {
            if (activeEpisodesComp === comp) activeEpisodesComp = null;
            clearTlBucket(jutBucket(slug));
            scroll.destroy(); html.remove();
        };
    }

    // ── Экран «Хелс-чеки» в настройках (qdl 2.39). Виден только при куке qdl_unlock=1:
    // без неё компонент вообще не регистрируется (плитку раздела иначе не скрыть).
    // Данные — GET /qdl/health (сервер кеширует ответ ~30 с, поллинга нет: перезапрос
    // при каждом открытии экрана + строка «Обновить»).
    var HEALTH_ICON = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M3 12h4l2.5-6 4 12 2.5-6h5" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>';
    // ⚠️ (warn) — «работает, но не своим путём или с ошибками»: сервер отдаёт его с 2.44.
    // Неизвестный статус трактуем как ❌: лучше лишний раз позвать смотреть, чем показать зелень.
    var HEALTH_MARK = { ok: '✅', warn: '⚠️', off: '⏸', fail: '❌' };
    function healthRow(s, withGroup) {
        var mark = HEALTH_MARK[s.status] || '❌';
        return '<div class="settings-param selector" data-static="true">'
            + '<div class="settings-param__name">' + mark + ' ' + esc(s.name || s.id)
            + (withGroup ? ' <span style="opacity:.55;font-size:.85em">' + esc(s.group || '') + '</span>' : '')
            + (s.ms ? ' <span style="opacity:.55;font-size:.85em">' + (+s.ms || 0) + ' мс</span>' : '') + '</div>'
            + (s.detail ? '<div class="settings-param__descr">' + esc(String(s.detail)) + '</div>' : '')
            + '</div>';
    }
    function healthSummary(list) {
        var n = { ok: 0, warn: 0, fail: 0, off: 0 };
        (list || []).forEach(function (s) {
            var k = HEALTH_MARK[s.status] ? s.status : 'fail';   // неизвестный статус — в сбои
            n[k]++;
        });
        return n;
    }
    function healthSummaryRow(n) {
        var parts = [];
        if (n.fail) parts.push('❌ ' + n.fail + ' не ' + (n.fail === 1 ? 'работает' : 'работают'));
        if (n.warn) parts.push('⚠️ ' + n.warn);
        if (n.ok) parts.push('✅ ' + n.ok);
        if (n.off) parts.push('⏸ ' + n.off);
        if (!n.fail && !n.warn) parts.unshift('всё работает');
        return '<div class="settings-param" data-static="true"><div class="settings-param__name">'
            + esc(parts.join(' · ')) + '</div></div>';
    }
    function renderHealth(body, force) {
        var list = body.find('.qdl-health-list');
        var bindRefresh = function () {
            try { list.find('.qdl-health-refresh').on('hover:enter click', function () { renderHealth(body, true); }); } catch (e) {}
            try { Lampa.Params.listener.send('update_scroll'); } catch (e) {}   // после динамической вставки (образец parental_control)
        };
        list.html('<div class="settings-param"><div class="settings-param__name">Проверяю…</div></div>');
        // ?fresh=1 только по кнопке: сервер иначе отдаёт кеш до 30 с и «Обновить» обманывала бы
        req(API + '/qdl/health' + (force ? '?fresh=1' : ''), function (r) {
            var services = (r && r.services) || [];
            var groups = {}, order = [];
            services.forEach(function (s) {
                var g = s.group || 'Прочее';
                if (!groups[g]) { groups[g] = []; order.push(g); }
                groups[g].push(s);
            });
            var html = '<div class="settings-param selector qdl-health-refresh"><div class="settings-param__name">↻ Обновить</div></div>';
            html += healthSummaryRow(healthSummary(services));

            // Сбои — первым блоком, копиями: 18 строк без порядка означали искать проблему глазами.
            // quiet — производные строки (например, все канарейки при вставшем мониторинге):
            // красятся на своём месте, но сводку не засоряют, иначе настоящая причина тонет.
            var bad = services.filter(function (s) {
                return (s.status === 'fail' || s.status === 'warn' || !HEALTH_MARK[s.status]) && !s.quiet;
            });
            if (bad.length) {
                html += '<div class="settings-param-title"><span>Проблемы</span></div>';
                bad.forEach(function (s) { html += healthRow(s, true); });
            }

            order.forEach(function (g) {
                html += '<div class="settings-param-title"><span>' + esc(g) + '</span></div>';
                groups[g].forEach(function (s) { html += healthRow(s); });
            });
            if (!order.length) html += '<div class="settings-param"><div class="settings-param__name">Пусто</div></div>';
            list.html(html);
            bindRefresh();
        }, function () {
            list.html('<div class="settings-param selector qdl-health-refresh"><div class="settings-param__name">❌ /qdl/health недоступен — повторить</div></div>');
            bindRefresh();
        });
    }
    function registerHealthSettings() {
        if (window.qdl_health_settings || !qdlUnlocked()) return;   // гард от двойной регистрации (паттерн Transcoding/backup)
        if (!window.Lampa || !Lampa.SettingsApi || !Lampa.SettingsApi.addComponent) return;
        window.qdl_health_settings = true;
        Lampa.SettingsApi.addComponent({ component: 'qdl_health', icon: HEALTH_ICON, name: 'Хелс-чеки' });
        // ⚠️ строго ПОСЛЕ addComponent: он сам ставит пустой шаблон settings_qdl_health и перетёр бы наш
        Lampa.Template.add('settings_qdl_health', '<div><div class="qdl-health-list"></div></div>');
        Lampa.Settings.listener.follow('open', function (e) {
            if (e && e.name === 'qdl_health') renderHealth(e.body);
        });
    }

    function start() {
        Lampa.Component.add('qdl_downloads', ComponentDownloads);
        Lampa.Component.add('qdl_episodes', ComponentEpisodes);
        Lampa.Component.add('qdl_card', ComponentCard);
        Lampa.Component.add('qdl_notifications', ComponentNotifications);
        Lampa.Component.add('qdl_live', ComponentLive);
        Lampa.Component.add('qdl_live_camera', ComponentLiveCamera);
        Lampa.Component.add('qdl_live_watch', ComponentLiveWatch);
        Lampa.Component.add('jut_catalog', ComponentJutCatalog);
        Lampa.Component.add('jut_title', ComponentJutTitle);
        Lampa.Component.add('jut_episodes', ComponentJutEpisodes);
        Lampa.Listener.follow('full', addButton);
        startMenuWatcher();
        startHeaderNotiWatcher();
        startPlayerFsWatcher();
        try { registerHealthSettings(); } catch (e) {}   // «Хелс-чеки» в настройках (только при куке qdl_unlock)
        pollNotifications();
        try { initSelectFix(); } catch (e) {}            // фикс скролла селектбоксов (upstream mheight-баг)
        try { initTimelineMirror(); } catch (e) {}       // аварийный фолбэк зеркала (режим 'auto', см. qdl 2.18)
        try { initTimecodeBridge(); } catch (e) {}       // перерисовка экрана серий по pull серверных таймкодов
        try { initContinueRefresh(); } catch (e) {}      // «Продолжить» на полной карточке — свежая после возврата
        try { whenDmca(function () {}); } catch (e) {}   // прогрев DMCA-списка до первого открытия карточки
        try { setInterval(pollNotifications, 90000); } catch (e) {}   // фолбэк: основной путь — пуш по 'lwsEvent' (qdl 2.19)
    }

    if (window.appready) start();
    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
