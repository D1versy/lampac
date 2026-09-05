using Xunit;

namespace QbitDownload.Tests;

/// <summary>
/// Тесты серверного патчера бандла Lampa (<c>AppPatch.PatchAppJs</c>, см. AppReplace.cs):
/// вырезание upstream-колокольчика (иконка Notice + NoticeCub), пунктов меню
/// Релизы/Расписание/Подписки, фонового TimeTable-парсера и рядов на главной.
/// Живой бандл в репо не хранится — тесты идут по синтетическому js со всеми якорями;
/// живая отдача проверяется после деплоя (grep qdl-cut по app.min.js).
/// </summary>
public class AppPatchTests
{
    // все якори + окружение, как в бандле (пин tree в LampaWeb/ModInit)
    //
    // 🔴 Переводы строк ОБЯЗАТЕЛЬНО LF: боевой app.min.js — чистый LF (54 230 строк,
    // ни одного CR), а этот файл лежит в репозитории с CRLF, и verbatim-строка
    // тащит их внутрь. Пока все
    // якоря были однострочными, расхождение не проявлялось; многострочный якорь broadcast-share
    // (2.84) на CRLF-фикстуре не нашёлся бы, а в бою работал — то есть тест врал бы в обе стороны.
    const string RawAnchors = @"
      PlayerPlaylist: PlayerPlaylist,
      Timeline: Timeline,
      PlayerVideo.listener.follow('ended', function (e) {
        if (Storage.field('playlist_next') && !$('body').hasClass('selectbox--open')) PlayerPlaylist.next();
      });
      key: 'init',
      value: function init() {
        var _this = this;
        this.icon = Head.addIcon(Template.string('icon_bell'), this.open.bind(this));
        this.icon.addClass('notice--icon');
        this.classes.all = new NoticeAll();
        this.classes.lampa = new NoticeLampa();
        this.classes.cub = new NoticeCub();
      }
      if (!window.lampa_settings.torrents_use && item.action == 'mytorrents') return false;
      if (window.lampa_settings.disable_features.persons && item.action == 'myperson') return false;
      Timer.add(time_favorites, favorites);
      Timer.add(time_extract, extract);
      if (screen == 'category' && params.url == 'movie') return;
      other_lately_code();
      if (screen == 'category' && params.url == 'movie') return;
      other_recently_code();
      function developerApp(proceed) {
        var expect = true;
        var pressed = 0;
        var timer = setTimeout(function () {
          expect = false;
          proceed();
        }, 1000);
      }
      function showApp() {
        setTimeout(function () {
          if (window.show_app) return;
          $('.welcome').fadeOut(500, function () {
            $(_this).remove();
          });
        }, 1000); // Старт приложения
        startApp();
      }
      if (!window.lampa_settings.iptv && window.lampa_settings.services) {
        include.push(protocol() + cub_domain + '/plugin/sport');
      }
      if (window.location.hostname !== 'localhost' && !window.lampa_settings.iptv) include.push(protocol() + cub_domain + '/plugin/shots');
      function addPluginParams(url) {
        var encode = url;
        encode = Utils$1.addUrlComponent(encode, 'logged=' + logged);
        encode = Utils$1.addUrlComponent(encode, 'reset=' + Math.random());
        encode = Utils$1.addUrlComponent(encode, 'origin=' + origin);
        return encode;
      }
      cacheGet(params, function (cached, old) {
        // Запомнить что есть старый кеш на случай ошибки что бы отдать его
        cache_old = old;

        if (cached) {
          secuses(cached, true);
        } else {
          $.ajax(data);
        }
      });
      need.timeout = 1000 * 30;
      function show$5(data, call) {
        if (type.any) return call();
        Preroll.load(data.vast_url, call);
      }
      function init$K() {
        var timer, activity;
        var broadcast = Head.addIcon(Template.string('icon_broadcast'), function () {
          open$6({ type: 'card', object: Activity.extractObject(activity) });
        });
        broadcast.addClass('open--broadcast');
        broadcast.hide();
      }
      var items = [{
      title: Lang.translate('player_video_speed'),
      subtitle: speed == 'default' ? Lang.translate('player_speed_default_title') : speed,
      method: 'speed'
    }, {
      title: Lang.translate('player_share_title'),
      subtitle: Lang.translate('player_share_descr'),
      method: 'share'
    }, {
      title: Lang.translate('player_segments_title'),
      subtitle: Lang.translate('player_segments_descr'),
      method: 'segments'
    }];
          var adult_block = key_tags && key_tags.find && key_tags.length ? key_tags.find(function (key) {
            return Keys.adult.find(function (word) {
              return key.name.toLowerCase().indexOf(word) >= 0;
            });
          }) : false;
          if (Storage.field('adult_content_view')) adult_block = false;
          if (adult_block) data.movie.adult = true;
          if (!adult_block && data.persons && data.persons.cast && data.persons.cast.length) _this.rows.push(['persons', {}]);
        if (this.card.adult) {
          var warning = new Warning({ type: 'full-adult' });
        }
      oncomplite(Utils$1.addSource(data, source$1));
    }, onerror, false, {
      cache: {
        life: day * 2
      }
      onBuild: function onBuild(data) {
        if (!data.results.length) return this.empty();
        this.total_pages = data.total_pages || 1;
        this.loaded.push(data.results);
        this.emit('pushLoaded');
      },
        this.emit('next', function (new_data) {
          _this.next_wait = false;
          var split_total = Math.ceil(new_data.results.length / _this.limit_view);
          for (var i = 0; i < split_total; i++) {
            _this.loaded.push(new_data.results.slice(i * _this.limit_view, (i + 1) * _this.limit_view));
          }
        });
      this.html.on('hover:long', function () {
        var enabled = Controller.enabled().name;
        var menu = [];

        _this.menu_list.forEach(function (item, i) {
          i !== 0 && menu.push({
            title: item.title,
            separator: true
          });
          menu = menu.concat(item.menu());
        });
      });
      this.html.on('hover:focus', function () {
        if (window.app_time_end < Date.now() - 20000) RemoteHelper.show({
          name: 'card_menu'
        });
      });
    this.onMenu = function (target, data) {
      var _this3 = this;

      var enabled = Controller.enabled().name;
      var status = Favorite.check(data);
      var menu_plugins = [];
      var menu_favorite = [{
        title: Lang.translate('title_book'),
    ";

    [Fact]
    public void PatchAppJs_AllAnchors_AllMarkersApplied()
    {
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("/*qdl-cut:notice-icon*/", result);
        Assert.Contains("/*qdl-cut:notice-cub*/", result);
        Assert.Contains("/*qdl-cut:menu*/", result);
        Assert.Contains("/*qdl-cut:tt-extract*/", result);
        Assert.Contains("/*qdl-cut:tt-rows*/", result);
        Assert.Contains("/*qdl-cut:jut-autonext*/", result);
        Assert.Contains("/*qdl-cut:segments-export*/", result);
        Assert.Contains("/*qdl-cut:dev-wait*/", result);
        Assert.Contains("/*qdl-cut:splash-wait*/", result);
        Assert.Contains("/*qdl-cut:splash-fade*/", result);
        Assert.Contains("/*qdl-cut:shots*/", result);
        Assert.Contains("/*qdl-cut:plugin-reset*/", result);
        Assert.Contains("/*qdl-cut:swr*/", result);
        // 🔴 preroll до 2.84 не был покрыт вовсе: якоря не было в фикстуре, и смена tree
        // сломала бы вырез рекламы молча.
        Assert.Contains("/*qdl-cut:preroll*/", result);
        Assert.Contains("/*qdl-cut:broadcast*/", result);
        Assert.Contains("/*qdl-cut:broadcast-share*/", result);
        Assert.Contains("/*qdl-cut:adult-block*/", result);
        Assert.Contains("/*qdl-cut:adult-flag*/", result);
        Assert.Contains("/*qdl-cut:grid-dedup-build*/", result);
        Assert.Contains("/*qdl-cut:grid-dedup-next*/", result);
        Assert.Contains("/*qdl-cut:card-menu*/", result);
        Assert.Contains("/*qdl-cut:card-menu-legacy*/", result);
        Assert.Contains("/*qdl-cut:remote-helper*/", result);
    }

    [Fact]
    public void PatchAppJs_CardMenu_EventBeforeMenuBuild_BothPaths()
    {
        // qdl 2.108: долгое нажатие на карточке → событие 'qdl_card' ДО сборки штатного меню
        // закладок; handled=true — штатное не строится. Два входа: миксин Menu и устаревший
        // Lampa.Card.onMenu (витрины CUB, внешние плагины, «Клубничка»).
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Equal(2, Count(result, "Lampa.Listener.send('qdl_card', qdl_ev)"));
        Assert.Equal(2, Count(result, "if (qdl_ev.handled) return;"));

        // событие стоит ДО сборки пунктов — иначе handled ничего не отменяет
        int evMenu = result.IndexOf("/*qdl-cut:card-menu*/");
        int build = result.IndexOf("_this.menu_list.forEach(function (item, i) {");
        Assert.True(evMenu >= 0 && build > evMenu, "card-menu: событие должно стоять до menu_list.forEach");
        int evLegacy = result.IndexOf("/*qdl-cut:card-menu-legacy*/");
        int plugins = result.IndexOf("var menu_plugins = [];");
        Assert.True(evLegacy >= 0 && plugins > evLegacy, "card-menu-legacy: событие должно стоять до menu_plugins");

        // штатная сборка цела — это фолбэк для карточек, которые qdl.js не берёт
        Assert.Contains("menu = menu.concat(item.menu());", result);
        Assert.Contains("var menu_favorite = [{", result);
        // `enabled` из бандла едет в событие: на главной/в поиске это 'items_line', а не 'content'
        Assert.Contains("data: _this.data, enabled: enabled, handled: false", result);
        Assert.Contains("data: data, params: params, enabled: enabled, handled: false", result);
        // падение подписчика не роняет карточку
        Assert.Equal(2, Count(result, "try { Lampa.Listener.send('qdl_card', qdl_ev); } catch (e) {}"));
    }

    [Fact]
    public void PatchAppJs_RemoteHelper_HintNeverShown()
    {
        // гайд «Удерживайте кнопку (ОК) для вызова меню» — единственный вызов RemoteHelper в
        // бандле, тумблера нет; гасим условие, тело вызова оставляем (синтаксис цел)
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.DoesNotContain("window.app_time_end < Date.now() - 20000", result);
        Assert.Contains("if (false)/*qdl-cut:remote-helper*/ RemoteHelper.show({", result);
        Assert.Contains("name: 'card_menu'", result);
    }

    [Fact]
    public void PatchAppJs_CardMenu_Idempotent()
    {
        // replacement обоих card-menu патчей содержит собственный якорь — гард Contains(replacement)
        // обязан отработать на повторном прогоне Staticache
        string once = AppPatch.PatchAppJs(AllAnchors);
        string twice = AppPatch.PatchAppJs(once);

        Assert.Equal(once, twice);
        Assert.Equal(1, Count(twice, "/*qdl-cut:card-menu*/"));
        Assert.Equal(1, Count(twice, "/*qdl-cut:card-menu-legacy*/"));
        Assert.Equal(1, Count(twice, "/*qdl-cut:remote-helper*/"));
        Assert.Equal(1, Count(twice, "_this.menu_list.forEach(function (item, i) {"));
        Assert.Equal(1, Count(twice, "var menu_plugins = [];"));
    }

    [Fact]
    public void PatchAppJs_Adult_BothSourcesDead_AndCardRowsRestored()
    {
        // 18+ на карточке взводился ДВУМЯ путями: ключевые слова TMDB (adult_block) и «родное»
        // поле adult от самого TMDB. Гасим оба — иначе предупреждение осталось бы у второй группы.
        string result = AppPatch.PatchAppJs(AllAnchors);

        // 1. источник по ключевым словам мёртв: гард Storage снят, сброс безусловный
        Assert.DoesNotContain("if (Storage.field('adult_content_view')) adult_block = false;", result);
        Assert.Contains("adult_block = false;/*qdl-cut:adult-block*/", result);

        // 2. флаг карточки не взводится ни от ключевых слов, ни от TMDB
        Assert.DoesNotContain("data.movie.adult = true", result);
        Assert.Contains("data.movie.adult = false;/*qdl-cut:adult-flag*/", result);

        // 3. 🔴 главное побочное: ряды карточки (актёры/серии/похожие) гейтились тем же
        //    adult_block — сама ветка остаётся на месте, но её условие теперь всегда истинно
        Assert.Contains("if (!adult_block && data.persons", result);

        // 4. точку отрисовки Warning намеренно НЕ патчим — она просто недостижима
        Assert.Contains("if (this.card.adult) {", result);
    }

    [Fact]
    public void PatchAppJs_Adult_Idempotent()
    {
        string once = AppPatch.PatchAppJs(AllAnchors);
        string twice = AppPatch.PatchAppJs(once);

        Assert.Equal(once, twice);
        Assert.Equal(1, Count(twice, "/*qdl-cut:adult-block*/"));
        Assert.Equal(1, Count(twice, "/*qdl-cut:adult-flag*/"));
    }

    [Fact]
    public void PatchAppJs_ListCache_CutsClientCacheOfMoreScreen()
    {
        // Экран «Ещё» (category_full) кешировал страницы у КЛИЕНТА на двое суток, а догон SWR
        // туда не достаёт: он подписан на событие 'line', которое шлёт только компонент РЯДА.
        // Владелец поймал это на 2.89 — на главной чисто, а внутри «Ещё» старый снимок.
        // life у cub-источника считается в МИНУТАХ (`var day = 60 * 24`), поэтому 15 — это 15 мин.
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("life: 15/*qdl-cut:list-cache*/", result);
        Assert.DoesNotContain("life: day * 2", result);
        // сам запрос не тронут — режем ТОЛЬКО срок жизни кеша
        Assert.Contains("oncomplite(Utils$1.addSource(data, source$1));", result);
        Assert.Contains("}, onerror, false, {", result);
    }

    [Fact]
    public void PatchAppJs_ListCache_Idempotent()
    {
        string once = AppPatch.PatchAppJs(AllAnchors);
        string twice = AppPatch.PatchAppJs(once);

        Assert.Equal(once, twice);
        Assert.Equal(1, Count(twice, "/*qdl-cut:list-cache*/"));
    }

    static readonly string AllAnchors = RawAnchors.Replace("\r\n", "\n");

    static int Count(string s, string sub) => s.Split(sub).Length - 1;

    [Fact]
    public void PatchAppJs_Broadcast_IconDetachedAndShareItemCut()
    {
        // «Трансляция» мертва by design (CUB-WebSocket выключен с 2.19, токена нет) — убираем оба
        // входа: иконку в шапке карточки и пункт «Поделиться» в панели плеера.
        string result = AppPatch.PatchAppJs(AllAnchors);

        // 1. иконка: класс вешается как раньше, но узел сразу снимается
        Assert.Contains("broadcast.addClass('open--broadcast');broadcast.remove();", result);
        // сам механизм не ломаем — hide() ниже отработает на detached-узле
        Assert.Contains("broadcast.hide();", result);

        // 2. пункт «Поделиться» исчез целиком, вместе с subtitle и method
        Assert.DoesNotContain("player_share_title", result);
        Assert.DoesNotContain("player_share_descr", result);
        Assert.DoesNotContain("method: 'share'", result);

        // 3. 🔴 соседи по массиву целы, структура не поехала: вырезан ровно один элемент
        Assert.Contains("player_speed_default_title", result);
        Assert.Contains("method: 'speed'", result);
        Assert.Contains("player_segments_title", result);
        Assert.Contains("method: 'segments'", result);
        Assert.Equal(Count(AllAnchors, "}, {") - 1, Count(result, "}, {"));
    }

    [Fact]
    public void PatchAppJs_Broadcast_Idempotent()
    {
        // Staticache может прогнать патч по уже пропатченному телу.
        string once = AppPatch.PatchAppJs(AllAnchors);
        string twice = AppPatch.PatchAppJs(once);

        Assert.Equal(once, twice);
        Assert.Equal(1, Count(twice, "/*qdl-cut:broadcast*/"));
        Assert.Equal(1, Count(twice, "/*qdl-cut:broadcast-share*/"));
        Assert.Equal(1, Count(twice, "broadcast.remove();"));
    }

    [Fact]
    public void PatchAppJs_Menu_HidesMytorrentsToo()
    {
        // «Мои торренты» дублируют наши «Загрузки». Убираем ПУНКТ МЕНЮ, а не torrents_use:
        // тот же флаг гейтит кнопку торрентов в карточке и разделы настроек Парсер/TorrServer.
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("item.action == 'mytorrents') return false;/*qdl-cut:menu*/", result);
        Assert.Contains("item.action == 'relise'", result);
        Assert.Contains("item.action == 'timetable'", result);
        Assert.Contains("item.action == 'subscribes'", result);
        // штатное условие upstream по torrents_use осталось на месте
        Assert.Contains("if (!window.lampa_settings.torrents_use && item.action == 'mytorrents') return false;", result);
    }

    [Fact]
    public void PatchAppJs_Preroll_EarlyReturn()
    {
        // Реклама: ранний return из Preroll.show — та же ветка, которой бандл сам пропускает
        // показ, поэтому плеер стартует как обычно, а Google IMA SDK не грузится вовсе.
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("function show$5(data, call) {return call();/*qdl-cut:preroll*/", result);
        Assert.Contains("Preroll.load(data.vast_url, call);", result);   // тело функции не тронуто
    }

    [Fact]
    public void PatchAppJs_Swr_RevalidatesCachedHit_WithoutTouchingMissOrFallback()
    {
        // Снимок отдаётся как раньше; ветка промаха и аварийный стале-фолбэк не тронуты.
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("secuses(cached, true);", result);        // штатная отдача снимка цела
        Assert.Contains("cache_old = old;", result);              // фолбэк на ошибку цел
        Assert.Contains("$.ajax(data);", result);                 // ветка промаха цела
        Assert.Contains("cached.qdl_req = params.url", result);   // метка для связи ряд↔URL
        Assert.Contains("cacheSet(params, fresh)", result);       // свежий ответ переписывает кеш
        Assert.Contains("Lampa.Listener.send('request_revalidate'", result);
        // 🔴 complite/secuses для свежего ответа звать нельзя: Progress в partNext досчитал бы
        // задачу второй раз, а Main.build() не идемпотентен — ряды бы задвоились.
        Assert.DoesNotContain("secuses(fresh", result);
        Assert.Equal(1, Count(result, "cacheSet(params, fresh)"));
    }

    [Fact]
    public void PatchAppJs_Swr_KillSwitchesReadableAtRuntime()
    {
        // Оба выключателя читаются в рантайме — второй патч бандла для отключения не нужен.
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("window.lampa_settings.qdl_swr !== false", result);   // серверный (lampainit-invc.js)
        Assert.Contains("typeof window.qdl_swr === 'function'", result);      // OTA-предикат из qdl.js
        Assert.Contains("window.qdl_swr(params)", result);                    // он же решает про троттлинг
    }

    [Fact]
    public void PatchAppJs_Swr_Idempotent_SingleMarkerAndSingleAnchor()
    {
        // Замена содержит собственный якорь: без гарда Contains(replacement) второй проход
        // вложил бы патч сам в себя (та же схема, что у menu-items).
        string once = AppPatch.PatchAppJs(AllAnchors);
        string twice = AppPatch.PatchAppJs(once);

        Assert.Equal(once, twice);
        Assert.Equal(1, Count(twice, "/*qdl-cut:swr*/"));
        Assert.Equal(1, Count(twice, "secuses(cached, true);"));
    }

    [Fact]
    public void PatchAppJs_StartupPauses_Shortened()
    {
        // 1000 мс «тройного вверх» + 1000 мс/fade 500 логотипа = 2.5 с, которые платил КАЖДЫЙ
        // старт каждого клиента. Проверяем и то, что новые значения на месте, и то, что старых
        // не осталось (иначе патч «применился», но мимо нужной ветки).
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("}, 150);", result);
        Assert.Contains("fadeOut(200,", result);
        Assert.DoesNotContain("}, 1000); // Старт приложения", result);
        Assert.DoesNotContain("fadeOut(500,", result);
    }

    [Fact]
    public void PatchAppJs_DevMenu_StaysReachableUnderManageRight()
    {
        // Меню разработчика не отнимаем — оно живёт под тем же правом «действия», что и
        // «Настройки»/«Консоль»: у обычных клиентов ранний proceed(), у устройств с грантом
        // прежние 1000 мс и рабочий тройной «вверх».
        //
        // 🔴 qdl 2.89: гейтом была кука qdl_unlock=1, теперь право. Читаем его из КЕША, который
        // кладёт qdl.js (Lampa.Storage.set пишет прямо в localStorage) — сюда мы попадаем на
        // старте, ещё до загрузки самого плагина, и синхронный localStorage единственное, что
        // тут доступно. Нет кеша — быстрый путь, это и есть безопасный дефолт.
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.DoesNotContain("qdl_unlock", result);
        Assert.Contains("localStorage.getItem('qdl_features')", result);
        Assert.Contains(".manage", result);
        Assert.Contains("return proceed();", result);
        Assert.Contains("var timer = setTimeout(function () {", result);   // сам механизм цел
    }

    [Fact]
    public void PatchAppJs_Shots_ObeysServicesFlag()
    {
        // У shots в бандле СВОЙ гейт, и services=false его не ловил — подчиняем тому же флагу,
        // а не вырезаем: вернуть = services:true. Гейты sport/tsarea при этом не трогаем.
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("if (window.lampa_settings.services && window.location.hostname !== 'localhost'", result);
        Assert.Contains("'/plugin/shots'", result);                        // сам include цел
        Assert.Contains("if (!window.lampa_settings.iptv && window.lampa_settings.services) {", result);
    }

    [Fact]
    public void PatchAppJs_PluginReset_DropsBusterKeepsOtherParams()
    {
        // reset=Math.random() бил только по внешним клиентам (блок гейтится «в URL нет IPv4»),
        // зато бил каждый старт: showApp() зовётся из Plugins.load(showApp) и ждёт ВСЕ плагины.
        // Остальные параметры (logged/origin) обязаны уцелеть — их читают сами плагины.
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.DoesNotContain("Math.random()", result);
        Assert.Contains("'logged=' + logged", result);
        Assert.Contains("'origin=' + origin", result);
    }

    [Fact]
    public void PatchAppJs_Idempotent_SecondPassChangesNothing()
    {
        // Staticache может прогнать отдачу повторно — второй проход обязан быть no-op.
        string once = AppPatch.PatchAppJs(AllAnchors);
        string twice = AppPatch.PatchAppJs(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void PatchAppJs_AutonextGate_KeepsUpstreamCondition()
    {
        // Автопереход глушится ТОЛЬКО для элементов с нашим флагом: штатный playlist_next
        // остаётся в условии, иначе «Загрузки» и всё остальное потеряли бы автопереход.
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("!(work && work.qdl_no_autonext)", result);
        Assert.Contains("Storage.field('playlist_next')", result);
        Assert.Contains("PlayerPlaylist.next();", result);
    }

    [Fact]
    public void PatchAppJs_ExportsSegments()
    {
        // Разметка серии, приехавшая после старта плеера, доставляется через Lampa.Segments.set
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("Segments: Segments,", result);
        Assert.Contains("PlayerPlaylist: PlayerPlaylist,", result);
    }

    [Fact]
    public void PatchAppJs_CutsNoticeIconAndCub()
    {
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.DoesNotContain("Head.addIcon(Template.string('icon_bell')", result);
        Assert.DoesNotContain("new NoticeCub()", result);
        // NoticeLampa живёт: classes.lampa читается бандлом без гарда (socket premiere, pushNotice)
        Assert.Contains("new NoticeLampa()", result);
        Assert.Contains("new NoticeAll()", result);
    }

    [Fact]
    public void PatchAppJs_MenuFilter_DropsAllThreeActions()
    {
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.Contains("item.action == 'relise'", result);
        Assert.Contains("item.action == 'timetable'", result);
        Assert.Contains("item.action == 'subscribes'", result);
        // штатная строка фильтра сохранена (замена дописывает, а не сносит)
        Assert.Contains("item.action == 'mytorrents') return false;", result);
    }

    [Fact]
    public void PatchAppJs_TtRows_ReplacesBothOccurrences()
    {
        string result = AppPatch.PatchAppJs(AllAnchors);

        Assert.DoesNotContain("if (screen == 'category' && params.url == 'movie') return;", result);
        // оба ряда (lately + recently) получили безусловный return
        int count = result.Split("return;/*qdl-cut:tt-rows*/").Length - 1;
        Assert.Equal(2, count);
        Assert.DoesNotContain("Timer.add(time_extract, extract);", result);
        Assert.Contains("Timer.add(time_favorites, favorites);", result);   // сосед не задет
    }

    [Fact]
    public void PatchAppJs_NoAnchors_InputUnchangedNoThrow()
    {
        // смена tree: якорей нет — вход возвращается как есть (warn в лог), без исключений
        const string js = "var lampa = 'совсем другой бандл';";
        Assert.Equal(js, AppPatch.PatchAppJs(js));
    }

    [Fact]
    public void PatchAppJs_Idempotent()
    {
        // Staticache может прогнать отдачу повторно — второй прогон не должен менять результат
        // (критично для menu-патча, чей replacement содержит собственный якорь)
        string once = AppPatch.PatchAppJs(AllAnchors);
        string twice = AppPatch.PatchAppJs(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void PatchAppJs_PartialAnchors_PatchesWhatItFinds()
    {
        // будущий tree: часть якорей уехала — уцелевшие всё равно режутся
        const string js = "this.classes.cub = new NoticeCub(); Timer.add(time_extract, extract);";
        string result = AppPatch.PatchAppJs(js);
        Assert.DoesNotContain("new NoticeCub()", result);
        Assert.DoesNotContain("Timer.add(time_extract, extract);", result);
    }

    [Fact]
    public void PatchAppJs_GridDedup_HooksBothPointsWithoutMutatingResponse()
    {
        // Экран «Ещё» рисовал одну карточку по два раза: страницы кешируются независимо
        // (Staticache 3 ч), а ?sort=now_playing — живой поток. Дедупа в бандле нет вовсе.
        string result = AppPatch.PatchAppJs(AllAnchors);

        // 1. обе точки подцеплены, штатные вызовы бандла целы
        Assert.Contains("window.qdl_grid_build === 'function'", result);
        Assert.Contains("window.qdl_grid_next === 'function'", result);
        Assert.Contains("this.loaded.push(data.results);", result);
        Assert.Contains("var split_total = Math.ceil(new_data.results.length / _this.limit_view);", result);

        // 2. 🔴 тело ответа НЕ мутируется: на попадании в клиентский кеш secuses(cached, true)
        //    отдаёт ТОТ ЖЕ объект, что лежит в памяти Request — укороченный results поселился
        //    бы в кеше, и карточки исчезли бы навсегда. Переприсваиваем локальную new_data.
        Assert.DoesNotContain("new_data.results = window.qdl_grid_next", result);
        Assert.Contains("new_data = { results: window.qdl_grid_next(_this, new_data.results) || new_data.results }", result);

        // 3. падение хука не имеет права ронять сборку и догрузку страницы
        Assert.Contains("try { if (typeof window.qdl_grid_build", result);
        Assert.Contains("try { if (typeof window.qdl_grid_next", result);
    }

    [Fact]
    public void PatchAppJs_GridDedup_Idempotent()
    {
        // Staticache может прогнать патч по уже пропатченному телу; оба replacement содержат
        // собственный якорь, поэтому гард Contains(replacement) обязан отработать.
        string once = AppPatch.PatchAppJs(AllAnchors);
        string twice = AppPatch.PatchAppJs(once);

        Assert.Equal(once, twice);
        Assert.Equal(1, Count(twice, "/*qdl-cut:grid-dedup-build*/"));
        Assert.Equal(1, Count(twice, "/*qdl-cut:grid-dedup-next*/"));
        Assert.Equal(1, Count(twice, "window.qdl_grid_build(this, data.results)"));
    }
}
