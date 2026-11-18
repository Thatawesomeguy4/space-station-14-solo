using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Content.Client.UserInterface.Systems.Chat;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.LivingStation;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Server.Preferences.Managers;
using Content.Server.Radio.EntitySystems;
using Content.Shared.AlertLevel;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Light.Components;
using Content.Shared.Preferences;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Station.Systems;
using Content.Shared.Verbs;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.NPC;

[TestFixture]
public sealed class BartenderDialogueTest : GameTest
{
    [SidedDependency(Side.Client)] private readonly IUserInterfaceManager _uiManager = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
        DummyTicker = false,
        Map = PoolManager.TestStation,
    };

    [Test]
    public async Task InferenceCountersIncludeSuccessfulAndUnavailableRequests()
    {
        using var transport = new OllamaBartenderClient(new EngineerDialogueHandler());
        var context = new DepartmentHeadContext("Chief Engineer", new DepartmentStatus(false, "Idle"), "Idle");
        Assert.That((await transport.AskDepartmentHeadAsync("test", "hello", context, CancellationToken.None)).Kind,
            Is.EqualTo(BartenderReplyKind.Speak));
        Assert.That((await transport.AskDepartmentHeadAsync("test", "hello", context, CancellationToken.None)).Kind,
            Is.EqualTo(BartenderReplyKind.Unavailable));
        var counters = transport.Counters;
        Assert.That(counters.Started, Is.EqualTo(2));
        Assert.That(counters.Finished, Is.EqualTo(2));
        Assert.That(counters.Failed, Is.EqualTo(1));
        Assert.That(counters.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task RepliesRememberConversationAndLeaveServiceAvailable()
    {
        var dialogue = Server.System<LivingStationBartenderDialogueSystem>();
        var chat = Server.System<ChatSystem>();
        var ticker = Server.System<ServerGameTicker>();
        var verbs = Server.System<SharedVerbSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var replies = new ConcurrentQueue<string>();
        var handler = new DialogueHandler();
        using var fakeTransport = new OllamaBartenderClient(handler);
        var originalTransport = dialogue.Transport;
        EntityUid counter = default;
        EntityUid bartender = default;
        EntityUid customer = default;
        EntityUid water = default;
        NetEntity bartenderNet = default;

        await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded += OnChat);

        try
        {
            await Server.WaitAssertion(() =>
            {
                dialogue.Transport = fakeTransport;
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, true);
                ticker.RestartRound();

                var counters = SEntMan.EntityQueryEnumerator<BarServiceCounterComponent>();
                Assert.That(counters.MoveNext(out counter, out var bar), Is.True);
                bartender = bar!.Bartender!.Value;
                bartenderNet = SEntMan.GetNetEntity(bartender);
                customer = SEntMan.SpawnAttachedTo("MobHuman", SComp<TransformComponent>(counter).Coordinates.Offset(-bar.StaffOffset));
                Assert.That(Server.PlayerMan.SetAttachedEntity(ServerSession, customer, force: true), Is.True);

                Say("Bartender, my ship is called Blue Comet.");
            });

            await WaitForReply(1);
            await Pair.RunTicksSync(65);
            await Server.WaitAssertion(() =>
            {
                Assert.That(replies.ToArray()[0], Does.Contain("Blue Comet"));
                Say("Bartender, what did I say my ship was called?");
            });

            await WaitForReply(2);
            await Pair.RunTicksSync(65);
            await Server.WaitAssertion(() =>
            {
                Assert.That(replies.ToArray()[1], Does.Contain("Blue Comet"));
                Say("Bartender, give me a moment.");
            });

            await WaitForReply(3);
            await Pair.RunTicksSync(65);
            await Server.WaitAssertion(() =>
            {
                Assert.That(replies.ToArray()[2], Does.Contain("request water"));
                Say("Bartender, are you open?");
            });

            await WaitForReply(4);
            await Pair.RunTicksSync(65);
            await Server.WaitAssertion(() =>
            {
                Assert.That(replies.ToArray()[3], Does.Contain("request water"));
                Say("Bartender, are you there?");
            });

            await handler.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Server.WaitAssertion(() =>
            {
                var request = verbs.GetLocalVerbs(counter, customer, typeof(Verb)).Single(verb => verb.Text == "Request water");
                verbs.ExecuteVerb(request, customer, counter);
                water = SComp<BarServiceOrderComponent>(bartender).Drink;
            });
            await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<BarServiceOrderComponent>(bartender));
            await Server.WaitAssertion(() =>
            {
                Assert.That(transform.GetGridTilePositionOrDefault(water), Is.EqualTo(transform.GetGridTilePositionOrDefault(counter)));
                Assert.That(replies, Has.Count.EqualTo(4));
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, false);
            });
            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Pair.RunTicksSync(2);
            Assert.That(replies, Has.Count.EqualTo(4));
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, false);
                dialogue.Transport = originalTransport;
            });
            await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnChat);
        }

        void Say(string message) => chat.TrySendInGameICMessage(customer, message, InGameICChatType.Speak,
            ChatTransmitRange.Normal, hideLog: true);

        void OnChat(ChatMessage message)
        {
            if (message.Channel == ChatChannel.Local && message.SenderEntity == bartenderNet)
                replies.Enqueue(message.Message);
        }

        async Task WaitForReply(int count)
        {
            for (var i = 0; i < 300 && replies.Count < count; i++)
                await Pair.RunTicksSync(2);

            Assert.That(replies, Has.Count.EqualTo(count));
        }
    }

    [Test]
    public async Task ShipNoteSurvivesRoundRestartAndFollowsTheSpawnedCharacter()
    {
        const string recall = "Bartender, what did I say my ship was called?";
        var dialogue = Server.System<LivingStationBartenderDialogueSystem>();
        var ticker = Server.System<ServerGameTicker>();
        var chat = Server.System<ChatSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var preferences = Server.ResolveDependency<IServerPreferencesManager>();
        var user = ServerSession.UserId;
        var replies = new ConcurrentQueue<string>();
        var folder = Directory.CreateTempSubdirectory("ss14-bartender-");
        var path = Path.Combine(folder.FullName, LivingStationMemoryStore.FileName);
        var originalMemory = dialogue.Memory;
        EntityUid customer = default;
        NetEntity bartenderNet = default;

        await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded += OnChat);
        try
        {
            await Server.WaitAssertion(() =>
            {
                var firstSlot = preferences.GetPreferences(user).SelectedCharacterIndex;
                var secondSlot = firstSlot == 0 ? 1 : 0;
                // Keep the test phrases intact across randomized integration-test profiles.
                var first = HumanoidCharacterProfile.DefaultWithSpecies("Human");
                preferences.SetProfile(user, firstSlot, first).GetAwaiter().GetResult();
                dialogue.Memory = new LivingStationMemoryStore(path);
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, true);
                RestartAndJoin();
                Say("Bartender, my ship is called Blue Comet.");

                // Two profiles can have the same name. Changing the lobby selection also leaves the
                // character already at the counter bound to its original profile.
                preferences.SetProfile(user, secondSlot, first.WithName(first.Name)).GetAwaiter().GetResult();
            });
            await AssertReply("I'll remember you said your ship is called", "Blue Comet");

            await Server.WaitAssertion(() => Say(recall));
            await AssertReply("You told me your ship is called", "Blue Comet");

            await Server.WaitAssertion(() =>
            {
                dialogue.Memory = new LivingStationMemoryStore(path);
                RestartAndJoin();
                Say(recall);
            });
            await AssertReply("I don't remember you telling me your ship's name.");

            await Server.WaitAssertion(() => Say("Bartender, my ship is called Red Dwarf."));
            await AssertReply("I'll remember you said your ship is called", "Red Dwarf");

            await Server.WaitAssertion(() =>
            {
                var prefs = preferences.GetPreferences(user);
                var otherSlot = prefs.SelectedCharacterIndex == 0 ? 1 : 0;
                // Saving a rename updates the existing profile row and preserves its note.
                preferences.SetProfile(user, otherSlot, prefs.Characters[otherSlot].WithName("Pat Morgan")).GetAwaiter().GetResult();
                dialogue.Memory = new LivingStationMemoryStore(path);
                RestartAndJoin();
                Say(recall);
            });
            await AssertReply("You told me your ship is called", "Blue Comet");
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, false);
                dialogue.Memory = originalMemory;
            });
            await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnChat);
            File.Delete(path);
            folder.Delete();
        }

        void RestartAndJoin()
        {
            ticker.RestartRound();
            var counters = SEntMan.EntityQueryEnumerator<BarServiceCounterComponent>();
            Assert.That(counters.MoveNext(out var counter, out var bar), Is.True);
            bartenderNet = SEntMan.GetNetEntity(bar!.Bartender!.Value);

            ticker.MakeJoinGame(ServerSession, EntityUid.Invalid, "Passenger", silent: true);
            customer = ServerSession.AttachedEntity!.Value;
            Assert.That(SEntMan.HasComponent<BartenderCustomerComponent>(customer), Is.True);
            transform.SetCoordinates(customer, SComp<TransformComponent>(counter).Coordinates.Offset(-bar.StaffOffset));
        }

        void Say(string message) => chat.TrySendInGameICMessage(customer, message, InGameICChatType.Speak,
            ChatTransmitRange.Normal, hideLog: true);

        void OnChat(ChatMessage message)
        {
            if (message.Channel == ChatChannel.Local && message.SenderEntity == bartenderNet)
                replies.Enqueue(message.Message);
        }

        async Task AssertReply(params string[] expected)
        {
            for (var i = 0; i < 300 && replies.IsEmpty; i++)
                await Pair.RunTicksSync(2);

            Assert.That(replies.TryDequeue(out var reply), Is.True, $"Expected bartender speech containing {string.Join(", ", expected)}.");
            foreach (var text in expected)
                Assert.That(reply, Does.Contain(text));
            await Pair.RunTicksSync(65);
        }
    }

    [TestCase("quiet", 12)]
    [TestCase("chaotic", 5)]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task DirectorPacingControlsIncidentStarts(string profile, int minutesBetweenStarts)
    {
        var director = Server.System<LivingStationStoryDirectorSystem>();
        var engineering = Server.System<LivingStationEngineeringSystem>();
        var ticker = Server.System<ServerGameTicker>();
        var timing = Server.ResolveDependency<IGameTiming>();
        var interval = TimeSpan.FromMinutes(minutesBetweenStarts);
        TimeSpan firstStart = default;
        EntityUid fixture = default;

        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            Server.CfgMan.SetCVar(CCVars.LivingStationDirectorEnabled, true);
            Server.CfgMan.SetCVar(CCVars.LivingStationDirectorPacing, profile);
            Server.CfgMan.SetCVar(CCVars.LivingStationCargoStaff, 0);
            ticker.RestartRound();
            fixture = engineering.Fixture!.Value;
            firstStart = timing.CurTime + LivingStationStoryDirectorSystem.Warmup;
            director.Advance(firstStart);
            Assert.That(director.MaintenanceActive, Is.True);
        });

        await PoolManager.WaitUntil(Server, () => SComp<PoweredLightComponent>(fixture).CurrentLit,
            maxTicks: 800, tickStep: 10);

        await Server.WaitAssertion(() =>
        {
            var rumorStart = firstStart + interval;
            director.Advance(rumorStart - TimeSpan.FromSeconds(1));
            Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
            director.Advance(rumorStart);
            Assert.That(director.RumorActive, Is.True);

            if (profile == "quiet")
            {
                director.Advance(rumorStart + TimeSpan.FromMinutes(2));
                Assert.That(director.RumorActive, Is.False);
                director.Advance(rumorStart + interval - TimeSpan.FromSeconds(1));
                Assert.That(director.MaintenanceActive, Is.False);
                director.Advance(rumorStart + interval);
                Assert.That(director.MaintenanceActive, Is.True);
                return;
            }

            var nextRepair = rumorStart + interval;
            var station = Server.System<StationSystem>().GetOwningStation(fixture)!.Value;
            var alerts = Server.System<AlertLevelSystem>();
            alerts.SetLevel(station, "Red", playSound: false, announce: false, force: true);
            director.Advance(nextRepair);
            Assert.That(director.MaintenanceActive, Is.False);
            Assert.That(director.RumorActive, Is.True);
            alerts.SetLevel(station, "Green", playSound: false, announce: false, force: true);

            nextRepair += TimeSpan.FromSeconds(30);
            director.Advance(nextRepair);
            Assert.That(director.MaintenanceActive, Is.True);
            Assert.That(director.RumorActive, Is.True);
            director.Advance(nextRepair + LivingStationEngineeringSystem.RepairTimeout);
            Assert.That(director.MaintenanceActive, Is.False);
            Assert.That(director.RumorActive, Is.True);
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(engineering.ChiefEngineer!.Value), Is.False);
            director.Advance(rumorStart + TimeSpan.FromMinutes(6));
            Assert.That(director.RumorActive, Is.False);
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task CommandStaffRememberTheLightWithDifferentAttribution()
    {
        var incidents = Server.System<LivingStationIncidentSystem>();
        var director = Server.System<LivingStationStoryDirectorSystem>();
        var timing = Server.ResolveDependency<IGameTiming>();
        var ticker = Server.System<ServerGameTicker>();
        var transform = Server.System<SharedTransformSystem>();
        var chat = Server.System<ChatSystem>();
        var radio = Server.System<RadioSystem>();
        var replies = new ConcurrentQueue<ChatMessage>();
        var folder = Directory.CreateTempSubdirectory("ss14-incident-");
        var path = Path.Combine(folder.FullName, LivingStationMemoryStore.FileName);
        EntityUid engineer = default;
        EntityUid security = default;
        EntityUid fixture = default;
        EntityUid customer = default;
        TimeSpan incidentStart = default;

        await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded += OnChat);
        try
        {
            await Server.WaitAssertion(() =>
            {
                incidents.Memory = new LivingStationMemoryStore(path);
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                Server.CfgMan.SetCVar(CCVars.LivingStationDirectorEnabled, true);
                Restart();
                var headset = SComp<WearingHeadsetComponent>(engineer).Headset;
                radio.SendRadioMessage(customer, Loc.GetString("living-station-incident-radio"),
                    SProtoMan.Index<RadioChannelPrototype>("Command"), headset);
                Assert.That(incidents.Notes.ReportedAt, Is.Null);
            });
            await PoolManager.WaitUntil(Server, () => SComp<PoweredLightComponent>(fixture).CurrentLit, maxTicks: 800, tickStep: 10);

            Task writes = Task.CompletedTask;
            LightIncidentNotes saved = default;
            await Server.WaitAssertion(() =>
            {
                writes = incidents.Writes;
                saved = incidents.Notes;
                Assert.That(saved.ObservedAt, Is.Not.Null, "The Chief Engineer should observe the failed bulb.");
                foreach (var npc in new[] { engineer, security })
                {
                    Assert.That(SEntMan.TryGetComponent<WearingHeadsetComponent>(npc, out var worn), Is.True, $"Headset on {npc}");
                    Assert.That(SComp<EncryptionKeyHolderComponent>(worn!.Headset).Channels.Any(channel => channel.Id == "Command"), Is.True,
                        $"Command channel on {npc}");
                }
                Assert.That(saved.ObservedAt, Is.EqualTo(saved.ReportedAt));
                var rumorStart = incidentStart + LivingStationStoryDirectorSystem.StartInterval;
                director.Advance(rumorStart - TimeSpan.FromSeconds(1));
                Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
                director.Advance(rumorStart);
                Assert.That(director.RumorActive, Is.True);
            });
            await writes.WaitAsync(TimeSpan.FromSeconds(5));

            await Ask(security, "Head of Security, what have you heard?", "The Chief Engineer relayed an unconfirmed report");
            await Ask(engineer, "Chief Engineer, can you check the light?", "The fixture is lit now");
            await Ask(security, "Head of Security, what have you heard?", "The Chief Engineer checked the fixture");
            await Server.WaitAssertion(() =>
            {
                var rumorStart = incidentStart + LivingStationStoryDirectorSystem.StartInterval;
                director.Advance(rumorStart + TimeSpan.FromSeconds(1));
                Assert.That(director.RumorActive, Is.False);
                director.Advance(rumorStart + LivingStationStoryDirectorSystem.RumorLifetime);
            });
            await Ask(security, "Head of Security, what have you heard?", "I have no active report");

            await Server.WaitAssertion(() =>
            {
                incidents.Memory = new LivingStationMemoryStore(path);
                Restart(checkAlert: true);
                // Leave the new round's fixture broken so all recalled notes must come from disk.
                SEntMan.RemoveComponent<EngineeringRepairComponent>(engineer);
                Assert.That(SComp<PoweredLightComponent>(fixture).CurrentLit, Is.False);
            });
            await PoolManager.WaitUntil(Server, () => incidents.Notes.ReportedAt != null);
            await Server.WaitAssertion(() => Assert.That(incidents.Notes, Is.EqualTo(saved)));

            await Ask(engineer, "Chief Engineer, what happened?", "I saw a broken light");
            await Ask(security, "Head of Security, what happened?", "The Chief Engineer reported a broken light");
            await Ask(security, "Head of Security, who broke it?", "I don't know who or what broke it");
            await Ask(security, "Head of Security, is the light fixed?", "The fixture still needs a working bulb");
            await Server.WaitAssertion(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationDirectorEnabled, false);
                director.Advance(incidentStart + LivingStationEngineeringSystem.RepairTimeout);
                Assert.That(director.MaintenanceActive, Is.False);
                Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(engineer), Is.False);
                Assert.That(SComp<PoweredLightComponent>(fixture).CurrentLit, Is.False);
                director.Advance(incidentStart + LivingStationStoryDirectorSystem.StartInterval);
                Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
            });
        }
        finally
        {
            await Server.WaitPost(() => incidents.Memory = null);
            await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnChat);
            File.Delete(path);
            folder.Delete();
        }

        void Restart(bool checkAlert = false)
        {
            ticker.RestartRound();
            incidentStart = timing.CurTime + LivingStationStoryDirectorSystem.Warmup;
            var engineering = Server.System<LivingStationEngineeringSystem>();
            Assert.That(engineering.ChiefEngineer, Is.Not.Null);
            Assert.That(SEntMan.HasComponent<EngineeringRepairComponent>(engineering.ChiefEngineer!.Value), Is.False);
            director.Advance(incidentStart - TimeSpan.FromSeconds(1));
            Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
            if (checkAlert)
            {
                var station = Server.System<StationSystem>().GetOwningStation(engineering.Fixture!.Value)!.Value;
                var alerts = Server.System<AlertLevelSystem>();
                alerts.SetLevel(station, "Red", playSound: false, announce: false, force: true);
                director.Advance(incidentStart);
                Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
                Assert.That(SEntMan.HasComponent<EngineeringRepairComponent>(engineering.ChiefEngineer!.Value), Is.False);
                alerts.SetLevel(station, "Green", playSound: false, announce: false, force: true);
                incidentStart += TimeSpan.FromSeconds(30);
            }
            director.Advance(incidentStart);
            Assert.That(director.MaintenanceActive, Is.True);
            var repairs = SEntMan.EntityQueryEnumerator<EngineeringRepairComponent, LivingStationWorkClaimComponent>();
            Assert.That(repairs.MoveNext(out engineer, out _, out var claim), Is.True);
            fixture = claim!.Target.EntityId;
            var officers = SEntMan.EntityQueryEnumerator<SecurityPatrolComponent>();
            Assert.That(officers.MoveNext(out security, out _), Is.True);
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(engineer).EntityPrototype!.ID, Is.EqualTo("MobLivingStationChiefEngineer"));
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(security).EntityPrototype!.ID, Is.EqualTo("MobLivingStationHeadOfSecurity"));
            customer = SEntMan.SpawnAttachedTo("MobHuman", SComp<TransformComponent>(security).Coordinates);
            Assert.That(Server.PlayerMan.SetAttachedEntity(ServerSession, customer, force: true), Is.True);
        }

        async Task Ask(EntityUid speaker, string question, string expected)
        {
            NetEntity sender = default;
            await Server.WaitAssertion(() =>
            {
                sender = SEntMan.GetNetEntity(speaker);
                transform.SetCoordinates(customer, SComp<TransformComponent>(speaker).Coordinates);
                chat.TrySendInGameICMessage(customer, question, InGameICChatType.Speak, ChatTransmitRange.Normal,
                    hideLog: true, checkRadioPrefix: false);
            });
            await Pair.RunTicksSync(65);
            Assert.That(replies.Any(reply => reply.SenderEntity == sender && reply.Message.Contains(expected)), Is.True,
                $"Expected '{expected}', got: {string.Join(", ", replies.Where(reply => reply.SenderEntity == sender).Select(reply => reply.Message))}");
            replies.Clear();
        }

        void OnChat(ChatMessage message)
        {
            if (message.Channel == ChatChannel.Local)
                replies.Enqueue(message);
        }
    }

    [Test]
    public async Task ChiefEngineerUsesLiveFactsAndFallsBackWhenOllamaFails()
    {
        var dialogue = Server.System<LivingStationBartenderDialogueSystem>();
        var engineering = Server.System<LivingStationEngineeringSystem>();
        var ticker = Server.System<ServerGameTicker>();
        var chat = Server.System<ChatSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var replies = new ConcurrentQueue<string>();
        var handler = new EngineerDialogueHandler();
        var originalTransport = dialogue.Transport;
        using var fakeTransport = new OllamaBartenderClient(handler);
        EntityUid customer = default;
        NetEntity engineerNet = default;

        await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded += OnChat);
        try
        {
            await Server.WaitAssertion(() =>
            {
                dialogue.Transport = fakeTransport;
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, true);
                ticker.RestartRound();
                var engineer = engineering.ChiefEngineer!.Value;
                engineerNet = SEntMan.GetNetEntity(engineer);
                customer = SEntMan.SpawnAttachedTo("MobHuman", SComp<TransformComponent>(engineer).Coordinates);
                Assert.That(Server.PlayerMan.SetAttachedEntity(ServerSession, customer, force: true), Is.True);
                Say("Chief Engineer, what are you working on?");
            });
            await AssertReply("I don't have an active work order.");
            Assert.That(handler.Requests, Is.Zero);

            await Server.WaitAssertion(() => Say("Chief Engineer, how is your shift going?"));
            await AssertReply("I'm between assignments.");
            Assert.That(handler.Prompt, Does.Contain("You are the Chief Engineer"));
            Assert.That(handler.Context!.Value.GetProperty("facts").GetProperty("WorkAssigned").GetBoolean(), Is.False);
            Assert.That(handler.Context.Value.GetProperty("facts").GetProperty("FixtureStatus").GetString(), Is.Not.Empty);
            Assert.That(handler.Context.Value.TryGetProperty("history", out _), Is.False);

            await Server.WaitAssertion(() => Say("Chief Engineer, how is your shift going?"));
            await AssertReply("I don't have an active work order.");

            await Server.WaitAssertion(() => Say("Chief Engineer, is the light fixed?"));
            await AssertReply("fixture");
            Assert.That(handler.Requests, Is.EqualTo(2));

            foreach (var (title, prototype) in new[]
            {
                ("Head of Security", "MobLivingStationHeadOfSecurity"),
                ("Quartermaster", "MobLivingStationQuartermaster"),
                ("Chief Medical Officer", "MobLivingStationChiefMedicalOfficer"),
                ("Head of Personnel", "MobLivingStationHeadOfPersonnel"),
                ("Research Director", "MobLivingStationResearchDirector"),
            })
            {
                var before = handler.Requests;
                await Server.WaitAssertion(() =>
                {
                    var entities = SEntMan.EntityQueryEnumerator<MetaDataComponent>();
                    EntityUid head = default;
                    while (entities.MoveNext(out var uid, out var metadata))
                    {
                        if (metadata.EntityPrototype?.ID != prototype)
                            continue;

                        head = uid;
                        break;
                    }

                    Assert.That(head.IsValid(), Is.True, $"Expected {title} to spawn.");
                    engineerNet = SEntMan.GetNetEntity(head);
                    transform.SetCoordinates(customer, SComp<TransformComponent>(head).Coordinates);
                    Say($"{title}, how is your shift going?");
                });
                for (var i = 0; i < 300 && replies.IsEmpty; i++)
                    await Pair.RunTicksSync(2);
                Assert.That(replies.TryDequeue(out var reply), Is.True, title);
                Assert.That(reply, Is.Not.Empty);
                Assert.That(handler.Requests, Is.EqualTo(before + 1));
                Assert.That(handler.Prompt, Does.Contain($"You are the {title}"));
                Assert.That(handler.Context!.Value.GetProperty("facts").GetProperty("Status").GetString(), Is.Not.Empty);
                await Pair.RunTicksSync(65);
            }
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, false);
                dialogue.Transport = originalTransport;
            });
            await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnChat);
        }

        void OnChat(ChatMessage message)
        {
            if (message.Channel == ChatChannel.Local && message.SenderEntity == engineerNet)
                replies.Enqueue(message.Message);
        }

        void Say(string message) => chat.TrySendInGameICMessage(customer, message, InGameICChatType.Speak,
            ChatTransmitRange.Normal, hideLog: true, checkRadioPrefix: false);

        async Task AssertReply(string expected)
        {
            for (var i = 0; i < 300 && replies.IsEmpty; i++)
                await Pair.RunTicksSync(2);

            Assert.That(replies.TryDequeue(out var reply), Is.True, $"Expected Chief Engineer speech containing {expected}.");
            Assert.That(reply, Does.Contain(expected));
            await Pair.RunTicksSync(65);
        }
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task EngineerAnnouncesRequestedRepairWhenCustomerReturns()
    {
        var ticker = Server.System<ServerGameTicker>();
        var engineering = Server.System<LivingStationEngineeringSystem>();
        var transforms = Server.System<SharedTransformSystem>();
        var verbs = Server.System<SharedVerbSystem>();
        var lights = Server.System<Content.Shared.Light.EntitySystems.SharedPoweredLightSystem>();
        var replies = new ConcurrentQueue<string>();
        EntityUid worker = default;
        EntityUid customer = default;
        EntityUid fixture = default;
        NetEntity workerNet = default;
        await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded += OnChat);
        try
        {
            await Server.WaitAssertion(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                ticker.RestartRound();
                worker = engineering.ChiefEngineer!.Value;
                workerNet = SEntMan.GetNetEntity(worker);
                var origin = SComp<TransformComponent>(worker).Coordinates;
                fixture = SEntMan.SpawnAttachedTo("PoweredSmallLightEmpty", origin.Offset(new System.Numerics.Vector2(-1, -1)));
                customer = SEntMan.SpawnAttachedTo("MobHuman", origin);
                Assert.That(Server.PlayerMan.SetAttachedEntity(ServerSession, customer, force: true), Is.True);
                var request = verbs.GetLocalVerbs(fixture, customer, typeof(Verb))
                    .Single(verb => verb.Text == "Request Engineering repair");
                verbs.ExecuteVerb(request, customer, fixture);
                transforms.SetCoordinates(customer, origin.Offset(new System.Numerics.Vector2(12, 0)));
            });

            await PoolManager.WaitUntil(Server, () => lights.GetBulb(fixture) != null &&
                !SEntMan.HasComponent<EngineeringRepairComponent>(worker), maxTicks: 1200, tickStep: 10);
            await Pair.RunTicksSync(5);
            Assert.That(replies, Is.Empty);
            await Server.WaitPost(() =>
                transforms.SetCoordinates(customer, SComp<TransformComponent>(worker).Coordinates));

            for (var i = 0; i < 150 && replies.IsEmpty; i++)
                await Pair.RunTicksSync(2);

            Assert.That(replies, Has.Count.EqualTo(1));
            Assert.That(replies.Single(), Does.Contain("An update on the repair you requested."));
            Assert.That(replies.Single(), Does.Contain("no further repair needed"));
            await Pair.RunTicksSync(65);
            Assert.That(replies, Has.Count.EqualTo(1));
        }
        finally
        {
            await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnChat);
        }

        void OnChat(ChatMessage message)
        {
            if (message.Channel == ChatChannel.Local && message.SenderEntity == workerNet)
                replies.Enqueue(message.Message);
        }
    }

    [TestCase("none")]
    [TestCase("work")]
    [TestCase("player")]
    [TestCase("working")]
    [TestCase("reassigned")]
    public async Task IdleNpcsConverseBrieflyAndYieldToInterruptions(string interruption)
    {
        var dialogue = Server.System<LivingStationBartenderDialogueSystem>();
        var ticker = Server.System<ServerGameTicker>();
        var chat = Server.System<ChatSystem>();
        var work = Server.System<LivingStationWorkSystem>();
        var working = interruption is "working" or "reassigned";
        var handler = new NpcConversationHandler(interruption is "work" or "player" or "reassigned");
        using var transport = new OllamaBartenderClient(handler);
        var original = dialogue.Transport;
        var speech = new ConcurrentQueue<ChatMessage>();
        EntityUid first = default;
        EntityUid second = default;
        EntityUid customer = default;
        NetEntity firstNet = default;
        NetEntity secondNet = default;
        LivingStationWorkClaimComponent? firstClaim = null;
        LivingStationWorkClaimComponent? secondClaim = null;
        NPCSteeringComponent? firstRoute = null;
        await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded += OnChat);
        try
        {
            await Server.WaitAssertion(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, true);
                Server.CfgMan.SetCVar(CCVars.LivingStationNpcConversationsEnabled, true);
                dialogue.Transport = transport;
                ticker.RestartRound();
                var bars = SEntMan.EntityQueryEnumerator<BarServiceCounterComponent, TransformComponent>();
                Assert.That(bars.MoveNext(out _, out var bar, out var location), Is.True);
                var origin = location!.Coordinates.Offset(-bar!.StaffOffset);
                first = SEntMan.SpawnAttachedTo("MobLivingStationCargoTechnician", origin);
                second = SEntMan.SpawnAttachedTo("MobLivingStationEngineer", origin.Offset(System.Numerics.Vector2.UnitX));
                firstNet = SEntMan.GetNetEntity(first);
                secondNet = SEntMan.GetNetEntity(second);
                dialogue.RegisterConversationPartner(first, LivingStationDepartment.Cargo);
                dialogue.RegisterConversationPartner(second, LivingStationDepartment.Engineering);
                SComp<LivingStationConversationPartnerComponent>(first).NextConversation = TimeSpan.Zero;
                SComp<LivingStationConversationPartnerComponent>(second).NextConversation = TimeSpan.Zero;
                if (working)
                {
                    var firstTarget = SEntMan.SpawnAttachedTo("CrateGenericSteel", origin.Offset(-System.Numerics.Vector2.UnitX));
                    var secondTarget = SEntMan.SpawnAttachedTo("PoweredSmallLightEmpty", origin.Offset(System.Numerics.Vector2.UnitY * 2));
                    Assert.That(work.TryAssign(first, new(firstTarget, System.Numerics.Vector2.Zero), TimeSpan.FromMinutes(1)), Is.True);
                    Assert.That(work.TryAssign(second, new(secondTarget, System.Numerics.Vector2.Zero), TimeSpan.FromMinutes(1)), Is.True);
                    firstClaim = SComp<LivingStationWorkClaimComponent>(first);
                    secondClaim = SComp<LivingStationWorkClaimComponent>(second);
                    firstRoute = Server.System<NPCSteeringSystem>().Register(first, origin.Offset(System.Numerics.Vector2.UnitY * 5));
                }
                customer = SEntMan.SpawnAttachedTo("MobHuman", origin.Offset(System.Numerics.Vector2.UnitY));
                Assert.That(Server.PlayerMan.SetAttachedEntity(ServerSession, customer, force: true), Is.True);
            });

            for (var i = 0; i < 150 && !handler.Started.Task.IsCompleted; i++)
                await Pair.RunTicksSync(2);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            if (interruption is "work" or "player" or "reassigned")
            {
                await Server.WaitAssertion(() =>
                {
                    if (interruption == "work")
                        Assert.That(work.TryAssign(first, SComp<TransformComponent>(first).Coordinates, TimeSpan.FromSeconds(10)), Is.True);
                    else if (interruption == "reassigned")
                    {
                        work.Release(first);
                        Assert.That(work.TryAssign(first, SComp<TransformComponent>(first).Coordinates, TimeSpan.FromSeconds(10)), Is.True);
                        Assert.That(SComp<LivingStationWorkClaimComponent>(second), Is.SameAs(secondClaim));
                    }
                    else
                        chat.TrySendInGameICMessage(customer, "Hello, got a moment?", InGameICChatType.Speak,
                            ChatTransmitRange.Normal, hideLog: true, checkRadioPrefix: false);
                });
                await Pair.RunTicksSync(2);
                await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Pair.RunTicksSync(65);
                Assert.That(speech, Is.Empty);
                Assert.That(handler.Requests, Is.EqualTo(1));
                return;
            }

            for (var i = 0; i < 300 && speech.Count < 2; i++)
                await Pair.RunTicksSync(2);
            Assert.That(speech, Has.Count.EqualTo(2));
            var lines = speech.ToArray();
            Assert.That(lines[0].SenderEntity, Is.Not.EqualTo(lines[1].SenderEntity));
            Assert.That(lines[0].Message, Does.Contain(working ? "handling my current assignment" : "A quiet moment for a break"));
            Assert.That(lines[1].Message, Does.Contain(working ? "keep working on mine" : "Back to our rounds soon"));
            Assert.That(handler.PreviousLine, Is.EqualTo(lines[0].Message));
            if (working)
            {
                foreach (var facts in handler.Facts)
                {
                    Assert.That(facts.GetProperty("WorkAssigned").GetBoolean(), Is.True);
                    Assert.That(facts.GetProperty("WorkTarget").GetString(), Is.Not.Empty);
                    Assert.That(facts.GetProperty("WorkStage").GetString(), Is.Not.Empty);
                }
                await Server.WaitAssertion(() =>
                {
                    Assert.That(SComp<LivingStationWorkClaimComponent>(first), Is.SameAs(firstClaim));
                    Assert.That(SComp<LivingStationWorkClaimComponent>(second), Is.SameAs(secondClaim));
                    Assert.That(SComp<NPCSteeringComponent>(first), Is.SameAs(firstRoute));
                });
            }
            await Pair.RunTicksSync(65);
            Assert.That(speech, Has.Count.EqualTo(2));
            Assert.That(handler.Requests, Is.EqualTo(2));
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, false);
                dialogue.Transport = original;
            });
            await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnChat);
        }

        void OnChat(ChatMessage message)
        {
            if (message.Channel == ChatChannel.Local && (message.SenderEntity == firstNet || message.SenderEntity == secondNet))
                speech.Enqueue(message);
        }
    }

    private sealed class NpcConversationHandler(bool stall) : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests;
        internal string? PreviousLine;
        internal readonly ConcurrentQueue<JsonElement> Facts = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref Requests);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            using var context = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            PreviousLine = context.RootElement.GetProperty("previousLine").GetString();
            var facts = context.RootElement.GetProperty("facts");
            Facts.Enqueue(facts.Clone());
            Started.TrySetResult();
            if (stall)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    Cancelled.TrySetResult();
                }
            }

            var working = facts.GetProperty("WorkAssigned").GetBoolean();
            var text = (count, working) switch
            {
                (1, true) => "I'm handling my current assignment.",
                (_, true) => "I'll keep working on mine.",
                (1, false) => "A quiet moment for a break.",
                _ => "Back to our rounds soon.",
            };
            var content = JsonSerializer.Serialize(new { version = 1, intent = "Speak", text });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { message = new { content } })),
            };
        }
    }

    private sealed class EngineerDialogueHandler : HttpMessageHandler
    {
        internal int Requests;
        internal string? Prompt;
        internal JsonElement? Context;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Requests) == 2)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var messages = body.RootElement.GetProperty("messages");
            Prompt = messages[0].GetProperty("content").GetString();
            using var context = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Context = context.RootElement.Clone();
            const string content = """{"version":1,"intent":"Speak","text":"I'm between assignments."}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { message = new { content } })),
            };
        }
    }

    private sealed class DialogueHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _requests);
            if (count == 4)
                throw new HttpRequestException("Test endpoint unavailable");

            if (count == 5)
            {
                Stalled.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    Cancelled.TrySetResult();
                }
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var userMessage = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var context = JsonDocument.Parse(userMessage);
            var history = context.RootElement.GetProperty("history");
            var content = count switch
            {
                1 => """{"version":1,"intent":"Speak","text":"I'll remember the Blue Comet."}""",
                2 when history.GetArrayLength() == 1 && history[0].GetProperty("Customer").GetString()!.Contains("Blue Comet") =>
                    """{"version":1,"intent":"Speak","text":"You said Blue Comet."}""",
                _ => """{"version":1,"intent":"RunCommand","text":"Invalid"}""",
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { message = new { content } })),
            };
        }
    }
}
