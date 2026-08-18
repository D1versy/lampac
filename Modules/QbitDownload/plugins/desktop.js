// ─────────────────────────────────────────────────────────────────────────────
// desktop.js — десктоп-адаптации D1Vision (qdl 2.50): Windows + Mac.
//
// Зачем: оба десктоп-клиента ходят с андроидной UA-маской («замок 4»), поэтому
// Lampa считает их ТВ-приставкой и скроллит «по-пультовому»: wheel-хендлер
// Scroll (app.min.js) душит колесо троттлом 200 мс, берёт только ЗНАК дельты
// (e.wheelDelta/120 — величина и инерция трекпада теряются), каждый шаг едет
// отдельной CSS-анимацией 0.3 с, а у лент onWheel вообще двигает фокус.
//
// Что делает этот файл (только windows/mac — гейт ниже и в lampainit-invc.js):
//  1. Перехватывает wheel НА document В CAPTURE-ФАЗЕ (штатные хендлеры висят на
//     самих .scroll в bubble → stopPropagation глушит их гарантированно) и ведёт
//     плавный глайд: честная дельта → аккумулятор цели → rAF с экспоненциальным
//     приближением через публичный scroll.shift() (инстанс достаётся из DOM:
//     html.Scroll, кламп границ внутри maxOffset).
//  2. shift() НЕ зовёт scrollEnded → ленивые постеры (Layer.visible) и пагинацию
//     (onEnd) дёргаем вручную с каденсом CHAIN_MS; заодно ГРУЗИМ РАНЬШЕ:
//     isEnd(3) — за 3 высоты вьюпорта до конца (штатно 2 и только по анимации).
//  3. После остановки ресинкает фокус-движок: карточка под курсором (или ближе
//     всех к центру) получает hover:touch (бухгалтерия last/active компонентов),
//     trigger_mouseenter (класс .focus как при обычном ховере) и тихий
//     Navigator.focused() — стрелки после скролла продолжают от вьюпорта,
//     а не от улетевшего старого фокуса.
//  4. Горизонтальные ленты: пиксельный скролл НЕ делаем (их onWheel = шаг фокуса,
//     на этом висит центрирование карточки) — оставляем шаг, но БЕЗ троттла
//     200 мс: порог 60 px / 90 мс листает в 2+ раза быстрее. deltaX трекпада и
//     Shift+колесо (Chromium конвертит в deltaX) листают ленту, вертикальное
//     колесо над лентой скроллит страницу.
//
// ⚠️ Грабли, учтённые здесь (не ломать при правках):
//  - transition глушим ТОЛЬКО классом .d1v-glide на время глайда: глобальное
//    отключение сломало бы штатную цепочку scrollEnded (ждёт webkitTransitionEnd
//    при animation=true) для фокус-скролла стрелками.
//  - onScroll компонентов НЕ переопределяем (qdl-каталог это прямо запрещает —
//    отключился бы Layer.visible), только вызываем существующий или
//    Lampa.Layer.visible напрямую — как в штатном scrollEnded.
//  - keydown НЕ preventDefault'им никогда (Lampa игнорирует события с
//    defaultPrevented — умерла бы навигация стрелками, грабля mac-клиента).
//  - повторные onEnd безопасны: у грида гвард next_wait/1000мс, у qdl-каталога
//    флаг loading.
// ─────────────────────────────────────────────────────────────────────────────
(function () {
    'use strict';

    var plat = window.d1vision_platform;
    if (plat !== 'windows' && plat !== 'mac') return;   // подключение уже гейтовано в lampainit-invc.js; это защита от ручной загрузки файла
    if (window.d1v_desktop) return;                     // синглтон (двойной putScriptAsync и т.п.)
    window.d1v_desktop = '1.0';

    var TAU = 110;          // мс — постоянная времени приближения к цели (экспоненциальный глайд)
    var CHAIN_MS = 120;     // каденс ручной цепочки Layer.visible/onScroll/onEnd во время глайда
    var END_RATIO = 3;      // ранняя пагинация: грузим за N высот вьюпорта до конца (штатно 2)
    var WHEEL_NOTCH = 100;  // |deltaY| ≥ порога при deltaMode=0 → щелчок колеса, не трекпад
    var NOTCH_SCALE = 1.6;  // px на щелчок ≈160 (штатный шаг Lampa был 150, но раз в 200 мс)
    var IDLE_MS = 140;      // тишина после последнего wheel, прежде чем закрывать глайд
    var LINE_PX = 60;       // горизонтальные ленты: порог аккумулятора на один шаг фокуса
    var LINE_MS = 90;       //                      и минимальный интервал между шагами

    // ── CSS ──
    if (!document.getElementById('d1v-desktop-css')) {
        var styleEl = document.createElement('style');
        styleEl.id = 'd1v-desktop-css';
        styleEl.textContent =
            // механика глайда: на время rAF-движения снимаем transition (сам shift()
            // вешает notransition лишь на 5 мс — между кадрами transition возвращался бы)
            '.scroll__body.d1v-glide{transition:none !important}' +
            // аффорданс кликабельности в mouse-режиме
            'body.d1v-desktop .selector{cursor:pointer}';
        document.head.appendChild(styleEl);
    }
    if (document.body) document.body.classList.add('d1v-desktop');

    // ── состояние ──
    var st = null;                  // активный глайд: { el, inst, body, pos, target, raf, time, chainAt, wheelAt }
    var mouseX = -1, mouseY = -1;   // последняя позиция курсора — для выбора кандидата фокуса
    var hacc = 0, hlast = 0, hel = null;   // аккумулятор горизонтальных лент

    // Нормализация дельты: deltaMode 1 = строки, 2 = страницы; дискретный щелчок
    // колеса (большая «ступенька» в px-режиме) слегка усиливаем, трекпад — 1:1.
    function norm(d, mode) {
        if (mode === 1) d *= 40;
        else if (mode === 2) d *= window.innerHeight;
        else if (Math.abs(d) >= WHEEL_NOTCH) d *= NOTCH_SCALE;
        var cap = 2 * window.innerHeight;
        return Math.max(-cap, Math.min(cap, d));
    }

    // Ручной аналог штатного scrollEnded(): ленивые постеры + ранняя пагинация.
    function chain(s, force) {
        var now = Date.now();
        if (!force && now - s.chainAt < CHAIN_MS) return;
        s.chainAt = now;
        try {
            if (s.inst.onScroll) s.inst.onScroll(-s.inst.position());
            else Lampa.Layer.visible(s.el);
            if (s.inst.onEnd && s.inst.isEnd(END_RATIO)) s.inst.onEnd();
        } catch (e) {}
    }

    // Кандидат для ресинка фокуса: .selector под курсором, иначе видимый ближе
    // всех к центру вьюпорта (скрытые и вне экрана отсеиваются).
    function pickCandidate(el) {
        if (mouseX >= 0 && document.elementFromPoint) {
            var under = document.elementFromPoint(mouseX, mouseY);
            var sel = under && under.closest ? under.closest('.selector') : null;
            if (sel && el.contains(sel)) return sel;
        }
        var vw = window.innerWidth, vh = window.innerHeight;
        var cx = vw / 2, cy = vh / 2;
        var best = null, bestDist = Infinity;
        var list = el.querySelectorAll('.selector');
        for (var i = 0; i < list.length; i++) {
            var r = list[i].getBoundingClientRect();
            if (!r.width || !r.height) continue;
            if (r.bottom < 0 || r.top > vh || r.right < 0 || r.left > vw) continue;
            var dx = (r.left + r.width / 2) - cx, dy = (r.top + r.height / 2) - cy;
            var d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = list[i]; }
        }
        return best;
    }

    function frame(now) {
        if (!st) return;
        if (!st.el.isConnected) {       // активность ушла (клик открыл карточку и т.п.)
            st.body.classList.remove('d1v-glide');
            st = null;
            return;
        }
        var dt = Math.min(50, now - st.time);
        st.time = now;
        var diff = st.target - st.pos;
        var step = Math.abs(diff) < 0.5 ? diff : diff * (1 - Math.exp(-dt / TAU));
        if (step) {
            var before = -st.inst.position();
            st.inst.shift(step);                        // кламп границ внутри (maxOffset)
            st.pos = -st.inst.position();
            if (Math.abs(st.pos - before - step) > 1) st.target = st.pos;   // упёрлись в край — цель к факту
            chain(st, false);
        }
        if (Math.abs(st.target - st.pos) >= 0.5 || Date.now() - st.wheelAt < IDLE_MS)
            st.raf = requestAnimationFrame(frame);
        else finish();
    }

    function start(el, dy) {
        if (st && st.el !== el) finish();               // переключились на другой скролл
        if (!st) {
            var inst = el.Scroll;
            st = {
                el: el, inst: inst, body: inst.body(true),
                pos: -inst.position(), target: -inst.position(),
                raf: 0, time: performance.now(), chainAt: 0, wheelAt: 0
            };
            st.body.classList.add('d1v-glide');
            st.raf = requestAnimationFrame(frame);
        }
        st.wheelAt = Date.now();
        st.target = Math.max(0, st.target + dy);
    }

    // Завершение глайда: штатное закрытие скролла + ресинк фокус-движка.
    // Порядок важен: hover:touch обновляет last/active компонента ДО onAnimateEnd
    // (виртуализация страниц грида компенсирует прыжок уже к новому last).
    function finish() {
        if (!st) return;
        var s = st;
        st = null;
        cancelAnimationFrame(s.raf);
        s.body.classList.remove('d1v-glide');
        if (!s.el.isConnected) return;
        var cand = pickCandidate(s.el);
        if (cand) try { Lampa.Utils.trigger(cand, 'hover:touch'); } catch (e) {}
        try { if (s.inst.onAnimateEnd) s.inst.onAnimateEnd(); } catch (e) {}
        chain(s, true);
        if (cand) {
            // как обычный ховер: clearAllFocus + класс .focus + hover:hover
            try { if (cand.trigger_mouseenter) cand.trigger_mouseenter(); } catch (e) {}
            // тихий перенос точки отсчёта стрелок (без события focus → без рывка scroll.update)
            try { if (window.Navigator && window.Navigator.focused) window.Navigator.focused(cand); } catch (e) {}
        }
    }

    // Горизонтальные ленты: штатный шаг фокуса (onWheel), но без троттла 200 мс.
    function stepLine(el, d) {
        if (hel !== el) { hacc = 0; hel = el; }
        hacc += d;
        if (Math.abs(hacc) < LINE_PX || Date.now() - hlast < LINE_MS) return;
        var dir = hacc > 0 ? 1 : -1;
        hacc = 0;
        hlast = Date.now();
        var inst = el.Scroll;
        try {
            var step = ((inst.params() || {}).step || 300) * dir;
            if (inst.onWheel) inst.onWheel(step); else inst.wheel(step);
        } catch (e) {}
    }

    function onWheel(e) {
        if (!e.target || !e.target.closest) return;
        // пинч трекпада приезжает как wheel с ctrlKey — глотаем над скроллами (зум не нужен)
        if (e.ctrlKey) {
            if (e.target.closest('.scroll')) { e.preventDefault(); e.stopPropagation(); }
            return;
        }
        var horiz = e.target.closest('.scroll--horizontal');
        var vert = e.target.closest('.scroll:not(.scroll--horizontal)');
        if (!vert && !horiz && !e.target.closest('.selectbox, .modal, .settings')) {
            // курсор вне скроллов (шапка и т.п.) — скроллим главный скролл активити;
            // при открытых модалках/настройках страницу ПОД ними не трогаем
            vert = document.querySelector('.activity--active .scroll:not(.scroll--horizontal)');
        }
        if (!vert && !horiz) return;
        e.preventDefault();
        e.stopPropagation();
        var dx = norm(e.deltaX, e.deltaMode);
        var dy = norm(e.deltaY, e.deltaMode);
        if (horiz && horiz.Scroll && Math.abs(dx) > Math.abs(dy)) return stepLine(horiz, dx);
        if (vert && vert.Scroll) return start(vert, dy);
        if (horiz && horiz.Scroll) return stepLine(horiz, dy);   // лента без вертикального родителя
    }

    document.addEventListener('wheel', onWheel, { capture: true, passive: false });

    // Chromium/WebKit шлют И легаси mousewheel — Lampa слушает оба; глушим дубль,
    // иначе к нашему глайду добавлялся бы штатный ступенчатый шаг.
    document.addEventListener('mousewheel', function (e) {
        if (e.target && e.target.closest && e.target.closest('.scroll')) {
            e.preventDefault();
            e.stopPropagation();
        }
    }, { capture: true, passive: false });

    // Стрелки/Enter во время глайда: остановить и ресинкнуть фокус ДО обработки
    // Lampa (capture на window срабатывает раньше её bubble-хендлеров). Без
    // preventDefault — см. шапку.
    window.addEventListener('keydown', function () { if (st) finish(); }, true);
    // Клик гасит инерцию (иначе карточка уезжает из-под курсора между down и up).
    window.addEventListener('mousedown', function () { if (st) st.target = st.pos; }, true);
    window.addEventListener('mousemove', function (e) {
        mouseX = e.clientX;
        mouseY = e.clientY;
    }, { capture: true, passive: true });
})();
