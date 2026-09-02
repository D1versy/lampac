using System;
using System.Text;
using Shared.Models.Events;

namespace QbitDownload;

// Патчи upstream-бандла Lampa (app.min.js) ПРИ ОТДАЧЕ — через EventListener.AppReplace
// (дёргается в LampaWeb/ApiController.LampaApp с arg "appjs"), чтобы не добавлять
// конфликтных точек в upstream-файлы. Что вырезаем и почему:
//   notice-icon — штатный колокольчик уведомлений в шапке (у нас свой, qdl-noti-head);
//   notice-cub  — NoticeCub: 5-мин опрос cub-уведомлений + TMDB-обогащение (не нужны);
//                 NoticeLampa/pushNotice НЕ трогаем — classes.lampa читается бандлом без гарда;
//   menu-items  — пункты левого меню «Релизы»/«Расписание»/«Подписки»;
//   tt-extract  — фоновый парсер TimeTable: TMDB-запрос каждые 30 сек вечным циклом;
//   tt-rows     — ряды «Скоро выйдут»/«Недавно вышли» на главной (питались от TimeTable);
//   dev-wait    — 1000 мс ожидания «тройного вверх» перед стартом очереди загрузки
//                 (оставлено по праву «действия», qdl 2.89);
//   splash-*    — 1000 мс + fadeOut 500 мс логотипа УЖЕ ПОСЛЕ готовности интерфейса;
//   shots       — сторонний JS с cub.red каждый старт (services=false его не ловил);
//   plugin-reset— reset=Math.random() у URL плагинов: кеш-бастер, бивший по внешним клиентам;
//   swr         — stale-while-revalidate у клиентского кеша Request: попадание в ЖИВОЙ кеш
//                 отдаётся как раньше, но параллельно тихо дотягивается свежий ответ — он
//                 переписывает запись кеша и уходит событием 'request_revalidate'. Что и когда
//                 перестраивать, решает qdl.js (чтобы политика менялась по воздуху);
//   broadcast   — мёртвая «Трансляция»: иконка в шапке карточки (CUB-WebSocket выключен);
//   broadcast-share — она же вторым входом: пункт «Поделиться» в панели плеера;
//   adult-*     — предупреждение «Взрослый контент» 18+ на карточке и всё, что оно за собой
//                 тянуло (ряды карточки, трейлеры, плашка ADULT) — решение владельца;
//   preroll     — рекламный преролл перед плеером: Preroll.show() тянет Google IMA SDK
//                 (https://imasdk.googleapis.com/js/sdkloader/ima3.js) и крутит VAST из data.vast_url.
//                 Штатного выключателя нет: disable_features.ads в текущем бандле не читается
//                 (0 вхождений) — только патч. Заменяем ветку на безусловный запуск плеера.
//   grid-dedup-* — экран «Ещё» (полноэкранная сетка) рисовал одну и ту же карточку по два раза:
//                  дедуп по id при догрузке страницы + насос, догружающий короткую страницу.
//                  Оба патча тупые, вся политика — в qdl.js (qdl 2.94).
// Якоря — неминглящиеся литералы бандла (пин tree в LampaWeb/ModInit). Смена tree →
// якорь тихо не найдётся (warn в лог), остатки прячет CSS-фолбэк в lampainit-invc.js.
public static class AppPatch
{
    sealed record P(string label, string anchor, string replacement);

    static readonly P[] patches = new[]
    {
        new P("notice-icon",
            "this.icon = Head.addIcon(Template.string('icon_bell'), this.open.bind(this));",
            "this.icon = $('<div class=\"notice--icon hide\"></div>');/*qdl-cut:notice-icon*/"),
        new P("notice-cub",
            "this.classes.cub = new NoticeCub();",
            "/*qdl-cut:notice-cub*/"),
        new P("menu-items",
            "if (!window.lampa_settings.torrents_use && item.action == 'mytorrents') return false;",
            "if (!window.lampa_settings.torrents_use && item.action == 'mytorrents') return false;\n      if (item.action == 'relise' || item.action == 'timetable' || item.action == 'subscribes' || item.action == 'mytorrents') return false;/*qdl-cut:menu*/"),
        new P("tt-extract",
            "Timer.add(time_extract, extract);",
            "/*qdl-cut:tt-extract*/"),
        // якорь встречается ровно 2 раза — оба в ContentRows timetable_lately/recently, режем оба
        new P("tt-rows",
            "if (screen == 'category' && params.url == 'movie') return;",
            "return;/*qdl-cut:tt-rows*/"),
        // Ранний выход из Preroll.show — ровно та же ветка, которой бандл сам пропускает рекламу
        // (`if (type.any) return call();` чуть ниже по коду), поэтому плеер стартует как обычно,
        // а IMA SDK не грузится вовсе. Точка выбрана по ОТДАВАЕМОМУ бандлу: прежний однострочный
        // якорь `if (data.vast_url) Preroll.show(...)` в нём не встречается — там `Preroll.show`
        // зовётся из четырёх мест с колбэком-функцией.
        new P("preroll",
            "function show$5(data, call) {",
            "function show$5(data, call) {return call();/*qdl-cut:preroll*/"),
        // Гейт «этим плейлистом автопереход ведём не мы»: штатный выключатель `playlist_next`
        // глобальный, и выключив его, мы отняли бы автопереход у «Загрузок» и у всего остального.
        // `work` — текущий элемент плеера. Ручное переключение (кнопки панели) остаётся всегда.
        //
        // Кто ставит флаг сегодня: раздел «Музыка» (Modules/Music/plugin.js, d1v:music-owns-next)
        // — там переходом обязаны владеть мы одни, иначе PlayerPlaylist.next() ядра стартует свой
        // резолв url-функции, наш switchToken делает его устаревшим, resolvePlaybackUrl выходит без
        // call(), и ядро навсегда залипает в wait_for_loading_url = true (умирает и автопереход, и
        // кнопка ► до перезапуска плеера).
        // jut.su флаг больше НЕ ставит — это зафиксировано Tests/js/qdl-jut-autopilot.test.js.
        new P("jut-autonext",
            "if (Storage.field('playlist_next') && !$('body').hasClass('selectbox--open')) PlayerPlaylist.next();",
            "if (!(work && work.qdl_no_autonext) && Storage.field('playlist_next') && !$('body').hasClass('selectbox--open')) PlayerPlaylist.next();/*qdl-cut:jut-autonext*/"),
        // Segments наружу не экспортирован, а нам нужно доставить разметку опенинга серии,
        // которая приехала уже ПОСЛЕ старта плеера (сегменты соседних серий сервер знает
        // только после резолва их страниц). Без экспорта такая серия просто останется без
        // скипа — поэтому патч не критичный, но полезный.
        new P("segments-export",
            "PlayerPlaylist: PlayerPlaylist,",
            "PlayerPlaylist: PlayerPlaylist,\n      Segments: Segments,/*qdl-cut:segments-export*/"),
        // ── qdl 2.53: стартовые паузы бандла ───────────────────────────────────────────
        // Обе — чистое ожидание, а НЕ сеть. Замер живого сервера: index.html 2.3 мс,
        // app.min.js 5.5 мс, /lampainit.js 4–8 мс, /qdl/notifications 7 мс. «Загрузка»,
        // которую видит пользователь, — вот эти 2.5 с, и они платятся каждым стартом
        // каждого клиента (веб, mac, iOS, Windows, Android — страница у всех одна).
        //
        // dev-wait: developerApp() жёстко ждёт 1000 мс, не нажмут ли трижды «вверх» (вход в
        // меню разработчика), и только потом зовёт loadLang → loadTask, то есть пауза стоит
        // ДО всей очереди загрузки. Режем не насухо, а гейтим правом «действия»: у обычных
        // клиентов 0 мс, у устройств с грантом прежние 1000 мс и рабочий тройной «вверх».
        //
        // 🔴 qdl 2.89: было гейтом по куке qdl_unlock=1, кука убрана целиком. Право читаем из
        // КЕША, который кладёт qdl.js: Lampa.Storage.set('qdl_features', …) пишет прямо в
        // localStorage['qdl_features'] как JSON — то есть значение доступно СИНХРОННО, ещё до
        // загрузки самого qdl.js (а сюда мы попадаем именно на старте). Кеша нет (первый в
        // жизни запуск устройства) — идём быстрым путём: это и есть безопасный дефолт.
        // Подделка localStorage правом не является: ею открывается только локальная пауза,
        // а все действия сервер всё равно проверяет через ManageDenied().
        new P("dev-wait",
            "function developerApp(proceed) {",
            "function developerApp(proceed) {try{if(!JSON.parse(localStorage.getItem('qdl_features')||'{}').manage) return proceed();}catch(e){return proceed();}/*qdl-cut:dev-wait*/"),
        // splash-wait / splash-fade: showApp() зовёт startApp() СРАЗУ, а логотип .welcome
        // снимает через setTimeout 1000 мс + fadeOut 500 мс — полторы секунды заставки уже
        // ПОСЛЕ того, как интерфейс готов и работает. 150 мс / fade 200 мс — выбор владельца
        // (нолём не делаем: на слабом ТВ первый кадр застал бы недорисованный экран).
        new P("splash-wait",
            "}, 1000); // Старт приложения",
            "}, 150);/*qdl-cut:splash-wait*/ // Старт приложения"),
        new P("splash-fade",
            "$('.welcome').fadeOut(500, function () {",
            "$('.welcome').fadeOut(200, function () {/*qdl-cut:splash-fade*/"),
        // shots: сторонний JS с cub.red (177 КБ сырых / 45 КБ br) грузится КАЖДЫЙ старт и
        // исполняется в НАШЕМ origin, где в cookie лежит ключ периметра D1Vision. Мы считали,
        // что его гасит services=false — неправда, и в lampainit-invc.js это было написано
        // ошибочно: у sport и tsarea гейт действительно по services, а у shots СВОЙ, только
        // по iptv и hostname !== 'localhost'. Подчиняем его тому же флагу, а не вырезаем:
        // вернуть = services:true, и поведение совпадёт с тем, что обещает комментарий.
        // ⚠️ Якорь БЕЗ хоста: {localhost} подставляется в ApiController.LampaApp ДО того,
        // как дёргается EventListener.AppReplace.
        new P("shots",
            "if (window.location.hostname !== 'localhost' && !window.lampa_settings.iptv) include.push(",
            "if (window.lampa_settings.services && window.location.hostname !== 'localhost' && !window.lampa_settings.iptv) include.push(/*qdl-cut:shots*/"),
        // plugin-reset: addPluginParams дописывает к URL КАЖДОГО плагина reset=<random>. Весь
        // блок гейтится «в адресе нет IPv4», поэтому дома (192.168.87.24) не срабатывает, а на
        // tv.d1versy.com срабатывает всегда — внешние клиенты качают все шесть плагинов
        // (~42 КБ br) мимо любого кеша на каждом старте, и заставку держит именно это:
        // showApp() зовётся из Plugins.load(showApp), то есть ждёт ВСЕ плагины.
        // 🔴 Снимать ТОЛЬКО в паре с revalidate-ветвью Staticache (no-cache + ETag): reset=
        // был единственным механизмом доставки новой версии плагина внешнему клиенту.
        // Якорь с Utils$1 — как и preroll (show$5) выше: tree бандла пинится в LampaWeb/ModInit,
        // при его смене якорь тихо не найдётся и уедет warn в лог.
        new P("plugin-reset",
            "encode = Utils$1.addUrlComponent(encode, 'reset=' + Math.random());",
            "/*qdl-cut:plugin-reset*/"),
        // ── qdl 2.63: stale-while-revalidate у клиентского кеша Request ────────────────────
        // Ряды каталога лежат в IndexedDB со сроком 2–7 СУТОК (life: day*2 … day*7). Пока запись
        // «живая», cacheGet отдаёт снимок и сети НЕ БЫВАЕТ ВООБЩЕ: появившийся на сервере фильм и
        // новый порядок популярности клиент не увидит до протухания. Кеш при этом нужен — он и
        // даёт мгновенную отрисовку. Требование владельца: рисовать быстро, но решать — серверу.
        //
        // 🔴 Патч НИЧЕГО не решает сам, вся политика — в qdl.js (меняется по воздуху). Снимок
        // отдаётся ровно как раньше (secuses(cached, true) → fromcache=true → повторного cacheSet
        // нет), ветка промаха и аварийный cache_old не тронуты. Добавлено ровно две вещи:
        //   1) метка cached.qdl_req — по ней qdl.js связывает готовый ряд с URL. Иначе связать
        //      нечем: в line.data.url лежит МЕТОД ('?sort=now_playing'), полный адрес туда не
        //      попадает. В кеш метка не утекает: тело из кеша обратно не пишется.
        //   2) тихий догон свежего ответа: переписывает запись кеша (cacheSet РОВНО ОДИН РАЗ) и
        //      уходит событием 'request_revalidate'.
        // 🔴 complite/secuses для свежего ответа звать НЕЛЬЗЯ: Progress в Api.partNext досчитал бы
        // задачу второй раз, partLoaded ушёл бы повторно, а Main.build() не идемпотентен —
        // ряды бы задвоились.
        //
        // Выключатели, оба читаются в рантайме (второй патч бандла не нужен):
        //   • window.lampa_settings.qdl_swr = false — серверный, из lampainit-invc.js (образец: shots);
        //   • window.qdl_swr(params) — предикат из qdl.js, там же троттлинг (образец: кука dev-wait).
        // Якорь уникален (1 вхождение и в вендоренном, и в отдаваемом бандле). Смена tree → warn
        // в лог, поведение возвращается к прежнему (мягкая деградация).
        new P("swr",
            "secuses(cached, true);",
            "try { if (cached && typeof cached === 'object') cached.qdl_req = params.url; } catch (e) {} " +
            "secuses(cached, true); " +
            "try { if ((!window.lampa_settings || window.lampa_settings.qdl_swr !== false) && " +
            "typeof window.qdl_swr === 'function' && window.qdl_swr(params)) { " +
            "var qdl_swr_req = $.extend({}, data); " +
            "qdl_swr_req.success = function (fresh) { if (!fresh) return; cacheSet(params, fresh); " +
            "Lampa.Listener.send('request_revalidate', { params: params, url: params.url, data: fresh, cached: cached }); }; " +
            "qdl_swr_req.error = function () {}; $.ajax(qdl_swr_req); } } catch (e) {}/*qdl-cut:swr*/"),
        // list-cache: экран «Ещё» (category_full) кешировал свои страницы у КЛИЕНТА на двое суток,
        // и догон SWR туда не достаёт — он подписан на событие 'line', а его шлёт только компонент
        // РЯДА (items-line); полноэкранная сетка его не шлёт вовсе. Итог, пойманный владельцем на
        // 2.89: на главной ряд уже отфильтрован, а внутри «Ещё» до двух суток висит старый снимок
        // с карточками старше порога, и инкогнито не помогает — там свой профиль, а кеш общий.
        //
        // Режем срок до 15 минут (life у cub-источника — в МИНУТАХ: `var day = 60 * 24`).
        // 🔴 Наружу это ничего не добавляет: наш Staticache держит тот же ответ 3 часа, клиент
        // попадает в него локально за миллисекунды. Свежесть диктует сервер — тот же принцип,
        // что принят для рядов в 2.63.
        // Якорь уникален (одно вхождение): у соседних источников свои имена переменных.
        new P("list-cache",
            "      oncomplite(Utils$1.addSource(data, source$1));\n    }, onerror, false, {\n      cache: {\n        life: day * 2\n      }",
            "      oncomplite(Utils$1.addSource(data, source$1));\n    }, onerror, false, {\n      cache: {\n        life: 15/*qdl-cut:list-cache*/\n      }"),
        // ── qdl 2.84: мёртвая «Трансляция» (broadcast) ────────────────────────────────
        // Иконка в шапке карточки и пункт «Поделиться» в панели плеера ведут в одну модалку,
        // которая шлёт Socket.send('devices') в WebSocket cub.rip. У нас он закрыт с qdl 2.19
        // (socket_use=false + socket_methods=false), а склеивать устройства сокету нечем:
        // единственный ключ в сообщении — data.account, то есть токен CUB, которого у нас нет.
        // Пользователь получал вечное «сканирование» с пустым списком. Чинить «по-своему»
        // (через наш /nws) осознанно не стали — решение владельца убрать.
        // remove() безопасен: элемент уже создан, дальнейшие show()/hide() отработают на
        // detached-узле. Фолбэк при смене tree — CSS в lampainit-invc.js.
        new P("broadcast",
            "broadcast.addClass('open--broadcast');",
            "broadcast.addClass('open--broadcast');broadcast.remove();/*qdl-cut:broadcast*/"),
        // Второй вход в ту же модалку: плеер → «Настройки» → «Поделиться». Режем элемент
        // массива целиком (вместе с закрывающим `}, {`), ветка `a.method == 'share'` в
        // обработчике остаётся недостижимой — трогать её незачем.
        new P("broadcast-share",
            "      title: Lang.translate('player_share_title'),\n      subtitle: Lang.translate('player_share_descr'),\n      method: 'share'\n    }, {\n",
            "/*qdl-cut:broadcast-share*/"),
        // ── qdl 2.87: предупреждение «Взрослый контент» (18+) ──────────────────────────
        // Владелец: «когда включаешь ужасы, у Лампы есть уведомление 18+ — в нём нет смысла,
        // кто захочет, просто нажимает „Смотреть“, и всё равно работает». Так и есть: блок
        // Warning(type:'full-adult') в описании карточки + модалка «Мне 18 лет или больше»,
        // которая ничего не проверяет, а лишь ставит Storage-ключ adult_content_view.
        //
        // 🔴 Ужасы ловились не случайно. adult_block взводится по КЛЮЧЕВЫМ СЛОВАМ TMDB из
        // списка Keys.adult (porn, sex, xxx, erotic, … nude, nudity, naked …), причём сравнение
        // подстрокой (indexOf) — у ужасов на TMDB сплошь стоят 'nudity' / 'female nudity' /
        // 'sexual violence'. И тот же флаг ВЫРЕЗАЕТ с карточки серии, съёмочную группу, актёров,
        // обсуждение, коллекцию, рекомендации и похожие, а card.adult сверх того прячет трейлеры
        // и вешает плашку ADULT на постер. То есть на «пойманном» ужастике карточка молча теряла
        // половину контента — это уходит вместе с предупреждением.
        //
        // Штатного выключателя нет: adult_content_view — скрытый ключ Storage без пункта в
        // настройках, его ставит только кнопка модалки. Через плагин (Storage.set) не делаем:
        // ключ локален устройству и не гасит «родной» флаг adult от самого TMDB.
        //
        // adult-block: снимаем гард у уже существующей в бандле ветки сброса — источник по
        // ключевым словам мёртв, ряды карточки возвращаются всегда.
        new P("adult-block",
            "if (Storage.field('adult_content_view')) adult_block = false;",
            "adult_block = false;/*qdl-cut:adult-block*/"),
        // adult-flag: второй источник — поле adult, пришедшее от самого TMDB. Гасим и его, тогда
        // недостижимы разом Warning (`if (this.card.adult)`), плашка ADULT и скрытие трейлеров;
        // отдельного якоря на месте отрисовки не заводим — лишняя точка отказа при смене tree.
        new P("adult-flag",
            "if (adult_block) data.movie.adult = true;",
            "data.movie.adult = false;/*qdl-cut:adult-flag*/"),
        // ── qdl 2.94: дубли карточек на экране «Ещё» (category_full) ───────────────────
        // Жалоба владельца: в «Сейчас смотрят» → «Ещё» каждый фильм двумя строчками по 6 карточек.
        // Серверную половину закрыл RowFilter (страница 1:1, без добора соседних). Здесь — вторая
        // половина, которую серверу не закрыть в принципе: страницы кешируются НЕЗАВИСИМО
        // (Staticache, 3 ч), а ?sort=now_playing — живой поток, поэтому свежая p1 и остывшая p2
        // законно содержат одну карточку. В бандле дедупа нет вовсе: Arrays.unique применяется
        // ровно один раз и к номерам страниц, а не к карточкам.
        //
        // Первый патч — регистрация первой страницы и взвод насоса. Второй — фильтрация догрузки.
        // 🔴 Второй ПЕРЕПРИСВАИВАЕТ локальную new_data, а НЕ мутирует new_data.results: на попадании
        // в клиентский кеш secuses(cached, true) отдаёт ТОТ ЖЕ объект, что лежит в памяти Request
        // (патч swr выше дописывает в него метку). Укороченный results поселился бы в кеше — и
        // карточки исчезли бы навсегда, а требование владельца прямо обратное: не терять.
        // Переменная new_data используется только в этих двух строках бандла.
        //
        // 🔴 Насос обязателен и обязан быть ОТЛОЖЕННЫМ. Scroll.isEnd() на незаполненном гриде
        // отдаёт true, но onEnd зовётся только из scrollEnded(), а тот на таком гриде достижим
        // ровно один раз — из hover:focus первой карточки через startScroll. И ровно в этот момент
        // Next.onLoadNext себя запрещает гардом builded_time < Date.now()-1000. Единственный шанс
        // догрузиться приходится на секунду, которую бандл сам себе закрыл. Задержку держит qdl.js.
        //
        // Выключатели рантайма (второй патч бандла не нужен): window.lampa_settings.qdl_grid_dedup
        // = false и Lampa.Storage 'qdl_grid_dedup_off'. Оба якоря уникальны (по 1 вхождению).
        new P("grid-dedup-build",
            "this.loaded.push(data.results);",
            "try { if (typeof window.qdl_grid_build === 'function') window.qdl_grid_build(this, data.results); } catch (e) {} " +
            "this.loaded.push(data.results);/*qdl-cut:grid-dedup-build*/"),
        new P("grid-dedup-next",
            "var split_total = Math.ceil(new_data.results.length / _this.limit_view);",
            "try { if (typeof window.qdl_grid_next === 'function') " +
            "new_data = { results: window.qdl_grid_next(_this, new_data.results) || new_data.results }; } catch (e) {} " +
            "var split_total = Math.ceil(new_data.results.length / _this.limit_view);/*qdl-cut:grid-dedup-next*/"),
    };

    public static void Attach() => EventListener.AppReplace += OnAppReplace;
    public static void Detach() => EventListener.AppReplace -= OnAppReplace;

    static StringBuilder OnAppReplace(string name, EventAppReplace e)
    {
        if (name != "appjs")
            return e.bulder;   // appcss/online/sisi не трогаем

        return new StringBuilder(PatchAppJs(e.bulder.ToString()));
    }

    // чистая функция — покрыта Tests/QbitDownload.Tests/AppPatchTests
    public static string PatchAppJs(string js)
    {
        foreach (var p in patches)
        {
            if (js.Contains(p.replacement))
                continue;   // уже пропатчено (Staticache может прогнать повторно)

            if (!js.Contains(p.anchor))
            {
                Console.WriteLine($"[QbitDownload] appjs patch '{p.label}': anchor NOT found (tree changed?) — skip");
                continue;
            }

            js = js.Replace(p.anchor, p.replacement);
        }

        return js;
    }
}
