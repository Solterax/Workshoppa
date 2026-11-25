using System;
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
    private readonly Func<bool> _isCrafting;

    public ConfigWindow(IDalamudPluginInterface pluginInterface, Configuration configuration,
        QueueCompletionNotification queueNotification, Func<bool> isCrafting)
        : base("Workshoppa - Configuration###WorkshoppaConfigWindow")

    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _queueNotification = queueNotification;
        _isCrafting = isCrafting;

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

        bool usePopup = _configuration.UseWorkshoppaDesktopPopup;
        if (ImGui.Checkbox("Use Workshoppa desktop popup", ref usePopup))
        {
            _configuration.UseWorkshoppaDesktopPopup = usePopup;
            _pluginInterface.SavePluginConfig(_configuration);
        }

        if (ImGui.Button("Test desktop alert (3s)"))
            _queueNotification.ScheduleTest(_pluginInterface.UiBuilder.WindowHandlePtr);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Switch away from FFXIV to test the banner and taskbar flash.");
        if (!usePopup)
            ImGui.TextDisabled(_queueNotification.UsesNotificationMaster
                ? "Desktop alerts use NotificationMaster."
                : "Desktop alerts use Windows directly.");
        if (_queueNotification.TestStatus is { } testStatus)
            ImGui.TextWrapped(testStatus);

        ImGui.Separator();
        ImGui.BeginDisabled(_isCrafting());
        bool automatic = _configuration.AutomateRetainers;
        if (ImGui.Checkbox("Automate crafting and retainer trips", ref automatic))
        {
            _configuration.AutomateRetainers = automatic;
            _pluginInterface.SavePluginConfig(_configuration);
        }
        if (automatic)
        {
            string materials = _configuration.MaterialsRetainer ?? string.Empty;
            string parts = _configuration.SubmarinePartsRetainer ?? string.Empty;
            if (ImGui.InputText("Materials retainers", ref materials, 512))
            {
                _configuration.MaterialsRetainer = materials;
                _pluginInterface.SavePluginConfig(_configuration);
            }
            if (ImGui.InputText("Submarine parts retainers", ref parts, 512))
            {
                _configuration.SubmarinePartsRetainer = parts;
                _pluginInterface.SavePluginConfig(_configuration);
            }
            ImGui.TextWrapped($"Enter retainer names separated by commas, highest priority first. Uses vnavmesh to walk to the bell. Processes up to {Configuration.AutomaticBatchLimit} crafts per batch.");
        }
        ImGui.EndDisabled();
    }

    public void SaveWindowConfig() => _pluginInterface.SavePluginConfig(_configuration);
}
