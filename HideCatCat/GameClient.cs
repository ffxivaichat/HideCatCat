using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HideCatCat;

/// <summary>
/// WebSocket 客户端，连接到 HideCatCatServer。
/// 自动重连、收发 JSON 消息。
/// </summary>
public class GameClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string _password = "";
    private bool _disposed;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>收到服务器消息时触发</summary>
    public event Action<JsonElement>? OnMessage;

    /// <summary>连接状态变化时触发</summary>
    public event Action<bool>? OnConnectionChanged;

    /// <summary>连接到服务器，传入服务器地址和口令</summary>
    public async Task ConnectAsync(string serverUrl, string password)
    {
        // URL 格式校验
        if (string.IsNullOrWhiteSpace(serverUrl) ||
            (!serverUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
             !serverUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)))
        {
            Plugin.Log.Warning($"[GameClient] ❌ 无效的服务器地址: {serverUrl}");
            OnConnectionChanged?.Invoke(false);
            return;
        }

        var fullUrl = $"{serverUrl}?password={Uri.EscapeDataString(password)}";
        if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out _))
        {
            Plugin.Log.Warning($"[GameClient] ❌ 无法解析的地址: {fullUrl}");
            OnConnectionChanged?.Invoke(false);
            return;
        }

        _password = password;
        _cts = new CancellationTokenSource();

        Plugin.Log.Info($"[GameClient] 正在连接 {serverUrl}?password=***");
        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(fullUrl), _cts.Token);

            Plugin.Log.Info($"[GameClient] ✅ 已连接 password=***");
            OnConnectionChanged?.Invoke(true);

            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[GameClient] ❌ 连接失败: {ex.Message}");
            OnConnectionChanged?.Invoke(false);
        }
    }

    /// <summary>断开连接</summary>
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        _ws = null;
        OnConnectionChanged?.Invoke(false);
        Plugin.Log.Info("[GameClient] 已断开");
    }

    /// <summary>发送 JSON 消息</summary>
    public async Task SendAsync(object message)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            Plugin.Log.Warning($"[GameClient] 发送跳过 (State={_ws?.State})");
            return;
        }
        var json = JsonSerializer.Serialize(message);
        Plugin.Log.Info($"[GameClient] → {json}");
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[GameClient] ❌ 发送失败: {ex.Message}");
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Plugin.Log.Info($"[GameClient] ← {json}");
                try
                {
                    var doc = JsonDocument.Parse(json);
                    OnMessage?.Invoke(doc.RootElement);
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[GameClient] 接收异常: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                OnConnectionChanged?.Invoke(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _ws?.Dispose();
    }
}
