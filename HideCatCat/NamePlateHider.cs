using Dalamud.Game.Gui.NamePlate;
using Dalamud.Plugin.Services;

namespace HideCatCat;

/// <summary>
/// 管理游戏原生头顶名牌的隐藏与恢复。
/// 仅在游戏中且当前玩家为猫队时启用，隐藏所有非本地玩家的名牌内容：
/// 角色名、称号、FC标签、在线状态图标、职业图标、大型标记图标（指导者王冠等）。
/// 同时覆盖玩家、NPC、宠物等所有名牌类型。
/// </summary>
internal sealed class NamePlateHider : IDisposable
{
    private readonly INamePlateGui _namePlateGui;
    private readonly IObjectTable _objects;
    /// <summary>是否已订阅名牌更新事件，防止重复订阅/取消。</summary>
    private bool _enabled;

    public NamePlateHider(INamePlateGui namePlateGui, IObjectTable objects)
    {
        _namePlateGui = namePlateGui;
        _objects = objects;
    }

    /// <summary>
    /// 启用名牌隐藏：订阅 Dalamud 的名牌更新事件，并在下一帧请求重绘。
    /// 重复调用安全（_enabled 守护）。
    /// </summary>
    public void Enable()
    {
        if (_enabled) return;

        _namePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;
        _namePlateGui.RequestRedraw();
        _enabled = true;
    }

    /// <summary>
    /// 禁用名牌隐藏：取消订阅事件并请求重绘，使名牌恢复默认显示。
    /// </summary>
    public void Disable()
    {
        if (!_enabled) return;

        _namePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate;
        _namePlateGui.RequestRedraw();
        _enabled = false;
    }

    /// <summary>
    /// 释放时自动取消订阅，防止插件卸载后残留事件回调。
    /// </summary>
    public void Dispose()
    {
        Disable();
    }

    /// <summary>
    /// 名牌更新回调。对非本地玩家的所有名牌（玩家 + NPC）隐藏文本和图标。
    /// </summary>
    private void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        // 获取本地玩家的 GameObjectId，用于跳过自己的名牌
        var localPlayerId = _objects.LocalPlayer?.GameObjectId ?? 0;

        foreach (var handler in handlers)
        {
            // 不隐藏本地玩家自己
            if (localPlayerId != 0 && handler.GameObjectId == localPlayerId)
                continue;

            // 移除名字（玩家名 / NPC名）
            handler.RemoveName();
            // 移除称号（仅玩家有效，NPC 无副作用）
            handler.RemoveTitle();
            // 移除 FC 标签（仅玩家有效，NPC 无副作用）
            handler.RemoveFreeCompanyTag();
            // 移除在线状态图标
            handler.RemoveStatusPrefix();
            // 隐藏职业图标（设为 -1 禁用）
            handler.NameIconId = -1;
            // 隐藏大型标记图标（指导者王冠等，设为 0 禁用）
            handler.MarkerIconId = 0;
        }
    }
}
