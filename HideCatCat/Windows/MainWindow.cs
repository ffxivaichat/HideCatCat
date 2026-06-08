using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace HideCatCat.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin _plugin;
    private readonly GameClient _gameClient;

    // UI state
    private string _password = "";
    private string _playerName = "";
    private string _selectedTeam = "";
    private bool _hasJoined;
    private float _radius = 50f;
    private string _winCondition = "ALL";
    private int _winCount = 1;
    private int _timeLimitMin = 5;
    private string _errorMessage = "";

    // Game state (from server)
    private readonly object _playersLock = new();
    private List<PlayerInfo> _players = new();
    private string _gameState = "WAITING";
    private string _hostName = "";
    private bool _settingsLocked;
    private bool _gameStarted;
    private DateTime _gameStartTime;
    private int _timeLimitSec;
    private double _startX, _startY, _startZ;
    private string _lastEvent = "";
    private bool _gameOver;

    public MainWindow(Plugin plugin, GameClient gameClient) : base("Hide Cat Cat 🐱🐭")
    {
        _plugin = plugin;
        _gameClient = gameClient;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(350, 280),
            MaximumSize = new Vector2(500, 700),
        };

        _gameClient.OnMessage += OnServerMessage;
        _gameClient.OnConnectionChanged += OnConnectionChanged;
        _gameClient.OnError += OnError;
    }

    /// <summary>游戏悬浮 HUD 数据，供 Plugin.DrawOverlay 读取（仅鼠队显示）</summary>
    public bool OverlayVisible => _gameStarted && _selectedTeam == "MOUSE";
    public string OverlayText { get; private set; } = "";
    /// <summary>最近猫的距离（鼠队HUD着色用）</summary>
    public float NearestCatDistance { get; private set; }

    public override void Draw()
    {
        // 连接状态
        if (_gameClient.IsConnected)
        {
            ImGui.TextColored(new Vector4(0.2f, 0.9f, 0.3f, 1f), "● 已连接");
            ImGui.SameLine();
            if (ImGui.Button("断开")) _ = _gameClient.DisconnectAsync();
        }
        else
        {
            ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1f), "● 未连接");
        }

        ImGui.Separator();

        // HUD 编辑模式开关
        var editMode = _plugin.OverlayEditMode;
        if (ImGui.Checkbox("编辑 HUD 位置", ref editMode))
            _plugin.OverlayEditMode = editMode;
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启后可直接拖拽屏幕上的 HUD 调整位置");

        ImGui.Separator();

        // 连接面板
        if (!_gameClient.IsConnected)
        {
            DrawConnectPanel();
            return;
        }

        // 已连接 → 选择阵营
        if (!_hasJoined)
        {
            DrawTeamSelection();
            return;
        }

        // 已加入房间 → 游戏主面板
        DrawGamePanel();
    }

    private void DrawConnectPanel()
    {
        _playerName = _plugin.LocalPlayerName;

        ImGui.Text($"玩家: {_playerName}");
        if (string.IsNullOrEmpty(_playerName))
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.2f, 1f), "请先登录游戏角色");
            return;
        }

        // 服务器地址
        var serverUrl = _plugin.ServerUrl;
        ImGui.Text("服务器:");
        ImGui.SameLine();
        ImGui.InputText("##serverUrl", ref serverUrl, 100);
        if (serverUrl != _plugin.ServerUrl)
            _plugin.ServerUrl = serverUrl;

        // 自动生成口令
        if (string.IsNullOrEmpty(_password))
            _password = GeneratePassword();

        ImGui.Text("口令:");
        ImGui.SameLine();
        ImGui.InputText("##password", ref _password, 12);
        ImGui.SameLine();
        if (ImGui.Button("Roll"))
            _password = GeneratePassword();
        ImGui.SameLine();
        if (ImGui.Button("Copy"))
            ImGui.SetClipboardText(_password);

        if (ImGui.Button("连接", new Vector2(200, 30)) && !string.IsNullOrEmpty(_password))
        {
            _errorMessage = "";
            Plugin.Log.Info($"[UI] 点击连接 url={serverUrl} password=*** player={_playerName}");
            _ = _gameClient.ConnectAsync(serverUrl, _password);
        }

        if (!string.IsNullOrEmpty(_errorMessage))
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
            ImGui.TextWrapped(_errorMessage);
            ImGui.PopStyleColor();
        }
    }

    private static string GeneratePassword()
    {
        var chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rand = new Random();
        return new string(Enumerable.Range(0, 5).Select(_ => chars[rand.Next(chars.Length)]).ToArray());
    }

    private void DrawTeamSelection()
    {
        ImGui.Separator();
        ImGui.Text("选择阵营:");

        if (ImGui.Button("🐱 猫队", new Vector2(200, 40)))
            _ = JoinRoomAsync("CAT");
        if (ImGui.Button("🐭 鼠队", new Vector2(200, 40)))
            _ = JoinRoomAsync("MOUSE");
    }

    private async Task JoinRoomAsync(string team)
    {
        _selectedTeam = team;
        Plugin.Log.Info($"[UI] 选择 {team} 队，等待连接就绪...");
        // 等连接稳定
        for (int i = 0; i < 20 && _gameClient.IsConnected == false; i++)
            await Task.Delay(100);
        if (!_gameClient.IsConnected)
        {
            Plugin.Log.Warning("[UI] 连接未就绪，放弃发送 JOIN_ROOM");
            return;
        }
        Plugin.Log.Info($"[UI] 发送 JOIN_ROOM team={team}");
        var server = _plugin.HomeWorldName;
        await _gameClient.SendAsync(new { type = "JOIN_ROOM", password = _password, playerName = _playerName, playerServer = server, team });
    }

    private void DrawGamePanel()
    {
        ImGui.Separator();

        // 房主设置
        if (_playerName == _hostName && !_settingsLocked)
        {
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.4f, 1f), "👑 房主设置");
            if (ImGui.Button("📍 设为当前位置")) SetStartPoint();

            ImGui.Text("半径 (yalms):");
            ImGui.SameLine();
            ImGui.InputFloat("##radius", ref _radius);

            ImGui.Text("胜利条件:");
            var conds = new[] { "ALL", "COUNT", "PERCENT" };
            var labels = new[] { "全部抓到", "抓到 N 个", "抓到 X%" };
            var curIdx = Array.IndexOf(conds, _winCondition);
            if (curIdx < 0) curIdx = 0;
            if (ImGui.Combo("##cond", ref curIdx, labels, labels.Length))
            {
                _winCondition = conds[curIdx];
                _winCount = 1;
            }
            if (_winCondition != "ALL")
            {
                ImGui.SameLine();
                ImGui.InputInt("##winCount", ref _winCount);
            }

            ImGui.Text("时间限制 (分钟):");
            ImGui.SameLine();
            ImGui.InputInt("##time", ref _timeLimitMin);
            if (_timeLimitMin < 1) _timeLimitMin = 1;

            if (ImGui.Button("⚙ 应用设置", new Vector2(200, 25)))
                _ = SendSettings();
        }

        ImGui.Separator();

        // 玩家列表
        ImGui.Text($"玩家: 猫{CountTeam("CAT")} 鼠{CountTeam("MOUSE")}");
        List<PlayerInfo> playersSnapshot;
        lock (_playersLock) { playersSnapshot = _players.ToList(); }
        foreach (var p in playersSnapshot)
        {
            var icon = p.team switch { "CAT" => "🐱", "MOUSE" => "🐭", _ => "❓" };
            var ready = p.ready ? "✅" : "⏳";
            var host = p.isHost ? "👑" : "";
            ImGui.Text($"  {host}{icon} {p.name} {ready}");
        }

        ImGui.Separator();

        // 准备 & 开始 / 重新开始
        if (!_gameStarted)
        {
            if (_gameOver)
            {
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), "游戏结束");
                if (ImGui.Button("🔄 重新开始", new Vector2(200, 30)))
                {
                    Plugin.Log.Info("[UI] 点击重新开始");
                    ResetLocalState();
                    _gameOver = false;
                    // 通知服务器重置房间
                    _ = _gameClient.SendAsync(new { type = "RESET_ROOM", password = _password });
                }
            }
            else
            {
                if (ImGui.Button("✅ 准备", new Vector2(200, 30)))
                {
                    Plugin.Log.Info("[UI] 点击准备");
                    _ = _gameClient.SendAsync(new { type = "READY", password = _password });
                }

                if (_playerName == _hostName && _gameState == "ALL_READY")
                {
                    if (ImGui.Button("▶ 开始游戏", new Vector2(200, 30)))
                    {
                        Plugin.Log.Info("[UI] 房主点击开始游戏");
                        _ = _gameClient.SendAsync(new { type = "START_GAME", password = _password });
                    }
                }
            }
        }
        else
        {
            var remaining = Math.Max(0, _timeLimitSec - (int)(DateTime.Now - _gameStartTime).TotalSeconds);
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.2f, 1f), $"[{remaining / 60}:{remaining % 60:D2}]");

            var pos = _plugin.LocalPlayerPosition;
            var sb = new System.Text.StringBuilder();

            if (_selectedTeam == "MOUSE")
            {
                List<PlayerInfo> catSnapshot;
                lock (_playersLock) { catSnapshot = _players.Where(p => p.team == "CAT").ToList(); }
                var nearestCat = catSnapshot
                    .Where(p => !p.eliminated && (p.x != 0 || p.y != 0 || p.z != 0))
                    .Select(p => new { p.name, d = Math.Sqrt(Math.Pow(pos.X - p.x, 2) + Math.Pow(pos.Y - p.y, 2) + Math.Pow(pos.Z - p.z, 2)) })
                    .OrderBy(p => p.d)
                    .FirstOrDefault();
                if (nearestCat != null)
                {
                    NearestCatDistance = (float)nearestCat.d;
                    sb.AppendLine($"Cat: {nearestCat.name} {nearestCat.d:F1} yalms");
                }
            }
            OverlayText = sb.ToString().TrimEnd();

            _ = SendPositionIfNeededAsync(pos);
        }

        if (!string.IsNullOrEmpty(_lastEvent))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1f, 1f), _lastEvent);
        }
    }

    private void SetStartPoint()
    {
        var pos = _plugin.LocalPlayerPosition;
        _ = _gameClient.SendAsync(new
        {
            type = "UPDATE_SETTINGS",
            password = _password,
            startPos = new { x = pos.X, y = pos.Y, z = pos.Z },
            radius = _radius,
            winCondition = _winCondition,
            winCount = _winCount,
            timeLimitSec = _timeLimitMin * 60
        });
    }

    private async Task SendSettings()
    {
        var pos = _plugin.LocalPlayerPosition;
        await _gameClient.SendAsync(new
        {
            type = "UPDATE_SETTINGS",
            password = _password,
            startPos = new { x = pos.X, y = pos.Y, z = pos.Z },
            radius = _radius,
            winCondition = _winCondition,
            winCount = _winCount,
            timeLimitSec = _timeLimitMin * 60
        });
    }

    private int CountTeam(string team) { lock (_playersLock) { return _players.Count(p => p.team == team); } }

    private static double jsonTryGetDouble(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return 0;
    }

    private DateTime _lastPosSend;
    private async Task SendPositionIfNeededAsync(Vector3 pos)
    {
        if ((DateTime.Now - _lastPosSend).TotalMilliseconds < 500) return;
        _lastPosSend = DateTime.Now;

        // 猫队：检查当前选中目标是否是鼠队玩家
        string? targetPlayer = null;
        if (_selectedTeam == "CAT")
        {
            var targetName = _plugin.CurrentTarget.Name;
            if (!string.IsNullOrEmpty(targetName) && GetPlayersSnapshot().Any(p => p.name == targetName && p.team == "MOUSE" && !p.eliminated))
                targetPlayer = targetName;
        }

        // 只发送非 null 的 targetPlayer（不发 null 避免服务端解析异常）
        var payload = targetPlayer != null
            ? (object)new { type = "POSITION_UPDATE", password = _password, position = new { x = pos.X, y = pos.Y, z = pos.Z }, targetPlayer }
            : new { type = "POSITION_UPDATE", password = _password, position = new { x = pos.X, y = pos.Y, z = pos.Z } };
        await _gameClient.SendAsync(payload);
    }

    private void ResetLocalState()
    {
        _gameOver = false;
        _gameStarted = false;
        lock (_playersLock) { _players.Clear(); }
        _lastEvent = "";
    }

    private List<PlayerInfo> GetPlayersSnapshot()
    {
        lock (_playersLock) { return _players.ToList(); }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!connected)
        {
            _selectedTeam = "";
            _hasJoined = false;
            lock (_playersLock) { _players.Clear(); }
            _gameStarted = false;
            _gameOver = false;
        }
    }

    private void OnError(string error)
    {
        _errorMessage = error;
    }

    private void OnServerMessage(JsonElement json)
    {
        var type = json.GetProperty("type").GetString();
        Plugin.Log.Info($"[UI] 收到 {type}");
        switch (type.ToUpperInvariant())
        {
            case "PLAYER_LIST":
            {
                _hostName = json.GetProperty("hostName").GetString() ?? "";
                _gameState = json.GetProperty("gameState").GetString() ?? "";
                _settingsLocked = json.TryGetProperty("settingsLocked", out var sl) && sl.GetBoolean();
                var newList = new List<PlayerInfo>();
                foreach (var p in json.GetProperty("players").EnumerateArray())
                {
                    newList.Add(new PlayerInfo
                    {
                        name = p.GetProperty("name").GetString()!,
                        team = p.GetProperty("team").GetString()!,
                        ready = p.GetProperty("ready").GetBoolean(),
                        isHost = p.GetProperty("isHost").GetBoolean(),
                        eliminated = p.TryGetProperty("eliminated", out var el) && el.GetBoolean(),
                        x = jsonTryGetDouble(p, "x"),
                        y = jsonTryGetDouble(p, "y"),
                        z = jsonTryGetDouble(p, "z"),
                    });
                }
                lock (_playersLock) { _players = newList; }
                _hasJoined = GetPlayersSnapshot().Any(p => p.name == _playerName);
                break;
            }

            case "ALL_READY":
                _gameState = "ALL_READY";
                break;

            case "START_GAME":
                _gameOver = false;
                _gameStarted = true;
                _gameStartTime = DateTime.Now;
                _timeLimitSec = json.GetProperty("timeLimitSec").GetInt32();
                var sp = json.GetProperty("startPos");
                _startX = sp.GetProperty("x").GetDouble();
                _startY = sp.GetProperty("y").GetDouble();
                _startZ = sp.GetProperty("z").GetDouble();
                _lastEvent = $"游戏开始！半径: {json.GetProperty("radius").GetSingle():F0} yalms";
                break;

            case "CATCH_EVENT":
                _lastEvent = $"[Cat]{json.GetProperty("catName").GetString()} caught [Mouse]{json.GetProperty("mouseName").GetString()}! Mice left: {json.GetProperty("miceRemaining")}/{json.GetProperty("miceTotal")}";
                break;

            case "GAME_OVER":
                _gameStarted = false;
                _gameOver = true;
                _lastEvent = $"Winner: {json.GetProperty("winner").GetString()}! {json.GetProperty("reason").GetString()}";
                break;
        }
    }

    private class PlayerInfo
    {
        public string name = "";
        public string team = "";
        public bool ready;
        public bool isHost;
        public bool eliminated;
        public double x, y, z;
    }
}
