using Shared.Models.Events;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace QbitDownload;

// HTTP/2 для проксируемых потоков отдельных хостов (qdl 2.106).
//
// 🔴 Зачем. 04.09.2026 «Клубничка» перестала играть на pornhub: наш /proxy/ отдавал 410 Gone
// на master.m3u8, на index-плейлист и на КАЖДЫЙ .ts. Ошибка приходила не от нас — ProxyAPI
// ретранслирует статус источника как есть (Core/Middlewares/ProxyAPI.M3u8.cs, StatusCode =
// (int)response.StatusCode), а 410 отдавал сам phncdn.com.
//
// Замер на боевом (один и тот же подписанный URL, одна и та же секунда, из контейнера lampac):
//
//     наш /proxy/                       410
//     curl h2   + user-agent + referer  200 200 200 200
//     curl h1.1 + user-agent + referer  410 410 410 410
//     curl h2   + user-agent, без referer   410 410 410 412
//
// То есть CDN за Cloudflare требует ДВУХ вещей сразу: браузерный referer и HTTP/2. Referer у нас
// был всегда (PornHub.headers_stream), а вот версию протокола ProxyAPI не задаёт вовсе —
// HttpRequestMessage по умолчанию 1.1 (в CreateProxyHttpRequest строка с Version так и лежит
// закомментированной). Chrome по такому URL всегда идёт по h2, и связка «Chrome в user-agent +
// HTTP/1.1» для бот-менеджмента Cloudflare — очевидный не-браузер.
//
// ⚠️ Это НЕ регресс наших правок: ни один коммит в Core/Middlewares/ProxyAPI* и в
// Modules/Adult/PornHub не наш (последний — upstream, 14.07.2026). Изменилась сторона pornhub.
//
// Почему хук, а не правка ProxyAPI. Глобально поднять версию до 2.0 у ВСЕХ проксируемых потоков
// заманчиво (ALPN сам откатится на 1.1 там, где h2 нет), но это меняет транспорт разом для
// балансеров «Онлайн», jut.su и торрент-раздач — ради починки одного источника. Хук
// ProxyApiCreateHttpRequest зовётся из всех трёх мест, где ProxyAPI создаёт запрос
// (обычный поток, ветка "url or url", dash), и правит ровно перечисленные хосты. Тем же хуком
// пользуются апстримные Collaps/Phantom/Spectre — точка врезки штатная и переживает rebase.
//
// Список хостов — в конфиге (proxyHttp2Hosts), поэтому следующий такой источник чинится правкой
// init.conf на лету, без пересборки образа. Сравнение суффиксное: "phncdn.com" накрывает и
// hv-h.phncdn.com, и ev-h.phncdn.com (pornhub раздаёт с разных площадок), и превью-домены.
public static class ProxyHttp2
{
    // Дефолт живёт в коде, а не в ModuleConf: пустой/отсутствующий ключ в init.conf должен
    // означать «как из коробки», а не «выключить починку».
    public static readonly string[] defaultHosts = ["phncdn.com"];

    static IReadOnlyList<string> Hosts()
    {
        var list = ModInit.conf?.proxyHttp2Hosts;
        return list == null || list.Count == 0 ? defaultHosts : list;
    }

    /// <summary>Хост запроса подпадает под список (точное совпадение или поддомен).</summary>
    public static bool Match(string host, IReadOnlyList<string> hosts)
    {
        if (string.IsNullOrEmpty(host) || hosts == null)
            return false;

        foreach (string h in hosts)
        {
            if (string.IsNullOrWhiteSpace(h))
                continue;

            string suffix = h.Trim().TrimStart('.');

            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
                return true;

            // Именно ".suffix", а не Contains: иначе "phncdn.com" совпал бы с "evilphncdn.com".
            if (host.Length > suffix.Length + 1 && host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static Task OnCreateRequest(EventProxyApiCreateHttpRequest e)
    {
        try
        {
            var req = e?.requestMessage;

            // Только https: h2 без TLS (h2c) .NET по этой политике не поднимает, а на http://
            // запрос версии 2.0 просто ушёл бы обратно в 1.1 — лишний повод не трогать чужое.
            if (req == null || e.uri == null || !e.uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            if (!Match(e.uri.Host, Hosts()))
                return Task.CompletedTask;

            // RequestVersionOrLower, а не Exact: если у хоста однажды не окажется h2 в ALPN,
            // запрос уедет по 1.1 вместо исключения. Ровно так ведёт себя curl и браузер.
            req.Version = HttpVersion.Version20;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }
        catch (Exception ex)
        {
            // Хук стоит на пути КАЖДОГО проксируемого сегмента: своё исключение здесь уронило бы
            // и те потоки, к которым он не имеет отношения.
            Console.WriteLine("[QbitDownload] proxy http2: " + ex);
        }

        return Task.CompletedTask;
    }

    // ── Ретрай на «Cloudflare отшил» ────────────────────────────────────────────────────────
    //
    // 🔴 Одного HTTP/2 мало. Cloudflare встречает КАЖДОЕ новое соединение вызовом: первый запрос
    // по нему получает 410, дальше по тому же соединению всё идёт. А клиент "proxy" закрывает
    // соединение после 2 минут простоя (PooledConnectionIdleTimeout, Core/Startup.cs) — значит
    // в вызов попадает ровно то, чем пользуется владелец: открытие видео после паузы.
    //
    // Замер по ОДНОЙ ссылке после простоя 200 с, дважды подряд с одинаковым исходом:
    //     410 410 200 200 200 200
    // В клиенте первый 410 на master.m3u8 = «details [manifestLoadError] fatal [true]» и вечный
    // спиннер вместо видео. Куки бы вопрос сняли (__cf_bm), но у пула стоит UseCookies = false,
    // и включать их всему прокси ради одного источника нельзя.
    //
    // Отсюда три попытки: по замеру успех приходит на третьей. Паузу берём маленькую и растущую —
    // дело не в «подожди, отпустит», а в том, чтобы соединение уже было установлено; секундные
    // задержки тут только тянули бы старт видео.
    public static readonly int[] retryDelaysMs = [120, 250, 400];

    // 429 в списке не по «слишком часто», а потому что Cloudflare отдаёт его тем же вызовом;
    // 412 «request incorrect» приходил в тех же замерах вперемешку с 410.
    static readonly int[] retryStatuses = [410, 412, 429];

    static int? OnRetry(EventProxyApiRetry e)
    {
        try
        {
            if (e?.uri == null || e.attempt >= retryDelaysMs.Length)
                return null;

            if (System.Array.IndexOf(retryStatuses, e.statusCode) < 0)
                return null;

            // Тот же список хостов, что и у h2: чужие источники ретраить нечем и незачем —
            // их 410 означает «ссылка правда мертва», и повтор только задержал бы ошибку.
            if (!Match(e.uri.Host, Hosts()))
                return null;

            return retryDelaysMs[e.attempt];
        }
        catch (Exception ex)
        {
            Console.WriteLine("[QbitDownload] proxy retry: " + ex);
            return null;
        }
    }

    public static void Attach()
    {
        EventListener.ProxyApiCreateHttpRequest += OnCreateRequest;
        // += , а не присваивание: поле общее, и присваивание молча снесло бы чужого подписчика
        // (а Detach — заодно и наш собственный хук, если модуль перезагрузят дважды).
        EventListener.ProxyApiRetry += OnRetry;
    }

    public static void Detach()
    {
        EventListener.ProxyApiCreateHttpRequest -= OnCreateRequest;
        EventListener.ProxyApiRetry -= OnRetry;
    }
}
