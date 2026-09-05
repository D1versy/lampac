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
    // Detection: рамка сканирования с лупой — смысловой аналог ScanSearch у оригинального
    // интерфейса регистратора. Стиль общий с CAM/HEALTH_ICON/D1V_ICON: 24x24, currentColor,
    // stroke-width 2 — иначе кнопка выбивалась бы из ряда наших иконок.
    var DETECT_ICON = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M3 8V5.5A2.5 2.5 0 015.5 3H8" stroke="currentColor" stroke-width="2" stroke-linecap="round"/><path d="M16 3h2.5A2.5 2.5 0 0121 5.5V8" stroke="currentColor" stroke-width="2" stroke-linecap="round"/><path d="M21 16v2.5a2.5 2.5 0 01-2.5 2.5H16" stroke="currentColor" stroke-width="2" stroke-linecap="round"/><path d="M8 21H5.5A2.5 2.5 0 013 18.5V16" stroke="currentColor" stroke-width="2" stroke-linecap="round"/><circle cx="11" cy="11" r="3.2" stroke="currentColor" stroke-width="2"/><path d="M13.4 13.4L17 17" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';
    var REC = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><circle cx="12" cy="12" r="9" stroke="currentColor" stroke-width="2"/><circle cx="12" cy="12" r="3.5" fill="currentColor"/></svg>';
    var ANIME = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><rect x="3" y="4" width="18" height="14" rx="2.5" stroke="currentColor" stroke-width="2"/><path d="M10 8.5l4.5 2.5L10 13.5v-5z" fill="currentColor"/><path d="M8 21h8" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';

    // ───────── Идентификатор устройства (для истории jut.su на сервере) ─────────
    // Берём канонический uid форка — тот же, с которым ходят Sync/TimeCode/Bookmark.
    // Своё второе понятие «устройства» немедленно разъехалось бы с прогрессом просмотра.
    // Сервер разбирает его сам: RequestInfo читает query uid= → requestInfo.user_uid.
    // ⚠️ String(): Utils.uid(8) может выдать чисто цифровую строку, а Storage.get такую
    // возвращает ЧИСЛОМ (ветка /^\d+$/) — конкатенация с number молча дала бы 'NaN'-мусор.
    function qdlUid() {
        try {
            var u = Lampa.Storage.get('lampac_unic_id', '');
            if (u) return String(u);
        } catch (e) {}
        try {
            if (window.AndroidJS && typeof window.AndroidJS.get === 'function')
                return String(window.AndroidJS.get('qdl_device_uid') || '');
        } catch (e) {}
        return '';
    }

    // 🔴 Гейт по API обязателен: тот же req() ходит и в /cub/-прокси, который склеивает
    // upstream-URL вместе с нашей строкой запроса дословно — лишний параметр уехал бы наружу.
    function withUid(url) {
        try {
            if (String(url).indexOf(API) !== 0) return url;
            if (/[?&]uid=/.test(url)) return url;
            var u = qdlUid();
            if (!u) return url;
            return url + (url.indexOf('?') >= 0 ? '&' : '?') + 'uid=' + encodeURIComponent(u);
        } catch (e) { return url; }
    }

    // ───────── Права на скрытые разделы (qdl 2.54) ─────────
    // Кому показывать «D1versy Live» (эфир) и «D1versy Rec» (записи), решает СЕРВЕР по айди
    // устройства — Modules/QbitDownload/Perms.cs, выдача прав в админке /admin/d1v.
    // 🔴 Кеш в Lampa.Storage нужен ТОЛЬКО чтобы пункт меню не мигал на старте: правом он не является,
    // подделка localStorage даёт пустой экран — сервер всё равно отвечает 404 на qdl/live/*.
    var qdlPerms = null;   // {live:bool, rec:bool} — свежий ответ сервера
    var qdlCard = null;    // {uid,name,platform,client} — для футера «Уведомлений»

    function qdlAllowed(feature) {
        var p = qdlPerms;
        if (!p) { try { p = Lampa.Storage.get('qdl_features', null); } catch (e) { p = null; } }
        return !!(p && p[feature]);
    }

    function loadFeatures(done) {
        req(API + '/qdl/features', function (r) {
            if (r && r.features) {
                qdlPerms = r.features;
                qdlCard = r;
                try { Lampa.Storage.set('qdl_features', r.features); } catch (e) {}
            }
            // Настройки живого прогресса (qdl 2.93) — отдельным ключом, НЕ внутри features:
            // тот объект читается qdlAllowed как булева карта прав, числа в нём стали бы «правом».
            if (r && r.progress) setProgressConf(r.progress);
            if (done) done();
        }, function () { if (done) done(); });
    }

    // Экран без права. Сюда можно попасть только дип-линком или из меню, отрисованного по
    // протухшему кешу прав: сервер всё равно ответит 404, но пустой экран читался бы как поломка.
    function denySection() {
        try { Lampa.Noty.show('Раздел недоступен на этом устройстве'); } catch (e) {}
        try { setTimeout(function () { Lampa.Activity.backward(); }, 100); } catch (e) {}
    }

    function req(url, cb, err) {
        url = withUid(url);
        try {
            var net = new Lampa.Reguest();
            net.timeout(45000);
            net.silent(url, function (json) { cb(json); }, function (a, c) { if (err) err(a, c); });
        } catch (e) {
            fetch(url).then(function (r) { return r.json(); }).then(cb).catch(function () { if (err) err(); });
        }
    }

    function post(url, data, cb, err) {
        // 🔴 uid обязателен и здесь: RequestInfo.getuid читает ТОЛЬКО query, а мутации коллекций
        // (colPost) с 2.67 закрыты правом «Управление» — без этой строки сервер отказал бы 403
        // даже устройству с грантом. withUid сам гейтится по префиксу API, чужие URL не трогает.
        url = withUid(url);
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
            // Сетка эфира — жёсткие ДВЕ колонки (4 камеры = 2x2). Ширину колонки считает
            // fitQuad() в рантайме: квадрат обязан ВЛЕЗАТЬ в экран целиком, а вычислить это
            // в CSS нельзя — над сеткой стоят шапка Lampa и наша кнопка, и их высота
            // зависит от корневого em, который Lampa пересчитывает под ширину экрана.
            // На телефоне корневой em клампится, две колонки превращаются в кашу — там одна.
            // Панель занимает ВЕСЬ экран (требование владельца): две колонки во всю ширину,
            // высоту ряда задаёт fitQuad так, чтобы два ряда легли ровно во вьюпорт. Кадр при
            // этом чуть подрезается по вертикали (object-fit:cover) — область контента у Lampa
            // на 57 px ниже 16:9, и без подрезки пришлось бы оставлять поля по бокам.
            // 🔴 align-items:start — плитка НЕ растягивается по высоте ряда. Иначе любой ряд,
            // ставший выше содержимого, растянул бы плитку, а картинка внутри (object-fit:contain)
            // получила бы чёрные поля сверху и снизу — жалоба владельца с айфона.
            '.qdl-watch-grid{display:grid;grid-template-columns:repeat(2,1fr);justify-content:center;align-items:start;gap:.25em;padding:0;overflow:hidden}' +
            // 🔴 На телефоне размер плитки задаётся ОТ ШИРИНЫ ЭКРАНА, а не от контейнера и не
            // через aspect-ratio. У владельца на айфоне плитки выходили втрое выше нужного, и
            // кадр висел в чёрном поле по центру (скриншоты 01.09.2026); в эмуляции телефона —
            // хоть с iPhone-UA, хоть с d1vision_platform=ios — раскладка была правильной, то есть
            // виновата среда самого приложения. Явные width/height в vw снимают вопрос целиком:
            // они не зависят ни от ширины ленты, ни от inline-стилей fitQuad, ни от поддержки
            // aspect-ratio движком.
            // Медиазапрос оставлен страховкой для обычного узкого браузера. На приложении
            // iPhone он НЕ срабатывает (вьюпорт там шире 600 px) — там раскладку задаёт
            // fitQuad инлайном по платформе, см. комментарий в нём.
            '@media (max-width:600px){' +
              '.qdl-watch-grid,.qdl-watch-off{grid-template-columns:1fr;gap:.5em;padding:0 .5em;justify-content:start;grid-auto-rows:auto}' +
            '}' +
            // Камеры не в эфире — отдельным блоком под живой четвёркой (возврат поведения
            // до 2.95: «снизу чтобы были даже неактивные отключённые стримы»).
            '.qdl-watch-offtitle{padding:1.3em 1.4em .4em;font-size:1.3em;font-weight:600;opacity:.75}' +
            '.qdl-watch-off{display:grid;grid-template-columns:repeat(2,1fr);justify-content:center;align-items:start;gap:1em;padding:0 1.4em 2em}' +

            // 🔴 Пропорцию держит САМА плитка, а не картинка внутри: так высота не зависит
            // ни от загрузки кадра, ни от того, как движок разрешает height:100% у потомка.
            // В режиме полноэкранной панели пропорцию задаёт ряд (см. --fit ниже).
            '.qdl-watch-tile{position:relative;border-radius:.8em;overflow:hidden;background:#111;min-width:0;aspect-ratio:16/9}' +
            // 🔴 Фокус рисуем рамкой ВНУТРИ плитки, отдельным узлом поверх видео. Прежние
            // box-shadow + scale(1.04) на полноэкранной панели не годятся: тень у плитки,
            // прижатой к краю экрана, обрезается, а масштаб выталкивает её за вьюпорт —
            // со стороны это выглядит как «фокус не виден, навигации нет» (жалоба владельца).
            '.qdl-watch-ring{position:absolute;left:0;top:0;right:0;bottom:0;border:.25em solid transparent;border-radius:inherit;pointer-events:none;z-index:3}' +
            '.qdl-watch-tile.focus{z-index:1}' +
            '.qdl-watch-tile.focus .qdl-watch-ring{border-color:#fff}' +
            // кадр-подложка задаёт высоту плитки и виден, пока поток не поднялся
            // 🔴 contain, а НЕ cover: владелец про оригинальный Live View — «там они не
            // обрезаются». У регистратора карточка ровно 16:9 и `object-fit:contain`
            // (HlsPlayer.tsx), кадр виден целиком. Мы держим то же самое: fitQuad подбирает
            // и ширину колонки, и высоту ряда так, чтобы плитка осталась 16:9.
            '.qdl-watch-frame{display:block;width:100%;height:100%;object-fit:contain;background:#0a0a0a}' +
            '.qdl-watch-grid--fit .qdl-watch-tile{aspect-ratio:auto;border-radius:0}' +
            // 🔴 Фон ПРОЗРАЧНЫЙ: под видео лежит кадр камеры (.qdl-watch-frame), и пока поток
            // не пошёл, зритель должен видеть именно его, а не серо-чёрный квадрат
            // (требование владельца). Чёрная заливка перекрывала кадр до первого декодированного
            // кадра — это и был «серый квадратик».
            '.qdl-watch-tile video{position:absolute;left:0;top:0;width:100%;height:100%;object-fit:contain;background:transparent}' +
            // Плашка состояния — в угол и мелко: она сообщает, а не занавешивает кадр.
            '.qdl-watch-note{position:absolute;left:.6em;top:.6em;padding:.15em .6em;border-radius:.35em;background:rgba(0,0,0,.55);font-size:.95em;opacity:.9;pointer-events:none;z-index:2}' +
            '.qdl-watch-note:empty{display:none}' +
            '.qdl-watch-bar{position:absolute;left:0;right:0;bottom:0;padding:.6em .8em;background:linear-gradient(0deg,rgba(0,0,0,.85),rgba(0,0,0,0));display:flex;align-items:center;gap:.6em;z-index:2}' +
            '.qdl-watch-name{flex:1;min-width:0;font-size:1.25em;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
            // Фулл вью одной камеры — та же плитка, растянутая на экран. Fullscreen API тут не
            // годится: у релизного Android-клиента WebChromeClient ставится только в DEBUG,
            // то есть requestFullscreen() мёртв (SysView.kt).
            '.qdl-watch-tile--full{position:fixed;left:0;top:0;width:100%;height:100%;z-index:900;border-radius:0;transform:none}' +
            // 🔴 z-index ОБЯЗАН быть переуказан здесь: у правила `.qdl-watch-tile.focus{z-index:1}`
            // специфичность выше, чем у одиночного `--full`, и развёрнутая камера уезжала под
            // сетку — на экране получалась мозаика ПОВЕРХ полноэкранного видео. Ловится только
            // после переключения стрелкой: при первом открытии Lampa успевает снять .focus сама.
            '.qdl-watch-tile--full.focus{box-shadow:none;transform:none;z-index:900}' +
            // и рамку фокуса на полный экран не рисуем — это была бы белая рамка вокруг всего экрана
            '.qdl-watch-tile--full.focus .qdl-watch-ring{border-color:transparent}' +
            '.qdl-watch-tile--full video{object-fit:contain}' +
            '.qdl-watch-tile--full .qdl-watch-frame{width:100%;height:100%;aspect-ratio:auto;object-fit:contain}' +
            // ── Detection: лента скриншотов детектора ──
            '.qdl-det-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:.8em;padding:.4em 1.4em 2em}' +
            '@media (max-width:600px){.qdl-det-grid{grid-template-columns:repeat(2,1fr)}}' +
            '.qdl-det-day{grid-column:1/-1;padding:.9em .2em .1em;font-size:1.35em;font-weight:600;opacity:.85}' +
            '.qdl-det-card{position:relative;border-radius:.7em;overflow:hidden;background:#111;transition:transform .1s}' +
            '.qdl-det-card.focus{box-shadow:0 0 0 .22em #fff;transform:scale(1.04);z-index:1}' +
            '.qdl-det-img{display:block;width:100%;aspect-ratio:16/9;object-fit:cover;background:#0a0a0a}' +
            '.qdl-det-type{position:absolute;left:.5em;top:.5em;padding:.12em .5em;border-radius:.35em;font-size:.9em;font-weight:700;color:#fff;background:rgba(200,30,30,.9)}' +
            '.qdl-det-type--motion{background:rgba(210,140,20,.92)}' +
            '.qdl-det-bar{position:absolute;left:0;right:0;bottom:0;padding:.5em .6em;background:linear-gradient(0deg,rgba(0,0,0,.85),rgba(0,0,0,0));display:flex;align-items:flex-end;gap:.5em}' +
            '.qdl-det-name{flex:1;min-width:0;font-size:1.05em;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
            '.qdl-det-time{font-size:1em;opacity:.85;flex:none}' +
            '.qdl-det-view{position:fixed;left:0;top:0;width:100%;height:100%;z-index:900;background:#000;display:flex;align-items:center;justify-content:center}' +
            '.qdl-det-view img{max-width:100%;max-height:100%;object-fit:contain}' +
            '.qdl-det-head{position:absolute;left:0;right:0;top:0;padding:1em 1.4em;font-size:1.3em;background:linear-gradient(180deg,rgba(0,0,0,.85),rgba(0,0,0,0))}' +
            '.qdl-det-foot{position:absolute;left:0;right:0;bottom:0;padding:1em 1.4em;text-align:center;font-size:1.1em;opacity:.85;background:linear-gradient(0deg,rgba(0,0,0,.85),rgba(0,0,0,0))}' +
            // фокус пульта в наших списках/кнопках: Lampa вешает класс .focus только на ТВ/десктопе,
            // а генерического .selector.focus в её CSS нет — без этих правил фокус невидим
            '.qdl-row-focus{transition:box-shadow .1s}' +
            '.qdl-row-focus.focus{box-shadow:0 0 0 .2em #fff}' +
            // экран серий: текущая (продолжаемая) серия подсвечена синим
            '.qdl-ep--cur{background:rgba(25,100,210,.22) !important}' +
            // серия ещё качается: приглушаем, но строку ОСТАВЛЯЕМ — иначе сериал выглядит
            // короче, чем есть, и непонятно, качается ли что-то (решение владельца 01.09.2026)
            '.qdl-ep--wait .qdl-ep-name,.qdl-ep--wait .qdl-ep-num{opacity:.45}' +
            '.qdl-ep--wait .qdl-ep-dl{opacity:.9;color:#ffd166}' +
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
            '.qdl-noti-head-badge{position:absolute;top:-0.1em;right:-0.1em;min-width:1.5em;height:1.5em;padding:0 0.35em;box-sizing:border-box;background:#d33;color:#fff;border:0.12em solid #fff;border-radius:1em;font-size:0.62em;line-height:1.26em;font-weight:700;text-align:center}' +
            // Автопилот jut.su. Выключен — приглушённый контур; включён — зелёный, как «Смотреть».
            // Кнопка стоит между заголовком и значками, поэтому нужны свои поля.
            // Detection в шапке: размер svg ОБЯЗАН быть задан явно — без правила ниже
            // иконка схлопывается в нулевую ширину и кнопка становится невидимой
            // (ровно это и случилось на первой выкатке: узел в DOM есть, а на экране пусто).
            '.qdl-det-btn{opacity:.85;transition:opacity .2s,transform .2s}' +
            '.qdl-det-btn svg{width:1.5em;height:1.5em;display:block}' +
            '.qdl-det-btn.focus{opacity:1;transform:scale(1.08)}' +
            '.qdl-jut-skip{margin:0 .2em 0 .8em;opacity:.45;transition:opacity .2s,color .2s,transform .2s}' +
            '.qdl-jut-skip svg{width:1.5em;height:1.5em;display:block}' +
            '.qdl-jut-skip.qdl-jut-skip--on{opacity:1;color:#19b531}' +
            '.qdl-jut-skip.focus{opacity:1;transform:scale(1.08)}' +
            // Экран поиска jut.su: поле сверху (не впритык к краю) + лента «Недавнее» под ним
            '.qdl-jut-search-wrap{padding:2em 1.5em 0;display:flex;justify-content:center}' +
            '.qdl-jut-search-field{display:flex;align-items:center;gap:.7em;width:70%;max-width:44em;padding:.85em 1.2em;' +
            'background:rgba(255,255,255,.09);border:0.12em solid rgba(255,255,255,.16);border-radius:.7em;font-size:1.25em}' +
            '.qdl-jut-search-field.focus{background:rgba(255,255,255,.18);border-color:#fff;transform:scale(1.01)}' +
            '.qdl-jut-search-field input{flex:1;min-width:0;background:transparent;border:0;outline:0;color:inherit;font-size:1em;font-family:inherit}' +
            '.qdl-jut-search-hint{opacity:.55}' +
            '.qdl-jut-recent-title{padding:1.4em 1.5em .4em;font-size:1.3em;opacity:.7}' +
            '@media screen and (max-width:580px){.qdl-jut-search-wrap{padding:1.2em 1em 0}.qdl-jut-search-field{width:100%}}';
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
                // ⚠️ Поле сервера тоже правим: иначе перерисовка из этого же объекта (Activity.replace)
                // вернула бы прежний URL, посчитанный ДО того, как постер лёг на диск (§BV).
                t.posterUrl = '/qdl/poster?hash=' + t.hash;
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

    // ───────── Склеенная карточка сезонов (сервер, qdl 2.78) ─────────
    // /qdl/list отдаёт сезоны одного сериала ОДНОЙ карточкой и кладёт в неё parts — раздачи
    // группы по сезонам. Просмотру знать про части не нужно вовсе: /qdl/episodes по хешу
    // карточки уже отдаёт общий плейлист, и у каждой серии свой hash (механика доноров).
    // Части нужны ровно УПРАВЛЕНИЮ: удалять, транскодировать, подписываться и запоминать
    // озвучку умеет только конкретная раздача.
    function cardParts(t) {
        var p = t && t.parts;
        return (p && p.length > 1) ? p : null;
    }

    // подпись части в меню: сезон, если сервер его разобрал, иначе укороченное имя раздачи
    function partLabel(p) {
        if (p && p.season > 0) return 'Сезон ' + p.season + (p.local ? ' · MP4' : '');
        var n = String((p && p.name) || 'Раздача');
        return (n.length > 40 ? n.slice(0, 40) + '…' : n) + (p && p.local ? ' · MP4' : '');
    }

    // часть как самостоятельная карточка: мета и постер общие, всё остальное — своё
    function partItem(t, p) {
        return {
            hash: p.hash, name: p.name, meta: t.meta, progress: p.progress, state: p.state,
            local: p.local, watched: p.watched, season: p.season
        };
    }

    // Действие, которое умеет только КОНКРЕТНАЯ раздача. На обычной карточке выполняется
    // сразу, на склеенной — сперва спрашиваем сезон. allTitle (если задан) добавляет пункт
    // «ко всем частям»: run(null, parts).
    // back — контроллер, которому вернуть управление по «Отмена»/Back (qdl 2.108: с карточки
    // каталога это 'items_line', см. onCardMenu); по умолчанию 'content', как было.
    function withPart(t, title, run, allTitle, back) {
        back = back || 'content';
        var ps = cardParts(t);
        if (!ps) { run(t, null); return; }
        var items = ps.map(function (p) {
            return { title: partLabel(p) + (p.watched ? '  🔔' : ''), subtitle: liveSize(p.size), part: p };
        });
        if (allTitle) items.unshift({ title: allTitle, all: true });
        items.push({ title: 'Отмена' });
        Lampa.Select.show({
            title: title,
            items: items,
            onSelect: function (b) {
                if (b.all) run(null, ps);
                else if (b.part) run(partItem(t, b.part), null);
                else Lampa.Controller.toggle(back);
            },
            onBack: function () { Lampa.Controller.toggle(back); }
        });
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

    // Карточка «Загрузок», скачанная из раздела XSMART: сервер кладёт {xsmart:{cat,id,ref,watch}}
    // в /qdl/list (XsmartDecorateListItem). Третий контур слежения — свой файл подписок и свои
    // ручки; с торрентным watch.json он не пересекается (пояс изоляции).
    function isXsmart(t) {
        return !!(t && t.xsmart && t.xsmart.id);
    }

    // Режим слежения xsmart-карточки — те же три состояния и тот же фолбэк, что у jutMode.
    function xsMode(t) {
        var m = t && t.xsmart && t.xsmart.watch;
        if (m === 'off' || m === 'notify' || m === 'grab') return m;
        return (t && t.watched) ? 'grab' : 'off';
    }

    // Показывать ли пункт слежения. Подписка у XSMART бывает только на сериал (сервер отвечает
    // NOT_FOUND «Следить можно только за сериалом»), тип берём из меты — XsmartEnsureMeta пишет
    // media_type по t.series.
    // ⚠️ Вторая половина условия обязательна: без неё кривая или старая мета оставила бы уже
    // существующую подписку вообще без способа её снять.
    function xsCanWatch(t) {
        return isXsmart(t) && ((t.meta && t.meta.media_type === 'tv') || xsMode(t) !== 'off');
    }

    function xsErrText(r) {
        var m = r && (r.message || r.error);
        return m || 'XSMART недоступен';
    }

    // ── «Жду следующий сезон» (qdl 2.79) ──────────────────────────────────────
    // Маркер живёт на СЕРИАЛЕ (TMDB id), а не на раздаче: сервер отдаёт его в карточке
    // /qdl/list полем seasonWait = {from}. 0 = маркера нет.
    function seasonWaitFrom(t) {
        var w = t && t.seasonWait;
        return (w && w.from > 0) ? w.from : 0;
    }

    // 🔴 Гейт по МЕТЕ, а не по t.local. Тот же гейт, что у торрентного слежения
    // (`!t.local && t.state !== 'local'`), дважды прятал пункт подписки у карточек, которые
    // всегда local — у jut.su в 2.28 и у XSMART в 2.76. Здесь маркер вообще не про раздачу:
    // сериал, целиком транскодированный в MP4 (торрента уже нет), ждать новый сезон может
    // и должен. Из контура выпадают только jut.su и XSMART — у них своё слежение за сезоном.
    function canSeasonWait(t) {
        return !!(t && t.meta && t.meta.media_type === 'tv') && !isJut(t) && !isXsmart(t);
    }

    function seasonWaitToggle(item, done) {
        var on = seasonWaitFrom(item);
        if (on) {
            req(API + '/qdl/season/watch/remove?hash=' + item.hash, function () {
                item.seasonWait = null;
                Lampa.Noty.show('Ожидание сезона снято');
                if (done) done();
            }, function () { Lampa.Noty.show('Не удалось снять ожидание'); });
        }
        else {
            req(API + '/qdl/season/watch?hash=' + item.hash, function (r) {
                if (r && r.success) {
                    item.seasonWait = { from: r.from };
                    Lampa.Noty.show('✓ Жду ' + r.from + ' сезон — скачаю сам, как выйдет');
                }
                else Lampa.Noty.show('Не вышло — у карточки нет данных TMDB');
                if (done) done();
            }, function () { Lampa.Noty.show('Не удалось включить ожидание'); });
        }
    }

    // Смена состояния подписки XSMART. from — текущий режим, want — желаемый ('off'|'notify'|'grab').
    //
    // ⚠️ Существующей подписке режим меняем через /qdl/xsmart/watch/mode, а НЕ повторной
    // подпиской: /qdl/xsmart/watch сбрасывает baseline на текущее состояние источника, и серия,
    // вышедшая между тиком и нажатием, ушла бы в baseline — в режиме «качаю» её уже никто
    // не скачает. Плюс /watch/mode не ходит в сеть и работает, когда XSMART лежит.
    function xsWatchSet(cat, id, from, want, done) {
        var q = 'cat=' + encodeURIComponent(cat) + '&id=' + encodeURIComponent(id);
        var grabFlag = want === 'grab' ? 1 : 0;
        var u = want === 'off' ? (API + '/qdl/xsmart/watch/remove?' + q)
              : from === 'off' ? (API + '/qdl/xsmart/watch?' + q + '&autoGrab=' + grabFlag)
              : (API + '/qdl/xsmart/watch/mode?' + q + '&autoGrab=' + grabFlag);
        var viaMode = u.indexOf('/watch/mode') !== -1;
        // подписки могло уже не быть (NOT_WATCHED) — добираем полной подпиской, режим проставится
        var repair = function () { xsWatchSet(cat, id, 'off', want, done); };

        req(u, function (r) {
            if (want !== 'off' && !(r && r.ok)) {
                if (viaMode) { repair(); return; }
                Lampa.Noty.show(xsErrText(r));
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

    // 1×1 прозрачный GIF. «Постера нет» — ШТАТНЫЙ случай (строка DIAG «Поиск раздач», вычищенная
    // раздача, тайтл без обложки), а img_broken.svg читается как поломка приложения. У <img> уже
    // есть background:#222, поэтому прозрачный пиксель поверх него даёт нейтральную плитку.
    // src='' резолвится в URL документа и сам стреляет error, а <img> вовсе без src на части
    // ТВ-движков всё равно рисует битую иконку — поэтому именно data-URI.
    // ⚠️ Объявлен ВЫШЕ posterUrl намеренно: тот его использует, полагаться на var-hoisting не надо.
    var PX1 = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==';

    // Постер карточки «Загрузок» (грид, обложка коллекции, экран qdl_card). Решает СЕРВЕР
    // (item.posterUrl, qdl 2.47): только он знает, лежит ли файл на диске СЕЙЧАС, есть ли
    // апгрейженная jut-обложка и какое у неё поколение. Прежний путь спрашивал has_poster, а тот
    // считается по кешированному листингу img/ — постер, доехавший позже, не показывался до
    // рестарта контейнера (§BV, боевой случай 15.08.2026).
    // ⚠️ Фолбэк на has_poster нужен на время выкатки (клиент 2.47 против сервера 2.46) — не
    // убирать раньше 2.48.
    function posterUrl(item) {
        if (item && item.posterUrl) return API + item.posterUrl;
        if (item && item.has_poster) return API + '/qdl/poster?hash=' + item.hash;
        return PX1;
    }

    // Фон под фокусом карточки — ровно то, что делает родной экран Lampa:
    //     Card.create() → hover:focus → onFocus → Background.change(Utils.cardImgBackground(data))
    // Всё остальное живёт ВНУТРИ Background.change и дублировать это у себя не надо: выключенный
    // фон (Storage 'background'), light_version, дебаунс 1000 мс, расчёт палитры и кроссфейд.
    // Utils.cardImgBackground здесь не зовём осознанно: у наших карточек на тайтл ровно ОДНА
    // картинка, и обе её ветки («простой фон» / «изображение») вернули бы один и тот же URL.
    // ⚠️ jut.su красим ПОСТЕРОМ, а не backdrop'ом: /qdl/jut/backdrop для НЕ открытого тайтла
    // лезет на jut.su за страницей тайтла и качает кадр 2560×1440 — прогулка фокусом по каталогу
    // стала бы десятками походов на источник. Постер уже в кеше клиента (ленивая загрузка).
    function bgFocus(url) {
        if (!url || url === PX1) return;   // прозрачный пиксель-заглушка стёр бы фон в пустоту
        try { Lampa.Background.change(url); } catch (e) {}
    }

    // ───────── Бухгалтерия фокуса: «где я был» и возврат ровно туда же (§CO) ─────────
    //
    // Наши экраны помнят текущую карточку в last, чтобы вернуть фокус после селектбокса,
    // плеера или левого меню. Писался он ТОЛЬКО по hover:focus — а это событие ПУЛЬТА:
    //  • палец: touchstart → hover:touch (+ таймер 800 мс → hover:long), hover:focus НЕ приходит вовсе;
    //  • мышь десктопа (navigation_type=mouse): mouseenter → trigger_mouseenter → ТОЛЬКО hover:hover
    //    (так же ресинкает фокус наш desktop.js после глайда колесом).
    // Значит на мыши и пальце last оставался пуст, и любой Controller.toggle('content') уводил
    // collectionFocus на ПЕРВЫЙ .selector — лента прыгала в начало (жалоба владельца по долгому
    // нажатию в «Загрузках»). Сама Lampa свои списки пишет именно парой обработчиков — список
    // торрентов и папки настроек в app.min.js; делаем так же.
    //
    // ⚠️ Navigator.focused, а НЕ focus: focus триггерит hover:focus и сам утащит скролл.
    // ⚠️ Вешать ОТДЕЛЬНОЙ строкой, не сливая с bgFocus: на таче родная Lampa фон не красит,
    //    а touchstart во время пальцевого скролла красил бы фоном случайные карточки.
    // Возвращает сам элемент, чтобы вызов на месте был одной строкой:
    //     el.on('hover:touch hover:hover', function () { last = markLast(el); });
    function markLast(el) {
        try { Navigator.focused(el[0]); } catch (e) {}
        return el[0];
    }

    // Элемент сейчас в кадре? Только по вертикали: все наши экраны — вертикальные ленты.
    function onScreen(el) {
        try {
            var r = el.getBoundingClientRect();
            var h = window.innerHeight || (document.documentElement && document.documentElement.clientHeight) || 0;
            return !!(r.width || r.height) && r.bottom > 0 && r.top < h;
        } catch (e) { return false; }
    }

    // Возврат управления экрану: фокус на прежний элемент И ЛЕНТА НЕ ДВИГАЕТСЯ.
    // Штатный обработчик hover:focus зовёт scroll.update(el, true), то есть ЦЕНТРИРУЕТ элемент,
    // и при возврате это лишний рывок на полэкрана: карточка и так перед глазами (требование
    // владельца — «фокус оставался на том же месте»). Глушим update ровно на время фокусировки:
    // это ОДНО место вместо правки двух десятков обработчиков.
    // 🔴 Подмена безопасна именно потому, что цепочка Navigator.focus → Controller.focus →
    //    Utils.trigger('hover:focus') идёт СИНХРОННО, без setTimeout — чужой вызов в окно не попадёт.
    // ⚠️ Элемент ВНЕ экрана центрируем как раньше (палец начал скролл с карточки, которая давно
    //    уехала): иначе фокус остался бы там, куда не видно.
    function focusBack(scroll, last) {
        Lampa.Controller.collectionSet(scroll.render());
        if (!last || !onScreen(last)) {
            Lampa.Controller.collectionFocus(last || false, scroll.render());
            return;
        }
        var upd = scroll.update;
        scroll.update = function () {};
        try { Lampa.Controller.collectionFocus(last, scroll.render()); }
        finally { scroll.update = upd; }
    }

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

    // ───────── Живой прогресс загрузок (qdl 2.93) ─────────
    // Один модуль-уровневый таймер на весь плагин + реестр подписчиков. Экраны подписываются
    // в start() и ОТПИСЫВАЮТСЯ в pause()/stop()/destroy().
    //
    // 🔴 Почему отписка в pause() обязательна. Lampa при навигации ВПЕРЁД не зовёт destroy():
    // компонент висит в стеке до pages_save_total (та же мина, что у сетки камер — см.
    // комментарий у ComponentLiveWatch.startTimer). Без отписки три сложенных копии «Загрузок»
    // держали бы опрос вечно. Ключ реестра — токен, а не сам компонент: один инстанс
    // стартует/паузится многократно, и протухшее замыкание не должно воскресать.
    //
    // Требование владельца — «опрашивать только пока идут активные загрузки»:
    //   active > 0            → быстрый опрос (poll, по умолчанию 5 с)
    //   active = 0, pending>0 → медленный пульс (idle, 30 с) с бюджетом (10 мин), потом молчим
    //   ничего не качается    → таймера НЕТ ВОВСЕ
    // Пробуждение после молчания: вход на экран-подписчик, пуш 'qdl_noti' по сокету, выход из
    // плеера и страховочный тик loadFeatures.
    //
    // 🔴 У pgGet ТРИ исхода, а не два. Спутать «данных нет» с «готово» — это молча играющий
    // недокачанный фильм; спутать наоборот — вечное «дождитесь загрузки» на скачанном.
    var DONE = 0.999;                  // тот же порог, что на сервере (Progress.cs ProgressDone)
    var _pgSubs = {};                  // token -> {hash, fn}
    var _pgSeq = 0;
    var _pgTimer = null;
    var _pgState = null;               // {ok, stamp, active, pending, byHash:{}, files:{}}
    var _pgConf = { poll: 5, idle: 30, budget: 10, block: true };
    var _pgInterval = 0;               // текущий интервал таймера, мс
    var _pgIdleSince = 0;              // когда ушли в медленный пульс (0 = не в нём)
    var _pgBusy = false;               // in-flight гард (как notiPollBusy)
    var _pgFails = 0;
    var _pgNet = null;

    function setProgressConf(c) {
        if (!c) return;
        if (typeof c.poll === 'number') _pgConf.poll = c.poll;
        if (typeof c.idle === 'number') _pgConf.idle = c.idle;
        if (typeof c.budget === 'number') _pgConf.budget = c.budget;
        if (typeof c.block === 'boolean') _pgConf.block = c.block;
        try { Lampa.Storage.set('qdl_progress_cfg', _pgConf); } catch (e) {}
    }
    (function () {   // на старте — из кеша, чтобы первый экран не ждал ответа /qdl/features
        try { var c = Lampa.Storage.get('qdl_progress_cfg', null); if (c) setProgressConf(c); } catch (e) {}
    })();

    /// Жёсткая блокировка недокачанного включена? Киллсвитч partialPlayBlock с сервера.
    function pgBlockEnabled() { return _pgConf.block !== false; }

    function pgHasSubs() { for (var k in _pgSubs) if (_pgSubs.hasOwnProperty(k)) return true; return false; }

    // hash последнего подписчика, которому нужен per-file прогресс (экран серий/карточка).
    // Один за тик: сводка общая, а files сервер отдаёт только для запрошенной раздачи.
    function pgWantedHash() {
        var h = null;
        for (var k in _pgSubs) if (_pgSubs.hasOwnProperty(k) && _pgSubs[k].hash) h = _pgSubs[k].hash;
        return h;
    }

    // 🔴 Таймер здесь НЕ заводится — только один немедленный тик. Интервал создаёт pgApply,
    // когда УЗНАЕТ, что качать есть что: это и есть буквальное «опрашивать только пока идут
    // активные загрузки». Побочно это же снимает вечный таймер там, где сервер не отвечает.
    // Провалившийся первый тик подберёт страховочный pgKick из 60-секундного тика loadFeatures.
    function pgSubscribe(hash, fn) {
        var token = 'p' + (++_pgSeq);
        _pgSubs[token] = { hash: hash || null, fn: fn || null };
        if (_pgConf.poll > 0) { _pgIdleSince = 0; pgTick(); }
        return token;
    }

    function pgUnsubscribe(token) {
        if (!token || !_pgSubs[token]) return;
        delete _pgSubs[token];
        // 🔴 _pgState НЕ выбрасываем: возврат на экран обязан рисоваться мгновенно по последнему
        // известному, а не «пусто → через секунду цифра».
        if (!pgHasSubs()) pgStopTimer();
    }

    function pgStopTimer() {
        if (_pgTimer) { clearInterval(_pgTimer); _pgTimer = null; }
        _pgInterval = 0;
        try { if (_pgNet) _pgNet.clear(); } catch (e) {}
    }

    function pgReschedule(ms) {
        if (!pgHasSubs() || !(ms > 0)) { pgStopTimer(); return; }
        if (_pgTimer && _pgInterval === ms) return;   // тот же интервал — таймер не пересоздаём
        if (_pgTimer) clearInterval(_pgTimer);
        _pgInterval = ms;
        _pgTimer = setInterval(pgTick, ms);
    }

    function pgKick() {
        if (!pgHasSubs() || _pgConf.poll <= 0) return;
        _pgIdleSince = 0;
        pgTick();   // интервал (или его отсутствие) решит pgApply по ответу
    }

    function pgTick() {
        if (_pgBusy || _pgConf.poll <= 0) return;
        // Плеер открыт оверлеем (активность остаётся «активной») — экран под ним не обновляем.
        // Тот же гард, что у сетки камер: плейлист уже передан плееру, менять его на лету нельзя.
        try { if (Lampa.Player.opened && Lampa.Player.opened()) return; } catch (e) {}

        var h = pgWantedHash();
        var url = API + '/qdl/progress' + (h ? '?hash=' + h : '');
        _pgBusy = true;
        try {
            if (!_pgNet) _pgNet = new Lampa.Reguest();
            _pgNet.silent(withUid(url),
                function (r) { _pgBusy = false; _pgFails = 0; pgApply(r); },
                function () {
                    _pgBusy = false;
                    _pgFails++;
                    // Ни одного тоста: поллер — фоновая мебель.
                    // Ретраим ТОЛЬКО если уже знали, что качать есть что. Иначе молчим до
                    // следующего пробуждения — долбить мёртвый сервер впустую незачем.
                    var busy = _pgState && (_pgState.active > 0 || _pgState.pending > 0);
                    if (!busy) { pgStopTimer(); return; }
                    if (_pgFails >= 3) pgReschedule(Math.min((_pgInterval || _pgConf.poll * 1000) * 2, 60000));
                });
        } catch (e) { _pgBusy = false; }
    }

    function pgApply(r) {
        // 🔴 ok:false — это «не знаю», а не «всё скачано». Вердикт гейтов не трогаем вовсе:
        // сюда попадают лёгший qBittorrent, киллсвитч на сервере и сервер-реплика.
        if (!r || r.ok === false) {
            if (r && r.poll === 0) { pgStopTimer(); _pgConf.poll = 0; }   // серверный киллсвитч
            return;
        }
        if (typeof r.poll === 'number' || typeof r.block === 'boolean') setProgressConf(r);

        var prev = _pgState;
        var byHash = {}, i;
        for (i = 0; i < (r.items || []).length; i++) {
            var it = r.items[i];
            if (it && it.h) byHash[it.h] = { p: typeof it.p === 'number' ? it.p : 0, s: it.s };
        }
        var st = {
            ok: true, stamp: r.stamp,
            active: r.active || 0, pending: r.pending || 0,
            byHash: byHash, files: r.files || {}
        };

        // Раздача пропала из items = докачалась. Сбрасываем ЕЁ кеш серий, чтобы приехали свежие
        // имена и замена донор→основная. ⚠️ Не dropEpCache() без аргумента: это убило бы
        // мемоизацию, ради которой она и заведена (путь «карточка → Смотреть» зовёт /qdl/episodes трижды).
        if (prev && prev.ok)
            for (var oh in prev.byHash)
                if (prev.byHash.hasOwnProperty(oh) && !byHash[oh]) dropEpCache(oh);

        var same = !!prev && prev.stamp === st.stamp && prev.stamp !== undefined;
        _pgState = st;

        // интервал: активные → быстро, стоящие → медленный пульс с бюджетом, пусто → молчим
        if (st.active > 0) {
            _pgIdleSince = 0;
            pgReschedule(_pgConf.poll * 1000);
        } else if (st.pending > 0 && _pgConf.idle > 0) {
            if (!_pgIdleSince) _pgIdleSince = Date.now();
            var over = _pgConf.budget > 0 && (Date.now() - _pgIdleSince) > _pgConf.budget * 60000;
            if (over) pgStopTimer(); else pgReschedule(_pgConf.idle * 1000);
        } else {
            _pgIdleSince = 0;
            pgStopTimer();   // ничего не качается — таймера нет вовсе (требование владельца)
        }

        if (same) return;   // состояние то же → DOM не трогаем, фокус пульта не рискует
        for (var k in _pgSubs)
            if (_pgSubs.hasOwnProperty(k) && _pgSubs[k].fn) { try { _pgSubs[k].fn(st); } catch (e) {} }
    }

    /// null = данных нет (fail-open); {p,s} = состояние; отсутствие хеша при ok:true = ГОТОВО.
    function pgGet(h) {
        if (!_pgState || !_pgState.ok || !h) return null;
        return _pgState.byHash[h] || { p: 1, s: 'done' };
    }

    /// Прогресс конкретного файла раздачи, 0..1. null = сервер про него не рассказывал.
    function pgFile(h, index) {
        if (!_pgState || !_pgState.ok || !h || !(index >= 0)) return null;
        var arr = _pgState.files && _pgState.files[h];
        if (!arr) return null;
        for (var i = 0; i < arr.length; i++)
            if (arr[i] && arr[i][0] === index) return arr[i][1];
        return null;
    }

    function pgStopAll() { _pgSubs = {}; pgStopTimer(); }
    function pgReset() { pgStopAll(); _pgState = null; _pgIdleSince = 0; _pgBusy = false; _pgFails = 0; _pgNet = null; }

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

    // ───────── История просмотров Lampa (favorite.history) ─────────
    // В бандле историю пишут только торрент-плеер Lampa и плагин «Онлайн», а наши экраны
    // («Загрузки», jut.su) играют через Lampa.Player.play напрямую — оттого раздел «История
    // просмотров» и ряд «Продолжить» на главной (Favorite.continues читает ту же history) были пусты.
    //
    // Момент записи — СТАРТ воспроизведения, как в upstream, а НЕ открытие экрана: иначе история
    // превратится в лог блужданий по карточкам. Своего сетевого кода не нужно: Favorite.add шлёт
    // событие 'add', его подхватывает серверный bookmark.js и уносит на /bookmark/add вместе с uid
    // устройства. limit 100 — то же число, что у самой Lampa.
    function noteHistory(card) {
        try {
            if (!card) return;
            var id = card.id;
            // id === 0 у сервера означает «TMDB id нет» (jut-маркеры, безымянные раздачи) —
            // такая карточка в истории бесполезна: открывать её будет нечем.
            if (id === null || id === undefined || id === '' || id === 0 || id === '0') return;
            Lampa.Favorite.add('history', historyCard(card), 100);
        } catch (e) {}
    }

    // 🔴 Нормализация под ожидания Lampa. Вход из истории роутер строит как
    // `method: data.original_name ? 'tv' : 'movie'`, а наш slimCard полей сериала не несёт вовсе
    // (только title/original_title) — сериал открылся бы как ФИЛЬМ, то есть ЧУЖИМ объектом TMDB:
    // у movie и tv id живут в разных пространствах. first_air_date заодно нужен Favorite.continues,
    // который по нему отличает сериал от фильма в ряду «Продолжить».
    // Клонируем: карточку нам дают из активности, портить чужой объект нельзя.
    function historyCard(card) {
        var c = {}, k;
        for (k in card) if (Object.prototype.hasOwnProperty.call(card, k)) c[k] = card[k];
        var isTv = c.media_type === 'tv' || !!c.number_of_seasons || !!c.number_of_episodes || !!c.first_air_date || !!c.name;
        if (isTv) {
            if (!c.name) c.name = c.title;
            if (!c.original_name) c.original_name = c.original_title;
            if (!c.first_air_date) c.first_air_date = c.release_date;
        }
        return c;
    }

    // Фолбэк, когда карточку в активность не положили (экран восстановлен из истории активностей):
    // ближайшая вверх по стеку ПОЛНАЯ карточка. Гейт по component==='full' обязателен — иначе
    // в историю уехала бы случайная карточка чужого экрана, просто оказавшегося ниже в стеке.
    function activityCard() {
        try {
            var list = Lampa.Activity.all() || [];
            for (var i = list.length - 1; i >= 0; i--) {
                var o = list[i] || {};
                if (o.component !== 'full') continue;
                var c = o.card || o.movie;
                if (c && c.id) return c;
            }
        } catch (e) {}
        return null;
    }

    // Карточка тайтла jut.su для истории. TMDB-идентификатора у аниме с jut.su нет (JutSuMatch
    // сопоставляет с Shikimori/MAL ради ПОСТЕРА, а не с TMDB), а на полной TMDB-карточке кнопки
    // «смотреть на jut.su» не существует — подставив чужую, мы завели бы вход из истории в тупик.
    // Слаг едет ВНУТРИ id: Utils.clearCard сохраняет только поля из белого списка card_fields,
    // произвольного jut_slug там нет, а id/title/img/source — есть.
    // 🔴 source:'jutsu' ОБЯЗАТЕЛЕН: сканер рекомендаций берёт из истории только карточки с source
    // из ['cub','tmdb'] — иначе каждый тайтл jut.su давал бы запрос к TMDB по несуществующему id.
    function jutHistoryCard(slug, title, pv) {
        if (!slug) return null;
        return { id: 'jut:' + slug, source: 'jutsu', title: title || slug, img: jutPosterUrl(slug, pv) };
    }
    function jutSlugFromCardId(id) {
        id = String(id === null || id === undefined ? '' : id);
        return id.indexOf('jut:') === 0 ? id.slice(4) : '';
    }

    // Вход в jut-карточку из «Истории просмотров». Грид избранного жёстко зовёт
    // router.call('full', card) — своего onEnter туда не подставить, поэтому ловим на
    // Lampa.Activity.push, куда этот вызов в итоге и приходит (Activity в бандле — ОДИН
    // объект-литерал, он же Lampa.Activity, то есть патч виден и внутренним вызовам).
    // Всё остальное проходит насквозь.
    function initHistoryRouting() {
        if (window.__qdl_history_routing) return;
        try {
            var push = Lampa.Activity.push;
            if (typeof push !== 'function') return;
            window.__qdl_history_routing = true;
            Lampa.Activity.push = function (object) {
                try {
                    var card = object && object.card;
                    if (object && object.component === 'full' && card && card.source === 'jutsu') {
                        var slug = jutSlugFromCardId(card.id);
                        if (slug) return push.call(Lampa.Activity, {
                            url: '', title: card.title || 'jut.su', component: 'jut_title', jut_slug: slug
                        });
                    }
                } catch (e) {}
                return push.apply(Lampa.Activity, arguments);
            };
        } catch (e) {}
    }

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

    // Сезон для ЗАГОЛОВКА на экране серий: только уверенный (поле сервера), у экстр — 0
    // («Дополнительно»). Гадать по имени здесь нельзя: epSeason по умолчанию отвечает «1»,
    // и после второго сезона появился бы заголовок «Сезон 1» над фильмом/OVA.
    function epHeadSeason(f) {
        if (f && f.season > 0) return f.season;
        return epKindRank(f) > 0 ? 0 : (f && f.epkey ? epSeason(f) : -1);
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
    // readyFn (qdl 2.93, необязательный) — фильтр «эту серию можно играть». Недокачанные не
    // должны попадать в плейлист вообще, иначе авто-переход внутри плеера прыгнет в дырявый файл.
    //
    // 🔴 Возврат несёт КАРТУ индексов свойством qdlMap[j] = индекс в исходном vids. Фильтровать
    // сам массив vids нельзя: `i` в comp.play(i), vids.indexOf(cur) и rows[i] — это индекс
    // в vids, и любое смещение развалило бы все три разом. Карта именно СВОЙСТВОМ массива, а не
    // объектом {list, map}: возврат buildPlaylist индексируют напрямую и код, и тесты.
    function buildPlaylist(hash, vids, audio, audioHash, readyFn) {
        var list = [], map = [];
        vids.forEach(function (f, i) {
            if (readyFn && !readyFn(f)) return;
            // 2.78: у чужой раздачи (донор охоты, соседний сезон склеенной карточки) свои id
            // дорожек — но своя запомненная озвучка у неё тоже есть, и она лучше дефолта
            var fh = srcHash(f, hash);
            var a = (audioHash == null || fh === audioHash) ? audio : (getAudioPref(fh) || null);
            var item = { title: baseName(f.name) + (f.source === 'donor' ? ' · врем.' : ''), url: streamUrl(srcHash(f, hash), f.index, a) };
            try { item.timeline = pickTimeline(hash, f); } catch (e) {}
            map.push(i);
            list.push(item);
        });
        list.qdlMap = map;
        return list;
    }

    // ───────── Готовность серии (qdl 2.93) ─────────
    // Владелец: «если это сериал, то только серии которые загружены можно смотреть».
    // Источник правды — живой per-file прогресс поллера, фолбэк — снимок из /qdl/episodes
    // (там progress лежит на каждой серии с самого начала, клиент его просто не читал).
    // Нет данных вовсе → серия играбельна: гейт обязан ошибаться в сторону «пустить».
    // ⚠️ srcHash обязателен: серия донора и серия соседнего сезона несут СВОЙ hash.
    function epProgress(f, hash) {
        if (!f) return -1;
        var live = pgFile(srcHash(f, hash), f.index);
        if (live !== null && typeof live === 'number') return live;
        return (typeof f.progress === 'number') ? f.progress : -1;
    }

    function epReady(f, hash) {
        var p = epProgress(f, hash);
        return p < 0 ? true : p >= DONE;
    }

    function epWaitNotice(f, hash) {
        var p = epProgress(f, hash);
        var pct = p >= 0 ? ' — ' + Math.round(p * 100) + '%' : '';
        try { Lampa.Noty.show('Серия ещё качается' + pct + '. Дождитесь загрузки'); } catch (e) {}
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

    // ───────── Свежесть рядов каталога: stale-while-revalidate (qdl 2.63) ─────────
    // Жалоба владельца: главная показывает вчерашний каталог. 🔴 Причина не в нашем серверном
    // кеше (он живёт минутами и свежий), а в клиентском: ряды лежат в IndexedDB со сроком
    // 2–7 СУТОК, и пока запись живая, клиент в сеть не ходит ВООБЩЕ. Activity.refresh() не
    // помогает — перечитывает те же записи.
    // Кеш нужен ради мгновенной отрисовки, поэтому решение такое: снимок рисуется как раньше,
    // а патч бандла (/*qdl-cut:swr*/ в AppReplace.cs) параллельно тихо дотягивает свежий ответ,
    // переписывает им запись кеша и шлёт 'request_revalidate'. Патч намеренно тупой — вся
    // политика здесь, чтобы менялась по воздуху (правка qdl.js доезжает без рестарта).
    //
    // Что делаем со свежим ответом: находим ЖИВОЙ ряд, построенный из того же URL, сверяем
    // состав И ПОРЯДОК и, если разъехалось, перестраиваем ряд на месте.
    // Чего НЕ делаем: не зовём заново Api.main (массив загрузчиков расходуемый — использованные
    // затираются в false, повторный вызов продублировал бы ряды) и не зовём Main.build (он не
    // идемпотентен, дописывает ряды). Поэтому НОВЫЙ ряд появится только на следующем старте.

    var SWR_MIN_INTERVAL = 10 * 60 * 1000;   // один URL не догоняем чаще раза в 10 минут
    var SWR_MIN_LIFE     = 60;               // только «длинные» кеши (life в МИНУТАХ) — это ряды каталога
    var SWR_BURST        = 12;               // и не больше 12 догонов за 10 с: первый экран 6 рядов + дозагрузка
    var SWR_BURST_WINDOW = 10000;
    var SWR_MAX_PENDING  = 32;
    var SWR_MAX_LINES    = 200;

    var swrLines = [];        // живые ряды
    var swrPending = {};      // url → свежий ответ, которому ещё не нашёлся ряд
    var swrLast = {};         // url → когда догоняли последний раз
    var swrBudget = [];       // таймстемпы догонов в окне SWR_BURST_WINDOW
    var swrRebuilding = false;   // ре-энтранси: перестройка сама шлёт событие 'line'

    function swrOff() {
        try { return !!Lampa.Storage.get('qdl_swr_off', false); } catch (e) { return false; }
    }

    // Предикат, который зовёт ПАТЧ БАНДЛА: «этот попавший в кеш запрос стоит догнать?».
    // Весь троттлинг живёт здесь — патч про него не знает и знать не должен.
    function swrGate(params) {
        try {
            if (swrOff()) return false;
            var u = params && params.url;
            if (!u || typeof u !== 'string') return false;
            if (params.post_data) return false;                                    // догоняем только GET
            if (!params.cache || !(params.cache.life >= SWR_MIN_LIFE)) return false;
            // 🔴 Исключать «всё, что начинается с API», НЕЛЬЗЯ: ряды каталога тоже идут через
            // наш сервер (/cub/tmdb./…, /tmdb/api/…) — такой фильтр отсекал ВСЕ ряды разом, и
            // фича молча не работала. Отсекаем только служебные ручки модуля.
            var path = u.indexOf(API) === 0 ? u.slice(API.length) : u;
            if (path.indexOf('/qdl/') === 0 || path.indexOf('/d1vision/') === 0) return false;
            var now = Date.now();
            if (swrLast[u] && now - swrLast[u] < SWR_MIN_INTERVAL) return false;
            while (swrBudget.length && now - swrBudget[0] > SWR_BURST_WINDOW) swrBudget.shift();
            if (swrBudget.length >= SWR_BURST) return false;
            if (Object.keys(swrLast).length > 400) swrLast = {};                   // долгая сессия: карту не растим вечно
            swrLast[u] = now;
            swrBudget.push(now);
            return true;
        } catch (e) { return false; }
    }

    // Ключ ряда — метка, которую патч поставил на тело ответа ДО того, как оно стало line.data.
    // В самом line.data.url лежит МЕТОД ('?sort=now_playing'), полного адреса там нет.
    function swrKey(line) {
        try { return (line && line.data && line.data.qdl_req) || null; } catch (e) { return null; }
    }

    function swrDrop(line) {
        for (var i = swrLines.length - 1; i >= 0; i--) if (swrLines[i].line === line) swrLines.splice(i, 1);
    }

    // Реестр живых рядов БЕЗ патча: каждый ряд шлёт Lampa.Listener('line').
    // ⚠️ Обработчик обязан быть целиком в try: рассылка в бандле обёрнута ОДНИМ общим try —
    // исключение отсюда оборвало бы остальных слушателей.
    function swrOnLine(e) {
        try {
            if (swrRebuilding) return;                              // события от нашей же перестройки
            var line = e && e.line;
            if (!line || typeof line.emit !== 'function') return;   // deprecated InteractionLine — не наш случай
            if (e.type === 'destroy') { swrDrop(line); return; }
            if (e.type === 'create') {
                if (!swrKey(line)) return;                          // ряд собран не из кешированного тела (персоны)
                for (var i = 0; i < swrLines.length; i++) if (swrLines[i].line === line) return;
                if (swrLines.length >= SWR_MAX_LINES) swrLines.shift();
                swrLines.push({ line: line });
            }
            if (e.type === 'create' || e.type === 'visible' || e.type === 'toggle') swrFlush();
        } catch (err) {}
    }

    // Свежий ответ от патча. Ряда может ещё не быть (батч ждёт все шесть частей) — тогда ответ
    // ждёт в очереди и подхватится, когда ряд появится или освободится.
    function swrOnRevalidate(e) {
        try {
            if (swrOff()) return;
            var url = e && (e.url || (e.params && e.params.url));
            var data = e && e.data;
            if (!url || !data || !data.results || !data.results.length) return;   // пустой ответ ряд НЕ гасит
            var keys = Object.keys(swrPending);
            if (keys.length >= SWR_MAX_PENDING) delete swrPending[keys[0]];
            swrPending[url] = data;
            swrFlush();
        } catch (err) {}
    }

    function swrIds(results, limit) {
        var out = [];
        for (var i = 0; i < results.length && i < limit; i++) {
            var r = results[i] || {};
            out.push(String(r.id != null ? r.id : (r.title || r.name || r.url || i)));
        }
        return out.join(',');
    }

    // Изменилось ли то, что показывает сервер: состав ИЛИ порядок. Сверяем с запасом за экран,
    // чтобы поймать перестановку и в хвосте, который дозагрузится скроллом.
    function swrChanged(oldResults, newResults) {
        var n = Math.max(20, (oldResults || []).length);
        return swrIds(oldResults || [], n) !== swrIds(newResults || [], n);
    }

    // Ряд, в котором СЕЙЧАС зритель. Признака три: владение контроллером (его регистрирует
    // сам ряд), сфокусированный элемент навигатора внутри ряда (мышь/десктоп) и класс .focus (ТВ).
    //
    // 🔴 Исключение (иначе фича невидима): при открытии экрана фокус ПО УМОЛЧАНИЮ стоит на первой
    // карточке верхнего ряда — то есть самый заметный ряд оказывался «под курсором» всегда и
    // обновлялся только после того, как зритель из него уйдёт. Пока он не листал ряд (active
    // на первой карточке), перестройка безопасна: фокус остаётся на первой позиции, из-под пальца
    // ничего не уезжает. Сдвинулся хоть на карточку — правило «не трогать» снова в силе.
    function swrBusyLine(line) {
        var untouched = false;
        try { untouched = !line.active; } catch (e) {}
        try { if (Lampa.Controller.own(line)) return !untouched; } catch (e) {}
        try {
            var f = (typeof Navigator !== 'undefined')
                ? (Navigator.getFocusedElement ? Navigator.getFocusedElement() : Navigator._focus) : null;
            if (f && line.html && line.html.contains && line.html.contains(f)) return !untouched;
        } catch (e) {}
        try { if (line.html && line.html.querySelector && line.html.querySelector('.selector.focus')) return !untouched; } catch (e) {}
        return false;
    }

    // Поверх открыт селектбокс/настройки/модалка/плеер — коллекцию навигатора трогать нельзя.
    function swrBusyScreen() {
        // Сравнения строгие: hasClass у jQuery отдаёт булево, length — число. Мягкая проверка
        // («если что-то вернулось») считала бы экран занятым всегда там, где $ подменён.
        try {
            var body = $('body');
            if (body.hasClass('selectbox--open') === true || body.hasClass('settings--open') === true) return true;
            var overlays = $('.modal, .player, .search-box');
            if (overlays && typeof overlays.length === 'number' && overlays.length > 0) return true;
        } catch (e) {}
        return false;
    }

    // Часть загрузчиков главной проставляет стиль КАЖДОЙ карточке (UHD-ряд и «Трейлеры» — широкие),
    // а свежий ответ сервера про это не знает: без переноса ряд молча стал бы обычным.
    function swrCardStyle(results) {
        try {
            var f = results && results[0];
            var n = f && f.params && f.params.style && f.params.style.name;
            return (n && n !== 'default') ? n : null;
        } catch (e) { return null; }
    }

    // Перестройка ряда НА МЕСТЕ: штатного line.update() в бандле нет, а Main.build() не идемпотентен.
    // 🔴 Порядок важен: сперва destroy карточек (их onDestroy снимает подписки и обнуляет
    // обработчики картинок — иначе утечка на каждой перестройке), и только потом очистка DOM.
    function swrRebuild(line, fresh) {
        var owned = false;
        try { owned = Lampa.Controller.own(line); } catch (e) {}
        var style = swrCardStyle(line.data.results);
        if (style) fresh.results.forEach(function (c) {
            if (!c.params) c.params = {};
            if (!c.params.style) c.params.style = { name: style };
        });
        swrRebuilding = true;
        try {
            try { Lampa.Arrays.destroy(line.items); } catch (e) {}
            line.items = [];
            line.active = 0;
            line.last = null;
            line.more = null;                        // без сброса кнопка «ещё» больше не появится
            try { line.scroll.clear(); line.scroll.reset(); } catch (e) {}
            line.data.results = fresh.results;
            if (fresh.total_pages) line.data.total_pages = fresh.total_pages;
            var view = (line.params && line.params.items && line.params.items.view) || 7;
            line.data.results.slice(0, view).forEach(function (el) { line.emit('createAndAppend', el); });
            // передаём прокрутку ряда, а НЕ его корень: иначе пришьётся вторая кнопка «ещё»
            try { Lampa.Layer.visible(line.scroll.render(true)); } catch (e) {}
            if (owned) { try { Lampa.Controller.collectionSet(line.scroll.render(true)); } catch (e) {} }
        } finally { swrRebuilding = false; }
    }

    function swrFlush() {
        try {
            if (swrRebuilding || swrOff()) return;
            if (!Object.keys(swrPending).length) return;
            if (swrBusyScreen()) return;
            var done = {}, skipped = {};
            for (var i = swrLines.length - 1; i >= 0; i--) {
                var line = swrLines[i].line;
                if (!line || !line.html || !line.html.parentNode) { swrLines.splice(i, 1); continue; }
                var url = swrKey(line);
                var fresh = url && swrPending[url];
                if (!fresh) continue;
                if (swrBusyLine(line)) { skipped[url] = 1; continue; }   // зритель внутри — отложили до выхода
                if (swrChanged(line.data.results, fresh.results)) swrRebuild(line, fresh);
                done[url] = 1;
            }
            Object.keys(done).forEach(function (u) { if (!skipped[u]) delete swrPending[u]; });
        } catch (e) {}
    }

    function initSwr() {
        if (window.__qdl_swr) return;              // повторная загрузка qdl.js → одна подписка
        window.__qdl_swr = true;
        window.qdl_swr = swrGate;                  // контракт с патчем бандла /*qdl-cut:swr*/
        try { Lampa.Listener.follow('line', swrOnLine); } catch (e) {}
        try { Lampa.Listener.follow('request_revalidate', swrOnRevalidate); } catch (e) {}
        // возврат на страницу и любая смена контроллера (зритель вышел из ряда) — поводы дожать
        try { Lampa.Listener.follow('activity', function (e) { if (e && e.type === 'start') swrFlush(); }); } catch (e) {}
        try { Lampa.Controller.listener.follow('toggle', function () { swrFlush(); }); } catch (e) {}
        // Диагностика: патч бандла мог не доехать (сменился tree / не чищен Staticache) — тогда
        // события не будет никогда. Молчать нельзя, иначе фичу будут искать в qdl.js.
        try {
            var t = setTimeout(function () {
                if (!swrBudget.length && !Object.keys(swrLast).length)
                    console.warn('qdl swr: бандл ни разу не спросил window.qdl_swr — патч /*qdl-cut:swr*/ не доехал?');
            }, 30000);
            // в браузере setTimeout отдаёт число, в тестовой песочнице — таймер Node: без unref
            // он держал бы прогон тестов лишние 30 секунд
            if (t && typeof t.unref === 'function') t.unref();
        } catch (e) {}
    }

    // ───────── Дедуп и насос полноэкранной сетки: экран «Ещё» (qdl 2.94) ─────────
    // Жалоба владельца: в «Сейчас смотрят» → «Ещё» каждый фильм показан двумя строчками по 6
    // карточек. Серверную половину закрыл RowFilter (наша страница = ровно одна апстримная,
    // добор соседних убран — он и давал перекрытие 4-8 карточек из 20). Здесь вторая половина,
    // серверу недоступная: страницы кешируются НЕЗАВИСИМО (Staticache, 3 ч), а ?sort=now_playing —
    // живой поток, поэтому свежая p1 и остывшая p2 законно содержат одну карточку.
    //
    // 🔴 Насос отложенный, и это не перестраховка. Scroll.isEnd() на незаполненном гриде отдаёт
    // TRUE, но onEnd зовётся только из scrollEnded(), а тот на таком гриде достижим ровно один
    // раз — из hover:focus первой карточки через startScroll. Ровно в этот момент Next.onLoadNext
    // себя запрещает гардом builded_time < Date.now()-1000: единственный шанс догрузиться
    // приходится на секунду, которую бандл сам себе закрыл. Отсюда GRID_PUMP_MS > 1000.
    //
    // 🔴 Урок ComponentRecFeed: окно сдвигаем на то, что ОТДАЛ сервер, а не на то, что нарисовали.
    // Здесь это буквально: object.page двигает бандл, мы его не трогаем.

    var GRID_PUMP_MAX = 8;      // подряд идущих АВТО-подтяжек без прироста карточек
    var GRID_PUMP_MS  = 1150;   // > гарда builded_time (1000 мс) + запас
    var GRID_SEEN_CAP = 5000;   // 86 страниц по ~12 карточек ≈ 1000; кап — от бесконечной сессии

    function gridOff() {
        try { if (window.lampa_settings && window.lampa_settings.qdl_grid_dedup === false) return true; } catch (e) {}
        try { return !!Lampa.Storage.get('qdl_grid_dedup_off', false); } catch (e) { return false; }
    }

    // 🔴 Ключ — тип + id. У TMDB нумерация РАЗДЕЛЬНАЯ для фильмов и сериалов: голый id склеил бы
    // два разных тайтла, и один из них молча потерялся бы. Признак типа — как в RowFilter.Keep.
    // Нет id — карточку НЕ выбрасываем никогда: терять нельзя (прямое требование владельца).
    function gridKey(c) {
        if (!c || typeof c !== 'object') return null;
        if (c.id === undefined || c.id === null || c.id === '') return null;
        return (c.media_type || (c.first_air_date ? 'tv' : 'movie')) + ':' + c.id;
    }

    function gridMark(comp, results) {
        if (!comp.qdl_seen) { comp.qdl_seen = {}; comp.qdl_seen_n = 0; }
        for (var i = 0; i < (results || []).length; i++) {
            var k = gridKey(results[i]);
            if (k && !comp.qdl_seen[k] && comp.qdl_seen_n < GRID_SEEN_CAP) { comp.qdl_seen[k] = 1; comp.qdl_seen_n++; }
        }
    }

    // Контракт с патчем grid-dedup-build: первая страница уже отфильтрована сервером, её только
    // регистрируем — и взводим насос, потому что после фильтра она могла оказаться короткой.
    function gridBuild(comp, results) {
        try {
            window.__qdl_grid_hit = 1;
            if (gridOff() || !comp) return;
            gridMark(comp, results);
            comp.qdl_auto = 0;
            gridPumpLater(comp);
        } catch (e) {}
    }

    // Контракт с патчем grid-dedup-next: вернуть массив БЕЗ уже показанных карточек.
    function gridNext(comp, results) {
        try { window.__qdl_grid_hit = 1; } catch (e) {}
        if (gridOff() || !comp || !results || !results.length) return results;
        try {
            if (!comp.qdl_seen) { comp.qdl_seen = {}; comp.qdl_seen_n = 0; }
            var out = [];
            for (var i = 0; i < results.length; i++) {
                var k = gridKey(results[i]);
                if (k && comp.qdl_seen[k]) continue;
                out.push(results[i]);
            }
            gridMark(comp, out);
            if (out.length) comp.qdl_auto = 0;   // прирост есть — пагинацию дальше ведёт зритель
            gridPumpLater(comp);
            return out;
        } catch (e) { return results; }
    }

    function gridFilled(comp) {
        try { return !!comp.scroll.isFilled(); } catch (e) { return true; }   // не знаем — считаем заполненным
    }

    function gridAlive(comp) {
        try {
            if (!comp || comp.destroyed) return false;
            var el = comp.scroll.render(true);
            return !!(el && el.isConnected !== false);
        } catch (e) { return false; }
    }

    function gridPumpLater(comp) {
        if (!comp || comp.qdl_pump_t) return;              // один таймер на компонент
        comp.qdl_pump_t = setTimeout(function () {
            comp.qdl_pump_t = 0;
            try { gridPump(comp); } catch (e) {}
        }, GRID_PUMP_MS);
    }

    function gridPump(comp) {
        if (gridOff() || !gridAlive(comp)) return;

        // 1. Слить очередь: Items.onPushLoaded отдаёт ОДНУ порцию (limit_view=6) на событие
        // скролла, а на незаполненном гриде скролла не бывает — порции зависли бы в loaded.
        // Жёсткий guard — страховка от неверного isFilled() на неотрисованном экране.
        var guard = 0;
        while (comp.loaded && comp.loaded.length && !gridFilled(comp) && guard++ < 12) comp.emit('pushLoaded');

        if (gridFilled(comp)) return;                      // заполнились — дальше решает зритель
        if (comp.loaded && comp.loaded.length) return;     // ещё есть что рисовать
        if (!(comp.object && comp.object.page < comp.total_pages)) return;   // страницы кончились

        // 2. 🔴 Бюджет — главная защита от вечного цикла: после дедупа страница может добавить
        // 0 карточек, грид не вырастет, isFilled() останется false. Считаем только НАШИ
        // подтяжки — любой прирост обнуляет счётчик в gridNext.
        comp.qdl_auto = (comp.qdl_auto || 0) + 1;
        if (comp.qdl_auto > GRID_PUMP_MAX) return;

        comp.emit('loadNext');
        gridPumpLater(comp);                               // страница вернулась пустой — следующий круг
    }

    function initGridDedup() {
        if (window.__qdl_grid) return;                     // повторная загрузка qdl.js — одни хуки
        window.__qdl_grid = true;
        window.qdl_grid_build = gridBuild;                 // контракт с патчем grid-dedup-build
        window.qdl_grid_next = gridNext;                   // контракт с патчем grid-dedup-next
        // Диагностика (образец initSwr): патч мог не доехать — сменился tree или не чищен Staticache.
        try {
            var t = setTimeout(function () {
                if (!window.__qdl_grid_hit)
                    console.warn('qdl grid: бандл ни разу не спросил window.qdl_grid_* — патчи не доехали?');
            }, 30000);
            if (t && typeof t.unref === 'function') t.unref();
        } catch (e) {}
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
    // Единственная точка гейта служебного UI — право «действия» (manage), выданное устройству
    // в /admin/d1v. Её и надо звать во всех местах, где решается «показывать ли».
    //
    // 🔴 qdl 2.89: мастер-кука qdl_unlock=1 (второй ключ с 2.39) убрана целиком по решению
    // владельца — «всё завязано по пермишину через админку». Страховка от самозапирания при
    // потере access.json теперь одна и её достаточно: сама админка /admin/d1v открывается
    // рут-паролем (кука accspasswd) и от прав устройств не зависит.
    // 🔴 Это по-прежнему только отрисовка: настоящий замок стоит на сервере (ManageDenied → 403),
    // поэтому подделка localStorage даёт максимум видимый пункт, который откажет при нажатии.
    function qdlManage() { return qdlAllowed('manage'); }

    // Замок служебных входов Lampa (шестерёнка «Настройки», пункт «Консоль»). Узел
    // <style id="qdl-hide-settings"> создаёт СИНХРОННО lampainit-invc.js — мгновенным обязано быть
    // именно СОКРЫТИЕ, иначе шестерёнка моргнёт у того, кому она не положена. Здесь узел только
    // снимается/возвращается: права приезжают асинхронно и перечитываются раз в минуту, так что
    // и выдача, и отзыв доезжают до открытого клиента сами. Владелец узла один — этот файл.
    function applySettingsLock() {
        try {
            var node = document.getElementById('qdl-hide-settings');
            // ⚠️ Через node.parentNode.removeChild() нельзя: у отсоединённого узла parentNode === null,
            // вылет молча съел бы try/catch — и замок остался бы висеть вообще без диагностики.
            if (qdlManage()) { if (node && node.parentNode) node.parentNode.removeChild(node); return; }
            if (node) return;
            node = document.createElement('style');
            node.id = 'qdl-hide-settings';
            node.textContent = '.head__action.open--settings{display:none!important}'
                + '.menu__item[data-action="settings"],.menu__item[data-action="console"]{display:none!important}'
                // 🔴 qdl 2.84: нижняя панель телефона (NavigationBar при Platform.screen('mobile'))
                // зовёт Controller.toggle('settings') напрямую — её кнопку надо гасить отдельно,
                // иначе замок обходится с телефона. Копия правила есть в lampainit-invc.js.
                + '.navigation-bar__item[data-action="settings"]{display:none!important}'
                // 🔴 Плитка «Хелс-чеки» — тем же замком. Lampa.SettingsApi снять компонент не умеет,
                // а гард window.qdl_health_settings стоит навсегда: без этого правила отозванное
                // право прятало шестерёнку, но раздел доживал до перезапуска (поймано permsgate).
                + '[data-component="qdl_health"],[data-component="qdl_d1vision"]{display:none!important}';
            document.head.appendChild(node);
        } catch (e) {}
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
    // card — карточка тайтла для «Истории просмотров»: экран серий сам её не знает (activity
    // здесь наша, а не полная карточка), а писать историю надо в момент старта серии.
    function chooseEpisode(hash, name, autoplay, card) {
        Lampa.Activity.push({
            url: '', component: 'qdl_episodes', title: 'Серии — ' + (name || ''),
            qdl_hash: hash, qdl_name: name || '', qdl_autoplay: !!autoplay, qdl_hcard: card || null
        });
    }

    // gateItem — необязательный, строго последний: карточка загрузки, если у вызывающего она
    // под рукой. Нужен только как фолбэк, когда список файлов не приехал.
    //
    // 🔴 Гейт живёт ЗДЕСЬ, а не в watch(): решение «фильм или сериал» принимается только после
    // fetchEpisodes, а до 2.93 гейт стоял раньше — и сериал, у которого нужная серия давно
    // скачана, ругался карточным прогрессом. У склеенной карточки он к тому же взвешен по
    // размеру (SeriesMerge.cs), то есть 11 готовых серий из 12 читались как «92%».
    // Сериал теперь уходит на экран серий БЕЗ диалога: там гейтит каждая строка отдельно.
    //
    // Бонус однофайловой ветки: прогресс берётся у САМОГО ФАЙЛА (per-file из /qdl/episodes),
    // а не у торрента — они расходятся всегда, когда в раздаче лежат сэмплы и субтитры.
    function watchByHash(hash, name, card, gateItem) {
        fetchEpisodes(hash, function (files) {
            var vids = mergedVideoFiles(files);
            // сериал уходит на экран серий — историю там запишет play(), в момент старта конкретной серии
            if (vids.length > 1) { chooseEpisode(hash, name, false, card); return; }
            var f = vids[0] || null;
            var gate = f ? { hash: srcHash(f, hash), progress: f.progress } : gateItem;
            gatePartial(gate, function () {
                noteHistory(card);
                playLocal(f ? srcHash(f, hash) : hash, f ? f.index : -1, name, f ? f.name : null, f);
            });
        }, function () {
            // список файлов не приехал — гейтим по тому, что знает вызывающий (обычно ничего → fail-open)
            gatePartial(gateItem, function () { noteHistory(card); playLocal(hash, -1, name); });
        });
    }

    // ───────── Гейт недокачанного (qdl 2.93) ─────────
    // Владелец: «сейчас когда мы загружаем фильм можно попробовать его посмотреть — нужно
    // выводить надпись „дождитесь загрузки“». Решение — ЖЁСТКАЯ блокировка, и вот почему:
    // последовательная загрузка (sequentialDownload/firstLastPiecePrio) в qBittorrent у нас не
    // включается нигде, куски приезжают «редкие первыми», файл на диске дырявый, а /qdl/stream
    // отдаёт его как есть без проверки доступности кусков. Заголовок (а у mp4 ещё и индекс в
    // хвосте) на 40% — лотерея: плеер либо не стартует, либо доигрывает до первой дыры.
    // Сам модуль это уже признаёт: транскод отказывает недокачанному, а под падения ffmpeg на
    // таких файлах написан негатив-кеш HLS. Киллсвитч на лету — partialPlayBlock:false.
    //
    // 🔴 Живое состояние ПОБЕЖДАЕТ снимок. Ровно этим чинится жалоба «докачалось, а клиент
    // всё равно спрашивает»: /qdl/list кешируется 30 с, а qdl_progress на активности —
    // снимок момента открытия карточки и не обновляется никогда.
    //
    // -1 = данных нет (fail-open). Отдельный сентинел нужен потому, что 0 — валидный прогресс:
    // невозможность выразить «неизвестно» и породила три места с `?? 1`.
    function livePartial(item) {
        if (!item) return -1;
        if (item.local || item.state === 'local') return 1;   // транскод/jut/XSMART — готов по построению
        var live = item.hash ? pgGet(item.hash) : null;
        if (live && typeof live.p === 'number') return live.p;
        return (typeof item.progress === 'number') ? item.progress : -1;
    }

    function gatePartial(item, run) {
        var p = livePartial(item);
        if (p < 0 || p >= DONE || !pgBlockEnabled()) { run(); return; }
        Lampa.Select.show({
            title: 'Дождитесь загрузки — скачано ' + Math.round(p * 100) + '%',
            items: [{ title: 'Понятно' }],
            onSelect: function () { Lampa.Controller.toggle('content'); },
            onBack: function () { Lampa.Controller.toggle('content'); }
        });
    }

    // card — настоящая TMDB-карточка, когда она под рукой (зелёная «Смотреть» на полной карточке):
    // у самой загрузки меты может не быть вовсе — безымянная раздача, привязка ещё не доехала.
    function watch(item, card) {
        var hcard = card || item.meta;
        // jut-маркер: TMDB id у него 0 (у аниме с jut.su его и не бывает), но слаг есть —
        // историю ведём своей карточкой, иначе скачанное аниме в неё вообще не попадёт.
        if ((!hcard || !hcard.id) && item && item.jut && item.jut.slug)
            hcard = jutHistoryCard(item.jut.slug, item.jut.titleRu || item.name);
        // гейт — внутри watchByHash: до неё ещё неизвестно, фильм это или сериал (см. её шапку)
        watchByHash(item.hash, (item.meta && item.meta.title) || item.name, hcard, item);
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
                // ⚠️ ТОЛЬКО подсказка для первой отрисовки подписи/чипа — НИКОГДА для гейта.
                // Это снимок момента открытия карточки, он не обновляется, и именно на него
                // жаловался владелец («докачалось, а клиент не знает»). Правду про прогресс
                // с qdl 2.93 знает поллер (pgGet). Поле оставлено: его читает восстановленная
                // активность, у которой поллер ещё пуст.
                qdl_progress: (typeof item.progress === 'number' ? item.progress : 1)
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
    // cls необязателен и строго второй: чип с классом можно потом найти и обновить на месте
    function chip(txt, cls) {
        return '<span' + (cls ? ' class="' + cls + '"' : '') + ' style="display:inline-block;background:rgba(255,255,255,.1);padding:.3em .65em;border-radius:.45em;margin:0 .5em .5em 0;font-size:1em">' + esc(txt) + '</span>';
    }

    // Живой прогресс карточки «Загрузок», 0..1. У склеенной (несколько сезонов одной раздачей)
    // считаем ВЗВЕШЕННО ПО РАЗМЕРУ — точное зеркало MergeSeriesGroup на сервере.
    // ⚠️ Наивное среднее здесь уже стреляло: докачанный 20-гигабайтный сезон рядом с пустым
    // одногигабайтным давало «50%» на готовом сериале (claude/06 §CM).
    function livePct(t) {
        if (!t) return 0;
        var ps = cardParts(t);
        if (!ps) {
            var l = pgGet(t.hash);
            return (l && typeof l.p === 'number') ? l.p : (t.progress || 0);
        }
        var size = 0, weighted = 0;
        ps.forEach(function (p) {
            var lp = pgGet(p.hash);
            var pp = (lp && typeof lp.p === 'number') ? lp.p : (p.progress || 0);
            var sz = p.size || 0;
            size += sz; weighted += sz * pp;
        });
        return size > 0 ? Math.min(1, weighted / size) : (t.progress || 0);
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
    // hash необязателен: без него подстрочник считается по снимку f.progress (так зовут тесты
    // и старые места). ⚠️ Про ЗАГРУЗКУ, не про просмотр — просмотр рисует epMark.
    function epMeta(f, hash) {
        var p = f ? epProgress(f, hash) : -1;
        return [liveSize(f && f.size),
                (f && f.source === 'donor') ? 'временная — заменится основной' : '',
                (p >= 0 && p < DONE) ? 'качается — ' + Math.round(p * 100) + '%' : '']
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
        // карточка тайтла для «Истории просмотров»: приезжает с активностью (chooseEpisode), а если
        // экран восстановили из истории активностей — добираем из предыдущей полной карточки.
        var hcard = object.qdl_hcard || null;

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
            // 🔴 Гейт ДО noteHistory: запертая серия не должна попадать в «Историю просмотров».
            // Тост, а не Select: по строке тыкают случайно, а она и так помечена визуально.
            if (!epReady(f, hash)) { epWaitNotice(f, hash); return; }
            noteHistory(hcard || activityCard());
            var go = function (audio) {
                // озвучка выбрана для основной раздачи (vids[0]) — донорским файлам buildPlaylist даст дефолт
                // Недокачанные в плейлист не попадают: иначе авто-переход внутри плеера прыгнет в дырявый файл.
                var playlist = buildPlaylist(hash, vids, audio, srcHash(vids[0], hash),
                                             function (x) { return epReady(x, hash); });
                // 🔴 Индекс в плейлисте ИЩЕМ по карте, а не предполагаем: playlist отфильтрован,
                // а i — индекс в vids. Без этого «серия 3» играла бы четвёртую (мина смещения).
                var j = playlist.qdlMap ? playlist.qdlMap.indexOf(i) : i;
                if (j < 0 || !playlist.length) { epWaitNotice(f, hash); return; }
                // 🔴 Играем ОТДЕЛЬНЫЙ объект, а не playlist[j]: плейлист обязан лежать НА объекте
                // до play (нативная ветка сериализует data синхронно внутри Player.play, а
                // Player.playlist() до нативов не доезжает — см. jutPlay), но item.playlist на
                // элементе самого массива дал бы цикл и JSON.stringify бросил бы. url — ТА ЖЕ
                // строка: нативы ищут текущий индекс точным сравнением. timeline — общий ref
                // с элементом плейлиста: его и пишет прогресс (обратной ссылки в нём нет).
                var item = { title: playlist[j].title, url: playlist[j].url };
                if (playlist[j].timeline) item.timeline = playlist[j].timeline;
                item.playlist = playlist;
                Lampa.Player.play(item);
                Lampa.Player.playlist(playlist);     // веб-плеер: ручное переключение серий остаётся
                warmupNext(hash, vids, f);           // следующая серия — в page cache, пока смотрят эту
            };
            if (comp.audioReady) go(comp.audio); else askAudio(go);
        };

        // Только докачанные — общий фильтр для «что продолжать», автоплея и подсветки текущей.
        // ⚠️ filter сохраняет ССЫЛКИ, а pickContinue/refreshMarks сравнивают через === , поэтому
        // отфильтрованный массив совместим с ними без единой правки самих функций.
        function readyVids() {
            return comp.vids.filter(function (f) { return epReady(f, hash); });
        }

        function rowEl(f, i) {
            var num = epNumber(f);   // номер серии крупно слева: длинные имена файлов обрезаются (репорт с iPhone)
            var el = $(
                '<div class="selector qdl-row-focus" style="display:flex;align-items:center;gap:1.2em;padding:.9em 1.2em;margin:.35em 1.4em;background:rgba(255,255,255,.06);border-radius:.8em">' +
                  '<div class="qdl-ep-num" style="flex:none;min-width:1.7em;text-align:center;font-size:1.8em;font-weight:700;opacity:.9">' + (num !== null ? num : i + 1) + '</div>' +
                  '<div style="flex:1;min-width:0">' +
                    // 🔴 .qdl-ep-dl — про ЗАГРУЗКУ, .qdl-ep-mark — про ПРОСМОТР. Два разных процента
                    // в одном узле уже однажды стоили путаницы; смешивать их нельзя.
                    '<div style="font-size:1.5em;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"><span class="qdl-ep-dl"></span><span class="qdl-ep-mark"></span><span class="qdl-ep-name"></span></div>' +
                    '<div class="qdl-ep-meta" style="opacity:.65;font-size:1.15em;margin-top:.25em"></div>' +
                  '</div>' +
                  '<div class="qdl-ep-play" style="opacity:.45;padding-right:.3em">' + WATCH_ICON + '</div>' +
                '</div>'
            );
            // data-* нужны живому обновлению: строку находим по ним и патчим текст, а не пересобираем
            // (иначе фокус пульта уезжает — тот же приём, что у сетки камер).
            el.attr('data-hash', srcHash(f, hash));
            el.attr('data-index', String(f.index));
            el.find('.qdl-ep-name').text(baseName(f.name) + (f.source === 'donor' ? ' · врем.' : ''));
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () { comp.play(i); });
            rows.push({ el: el, f: f });
            paintRowDownload(el, f);
            return el;
        }

        // Отметка загрузки строки + запертость. Зовётся при сборке и на каждом тике поллера.
        function paintRowDownload(el, f) {
            var ready = epReady(f, hash);
            var p = epProgress(f, hash);
            el.find('.qdl-ep-dl').text(ready ? '' : '⬇ ' + Math.round(Math.max(0, p) * 100) + '% ');
            el.find('.qdl-ep-meta').text(epMeta(f, hash));
            el.toggleClass('qdl-ep--wait', !ready);
            el.find('.qdl-ep-play').css('opacity', ready ? .45 : .15);
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
                    langBtn.on('hover:touch hover:hover', function () { last = markLast(langBtn); });
                    langBtn.on('hover:enter', function () { askLang(); });
                    bar.append(langBtn);
                }
                if (comp.audioOpts.length > 1) {
                    audioBtn = $('<div class="selector qdl-btn-focus qdl-audio-btn" style="' + btnCss + '"></div>');
                    audioBtn.text('Озвучка: ' + audioLabel());
                    audioBtn.on('hover:focus', function () { last = audioBtn[0]; scroll.update(audioBtn, true); });
                    audioBtn.on('hover:touch hover:hover', function () { last = markLast(audioBtn); });
                    audioBtn.on('hover:enter', function () { askAudio(null); });
                    bar.append(audioBtn);
                }
                if (bar.children().length) body.append(bar);

                // 2.78: в списке больше одного сезона (склеенная карточка) → разделители.
                // Без них нумерация начинается заново посреди списка и читается как дубль.
                var seasons = {}, multi = 0, prevHead = null;
                vids.forEach(function (f) { var h = epHeadSeason(f); if (h > 0 && !seasons[h]) { seasons[h] = 1; multi++; } });
                vids.forEach(function (f, i) {
                    if (multi > 1) {
                        var h = epHeadSeason(f);
                        if (h !== prevHead && h >= 0) {
                            body.append('<div style="padding:1.2em 1.6em .3em;font-size:1.35em;font-weight:700;opacity:.7">'
                                + esc(h > 0 ? 'Сезон ' + h : 'Дополнительно') + '</div>');
                            prevHead = h;
                        }
                    }
                    body.append(rowEl(f, i));
                });
                comp.refreshMarks();   // отметки + подсветка текущей + стартовый фокус на ней
            }

            this.activity.loader(false);
            this.activity.toggle();

            if (object.qdl_autoplay && vids.length) {
                object.qdl_autoplay = false;   // одноразово: возврат из плеера не перезапускает плей
                var vw = function (f) { return pickTimeline(hash, f); };
                // 2.93: выбираем только из ДОКАЧАННЫХ — иначе «Продолжить» с карточки молча
                // уводила бы в серию, которая всё равно не сыграет.
                var pool = readyVids();
                if (!pool.length) {
                    try { Lampa.Noty.show('Серии ещё качаются — дождитесь загрузки'); } catch (e) {}
                } else {
                    // 🔥 Было `comp.play(cur ? vids.indexOf(cur) : 0)`: когда продолжать нечего,
                    // «Продолжить» МОЛЧА запускала первый файл списка — ровно симптом жалобы
                    // 14.08.2026. Нечего продолжать → первая НЕпросмотренная (по номеру, не по
                    // индексу); досмотрено всё → начинаем сначала, но вслух.
                    var cur = chooseContinue(pool, vw) || firstUnwatched(pool, vw);
                    if (!cur) {
                        cur = sortEpisodes(pool)[0];
                        try { Lampa.Noty.show('Всё просмотрено — включаю с начала'); } catch (e) {}
                    }
                    comp.play(vids.indexOf(cur));   // обратно в индекс НЕотфильтрованного vids
                }
            }
        };

        // свежие ✓/►N% и подсветка «текущей» — зовётся из start() при каждом возврате (в т.ч. из плеера)
        this.refreshMarks = function () {
            if (!rows.length) return;
            var view = function (f) { return pickTimeline(hash, f); };
            // «текущая» считается по докачанным: подсветка и стартовый фокус не должны садиться
            // на запертую строку — нажатие по ней даст только тост.
            var cur = chooseContinue(readyVids(), view);
            rows.forEach(function (r) {
                r.el.find('.qdl-ep-mark').text(epMark((view(r.f) || {}).percent || 0));
                r.el.toggleClass('qdl-ep--cur', !!cur && r.f === cur);
            });
            if (cur && !last) {   // стартовый фокус — на продолжаемой серии; выбор пользователя не перебиваем
                var hit = rows.filter(function (r) { return r.f === cur; })[0];
                if (hit) last = hit.el[0];
            }
        };

        // Живая разблокировка строк по мере докачки (qdl 2.93). DOM не пересобираем — патчим
        // текст найденных узлов, иначе фокус пульта уезжает на первую строку.
        this.refreshDownload = function () {
            if (!rows.length) return;
            rows.forEach(function (r) { paintRowDownload(r.el, r.f); });
            comp.refreshMarks();   // докачавшаяся серия могла изменить, куда указывает «продолжить»
        };

        this.render = function () { return html; };
        this.start = function () {
            // возврат на экран (в т.ч. из плеера/другой активности) — восстановить бакет таймкодов
            if (comp.tlbucket) { activeEpisodesComp = comp; setTlBucket(comp.tlbucket); }
            comp.refreshMarks();
            // 🔴 Подписка именно в start(), а отписка в pause(): Lampa при навигации ВПЕРЁД
            // не зовёт destroy(), компонент висит в стеке — без этого каждая сложенная копия
            // экрана продолжала бы опрашивать сервер из фона (мина ComponentLiveWatch).
            if (!comp.pgToken) comp.pgToken = pgSubscribe(hash, function () { comp.refreshDownload(); });
            comp.refreshDownload();
            Lampa.Controller.add('content', {
                toggle: function () { focusBack(scroll, last); },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };
        function pgOff() { if (comp.pgToken) { pgUnsubscribe(comp.pgToken); comp.pgToken = null; } }
        this.pause = function () { pgOff(); };
        this.stop = function () { pgOff(); };
        this.destroy = function () {
            pgOff();   // идемпотентно: на выселении из стека pause не приходит
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
            var pct = Math.round(livePct(item) * 100);
            var kind = m.media_type === 'tv' ? 'Сериал' : 'Фильм';
            var rt = m.runtime ? (Math.floor(m.runtime / 60) + ':' + ('0' + (m.runtime % 60)).slice(-2)) : '';
            var meta1 = [m.year, (m.countries && m.countries.length ? m.countries.slice(0, 2).join(', ') : ''), kind, rt].filter(Boolean).join('   ·   ');
            var genres = (m.genres && m.genres.length) ? m.genres.slice(0, 4).join(', ') : '';

            var badges = '';
            if (m.vote_average) badges += badge((Math.round(m.vote_average * 10) / 10), 'TMDB');
            if (m.age) badges += chip(/\d$/.test(String(m.age)) ? m.age + '+' : m.age);
            if (m.status) badges += chip(m.status === 'Released' ? 'Выпущен' : (m.status === 'Ended' ? 'Завершён' : (m.status === 'Returning Series' ? 'Идёт' : m.status)));
            if (m.number_of_seasons) badges += chip('Сезонов: ' + m.number_of_seasons);
            // текст сохранён дословно; класс нужен, чтобы обновлять чип на месте по тику поллера
            badges += chip(pct < 100 ? pct + '% загружено' : '✓ загружено', 'qdl-chip-dl');

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
            // рваная иконка читается как поломка приложения — нейтральная плитка поверх #222 (§BV)
            body.find('.qdl-poster').on('error', function () { this.src = PX1; });
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
                    // фильм/одна серия — продолжать нечего, хватит «Смотреть».
                    // 2.93: цель ищем только среди ДОКАЧАННЫХ — «Продолжить · Серия 5» не должна
                    // вести в серию, которая всё равно упрётся в гейт экрана серий.
                    var ready = vids.filter(function (f) { return epReady(f, item.hash); });
                    var target = vids.length < 2 ? null
                        : chooseContinue(ready, function (f) { return pickTimeline(item.hash, f); });
                    // Всё досмотрели → кнопка обязана уйти. Узел удаляем без починки навигатора:
                    // toggle() этого экрана пересобирает коллекцию через collectionSet.
                    if (!target) { if (btn.length) btn.remove(); return; }
                    // Кнопка уже есть → правим ТОЛЬКО подпись: тот же DOM-узел = живой фокус пульта
                    if (btn.length) { btn.children('span').text('Продолжить · ' + epShort(target.name)); return; }
                    var b = $('<div class="qdl-continue selector" style="display:inline-flex;align-items:center;gap:.55em;padding:.75em 2em;border-radius:.6em;font-size:1.4em">' + CONTINUE_ICON + '<span></span></div>');
                    b.children('span').text('Продолжить · ' + epShort(target.name));
                    // Кнопка есть только у сериала (target считается при vids.length >= 2), а его
                    // карточный прогресс взвешен по размеру — гейтить им нечего. Гейт стоит
                    // построчно на экране серий (qdl 2.93).
                    b.on('hover:enter', function () { chooseEpisode(item.hash, (m.title || item.name), true); });
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
                        if (r && r.has_poster) { item.has_poster = true; item.posterUrl = '/qdl/poster?hash=' + item.hash; }
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
            // подписка в start / отписка в pause — см. мину «Lampa не зовёт destroy вперёд»
            if (!comp.pgToken) comp.pgToken = pgSubscribe(item.hash, function () { comp.refreshChip(); });
            comp.refreshChip();
            Lampa.Controller.add('content', {
                toggle: function () { focusBack(scroll, false); },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };
        // текст чипа тот же, меняется только число — узел не пересоздаём (фокус пульта жив)
        this.refreshChip = function () {
            try {
                var c = html.find('.qdl-chip-dl');
                if (!c.length) return;
                var p = Math.round(livePct(item) * 100);
                c.text(p < 100 ? p + '% загружено' : '✓ загружено');
            } catch (e) {}
        };
        function pgOff() { if (comp.pgToken) { pgUnsubscribe(comp.pgToken); comp.pgToken = null; } }
        this.pause = function () { pgOff(); };
        this.stop = function () { pgOff(); };
        // destroyed — не косметика: на него смотрят три async-колбэка (кнопка «Продолжить»,
        // enrich→saveMeta→Activity.replace). Флаг никогда не выставлялся, и гарды были мертвы.
        this.destroy = function () { pgOff(); comp.destroyed = true; scroll.destroy(); html.remove(); };
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
        this.items = [];       // карточки грида — по ним refreshBadges обновляет проценты

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
            comp.items = [];
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
            img.on('error', function () { this.src = PX1; });
            healPoster(c.cover, img);   // обложка коллекции тоже лечится

            var view = el.find('.card__view'); if (!view.length) view = el;
            view.append('<div style="position:absolute;left:.4em;top:.4em;background:rgba(110,60,220,.9);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">📁 ' + c.items.length + '</div>');

            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () { openCollection(c.col); });
            // Фон под фокусом (bgFocus): hover:focus — пульт, hover:hover — мышь десктопа.
            el.on('hover:focus hover:hover', function () { bgFocus(posterUrl(c.cover)); });
            // без «Управления» меню коллекции вырождается в один пункт «Открыть», который
            // дублирует обычное нажатие — тогда long-press просто не вешаем
            if (qdlManage()) el.on('hover:long', function () { collectionMenu(c.col, c.items); });

            body.append(el);
        };

        // Бейдж состояния загрузки. Вынесен из append, чтобы его можно было перерисовывать
        // на месте по тику поллера — карточка при этом остаётся ТЕМ ЖЕ узлом, фокус пульта жив.
        this.dlBadge = function (t) {
            var css = 'position:absolute;left:.4em;top:.4em;color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5;';
            if (t.local || t.state === 'local')
                return '<div class="qdl-dl-badge" style="' + css + 'background:rgba(30,120,220,.9)">MP4</div>';
            var pct = Math.round(livePct(t) * 100);
            return pct < 100
                ? '<div class="qdl-dl-badge" style="' + css + 'background:rgba(0,0,0,.75)">' + pct + '%</div>'
                : '<div class="qdl-dl-badge" style="' + css + 'background:rgba(20,160,40,.9)">✓</div>';
        };

        this.append = function (t, ctx) {
            var meta = t.meta || {};

            // обычная ВЕРТИКАЛЬНАЯ карточка-постер (без card--collection!)
            var el = Lampa.Template.get('card', { title: meta.title || t.name, release_year: meta.year || '' });
            el.attr('data-hash', t.hash);   // по нему живое обновление находит карточку

            var img = el.find('.card__img');
            img.attr('src', posterUrl(t));
            img.on('error', function () { this.src = PX1; });

            var view = el.find('.card__view'); if (!view.length) view = el;
            view.append(comp.dlBadge(t));

            // 2.78: карточка склеена из нескольких раздач — говорим об этом вслух. Иначе
            // «одна карточка вместо двух» выглядит как пропавший сезон.
            var prt = cardParts(t);
            if (prt) {
                var sc = (t.seasons && t.seasons.length) || 0;
                var slbl = sc > 1 ? sc + ' ' + livePlural(sc, 'сезон', 'сезона', 'сезонов')
                                  : prt.length + ' ' + livePlural(prt.length, 'раздача', 'раздачи', 'раздач');
                view.append('<div style="position:absolute;right:.4em;top:.4em;background:rgba(110,60,220,.9);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.9em;z-index:5">' + esc(slbl) + '</div>');
            }

            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () { openDownload(t); });
            // Фон под фокусом (bgFocus): hover:focus — пульт, hover:hover — мышь десктопа.
            // ⚠️ posterUrl(t) — в момент фокуса: enrich() дописывает t.posterUrl уже ПОСЛЕ сборки карточки.
            el.on('hover:focus hover:hover', function () { bgFocus(posterUrl(t)); });
            el.on('hover:long', function () { quickMenu(t, ctx); });

            body.append(el);
            comp.items.push(t);   // реестр для refreshBadges: обходим карточки, а не весь DOM

            // нет метаданных → ищем в TMDB + тянем полные детали, кэшируем, обновляем карточку
            if (!t.meta) {
                enrich(t.name, function (card) {
                    if (!card) return;
                    saveMeta(t.hash, card, function (r) {
                        t.meta = slimCard(card);
                        el.find('.card__title').text(card.title || card.name || t.name);
                        if (r && r.has_poster) { t.has_poster = true; t.posterUrl = '/qdl/poster?hash=' + t.hash; el.find('.card__img').attr('src', API + '/qdl/poster?hash=' + t.hash + '&t=' + Date.now()); }
                    });
                });
            }
            else healPoster(t, img);   // мета есть, но постер на сервере не скачался → ретрай
        };

        // Живые проценты (qdl 2.93): DOM не перестраиваем — подменяем один бейдж на карточку.
        // ⚠️ t.progress тоже обновляем: openDownload кладёт его на активность как подсказку
        // для первой отрисовки подписи «Смотреть · N%».
        this.refreshBadges = function () {
            try {
                (comp.items || []).forEach(function (t) {
                    var el = body.find('.card[data-hash="' + t.hash + '"]');
                    if (!el.length) return;
                    var p = livePct(t);
                    t.progress = p;
                    var b = el.find('.qdl-dl-badge');
                    if (b.length) b.replaceWith(comp.dlBadge(t));
                });
            } catch (e) {}
        };

        this.render = function () { return html; };
        this.start = function () {
            // коллекции менялись, пока грид был в фоне (мутация в под-гриде и т.п.) → перерисовать
            if (builtStamp !== -1 && builtStamp !== colStamp) { Lampa.Activity.replace(); return; }
            // подписка ПОСЛЕ раннего выхода: иначе подписали бы компонент, который сейчас заменят
            if (!comp.pgToken) comp.pgToken = pgSubscribe(null, function () { comp.refreshBadges(); });
            comp.refreshBadges();
            Lampa.Controller.add('content', {
                toggle: function () { focusBack(scroll, last); },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };
        function pgOff() { if (comp.pgToken) { pgUnsubscribe(comp.pgToken); comp.pgToken = null; } }
        this.pause = function () { pgOff(); };
        this.stop = function () { pgOff(); };
        this.destroy = function () { pgOff(); network.clear(); scroll.destroy(); html.remove(); };
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
            try {
                if (e && e.detail && e.detail.name === 'qdl_noti') {
                    pollNotifications();
                    pgKick();   // серверный пуш «что-то докачалось» — будим замолчавший поллер прогресса
                }
            } catch (err) {}
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
            // xsmart-уведомление: сервер отдаёт ref «cat-id» (только для своих ключей). В режиме
            // «только уведомляю» карточки в «Загрузках» НЕТ вовсе — открываем карточку тайтла
            // в разделе XSMART, а не плеер по мёртвому /qdl/stream-URL. Раздел рисует ЧУЖОЙ
            // плагин: не загружен → openXsmartTitle вернёт false и мы уйдём в прежний фолбэк.
            else if (n.xsmart && openXsmartTitle(n.xsmart, n.title)) { /* открыли карточку тайтла */ }
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

    // Карточку тайтла XSMART рисует ЧУЖОЙ плагин (xsmart.js из контейнера xsmart-proxy), а не мы.
    // Если раздел не загружен, компонента в реестре нет и Activity.push увёл бы в nocomponent —
    // пустой экран. Поэтому проверяем реестр и возвращаем false, чтобы вызывающий ушёл в свой
    // фолбэк. Контракт активити — openTitle в xsmart.js (xsmart_card компонент не читает).
    function openXsmartTitle(ref, title) {
        var m = /^(\d+)-(.+)$/.exec(String(ref || ''));
        if (!m) return false;
        try {
            if (!Lampa.Component || typeof Lampa.Component.get !== 'function') return false;
            if (!Lampa.Component.get('xsmart_title')) return false;
            Lampa.Activity.push({
                url: '', title: title || 'XSMART', component: 'xsmart_title',
                xsmart_cat: parseInt(m[1], 10), xsmart_id: m[2], page: 1
            });
            return true;
        } catch (e) { return false; }
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
            comp.appendId();

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
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () { openNotification(n); });
            body.append(el);
        };

        // Айди устройства — в самом низу ленты (требование владельца): по нему выдаются права на
        // скрытые разделы в админке /admin/d1v. Строка ФОКУСИРУЕМАЯ, иначе на ТВ до неё не доскроллить;
        // по «ОК» — тот же текст крупным тостом, чтобы прочитать с дивана.
        // Показываем и на пустой ленте, и когда айди не определился (старый кеш lampainit.js, сбой
        // нативного KV) — «не определён» честнее пустого места: клиент без айди прав не получит.
        this.appendId = function () {
            var card = qdlCard || {};
            var uid = card.uid || qdlUid();
            var tail = [];
            if (card.name) tail.push(card.name);
            if (card.platform) tail.push(card.platform + (card.client ? ' ' + card.client : ''));

            var text = 'ID устройства: ' + (uid || 'не определён') + (tail.length ? '   ·   ' + tail.join('   ·   ') : '');
            var el = $('<div class="qdl-noti-id selector qdl-row-focus" style="padding:1.1em 1.4em;margin:1.2em .6em .4em;' +
                'background:rgba(255,255,255,.04);border-radius:.7em;opacity:.75;font-size:1.15em">' + esc(text) + '</div>');
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () { Lampa.Noty.show(text); });
            body.append(el);
        };

        this.render = function () { return html; };
        this.start = function () {
            Lampa.Controller.add('content', {
                toggle: function () { focusBack(scroll, last); },
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
            // 2.67: мутации коллекций закрыты правом «Управление» — сервер отвечает 403 с причиной.
            // Показываем ЕЁ, а не общее «не получилось»: иначе отозванный грант выглядит как поломка.
            else Lampa.Noty.show((r && r.error) ? r.error : 'Не получилось — попробуй ещё раз');
        }, function () { Lampa.Noty.show('Ошибка запроса к серверу'); });
    }

    function addToCollection(t, back) {
        back = back || 'content';
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
                    onBack: function () { Lampa.Controller.toggle(back); }
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
    function startTranscode(t, back) {
        back = back || 'content';
        var title = (t.meta && t.meta.title) || t.name;
        var run = function (mode) {
            req(API + '/qdl/transcode?hash=' + t.hash + (mode ? '&mode=' + mode : ''), function (r) {
                if (!r || !r.success) { Lampa.Noty.show('Транскодирование: ' + ((r && r.error) || 'ошибка')); return; }
                dropEpCache(t.hash);   // имена файлов сменятся (mkv→mp4) — кеш списка серий устареет
                if (r.queued > 1) Lampa.Noty.show('🎬 В очереди (' + r.queued + ') — сообщу о прогрессе');
                else if (r.files > 1) Lampa.Noty.show('🎬 Транскодирование запущено (' + r.files + ' серий) — сообщу о прогрессе');
                else Lampa.Noty.show('🎬 Транскодирование запущено — это займёт заметное время, сообщу о прогрессе');
                pollTranscode(t.hash, title);
            }, function () { Lampa.Noty.show('Транскодирование недоступно — нет права или сервер недоступен'); });
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
                onSelect: function (a) { if (a.mode) run(a.mode); else Lampa.Controller.toggle(back); },
                onBack: function () { Lampa.Controller.toggle(back); }
            });
        }, function () { run(null); });
    }

    // Слежение за новыми сериями — по infohash РАЗДАЧИ (контур торрентов; у jut.su/XSMART свои).
    function watchToggle(item, done) {
        if (item.watched)
            req(API + '/qdl/watch/remove?hash=' + item.hash, function () {
                item.watched = false; Lampa.Noty.show('Слежение выключено'); if (done) done();
            });
        else
            req(API + '/qdl/watch?hash=' + item.hash, function (r) {
                if (r && r.success) { item.watched = true; Lampa.Noty.show('✓ Слежу за новыми сериями'); }
                else Lampa.Noty.show('Не вышло — перекачай раздачу и попробуй снова');
                if (done) done();
            });
    }

    // Удаление пачкой (склеенная карточка = несколько раздач). Строго ПОСЛЕДОВАТЕЛЬНО:
    // /qdl/delete чистит общие структуры (watch.json, коллекции, activity), и параллельные
    // вызовы гонялись бы за одни и те же файлы. Первая ошибка останавливает цепочку — молча
    // удалить половину сериала и отрапортовать «готово» нельзя.
    function deleteHashes(hashes, done) {
        var i = 0;
        (function next() {
            if (i >= hashes.length) { done(true); return; }
            var h = hashes[i++];
            req(API + '/qdl/delete?hash=' + h + '&deleteFiles=true', function () {
                dropAudioPref(h);   // подчистить запомненную озвучку (localStorage)
                dropEpCache(h);     // и кеш списка серий
                next();
            }, function () { done(false); });
        })();
    }

    // ctx: { collection } — под-грид коллекции; { back, catalog } — открыто долгим нажатием с
    // карточки КАТАЛОГА (qdl 2.108, onCardMenu): back — контроллер, которому вернуть управление
    // ('items_line' на главной/в поиске, а не 'content'), catalog — экран не наш, поэтому
    // Activity.replace() после удаления не делаем.
    function quickMenu(t, ctx) {
        var back = (ctx && ctx.back) || 'content';
        var items = [
            { title: 'Открыть карточку', act: 'page' },
            { title: '▶ Смотреть (оффлайн)', act: 'play' },
            { title: '🔊 Озвучка', act: 'audio' }
        ];
        if (canTranscode(t) && qdlManage()) items.push({ title: '🎬 Транскодировать в MP4 (для браузера)', act: 'mp4' });
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
        // XSMART — третий контур: подписка на (тайтл, сезон) в своём файле и своих ручках.
        // ⚠️ Как и jut-карточка, xsmart-карточка ВСЕГДА local — торрентная ветка ниже её не
        // ловит, и без этой ветки пункта не было видно вообще (жалоба владельца).
        // ⚠️ «Следить» ЗДЕСЬ означает «качать новые серии» (паритет с jut.su): понижение до
        // «только уведомляю» живёт на карточке тайтла в разделе XSMART.
        else if (xsCanWatch(t)) {
            var xm = xsMode(t);
            items.push({
                title: xm === 'grab' ? '🔔 Не следить за новыми сериями'
                     : xm === 'notify' ? '🔔 Слежу: только уведомления…'
                     : '🔔 Следить: качать новые серии',
                act: 'xswatch'
            });
        }
        else if (!t.local && t.state !== 'local')
            items.push({ title: t.watched ? '🔔 Не следить за новыми сериями' : '🔔 Следить за новыми сериями', act: 'watch' });
        // Ожидание СЛЕДУЮЩЕГО сезона (qdl 2.79) — отдельный пункт, а не режим слежения выше:
        // то следит за раздачей (её новыми сериями), это ждёт сезон, которого ещё нет в природе.
        // Пункт идёт рядом и виден ВСЕГДА у сериала — в том числе когда всё скачано и следить
        // уже не за чем (жалоба владельца по «Телохранителям»).
        if (canSeasonWait(t)) {
            var swf = seasonWaitFrom(t);
            items.push({
                title: swf ? '⏳ Жду ' + swf + ' сезон — отменить' : '⏳ Ждать следующий сезон',
                act: 'seasonwait'
            });
        }
        // в под-гриде коллекции — «Убрать», в общем гриде/карточке — «Добавить».
        // qdl 2.67: коллекции — тоже «Управление» (решение владельца), сервер их мутации гейтит.
        if (qdlManage()) {
            if (ctx && ctx.collection) items.push({ title: '📁 Убрать из коллекции', act: 'uncol' });
            else items.push({ title: '📁 Добавить в коллекцию', act: 'addcol' });
            items.push({ title: '🗑 Удалить (с файлами)', act: 'del' });
        }

        Lampa.Select.show({
            title: (t.meta && t.meta.title) || t.name,
            items: items,
            onSelect: function (b) {
                if (b.act === 'page') openDownload(t);
                else if (b.act === 'play') watch(t);
                else if (b.act === 'audio') {
                    // 2.78: id дорожки специфичен для КОНКРЕТНОГО рипа, поэтому на склеенной
                    // карточке озвучка запоминается сезону, а не «сериалу вообще»
                    withPart(t, 'Озвучка — какой сезон?', function (it) {
                        req(API + '/qdl/audio?hash=' + it.hash + '&index=-1', function (opts) {
                            opts = opts || [];
                            if (!opts.length) { Lampa.Noty.show('Аудиодорожек не найдено'); return; }
                            Lampa.Select.show({
                                title: 'Озвучка',
                                items: opts.map(function (o) { return { title: o.label, id: o.id }; }),
                                onSelect: function (s) { setAudioPref(it.hash, s.id); Lampa.Noty.show('Озвучка: ' + s.title); },
                                onBeforeClose: function () { Lampa.Controller.toggle(back); return true; },
                                onBack: function () { Lampa.Controller.toggle(back); }
                            });
                        });
                    }, null, back);
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
                            else Lampa.Controller.toggle(back);
                        },
                        onBack: function () { Lampa.Controller.toggle(back); }
                    });
                }
                else if (b.act === 'xswatch') {
                    // «Загрузки» — точка, где включается автоскачивание. Понижение до
                    // «только уведомления» живёт на карточке тайтла в разделе XSMART.
                    var xc = t.xsmart.cat, xi = t.xsmart.id;
                    var applyXm = function (now) {
                        t.xsmart.watch = now;
                        t.watched = now !== 'off';   // отметка в гриде — поле, общее с торрентами
                    };
                    var xmNow = xsMode(t);
                    if (xmNow === 'off') xsWatchSet(xc, xi, 'off', 'grab', applyXm);
                    else if (xmNow === 'grab') xsWatchSet(xc, xi, 'grab', 'off', applyXm);
                    else Lampa.Select.show({
                        // подписка сделана с карточки тайтла (только уведомления) — из «Загрузок»
                        // её можно поднять до скачивания или снять совсем
                        title: 'Новые серии — XSMART',
                        items: [
                            { title: '⬇ Качать новые серии', subtitle: 'сейчас только уведомления', want: 'grab' },
                            { title: '🔕 Не следить', want: 'off' },
                            { title: 'Отмена' }
                        ],
                        onSelect: function (a) {
                            if (a.want) xsWatchSet(xc, xi, 'notify', a.want, applyXm);
                            else Lampa.Controller.toggle(back);
                        },
                        onBack: function () { Lampa.Controller.toggle(back); }
                    });
                }
                else if (b.act === 'watch') {
                    // слежение живёт на КОНКРЕТНОЙ раздаче (по её infohash), поэтому на склеенной
                    // карточке спрашиваем сезон: новые серии приходят в последний
                    withPart(t, 'Следить за новыми сериями', function (it) { watchToggle(it); }, null, back);
                }
                else if (b.act === 'seasonwait') {
                    // 🔴 БЕЗ withPart: маркер живёт на сериале, а не на раздаче. Спрашивать
                    // «какой сезон» тут бессмысленно — withPart перечисляет уже СКАЧАННОЕ,
                    // а ждём мы тот, которого ещё нет.
                    seasonWaitToggle(t);
                }
                else if (b.act === 'mp4') withPart(t, 'Транскодировать — какой сезон?', function (it) { startTranscode(it, back); }, null, back);
                else if (b.act === 'addcol') addToCollection(t, back);
                else if (b.act === 'uncol') {
                    colPost('/qdl/collections/remove', { id: ctx.collection.id, hash: t.hash }, function (r) {
                        if (r.deleted) { Lampa.Noty.show('Коллекция удалена — это был последний фильм'); Lampa.Activity.backward(); }
                        else { Lampa.Noty.show('Убрано из коллекции'); Lampa.Activity.replace(); }
                    });
                }
                else if (b.act === 'del') {
                    // 2.78: на склеенной карточке сперва выбор — весь сериал или один сезон.
                    // Молча сносить всю группу нельзя: сезоны — отдельные раздачи, и «удалить
                    // лишний сезон» обязано остаться отдельным действием.
                    var dtitle = (t.meta && t.meta.title) || t.name;
                    withPart(t, 'Удалить с файлами: «' + dtitle + '»', function (it, all) {
                        var hashes = all ? all.map(function (p) { return p.hash; }) : [it.hash];
                        var what = all ? '«' + dtitle + '» целиком (раздач: ' + hashes.length + ')'
                                       : (it.season > 0 ? 'сезон ' + it.season + ' — «' + dtitle + '»' : '«' + dtitle + '»');
                        // подтверждение: одно случайное нажатие не должно безвозвратно удалять файлы
                        Lampa.Select.show({
                            title: 'Удалить ' + what + ' с файлами?',
                            items: [{ title: 'Удалить', ok: true }, { title: 'Отмена' }],
                            onSelect: function (a) {
                                if (!a.ok) { Lampa.Controller.toggle(back); return; }
                                deleteHashes(hashes, function (ok) {
                                    // 2.67: в ошибку приходит и 403 «нет права управления» (протухший
                                    // клиент или отозванный грант) — без колбэка экран молча ничего не делал
                                    Lampa.Noty.show(ok ? 'Удалено' : 'Не удалось удалить — нет права или сервер недоступен');
                                    // с карточки каталога перерисовывать чужой экран незачем
                                    if (!(ctx && ctx.catalog)) Lampa.Activity.replace();
                                });
                            },
                            onBack: function () { Lampa.Controller.toggle(back); }
                        });
                    }, '🗑 Весь сериал', back);
                }
            },
            // 🔴 Штатный Select после ВЫБОРА пункта контроллер не восстанавливает (активным остаётся
            // 'select' над скрытым списком): путь «выбор» закрывает onBeforeClose, путь «назад» —
            // onBack. Ровно так делает штатное меню карточки Lampa.
            onBeforeClose: function () { Lampa.Controller.toggle(back); return true; },
            onBack: function () { Lampa.Controller.toggle(back); }
        });
    }

    // ───────── Долгое нажатие на карточке КАТАЛОГА → наше меню (qdl 2.108) ─────────
    // Бандл (AppPatch card-menu / card-menu-legacy) перед сборкой штатного меню закладок шлёт
    // событие 'qdl_card' {type:'menu', data, params, enabled, handled}. Берём его на себя:
    // скачанный тайтл → quickMenu «Загрузок» (следить / ждать сезон / озвучка / коллекции /
    // удалить), нескачанный → «Скачать». handled=true — штатное меню не строится. Отказ
    // (handled не трогаем) → всё как раньше: карточки «Клубнички» (у них закладки — единственный
    // способ добавить видео в избранное) и персон.
    //
    // 🔴 e.data — ТОТ ЖЕ объект, что лежит в ряду и в клиентском кеше Request: не мутировать
    // (в т.ч. не дописывать media_type). Тип выводим так же, как сама Lampa при открытии
    // карточки (method: movie.name ? 'tv' : 'movie'): после конструктора карточки у сериала есть
    // и title, и release_date (Arrays.extend), поэтому slimCard здесь соврал бы.
    // 🔴 e.enabled — имя контроллера, которому вернуть управление. На главной / в категориях /
    // в поиске при фокусе на карточке это 'items_line', а не 'content' (строка регистрирует
    // свой контроллер); жёсткое 'content' из поиска уводило бы клавиши под оверлей.
    function cardIsTitle(data, params) {
        if (!data || data.id == null || data.id === '') return false;
        if (!(data.title || data.name)) return false;
        if (params && params.card_collection) return false;                                    // «Клубничка»
        if (data.gender != null || data.profile_path || data.known_for_department) return false;  // персона
        return true;
    }

    function cardType(data) {
        if (!data) return 'movie';
        if (data.media_type === 'tv' || data.media_type === 'movie') return data.media_type;
        return data.name ? 'tv' : 'movie';
    }

    // копия карточки с выставленным типом — для «Скачать» (saveMeta/slimCard читают media_type)
    function cardCopy(data, type) {
        var c = {};
        for (var k in data) if (Object.prototype.hasOwnProperty.call(data, k) && k !== 'params') c[k] = data[k];
        c.media_type = type;
        return c;
    }

    function selectOpen() {
        try { return !!(document.body && document.body.classList.contains('selectbox--open')); } catch (e) { return false; }
    }

    function onCardMenu(e) {
        if (!e || e.type !== 'menu' || !e.data) return;
        // Уже открыт селектбокс → это второе событие: на десктопе удержание ПРАВОЙ кнопки ≥800 мс
        // даёт hover:long дважды (таймер mousedown + contextmenu). Проглатываем: иначе
        // enabled='select', и после закрытия управление вернулось бы скрытому списку.
        if (e.enabled === 'select' || selectOpen()) { e.handled = true; return; }
        if (!cardIsTitle(e.data, e.params)) return;

        e.handled = true;
        var data = e.data;
        var back = e.enabled || 'content';
        var type = cardType(data);
        var probe = {
            id: data.id, media_type: type, source: data.source,
            title: data.title, name: data.name,
            original_title: data.original_title, original_name: data.original_name
        };
        var act = null;
        try { act = Lampa.Activity.active(); } catch (err) {}
        // Зазор: пока шёл /qdl/list, Enter мог открыть карточку или всплыл другой селектбокс —
        // тогда меню молча не показываем (легло бы поверх чужого экрана со старым enabled).
        var stale = function () {
            var a = null;
            try { a = Lampa.Activity.active(); } catch (err) {}
            return a !== act || selectOpen();
        };
        req(API + '/qdl/list', function (list) {
            if (stale()) return;
            var hit = findDownload(list || [], probe);
            if (hit) quickMenu(hit, { back: back, catalog: true });
            else catalogMenu(data, back, type);
        }, function () {
            if (stale()) return;
            catalogMenu(data, back, type);
        });
    }

    // Меню НЕскачанной карточки: «Скачать» (тот же поиск раздач, что у кнопки на экране
    // карточки) и «Открыть карточку». Пунктов «следить» / «ждать сезон» здесь нет сознательно:
    // оба контура привязаны к скачанному (слежение — к раздаче, ожидание сезона — к скачанным
    // сезонам); после загрузки они появятся в этом же меню.
    function catalogMenu(movie, back, type) {
        back = back || 'content';
        type = type || cardType(movie);
        Lampa.Select.show({
            title: movie.title || movie.name || '',
            items: [
                { title: '⬇ Скачать на сервер', act: 'download' },
                { title: 'Открыть карточку', act: 'page' }
            ],
            onSelect: function (b) {
                if (b.act === 'download') chooseAndDownload(cardCopy(movie, type), back);
                else if (b.act === 'page') {
                    // как сама Lampa по Enter на карточке: component 'full', method по типу
                    Lampa.Activity.push({
                        url: '', component: 'full', id: movie.id, method: type,
                        card: movie, source: movie.source || 'tmdb'
                    });
                }
            },
            onBeforeClose: function () { Lampa.Controller.toggle(back); return true; },
            onBack: function () { Lampa.Controller.toggle(back); }
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

    function chooseAndDownload(movie, back) {
        movie = movie || {};
        back = back || 'content';
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
                    Lampa.Controller.toggle(back);
                    if (qdlManage() && (a.t.codec === 'hevc' || a.t.codec === 'av1'))
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
                onBack: function () { Lampa.Controller.toggle(back); }
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
    function addContinueButton(render, cont, hash, name, gateItem, card) {
        fetchEpisodes(hash, function (files) {
            var vids = mergedVideoFiles(files);
            if (!vids.length) return;
            // прогрев и здесь: покрывает восстановленную активность и зелёную «Смотреть»
            // на обычной карточке — пути, где openDownload/prewarmForCard не звались.
            // Дубль с prewarmForCard безвреден: сервер дедупит и очередь, и «уже прогрет».
            if (vids.length === 1) { warmup(srcHash(vids[0], hash), vids[0].index); return; }   // фильм/один файл
            // 2.93: только докачанные — иначе кнопка ведёт в серию, которую экран серий запрёт
            var ready = vids.filter(function (f) { return epReady(f, hash); });
            var target = chooseContinue(ready, function (f) { return pickTimeline(hash, f); });
            var warm = target || ready[0] || vids[0];
            warmup(srcHash(warm, hash), warm.index);
            var exist = $('.qdl-continue-btn', render);
            if (!target) { if (exist.length) { exist.remove(); orderButtons(cont); } return; }
            if (exist.length) { exist.children('span').text('Продолжить · ' + epShort(target.name)); return; }
            var label = 'Продолжить · ' + epShort(target.name);
            var b = $('<div class="full-start__button selector qdl-continue-btn">' + CONTINUE_ICON + '<span>' + esc(label) + '</span></div>');
            b.on('hover:enter', function () {
                // через экран серий с автоплеем: «назад» из плеера вернёт в список серий.
                // Гейта здесь нет намеренно: кнопка живёт только у сериала (vids.length >= 2),
                // а недокачанные серии запирает сам экран серий — построчно (qdl 2.93).
                chooseEpisode(hash, name, true, card || (gateItem && gateItem.meta));
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
    // qdl 2.93: сюда же переехала подписка «текущей карточки» на живой прогресс. addButton —
    // обработчик события, у него нет ни pause, ни destroy, поэтому подписывать из него нельзя.
    // Здесь ОДИН слот, следующий за передним планом: утечь больше чем одной подпиской физически
    // не может. Свежий процент рисует подпись «Смотреть · 62%» и кормит гейт.
    var _pgCardToken = null;

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
                addContinueButton(render, cont, hash, movie.title || movie.name || '', { hash: hash }, movie);
            } catch (e) {}
        };
        var follow = function () {
            try {
                var act = Lampa.Activity.active() || {};
                var hash = act.qdl_hash || null;
                if (_pgCardToken) { pgUnsubscribe(_pgCardToken); _pgCardToken = null; }
                if (!hash) return;
                _pgCardToken = pgSubscribe(hash, function () { paintCardWatch(); });
                paintCardWatch();
            } catch (e) {}
        };
        try { Lampa.Listener.follow('activity', function (e) { if (e && e.type === 'start') { sync(); follow(); } }); } catch (e) {}
        try { Lampa.Player.listener.follow('destroy', function () { sync(); pgKick(); }); } catch (e) {}
    }

    // «Смотреть» на карточке из «Загрузок» → «Смотреть · 62%», пока раздача не готова.
    // Патчим текст того же узла: на кнопке живёт фокус пульта, пересоздание его бы уронило.
    // ✅ Безопасно для порядка кнопок: buttonRank сортирует по КЛАССАМ, а ensureOrderObserver
    // слушает {childList:true} без subtree — text() на внуке его не будит.
    function paintCardWatch() {
        try {
            var act = Lampa.Activity.active() || {};
            if (!act.qdl_hash || !act.activity) return;
            var sp = $('.qdl-watch-btn span', act.activity.render());
            if (!sp.length) return;
            var live = pgGet(act.qdl_hash);
            var p = live && typeof live.p === 'number' ? live.p
                  : (typeof act.qdl_progress === 'number' ? act.qdl_progress : 1);
            sp.text(p >= DONE ? 'Смотреть' : 'Смотреть · ' + Math.round(p * 100) + '%');
        } catch (e) {}
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
                    // 🔴 Поля progress здесь БОЛЬШЕ НЕТ. Снимок qdl_progress брался в момент
                    // открытия карточки и не обновлялся никогда — отсюда и «докачалось, а
                    // клиент всё равно спрашивает». Прогресс берёт watchByHash: живой у поллера,
                    // иначе per-file из /qdl/episodes, иначе fail-open.
                    w.on('hover:enter', function () {
                        watchByHash(active.qdl_hash, movie.title || movie.name, movie, { hash: active.qdl_hash });
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
                    paintCardWatch();   // первый процент — сразу, не дожидаясь тика поллера
                }
                // сериал с прогрессом просмотра → вторая кнопка «Продолжить: Серия N»
                addContinueButton(render, cont, active.qdl_hash, movie.title || movie.name,
                    { hash: active.qdl_hash }, movie);
                return;   // НЕ добавляем «Скачать», прочие кнопки скрыты
            }

            // Сервер-реплика — только чтение: на ней «Скачать» упрётся в 403 (ReplicaReadOnlyDeny),
            // а всё, что там лежит, приезжает из дома. Кнопку не рисуем вовсе.
            if (!window.qdl_replica && !$('.qdl-download', render).length) {
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
                    play.on('hover:enter', function () { watch(hit, movie); });
                    cont.prepend(play);
                    navAppend(cont, play);   // приехала async → в коллекции навигатора её ещё нет
                    orderButtons(cont);
                    addContinueButton(render, cont, hit.hash, (hit.meta && hit.meta.title) || hit.name, hit, movie);
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
    function liveDayCancel() { liveDayToken++; clearTimeout(liveWarmTimer); }

    // ── Прогрев суток ──
    // Первый же /qdl/live/day будит на регистраторе склейку ВСЕХ суток, поэтому наводка на камеру —
    // уже достаточный повод его дёрнуть: пока зритель дочитывает строку и жмёт Enter, день чаще
    // всего успевает домолоться целиком, и плеер получает полную ленту, а не готовый огрызок.
    // Дебаунс обязателен: пультом фокус пробегает по всему списку, будить каждую камеру по пути —
    // это лишний ремукс и лишние гигабайты кэша на регистраторе.
    var liveWarmTimer = null, liveWarmed = {};

    function liveWarmDay(cam, date) {
        clearTimeout(liveWarmTimer);

        var key = cam.id + ':' + (date || '');
        if (liveWarmed[key]) return;

        liveWarmTimer = setTimeout(function () {
            liveWarmed[key] = 1;
            req(API + '/qdl/live/day?camera=' + encodeURIComponent(cam.id) + (date ? '&date=' + encodeURIComponent(date) : ''),
                function () {},
                function () { delete liveWarmed[key]; });   // не вышло — пусть следующая наводка попробует снова
        }, 700);
    }

    // Сколько ждём ПОЛНЫЕ сутки, прежде чем играть готовый префикс. Ремукс идёт ~113× реального
    // времени, а прогрев выше обычно успевает раньше нажатия — так что ожидание чаще всего нулевое.
    // Не дождались — играем то, что готово: плейлист всё равно самозавершённый, перемотка по нему
    // работает, просто лента короче суток.
    var LIVE_DAY_WAIT_MS = 20000;

    function livePlayDay(cam, date, label) {
        var my = ++liveDayToken;
        var tries = 0;
        var started = Date.now();

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
                url: withUid(API + info.path)
            };
            try {
                var tl = Lampa.Timeline.view(Lampa.Utils.hash('qdlliveday:' + cam.id + ':' + info.date));
                if (tl) {
                    // За текущий день запись РАСТЁТ (доезжают новые куски), а процент Lampa посчитала
                    // от прежней длины: досмотренный «конец дня» становился 98–100% и потом блокировал
                    // продолжение (Lampa не предлагает докрутку при percent ≥ 90). Позиция в секундах
                    // остаётся верной — пересчитываем процент от новой длины.
                    if (info.seconds && tl.time > 0) {
                        // ⚠️ Позиция может оказаться ЗА концом того, что сейчас собрано: вчера день
                        // досмотрели до конца, сегодня открыли его же, а префикс ещё короче. Плейлист
                        // самозавершённый, и seek за ENDLIST — это seek за конец потока: плеер либо
                        // сразу выкинет зрителя, либо встанет. Зажимаем.
                        if (tl.time > info.seconds - 5) tl.time = Math.max(0, info.seconds - 5);

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

                    // всё готово, но играть нечего — все куски битые
                    if (info.complete && !info.ready) { stop('Записи за этот день не читаются'); return; }

                    // Сутки собраны целиком — обычный случай после прогрева.
                    if (info.complete) { fire(info); return; }

                    // Ещё домалываются. Ждём полную ленту, но не бесконечно: терпение вышло —
                    // играем готовый префикс (раньше играли его сразу, и таймлайн был короче суток).
                    if (info.ready > 0 && (Date.now() - started) >= LIVE_DAY_WAIT_MS) { fire(info); return; }

                    if (++tries > 45) { stop('Регистратор слишком долго готовит запись'); return; }
                    if (tries % 5 === 0) Lampa.Noty.show('Готовлю запись: ' + info.ready + ' из ' + info.total);
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
            var item = { title: r.start + ' – ' + r.end + '   ·   ' + (cam.name || 'Камера'), url: withUid(API + '/qdl/live/stream?id=' + r.id) };
            var tl = liveTimeline(r);
            if (tl) item.timeline = tl;
            return item;
        });
        index = Math.max(0, Math.min(index || 0, playlist.length - 1));
        Lampa.Player.play(playlist[index]);
        Lampa.Player.playlist(playlist);
    }

    // Список дней просим на всю глубину архива регистратора (30 суток), иначе сервер режет
    // окно по liveDaysBack=14 — ровно это и выглядело как «записи только за текущий месяц».
    var LIVE_DAYS_BACK = 31;

    /// Выбор дня одним списком (используют и экран дня, и лента). onPick(dateKey).
    function livePickDay(currentDate, onPick) {
        var net = new Lampa.Reguest();
        net.silent(withUid(API + '/qdl/live/days?back=' + LIVE_DAYS_BACK), function (r) {
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
                        selected: d.date === currentDate
                    };
                }),
                onSelect: function (a) { Lampa.Controller.toggle('content'); onPick(a.date); },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }, function () { Lampa.Noty.show('Видеорегистратор не отвечает'); });
    }

    /// Лента всех записей: свежие сверху, старые подтягиваются прокруткой.
    function openRecFeed() {
        Lampa.Activity.push({ url: '', title: 'Все записи', component: 'qdl_rec_feed', page: 1 });
    }

    // ── D1versy Live: ЭФИР — сетка подключённых камер ──
    // С qdl 2.95 тайл — ЖИВОЕ видео (rolling-HLS через наш прокси /qdl/live/watch/*), кадр
    // остался подложкой и режимом при выключенном тумблере. Поток на регистраторе общий
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
        v.src = withUid(API + '/qdl/live/watch/hls/' + encodeURIComponent(cam.id) + '/index.m3u8');
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
                        // 🔴 withUid обязателен: плейлист уходит в НАТИВНЫЙ плеер (VLC на Android/маке,
                        // LibVLCSharp на Windows), а тот не несёт ни cookie, ни заголовков — айди
                        // устройства живёт только в query. Без него гейт прав (LiveDenied) отдаёт 404
                        // и эфир падает в «Не удалось воспроизвести». У iOS своя ветка
                        // (liveWatchPlayIOS), там withUid стоял — потому айфон и продолжал играть.
                        var item = { title: (cam.name || 'Камера') + '   ·   Эфир', url: withUid(API + st.path) };
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

    // ── ЖИВОЙ ЭФИР В ПЛИТКАХ (qdl 2.95) ──
    // Прежнее решение «сетка на ТВ — это тайлы, а не видеостена» (claude/06 §AL2) отменено
    // замерами, а не на глаз: поток каждой камеры — H.264 Main@L3.1 720p25 ~2 Мбит/с, декодер
    // домашнего ТВ (MT5896) паспортно держит 720p на 510 fps при 16 инстансах, а нам нужно 100;
    // сам сервер на зрителя не тратит ничего — ffmpeg крутит эти потоки 24/7 в любом случае.
    // Разбор с числами — claude/06 §DB.
    //
    // Три правила, на которых всё держится:
    //  1. играют ТОЛЬКО плитки в кадре и не больше LIVE_MAX_PLAYERS — прокрутил ниже, верхние гаснут;
    //  2. тумблер «Видео» гасит стриминг на живую, без перезахода в раздел;
    //  3. pause()/stop()/destroy() ОБЯЗАНЫ снести все плееры: Lampa на forward-навигации зовёт
    //     только pause(), и без этого каждый вход в раздел оставлял бы позади ещё четыре
    //     живых декодера (та же грабля, что убила таймер сетки в §AL2).
    var LIVE_MAX_PLAYERS = 4;

    // Пороги сторожей эфира. Вынесены в объект, чтобы тесты гоняли их за миллисекунды, а не
    // за минуты (тот же приём, что у поллера прогресса — setProgressConf).
    var LIVE_GUARD = {
        beat: 2000,       // как часто плеер смотрит на себя сам
        soft: 5000,       // развёрнутая камера: столько терпим замерший currentTime
        stale: 4000,      // картинка не двигалась дольше — плеер уже не считается живым
        softGrid: 15000,  // плитка в сетке: там торопиться некуда
        tick: 2000,       // как часто сторож фулл вью смотрит на развёрнутую камеру
        dead: 10000,      // нет картинки столько → первый жёсткий перезапуск
        again: 15000,     // и дальше каждые столько, бесконечно (выбор владельца)
        young: 3000,      // плеер моложе этого ещё раскачивается, под нож не идёт
        wake: 15000       // 🔴 пол по /watch/start на камеру: регистратор пишет видео 24/7
    };

    // Настройка ОДНА на весь дом (решение владельца: «я у себя включил — и оно на все девайсы»):
    // значение живёт на сервере и приезжает ключом live.video в /qdl/features, который клиент
    // и так перечитывает каждые 60 с. Переключается в настройках, раздел «D1Vision».
    function liveVideoGlobal() {
        try { if (qdlCard && qdlCard.live) return qdlCard.live.video !== false; } catch (e) {}
        return true;   // файла на сервере нет / права ещё не приехали — эфир включён
    }

    // 🔴 На iPhone это вопрос не настройки, а системы: WKWebView собран без снятого
    // mediaTypesRequiringUserActionForPlayback, автоплей запрещён, и глобальное «включено»
    // не сделает видео возможным. Там плитки остаются кадрами, а тап уводит в нативный
    // плеер iOS (liveWatchPlayIOS) — этот путь не трогаем.
    function liveVideoOn() {
        if (window.d1vision_platform === 'ios') return false;
        return liveVideoGlobal();
    }

    /// Зеркалим ответ сервера в кеш прав, чтобы открытый экран эфира подхватил смену сразу,
    /// не дожидаясь следующего опроса /qdl/features.
    function liveVideoSet(on) {
        try {
            if (!qdlCard) qdlCard = {};
            qdlCard.live = qdlCard.live || {};
            qdlCard.live.video = !!on;
        } catch (e) {}
    }

    // hls.js приезжает АСИНХРОННО: бандл Lampa грузит ./vender/hls/hls.js через putScriptAsync,
    // и в момент открытия раздела window.Hls может ещё не существовать.
    function liveHlsReady(cb, dead) {
        var tries = 0;
        (function wait() {
            if (dead && dead()) return;              // плитку успели увести с экрана — ждать некому
            if (window.Hls && window.Hls.isSupported) { cb(window.Hls); return; }
            if (++tries > 40) { cb(null); return; }   // 40 x 250 мс = 10 с, дальше сдаёмся честно
            setTimeout(wait, 250);
        })();
    }

    /// Живой плеер одной плитки. box — DOM плитки, note(text) рисует надпись поверх кадра.
    /// Поведение при сбое — как у оригинального Live View: не подменяем картинку молча,
    /// а пишем «Переподключаюсь…» и грузим снова.
    function liveMakePlayer(box, url, note, poster) {
        var video = document.createElement('video');
        video.muted = true;
        video.autoplay = true;
        video.setAttribute('playsinline', '');
        video.setAttribute('muted', '');
        // Пока не пришёл первый декодированный кадр, показываем СНИМОК С КАМЕРЫ, а не пустой
        // прямоугольник (требование владельца). Постер и прозрачный фон работают вместе:
        // под видео лежит тот же кадр отдельной картинкой — что бы ни отвалилось, серого нет.
        if (poster) { try { video.poster = poster; } catch (e) {} }

        var st = { video: video, hls: null, dead: false, retry: null, watch: null, seen: -1,
                   born: Date.now(), lastMove: Date.now(), lastStep: 0, urgent: false, step: 0 };

        /// Есть ли у плитки ДЕКОДИРОВАННАЯ картинка. Отдельно от alive(), потому что соседи в
        /// фулл вью стоят на паузе намеренно — они не «мертвы», у них просто нажата пауза.
        st.ok = function () {
            return !st.dead && (video.readyState || 0) >= 2 && (video.currentTime || 0) > 0;
        };

        /// Идёт ли поток ПРЯМО СЕЙЧАС. Наличие <video> в DOM не доказывает ничего: камера может
        /// висеть на первом кадре, и fatal-ошибки при этом нет — hls.js просто молчит.
        st.alive = function () {
            return st.ok() && !video.paused && Date.now() - st.lastMove < LIVE_GUARD.stale;
        };

        st.destroy = function () {
            st.dead = true;
            clearTimeout(st.hint);
            clearTimeout(st.retry);
            clearInterval(st.watch);
            if (st.hls) { try { st.hls.destroy(); } catch (e) {} st.hls = null; }
            try { video.pause(); } catch (e) {}
            try { video.removeAttribute('src'); video.load(); } catch (e) {}   // канонический release потока в WebKit
            try { if (video.parentNode) video.parentNode.removeChild(video); } catch (e) {}
        };

        function say(t) { try { note(t); } catch (e) {} }

        function play() {
            var p;
            try { p = video.play(); } catch (e) { say('Нажми, чтобы включить'); return; }
            if (p && p.catch) p.catch(function () { say('Нажми, чтобы включить'); });
        }

        video.addEventListener('playing', function () { st.lastMove = Date.now(); say(''); });

        box.appendChild(video);
        // Надпись показываем, только если картинки долго нет: на горячем потоке видео
        // появляется за секунду, и мигать плашкой ради этого незачем.
        st.hint = setTimeout(function () { if (!st.dead && video.paused) say('Соединяюсь…'); }, 3000);

        if (video.canPlayType && video.canPlayType('application/vnd.apple.mpegurl')) {
            // Мак/айфон: HLS в <video> нативно, hls.js не нужен вовсе.
            video.src = url;
            play();
        }
        else liveHlsReady(function (Hls) {
            if (st.dead) return;
            if (!Hls || !Hls.isSupported()) { say('Видео не поддерживается'); return; }

            // Кап буферов — не косметика: на домашнем ТВ свободно ~800 МБ, а дефолт hls.js
            // тянет 30-60 с вперёд на КАЖДЫЙ из четырёх потоков.
            var hls = new Hls({
                enableWorker: true,
                lowLatencyMode: true,
                backBufferLength: 10,
                maxBufferLength: 10,
                maxMaxBufferLength: 20,
                liveSyncDurationCount: 3,
                liveMaxLatencyDurationCount: 6
            });
            st.hls = hls;
            hls.loadSource(url);
            hls.attachMedia(video);
            hls.on(Hls.Events.MANIFEST_PARSED, play);
            hls.on(Hls.Events.ERROR, function (e, data) {
                if (st.dead || !data || !data.fatal) return;
                if (data.type === Hls.ErrorTypes.NETWORK_ERROR) {
                    // Watchdog регистратора при рестарте ffmpeg СНАЧАЛА удаляет плейлист (окно
                    // 4-8 с) — это норма, а не смерть эфира. Ждём и грузим снова, как оригинал.
                    say('Переподключаюсь…');
                    clearTimeout(st.retry);
                    st.retry = setTimeout(function () {
                        if (!st.dead && st.hls) { try { st.hls.startLoad(); } catch (er) {} }
                    }, 3000);
                }
                else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
                    say('Переподключаюсь…');
                    try { hls.recoverMediaError(); } catch (er) {}
                }
                else say('Эфир недоступен');
            });
        }, function () { return st.dead; });

        // Сторож: ffmpeg может встать, а плеер об этом не узнает — ошибки нет, просто нет данных.
        // 🔴 Buffer-stall у hls.js НЕ fatal и в обработчик ERROR выше не попадает вовсе, поэтому
        // единственный честный признак — currentTime перестал расти.
        st.watch = setInterval(function () {
            if (st.dead) return;
            var t = video.currentTime || 0;
            if (t > st.seen + 0.1) { st.seen = t; st.lastMove = Date.now(); st.step = 0; return; }

            if (video.paused) {
                // В сетке пауза — так и задумано (соседи развёрнутой камеры). А вот на самой
                // развёрнутой это отбитый автоплей: жмём play(), а не лечим то, что не сломано.
                if (st.urgent) play();
                st.lastMove = Date.now();
                return;
            }

            var wait = st.urgent ? LIVE_GUARD.soft : LIVE_GUARD.softGrid;
            if (Date.now() - st.lastMove < wait) return;
            // 🔴 Ступени пейсим ОТДЕЛЬНЫМ счётчиком: если двигать им же lastMove, зависший плеер
            // выглядел бы живым для alive() — сторож фулл вью такую камеру не перезапустит никогда.
            if (Date.now() - st.lastStep < wait) return;
            st.lastStep = Date.now();
            say('Переподключаюсь…');
            // Ступень 1 — прыжок к живому краю: зависший буфер одним startLoad() не лечится,
            // плеер так и остаётся стоять на своей позиции. Ступень 2 — пересборка SourceBuffer.
            if (st.hls) {
                try {
                    if ((st.step++ % 2) === 0) {
                        if (st.hls.liveSyncPosition) video.currentTime = st.hls.liveSyncPosition;
                        st.hls.startLoad();
                    }
                    else st.hls.recoverMediaError();
                } catch (e) {}
            }
            else { try { video.load(); } catch (e) {} }
            play();
            // Прыжок к живому краю двигает currentTime САМ — если не сдвинуть отметку, следующий
            // тик примет этот скачок за ожившую картинку и сторож фулл вью решит, что всё хорошо.
            st.seen = video.currentTime || 0;
        }, LIVE_GUARD.beat);

        return st;
    }

    function ComponentLiveWatch(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div></div>');
        var grid = $('<div class="qdl-watch-grid"></div>');     // камеры в эфире: квадрат по центру
        var offGrid = $('<div class="qdl-watch-off"></div>');    // не в эфире: блок под квадратом
        var last;
        var timer = null;
        var haveTiles = false;

        var cams = [];        // снимок ответа сервера; refresh мутирует ЭТИ ЖЕ объекты
        var tiles = {};       // id -> плитка
        var players = {};     // id -> живой плеер (см. liveMakePlayer)
        var visible = {};     // id -> плитка сейчас в кадре
        var wokeAt = {};      // id -> когда последний раз будили /watch/start
        var io = null;
        var syncTimer = null;
        var visTimer = null;   // фолбек-пересчёт видимости там, где нет IntersectionObserver
        var fullId = 0;       // какая камера развёрнута на весь экран (0 — сетка)
        var fullHome = null;  // куда вернуть развёрнутую плитку: {parent, next}
        var fullTimer = null; // сторож развёрнутой камеры (в сетке его нет — см. fullGuard)
        var fullSince = 0;    // когда развёрнутая камера последний раз показывала картинку
        var fullTries = 0;    // сколько раз её уже перезапускали
        var liveCols = 2;     // сколько колонок сейчас в панели (на телефоне одна)
        var onResize = null;

        // Таймер живёт только пока экран активен: Lampa на forward-навигации НЕ зовёт destroy
        // (компонент висит в стеке до pages_save_total), и без stop в pause() каждая копия сетки
        // продолжала бы дёргать регистратор каждые 12 с из фона.
        function startTimer() { if (!timer && haveTiles) timer = setInterval(refresh, 12000); }
        function stopTimer() { if (timer) { clearInterval(timer); timer = null; } }

        this.create = function () {
            if (!qdlAllowed('live')) { denySection(); return this.render(); }

            injectCss();
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            network.silent(withUid(API + '/qdl/live/watch'),
                function (r) { comp.build(r || {}); },
                function () { comp.build({ error: 'Видеорегистратор не отвечает' }); });
            return this.render();
        };

        this.build = function (r) {
            if (comp.destroyed) return;
            cams = r.cameras || [];

            body.append(grid);   // кнопка Detection живёт в шапке Lampa (ensureLiveDetectBtn)

            if (r.error)
                body.append(liveMsg('⚠️ ' + r.error));
            else if (!cams.length)
                body.append(liveMsg('Камер не найдено.'));
            else {
                // Эфирные — в квадрат, остальные отдельным блоком ниже (возврат поведения до 2.95).
                // 🔴 Деление делается ОДИН раз, при построении: камера, поднявшаяся в эфир между
                // обновлениями, получает свежий бейдж на месте, но переезжает наверх только при
                // следующем входе в раздел. Перестройка DOM уронила бы фокус пульта — та же
                // причина, по которой refresh() никогда не перерисовывает сетку (§AL2).
                var off = [];
                cams.forEach(function (c) { if (c.live) grid.append(tile(c)); else off.push(c); });

                if (off.length) {
                    body.append($('<div class="qdl-watch-offtitle">Не в эфире</div>'));
                    body.append(offGrid);
                    off.forEach(function (c) { offGrid.append(tile(c)); });
                }
            }

            // статусы и кадры дышат сами; DOM не перестраиваем — фокус пульта не теряется
            haveTiles = cams.length > 0;
            startTimer();
            watchVisibility();

            this.activity.loader(false);
            this.activity.toggle();
            fitQuad();
            // 🔴 Повторяем несколько раз: Lampa показывает активность и досчитывает высоту
            // скролла (scroll.minus) уже ПОСЛЕ нашего построения, поэтому первый замер даёт
            // и нули, и просто устаревший top. Ловилось живьём: ряд получался 222 px вместо
            // 264, и панель не доходила до низа экрана на 80 px.
            [0, 250, 800, 2000].forEach(function (t) { setTimeout(fitQuad, t); });
            if (!onResize) {
                onResize = function () { fitQuad(); };
                try { window.addEventListener('resize', onResize); } catch (e) {}
            }
            sync();
        };

        // ── панель камер занимает ВЕСЬ экран ──────────────────────────────────────
        // Требование владельца: «панелька из 4х видео на весь экран если на телевизоре,
        // и друг за другом на айфоне». Ширину дают две колонки во всю ширину ленты, а высоту
        // ряда считаем здесь: (что осталось от вьюпорта под сеткой − зазор) / 2. Посчитать это
        // в CSS нельзя — сверху стоит шапка Lampa, её высота зависит от корневого em, который
        // Lampa пересчитывает под ширину экрана.
        //
        // 🔴 Кадр при этом подрезается по вертикали (object-fit:cover), и это осознанно:
        // область контента у Lampa примерно на высоту шапки ниже 16:9, а четыре 16:9-плитки
        // в две колонки дают ровно 16:9. Либо небольшая подрезка, либо поля по бокам —
        // владелец просил «на весь экран».
        function fitQuad() {
            if (comp.destroyed) return;
            try {
                var node = grid[0];
                if (!node || !node.parentNode) return;

                // 🔴 Ширину берём у ВЬЮПОРТА ДОКУМЕНТА, а не у контейнера: на айфоне лента
                // Lampa оказалась шире экрана, и расчёт по node.clientWidth давал плитки,
                // которые уезжали за край (владелец: «могу свайпать влево-вправо»).
                var vw = document.documentElement.clientWidth || window.innerWidth || 1280;
                var cs = window.getComputedStyle(node);
                var gap = parseFloat(cs.columnGap) || 4;
                var rowGap = parseFloat(cs.rowGap) || gap;

                // 🔴 Телефон определяем ПЛАТФОРМОЙ, а не порогом ширины: у приложения на iPhone
                // вьюпорт оказался шире 600 px, из-за чего и медиазапрос, и прежний порог молчали —
                // офлайн-камеры вставали в два столбца, а панель уезжала за экран.
                var phone = window.d1vision_platform === 'ios' || vw <= 600;
                liveCols = phone ? 1 : 2;

                // Телефон: плитки идут друг за другом во всю ширину экрана, высоту даёт 16:9.
                if (phone) {
                    var pw = Math.max(120, Math.floor(vw - gap * 2));
                    grid.removeClass('qdl-watch-grid--fit');
                    node.style.gridAutoRows = '';
                    node.style.gridTemplateColumns = pw + 'px';
                    node.style.maxWidth = vw + 'px';
                    offGrid[0].style.gridTemplateColumns = pw + 'px';
                    offGrid[0].style.maxWidth = vw + 'px';
                    return;
                }

                node.style.maxWidth = '';
                offGrid[0].style.maxWidth = '';

                var box = node.getBoundingClientRect();
                if (box.top <= 0) return;   // ещё не показали — посчитаем позже

                // 🔴 Держим плитку РОВНО 16:9 — тогда кадр виден целиком, без обрезки
                // (жалоба владельца: «на IPCamLive они не обрезаются»). Поэтому берём
                // меньшее из двух: сколько даёт ширина экрана и сколько остаётся по высоте.
                var avail = (window.innerHeight || 720) - box.top;
                var byHeight = Math.floor(((avail - rowGap) / 2) * 16 / 9);
                var byWidth = Math.floor((vw - gap) / 2);
                var w = Math.max(160, Math.min(byWidth, byHeight));
                var h = Math.round(w * 9 / 16);

                grid.addClass('qdl-watch-grid--fit');
                node.style.gridTemplateColumns = 'repeat(2, ' + w + 'px)';
                node.style.gridAutoRows = h + 'px';
                // Не в эфире — статичные кадры под панелью, им столько места не нужно.
                offGrid[0].style.gridTemplateColumns = 'repeat(2, ' + Math.round(w * 0.6) + 'px)';
            } catch (e) {}
        }

        function badgeHtml(c) {
            return c.live
                ? '<span style="background:rgba(200,30,30,.92);color:#fff;padding:.12em .55em;border-radius:.35em;font-size:.85em;font-weight:700">● LIVE</span>'
                : '<span style="background:rgba(255,255,255,.16);color:#ddd;padding:.12em .55em;border-radius:.35em;font-size:.85em">не в эфире</span>';
        }

        function tile(c) {
            var el = $(
                '<div class="selector qdl-watch-tile" data-cam="' + c.id + '">' +
                  '<img class="qdl-watch-frame">' +
                  '<div class="qdl-watch-note"></div>' +
                  '<div class="qdl-watch-ring"></div>' +
                  '<div class="qdl-watch-bar">' +
                    '<div class="qdl-watch-name">' + esc(c.name) + '</div>' +
                    '<div class="qdl-watch-badge">' + badgeHtml(c) + '</div>' +
                  '</div>' +
                '</div>'
            );
            var img = el.find('.qdl-watch-frame');
            img.attr('src', withUid(API + '/qdl/live/watch/thumb?camera=' + c.id + '&t=' + Date.now()));
            img.on('error', function () { this.src = './img/img_broken.svg'; });
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); scheduleSync(); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });

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
                // 🔴 В фулл вью OK ЗАКРЫВАЕТ его, кто бы ни считался сфокусированным. Перенос
                // плитки в body сбивает фокус Lampa на соседнюю, и без этой строки OK
                // разворачивал другую камеру вместо выхода — «стрелки забагованы».
                if (fullId) { exitFull(); return; }
                if (liveVideoOn() && !iosLiveNative()) { enterFull(c.id); return; }
                liveWatchPlay(c);   // тумблер выключен (и весь путь iOS) — прежний нативный плеер
            });
            // Нативный плеер со звуком/перемоткой остаётся под рукой всегда.
            el.on('hover:long', function () { liveWatchPlay(c); });

            tiles[c.id] = el;
            return el;
        }

        // ── кто сейчас обязан играть ──────────────────────────────────────────────
        function camById(id) {
            for (var i = 0; i < cams.length; i++) if (cams[i].id === id) return cams[i];
            return null;
        }

        function playerOpened() {
            try { return !!(Lampa.Player.opened && Lampa.Player.opened()); } catch (e) { return false; }
        }

        function note(el, text) { try { el.find('.qdl-watch-note').text(text || ''); } catch (e) {} }

        function tileDist(el) {
            try {
                var r = el.getBoundingClientRect();
                var h = window.innerHeight || 720;
                return Math.abs((r.top + r.bottom) / 2 - h / 2);
            } catch (e) { return 0; }
        }

        /// after(player) — на СОЗДАННЫЙ плеер, а не на слот: разбег стартов делает плеер
        /// асинхронным, и «включить звук сразу после startPlayer» иначе уходит в пустоту.
        function startPlayer(c, idx, after) {
            if (players[c.id]) return;
            var el = tiles[c.id];
            if (!el || !el.length) return;

            // Камера не в эфире: будим поток на регистраторе (идемпотентно) и ждём ответа —
            // плейлист появится не мгновенно, плитка пока живёт кадром.
            if (!c.path) { wake(c); return; }

            var slot = { pending: true, timerId: null };
            players[c.id] = slot;
            // Разбег стартов: не бить регистратор четырьмя запросами разом (как initDelay
            // у оригинального CameraGrid).
            slot.timerId = setTimeout(function () {
                if (comp.destroyed || players[c.id] !== slot) return;
                players[c.id] = liveMakePlayer(el[0], withUid(API + c.path), function (t) { note(el, t); },
                                               withUid(API + '/qdl/live/watch/thumb?camera=' + c.id));
                if (after) { try { after(players[c.id]); } catch (e) {} }
            }, (idx || 0) * 500);
        }

        function stopPlayer(id) {
            var p = players[id];
            if (!p) return;
            delete players[id];
            clearTimeout(p.timerId);
            if (p.destroy) p.destroy();
            var el = tiles[id];
            if (el && el.length) note(el, '');
        }

        function stopAll() { Object.keys(players).forEach(stopPlayer); }

        function setPaused(id, on) {
            var p = players[id];
            if (!p || !p.video) return;
            if (on) {
                try { p.video.pause(); } catch (e) {}
                if (p.hls) { try { p.hls.stopLoad(); } catch (e) {} }
            }
            else {
                if (p.hls) { try { p.hls.startLoad(); } catch (e) {} }
                try { var q = p.video.play(); if (q && q.catch) q.catch(function () {}); } catch (e) {}
            }
        }

        function unmute(id, on) {
            var p = players[id];
            if (!p || !p.video) return;
            p.video.muted = !on;
            if (!on) return;
            var q;
            try { q = p.video.play(); } catch (e) {}
            // Со звуком автоплей может быть запрещён политикой браузера — тихо возвращаем немой.
            if (q && q.catch) q.catch(function () {
                try {
                    p.video.muted = true;
                    var again = p.video.play();
                    if (again && again.catch) again.catch(function () {});
                } catch (e) {}
            });
        }

        /// force — перезапуск развёрнутой камеры: вдруг лёг сам поток на регистраторе.
        /// 🔴 Пол троттлинга остаётся при любом force: регистратор пишет видео 24/7, и долбить
        /// его стартами нельзя — перезапуск это про клиент, а не про рекордер.
        function wake(c, force) {
            var now = Date.now();
            if (wokeAt[c.id] && now - wokeAt[c.id] < (force ? LIVE_GUARD.wake : 20000)) return;
            wokeAt[c.id] = now;
            network.silent(withUid(API + '/qdl/live/watch/start?camera=' + encodeURIComponent(c.id)),
                function (st) {
                    if (comp.destroyed || !st || !st.ready || !st.path) return;
                    c.live = true;
                    c.path = st.path;
                    var el = tiles[c.id];
                    if (el && el.length) { el.attr('data-live', '1'); el.find('.qdl-watch-badge').html(badgeHtml(c)); }
                    sync();
                },
                function () {});
        }

        function sync() {
            if (comp.destroyed) return;

            // Тумблер выключен или поверх открыт нативный плеер — в сетке не должно остаться
            // ни одного декодера.
            if (!liveVideoOn() || playerOpened()) { stopAll(); return; }

            // В фулл вью составом рулят enterFull/exitFull: там ровно одна играющая камера,
            // остальные стоят на паузе, но живут (возврат в сетку обязан быть мгновенным).
            if (fullId) {
                var fc = camById(fullId);
                if (fc) startPlayer(fc, 0);
                return;
            }

            var cand = [];
            cams.forEach(function (c) {
                var el = tiles[c.id];
                if (!el || !el.length || !visible[String(c.id)]) return;
                // Не в эфире: будим поток на регистраторе, но слот декодера не занимаем —
                // иначе мёртвая плитка из нижней секции вытеснила бы живую камеру.
                if (!c.live) { wake(c); return; }
                cand.push({ c: c, d: tileDist(el[0]) });
            });
            cand.sort(function (a, b) { return a.d - b.d; });
            cand = cand.slice(0, LIVE_MAX_PLAYERS);

            var want = {};
            cand.forEach(function (x) { want[String(x.c.id)] = 1; });

            Object.keys(players).forEach(function (id) { if (!want[String(id)]) stopPlayer(id); });

            var fresh = 0;
            cand.forEach(function (x) {
                // Плитка вернулась из фулл вью — снимаем паузу идемпотентно: одиночный resume
                // на выходе иногда не доезжает (замер: одна камера из четырёх осталась на паузе).
                if (players[x.c.id]) { setPaused(x.c.id, false); return; }
                startPlayer(x.c, fresh++);
            });
        }

        function scheduleSync() {
            clearTimeout(syncTimer);
            syncTimer = setTimeout(function () { fitQuad(); sync(); }, 200);
        }

        function watchVisibility() {
            if (window.IntersectionObserver) {
                io = new window.IntersectionObserver(function (entries) {
                    entries.forEach(function (en) {
                        var id = en.target.getAttribute('data-cam');
                        if (en.isIntersecting) visible[id] = 1; else delete visible[id];
                    });
                    scheduleSync();
                }, { rootMargin: '25% 0px', threshold: 0.01 });
                Object.keys(tiles).forEach(function (id) { try { io.observe(tiles[id][0]); } catch (e) {} });
            }
            else {
                // Фолбек для движков без IntersectionObserver: считаем сами тем же onScreen(),
                // которым живут остальные наши экраны.
                recompute();
                visTimer = setInterval(recompute, 1000);
            }
        }

        function recompute() {
            if (comp.destroyed) return;
            Object.keys(tiles).forEach(function (id) {
                if (onScreen(tiles[id][0])) visible[id] = 1; else delete visible[id];
            });
            sync();
        }

        // ── фулл вью: одна камера на весь экран ───────────────────────────────────
        function liveCams() { return cams.filter(function (c) { return c.live; }); }

        // 🔴 Плитку на время фулл вью УНОСИМ В body. position:fixed внутри скролла Lampa
        // не покрывает экран: у скролл-контейнера есть transform, а трансформированный предок
        // становится содержащим блоком для fixed-потомков — плитка оставалась внутри ленты, и
        // поверх неё было видно соседние камеры и шапку Lampa. Поймано скриншотом: функциональная
        // проверка мерила ТОЛЬКО размер элемента, а он совпадал с вьюпортом по совпадению.
        // Перенос узла эфир не рвёт — проверено живьём: currentTime шёл 15 → 19 с без сброса.
        function takeFull(el) {
            var node = el[0];
            fullHome = { parent: node.parentNode, next: node.nextSibling };
            document.body.appendChild(node);
            el.addClass('qdl-watch-tile--full');
            // 🔴 Перенос узла в body уводит фокус Lampa на соседнюю плитку (замер: развёрнута
            // камера 1, а .focus висит на камере 3). Тогда OK в фулл вью открывал НЕ ТУ камеру,
            // а Back возвращал не на ту плитку — это и читалось как «стрелки забагованы».
            // Возвращаем фокус на саму развёрнутую плитку.
            last = node;
            markLast(el);
            // Класс .focus Lampa сама на перенесённый узел не перевесит — делаем это руками,
            // иначе после выхода подсветка остаётся на чужой плитке.
            try { body.find('.qdl-watch-tile.focus').removeClass('focus'); } catch (e) {}
            el.addClass('focus');
        }

        function restoreFull() {
            if (!fullId) return;
            var el = tiles[fullId];
            if (el && el.length) {
                el.removeClass('qdl-watch-tile--full');   // .focus оставляем: плитка возвращается под фокус
                var node = el[0];
                if (fullHome && fullHome.parent) {
                    if (fullHome.next && fullHome.next.parentNode === fullHome.parent)
                        fullHome.parent.insertBefore(node, fullHome.next);
                    else
                        fullHome.parent.appendChild(node);
                }
            }
            fullHome = null;
        }

        /// Раздать ресурсы развёрнутой камере. Владелец выбрал «гасить соседей только когда
        /// нужно»: пока открытая камера показывает картинку, соседи стоят на паузе и возврат
        /// в сетку мгновенный.
        /// 🔴 А вот если картинки нет — пауза декодер НЕ освобождает (её освобождает только
        /// destroy), и на слабом ТВ владельца, где из четырёх плиток поднимаются две, открытой
        /// камере взять декодер попросту неоткуда: она висит без единой ошибки в консоли.
        /// Тогда соседей сносим совсем и пересобираем плеер начисто.
        function giveFull(id, c) {
            var p = players[id];
            var stuck = !p || (p.ok ? !p.ok() && Date.now() - p.born > LIVE_GUARD.young : false);

            Object.keys(players).forEach(function (pid) {
                if (+pid === id) return;
                // 🔴 Слот, который ещё НЕ развернулся в плеер (ждёт свои 500 мс разбега), паузить
                // нечем — его надо снять совсем, иначе таймер сработает уже ПОСЛЕ входа в фулл вью
                // и заведёт декодер за оверлеем. Ловится только живьём: вход сразу после открытия
                // раздела (headless livegrid.mjs, 01.09.2026 — три соседа играли под полноэкранной).
                if (stuck || players[pid].pending) stopPlayer(pid);
                else setPaused(pid, true);
            });

            if (stuck) stopPlayer(id);
            startPlayer(c, 0, function (np) { np.urgent = true; unmute(id, true); });
            if (players[id]) players[id].urgent = true;
            setPaused(id, false);
            unmute(id, true);
            fullSince = Date.now();
            fullTries = 0;
        }

        /// Сторож развёрнутой камеры (просьба владельца: «когда открываю фулл вью, хочу чтобы
        /// была проверка, и если висит — перезапускался»). Смотрит не на наличие <video>, а на
        /// то, что картинка ДВИЖЕТСЯ: зависший поток ошибок не даёт вовсе.
        /// 🔴 В сетке такого сторожа нет НАМЕРЕННО: там на слабом ТВ две плитки из четырёх не
        /// заводятся по железу («это не страшно, телевизор слабый»), и автоперезапуск стал бы
        /// вечным циклом «создать декодер — не дали — снести» на том же железе.
        function fullGuard() {
            if (comp.destroyed || !fullId || playerOpened()) return;
            // Тумблер эфира выключили с другого устройства прямо во время просмотра — sync()
            // уже погасил плееры, и поднимать их обратно сторожу нечего: он бы воскрешал
            // выключенное видео каждые десять секунд.
            if (!liveVideoOn()) return;
            var id = fullId, c = camById(id), el = tiles[id];
            var p = players[id];
            if (p) p.urgent = true;
            if (p && p.alive && p.alive()) { fullSince = Date.now(); return; }
            if (!c || !el || !el.length) return;
            if (Date.now() - fullSince < (fullTries ? LIVE_GUARD.again : LIVE_GUARD.dead)) return;

            fullTries++;
            fullSince = Date.now();
            wake(c, true);      // вдруг лёг сам поток: старт идемпотентен и вернёт свежий path
            // Соседи ещё держат декодеры и буферы — теперь они точно нужнее здесь.
            Object.keys(players).forEach(function (pid) { if (+pid !== id) stopPlayer(pid); });
            stopPlayer(id);
            startPlayer(c, 0, function (np) {
                np.urgent = true; unmute(id, true); note(el, 'Перезапускаю…');
            });
            note(el, 'Перезапускаю…');
        }

        function startFullGuard() {
            stopFullGuard();
            fullSince = Date.now();
            fullTries = 0;
            fullTimer = setInterval(fullGuard, LIVE_GUARD.tick);
        }

        function stopFullGuard() {
            if (fullTimer) { clearInterval(fullTimer); fullTimer = null; }
            // Вернулись в сетку — плеерам снова можно жить по ленивому порогу.
            Object.keys(players).forEach(function (pid) { if (players[pid]) players[pid].urgent = false; });
        }

        function enterFull(id) {
            var el = tiles[id];
            var c = camById(id);
            if (!el || !el.length || !c) return;
            if (!c.live) { wake(c); Lampa.Noty.show('Камера сейчас не в эфире'); return; }

            fullId = id;
            takeFull(el);
            giveFull(id, c);
            startFullGuard();
            // Перенос узла в редких движках ставит медиа на паузу — поднимаем обратно.
            setTimeout(function () {
                var p = players[id];
                if (!p || !p.video || !p.video.paused) return;
                try { var again = p.video.play(); if (again && again.catch) again.catch(function () {}); } catch (e) {}
            }, 120);
            Lampa.Controller.toggle('content');
        }

        function exitFull() {
            var id = fullId;
            stopFullGuard();
            unmute(id, false);
            restoreFull();
            fullId = 0;
            var el = tiles[id];
            if (el && el.length) last = el[0];
            Object.keys(players).forEach(function (pid) { setPaused(pid, false); });
            sync();
            Lampa.Controller.toggle('content');
        }

        function switchFull(dir) {
            var list = liveCams();
            if (list.length < 2) return;
            var i = 0;
            for (var k = 0; k < list.length; k++) if (list[k].id === fullId) i = k;
            var next = list[(i + dir + list.length) % list.length];
            unmute(fullId, false);
            setPaused(fullId, true);
            restoreFull();                  // прежнюю вернули в сетку, новую уносим в body
            fullId = next.id;
            var el = tiles[next.id];
            if (el && el.length) { takeFull(el); last = el[0]; }
            // Стрелка лечится так же, как первый вход: следующей камере тоже может не хватать
            // декодера, и ждать десять секунд сторожа тут незачем.
            giveFull(next.id, next);
        }

        function refresh() {
            // плеер открыт оверлеем (activity остаётся «активной») — сетку под ним не обновляем
            if (playerOpened()) return;
            network.silent(withUid(API + '/qdl/live/watch'), function (r) {
                if (comp.destroyed || !r || !r.cameras) return;

                var alive = {};
                r.cameras.forEach(function (n) {
                    alive[String(n.id)] = 1;
                    var c = camById(n.id);
                    var el = tiles[n.id];
                    if (!c || !el || !el.length) return;   // новая камера появится при следующем входе: перестройка DOM уронила бы фокус пульта
                    c.live = n.live; c.running = n.running; c.path = n.path;
                    el.attr('data-live', n.live ? '1' : '0');   // свежий статус для iOS-пути (замыкание тайла — снимок)
                    el.find('.qdl-watch-badge').html(badgeHtml(n));
                    // Живой кадр дышит только там, где НЕ играет видео: под работающим потоком
                    // его всё равно не видно, а трафик он ест.
                    if (n.live && !players[n.id])
                        el.find('.qdl-watch-frame').attr('src', withUid(API + '/qdl/live/watch/thumb?camera=' + n.id + '&t=' + Date.now()));
                });

                // Mac-рекордер отключился → сервер перестал его отдавать. Плитку не сносим (это
                // выбило бы фокус), но гасим её плеер и честно пишем «не в эфире».
                cams.forEach(function (c) {
                    if (alive[String(c.id)]) return;
                    c.live = false; c.path = null;
                    stopPlayer(c.id);
                    var el = tiles[c.id];
                    if (el && el.length) { el.attr('data-live', '0'); el.find('.qdl-watch-badge').html(badgeHtml(c)); }
                    if (fullId === c.id) exitFull();
                });

                sync();
            }, function () {});
        }

        this.render = function () { return html; };

        this.start = function () {
            startTimer();   // вернулись на экран (в т.ч. Back с другого) — сетка снова дышит
            Lampa.Controller.add('content', {
                toggle: function () { focusBack(scroll, last); },
                left: function () {
                    if (fullId) { switchFull(-1); return; }
                    if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu');
                },
                right: function () {
                    if (fullId) { switchFull(1); return; }
                    Navigator.move('right');
                },
                up: function () {
                    if (fullId) return;
                    // Из верхнего ряда уходить некуда, кроме шапки — а Detection теперь там.
                    if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head');
                },
                down: function () {
                    if (fullId) return;
                    if (Navigator.canmove('down')) Navigator.move('down');
                },
                back: function () {
                    if (fullId) { exitFull(); return; }
                    Lampa.Activity.backward();
                }
            });
            Lampa.Controller.toggle('content');
            fitQuad();     // вернулись из настроек/другого экрана — размеры могли устареть
            sync();        // и глобальный тумблер эфира мог смениться на другом устройстве
        };

        // pause = ушли ВПЕРЁД на другой экран: глушим таймер, висящий прогрев эфира, ещё НЕ
        // заигравший скрытый iOS-<video> и — обязательно — ВСЕ живые плееры сетки.
        // Уже играющий/PiP iOS-элемент не трогаем: зритель им пользуется.
        function killPendingIosVideo() { if (iosLiveVideo && !iosLiveVideo.__qdlStarted) iosLiveStop(); }

        function leave() {
            // 🔴 Развёрнутая плитка лежит на body и переживёт уход с экрана — вернуть её
            // обязательно, иначе камера останется висеть поверх следующего экрана.
            restoreFull();
            fullId = 0;
            stopFullGuard();
            stopTimer();
            clearTimeout(syncTimer);
            clearInterval(visTimer); visTimer = null;
            liveDayCancel();
            killPendingIosVideo();
            stopAll();
        }

        this.pause = function () { leave(); };
        this.stop = function () { leave(); };
        this.destroy = function () {
            comp.destroyed = true;
            leave();
            if (io) { try { io.disconnect(); } catch (e) {} io = null; }
            if (onResize) { try { window.removeEventListener('resize', onResize); } catch (e) {} onResize = null; }
            tiles = {}; visible = {};
            network.clear(); scroll.destroy(); html.remove();
        };
    }
    // ── D1versy Live → DETECTION: лента скриншотов детектора (qdl 2.95) ──
    // Сплошная лента, как на странице /detection регистратора (решение владельца): свежие сверху,
    // старые подтягиваются прокруткой. Замер 01.09.2026 — ~170 срабатываний в час (кадр пишется
    // на КАЖДОМ тике детектора, пока человек в кадре), за сутки это тысячи карточек, поэтому
    // фильтры по дню/камере/типу здесь не украшение.
    //
    // Механика бесконечной ленты — копия ComponentRecFeed вместе с её граблями: ручной
    // Lampa.Layer.visible и activity.toggle() ТОЛЬКО на первой странице.
    //
    // 🔴 Курсор берём из ответа сервера (r.cursor), а не «минимальный показанный id»: в режиме дня
    // страница может целиком выпасть за окно локальных суток, и считать курсор было бы не по чему.

    var LIVE_MONTHS_JS = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'];
    var LIVE_WDAYS_JS = ['вс', 'пн', 'вт', 'ср', 'чт', 'пт', 'сб'];

    /// Подпись дня для списка выбора. Считаем на клиенте: /qdl/live/days закрыт правом «rec»,
    /// а Detection живёт под правом «live» — устройство с одним только эфиром получило бы ошибку.
    function liveDayName(ds, today) {
        var p = String(ds || '').split('-');
        if (p.length !== 3) return ds || '';
        var d = new Date(+p[0], +p[1] - 1, +p[2]);
        var t = String(today || '').split('-');
        if (t.length === 3) {
            var diff = Math.round((d - new Date(+t[0], +t[1] - 1, +t[2])) / 86400000);
            if (diff === 0) return 'Сегодня';
            if (diff === -1) return 'Вчера';
            if (diff === -2) return 'Позавчера';
        }
        return d.getDate() + ' ' + LIVE_MONTHS_JS[d.getMonth()] + ', ' + LIVE_WDAYS_JS[d.getDay()];
    }

    function ComponentLiveDetect(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div></div>');
        var grid = $('<div class="qdl-det-grid"></div>');
        var dayBtn = null, camBtn = null, typeBtn = null;
        var last;

        var items = [], seen = {}, camList = [];
        var cursor = 0, loading = false, hasNext = true, lastDay = '', emptyRuns = 0;
        var fCam = 0, fType = '', fDate = '';
        var today = '';
        var view = null, viewIdx = -1;
        var LIMIT = 30;
        var PREFETCH_AHEAD = 6;

        this.create = function () {
            if (!qdlAllowed('live')) { denySection(); return this.render(); }

            injectCss();
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            // Страховка для мыши/тача; на пульте раньше срабатывает prefetch по фокусу.
            scroll.onEnd = function () { comp.load(false); };
            body.append(headBar());
            body.append(grid);
            this.load(true);
            return this.render();
        };

        function mkBtn(text) {
            var el = $('<div class="selector qdl-btn-focus" style="padding:.65em 1.1em;background:rgba(255,255,255,.08);border-radius:.6em;font-size:1.3em;white-space:nowrap"></div>');
            el.text(text);
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            return el;
        }

        function camName(id) {
            for (var i = 0; i < camList.length; i++) if (camList[i].id === id) return camList[i].name;
            return 'Камера ' + id;
        }

        function labels() {
            if (dayBtn) dayBtn.text('📅 ' + (fDate ? liveDayName(fDate, today) : 'Все даты'));
            if (camBtn) camBtn.text('📷 ' + (fCam ? camName(fCam) : 'Все камеры'));
            if (typeBtn) typeBtn.text('🎯 ' + (fType === 'human' ? 'Человек' : (fType === 'motion' ? 'Движение' : 'Всё')));
        }

        function headBar() {
            var bar = $('<div style="display:flex;align-items:center;gap:.7em;padding:1.2em 1.4em .5em;flex-wrap:wrap"></div>');
            dayBtn = mkBtn('');
            camBtn = mkBtn('');
            typeBtn = mkBtn('');
            labels();

            dayBtn.on('hover:enter', pickDay);
            camBtn.on('hover:enter', pickCam);
            typeBtn.on('hover:enter', pickType);

            return bar.append(dayBtn).append(camBtn).append(typeBtn);
        }

        function pickDay() {
            var list = [{ title: 'Все даты', date: '' }];
            var base = today || fDate;
            for (var i = 0; i < 14; i++) {
                var d = liveShift(base, -i);
                list.push({ title: liveDayName(d, today), date: d });
            }
            Lampa.Select.show({
                title: 'Какой день показать?',
                items: list.map(function (x) { return { title: x.title, date: x.date, selected: x.date === fDate }; }),
                onSelect: function (a) { Lampa.Controller.toggle('content'); fDate = a.date; reload(); },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }

        function pickCam() {
            var list = [{ title: 'Все камеры', cam: 0 }];
            camList.forEach(function (c) { list.push({ title: c.name, cam: c.id }); });
            Lampa.Select.show({
                title: 'Камера',
                items: list.map(function (x) { return { title: x.title, cam: x.cam, selected: x.cam === fCam }; }),
                onSelect: function (a) { Lampa.Controller.toggle('content'); fCam = a.cam; reload(); },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }

        function pickType() {
            var list = [{ title: 'Всё', kind: '' }, { title: 'Человек', kind: 'human' }, { title: 'Движение', kind: 'motion' }];
            Lampa.Select.show({
                title: 'Что показывать',
                items: list.map(function (x) { return { title: x.title, kind: x.kind, selected: x.kind === fType }; }),
                onSelect: function (a) { Lampa.Controller.toggle('content'); fType = a.kind; reload(); },
                onBack: function () { Lampa.Controller.toggle('content'); }
            });
        }

        function reload() {
            closeView();
            grid.empty();
            items = []; seen = {}; cursor = 0; hasNext = true; lastDay = ''; emptyRuns = 0;
            last = dayBtn ? dayBtn[0] : null;
            labels();
            comp.activity.loader(true);
            comp.load(true);
        }

        this.load = function (first) {
            if (loading || (!first && !hasNext)) return;
            loading = true;

            var q = '/qdl/live/detect?limit=' + LIMIT;
            if (!first && cursor) q += '&before=' + cursor;
            if (fCam) q += '&camera=' + fCam;
            if (fType) q += '&type=' + fType;
            if (fDate) q += '&date=' + encodeURIComponent(fDate);

            network.silent(withUid(API + q),
                function (r) {
                    if (comp.destroyed) return;
                    loading = false;
                    comp.activity.loader(false);
                    r = r || {};
                    if (r.error) { if (first) comp.empty('⚠️ ' + r.error); return; }

                    if (r.today) today = r.today;
                    if (r.cameras && r.cameras.length) camList = r.cameras;
                    hasNext = !!(r.hasNext && r.cursor);
                    if (r.cursor) cursor = r.cursor;
                    labels();

                    var added = 0;
                    (r.items || []).forEach(function (it) {
                        if (seen[it.id]) return;
                        seen[it.id] = true;
                        items.push(it);
                        appendCard(it, items.length - 1);
                        added++;
                    });

                    // 🔥 Обязательно: Scroll сам зовёт scrollEnded только когда экран не заполнен,
                    // без этого первая страница осталась бы с заглушками вместо превью.
                    try { Lampa.Layer.visible(scroll.render(true)); } catch (e) {}

                    if (first && !added)
                        comp.empty(fDate || fCam || fType ? 'По этому фильтру срабатываний нет' : 'Срабатываний ещё не было');

                    // 🔥 toggle ТОЛЬКО на первой странице: на догрузке он через collectionFocus
                    // ставит фокус на первый элемент и утаскивает скролл в начало ленты.
                    if (first) comp.activity.toggle();

                    // В режиме дня страница может целиком лечь за окно локальных суток (её события
                    // принадлежат соседнему дню) — тянем дальше, но не бесконечно.
                    emptyRuns = added ? 0 : emptyRuns + 1;
                    if (!added && hasNext && emptyRuns < 5) comp.load(false);
                },
                function () {
                    if (comp.destroyed) return;
                    loading = false;
                    comp.activity.loader(false);
                    if (first) comp.empty('Видеорегистратор не отвечает');
                });
        };

        // Догружаем заранее — за несколько карточек до конца, чтобы пульт не упирался в ожидание.
        this.prefetch = function (el) {
            if (loading || !hasNext) return;
            var kids = grid.children();
            var i = kids.index(el);
            if (i >= 0 && i >= kids.length - PREFETCH_AHEAD) comp.load(false);
        };

        function appendCard(it, idx) {
            if (it.day && it.day !== lastDay) {
                lastDay = it.day;
                grid.append($('<div class="qdl-det-day">' + esc(it.dayLabel || it.day) + '</div>'));
            }

            var kind = it.type === 'human' ? 'ЧЕЛОВЕК' : 'ДВИЖЕНИЕ';
            var conf = it.confidence ? ('  ' + it.confidence + '%') : '';
            var el = $(
                '<div class="selector qdl-det-card" data-idx="' + idx + '">' +
                  '<img class="qdl-det-img">' +
                  '<div class="qdl-det-type qdl-det-type--' + esc(it.type) + '">' + kind + esc(conf) + '</div>' +
                  '<div class="qdl-det-bar">' +
                    '<div class="qdl-det-name">' + esc(it.cameraName) + '</div>' +
                    '<div class="qdl-det-time">' + esc(it.time) + '</div>' +
                  '</div>' +
                '</div>'
            );

            var img = el.find('img');
            // w=640 — плитке этого с запасом, а оригинал весит ~340 КБ (замер): в гриде их десятки.
            img.attr('src', withUid(API + '/qdl/live/detect/thumb?w=640&id=' + it.id));
            img.on('error', function () { this.src = './img/img_broken.svg'; });

            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); comp.prefetch(el); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () {
                // Просмотр уже открыт (фокус остался на этой карточке) → OK открывает запись.
                if (view) { openRecording(items[viewIdx]); return; }
                openView(idx);
            });
            el.on('hover:long', function () { openRecording(it); });

            grid.append(el);
            // ⚠️ Без регистрации в коллекции фокуса лента кончается для ПУЛЬТА на последнем ряду
            // первой страницы: карточки в DOM есть, а Navigator о них не знает — canmove('down') ложь.
            // Мышь и тач бага не видят вовсе: они фокусят элемент напрямую, мимо коллекции.
            // ⚠️ Только пока контроллер наш: ответ мог прийти, когда зритель ушёл в меню или в плеер —
            // тогда карточки уехали бы в ЧУЖУЮ коллекцию; при возврате toggle соберёт всё заново.
            try { if (!Lampa.Controller.own || Lampa.Controller.own(comp)) Lampa.Controller.collectionAppend(el); } catch (e) {}
        }

        // ── полноэкранный просмотр кадра ─────────────────────────────────────────
        function openView(idx) {
            if (idx < 0 || idx >= items.length) return;
            viewIdx = idx;
            var it = items[idx];

            if (!view) {
                view = $('<div class="qdl-det-view"><img><div class="qdl-det-head"></div><div class="qdl-det-foot"></div></div>');
                $('body').append(view);
            }

            var img = view.find('img');
            // Без w= — оригинал байт в байт: экономим на контейнере, а не на качестве.
            img.attr('src', withUid(API + '/qdl/live/detect/thumb?id=' + it.id));
            img.off('error').on('error', function () { this.src = './img/img_broken.svg'; });

            view.find('.qdl-det-head').text(
                (it.type === 'human' ? 'Человек' : 'Движение') + (it.confidence ? '  ·  ' + it.confidence + '%' : '') +
                '   ·   ' + it.cameraName + '   ·   ' + (it.dayLabel || it.day) + ', ' + it.time);
            view.find('.qdl-det-foot').text(
                (idx + 1) + ' из ' + items.length + '   ·   ◀ ▶ листать' +
                (it.recording ? '   ·   OK — открыть запись' : '') + '   ·   Назад — выйти');

            preload(idx + 1);
            if (idx >= items.length - 5) comp.load(false);
        }

        function preload(i) {
            if (i < 0 || i >= items.length) return;
            try { var im = new Image(); im.src = withUid(API + '/qdl/live/detect/thumb?id=' + items[i].id); } catch (e) {}
        }

        function moveView(dir) {
            var next = viewIdx + dir;
            if (next < 0 || next >= items.length) return;
            openView(next);
        }

        function closeView() {
            if (!view) return;
            view.remove();
            view = null;
            // Фокус возвращаем на ту карточку, до которой долистали в просмотре (§CO).
            var el = grid.find('.qdl-det-card[data-idx="' + viewIdx + '"]');
            if (el.length) last = el[0];
            viewIdx = -1;
        }

        function openRecording(it) {
            if (!it) return;
            if (!it.recording) { Lampa.Noty.show('К этому кадру запись не привязана'); return; }
            if (!qdlAllowed('rec')) { Lampa.Noty.show('Раздел записей недоступен на этом устройстве'); return; }
            closeView();
            livePlay({ id: it.camera, name: it.cameraName },
                     [{ id: it.recording, start: it.time, end: it.dayLabel || it.day, seconds: 0 }], 0);
        }

        this.empty = function (text) {
            grid.append($('<div class="qdl-det-day">' + esc(text) + '</div>'));
            comp.activity.loader(false);
            comp.activity.toggle();
        };

        this.render = function () { return html; };

        this.start = function () {
            Lampa.Controller.add('content', {
                // link обязателен: по нему Controller.own(comp) отличает «активны мы» от чужого экрана.
                link: comp,
                toggle: function () { focusBack(scroll, last); },
                left: function () {
                    if (view) { moveView(-1); return; }
                    if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu');
                },
                right: function () {
                    if (view) { moveView(1); return; }
                    Navigator.move('right');
                },
                up: function () {
                    if (view) return;
                    if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head');
                },
                down: function () {
                    if (view) return;
                    if (Navigator.canmove('down')) Navigator.move('down');
                },
                back: function () {
                    if (view) { closeView(); Lampa.Controller.toggle('content'); return; }
                    Lampa.Activity.backward();
                }
            });
            Lampa.Controller.toggle('content');
        };

        // Lampa на forward-навигации зовёт pause(), а не destroy(): висящий запрос обязан умереть
        // здесь, иначе доживший ответ дорисует ленту поверх чужого экрана. Оверлей просмотра живёт
        // на body и обязан уехать вместе с экраном.
        this.pause = function () { network.clear(); closeView(); };
        this.stop = function () { network.clear(); closeView(); };
        this.destroy = function () {
            comp.destroyed = true;
            network.clear();
            closeView();
            scroll.destroy();
            html.remove();
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
            if (!qdlAllowed('rec')) { denySection(); return this.render(); }

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
            network.silent(withUid(API + '/qdl/live/cameras' + (date ? '?date=' + encodeURIComponent(date) : '')),
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
            // День переехал из средней кнопки сюда: сама кнопка теперь открывает ленту
            // всех записей (жалоба «до прошлого месяца надо доклацывать стрелками»).
            if (r.label || currentLabel)
                body.append($('<div style="padding:.1em 1.6em .3em;font-size:1.35em;font-weight:600">' + esc(r.label || currentLabel) + '</div>'));

            if (r.error)
                body.append(liveMsg('⚠️ ' + r.error));
            else if (!r.cameras || !r.cameras.length) {
                // Сегодня пусто, а зритель день не выбирал → сами прыгаем на последний день
                // с записями. Иначе на ТВ экран выглядит мёртвым: три кнопки сверху, стрелке
                // «вниз» некуда идти — читается как «навигация не работает».
                if (!autoJumped && !userTouched && !object.qdl_date) {
                    autoJumped = true;
                    body.append(liveMsg('За сегодня записей нет — ищу последний день с записями…'));
                    network.silent(withUid(API + '/qdl/live/days?back=' + LIVE_DAYS_BACK), function (dr) {
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
            var day = $('<div class="selector qdl-btn-focus qdl-live-day" style="flex:1;text-align:center;padding:.65em 1.2em;background:rgba(255,255,255,.13);border-radius:.6em;font-size:1.5em;font-weight:600">📅 Все записи</div>');
            var next = $('<div class="selector qdl-btn-focus" style="padding:.65em 1.1em;background:rgba(255,255,255,' + (canNext ? '.08' : '.03') + ');border-radius:.6em;font-size:1.4em;opacity:' + (canNext ? '1' : '.35') + '">▶</div>');

            prev.on('hover:enter', function () { userTouched = true; date = liveShift(date || today, -1); reload(); });
            next.on('hover:enter', function () {
                if (!canNext) { Lampa.Noty.show('Это самый свежий день'); return; }
                userTouched = true;
                date = liveShift(date, 1);
                reload();
            });
            day.on('hover:enter', function () { openRecFeed(); });

            [prev, day, next].forEach(function (el) {
                el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
                el.on('hover:touch hover:hover', function () { last = markLast(el); });
            });

            return bar.append(prev).append(day).append(next);
        }

        function pickDay() {
            livePickDay(date, function (d) { userTouched = true; date = d; reload(); });
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
            img.attr('src', withUid(API + '/qdl/live/thumb?id=' + c.thumb));
            img.on('error', function () { this.src = './img/img_broken.svg'; });
            // Наводка будит ремукс суток — к нажатию Enter день обычно уже готов целиком.
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); liveWarmDay(c, date); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
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
                toggle: function () { focusBack(scroll, last); },
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
            if (!qdlAllowed('rec')) { denySection(); return this.render(); }

            injectCss();   // фокус-стили кнопок/строк записей
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            network.silent(withUid(API + '/qdl/live/recordings?camera=' + encodeURIComponent(cam.id) + (date ? '&date=' + encodeURIComponent(date) : '')),
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
            day.on('hover:touch hover:hover', function () { last = markLast(day); });
            day.on('hover:enter', function () { livePlayDay(cam, date, label); });

            // Запасной: куски по очереди (каждый со своим таймлайном) — если склейка не собралась.
            var seq = $('<div class="selector qdl-btn-focus" style="margin:.4em 1.4em;padding:.8em 1.2em;background:rgba(255,255,255,.1);border-radius:.8em;font-size:1.3em">Фрагменты подряд, по одному</div>');
            seq.on('hover:focus', function () { last = seq[0]; scroll.update(seq, true); });
            seq.on('hover:touch hover:hover', function () { last = markLast(seq); });
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
            img.attr('src', withUid(API + '/qdl/live/thumb?id=' + rec.id));
            img.on('error', function () { this.src = './img/img_broken.svg'; });
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () { livePlay(cam, items, i); });
            return el;
        }

        this.render = function () { return html; };
        this.start = function () {
            Lampa.Controller.add('content', {
                toggle: function () { focusBack(scroll, last); },
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

    // ── D1versy Rec: СКВОЗНАЯ ЛЕНТА ЗАПИСЕЙ ──
    // Жалоба владельца: «все записи отображаются только за текущий месяц, чтобы выбрать месяц
    // назад надо руками стрелочки клацать». Навигация в Rec крутилась вокруг ОДНОГО дня
    // (◀ день ▶), а список дней сервер резал окном liveDaysBack=14. Здесь — лента всех записей
    // всех камер: свежие сверху, старые подтягиваются по мере прокрутки (сервер — /qdl/live/feed,
    // один запрос на страницу). Выбор конкретного дня остался кнопкой в шапке.
    //
    // Механика бесконечной прокрутки — копия ComponentJutCatalog вместе с его граблями:
    // ручной Lampa.Layer.visible (иначе картинки останутся заглушками) и activity.toggle()
    // ТОЛЬКО на первой странице (иначе догрузка выбрасывает фокус в начало ленты).
    function ComponentRecFeed(object) {
        var comp = this;
        var network = new Lampa.Reguest();
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var body = $('<div></div>');
        var last, offset = 0, loading = false, hasNext = true, total = 0;
        var seen = {};        // дедуп по id записи: пока зритель листает, сверху появляются новые
        var lastDay = '';     // разделитель дня рисуем, когда дата сменилась
        var LIMIT = 30;
        var PREFETCH_AHEAD = 8;

        this.create = function () {
            if (!qdlAllowed('rec')) { denySection(); return this.render(); }

            injectCss();   // фокус-стили: без них фокус на ТВ невидим (§AK.3)
            this.activity.loader(true);
            scroll.minus();
            html.append(scroll.render());
            scroll.body().append(body);
            // Страховка для мыши/тача; на пульте раньше срабатывает prefetch по фокусу.
            scroll.onEnd = function () { comp.load(false); };
            body.append(headBar());
            this.load(true);
            return this.render();
        };

        function headBar() {
            var bar = $('<div style="display:flex;align-items:center;gap:.7em;padding:1.2em 1.4em .5em"></div>');
            var pick = $('<div class="selector qdl-btn-focus" style="padding:.65em 1.1em;background:rgba(255,255,255,.08);border-radius:.6em;font-size:1.4em">📅 Выбрать день</div>');
            pick.on('hover:focus', function () { last = pick[0]; scroll.update(pick, true); });
            pick.on('hover:touch hover:hover', function () { last = markLast(pick); });
            pick.on('hover:enter', function () {
                livePickDay('', function (d) {
                    Lampa.Activity.push({ url: '', title: 'D1versy Rec', component: 'qdl_live', qdl_date: d, page: 1 });
                });
            });
            return bar.append(pick);
        }

        this.load = function (first) {
            if (loading || (!first && !hasNext)) return;
            loading = true;
            network.silent(withUid(API + '/qdl/live/feed?offset=' + offset + '&limit=' + LIMIT),
                function (r) {
                    if (comp.destroyed) return;
                    loading = false;
                    comp.activity.loader(false);
                    r = r || {};
                    if (r.error) { if (first) comp.empty('⚠️ ' + r.error); return; }

                    hasNext = !!r.hasNext;
                    var got = r.items || [];
                    got.forEach(function (rec) {
                        if (seen[rec.id]) return;
                        seen[rec.id] = true;
                        appendRec(rec);
                        total++;
                    });
                    // Сдвигаем окно на то, что ОТДАЛ сервер, а не на то, что нарисовали:
                    // иначе после дедупа страницы начали бы перекрываться бесконечно.
                    offset += got.length;

                    // 🔥 Обязательно: Scroll сам зовёт scrollEnded только когда экран не заполнен,
                    // без этого первая страница осталась бы с заглушками вместо превью.
                    try { Lampa.Layer.visible(scroll.render(true)); } catch (e) {}
                    if (first && total === 0) comp.empty('Записей нет');
                    // 🔥 toggle ТОЛЬКО на первой странице: на догрузке он через collectionFocus
                    // ставит фокус на первый элемент и утаскивает скролл в начало ленты.
                    if (first) comp.activity.toggle();
                },
                function () {
                    if (comp.destroyed) return;
                    loading = false;
                    comp.activity.loader(false);
                    if (first) comp.empty('Видеорегистратор не отвечает');
                });
        };

        // Догружаем заранее — за несколько строк до конца, чтобы пульт не упирался в ожидание.
        this.prefetch = function (el) {
            if (loading || !hasNext) return;
            var kids = body.children();
            var i = kids.index(el);
            if (i >= 0 && i >= kids.length - PREFETCH_AHEAD) comp.load(false);
        };

        function appendRec(rec) {
            if (rec.day && rec.day !== lastDay) {
                lastDay = rec.day;
                body.append($('<div style="padding:.9em 1.6em .2em;font-size:1.35em;font-weight:600;opacity:.85">' + esc(rec.dayLabel || rec.day) + '</div>'));
            }

            var tl = liveTimeline(rec);
            var pct = (tl && tl.percent) || 0;
            var mark = pct >= 90 ? '✓ ' : (pct >= 5 ? '► ' + Math.round(pct) + '%   ·   ' : '');
            var meta = [rec.cameraName, liveDur(rec.seconds), liveSize(rec.size),
                        rec.trigger === 'motion' ? 'движение' : (rec.trigger === 'human' ? 'человек' : '')]
                       .filter(Boolean).join('   ·   ');

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
            img.attr('src', withUid(API + '/qdl/live/thumb?id=' + rec.id));
            img.on('error', function () { this.src = './img/img_broken.svg'; });
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); comp.prefetch(el); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () {
                livePlay({ id: rec.camera, name: rec.cameraName }, [rec], 0);
            });
            body.append(el);
            // ⚠️ Та же мина, что и в Detection: без collectionAppend пульт упирается в конец
            // ПЕРВОЙ страницы, хотя лента в DOM уже длиннее. Только пока контроллер наш.
            try { if (!Lampa.Controller.own || Lampa.Controller.own(comp)) Lampa.Controller.collectionAppend(el); } catch (e) {}
        }

        this.empty = function (text) {
            body.append(liveMsg(text));
            comp.activity.loader(false);
            comp.activity.toggle();
        };

        this.render = function () { return html; };

        this.start = function () {
            Lampa.Controller.add('content', {
                // link обязателен: по нему Controller.own(comp) отличает «активны мы» от чужого экрана.
                link: comp,
                toggle: function () { focusBack(scroll, last); },
                left: function () { if (Navigator.canmove('left')) Navigator.move('left'); else Lampa.Controller.toggle('menu'); },
                right: function () { Navigator.move('right'); },
                up: function () { if (Navigator.canmove('up')) Navigator.move('up'); else Lampa.Controller.toggle('head'); },
                down: function () { if (Navigator.canmove('down')) Navigator.move('down'); },
                back: function () { Lampa.Activity.backward(); }
            });
            Lampa.Controller.toggle('content');
        };

        // Lampa на forward-навигации зовёт pause(), а не destroy(): висящий запрос обязан
        // умереть здесь, иначе доживший ответ дорисует ленту поверх чужого экрана.
        this.pause = function () { network.clear(); };
        this.stop = function () { network.clear(); };
        this.destroy = function () { network.clear(); scroll.destroy(); html.remove(); };
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

    // Порядок наших пунктов слева, сверху вниз. Каждый цепляется за ПОСЛЕДНИЙ существующий выше,
    // а не жёстко за соседа. 🔴 Раньше Rec цеплялся за Live, а jut.su за Rec — стоило спрятать Live,
    // и исчезали все три сразу. С правами (qdl 2.54) пункты пропадают штатно, так что цепочка обязана
    // переживать любую дырку в середине.
    var MENU_ORDER = [
        { cls: 'qdl-menu',       build: function () { return buildMenuItem(); },      show: function () { return true; } },
        { cls: 'qdl-noti-menu',  build: function () { return buildNotiMenuItem(); },  show: function () { return true; },
          onAdd: function () { setTimeout(pollNotifications, 200); } },   // подтянуть бейдж сразу после появления пункта
        // 🔴 Здесь был СЛОТ-ПРОХОДНИК 'xsmart-menu': пункт строит чужой плагин (xsmart.js из
        // контейнера xsmart-proxy), и пока он стоял ВНУТРИ нашей цепочки, слот был обязателен —
        // иначе наш цикл «держим строго после якоря» вырывал бы jut.su на его место, xsmart.js
        // вставлял бы свой пункт назад, и пункты прыгали бы вечно.
        // Теперь «xSmart» переехал НАД «Лентой» (сразу под «Главную», решение владельца), то есть
        // выше точки, с которой начинается наша цепочка, — держать его нам больше не нужно и
        // нельзя: слот тянул бы пункт обратно вниз. Ровно та же война, только зеркальная.
        { cls: 'qdl-jut-menu',   build: function () { return buildJutMenuItem(); },   show: function () { return true; } },

        { cls: 'qdl-watch-menu', build: function () { return buildWatchMenuItem(); }, show: function () { return qdlAllowed('live'); } },
        { cls: 'qdl-live-menu',  build: function () { return buildLiveMenuItem(); },  show: function () { return qdlAllowed('rec'); } }
    ];

    // Якорь наших пунктов — «Лента» (data-action="feed", решение владельца: все наши разделы
    // стоят сразу под ней). Фолбэк-цепочка обязательна: пункты штатного меню Lampa прячутся
    // настройкой, а пропавший якорь означал бы, что наших пунктов нет вовсе.
    // Конец списка в фолбэки НЕ берём осознанно: ни одного из трёх пунктов нет только
    // пока меню ещё не отрисовано — тогда честнее подождать следующего тика, чем вставить
    // пункты в полуготовый список.
    function menuAnchor() {
        var a = $('.menu .menu__item[data-action="feed"]').first();
        if (a.length) return a;
        a = $('.menu .menu__item[data-action="main"]').first();
        if (a.length) return a;
        // 🔴 2.84: третьим фолбэком был «myperson», но пункт «Персоны» мы скрыли штатным
        // флагом disable_features.persons — держим «Фильмы», он остаётся при любых настройках.
        return $('.menu .menu__item[data-action="movie"]').first();
    }

    function ensureMenu() {
        try {
            var anchor = menuAnchor();
            if (!anchor.length) return;                 // меню ещё не отрисовано — ждём

            for (var i = 0; i < MENU_ORDER.length; i++) {
                var spec = MENU_ORDER[i];
                var node = dedupe('.menu .' + spec.cls);

                // право отозвали (или его и не было) — пункт снимаем, якорем он не становится
                if (!spec.show()) { if (node.length) node.remove(); continue; }

                if (!node.length) {
                    // Слот-проходник — пункт не наш: ждём, пока его вставит хозяин.
                    if (!spec.build) continue;
                    anchor.after(spec.build());
                    node = dedupe('.menu .' + spec.cls);
                    if (spec.onAdd) spec.onAdd();
                }
                // уже есть — держим строго сразу после якоря (меню могло пере-рендериться)
                else if (node.prev('.menu__item')[0] !== anchor[0]) {
                    node.detach();
                    anchor.after(node);
                }

                anchor = node;
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
        ensureJutAutopilot();
        ensureLiveDetectBtn();
        var deb = null;
        function onMut() {
            if (deb) return;
            deb = setTimeout(function () { deb = null; ensureHeaderNoti(); ensureJutAutopilot(); ensureLiveDetectBtn(); }, 300);
        }
        try {
            var headEl = document.querySelector('.head') || document.body;   // узкий observer
            new MutationObserver(onMut).observe(headEl, { childList: true, subtree: true });
        } catch (e) {}
        [500, 1500, 3000, 6000].forEach(function (t) {
            setTimeout(function () { ensureHeaderNoti(); ensureJutAutopilot(); ensureLiveDetectBtn(); }, t);
        });
    }

    // ───────── Автопилот jut.su: пропуск опенинга + автозапуск следующей серии ─────────
    // Одна кнопка на обе фичи (требование владельца: «двойная стрелочка», активна/неактивна),
    // видна ТОЛЬКО на страницах jut.su. Состояние едет между устройствами через sync_invc
    // (ключ добавлен в import_keys) — включил на телефоне, работает и на ТВ.
    var JUT_AUTOPILOT_KEY = 'qdl_jut_autopilot';
    var JUT_PAGES = { jut_catalog: 1, jut_title: 1, jut_episodes: 1, jut_search: 1 };

    // Двойной шеврон: «мотать вперёд» — то, что кнопка и делает (опенинг + следующая серия).
    var JUT_SKIP_SVG =
        '<svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">' +
        '<path d="M4 5l7 7-7 7" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/>' +
        '<path d="M12 5l7 7-7 7" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/>' +
        '</svg>';

    function jutAutopilot() {
        try { return Lampa.Storage.get(JUT_AUTOPILOT_KEY, false) === true; } catch (e) { return false; }
    }

    function jutAutopilotPaint() {
        try {
            var on = jutAutopilot();
            dedupe('.qdl-jut-skip')
                .toggleClass('qdl-jut-skip--on', on)
                .attr('title', on ? 'Автопилот включён: пропуск опенинга и следующая серия'
                                  : 'Автопилот выключен');
        } catch (e) {}
    }

    // Кнопка живёт в DOM постоянно, а прячется/показывается по активности: пересоздавать её
    // на каждом переходе — это гонка с MutationObserver хедера и лишние дубли.
    function jutAutopilotVisibility() {
        try {
            var act = Lampa.Activity.active() || {};
            dedupe('.qdl-jut-skip').css('display', JUT_PAGES[act.component] ? '' : 'none');
        } catch (e) {}
    }

    // ── Detection в шапке Lampa ──────────────────────────────────────────────────
    // ⚠️ Класс кнопки — `qdl-det-btn`, а НЕ `qdl-det-head`: последний уже занят шапкой
    // полноэкранного просмотра детекций (`position:absolute;left:0;right:0`). Совпадение имён
    // клало кнопку в левый верхний угол под «назад» и схлопывало её иконку в нулевую ширину —
    // в DOM элемент есть, на экране пусто. Ловится только замером живого клиента.
    // Владелец: «detection в хедере». Иконка живёт там же, где кнопка автопилота jut.su —
    // сразу за названием раздела, слева от значков. Видна ТОЛЬКО на экранах эфира и самой
    // ленты детекций и только при праве «эфир»: в остальных местах она бессмысленна.
    // Побочно это закрывает и прежнюю просьбу «вверх с плиток попадает на Detection»:
    // «вверх» из сетки штатно уводит в шапку (Controller.toggle('head')), а там она и есть.
    var LIVE_HEAD_PAGES = { qdl_live_watch: 1, qdl_live_detect: 1 };

    function liveDetectVisibility() {
        try {
            var act = Lampa.Activity.active() || {};
            var show = !!LIVE_HEAD_PAGES[act.component] && qdlAllowed('live');
            dedupe('.qdl-det-btn').css('display', show ? '' : 'none');
        } catch (e) {}
    }

    function ensureLiveDetectBtn() {
        try {
            if (dedupe('.qdl-det-btn').length) { liveDetectVisibility(); return; }
            // 🔴 Кладём в ряд значков (.head__actions), а не за названием раздела: вставленная
            // после .head__title кнопка ложится ПОД «назад» в левом верхнем углу — замер
            // живого клиента дал ей box [29,0,55,55], то есть ровно место стрелки, и на экране
            // её не видно вовсе. Ряд значков — проверенное место, там же живёт колокольчик.
            // ⚠️ .first(): вставка в НАБОР клонирует узел (та же грабля, что у колокольчика).
            var actions = $('.head .head__actions').first();
            if (!actions.length) return;
            injectCss();

            var btn = $('<div class="head__action selector qdl-det-btn">' + DETECT_ICON + '</div>');
            btn.data('controller', 'head');   // поздняя иконка в хедере иначе не фокусируется пультом
            btn.on('hover:enter', function () {
                Lampa.Activity.push({ url: '', title: 'Detection', component: 'qdl_live_detect', page: 1 });
            });

            actions.prepend(btn);   // первым в ряду: раздел свой, а не системный
            liveDetectVisibility();
        } catch (e) {}
    }

    function ensureJutAutopilot() {
        try {
            if (dedupe('.qdl-jut-skip').length) { jutAutopilotVisibility(); return; }
            // Владелец просил «слева возле названия jut.su»: в шаблоне хедера .head__title
            // стоит вплотную перед .head__actions, поэтому вставка сразу за ним даёт кнопку
            // слева от зоны значков. ⚠️ .first(): вставка в НАБОР клонирует узел.
            var title = $('.head .head__title').first();
            if (!title.length) return;
            injectCss();

            var btn = $('<div class="head__action selector qdl-jut-skip">' + JUT_SKIP_SVG + '</div>');
            btn.data('controller', 'head');   // поздняя иконка в хедере иначе не фокусируется пультом
            btn.on('hover:enter', function () {
                var on = !jutAutopilot();
                try { Lampa.Storage.set(JUT_AUTOPILOT_KEY, on); } catch (e) {}
                jutAutopilotPaint();
                Lampa.Noty.show(on ? 'Автопилот jut.su включён: пропускаю опенинг и запускаю следующую серию'
                                   : 'Автопилот jut.su выключен');
            });

            title.after(btn);
            jutAutopilotPaint();
            jutAutopilotVisibility();
        } catch (e) {}
    }

    function initJutAutopilot() {
        // Возврат из плеера идёт через Activity.backward(), а он события 'start' НЕ шлёт —
        // поэтому слушаем ещё и закрытие плеера (тот же приём, что в initContinueRefresh).
        try {
            Lampa.Listener.follow('activity', function (e) {
                if (e && e.type === 'start') {
                    ensureJutAutopilot(); jutAutopilotVisibility();
                    ensureLiveDetectBtn(); liveDetectVisibility();
                }
            });
        } catch (e) {}
        try { Lampa.Player.listener.follow('destroy', jutAutopilotVisibility); } catch (e) {}
        // Прилетело чужое состояние с другого устройства — перекрасить кнопку.
        try {
            Lampa.Storage.listener.follow('change', function (e) {
                if (e && e.name === JUT_AUTOPILOT_KEY) jutAutopilotPaint();
            });
        } catch (e) {}
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

    // Плейлист текущего просмотра — нужен префетчу сегментов, чтобы у следующей серии
    // разметка опенинга уже лежала на элементе к моменту переключения.
    var jutActivePlaylist = null;

    function jutTokOf(list, e) {
        for (var i = 0; i < list.length; i++) if (list[i].key === e.key) return list[i].tok || '';
        return e.tok || '';
    }

    /// Сегменты соседних серий сервер знает только после резолва их страниц, поэтому веб
    /// подтягивает их заранее: к моменту 'select' элемент уже несёт segments, а бандл сам
    /// применит их в play$1 → Segments.set. Нативные плееры делают это сами по segmentsUrl.
    function initJutSegmentsPrefetch() {
        try {
            Lampa.Player.listener.follow('start', function (e) {
                var data = e && e.data;
                if (!data || !data.qdl_jut_tok || !jutAutopilot() || !jutActivePlaylist) return;

                var idx = -1;
                for (var i = 0; i < jutActivePlaylist.length; i++)
                    if (jutActivePlaylist[i].qdl_jut_tok === data.qdl_jut_tok) { idx = i; break; }

                // Текущая серия без разметки — значит её сегменты не успели приехать к старту
                // (переключились раньше префетча). Досылаем прямо в плеер.
                var cur = idx >= 0 ? jutActivePlaylist[idx] : null;
                if (cur && !cur.segments && cur.segmentsUrl) {
                    req(cur.segmentsUrl, function (r) {
                        if (!r || !r.ok || !r.segments) return;
                        cur.segments = r.segments;
                        try { if (Lampa.Segments) Lampa.Segments.set(r.segments); } catch (e2) {}
                    }, function () {});
                }

                var next = idx >= 0 ? jutActivePlaylist[idx + 1] : null;
                if (!next || next.segments || !next.segmentsUrl) return;

                req(next.segmentsUrl, function (r) {
                    if (r && r.ok && r.segments) next.segments = r.segments;
                }, function () {});
            });
        } catch (e) {}
        // Плеер закрыли — плейлист больше не наш (иначе префетч цеплялся бы к чужому просмотру).
        try { Lampa.Player.listener.follow('destroy', function () { jutActivePlaylist = null; }); } catch (e) {}
    }

    // Плеер запускается ТОЛЬКО после resolve: прямую ссылку на CDN отдавать нельзя —
    // hash в ней вяжется с UA, а у плеера он свой → 403.
    function jutPlay(slug, e, titleName, siblings) {
        Lampa.Noty.show('Готовлю ' + jutEpTitle(e, ''));
        req(API + '/qdl/jut/resolve?slug=' + encodeURIComponent(slug) +
            '&season=' + e.season + '&ep=' + e.ep + '&kind=' + encodeURIComponent(e.kind === 'gameova' ? 'game-ova' : e.kind),
            function (r) {
                if (!r || !r.ok) { Lampa.Noty.show(jutErrText(r)); return; }
                // История просмотров — после успешного резолва: серия, которая не поднялась,
                // в историю попадать не должна. Карточка своя (TMDB id у jut.su нет), вход
                // из истории вернёт на jut_title через initHistoryRouting.
                noteHistory(jutHistoryCard(slug, titleName));
                var on = jutAutopilot();
                // 🔴 uid дописываем В САМУ строку URL: поток открывает НАТИВНЫЙ плеер
                // (VLC/ExoPlayer), заголовок или cookie туда не подложить, а история
                // просмотров пишется именно по факту байтов через /qdl/jut/stream.
                var item = { title: jutEpTitle(e, titleName), url: withUid(API + r.url) };
                var tl = jutTl(slug, e.key); if (tl) item.timeline = tl;
                // Пропуск опенинга: секунды приходят со страницы серии (те же, что читает
                // кнопка «Пропустить заставку» на сайте). Формат — штатный для Lampa:
                // модуль Segments сам скипнет в режиме auto. duration_ms НЕ кладём — с ним
                // бандл «подгоняет» метки, а наши секунды точны для этого файла.
                if (on && r.segments) item.segments = r.segments;

                try {
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
                    var plist = list.map(function (x) {
                        var pi = { title: jutEpTitle(x, titleName) };
                        // 🔴 Текущей серии берём ГОТОВЫЙ item.url, а не строим заново: Android
                        // ищет текущий элемент плейлиста сравнением строк url, и расхождение
                        // хоть на символ молча заиграло бы первую серию сезона вместо выбранной.
                        pi.url = (x.key === e.key) ? item.url
                                                   : withUid(API + '/qdl/jut/stream?t=' + encodeURIComponent(x.tok));
                        var t2 = jutTl(slug, x.key); if (t2) pi.timeline = t2;
                        if (x.tok) {
                            pi.qdl_jut_tok = x.tok;
                            // Сегменты соседних серий известны только серверу — их подтягивает
                            // префетч (веб) или сам нативный плеер по этому URL.
                            if (on) pi.segmentsUrl = API + '/qdl/jut/segments?t=' + encodeURIComponent(x.tok);
                        }
                        if (x.key === e.key && item.segments) pi.segments = item.segments;
                        return pi;
                    });

                    item.qdl_jut_tok = jutTokOf(list, e);
                    // 🔥 Плейлист обязан лежать НА ОБЪЕКТЕ до play: нативная ветка (мак/винда/
                    // андроид — наши оболочки всегда идут по ней) сериализует data синхронно
                    // внутри Player.play, а Player.playlist() отрабатывает уже после — до
                    // нативов список так и не доезжал, и автоперехода у них не было вовсе.
                    // С 2.62 кладём всегда: автопереход управляется штатным playlist_next
                    // (как в «Загрузках»), автопилот («2 стрелочки») — только про опенинги.
                    item.playlist = plist;

                    jutActivePlaylist = on ? plist : null;
                    Lampa.Player.play(item);
                    Lampa.Player.playlist(plist);   // веб-плеер: ручное переключение серий остаётся
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


        // ⚠️ width:100% — правило .cols--N > * даёт долю ширины ЛЮБОМУ прямому потомку,
        // иначе сообщение сожмётся до ширины одной карточки
        this.empty = function (txt) {
            body.append($('<div style="width:100%;padding:2em;font-size:1.4em;opacity:.7">' + esc(txt) + '</div>'));
            comp.activity.toggle();
        };

        // Плитка ведёт на свой экран поиска (поле + топ-50 недавнего), а не открывает
        // клавиатуру поверх каталога: раньше из клавиатуры некуда было вернуться, и история
        // выдачи нигде не жила.
        this.appendSearchTile = function () {
            var el = Lampa.Template.get('card', { title: 'Поиск', release_year: 'по названию' });
            el.addClass('qdl-jut-search');
            var img = el.find('.card__img');
            img.attr('src', './img/img_broken.svg');
            var view = el.find('.card__view'); if (!view.length) view = el;
            view.append('<div style="position:absolute;left:0;top:0;right:0;bottom:0;display:flex;align-items:center;justify-content:center;font-size:3em">🔍</div>');
            el.on('hover:focus', function () { last = el[0]; scroll.update(el, true); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () {
                Lampa.Activity.push({ url: '', title: 'jut.su — поиск', component: 'jut_search', page: 1 });
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
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            el.on('hover:enter', function () {
                Lampa.Activity.push({ url: '', title: c.title || c.slug, component: 'jut_title', jut_slug: c.slug, jut_card: c });
            });
            // Фон под фокусом — как на родных экранах Lampa (см. bgFocus выше). hover:hover нужен
            // ОТДЕЛЬНО от hover:focus: десктопным клиентам lampainit-invc.js форсит
            // navigation_type='mouse', а в этом режиме mouseenter шлёт ТОЛЬКО hover:hover —
            // без второй ветки на Windows/Mac фон не менялся бы вовсе. hover:touch намеренно НЕ
            // вешаем: на таче родная Lampa фон тоже не красит, а touchstart во время пальцевого
            // скролла красил бы случайные карточки.
            el.on('hover:focus hover:hover', function () { bgFocus(psrc); });
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
                toggle: function () { focusBack(scroll, last); },
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
                b.on('hover:touch hover:hover', function () { last = markLast(b); });
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
                toggle: function () { focusBack(scroll, last); },
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
                    el.on('hover:touch hover:hover', function () { last = markLast(el); });
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
                toggle: function () { focusBack(scroll, last); },
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

    // ───────── Экран поиска jut.su: поле сверху + топ-50 недавнего ─────────
    // Раньше плитка «Поиск» открывала клавиатуру сразу и на весь экран, а вернуться было
    // некуда. Теперь это отдельный экран: поле ввода (не впритык к верхнему краю) и под ним
    // лента последнего — сперва то, что реально смотрели, потом добор из поисковых выдач.
    // Память ведёт сервер (/qdl/jut/recent), клиент только читает. С 2.52 она РАЗДЕЛЬНАЯ
    // по устройствам: uid уезжает в запросах (withUid), у каждого клиента своя выдача.
    //
    // ⚠️ Клавиатура выбирается по устройству, а не по платформе «на глаз»: на ТВ нужна
    // экранная клавиатура Lampa (пультом), на телефоне и десктопе — системная.
    function jutUseNativeInput() {
        try { if (Lampa.Storage.field('keyboard_type') === 'integrate') return true; } catch (e) {}
        try { if (isMobile() && Lampa.Platform.screen('mobile')) return true; } catch (e) {}
        var p = window.d1vision_platform;
        return p === 'windows' || p === 'mac';
    }

    function ComponentJutSearch(object) {
        var comp = this;
        var scroll = new Lampa.Scroll({ mask: true, over: true, step: 250 });
        var html = $('<div></div>');
        var wrap = $('<div class="qdl-jut-search-wrap"></div>');
        var field = $('<div class="selector qdl-jut-search-field"><span>🔍</span></div>');
        var input = null;
        var recentTitle = $('<div class="qdl-jut-recent-title">Недавнее</div>');
        var body = $('<div class="category-full mapping--grid cols--6"></div>');
        var last;

        function submit(q) {
            q = (q || '').trim();
            if (!q) return;
            Lampa.Activity.push({ url: '', title: 'jut.su — ' + q, component: 'jut_catalog', jut_query: q, page: 1 });
        }

        function openKeyboard() {
            Lampa.Input.edit({ value: input ? input.val() : '', title: 'Поиск на jut.su', free: true }, function (q) {
                // 🔴 Закрытие клавиатуры уводит фокус в 'settings_component' (штатный back
                // у Input.edit) — без возврата экран остаётся живым, но глухим к пульту.
                try { Lampa.Controller.toggle('content'); } catch (e) {}
                submit(q);
            });
        }

        this.create = function () {
            injectCss();
            this.activity.loader(true);
            scroll.minus();                       // ⚠️ без этого на ТВ у .scroll нет высоты
            html.append(scroll.render());

            if (jutUseNativeInput()) {
                input = $('<input type="text" placeholder="Название аниме" />');
                field.append(input);
                // Клавиши уходят движку Lampa (он слушает keydown без preventDefault) —
                // без остановки всплытия набор текста дёргал бы навигацию и скролл.
                input.on('keydown', function (ev) {
                    ev.stopPropagation();
                    if (ev.keyCode === 13) { ev.preventDefault(); submit(input.val()); }
                });
                field.on('hover:enter', function () { try { input[0].focus(); } catch (e) {} });
                field.on('click', function () { try { input[0].focus(); } catch (e) {} });
            } else {
                field.append('<span class="qdl-jut-search-hint">Название аниме</span>');
                field.on('hover:enter', openKeyboard);
            }
            field.on('hover:focus', function () { last = field[0]; scroll.update(field, true); });
            field.on('hover:touch hover:hover', function () { last = markLast(field); });

            wrap.append(field);
            scroll.body().append(wrap);
            scroll.body().append(recentTitle);
            scroll.body().append(body);

            this.load();
            return this.render();
        };

        this.load = function () {
            req(API + '/qdl/jut/recent?limit=50', function (r) {
                comp.activity.loader(false);
                var items = (r && r.ok && r.items) || [];
                if (!items.length) {
                    recentTitle.remove();
                    body.append($('<div style="width:100%;padding:2em;font-size:1.3em;opacity:.7">Пока пусто — найди что-нибудь, и оно появится здесь</div>'));
                } else items.forEach(comp.append);
                // Постеры ленивые: без этого вызова первая пачка осталась бы с заглушками
                // (Scroll шлёт visible только при реальной прокрутке).
                try { Lampa.Layer.visible(scroll.render(true)); } catch (e) {}
                comp.activity.toggle();
            }, function () {
                comp.activity.loader(false);
                recentTitle.remove();
                body.append($('<div style="width:100%;padding:2em;font-size:1.3em;opacity:.7">Не удалось получить недавнее</div>'));
                comp.activity.toggle();
            });
        };

        this.append = function (c) {
            var el = Lampa.Template.get('card', {
                title: c.title || c.slug,
                release_year: (c.years && c.years.length ? c.years[c.years.length - 1] : '') + ''
            });
            var img = el.find('.card__img');
            img.on('error', function () { this.src = './img/img_broken.svg'; });

            var psrc = jutPosterUrl(c.slug, c.pv);
            var loadPoster = function () { if (img.attr('src') !== psrc) img.attr('src', psrc); };
            el.on('visible', loadPoster);   // ⚠️ scroll.onScroll здесь задавать нельзя — убьёт lazy

            var view = el.find('.card__view'); if (!view.length) view = el;
            if (c.ongoing)
                view.append('<div style="position:absolute;left:.4em;top:.4em;background:rgba(40,160,80,.92);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.85em;z-index:5">онгоинг</div>');
            if (c.episodes)
                view.append('<div style="position:absolute;right:.4em;top:.4em;background:rgba(0,0,0,.65);color:#fff;padding:.15em .5em;border-radius:.4em;font-size:.85em;z-index:5">' + c.episodes + '</div>');

            el.on('hover:focus', function () { loadPoster(); last = el[0]; scroll.update(el, true); });
            el.on('hover:touch hover:hover', function () { last = markLast(el); });
            // Фон под фокусом (bgFocus): hover:focus — пульт, hover:hover — мышь десктопа.
            el.on('hover:focus hover:hover', function () { bgFocus(psrc); });
            el.on('hover:enter', function () {
                Lampa.Activity.push({ url: '', title: c.title || c.slug, component: 'jut_title', jut_slug: c.slug, jut_card: c });
            });
            el.on('hover:long', function () { jutDownloadMenu(c.slug, null, 0); });

            body.append(el);
            try { if (!Lampa.Controller.own || Lampa.Controller.own(comp)) Lampa.Controller.collectionAppend(el); } catch (e) {}
        };

        this.render = function () { return html; };
        this.start = function () {
            Lampa.Controller.add('content', {
                link: comp,
                // Стартовый фокус — на поле: экран открывают, чтобы искать.
                toggle: function () { focusBack(scroll, last); },
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

    // ── Экран «Хелс-чеки» в настройках (qdl 2.39). Виден только по праву «действия»:
    // без него компонент вообще не регистрируется (плитку раздела иначе не скрыть).
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
        // Гард от двойной регистрации (паттерн Transcoding/backup). ⚠️ Он же делает регистрацию
        // НЕОБРАТИМОЙ: при отзыве права на живом клиенте замок вернётся, а уже добавленный раздел
        // из настроек не исчезнет до перезапуска. Мирится с этим сознательно — вход в сами настройки
        // к тому моменту уже скрыт, а Lampa.SettingsApi снятия компонента не умеет.
        if (window.qdl_health_settings || !qdlManage()) return;
        if (!window.Lampa || !Lampa.SettingsApi || !Lampa.SettingsApi.addComponent) return;
        window.qdl_health_settings = true;
        Lampa.SettingsApi.addComponent({ component: 'qdl_health', icon: HEALTH_ICON, name: 'Хелс-чеки' });
        // ⚠️ строго ПОСЛЕ addComponent: он сам ставит пустой шаблон settings_qdl_health и перетёр бы наш
        Lampa.Template.add('settings_qdl_health', '<div><div class="qdl-health-list"></div></div>');
        Lampa.Settings.listener.follow('open', function (e) {
            if (e && e.name === 'qdl_health') renderHealth(e.body);
        });
    }

    // ── Раздел «D1Vision» в настройках (qdl 2.89) ────────────────────────────────────────────
    // Настройки здесь ОБЩИЕ на весь сервер: меняешь на одном устройстве — применяется всем.
    // Поэтому раздел и гейтится правом «действия»: сервер отдаёт 403 на запись всем остальным
    // (ManageDenied), а прятать раздел — только чтобы не показывать заведомо отказную кнопку.
    //
    // Разметку рисуем руками, как в «Хелс-чеках», а не через Lampa.SettingsApi.addParam:
    // ⚠️ типа 'toggle' у addParam НЕ существует (валидны select/trigger/input/title/static/button),
    // поле с ним молча не отрисовывается — грабля уже описана в Modules/Music/plugin.js.
    var D1V_ICON = '<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M4 6h16M4 12h10M4 18h6" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';

    function d1vRow(action, name, value, descr) {
        return '<div class="settings-param selector" data-action="' + action + '">'
            + '<div class="settings-param__name">' + esc(name) + '</div>'
            + '<div class="settings-param__value">' + esc(value) + '</div>'
            + (descr ? '<div class="settings-param__descr">' + esc(descr) + '</div>' : '')
            + '</div>';
    }

    function renderD1Vision(body) {
        var list = body.find('.qdl-d1v-list');
        // Значение эфира показываем сразу из кеша прав, а ниже уточняем у сервера: раздел
        // открывают редко, но настройка общая — на другом устройстве её могли уже поменять.
        var video = liveVideoGlobal();
        var lastFilter = null;

        var paint = function (f) {
            lastFilter = f;
            list.html(
                '<div class="settings-param-title"><span>Эфир</span></div>'
                + d1vRow('liveVideo', 'Видео в плитках камер', video ? 'Включено' : 'Выключено',
                    'Живая картинка прямо в сетке раздела «D1versy Live». Настройка общая для всех устройств. '
                    + 'На iPhone плитки всё равно остаются кадрами — там автоплей запрещён самой системой, '
                    + 'а тап по плитке открывает нативный плеер.')
                + '<div class="settings-param-title"><span>Каталог</span></div>'
                + d1vRow('enabled', 'Фильтр по году выпуска', f.enabled ? 'Включён' : 'Выключен',
                    'Убирает из рядов «Сейчас смотрят», «Новинки» и «В хорошем качестве» всё старее указанных годов. Топы, жанровые подборки и коллекции не трогает.')
                + d1vRow('movieYear', 'Фильмы не старше', String(f.movieYear), 'Год выпуска. Всё, что вышло раньше, в рядах не показывается.')
                + d1vRow('tvYear', 'Сериалы не старше', String(f.tvYear), 'Отдельный порог: у сериалов считается год первой серии.')
                + '<div class="settings-param" data-static="true"><div class="settings-param__descr">'
                + 'Настройка общая для всех устройств. Ряды обновятся не сразу — кеш каталога живёт до 3 часов.'
                + '</div></div>'
            );

            var save = function (next) {
                post(API + '/qdl/catalog-filter', next, function (r) {
                    if (r && r.success) { paint(r.filter || next); try { Lampa.Noty.show('Сохранено для всех устройств'); } catch (e) {} }
                    // Отказ показываем ПРИЧИНОЙ: отозванное право иначе читается как поломка.
                    else { try { Lampa.Noty.show((r && r.error) || 'Не удалось сохранить'); } catch (e) {} }
                }, function () { try { Lampa.Noty.show('Сервер недоступен'); } catch (e) {} });
            };

            var year = function (key, title) {
                if (!(Lampa.Input && Lampa.Input.edit)) { try { Lampa.Noty.show('Ввод недоступен'); } catch (e) {} return; }
                Lampa.Input.edit({ title: title, value: String(f[key]), free: true, nosave: true }, function (v) {
                    try { Lampa.Controller.toggle('content'); } catch (e) {}   // клавиатура уводит фокус (та же грабля, что в поиске jut)
                    var n = parseInt(String(v || '').replace(/\D+/g, ''), 10);
                    if (!n || n < 1900) { try { Lampa.Noty.show('Нужен год, например 2020'); } catch (e) {} return; }
                    var next = { enabled: f.enabled, movieYear: f.movieYear, tvYear: f.tvYear };
                    next[key] = n;
                    save(next);
                });
            };

            try {
                list.find('[data-action="liveVideo"]').on('hover:enter click', function () {
                    var next = !video;
                    // 🔴 post() сам дописывает uid в query: гейт «действий» читает его ТОЛЬКО
                    // оттуда, и без этого сервер отказал бы даже устройству с грантом (2.67).
                    post(API + '/qdl/live/video', { on: next }, function (r) {
                        if (r && r.success) {
                            video = next;
                            liveVideoSet(next);          // зеркало в кеш прав — экран эфира подхватит сам
                            paint(lastFilter);
                            try { Lampa.Noty.show('Сохранено для всех устройств'); } catch (e) {}
                        }
                        // Отказ показываем ПРИЧИНОЙ: отозванное право иначе читается как поломка.
                        else { try { Lampa.Noty.show((r && r.error) || 'Не удалось сохранить'); } catch (e) {} }
                    }, function () { try { Lampa.Noty.show('Сервер недоступен'); } catch (e) {} });
                });
                list.find('[data-action="enabled"]').on('hover:enter click', function () {
                    save({ enabled: !f.enabled, movieYear: f.movieYear, tvYear: f.tvYear });
                });
                list.find('[data-action="movieYear"]').on('hover:enter click', function () { year('movieYear', 'Фильмы не старше'); });
                list.find('[data-action="tvYear"]').on('hover:enter click', function () { year('tvYear', 'Сериалы не старше'); });
                Lampa.Params.listener.send('update_scroll');   // после динамической вставки (образец parental_control)
            } catch (e) {}
        };

        list.html('<div class="settings-param"><div class="settings-param__name">Читаю настройки…</div></div>');
        req(API + '/qdl/live/video', function (v) {
            if (!v || typeof v.video !== 'boolean' || v.video === video) return;
            video = v.video;
            liveVideoSet(video);
            if (lastFilter) paint(lastFilter);   // список уже нарисован — обновляем строку на месте
        }, function () {});
        req(API + '/qdl/catalog-filter', function (r) {
            if (r && typeof r.movieYear === 'number') paint(r);
            else list.html('<div class="settings-param"><div class="settings-param__name">❌ Сервер вернул не то</div></div>');
        }, function () {
            list.html('<div class="settings-param"><div class="settings-param__name">❌ /qdl/catalog-filter недоступен</div></div>');
        });
    }

    function registerD1VisionSettings() {
        // Гард от двойной регистрации — и он же делает её необратимой (Lampa.SettingsApi снятия
        // компонента не умеет). Отзыв права дострахован CSS-замком в applySettingsLock: ровно
        // та же схема, что у «Хелс-чеков», и поймана она была тем же permsgate.
        if (window.qdl_d1v_settings || !qdlManage()) return;
        if (!window.Lampa || !Lampa.SettingsApi || !Lampa.SettingsApi.addComponent) return;
        window.qdl_d1v_settings = true;
        Lampa.SettingsApi.addComponent({ component: 'qdl_d1vision', icon: D1V_ICON, name: 'D1Vision' });
        // ⚠️ строго ПОСЛЕ addComponent: он сам ставит пустой шаблон и перетёр бы наш
        Lampa.Template.add('settings_qdl_d1vision', '<div><div class="qdl-d1v-list"></div></div>');
        Lampa.Settings.listener.follow('open', function (e) {
            if (e && e.name === 'qdl_d1vision') renderD1Vision(e.body);
        });
    }

    function start() {
        Lampa.Component.add('qdl_downloads', ComponentDownloads);
        Lampa.Component.add('qdl_episodes', ComponentEpisodes);
        Lampa.Component.add('qdl_card', ComponentCard);
        Lampa.Component.add('qdl_notifications', ComponentNotifications);
        Lampa.Component.add('qdl_live', ComponentLive);
        Lampa.Component.add('qdl_live_camera', ComponentLiveCamera);
        Lampa.Component.add('qdl_rec_feed', ComponentRecFeed);
        Lampa.Component.add('qdl_live_watch', ComponentLiveWatch);
        Lampa.Component.add('qdl_live_detect', ComponentLiveDetect);
        Lampa.Component.add('jut_catalog', ComponentJutCatalog);
        Lampa.Component.add('jut_title', ComponentJutTitle);
        Lampa.Component.add('jut_episodes', ComponentJutEpisodes);
        Lampa.Component.add('jut_search', ComponentJutSearch);
        Lampa.Listener.follow('full', addButton);
        Lampa.Listener.follow('qdl_card', onCardMenu);   // долгое нажатие на карточке каталога → наше меню (qdl 2.108)
        // Всё, что зависит от прав, перестраивается ОДНОЙ функцией — и на старте, и на каждом
        // перечитывании раз в минуту. 🔴 registerHealthSettings обязан быть именно здесь, а не
        // только в start(): там он отрабатывает ДО приезда прав, и устройство с грантом не увидело
        // бы раздел «Хелс-чеки» до перезапуска приложения (свой гард делает повтор безопасным).
        var onFeatures = function () {
            try { ensureMenu(); } catch (e) {}
            try { applySettingsLock(); } catch (e) {}
            try { registerHealthSettings(); } catch (e) {}
            try { registerD1VisionSettings(); } catch (e) {}
        };
        // Права тянем ДО первой отрисовки меню, но не ждём ответа: стартовый проход рисует по кешу
        // (мгновенно и без мигания), а пришедший ответ перестраивает меню — в том числе снимает
        // пункт, если право отозвали.
        try { applySettingsLock(); } catch (e) {}   // по кешу прав — до ответа сервера
        loadFeatures(onFeatures);
        startMenuWatcher();
        startHeaderNotiWatcher();
        startPlayerFsWatcher();
        try { registerHealthSettings(); } catch (e) {}     // «Хелс-чеки» — по праву «действия» (повтор в onFeatures)
        try { registerD1VisionSettings(); } catch (e) {}   // раздел «D1Vision» — там же (qdl 2.89)
        pollNotifications();
        try { initSelectFix(); } catch (e) {}            // фикс скролла селектбоксов (upstream mheight-баг)
        try { initTimelineMirror(); } catch (e) {}       // аварийный фолбэк зеркала (режим 'auto', см. qdl 2.18)
        try { initSwr(); } catch (e) {}                  // свежесть рядов каталога поверх клиентского кеша (qdl 2.63)
        try { initGridDedup(); } catch (e) {}            // дедуп карточек и насос экрана «Ещё» (qdl 2.94)
        try { initTimecodeBridge(); } catch (e) {}       // перерисовка экрана серий по pull серверных таймкодов
        try { initContinueRefresh(); } catch (e) {}      // «Продолжить» на полной карточке — свежая после возврата
        try { initHistoryRouting(); } catch (e) {}       // вход в jut-карточку из «Истории просмотров»
        try { initJutAutopilot(); } catch (e) {}         // тумблер автопилота jut.su в хедере (видимость + синк)
        try { initJutSegmentsPrefetch(); } catch (e) {}  // сегменты следующей серии — заранее, к моменту переключения
        try { whenDmca(function () {}); } catch (e) {}   // прогрев DMCA-списка до первого открытия карточки
        try { setInterval(pollNotifications, 90000); } catch (e) {}   // фолбэк: основной путь — пуш по 'lwsEvent' (qdl 2.19)
        // Права перечитываем на живом клиенте: владелец выдаёт их в админке, пока аппка уже открыта.
        // Заодно это пульс устройства — по нему свежая строка всплывает в списке /admin/d1v.
        try { setInterval(function () { loadFeatures(onFeatures); }, 60000); } catch (e) {}
        // Страховка поллера прогресса: он гасит таймер насухо, когда качать нечего, и просыпается
        // по входу на экран / пушу / выходу из плеера. Этот тик — последний пояс на случай,
        // если все три сигнала пропали, а подписчик на экране остался.
        try { setInterval(function () { if (!_pgTimer && pgHasSubs()) pgKick(); }, 60000); } catch (e) {}
    }

    if (window.appready) start();
    else Lampa.Listener.follow('app', function (e) { if (e.type === 'ready') start(); });
})();
