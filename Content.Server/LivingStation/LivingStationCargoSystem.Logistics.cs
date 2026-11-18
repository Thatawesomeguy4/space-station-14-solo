using System.Linq;
using Content.Server.Cargo.Systems;
using Content.Server.Power.EntitySystems;
using Content.Shared.Access.Systems;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Lathe;
using Content.Shared.Stacks;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Verbs;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationCargoSystem
{
    private static readonly ProtoId<CargoAccountPrototype> CargoAccount = "Cargo";
    private static readonly EntProtoId EngineeringStock = "LivingStationEngineeringRestock";
    private static readonly EntProtoId JanitorialStock = "LivingStationJanitorialRestock";
    private static readonly ProtoId<StackPrototype> Steel = "Steel";
    private static readonly ProtoId<StackPrototype> Cable = "Cable";
    private static readonly ProtoId<StackPrototype> HullTiles = "FloorTileSteel";
    private static readonly ProtoId<ReagentPrototype> SpaceCleaner = "SpaceCleaner";
    private static readonly EntProtoId LightBulb = "LightBulb";
    private static readonly EntProtoId LightTube = "LightTube";
    private static readonly EntProtoId TrashBag = "TrashBag";
    private const int ExportSheets = 30;
    private const int ExportReward = 400;

    [Dependency] private CargoSystem _cargo = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private SharedStackSystem _stacks = default!;
    [Dependency] private SharedEntityStorageSystem _storage = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;

    private void InitializeCargoLogistics()
    {
        SubscribeLocalEvent<CargoOrderConsoleComponent, GetVerbsEvent<Verb>>(OnGetCargoConsoleVerbs);
        SubscribeLocalEvent<CargoOreProcessorComponent, LatheItemPrintedEvent>(OnOrePrinted);
    }

    private void OnGetCargoConsoleVerbs(Entity<CargoOrderConsoleComponent> console, ref GetVerbsEvent<Verb> args)
    {
        var requester = args.User;
        if (!args.CanAccess || !args.CanInteract || !CanUseCargoTerminal(console, requester) ||
            FindPickupOnGrid(console) is not { } pickup)
            return;

        AddRequisitionVerbs(pickup, console, requester, args.Verbs);
        if (Transform(pickup).GridUid is not { } grid || FindDepartmentPallet(grid, CargoDepartment.Export) == null)
            return;

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-cargo-export-steel"),
            Message = Loc.GetString("living-station-cargo-export-manifest", ("sheets", ExportSheets), ("reward", ExportReward)),
            Act = () =>
            {
                if (CanUseCargoTerminal(console, requester) && !TerminatingOrDeleted(pickup))
                    SubmitSteelExport(pickup, console, requester);
            },
        });

        if (FindDepartmentPallet(grid, CargoDepartment.Materials) == null)
            return;

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-cargo-export-surplus"),
            Message = Loc.GetString("living-station-cargo-export-surplus-info", ("sheets", ExportSheets), ("reward", ExportReward)),
            Act = () =>
            {
                if (CanUseCargoTerminal(console, requester) && !TerminatingOrDeleted(pickup))
                    SubmitSteelExport(pickup, console, requester, surplus: true);
            },
        });
    }

    private bool CanUseCargoTerminal(EntityUid terminal, EntityUid requester) =>
        Enabled && !TerminatingOrDeleted(terminal) && _mobState.IsAlive(requester) &&
        _blocker.CanInteract(requester, terminal) && _interaction.InRangeAndAccessible(requester, terminal) &&
        (!HasComp<CargoOrderConsoleComponent>(terminal) || _power.IsPowered(terminal) && _access.IsAllowed(requester, terminal));

    private Entity<CargoDeliveryPickupComponent>? FindPickupOnGrid(EntityUid entity)
    {
        if (Transform(entity).GridUid is not { } grid)
            return null;

        var pickups = EntityQueryEnumerator<CargoDeliveryPickupComponent, TransformComponent>();
        while (pickups.MoveNext(out var uid, out var pickup, out var transform))
        {
            if (transform.GridUid == grid && pickup.Workers.Count != 0)
                return (uid, pickup);
        }

        return null;
    }

    private static int GetRequisitionCost(EntProtoId product)
    {
        if (product == EngineeringStock || product == JanitorialStock)
            return 250;

        return RequisitionCatalog.FirstOrDefault(entry => entry.Product == product).Cost;
    }

    private int GetCargoBalance(EntityUid? station) =>
        station is { } uid && HasComp<StationBankAccountComponent>(uid) ? _cargo.GetBalanceFromAccount(uid, CargoAccount) : 0;

    private string DescribeRequisition(CargoRequisition order) =>
        Loc.GetString("living-station-cargo-queue-line", ("product", ProtoMan.Index(order.Product).Name),
            ("cost", GetRequisitionCost(order.Product)));

    internal IEnumerable<string> ReadQueuedWork()
    {
        var lines = new List<string>();
        var pickups = EntityQueryEnumerator<CargoDeliveryPickupComponent, TransformComponent>();
        while (pickups.MoveNext(out var uid, out var pickup, out var transform))
        {
            var balance = GetCargoBalance(_station.GetOwningStation(uid));
            lines.Add($"  Cargo route {uid}: balance={balance}, workers={pickup.Workers.Count}, queued={pickup.Requisitions.Count}");
            foreach (var order in pickup.Requisitions)
            {
                var reason = "waiting for dispatch";
                if (TerminatingOrDeleted(order.Requester))
                    reason = "requester unavailable";
                else if (!pickup.Workers.Any(worker => _mobState.IsAlive(worker) && !HasComp<Robust.Shared.Player.ActorComponent>(worker)))
                    reason = "no autonomous cargo staff";
                else if (GetRequisitionCost(order.Product) > balance)
                    reason = "insufficient cargo funds";
                else if (transform.GridUid is { } grid && TryComp<MapGridComponent>(grid, out var mapGrid) &&
                         !IsDropoffClear(GetDestination(grid, order.Product, pickup.Dropoff), grid, mapGrid))
                    reason = "destination occupied or reserved";
                lines.Add($"  Cargo: {order.Product}, requester={order.Requester}, cost={GetRequisitionCost(order.Product)}, balance={balance}; {reason}");
            }
        }
        return lines;
    }

    private void QueueStockroomOrders()
    {
        var stockrooms = EntityQueryEnumerator<CargoStockroomComponent>();
        while (stockrooms.MoveNext(out var uid, out var stockroom))
        {
            if (_timing.CurTime < stockroom.NextRequisition)
                continue;

            stockroom.NextRequisition = _timing.CurTime + TimeSpan.FromSeconds(15);
            if (GetRestockItems(uid, stockroom).Count == 0 || Transform(uid).GridUid is not { } grid ||
                FindDepartmentPallet(grid, stockroom.Department) == null)
                continue;

            RequestSupplies(uid, stockroom.Department == CargoDepartment.Engineering ? EngineeringStock : JanitorialStock);
        }
    }

    internal void RequestEngineeringRestock(EntityUid room) => RequestSupplies(room, EngineeringStock);

    private List<EntProtoId> GetRestockItems(EntityUid uid, CargoStockroomComponent stockroom)
    {
        var items = new List<EntProtoId>();
        if (!TryComp(uid, out EntityStorageComponent? storage) || storage.Open)
            return items;

        var contents = storage.Contents.ContainedEntities;
        switch (stockroom.Department)
        {
            case CargoDepartment.Engineering:
                if (!contents.Any(item => TryComp(item, out StackComponent? stack) && stack.StackTypeId == HullTiles && stack.Count > 0))
                    items.Add("FloorTileItemSteel");
                if (contents.Sum(item => TryComp(item, out StackComponent? stack) && stack.StackTypeId == Cable ? stack.Count : 0) < 5)
                    items.Add("CableApcStack10");
                var bulbs = contents.Count(item => MetaData(item).EntityPrototype?.ID == LightBulb.Id);
                for (var i = bulbs; i < 2; i++)
                    items.Add(LightBulb);
                var tubes = contents.Count(item => MetaData(item).EntityPrototype?.ID == LightTube.Id);
                for (var i = tubes; i < 2; i++)
                    items.Add(LightTube);
                break;
            case CargoDepartment.Janitorial:
                if (!contents.Any(item => MetaData(item).EntityPrototype?.ID == TrashBag.Id))
                    items.Add(TrashBag);
                if (contents.Sum(item => _solutions.TryGetSolution(item, "drink", out _, out var solution)
                        ? solution.GetTotalPrototypeQuantity(SpaceCleaner).Float() : 0) < 30)
                    items.Add("SprayBottleSpaceCleaner");
                break;
        }

        return items;
    }

    private void UnloadStockroomOrder(CargoRequisition order, EntityUid grid, EntityStorageComponent freight, TransformComponent pallet)
    {
        if (!HasComp<CargoStockroomComponent>(order.Requester) ||
            !TryComp(order.Requester, out EntityStorageComponent? stockroom) || stockroom.Open ||
            Transform(order.Requester).GridUid != grid ||
            !Transform(order.Requester).Coordinates.TryDistance(EntityManager, pallet.Coordinates, out var distance) || distance > 4f)
            return;

        foreach (var item in freight.Contents.ContainedEntities.ToArray())
        {
            if (_storage.CanInsert(item, order.Requester, stockroom))
                _storage.Insert(item, order.Requester, stockroom);
        }
    }

    private void SubmitSteelExport(Entity<CargoDeliveryPickupComponent> pickup, EntityUid terminal, EntityUid requester, bool surplus = false)
    {
        if (Transform(pickup).GridUid is not { } grid || Transform(terminal).GridUid != grid ||
            FindDepartmentPallet(grid, CargoDepartment.Export) is not { } pallet ||
            !TryComp(grid, out MapGridComponent? mapGrid) || _station.GetOwningStation(grid) is not { } station ||
            !HasComp<StationBankAccountComponent>(station) || !_cargo.TryGetAccount(station, CargoAccount, out _))
            return;

        if (_timing.CurTime < pickup.Comp.NextSteelExport)
        {
            _popup.PopupEntity(Loc.GetString("living-station-cargo-export-cooldown"), terminal, requester);
            return;
        }

        if (surplus)
        {
            var stock = FindRefinedStacks(grid, Steel);
            var reserved = pickup.Comp.Requisitions.Count(order => order.Product == MinedSteel) * MaterialDeliverySize;
            if (stock.Sum(stack => stack.Comp.Count) - reserved < ExportSheets)
            {
                _popup.PopupEntity(Loc.GetString("living-station-cargo-export-surplus-short"), terminal, requester);
                return;
            }

            CompleteSteelExport(pickup.Comp, terminal, requester, station, stock);
            return;
        }

        foreach (var crate in CratesOnPallet(Transform(pallet), grid, mapGrid))
        {
            if (!CanHaul(crate))
                continue;

            var contents = Comp<EntityStorageComponent>(crate).Contents.ContainedEntities;
            var stacks = new List<Entity<StackComponent>>();
            foreach (var item in contents)
            {
                if (TryComp(item, out StackComponent? stack) && stack.StackTypeId == Steel &&
                    !stack.Unlimited && !EntityManager.IsQueuedForDeletion(item))
                    stacks.Add((item, stack));
            }

            if (stacks.Count != contents.Count || stacks.Sum(stack => stack.Comp.Count) < ExportSheets)
                continue;

            CompleteSteelExport(pickup.Comp, terminal, requester, station, stacks);
            return;
        }

        _popup.PopupEntity(Loc.GetString("living-station-cargo-export-manifest", ("sheets", ExportSheets), ("reward", ExportReward)),
            terminal, requester);
    }

    private void CompleteSteelExport(CargoDeliveryPickupComponent pickup, EntityUid terminal, EntityUid requester,
        EntityUid station, List<Entity<StackComponent>> stacks)
    {
        var remaining = ExportSheets;
        foreach (var stack in stacks)
        {
            var amount = Math.Min(remaining, stack.Comp.Count);
            _stacks.TryUse(stack.AsNullable(), amount);
            remaining -= amount;
            if (remaining == 0)
                break;
        }

        pickup.NextSteelExport = _timing.CurTime + TimeSpan.FromSeconds(30);
        _cargo.TryAdjustBankAccount(station, CargoAccount, ExportReward);
        _popup.PopupEntity(Loc.GetString("living-station-cargo-export-paid", ("reward", ExportReward)), terminal, requester);
    }
}
