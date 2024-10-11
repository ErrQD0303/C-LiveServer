using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

string settingsPath = "settings/appsettings.json";
settingsPath = Path.Combine(Directory.GetCurrentDirectory(), settingsPath);

ReadFromSettingsFile(out var prefixes, out var htmlDirectory, out var indexUrl);

HttpListener httpListener = new();
CancellationTokenSource? cancellationTokenSource = null;
Task? httpListenerTask = null;

ConcurrentBag<HttpListenerContext> clients = new ConcurrentBag<HttpListenerContext>();

if (prefixes == null || prefixes.Length == 0) throw new ArgumentException("prefixes");

prefixes.ToList().ForEach(p => httpListener.Prefixes.Add(p));

if (!HttpListener.IsSupported)
{
    throw new Exception("System doesn't support HttpListener.");
}

var watcher = GetFileSystemWatcher();

await StartServer();

while (true) { }

async Task<byte[]> GetWebsite(string url = "")
{
    if (string.IsNullOrEmpty(url))
        url = indexUrl;
    string filePath = Path.Combine(Directory.GetCurrentDirectory(), htmlDirectory, url);
    return await File.ReadAllBytesAsync(filePath);
}

void ReadFromSettingsFile(out string[] prefixes, out string htmlDirectory, out string indexUrl)
{
    var root = new ConfigurationBuilder().AddJsonFile(settingsPath).Build();
    prefixes = (string[])root.GetSection("Prefixes").GetChildren().Select(p => p.Value).ToArray()!;
    htmlDirectory = root.GetSection("HtmlDirectory").Value?.ToString() ?? "html";
    indexUrl = root.GetSection("IndexFile").Value?.ToString() ?? "index.html";
}

async void OnChanged(object sender, FileSystemEventArgs e)
{
    System.Console.WriteLine("Reload server");
    //ReloadServer();
    foreach (var client in clients)
    {
        try
        {
            cancellationTokenSource?.Cancel();
            await client.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes($"data: reload\n\n"));
            await client.Response.OutputStream.FlushAsync();
        }
        catch (Exception ex)
        {
            System.Console.WriteLine("Error sending reload message: " + ex.Message);
        }
    }
}

void ReloadServer()
{
    if (httpListener.IsListening)
    {
        cancellationTokenSource.Cancel();
        httpListener.Stop();
    }

    Task.Run(async () =>
    {
        if (httpListenerTask != null)
            await httpListenerTask;
        StartServer();
    });
}

FileSystemWatcher GetFileSystemWatcher()
{
    FileSystemWatcher watcher = new FileSystemWatcher
    {
        Path = htmlDirectory,
        NotifyFilter = NotifyFilters.LastWrite,
        Filter = "*",
        EnableRaisingEvents = true
    };

    watcher.Changed += OnChanged;
    watcher.Created += OnChanged;
    watcher.Deleted += OnChanged;

    return watcher;
}

async Task StartServer()
{
    cancellationTokenSource = new CancellationTokenSource();
    httpListener.Start();
    httpListenerTask = Task.Run(() => ListenAsync(cancellationTokenSource));
    System.Console.WriteLine("Server started");
}

async Task ListenAsync(CancellationTokenSource tokenSource)
{
    try
    {
        while (!tokenSource.IsCancellationRequested && httpListener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await httpListener.GetContextAsync();
            }
            catch (HttpListenerException) when (tokenSource.IsCancellationRequested)
            {
                return;
            }

            var response = context.Response;
            var outputStream = response.OutputStream;
            var request = context.Request;
            System.Console.WriteLine($"Access by method: {request.HttpMethod}");

            byte[]? buffer = new byte[0];

            if (request.Url!.AbsolutePath.Equals("/events", StringComparison.OrdinalIgnoreCase))
            {
                _ = MapReloadEvents(context, tokenSource);
                continue;
            }

            if (request.Url!.AbsolutePath.Equals("/", StringComparison.OrdinalIgnoreCase))
            {
                response.Headers.Add("Content-Type", "text/html");
                buffer = await GetWebsite();
            }
            response.ContentLength64 = buffer.Length;
            await outputStream.WriteAsync(buffer.AsMemory(0, buffer.Length));
            outputStream.Close();
        }
    }
    catch (Exception ex)
    {
        System.Console.WriteLine($"Error: {ex.Message}");
    }
}

async Task MapReloadEvents(HttpListenerContext context, CancellationTokenSource tokenSource)
{
    context.Response.Headers.Add("Content-Type", "text/event-stream");
    clients.Add(context);
    // Keep the connection alive
    try
    {
        while (!tokenSource.IsCancellationRequested)
        {
            await Task.Delay(1000);
        }
    }
    finally
    {
        clients.TryTake(out _);
        context.Response.OutputStream.Close();
        httpListenerTask = Task.Run(() => ListenAsync(new CancellationTokenSource()));
    }
}