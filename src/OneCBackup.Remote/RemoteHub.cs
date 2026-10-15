using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OneCBackup.Remote;

/// <summary>Blind, bounded relay. Archive operations and mutual TLS are inside the forwarded binary stream.</summary>
public sealed class RemoteHub
{
    private sealed class Registration
    {
        public Channel<Guid> Offers { get; } = Channel.CreateBounded<Guid>(4);
        public SemaphoreSlim Slots { get; } = new(4, 4);
    }
    private sealed record Pending(Guid GatewayId, TaskCompletionSource<WebSocket> Connected, TaskCompletionSource Finished);
    private readonly ConcurrentDictionary<Guid, Registration> gateways = new();
    private readonly ConcurrentDictionary<Guid, Pending> pending = new();
    public static WebApplication Build(HubOptions options)
    {
        options.Validate();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, options.Port));
        var app = builder.Build(); var hub = new RemoteHub();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
        app.MapGet("/healthz", () => Results.Json(new { service = "onec-backup-relay", ready = true }));
        app.MapGet("/remote/{role}/{id:guid}/{offer:guid?}", async (HttpContext context) =>
        {
            if (!Guid.TryParse(context.Request.RouteValues["id"]?.ToString(), out var id) || !Authorized(context, options, id))
            { context.Response.StatusCode = 401; return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            switch (context.Request.RouteValues["role"]?.ToString())
            {
                case "control": await hub.Control(context, id); break;
                case "client": await hub.Client(context, id); break;
                case "gateway" when Guid.TryParse(context.Request.RouteValues["offer"]?.ToString(), out var offer): await hub.Gateway(context, id, offer); break;
                default: context.Response.StatusCode = 404; break;
            }
        });
        return app;
    }
    private static bool Authorized(HttpContext context, HubOptions options, Guid id)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length > 1031) return false;
        var peer = options.Peers.FirstOrDefault(p => p.GatewayId == id); if (peer is null) return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]));
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(peer.TokenSha256));
    }
    private async Task Control(HttpContext context, Guid id)
    {
        var registration = new Registration();
        if (!gateways.TryAdd(id, registration)) { context.Response.StatusCode = 409; return; }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var receive = Monitor(socket, shutdown.Token);
            try
            {
                var send = SendOffers(socket, registration.Offers.Reader, shutdown.Token);
                await Task.WhenAny(receive, send); shutdown.Cancel();
                try { await Task.WhenAll(receive, send); } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            }
            catch (Exception error) when (error is WebSocketException or IOException) { }
        }
        finally { gateways.TryRemove(new KeyValuePair<Guid, Registration>(id, registration)); registration.Offers.Writer.TryComplete(); }
    }
    private static async Task Monitor(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[128];
        var message = await socket.ReceiveAsync(buffer.AsMemory(), token);
        if (message.MessageType != WebSocketMessageType.Close) throw new IOException("Unexpected control input");
    }
    private static async Task SendOffers(WebSocket socket, ChannelReader<Guid> offers, CancellationToken token)
    {
        await foreach (var offer in offers.ReadAllAsync(token))
            await socket.SendAsync(Encoding.ASCII.GetBytes(offer.ToString("D")), WebSocketMessageType.Text, true, token);
    }
    private async Task Client(HttpContext context, Guid id)
    {
        if (!gateways.TryGetValue(id, out var registration)) { context.Response.StatusCode = 503; return; }
        if (!await registration.Slots.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 429; return; }
        var offer = Guid.NewGuid(); var record = new Pending(id, new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
        pending[offer] = record;
        try
        {
            using var client = await context.WebSockets.AcceptWebSocketAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await registration.Offers.Writer.WriteAsync(offer, timeout.Token);
            using var gateway = await record.Connected.Task.WaitAsync(timeout.Token);
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var outbound = Forward(client, gateway, transfer.Token); var inbound = Forward(gateway, client, transfer.Token);
            await Task.WhenAny(outbound, inbound); transfer.Cancel();
            try { await Task.WhenAll(outbound, inbound); } catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException) { }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or ChannelClosedException) { }
        finally { pending.TryRemove(offer, out _); record.Finished.TrySetResult(); registration.Slots.Release(); }
    }
    private async Task Gateway(HttpContext context, Guid id, Guid offer)
    {
        if (!pending.TryGetValue(offer, out var record) || record.GatewayId != id) { context.Response.StatusCode = 404; return; }
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!record.Connected.TrySetResult(socket)) { socket.Dispose(); return; }
        try { await record.Finished.Task.WaitAsync(context.RequestAborted); }
        catch (OperationCanceledException) { }
        finally { socket.Dispose(); }
    }
    private static async Task Forward(WebSocket source, WebSocket destination, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReceiveAsync(buffer.AsMemory(), token);
            if (read.MessageType == WebSocketMessageType.Close) return;
            if (read.MessageType != WebSocketMessageType.Binary) throw new IOException("Binary TLS tunnel required");
            await destination.SendAsync(buffer.AsMemory(0, read.Count), WebSocketMessageType.Binary, read.EndOfMessage, token);
        }
    }
}
