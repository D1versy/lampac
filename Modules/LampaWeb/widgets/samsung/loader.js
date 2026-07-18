console.log('Start load');

// ── D1Vision: bootstrap-список хостов ──
// {localhost} подставляет сервер при сборке .wgt (ApiController SamsWgt, ?overwritehost= меняет);
// tv/tv2 — фолбек-домены (задел на будущее, DNS может ещё не существовать — проба дёшево фейлится).
// OTA: после успешного коннекта кэшируем /d1vision/hosts.json в localStorage['d1vision_hosts'];
// кэш только ДОПОЛНЯЕТ bootstrap-список, никогда не заменяет (защита от «окирпичивания»).
// Канонический документ: E:\Media-server\claude\08-clients.md (репо медиасервера).
// {localhost} — адрес, с которого реально скачан .wgt (сервер подставляет при сборке); идёт первым.
// LAN-литерал добавлен явно: если .wgt скачан с tv.d1versy.com, {localhost}→tv и без литерала
// LAN-хост потерялся бы из bootstrap. Дедуп в d1vHosts() схлопнет повтор, порядок сохранится.
var D1V_BOOTSTRAP = ['{localhost}', 'http://192.168.87.24:9118', 'https://tv.d1versy.com:9443', 'https://tv2.d1versy.com:9443'];
var activeHost = D1V_BOOTSTRAP[0];
var appStarted = false;

// Ключ периметра платформы tizen: сервер подставляет при сборке .wgt из init.conf d1v.keys.
// Без ключа (пусто или старый сервер не заменил плейсхолдер) LAN работает как раньше,
// а tv/tv2 из интернета закрыты периметром (проба честно сфейлится).
var D1V_KEY = '{d1vkey}';

// Grace-окно приоритета гонки хостов и общий бюджет старта — единый контракт клиентов D1Vision.
var D1V_GRACE_MS  = 300;
var D1V_BUDGET_MS = 15000;

// Подпись URL ключом периметра. Только наши WAN-домены (d1versy.com) — LAN открыт без ключа,
// на сторонние хосты ключ утекать не должен. Виджет живёт на file://, cookie не гарантирована,
// поэтому подписываем КАЖДЫЙ URL к хостам; подписанный запрос заодно посеет cookie для XHR приложения.
function d1vSign(url) {
    // Литерал плейсхолдера разорван конкатенацией: иначе серверный Replace («{d1vkey}» → ключ)
    // заменил бы и его, и сравнение стало бы всегда истинным.
    if (!D1V_KEY || D1V_KEY === '{' + 'd1vkey' + '}') return url;   // ключа нет / старый сервер не заменил
    // Матчим именно ХОСТ (суффикс .d1versy.com), а не подстроку URL — иначе ключ
    // утёк бы на tv.d1versy.com.evil.tld из отравленного OTA-списка.
    var m = url.match(/^https?:\/\/([^\/?#:]+)/i);
    var h = m && m[1].toLowerCase();
    if (!h || (h !== 'd1versy.com' && h.slice(-12) !== '.d1versy.com')) return url;
    return url + (url.indexOf('?') === -1 ? '?' : '&') + 'd1v=' + encodeURIComponent(D1V_KEY);
}

// Честная платформа для серверного платформенного блока (lampainit-invc.js):
// у Tizen свой UA, поэтому вместо UA-токена сеем ключ напрямую — ДО загрузки lampainit/app.
try { window.localStorage.setItem('d1vision_platform', 'tizen'); } catch (e) {}

function d1vHosts() {
    var list = [];
    var push = function (h) { if (h && list.indexOf(h) === -1) list.push(h); };   // дедуп bootstrap + OTA
    for (var i = 0; i < D1V_BOOTSTRAP.length; i++) push(D1V_BOOTSTRAP[i]);
    try {
        var extra = JSON.parse(window.localStorage.getItem('d1vision_hosts') || '[]');
        for (var j = 0; j < extra.length; j++) push(extra[j]);   // OTA только ДОПОЛНЯЕТ bootstrap
    } catch (e) {}
    return list;
}

function saveOtaHosts() {
    var request = new XMLHttpRequest();
    request.onload = function () {
        try {
            if (request.status == 200) {
                var j = JSON.parse(request.responseText);
                if (j && j.hosts && j.hosts.length)
                    window.localStorage.setItem('d1vision_hosts', JSON.stringify(j.hosts));
            }
        } catch (e) {}
    };
    request.onerror = function () {};
    request.open('GET', d1vSign(activeHost + '/d1vision/hosts.json?v' + Math.random()));
    request.send();
}

function createScript(src,error){
    console.log('Load script:' + src);

    var script         = document.createElement('script');
        script.onerror = error;
        script.src     = src;
        script.type    = 'text/javascript';
        script.async   = false;   // порядок исполнения = порядок вставки: lampainit.js обязан отработать до app.min.js

    document.getElementsByTagName("body")[0].appendChild(script);
}

function startAppWithDeepLink(){
    // lampainit.js грузим с ТОГО ЖЕ хоста, что и app (раньше был статичный тег {localhost} в
    // index.html — при уходе на фолбек-хост он бил в мёртвый адрес и плагины не приезжали)
    createScript(d1vSign(activeHost + '/lampainit.js?v' + Math.random()), function(){
        console.log('lampainit load fail');
    })
    createScript(d1vSign(activeHost + '/lampa-main/app.min.js?v' + Math.random()), function(){
        console.log('Protocol https fail');

        loadFromLocal()
    })
}

function saveToLocal(){
    var request = new XMLHttpRequest();

    request.onload = function() {
        if (this.readyState == 4 && this.status == 200) {
            window.localStorage.setItem('app.js',this.responseText)

            console.log('Saved in storage')
        }
    };

    request.onerror = function () {

    };

    request.open('GET', d1vSign(activeHost + '/lampa-main/app.min.js?v' + Math.random()));
    request.send();
}

function loadFromLocal(){
	if(window.appready) return

    var app = window.localStorage.getItem('app.js')

    if(app){
        console.log('Try eval app')

        try{
            eval(app)
        }
        catch(e){
            createScript('app.js', function(){
                console.log('Load local error');
            })
        }
    }
    else{
        createScript('app.js', function(){
            console.log('Load local error');
        })
    }
}

function checkConnection(url, successCb, errorCb) {
    var xhr = new XMLHttpRequest();
    var executed = false;

    xhr.open('GET', url, true);
    xhr.timeout = 2500;   // контракт D1Vision: таймаут пробы 2.5с (WAN-фолбек tv/tv2 не успевал за 800мс)
    xhr.onload = function () {
        if (executed) {
            return;
        }
        executed = true;
        if (xhr.status == '200') {
            successCb && successCb(xhr);
        } else {
            errorCb && errorCb(xhr);
        }
    };
    xhr.onerror = function () {
        if (executed) {
            return;
        }
        executed = true;
        errorCb && errorCb(xhr);
    };
    xhr.ontimeout = function () {
        if (executed) {
            return;
        }
        executed = true;
        errorCb && errorCb(xhr);
    };
    xhr.send(null);

    return xhr;   // для xhr.abort() — гонка глушит проигравших
}

// Единая точка старта: гарда от двойного запуска (победа гонки могла прилететь
// одновременно с 15-секундным таймаутом — раньше это грозило двойной вставкой скриптов).
function startOnce(host) {
    if (appStarted) return;
    appStarted = true;
    clearTimeout(budgetTimer);

    if (host) {
        activeHost = host;
        startAppWithDeepLink();
        saveToLocal();
        saveOtaHosts();
    }
    else {
        // ни один хост не ответил за 15 с — пробуем primary как раньше (onerror уведёт в локальный кэш)
        startAppWithDeepLink();
        saveToLocal();
    }
}

// ── Гонка хостов (контракт D1Vision, одинаков на всех клиентах) ──
// Все пробы стартуют ОДНОВРЕМЕННО; приоритет = позиция в списке. Успех кандидата,
// выше которого живых не осталось, побеждает мгновенно; менее приоритетный успех
// ждёт старших не дольше grace 300 мс (LAN не проигрывает интернет-хосту из-за
// джиттера). Проигравшие пробы отменяются. onDone(host|null): null — все мертвы.
function raceHosts(hosts, onDone) {
    var failed = [], xhrs = [], best = -1, failCount = 0, decided = false, graceTimer = null;

    function allHigherFailed(i) {
        for (var k = 0; k < i; k++) if (!failed[k]) return false;
        return true;
    }

    function decide(idx) {
        if (decided) return;
        decided = true;
        if (graceTimer) clearTimeout(graceTimer);
        for (var k = 0; k < xhrs.length; k++)   // отмена проигравших; abort дёрнет onerror —
            if (k !== idx && xhrs[k]) try { xhrs[k].abort(); } catch (e) {}   // гарды executed/decided гасят
        onDone(idx >= 0 ? hosts[idx] : null);
    }

    function onResult(i, ok) {
        if (decided) return;
        xhrs[i] = null;
        if (ok) {
            if (allHigherFailed(i)) return decide(i);
            if (best === -1 || i < best) best = i;
            if (!graceTimer) graceTimer = setTimeout(function () { decide(best); }, D1V_GRACE_MS);
        } else {
            failed[i] = true;
            failCount++;
            if (best !== -1 && allHigherFailed(best)) return decide(best);
            if (failCount === hosts.length && best === -1) decide(-1);
        }
    }

    for (var i = 0; i < hosts.length; i++) (function (i) {   // ВСЕ пробы разом
        // контракт D1Vision: пробуем лёгкий /lampainit.js (не тяжёлый app.min.js)
        xhrs[i] = checkConnection(
            d1vSign(hosts[i] + '/lampainit.js?v' + Math.random()),
            function () { onResult(i, true); },
            function () { console.log('No Network: ' + hosts[i]); onResult(i, false); });
    })(i);
}

// Оркестрация: гонка → победитель стартует приложение; все мертвы → новая гонка через 1 с,
// пока в бюджете остаётся время хотя бы на одну полную пробу (~2.5 с + запас);
// бюджет исчерпан — budgetTimer стартует с primary (onerror уведёт в localStorage-кэш).
var raceStart   = new Date().getTime();
var budgetTimer = setTimeout(function () { startOnce(null); }, D1V_BUDGET_MS);

function runRace() {
    if (appStarted) return;
    raceHosts(d1vHosts(), function (host) {
        if (host) return startOnce(host);
        if (new Date().getTime() - raceStart < D1V_BUDGET_MS - 3000) setTimeout(runRace, 1000);
        // иначе — доживаем до budgetTimer
    });
}

runRace();
