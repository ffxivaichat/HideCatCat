using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using HideCatCat.Windows;

namespace HideCatCat;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private const string CommandName = "/hidecatcat";

    private readonly IDalamudPluginInterface _pi;
    private readonly ITargetManager _target;
    private readonly IPartyList _party;
    private readonly IClientState _client;
    private readonly IObjectTable _objects;
    private readonly IPlayerState _playerState;
    private readonly IFramework _framework;
    private readonly ICommandManager _command;
    private readonly IPluginLog _log;
    private readonly INamePlateGui _namePlateGui;

    public static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem _windowSystem;
    private readonly MainWindow _mainWindow;
    private readonly PluginConfig _config;
    private readonly GameClient _gameClient;
    /// <summary>管理头顶名牌的隐藏/恢复，仅在游戏中且猫队时启用。</summary>
    private readonly NamePlateHider _namePlateHider;

    /// <summary>当前选中目标的检测结果。</summary>
    public TargetInfo CurrentTarget { get; } = new();

    /// <summary>本地玩家名称（从游戏客户端获取）</summary>
    public string LocalPlayerName => _objects.LocalPlayer?.Name.TextValue ?? "";

    /// <summary>本地玩家坐标</summary>
    public Vector3 LocalPlayerPosition => _objects.LocalPlayer?.Position ?? Vector3.Zero;

    /// <summary>玩家所在服务器名</summary>
    public string HomeWorldName => _playerState.HomeWorld.ValueNullable?.Name.ToString() ?? "";

    /// <summary>WebSocket 服务器地址（持久化）</summary>
    public string ServerUrl
    {
        get => _config.ServerUrl;
        set { _config.ServerUrl = value; _pi.SavePluginConfig(_config); }
    }

    /// <summary>HUD 编辑模式（持久化）</summary>
    public bool OverlayEditMode
    {
        get => _config.OverlayEditMode;
        set { _config.OverlayEditMode = value; _pi.SavePluginConfig(_config); }
    }

    private IGameObject? _lastTarget;

    // HUD 拖拽状态机
    private bool _dragging;
    private Vector2 _dragOffset;

    public Plugin(
        IDalamudPluginInterface pi,
        ITargetManager target,
        IPartyList party,
        IClientState client,
        IObjectTable objects,
        IPlayerState playerState,
        IFramework framework,
        ICommandManager command,
        IPluginLog log,
        INamePlateGui namePlateGui)
    {
        _pi = pi;
        _target = target;
        _party = party;
        _client = client;
        _objects = objects;
        _playerState = playerState;
        _framework = framework;
        _command = command;
        _log = log;
        // 注入 INamePlateGui，用于修改游戏原生头顶名牌
        _namePlateGui = namePlateGui;
        Log = log;

        _config = pi.GetPluginConfig() as PluginConfig ?? new PluginConfig();

        _gameClient = new GameClient();
        // 创建名牌隐藏控制器，后续根据游戏状态和阵营决定是否启用
        _namePlateHider = new NamePlateHider(_namePlateGui, _objects);

        _windowSystem = new WindowSystem("HideCatCat");
        _mainWindow = new MainWindow(this, _gameClient);
        _mainWindow.IsOpen = _config.IsOpen;
        _windowSystem.AddWindow(_mainWindow);
    }

    public Task LoadAsync(CancellationToken ct)
    {
        _pi.UiBuilder.Draw += _windowSystem.Draw;
        _pi.UiBuilder.Draw += DrawOverlay;

        _command.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Hide Cat Cat window"
        });

        _framework.Update += OnFrameworkUpdate;

        _log.Info("HideCatCat loaded");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // 释放名牌隐藏控制器，取消事件订阅并恢复名牌显示
        _namePlateHider.Dispose();
        _framework.Update -= OnFrameworkUpdate;
        _pi.UiBuilder.Draw -= _windowSystem.Draw;
        _pi.UiBuilder.Draw -= DrawOverlay;
        _command.RemoveHandler(CommandName);

        _gameClient.Dispose();

        _mainWindow.IsOpen = false;
        _windowSystem.RemoveAllWindows();

        _config.IsOpen = _mainWindow.IsOpen;
        _pi.SavePluginConfig(_config);

        _log.Info("HideCatCat unloaded");
    }

    private void OnCommand(string command, string args)
    {
        _mainWindow.Toggle();
    }

    /// <summary>屏幕上绘制可拖拽的游戏 HUD 或队友距离。</summary>
    private void DrawOverlay()
    {
        var io = ImGui.GetIO();

        // 游戏进行中 → 显示躲猫猫 HUD
        if (_mainWindow.OverlayVisible)
        {
            var gx = _config.GameOverlayX;
            var gy = _config.GameOverlayY;
            DrawDraggableOverlay(_mainWindow.OverlayText, ColorWithDist(_mainWindow.NearestCatDistance), ref gx, ref gy);
            _config.GameOverlayX = gx;
            _config.GameOverlayY = gy;
            return;
        }

        // 非游戏状态 → 显示选中队友距离
        if (!CurrentTarget.IsTeammate) return;
        var dist = CurrentTarget.Distance;
        var t = $"{dist:F1} yalms";
        var ox = _config.OverlayX;
        var oy = _config.OverlayY;
        DrawDraggableOverlay(t, ColorWithDist(dist), ref ox, ref oy);
        _config.OverlayX = ox;
        _config.OverlayY = oy;
    }

    private static uint ColorWithDist(float dist) => dist switch
    {
        < 10 => 0xFF_0000FF,  // 红
        < 20 => 0xFF_00FFFF,  // 黄
        < 30 => 0xFF_FF8080,  // 淡蓝
        _    => 0xFF_00FF00,  // 绿
    };

    private void DrawDraggableOverlay(string text, uint color, ref float configX, ref float configY)
    {
        if (string.IsNullOrEmpty(text))
        {
            _dragging = false;
            return;
        }

        var io = ImGui.GetIO();
        var draw = ImGui.GetForegroundDrawList();
        var editMode = _config.OverlayEditMode;

        var lines = text.Split('\n');
        var lineH = ImGui.GetTextLineHeight() + 2;
        var totalW = 0f;
        foreach (var line in lines) { var w = ImGui.CalcTextSize(line).X; if (w > totalW) totalW = w; }
        var totalH = lines.Length * lineH;
        var pad = 6f;

        var x = io.DisplaySize.X * configX;
        var y = io.DisplaySize.Y * configY;
        var pos = new Vector2(x, y);

        var bgMin = pos - new Vector2(pad, pad);
        var bgMax = pos + new Vector2(totalW + pad, totalH + pad);

        // ── 命中检测 ──
        var mp = io.MousePos;
        var hovered = mp.X >= bgMin.X && mp.X <= bgMax.X
                   && mp.Y >= bgMin.Y && mp.Y <= bgMax.Y;

        if (editMode)
        {
            // ── 编辑模式：拖动状态机 ──
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _dragging = true;
                _dragOffset = mp - pos;
            }

            if (_dragging && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var newPos = mp - _dragOffset;
                configX = Math.Clamp(newPos.X / io.DisplaySize.X, 0f, 1f);
                configY = Math.Clamp(newPos.Y / io.DisplaySize.Y, 0f, 1f);
            }

            if (_dragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                _dragging = false;
                _pi.SavePluginConfig(_config);
            }

            // 拦截鼠标（编辑模式：hover 或拖拽中都要拦截）
            if (hovered || _dragging)
                io.WantCaptureMouse = true;

            // ── 编辑模式视觉 ──
            var borderColor = (hovered || _dragging) ? 0xFF00FFFF : 0x80FFFFFF;
            draw.AddRect(bgMin, bgMax, borderColor, 4f);

            var bgAlpha = (hovered || _dragging) ? 0xAA000000u : 0x66000000u;
            draw.AddRectFilled(bgMin, bgMax, bgAlpha, 4f);

            // 左上角拖动提示
            var hintText = _dragging ? " 释放以放置 " : " 拖动以调整位置 ";
            var hintSize = ImGui.CalcTextSize(hintText);
            draw.AddRectFilled(
                bgMin,
                bgMin + new Vector2(hintSize.X + 8, hintSize.Y + 4),
                0xCC000000, 4f);
            draw.AddText(bgMin + new Vector2(4, 2), 0xFF_FFFF00, hintText);
        }
        else
        {
            // ── 正常模式：仅显示，不响应鼠标 ──
            _dragging = false;
            draw.AddRectFilled(bgMin, bgMax, 0x88000000, 4f);
        }

        // ── 文字（两种模式都绘制）──
        var lp = pos;
        foreach (var line in lines)
        {
            draw.AddText(lp + Vector2.One, 0xCC_000000, line);
            draw.AddText(lp, color, line);
            lp.Y += lineH;
        }
    }

    /// <summary>每帧检测：① 根据游戏状态和阵营切换名牌隐藏 ② 目标变化时更新队友距离信息。</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        // 猫队 + 游戏进行中 → 启用名牌隐藏；否则 → 禁用
        if (_mainWindow.IsGameStarted && _mainWindow.IsCatTeam)
            _namePlateHider.Enable();
        else
            _namePlateHider.Disable();

        var target = _target.Target;
        if (target == _lastTarget) return;
        _lastTarget = target;

        if (target == null)
        {
            CurrentTarget.Reset();
            return;
        }

        CurrentTarget.Name = target.Name.TextValue;
        CurrentTarget.ObjectKind = target.ObjectKind.ToString();
        CurrentTarget.HasTarget = true;

        var isTeammate = false;

        if (target is ICharacter ch)
        {
            if (ch.StatusFlags.HasFlag(StatusFlags.PartyMember) ||
                ch.StatusFlags.HasFlag(StatusFlags.AllianceMember))
                isTeammate = true;
        }

        if (!isTeammate)
        {
            foreach (var member in _party)
            {
                if (member.EntityId == target.EntityId)
                {
                    isTeammate = true;
                    break;
                }
            }
        }

        CurrentTarget.IsTeammate = isTeammate;

        if (isTeammate)
        {
            var player = _objects.LocalPlayer;
            if (player != null)
            {
                CurrentTarget.Distance = Vector3.Distance(player.Position, target.Position);
            }
        }
    }
}

/// <summary>当前目标检测结果。</summary>
public sealed class TargetInfo
{
    public bool HasTarget { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ObjectKind { get; set; } = string.Empty;
    public bool IsTeammate { get; set; }
    public float Distance { get; set; }

    public void Reset()
    {
        HasTarget = false;
        Name = string.Empty;
        ObjectKind = string.Empty;
        IsTeammate = false;
        Distance = 0;
    }
}
