using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using LLib.ImGui;

namespace Workshoppa.Windows;

internal sealed class ConfigWindow : LWindow, IPersistableWindowConfig
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly QueueCompletionNotification _queueNotification;

    public ConfigWindow(IDalamudPluginInterface pluginInterface, Configuration configuration,
        QueueCompletionNotification queueNotification)
        : base("Workshoppa - Configuration###WorkshoppaConfigWindow")

    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _queueNotification = queueNotification;

        Position = new Vector2(100, 100);
        PositionCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.AlwaysAutoResize;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(270, 50),
        };
    }

    public WindowConfig WindowConfig => _configuration.ConfigWindowConfig;

    public override void DrawContent()
    {
        bool enableRepairKitCalculator = _configuration.EnableRepairKitCalculator;
        if (ImGui.Checkbox("Enable Repair Kit Calculator", ref enableRepairKitCalculator))
        {
            _configuration.EnableRepairKitCalculator = enableRepairKitCalculator;
            _pluginInterface.SavePluginConfig(_configuration);
        }

        bool enableCeruleumTankCalculator = _configuration.EnableCeruleumTankCalculator;
        if (ImGui.Checkbox("Enable Ceruleum Tank Calculator", ref enableCeruleumTankCalculator))
        {
            _configuration.EnableCeruleumTankCalculator = enableCeruleumTankCalculator;
            _pluginInterface.SavePluginConfig(_configuration);
        }

        bool notifyInBackground = _configuration.NotifyOnQueueCompletionInBackground;
        if (ImGui.Checkbox("Desktop alerts for queue completion and failure in the background", ref notifyInBackground))
        {
            _configuration.NotifyOnQueueCompletionInBackground = notifyInBackground;
            _pluginInterface.SavePluginConfig(_configuration);
        }

        if (ImGui.Button("Test desktop alert (3s)"))
            _queueNotification.ScheduleTest(_pluginInterface.UiBuilder.WindowHandlePtr);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Switch away from FFXIV to test the banner and taskbar flash.");
    }

    public void SaveWindowConfig() => _pluginInterface.SavePluginConfig(_configuration);
}
