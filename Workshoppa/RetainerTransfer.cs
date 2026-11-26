using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Workshoppa.GameData;

namespace Workshoppa;

internal sealed unsafe class RetainerTransfer(IGameGui gameGui, ICondition condition, IDataManager dataManager,
    IPluginLog pluginLog)
{
    // Item UI categories for submersible hull, stern, bow and bridge.
    private const uint SubmersibleHullCategory = 101;
    private const uint SubmersibleBridgeCategory = 104;
    private const ulong Retrieve = 0;
    private const ulong Entrust = 1;
    private const ulong RetrieveQuantity = 3;
    private const ulong EntrustQuantity = 4;

    private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(4);
    private static readonly InventoryType[] PlayerPages =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7
    ];

    private readonly Queue<Slot> _sources = new();
    private Dictionary<uint, int> _required = new();
    private Session _session;
    private TransferKind _kind;
    private PendingMove? _pending;
    private bool _initialize;
    private bool _stopRequested;
    private long _lastMove;
    private int _moved;
    private int _skipped;
    private IReadOnlySet<uint>? _depositItemIds;

    public bool IsOpen => GetSession() != default;
    public ulong OpenRetainerId => GetSession().RetainerId;
    public static bool InventoriesReady => PlayerPages.All(IsLoaded) && RetainerPages.All(IsLoaded);
    public bool IsBusy => _session != default;
    public bool FinishedNormally { get; private set; }
    public string Status { get; private set; } = string.Empty;
    private bool IsWithdrawal => _kind == TransferKind.WithdrawMaterials;
    private InventoryType[] Destinations => IsWithdrawal ? PlayerPages : RetainerPages;

    public void Start(TransferKind kind, IReadOnlyList<Ingredient>? materials = null,
        IReadOnlySet<uint>? depositItemIds = null)
    {
        if (IsBusy)
            return;

        var session = GetSession();
        if (session == default)
        {
            Status = "Open a retainer's inventory first.";
            return;
        }

        _required = materials?.Where(x => x.TotalQuantity > 0)
            .ToDictionary(x => x.ItemId, x => x.TotalQuantity) ?? new();
        _kind = kind;
        _depositItemIds = depositItemIds;
        _session = session;
        _initialize = true;
        _stopRequested = false;
        _lastMove = 0;
        _moved = _skipped = 0;
        FinishedNormally = false;
        Status = "Preparing transfer…";
    }

    public void Stop() => _stopRequested = true;

    public void RefreshMoveTimeout()
    {
        if (_pending is { } move)
            _pending = move with { Started = Stopwatch.GetTimestamp() };
    }

    public void Update()
    {
        if (!IsBusy)
            return;

        try
        {
            UpdateTransfer();
        }
        catch (Exception exception)
        {
            pluginLog.Error(exception, "Workshoppa retainer transfer stopped");
            Finish("Transfer failed. Check both inventories before trying again.");
        }
    }

    private void UpdateTransfer()
    {
        if (GetSession() != _session)
        {
            Finish("The retainer inventory closed or changed.");
            return;
        }

        if (_stopRequested && (_pending == null || _pending.WaitingForQuantity))
        {
            Finish("Transfer stopped.");
            return;
        }

        if (_initialize)
        {
            _initialize = false;
            if (!InventoriesReady)
            {
                Finish("The inventories are still loading. Try again when they are open.");
                return;
            }

            CaptureSourceSlots();
        }

        if (_pending != null && !CheckPendingMove(_pending))
            return;

        if (_stopRequested)
        {
            Finish("Transfer stopped.");
            return;
        }

        if (HasOpenDialog())
        {
            Status = "Close the open inventory dialog to continue.";
            return;
        }

        if (_lastMove != 0 && Stopwatch.GetElapsedTime(_lastMove) < MoveInterval)
            return;

        var items = dataManager.GetExcelSheet<Item>();
        while (_sources.TryDequeue(out var source))
        {
            if (Read(source.Container, source.Index) != source.Item || !items.TryGetRow(source.Item.BaseId, out var item))
            {
                _skipped++;
                continue;
            }

            int requested = IsWithdrawal ? Math.Min(source.Item.Quantity, GetShortfall(source.Item.BaseId))
                : source.Item.Quantity;
            if (requested <= 0)
                continue;

            var (count, capacity) = MeasureDestination(source.Item, (int)item.StackSize, Destinations);
            int amount = Math.Min(requested, capacity);
            if (amount <= 0)
            {
                _skipped++;
                continue;
            }

            var agent = AgentRetainer.Instance();
            if (agent == null)
            {
                Finish("The retainer is unavailable.");
                return;
            }

            bool partial = amount < source.Item.Quantity;
            ulong command = IsWithdrawal ? (partial ? RetrieveQuantity : Retrieve)
                : (partial ? EntrustQuantity : Entrust);
            _lastMove = Stopwatch.GetTimestamp();
            _pending = new(source, amount, count, partial, _lastMove);
            agent->HandleCallback((uint)source.Index, source.Container, 0, command);
            Status = $"Transferring items… {_moved:N0} moved.";
            return;
        }

        FinishedNormally = true;
        if (IsWithdrawal)
        {
            int missing = _required.Keys.Sum(GetShortfall);
            Finish(missing == 0 ? "Queue materials are ready." : $"{missing:N0} queue materials still needed.");
        }
        else
        {
            Finish("Deposit finished.");
        }
    }

    private void CaptureSourceSlots()
    {
        foreach (var type in IsWithdrawal ? RetainerPages : PlayerPages)
        {
            var container = GetContainer(type);
            for (int i = 0; i < container->Size; i++)
            {
                var stack = Read(type, i);
                if (stack.RawId == 0)
                    continue;

                if (IsWithdrawal ? !_required.ContainsKey(stack.BaseId)
                    : !MatchesDeposit(stack, _kind, _depositItemIds, out _))
                    continue;

                _sources.Enqueue(new Slot(type, i, stack));
            }
        }
    }

    public bool HasItemsToDeposit(TransferKind kind, IReadOnlySet<uint>? itemIds = null,
        bool requireRetainerSpace = false)
    {
        foreach (var type in PlayerPages)
        {
            var container = GetContainer(type);
            if (container == null)
                continue;
            for (int index = 0; index < container->Size; index++)
            {
                var stack = Read(type, index);
                if (MatchesDeposit(stack, kind, itemIds, out var item) &&
                    (!requireRetainerSpace || MeasureDestination(stack, (int)item.StackSize, RetainerPages).Capacity > 0))
                    return true;
            }
        }

        return false;
    }

    private bool MatchesDeposit(Stack stack, TransferKind kind, IReadOnlySet<uint>? itemIds, out Item item)
    {
        item = default;
        if (stack.RawId == 0 || (itemIds != null && (stack.Collectable || !itemIds.Contains(stack.BaseId))) ||
            !dataManager.GetExcelSheet<Item>().TryGetRow(stack.BaseId, out item))
            return false;

        bool submarinePart = item.ItemUICategory.RowId is >= SubmersibleHullCategory and <= SubmersibleBridgeCategory;
        return submarinePart == (kind == TransferKind.DepositSubmarineParts);
    }

    private bool CheckPendingMove(PendingMove move)
    {
        var source = Read(move.Source.Container, move.Source.Index);
        var original = move.Source.Item;
        bool sameItem = source.Matches(original);
        if (source.RawId != 0 && !sameItem)
        {
            Finish("A source slot changed during the transfer.");
            return false;
        }

        int decrease = original.Quantity - source.Quantity;
        if (decrease > 0)
        {
            var (destinationCount, _) = MeasureDestination(original, 1, Destinations);
            int increase = destinationCount - move.DestinationCount;
            if (!move.WaitingForQuantity && decrease <= move.Amount && increase == decrease)
            {
                _moved += decrease;
                if (decrease < move.Amount || (!IsWithdrawal && decrease < original.Quantity))
                    _skipped++;
                _pending = null;
                return true;
            }
        }

        if (Stopwatch.GetElapsedTime(move.Started) >= MoveTimeout)
        {
            if (source != original || GetVisibleAddon("InputNumeric") != null)
            {
                Finish("A transfer could not be confirmed. Check both inventories.");
                return false;
            }

            _skipped++;
            _pending = null;
            return true;
        }

        if (move.WaitingForQuantity && source == original)
        {
            var numeric = GetVisibleAddon("InputNumeric");
            if (numeric != null && numeric->IsReady && numeric->AtkValuesCount >= 4)
            {
                var minimum = numeric->AtkValues[2];
                var maximum = numeric->AtkValues[3];
                if (minimum.Type != AtkValueType.UInt || maximum.Type != AtkValueType.UInt ||
                    minimum.UInt > move.Amount || maximum.UInt < move.Amount || maximum.UInt > original.Quantity ||
                    (IsWithdrawal && GetShortfall(original.BaseId) < move.Amount))
                {
                    Finish("The quantity prompt did not match the transfer.");
                    return false;
                }

                var value = new AtkValue { Type = AtkValueType.Int, Int = move.Amount };
                _pending = move with { WaitingForQuantity = false, Started = Stopwatch.GetTimestamp() };
                _lastMove = _pending.Started;
                numeric->FireCallback(1, &value, true);
            }
        }

        return false;
    }

    private static (int Count, int Capacity) MeasureDestination(Stack source, int stackLimit, InventoryType[] pages)
    {
        int count = 0;
        int capacity = 0;
        foreach (var type in pages)
        {
            var container = GetContainer(type);
            if (container == null)
                continue;

            for (int i = 0; i < container->Size; i++)
            {
                var stack = Read(type, i);
                if (stack.RawId == 0)
                    capacity += stackLimit;
                else if (stack.BaseId == source.BaseId)
                {
                    count += stack.Quantity;
                    if (stack.Matches(source) && !source.Collectable)
                        capacity += Math.Max(0, stackLimit - stack.Quantity);
                }
            }
        }

        return (count, capacity);
    }

    private int GetShortfall(uint itemId) => Math.Max(0, _required.GetValueOrDefault(itemId) - CountInInventory(itemId));

    public static int CountInInventory(uint itemId)
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : manager->GetInventoryItemCount(itemId, true, false, false) +
                                     manager->GetInventoryItemCount(itemId, false, false, false);
    }

    private Session GetSession()
    {
        if (!condition[ConditionFlag.OccupiedSummoningBell])
            return default;

        var window = GetVisibleAddon("InventoryRetainerLarge");
        if (window == null)
            window = GetVisibleAddon("InventoryRetainer");
        var manager = RetainerManager.Instance();
        if (window == null || manager == null || manager->LastSelectedRetainerId == 0)
            return default;

        return new Session(manager->LastSelectedRetainerId, window->Id);
    }

    private AtkUnitBase* GetVisibleAddon(string name)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible ? addon : null;
    }

    private bool HasOpenDialog() => GetVisibleAddon("InputNumeric") != null ||
        GetVisibleAddon("SelectYesno") != null || GetVisibleAddon("SelectOk") != null ||
        GetVisibleAddon("ContextMenu") != null;

    private static bool IsLoaded(InventoryType type) => GetContainer(type) != null;

    private static InventoryContainer* GetContainer(InventoryType type)
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(type);
        return container != null && container->IsLoaded ? container : null;
    }

    private static Stack Read(InventoryType type, int index)
    {
        var container = GetContainer(type);
        var item = container != null && index < container->Size ? container->GetInventorySlot(index) : null;
        return item == null || item->ItemId == 0 ? default :
            new Stack(item->ItemId, item->GetBaseItemId(), item->Quantity, item->Flags, item->IsCollectable());
    }

    private void Finish(string message)
    {
        Status = $"{message} {_moved:N0} moved.";
        if (_skipped > 0)
            Status += $" {_skipped:N0} source slot(s) could not be fully moved.";

        _session = default;
        _pending = null;
        _sources.Clear();
    }

    public enum TransferKind
    {
        DepositMaterials,
        DepositSubmarineParts,
        WithdrawMaterials,
    }

    private readonly record struct Session(ulong RetainerId, ushort AddonId);
    private readonly record struct Slot(InventoryType Container, int Index, Stack Item);
    private readonly record struct Stack(uint RawId, uint BaseId, int Quantity, InventoryItem.ItemFlags Flags, bool Collectable)
    {
        public bool Matches(Stack other) => RawId == other.RawId && Flags == other.Flags;
    }

    private sealed record PendingMove(Slot Source, int Amount, int DestinationCount, bool WaitingForQuantity, long Started);
}
