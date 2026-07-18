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
    request.open('GET', activeHost + '/d1vision/hosts.json?v' + Math.random());
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
    createScript(activeHost + '/lampainit.js?v' + Math.random(), function(){
        console.log('lampainit load fail');
    })
    createScript(activeHost + '/lampa-main/app.min.js?v' + Math.random(), function(){
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

    request.open('GET', activeHost + '/lampa-main/app.min.js?v' + Math.random());
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

var timeLeft = 15;
var timerId  = setInterval(countdown, 1000);
var probeIdx = 0;
var probing  = false;   // проба (2.5с) длиннее тика (1с) — не запускаем параллельные пробы

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
}

// Единая точка старта: гарда от двойного запуска (успешная проба могла прилететь
// одновременно с 15-секундным таймаутом — раньше это грозило двойной вставкой скриптов).
function startOnce(host) {
    if (appStarted) return;
    appStarted = true;
    clearTimeout(timerId);

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

function countdown() {
    if (timeLeft == 0) {
        if (!probing) startOnce(null);   // бюджет исчерпан — стартуем с primary (onerror уведёт в кэш)
        return;
    }
    timeLeft--;

    if (probing) return;   // предыдущая проба (до 2.5с) ещё идёт — ждём её колбэк

    // перебор хостов по кругу: {localhost}/LAN → tv → tv2 → OTA-кэш → снова …
    var hosts = d1vHosts();
    var probeHost = hosts[probeIdx % hosts.length];
    probeIdx++;
    probing = true;

    // контракт D1Vision: пробуем лёгкий /lampainit.js (не тяжёлый app.min.js)
    checkConnection(
        probeHost + '/lampainit.js?v' + Math.random(),
        function () {
            probing = false;
            startOnce(probeHost);
        },
        function () {
            probing = false;
            console.log('No Network: ' + probeHost);
        });
}

countdown();
