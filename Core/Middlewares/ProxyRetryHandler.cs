using Shared.Models.Events;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Middlewares;

/// <summary>
/// Повтор проксируемого запроса, когда источник отшил нас по статусу. Решает подписчик
/// <see cref="EventListener.ProxyApiRetry"/> — сам обработчик ничего не знает ни про хосты,
/// ни про коды.
/// </summary>
///
/// 🔴 Зачем (qdl 2.106). Cloudflare у pornhub встречает КАЖДОЕ новое h2-соединение вызовом:
/// первый запрос по нему получает 410 Gone, дальше по тому же соединению всё идёт нормально.
/// А у клиента "proxy" стоит PooledConnectionIdleTimeout = 2 минуты (Core/Startup.cs), то есть
/// после любой паузы соединение закрывается и следующее открытие видео снова попадает в вызов.
/// Замер: пауза 200 с → «410 410 200 200 200 200» по ОДНОЙ и той же ссылке, дважды подряд
/// с одинаковым результатом. В клиенте это ровно та ошибка, с которой пришёл владелец:
/// плеер показывает «details [manifestLoadError] fatal [true]» и крутит спиннер, потому что
/// master.m3u8 не загрузился с первого раза. Куку __cf_bm нам не предъявить: у клиента
/// UseCookies = false, и включать их на общий пул ради одного источника нельзя.
///
/// Почему DelegatingHandler, а не цикл вокруг SendAsync в ProxyAPI. ProxyAPI.cs — живой
/// upstream-файл, который правится каждый месяц; ретрай там означал бы лишний уровень
/// вложенности на 80 строк и конфликт при каждом rebase. Здесь же в апстримном коде остаётся
/// ОДНА строка — AddHttpMessageHandler в Core/Startup.cs.
///
/// ⚠️ Повторяем только GET без тела: POST с телом-потоком второй раз не отправить (Body уже
/// вычитан), а запрос клонируется целиком, потому что переиспользование одного
/// HttpRequestMessage между отправками — источник тонких сюрпризов в HttpClient.
///
/// ⚠️ Обработчик стоит на клиенте фабрики "proxy". Если у ссылки свой прокси или своя кука,
/// FriendlyHttp.MessageClient создаёт HttpClient в обход фабрики — там ретрая не будет, и это
/// намеренно: такой запрос идёт через чужой выход, где 410 значит совсем другое.
public class ProxyRetryHandler : DelegatingHandler
{
    // Потолок попыток на случай, если подписчик однажды начнёт возвращать паузу всегда:
    // проксируемый запрос держит соединение клиента, зациклиться здесь — повесить плеер.
    const int maxAttempts = 4;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);

        if (EventListener.ProxyApiRetry == null || request.Content != null || request.Method != HttpMethod.Get)
            return response;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int? delay;

            try
            {
                delay = EventListener.ProxyApiRetry(new EventProxyApiRetry(request.RequestUri, (int)response.StatusCode, attempt));
            }
            catch (Exception ex)
            {
                Console.WriteLine("ProxyApiRetry: " + ex);
                return response;
            }

            if (delay == null)
                return response;

            response.Dispose();

            if (delay > 0)
                await Task.Delay(delay.Value, ct).ConfigureAwait(false);

            using (var retry = Clone(request))
                response = await base.SendAsync(retry, ct).ConfigureAwait(false);
        }

        return response;
    }

    static HttpRequestMessage Clone(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };

        foreach (var h in source.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

        return clone;
    }
}
