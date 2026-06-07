using Dalamud.Configuration;

namespace HideCatCat;

[Serializable]
public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool IsOpen { get; set; } = true;

    // WebSocket 服务器地址
    public string ServerUrl { get; set; } = "wss://your-server:port/ws";

    // 悬浮 HUD 位置 — 队友距离（非游戏状态）
    public float OverlayX { get; set; } = 0.5f;
    public float OverlayY { get; set; } = 0.3f;

    // 悬浮 HUD 位置 — 鼠队游戏 HUD
    public float GameOverlayX { get; set; } = 0.5f;
    public float GameOverlayY { get; set; } = 0.3f;

    // HUD 编辑模式：开启后可拖拽调整位置，关闭后 HUD 仅显示不响应鼠标
    public bool OverlayEditMode { get; set; }
}
