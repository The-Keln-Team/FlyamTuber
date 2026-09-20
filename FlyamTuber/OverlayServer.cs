using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlyamTuber;

/// <summary>
/// Крошечный HTTP-сервер внутри программы. Отдаёт страницу с персонажем,
/// её добавляют в OBS как источник «Браузер» — тогда прозрачность работает
/// сама собой, без хромакея и без возни с захватом окна.
/// </summary>
public sealed class OverlayServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cancel;
    private readonly List<Stream> _clients = new();
    private readonly object _lock = new();

    private List<string> _sprites = new();
    private string? _halfPath;
    private string? _closedPath;
    private int _version;

    private volatile string _state = "{}";

    public int Port { get; private set; } = 8752;
    public bool IsRunning => _listener != null;
    public string Url => $"http://127.0.0.1:{Port}/overlay";
    public int Version => _version;

    public void Start(int port)
    {
        Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();           // бросит исключение, если порт занят

        _listener = listener;
        Port = port;
        _cancel = new CancellationTokenSource();

        _ = Task.Run(() => AcceptLoop(listener, _cancel.Token));
        _ = Task.Run(() => BroadcastLoop(_cancel.Token));
    }

    public void Stop()
    {
        _cancel?.Cancel();
        _cancel = null;

        lock (_lock)
        {
            foreach (Stream client in _clients)
            {
                try { client.Dispose(); } catch { }
            }
            _clients.Clear();
        }

        if (_listener != null)
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _listener = null;
        }
    }

    /// <summary>Меняет набор картинок. Страница сама перезагрузит их по новому номеру версии.</summary>
    public void SetSources(List<string> sprites, string? half, string? closed)
    {
        lock (_lock)
        {
            _sprites = sprites;
            _halfPath = half;
            _closedPath = closed;
            _version++;
        }
    }

    /// <summary>Вызывается каждый кадр из окна. Просто кладёт строку, сеть её заберёт сама.</summary>
    public void PushState(string json) => _state = json;

    // ---------- приём запросов ----------

    private async Task AcceptLoop(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch { return; }

            _ = Task.Run(() => Handle(context, token), token);
        }
    }

    private async Task Handle(HttpListenerContext context, CancellationToken token)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";

        try
        {
            switch (path)
            {
                case "/":
                case "/overlay":
                    await WriteText(context, "text/html; charset=utf-8", PageHtml);
                    break;

                case "/config":
                    await WriteText(context, "application/json; charset=utf-8", BuildConfig());
                    break;

                case "/events":
                    await StreamEvents(context, token);
                    break;

                default:
                    if (path.StartsWith("/img/", StringComparison.Ordinal))
                        await WriteImage(context, path);
                    else
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                    }
                    break;
            }
        }
        catch
        {
            try { context.Response.Abort(); } catch { }
        }
    }

    private static async Task WriteText(HttpListenerContext context, string type, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = type;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task WriteImage(HttpListenerContext context, string path)
    {
        string? file = null;

        lock (_lock)
        {
            string name = path["/img/".Length..];
            if (name == "half") file = _halfPath;
            else if (name == "closed") file = _closedPath;
            else if (int.TryParse(name, out int index) && index >= 0 && index < _sprites.Count)
                file = _sprites[index];
        }

        if (file == null || !File.Exists(file))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(file);
        context.Response.ContentType = "image/png";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    /// <summary>
    /// Поток событий. Браузер сам держит соединение и переподключается,
    /// если программа перезапустилась — для этого в HTML ничего писать не надо.
    /// </summary>
    private async Task StreamEvents(HttpListenerContext context, CancellationToken token)
    {
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.Add("Cache-Control", "no-cache");
        context.Response.SendChunked = true;

        Stream stream = context.Response.OutputStream;
        lock (_lock) { _clients.Add(stream); }

        try
        {
            while (!token.IsCancellationRequested)
                await Task.Delay(250, token);
        }
        catch
        {
        }
        finally
        {
            lock (_lock) { _clients.Remove(stream); }
            try { stream.Dispose(); } catch { }
        }
    }

    /// <summary>Рассылка состояния 60 раз в секунду, в своём потоке — окно не ждёт сеть.</summary>
    private async Task BroadcastLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            byte[] payload = Encoding.UTF8.GetBytes("data: " + _state + "\n\n");

            List<Stream> snapshot;
            lock (_lock) { snapshot = new List<Stream>(_clients); }

            foreach (Stream client in snapshot)
            {
                try
                {
                    await client.WriteAsync(payload, token);
                    await client.FlushAsync(token);
                }
                catch
                {
                    lock (_lock) { _clients.Remove(client); }
                }
            }

            try { await Task.Delay(16, token); }
            catch { return; }
        }
    }

    private string BuildConfig()
    {
        lock (_lock)
        {
            var builder = new StringBuilder();
            builder.Append("{\"v\":").Append(_version);
            builder.Append(",\"n\":").Append(_sprites.Count);
            builder.Append(",\"half\":").Append(_halfPath != null ? "true" : "false");
            builder.Append(",\"closed\":").Append(_closedPath != null ? "true" : "false");
            builder.Append('}');
            return builder.ToString();
        }
    }

    public void Dispose() => Stop();

    // ---------- страница для OBS ----------

    private const string PageHtml = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>FlyamTuber</title>
<style>
  html, body { margin: 0; height: 100%; background: transparent; overflow: hidden; }
  #wrap { position: absolute; transform-origin: 50% 90%; will-change: transform; }
  #wrap img { position: absolute; left: 0; top: 0; width: 100%; height: 100%;
              image-rendering: auto; user-select: none; -webkit-user-drag: none; }
</style>
</head>
<body>
<div id="wrap"></div>
<script>
const wrap = document.getElementById('wrap');
let cfg = null, sprites = [], half = null, closed = null;
let natural = { w: 0, h: 0 };

function url(name, v) { return '/img/' + name + '?v=' + v; }

async function loadConfig() {
  cfg = await (await fetch('/config')).json();
  wrap.innerHTML = '';
  sprites = [];
  half = closed = null;

  for (let i = 0; i < cfg.n; i++) {
    const img = new Image();
    img.src = url(i, cfg.v);
    img.style.opacity = '0';
    if (i === 0) img.onload = () => { natural = { w: img.naturalWidth, h: img.naturalHeight }; layout(); };
    wrap.appendChild(img);
    sprites.push(img);
  }
  if (cfg.half) { half = new Image(); half.src = url('half', cfg.v); half.style.opacity = '0'; wrap.appendChild(half); }
  if (cfg.closed) { closed = new Image(); closed.src = url('closed', cfg.v); closed.style.opacity = '0'; wrap.appendChild(closed); }

  layout();
}

// Рамку подгоняем под настоящие пропорции картинки, а не растягиваем по окну.
// Тогда проценты в clip-path попадают ровно туда же, куда в самой программе.
function layout() {
  if (!natural.w || !natural.h) return;
  const vw = window.innerWidth, vh = window.innerHeight;
  const scale = Math.min(vw / natural.w, vh / natural.h);
  const w = natural.w * scale, h = natural.h * scale;
  wrap.style.width = w + 'px';
  wrap.style.height = h + 'px';
  wrap.style.left = ((vw - w) / 2) + 'px';
  wrap.style.top = ((vh - h) / 2) + 'px';
}
window.addEventListener('resize', layout);

function apply(s) {
  for (let i = 0; i < sprites.length; i++) {
    sprites[i].style.opacity = (i === s.i) ? s.o : (i === s.p ? 1 : 0);
  }

  const inset = 'inset(' + (s.ey * 100) + '% ' + ((1 - s.ex - s.ew) * 100) + '% '
              + ((1 - s.ey - s.eh) * 100) + '% ' + (s.ex * 100) + '%)';

  if (half) { half.style.opacity = s.h; half.style.clipPath = inset; }
  if (closed) { closed.style.opacity = s.c; closed.style.clipPath = inset; }

  wrap.style.transform = 'translate(' + s.x + 'px,' + s.y + 'px) rotate(' + s.a + 'deg) scaleX(' + (s.m ? -1 : 1) + ')';
}

const events = new EventSource('/events');
events.onmessage = e => {
  const s = JSON.parse(e.data);
  if (!cfg || s.v !== cfg.v) { loadConfig(); return; }
  apply(s);
};

loadConfig();
</script>
</body>
</html>
""";
}