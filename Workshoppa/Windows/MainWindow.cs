using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LLib;
using LLib.ImGui;
using Workshoppa.GameData;

namespace Workshoppa.Windows;

// FIXME The close button doesn't work near the workshop, either hide it or make it work
internal sealed class MainWindow : LWindow, IPersistableWindowConfig
{
    private static readonly Regex CountAndName = new(@"^(\d{1,5})x?\s+(.*)$", RegexOptions.Compiled);

    private readonly WorkshopPlugin _plugin;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly Configuration _configuration;
    private readonly WorkshopCache _workshopCache;
    private readonly IconCache _iconCache;
    private readonly IChatGui _chatGui;
    private readonly RecipeTree _recipeTree;
    private readonly RetainerTransfer _retainerTransfer;
    private readonly CraftTiming _craftTiming;
    private readonly IPluginLog _pluginLog;

    private string _searchString = string.Empty;
    private bool _checkInventory;
    private string _newPresetName = string.Empty;

    public MainWindow(WorkshopPlugin plugin, IDalamudPluginInterface pluginInterface, IClientState clientState,
        IObjectTable objectTable, Configuration configuration, WorkshopCache workshopCache, IconCache iconCache, IChatGui chatGui,
        RecipeTree recipeTree, RetainerTransfer retainerTransfer, CraftTiming craftTiming, IPluginLog pluginLog)
        : base($"Workshoppa v{typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}###WorkshoppaMainWindow")
    {
        _plugin = plugin;
        _pluginInterface = pluginInterface;
        _clientState = clientState;
        _objectTable = objectTable;
        _configuration = configuration;
        _workshopCache = workshopCache;
        _iconCache = iconCache;
        _chatGui = chatGui;
        _recipeTree = recipeTree;
        _retainerTransfer = retainerTransfer;
        _craftTiming = craftTiming;
        _pluginLog = pluginLog;

        Position = new Vector2(100, 100);
        PositionCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 50),
            MaximumSize = new Vector2(600, 9999),
        };

        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.MenuBar;
        AllowClickthrough = false;
    }

    public EOpenReason OpenReason { get; set; } = EOpenReason.None;
    public bool NearFabricationStation { get; set; }
    public ButtonState State { get; set; } = ButtonState.None;

    private bool IsDiscipleOfHand =>
        _objectTable.LocalPlayer?.ClassJob.RowId is >= 8 and <= 15;

    public WindowConfig WindowConfig => _configuration.MainWindowConfig;

    public override void DrawContent()
    {
        if (ImGui.BeginMenuBar())
        {
            ImGui.BeginDisabled(_plugin.CurrentStage != Stage.Stopped || _retainerTransfer.IsBusy);
            DrawPresetsMenu();
            DrawClipboardMenu();
            ImGui.EndDisabled();

            ImGui.EndMenuBar();
        }

        var currentItem = _configuration.CurrentlyCraftedItem;
        if (currentItem != null)
        {
            var currentCraft = _workshopCache.Crafts.Single(x => x.WorkshopItemId == currentItem.WorkshopItemId);
            ImGui.Text($"Currently Crafting:");

            IDalamudTextureWrap? icon = _iconCache.GetIcon(currentCraft.IconId);
            if (icon != null)
            {
                ImGui.Image(icon.Handle, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2);
            }

            ImGui.TextUnformatted($"{currentCraft.Name}");
            ImGui.Spacing();

            if (_plugin.CurrentStage == Stage.Stopped)
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Search, "Check Inventory"))
                    _checkInventory = !_checkInventory;

                ImGui.SameLine();
                ImGui.BeginDisabled((!NearFabricationStation && !_plugin.CanResumeRetainerTrip) ||
                                    !IsDiscipleOfHand || _retainerTransfer.IsBusy);
                if (currentItem.StartedCrafting)
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Resume"))
                    {
                        State = ButtonState.Resume;
                        _checkInventory = false;
                    }
                }
                else
                {
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Start Crafting"))
                    {
                        State = ButtonState.Start;
                        _checkInventory = false;
                    }
                }

                ImGui.EndDisabled();

                ImGui.SameLine();

                bool keysHeld = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
                ImGui.BeginDisabled(!keysHeld || _retainerTransfer.IsBusy);
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Cancel"))
                {
                    State = ButtonState.Pause;
                    _configuration.CurrentlyCraftedItem = null;

                    Save();
                }

                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !keysHeld)
                    ImGui.SetTooltip(
                        $"Hold CTRL+SHIFT to remove this as craft. You have to manually use the fabrication station to cancel or finish the workshop project before you can continue using the queue.");

                ShowErrorConditions();
            }
            else
            {
                ImGui.BeginDisabled(_plugin.CurrentStage == Stage.RequestStop);
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Pause, "Pause"))
                    State = ButtonState.Pause;

                ImGui.EndDisabled();
            }
        }
        else
        {
            ImGui.Text("Currently Crafting: ---");

            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Search, "Check Inventory"))
                _checkInventory = !_checkInventory;

            ImGui.SameLine();
            if (_plugin.CurrentStage == Stage.Stopped)
            {
                bool hasQueuedCrafts = _configuration.ItemQueue.Any(item => item.Quantity > 0);
                bool pendingRetainerWork = _configuration.AutomateRetainers &&
                    (_plugin.PendingWorkshopStorage || _plugin.PendingCraftingOperations);
                bool hasWork = _configuration.AutomateRetainers
                    ? pendingRetainerWork || hasQueuedCrafts
                    : _configuration.RunnableQueue.Any();
                bool canResumeOperations = _configuration.AutomateRetainers && _plugin.PendingCraftingOperations;
                ImGui.BeginDisabled((!NearFabricationStation && !_plugin.CanResumeRetainerTrip && !canResumeOperations) ||
                                    !hasWork || !IsDiscipleOfHand || _retainerTransfer.IsBusy);
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play,
                        pendingRetainerWork ? "Resume" : "Start Crafting"))
                {
                    State = ButtonState.Start;
                    _checkInventory = false;
                }

                ImGui.EndDisabled();

                if (_configuration.AutomateRetainers)
                {
                    ImGui.BeginDisabled(!_clientState.IsLoggedIn || !hasQueuedCrafts || !IsDiscipleOfHand ||
                                        _retainerTransfer.IsBusy);
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Begin Crafting Operations"))
                    {
                        State = ButtonState.BeginOperations;
                        _checkInventory = false;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Travel to the Company Workshop with Lifestream, then use your assigned retainers to prepare the first batch.");
                    ImGui.EndDisabled();
                }

                ShowErrorConditions();
            }
            else if (_plugin.CurrentStage != Stage.RequestStop &&
                     ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Pause, "Pause"))
                State = ButtonState.Pause;
        }

        if (_plugin.RetainerTripStatus is { } tripStatus)
            ImGui.TextWrapped(tripStatus);
        else
            DrawRetainerTransfers();

        if (_checkInventory)
        {
            ImGui.Separator();
            CheckMaterial();
        }

        ImGui.Separator();
        ImGui.Text("Queue:");
        DrawQueueEstimate();
        bool queueLocked = _plugin.CurrentStage != Stage.Stopped || _retainerTransfer.IsBusy;
        float pauseButtonWidth = ImGui.CalcTextSize("Resume").X + 2 * ImGui.GetStyle().FramePadding.X;
        Configuration.QueuedItem? itemToRemove = null;
        if (_configuration.ItemQueue.Count > 0 &&
            ImGui.BeginTable("##QueueRows", 2, ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Craft", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Queue action", ImGuiTableColumnFlags.WidthFixed, pauseButtonWidth);
            for (int i = 0; i < _configuration.ItemQueue.Count; ++i)
            {
                using var _ = ImRaii.PushId(i);
                var item = _configuration.ItemQueue[i];
                var craft = _workshopCache.Crafts.Single(x => x.WorkshopItemId == item.WorkshopItemId);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (DrawQueueItem(item, craft, queueLocked))
                    itemToRemove = item;

                ImGui.TableSetColumnIndex(1);
                using var disabled = ImRaii.Disabled(_retainerTransfer.IsBusy ||
                    (_configuration.AutomateRetainers && _plugin.CurrentStage != Stage.Stopped));
                if (ImGui.Button(item.IsPaused ? "Resume" : "Pause", new Vector2(pauseButtonWidth, 0)))
                {
                    item.IsPaused = !item.IsPaused;
                    Save();
                }
            }

            ImGui.EndTable();
        }

        if (itemToRemove != null)
        {
            _configuration.ItemQueue.Remove(itemToRemove);
            Save();
        }

        ImGui.BeginDisabled(queueLocked);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##CraftSelection", "Add Craft...", ImGuiComboFlags.HeightLarge))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            ImGui.InputTextWithHint("", "Filter...", ref _searchString, 256);

            foreach (var craft in _workshopCache.Crafts
                         .Where(x => x.Name.Contains(_searchString, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x.WorkshopItemId))
            {
                IDalamudTextureWrap? icon = _iconCache.GetIcon(craft.IconId);
                Vector2 pos = ImGui.GetCursorPos();
                Vector2 iconSize = new Vector2(ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y);
                if (icon != null)
                {
                    ImGui.SetCursorPos(pos + new Vector2(iconSize.X + ImGui.GetStyle().FramePadding.X, ImGui.GetStyle().ItemSpacing.Y / 2));
                }

                if (ImGui.Selectable($"{craft.Name}##SelectCraft{craft.WorkshopItemId}", false, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _configuration.ItemQueue.Add(new Configuration.QueuedItem
                    {
                        WorkshopItemId = craft.WorkshopItemId,
                        Quantity = 1,
                    });
                    Save();
                }

                if (icon != null)
                {
                    ImGui.SameLine(0, 0);
                    ImGui.SetCursorPos(pos);
                    ImGui.Image(icon.Handle, iconSize);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();

        DrawCraftTimes();

        ImGui.Separator();
        ImGui.Text($"Debug (Stage): {_plugin.CurrentStage}");
    }

    private bool DrawQueueItem(Configuration.QueuedItem item, WorkshopCraft craft, bool queueLocked)
    {
        using IDalamudTextureWrap? icon = _iconCache.GetIcon(craft.IconId);
        if (icon != null)
        {
            ImGui.Image(icon.Handle, new Vector2(ImGui.GetFrameHeight()));
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        }

        using var disabled = ImRaii.Disabled(queueLocked);
        ImGui.SetNextItemWidth(Math.Max(100 * ImGui.GetIO().FontGlobalScale,
            4 * (ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X)));
        int quantity = item.Quantity;
        if (ImGui.InputInt("##Quantity", ref quantity))
        {
            item.Quantity = Math.Max(0, quantity);
            Save();
        }

        ImGui.OpenPopupOnItemClick("##QueueItemActions");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextWrapped(craft.Name);
        ImGui.OpenPopupOnItemClick("##QueueItemActions");
        if (!ImGui.BeginPopup("##QueueItemActions"))
            return false;

        bool remove = ImGui.MenuItem($"Remove {craft.Name}");
        ImGui.EndPopup();
        return remove;
    }

    private void DrawPresetsMenu()
    {
        if (ImGui.BeginMenu("Presets"))
        {
            if (_configuration.Presets.Count == 0)
            {
                ImGui.BeginDisabled();
                ImGui.MenuItem("Import Queue from Preset");
                ImGui.EndDisabled();
            }
            else if (ImGui.BeginMenu("Import Queue from Preset"))
            {
                if (_configuration.Presets.Count == 0)
                    ImGui.MenuItem("You have no presets.");

                foreach (var preset in _configuration.Presets)
                {
                    ImGui.PushID($"Preset{preset.Id}");
                    if (ImGui.MenuItem(preset.Name))
                    {
                        foreach (var item in preset.ItemQueue)
                        {
                            var queuedItem =
                                _configuration.ItemQueue.FirstOrDefault(x => x.WorkshopItemId == item.WorkshopItemId &&
                                    x.IsPaused == item.IsPaused);
                            if (queuedItem != null)
                                queuedItem.Quantity += item.Quantity;
                            else
                            {
                                _configuration.ItemQueue.Add(new Configuration.QueuedItem
                                {
                                    WorkshopItemId = item.WorkshopItemId,
                                    Quantity = item.Quantity,
                                    IsPaused = item.IsPaused,
                                });
                            }
                        }

                        Save();
                        _chatGui.Print($"Imported {preset.ItemQueue.Count} items from preset.");
                    }

                    ImGui.PopID();
                }

                ImGui.EndMenu();
            }

            if (_configuration.ItemQueue.Count == 0)
            {
                ImGui.BeginDisabled();
                ImGui.MenuItem("Export Queue to Preset");
                ImGui.EndDisabled();
            }
            else if (ImGui.BeginMenu("Export Queue to Preset"))
            {
                ImGui.InputTextWithHint("", "Preset Name...", ref _newPresetName, 64);

                ImGui.BeginDisabled(_configuration.Presets.Any(x =>
                    x.Name.Equals(_newPresetName, StringComparison.OrdinalIgnoreCase)));
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save"))
                {
                    _configuration.Presets.Add(new Configuration.Preset
                    {
                        Id = Guid.NewGuid(),
                        Name = _newPresetName,
                        ItemQueue = _configuration.ItemQueue.Select(x => new Configuration.QueuedItem
                        {
                            WorkshopItemId = x.WorkshopItemId,
                            Quantity = x.Quantity,
                            IsPaused = x.IsPaused,
                        }).ToList()
                    });

                    Save();
                    _chatGui.Print($"Saved queue as preset '{_newPresetName}'.");

                    _newPresetName = string.Empty;
                }

                ImGui.EndDisabled();

                ImGui.EndMenu();
            }

            if (_configuration.Presets.Count == 0)
            {
                ImGui.BeginDisabled();
                ImGui.MenuItem("Delete Preset");
                ImGui.EndDisabled();
            }
            else if (ImGui.BeginMenu("Delete Preset"))
            {
                if (_configuration.Presets.Count == 0)
                    ImGui.MenuItem("You have no presets.");

                Guid? presetToRemove = null;
                foreach (var preset in _configuration.Presets)
                {
                    ImGui.PushID($"Preset{preset.Id}");
                    if (ImGui.MenuItem(preset.Name))
                    {
                        presetToRemove = preset.Id;
                    }

                    ImGui.PopID();
                }

                if (presetToRemove != null)
                {
                    var preset = _configuration.Presets.First(x => x.Id == presetToRemove);
                    _configuration.Presets.Remove(preset);

                    Save();
                    _chatGui.Print($"Deleted preset '{preset.Name}'.");
                }

                ImGui.EndMenu();
            }

            ImGui.EndMenu();
        }
    }

    private void DrawClipboardMenu()
    {
        if (ImGui.BeginMenu("Clipboard"))
        {
            List<Configuration.QueuedItem> fromClipboardItems = new();
            try
            {
                string? clipboardText = GetClipboardText();
                if (!string.IsNullOrWhiteSpace(clipboardText))
                {
                    foreach (var clipboardLine in clipboardText.ReplaceLineEndings().Split(Environment.NewLine))
                    {
                        var match = CountAndName.Match(clipboardLine);
                        if (!match.Success)
                            continue;

                        var craft = _workshopCache.Crafts.FirstOrDefault(x =>
                            x.Name.Equals(match.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
                        if (craft != null && int.TryParse(match.Groups[1].Value, out int quantity))
                        {
                            fromClipboardItems.Add(new Configuration.QueuedItem
                            {
                                WorkshopItemId = craft.WorkshopItemId,
                                Quantity = quantity,
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                //_pluginLog.Warning(e, "Unable to extract clipboard text");
            }

            ImGui.BeginDisabled(fromClipboardItems.Count == 0);
            if (ImGui.MenuItem("Import Queue from Clipboard"))
            {
                _pluginLog.Information($"Importing {fromClipboardItems.Count} items...");
                int count = 0;
                foreach (var item in fromClipboardItems)
                {
                    var queuedItem =
                        _configuration.ItemQueue.FirstOrDefault(x => x.WorkshopItemId == item.WorkshopItemId && !x.IsPaused);
                    if (queuedItem != null)
                        queuedItem.Quantity += item.Quantity;
                    else
                    {
                        _configuration.ItemQueue.Add(new Configuration.QueuedItem
                        {
                            WorkshopItemId = item.WorkshopItemId,
                            Quantity = item.Quantity,
                        });
                    }

                    ++count;
                }

                Save();
                _chatGui.Print($"Imported {count} items from clipboard.");
            }

            ImGui.EndDisabled();

            ImGui.BeginDisabled(_configuration.ItemQueue.Count == 0);
            if (ImGui.MenuItem("Export Queue to Clipboard"))
            {
                var toClipboardItems = _configuration.ItemQueue.Select(x =>
                        new
                        {
                            _workshopCache.Crafts.Single(y => x.WorkshopItemId == y.WorkshopItemId).Name,
                            x.Quantity
                        })
                    .Select(x => $"{x.Quantity}x {x.Name}");
                ImGui.SetClipboardText(string.Join(Environment.NewLine, toClipboardItems));

                _chatGui.Print("Copied queue content to clipboard.");
            }

            if (ImGui.MenuItem("Export Material List to Clipboard"))
            {
                var toClipboardItems = _recipeTree.ResolveRecipes(GetMaterialList()).Where(x => x.Type == Ingredient.EType.Craftable);
                ImGui.SetClipboardText(string.Join(Environment.NewLine, toClipboardItems.Select(x => $"{x.TotalQuantity}x {x.Name}")));

                _chatGui.Print("Copied material list to clipboard.");
            }

            if (ImGui.MenuItem("Export Gathered/Venture materials to Clipboard"))
            {
                var toClipboardItems = _recipeTree.ResolveRecipes(GetMaterialList()).Where(x => x.Type == Ingredient.EType.Gatherable);
                ImGui.SetClipboardText(string.Join(Environment.NewLine, toClipboardItems.Select(x => $"{x.TotalQuantity}x {x.Name}")));

                _chatGui.Print("Copied material list to clipboard.");
            }

            ImGui.EndDisabled();

            ImGui.EndMenu();
        }
    }

    /// <summary>
    /// The default implementation for <see cref="ImGui.GetClipboardText"/> throws an NullReferenceException if the clipboard is empty, maybe also if it doesn't contain text.
    /// </summary>
    private unsafe string? GetClipboardText()
    {
        byte* ptr = ImGuiNative.GetClipboardText();
        if (ptr == null)
            return null;

        int byteCount = 0;
        while (ptr[byteCount] != 0)
            ++byteCount;
        return Encoding.UTF8.GetString(ptr, byteCount);
    }

    private void Save()
    {
        _pluginInterface.SavePluginConfig(_configuration);
    }

    public void Toggle(EOpenReason reason)
    {
        if (!IsOpen)
        {
            IsOpen = true;
            OpenReason = reason;
        }
        else
            IsOpen = false;
    }

    public override void OnClose()
    {
        OpenReason = EOpenReason.None;
    }

    private void DrawQueueEstimate()
    {
        double estimatedSeconds = 0;
        long estimatedItems = 0;
        long unknownItems = 0;
        var planned = _configuration.AutomateRetainers
            ? _configuration.ItemQueue.Where(item => item.Quantity > 0)
            : _configuration.RunnableQueue;
        foreach (var item in planned)
        {
            if (GetCraftAverage(item.WorkshopItemId) is { } average)
            {
                estimatedSeconds += average * item.Quantity;
                estimatedItems += item.Quantity;
            }
            else
                unknownItems += item.Quantity;
        }

        if (_configuration.CurrentlyCraftedItem is { } current)
        {
            if (GetCraftAverage(current.WorkshopItemId) is { } average &&
                _craftTiming.GetElapsedSeconds(current) is { } elapsed)
            {
                estimatedSeconds += Math.Max(0, average - elapsed);
                estimatedItems++;
            }
            else
                unknownItems++;
        }

        if (estimatedItems > 0)
        {
            var extra = unknownItems > 0 ? $" + {unknownItems:N0} without estimates" : string.Empty;
            ImGui.TextWrapped($"Estimated active time remaining: {FormatDuration(estimatedSeconds)}{extra}");
        }
        else if (unknownItems > 0)
            ImGui.TextDisabled("No time estimates for the remaining crafts.");
        else if (_configuration.ItemQueue.Any(item => item.IsPaused && item.Quantity > 0))
            ImGui.TextDisabled("All queued crafts are paused.");
    }

    private void DrawCraftTimes()
    {
        if (!ImGui.CollapsingHeader("Craft times"))
            return;

        if (_configuration.CraftTimes.Count == 0)
        {
            ImGui.TextWrapped("Complete a workshop craft to record its first time.");
            return;
        }

        foreach (var craft in _workshopCache.Crafts.OrderBy(x => x.Name))
        {
            if (GetCraftAverage(craft.WorkshopItemId) is { } average)
            {
                long samples = _configuration.CraftTimes[craft.WorkshopItemId].CompletedCrafts;
                string runs = samples == 1 ? "run" : "runs";
                ImGui.TextWrapped($"{craft.Name}: {FormatDuration(average)} average ({samples:N0} {runs})");
            }
        }
    }

    private double? GetCraftAverage(uint itemId) =>
        _configuration.CraftTimes.TryGetValue(itemId, out var stats) &&
        stats.CompletedCrafts > 0 && double.IsFinite(stats.TotalActiveSeconds) && stats.TotalActiveSeconds > 0
            ? stats.GetAverageSeconds()
            : null;

    private static string FormatDuration(double seconds)
    {
        seconds = Math.Ceiling(seconds);
        if (seconds >= 86400)
            return $"{Math.Floor(seconds / 86400):N0}d {Math.Floor(seconds % 86400 / 3600):0}h";
        if (seconds >= 3600)
            return $"{Math.Floor(seconds / 3600):0}h {Math.Floor(seconds % 3600 / 60):0}m";
        if (seconds >= 60)
            return $"{Math.Floor(seconds / 60):0}m {seconds % 60:0}s";
        return $"{seconds:0}s";
    }

    private void DrawRetainerTransfers()
    {
        bool retainerOpen = _retainerTransfer.IsOpen;
        if (!retainerOpen && string.IsNullOrEmpty(_retainerTransfer.Status))
            return;

        ImGui.Separator();
        if (retainerOpen)
        {
            ImGui.BeginDisabled(_plugin.CurrentStage != Stage.Stopped || _retainerTransfer.IsBusy);
            if (ImGui.Button("Deposit all materials"))
                _retainerTransfer.Start(RetainerTransfer.TransferKind.DepositMaterials);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Deposit every transferable item in your inventory bags except submarine parts.");

            ImGui.SameLine();
            if (ImGui.Button("Deposit all submarine parts"))
                _retainerTransfer.Start(RetainerTransfer.TransferKind.DepositSubmarineParts);
            ImGui.EndDisabled();

            ImGui.BeginDisabled(_plugin.CurrentStage != Stage.Stopped || _retainerTransfer.IsBusy ||
                (_configuration.CurrentlyCraftedItem == null && !_configuration.RunnableQueue.Any()));
            if (ImGui.Button("Withdraw needed materials"))
            {
                _retainerTransfer.Start(RetainerTransfer.TransferKind.WithdrawMaterials, GetMaterialList());
                _checkInventory = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Withdraw the shortfall for the active project and queued crafts from this retainer.");
            ImGui.EndDisabled();
        }

        if (_retainerTransfer.IsBusy)
        {
            if (ImGui.Button("Stop transfer"))
                _retainerTransfer.Stop();
        }

        if (!string.IsNullOrEmpty(_retainerTransfer.Status))
            ImGui.TextWrapped(_retainerTransfer.Status);
    }

    private void CheckMaterial()
    {
        ImGui.Text("Items needed for active and unpaused crafts:");
        var items = GetMaterialList();

        ImGui.Indent(20);
        foreach (var item in items)
        {
            int inInventory = RetainerTransfer.CountInInventory(item.ItemId);

            IDalamudTextureWrap? icon = _iconCache.GetIcon(item.IconId);
            if (icon != null)
            {
                ImGui.Image(icon.Handle, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2);

                icon.Dispose();
            }

            ImGui.TextColored(inInventory >= item.TotalQuantity ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
                $"{item.Name} ({inInventory} / {item.TotalQuantity})");
        }

        ImGui.Unindent(20);
    }

    private List<Ingredient> GetMaterialList()
    {
        List<uint> workshopItemIds = _configuration.RunnableQueue
            .SelectMany(x => Enumerable.Range(0, x.Quantity).Select(_ => x.WorkshopItemId))
            .ToList();
        Dictionary<uint, int> completedForCurrentCraft = new();
        var currentItem = _configuration.CurrentlyCraftedItem;
        if (currentItem != null)
        {
            workshopItemIds.Add(currentItem.WorkshopItemId);

            var craft = _workshopCache.Crafts.Single(x =>
                x.WorkshopItemId == currentItem.WorkshopItemId);
            for (int i = 0; i < currentItem.PhasesComplete; ++i)
            {
                foreach (var item in craft.Phases[i].Items)
                    AddMaterial(completedForCurrentCraft, item.ItemId, item.TotalQuantity);
            }

            if (currentItem.PhasesComplete < craft.Phases.Count)
            {
                foreach (var item in currentItem.ContributedItemsInCurrentPhase)
                    AddMaterial(completedForCurrentCraft, item.ItemId, (int)item.QuantityComplete);
            }
        }

        return workshopItemIds.Select(x => _workshopCache.Crafts.Single(y => y.WorkshopItemId == x))
            .SelectMany(x => x.Phases)
            .SelectMany(x => x.Items)
            .GroupBy(x => new { x.Name, x.ItemId, x.IconId })
            .OrderBy(x => x.Key.Name)
            .Select(x => new Ingredient
            {
                ItemId = x.Key.ItemId,
                IconId = x.Key.IconId,
                Name = x.Key.Name,
                TotalQuantity = completedForCurrentCraft.TryGetValue(x.Key.ItemId, out var completed)
                    ? x.Sum(y => y.TotalQuantity) - completed
                    : x.Sum(y => y.TotalQuantity),
                Type = Ingredient.EType.Craftable,
            })
            .ToList();
    }

    private static void AddMaterial(Dictionary<uint, int> completedForCurrentCraft, uint itemId, int quantity)
    {
        if (completedForCurrentCraft.TryGetValue(itemId, out var existingQuantity))
            completedForCurrentCraft[itemId] = quantity + existingQuantity;
        else
            completedForCurrentCraft[itemId] = quantity;
    }

    private void ShowErrorConditions()
    {
        if (!_plugin.WorkshopTerritories.Contains(_clientState.TerritoryType))
            ImGui.TextColored(ImGuiColors.DalamudRed, "You are not in the Company Workshop.");
        else if (!NearFabricationStation)
            ImGui.TextColored(ImGuiColors.DalamudRed, "You are not near a Fabrication Station.");

        if (!IsDiscipleOfHand)
            ImGui.TextColored(ImGuiColors.DalamudRed, "You need to be a Disciple of the Hand to start crafting.");
    }

    public void SaveWindowConfig() => Save();

    public enum ButtonState
    {
        None,
        Start,
        BeginOperations,
        Resume,
        Pause,
        Stop,
    }

    public enum EOpenReason
    {
        None,
        Command,
        NearFabricationStation,
        PluginInstaller,
    }
}
