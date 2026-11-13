using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Fluids.EntitySystems;
using Content.Server.Gravity;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators;
using Content.Shared.CCVar;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Fluids.Components;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Storage;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.NPC;

[TestFixture]
public sealed class JanitorTest : GameTest
{
    public override PoolSettings PoolSettings => new();

    [TestCase(false)]
    [TestCase(true)]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task JanitorMopsAndBagsLitter(bool withLitter)
    {
        var map = await Pair.CreateTestMap();
        var mapSystem = Server.System<SharedMapSystem>();
        var puddles = Server.System<PuddleSystem>();
        var solutions = Server.System<SharedSolutionContainerSystem>();
        var hands = Server.System<SharedHandsSystem>();
        EntityUid janitor = default;
        EntityUid spill = default;
        EntityUid litter = default;
        EntityUid tool = default;
        StorageComponent bag = null;
        var spillLocation = new EntityCoordinates(map.Grid, 3.5f, 1.5f);

        await Server.WaitAssertion(() =>
        {
            SEntMan.EnsureComponent<GravityComponent>(map.Grid.Owner);
            Server.System<GravitySystem>().EnableGravity(map.Grid.Owner);

            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 3; y++)
                    mapSystem.SetTile(map.Grid, new Vector2i(x, y), map.Tile.Tile);
            }

            janitor = SEntMan.SpawnAttachedTo("MobLivingStationJanitor", new EntityCoordinates(map.Grid, 0.5f, 1.5f));
            Assert.That(hands.GetActiveItem(janitor), Is.Not.Null);

            Assert.That(puddles.TrySpillAt(spillLocation, new Solution("Blood", 10), out spill), Is.True);

            if (withLitter)
            {
                bag = hands.EnumerateHeld(janitor)
                    .Select(item => SEntMan.TryGetComponent<StorageComponent>(item, out var storage) ? storage : null)
                    .Single(storage => storage != null);
                litter = SEntMan.SpawnAttachedTo("FoodPacketBoritosTrash", new EntityCoordinates(map.Grid, 4.5f, 1.5f));
                tool = SEntMan.SpawnAttachedTo("Wrench", new EntityCoordinates(map.Grid, 3.5f, 0.5f));
            }
        });

        await PoolManager.WaitUntil(Server,
            () => !SEntMan.TryGetComponent<PuddleComponent>(spill, out var puddle) ||
                  solutions.TryGetSolution(spill, puddle.SolutionName, out _, out var remaining) &&
                  puddles.CanFullyEvaporate(remaining),
            tickStep: 10);

        if (withLitter)
        {
            await PoolManager.WaitUntil(Server, () => bag.Container.Contains(litter), tickStep: 10);
            await Server.WaitAssertion(() =>
            {
                Assert.That(bag.Container.ContainedEntities, Is.EquivalentTo(new[] { litter }));
                Assert.That(SEntMan.GetComponent<TransformComponent>(tool).ParentUid, Is.EqualTo(map.Grid.Owner));
            });
        }

        await PoolManager.WaitUntil(Server,
            () => SEntMan.GetComponent<HTNComponent>(janitor).Plan?.CurrentOperator is WaitOperator);
    }
}
