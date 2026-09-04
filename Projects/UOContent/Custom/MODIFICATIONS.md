# Upstream modifications

Per `SHARD.md`, all custom content lives under `Projects/UOContent/Custom/` (namespace
`Server.Custom`). This file logs any change that had to be made to an upstream file because it
genuinely could not be done any other way.

## Modifications

### `Projects/UOContent/Systems/JailSystem/JailSystem.cs` — jail release safety net on login

**What changed.** Purely additive: two `using` directives (`ModernUO.CodeGeneratedEvents`,
`Server.Regions`) and one new method, `CheckJailOnLogin`, decorated with
`[OnEvent(nameof(PlayerMobile.PlayerLoginEvent))]`. **No existing line was modified or deleted**
(54 insertions, 0 deletions).

**Why it could not be done from `Custom/`.** The fix needs `PlayerJailRecords` and `JailTimers`
to decide whether a player is stranded and to re-arm a release, and it needs `ReleasePlayer` to
let them out. All three are `private static`, and there is no public release API — `Unjail` is a
private command handler that refuses anyone who is not "currently jailed", which is precisely the
state a stranded player is in. Reimplementing the release in `Custom/` would mean duplicating the
freeze/teleport/unfreeze sequence and writing to a record we cannot read.

**The bug.** `JailSystem.Deserialize` only re-arms a release timer when
`record.IsCurrentlyJailed` is true. A sentence that expires while the server is down therefore
gets no timer at all, and the system has no login check. The player logs back in inside the
`JailRegion` — no skills, no spells, no travel, no combat (`JailRegion.cs:23-89`) — with nothing
scheduled to release them, and `[Unjail` rejects them because their sentence has already
"ended". The only remedy was a staff member noticing and moving them out by hand. Reported
against upstream behaviour as of commit `5f0561b7f` ("feat: New Jail System (#2215)").

**What the new method does**, once per login:
- Sentence expired *and* the player is still inside a `JailRegion` → the release never happened,
  so run `ReleasePlayer`.
- Sentence still running but no entry in `JailTimers` → re-arm the release timer, so a live
  prisoner can never be left with nothing to let them out.
- Otherwise do nothing. It also returns early when `player.Frozen`, which means a jail or release
  sequence is already in flight for that session.

**Re-verify after an upstream merge:**
1. That the method still exists and still carries its `[OnEvent]` attribute — a merge that
   rewrites the usings block or the region around `UnfreezeFromRelease` could drop it silently,
   and nothing would fail to compile.
2. That `PlayerMobile.PlayerLoginEvent` still exists and still fires *after* `SendLoginComplete()`
   in `IncomingAccountPackets.cs`. The check reads `player.Location`/`player.Map`, so an earlier
   firing point would evaluate a stale location and miss stranded players.
3. That `Deserialize` still skips timer re-arming for expired sentences. **If upstream fixes this
   properly, delete this modification** rather than keeping both.
4. That `ReleasePlayer`, `PlayerJailRecords` and `JailTimers` keep their current signatures and
   semantics — particularly that `ReleasePlayer` still tolerates a null `from` (it guards its
   `CommandLogging` calls) and still sets `record.JailEndTime = Core.Now`.
5. That `JailRegion` is still the region type used for the jail, and is still reachable via
   `Region.Find(...).IsPartOf<JailRegion>()`.

**Related bugs deliberately NOT fixed here**, to keep this edit minimal — both are separate from
the stranding bug and are worked around from `Custom/` instead (see the restricted zones entry
below):
- `UnfreezePlayer` overwrites `JailTimers[player]` without stopping the previous timer, so
  re-jailing an active prisoner releases them early and then fires a second release.
- Nothing removes a player from `CurrentlyBeingJailed` on release, so a prisoner restored across a
  restart stays in that latch forever and every later `JailPlayer` call for them silently returns.

### `Projects/UOContent/Systems/JailSystem/JailSystem.cs` — public `GetJailEndTime` accessor

**What changed.** Purely additive: one method next to `IsPlayerJailed`. No existing line modified
or deleted.

```csharp
public static DateTime GetJailEndTime(PlayerMobile player) =>
    PlayerJailRecords.GetValueOrDefault(player)?.JailEndTime ?? DateTime.MinValue;
```

**Why it could not be done from `Custom/`.** `Custom/Jail/JailStatusGump.cs` shows a jailed player
how long is left, which needs the sentence end time. JailSystem exposed no read path for it: the
public surface is `JailMap`, `ReleaseMap`, `Configure`, the constructor, `IsPlayerJailed`,
`JailPlayer`, `CheckJailOnLogin` and the two serialization overrides — and only `IsPlayerJailed`
reads state, returning a `bool`. `JailRecord.JailEndTime` is a public property, but the only
container of records (`PlayerJailRecords`) and the fallback (`EmptyRecord`) are both
`private static`, and `JailRecordGump`'s public constructor takes a `JailRecord` that cannot be
obtained from outside. `[JailInfo` works only because it lives inside the class. The alternative
was reflecting over the private dictionary, which would fail silently at runtime on a field rename
— in a gump players look at.

**Re-verify after an upstream merge:**
1. That the method still exists — a merge that rewrites the area around `IsPlayerJailed` could
   drop it, which *would* fail the build, so this one is self-announcing.
2. That `JailRecord.JailEndTime` still means "when the sentence ends" and is still stamped at jail
   time rather than at release-timer arming. `Custom/Jail/JailStatusSystem.cs` subtracts
   `Core.Now` from it directly.
3. That `PlayerJailRecords` still keys on `PlayerMobile` (per character, not per account).
4. **If upstream adds its own public accessor, delete this one** and repoint
   `JailStatusSystem.GetRemaining` at theirs rather than keeping both.

### `Projects/UOContent/Systems/JailSystem/` — release to the facet the player was jailed from

**What changed.** 42 insertions, 3 deletions across two files.

- `JailRecord.cs` — version bumped `[SerializationGenerator(0)]` → `(1)`, one new
  `[SerializableField(5)] private Map _originMap`, and the required `MigrateFrom(V0Content)`.
  New migration schema `Migrations/Server.Systems.JailSystem.JailRecord.v1.json`.
- `JailSystem.cs` — `JailPlayer` records `record.OriginMap = player.Map` alongside the other
  record fields; a new private `GetReleaseMap(PlayerMobile)` resolves the destination facet;
  `TeleportFromJail` calls it instead of using the constant. `ReleaseMap` keeps its name and
  visibility but changes value from `Map.Felucca` to `Map.Trammel` and is now documented as the
  *fallback* facet.

**Why it could not be done from `Custom/`.** The origin facet has to be captured inside
`JailPlayer`, before the teleport to jail overwrites `player.Map`, and stored on `JailRecord` —
and `PlayerJailRecords` is `private static`, `ReleaseLocation`/`TeleportFromJail` are private, and
there is no hook between "player is jailed" and "player is teleported". Nothing outside the class
can observe the jailing early enough or influence where the release lands.

**The behaviour.** `ReleaseMap` was hardcoded to `Map.Felucca`, so a Trammel player served their
sentence and was then dumped on the Felucca side of Britain bank — a different, PvP-enabled
facet from the one they were playing on. Release now returns them to the facet they were taken
from.

**Facet clamp — a judgement call worth knowing about.** Only Felucca and Trammel have a Britain;
they share terrain, so `ReleaseLocation` (1444, 1697, 10) is the bank on both. A player jailed
from Ilshenar, Malas, Tokuno or Ter Mur has no Britain to go back to, and releasing them at those
coordinates would drop them in open country. `GetReleaseMap` therefore returns the origin facet
only when it is Felucca or Trammel, and falls back to `ReleaseMap` (Trammel) otherwise — which is
also what a pre-v1 record with no stored facet gets.

**Re-verify after an upstream merge:**
1. That `JailRecord` is still at version 1 with `MigrateFrom(V0Content)` intact. If upstream bumps
   the version itself, the migration chain must be reconciled by hand — this is the one
   modification on this shard that touches persisted data, so a bad merge corrupts saves rather
   than failing the build.
2. That `record.OriginMap` is still assigned in `JailPlayer` **before** `DismountPlayer` /
   `TeleportToJail` run. Capturing it after the teleport would record the jail's facet.
3. That `ReleaseLocation` is still Britain bank. If upstream moves it somewhere that exists on
   only one facet, the per-facet release stops making sense.
4. **If upstream makes the release facet configurable or per-facet itself, delete this
   modification** rather than keeping both.

## Notes on files that look like modifications but aren't

- **`Projects/UOContent/Migrations/Server.Custom.*.v0.json`** — the serialization schema record
  for each `[SerializationGenerator]` class, which lives in the upstream `Migrations/` folder
  because `UOContent.csproj` wires it there
  (`<AdditionalFiles Include="Migrations/*.v*.json" />`). These are *additive* files, not edits
  to upstream ones, and should be committed alongside the class they describe.

  **Correction to an earlier note in this file:** `dotnet build` does not write these, but a
  separate schema-generator tool does, and it is the right way to produce them:

  ```sh
  dotnet run --project Projects/BuildTool -- --action migrate
  # equivalently: dotnet tool restore && dotnet tool run ModernUOSchemaGenerator -- ModernUO.slnx
  ```

  Run it after adding a serializable class or bumping a version, then commit the generated
  `vN.json` with the code. An earlier entry here claimed these had to be hand-written; that was
  wrong. (The hand-written stubs already committed are correct — re-running the generator leaves
  them unchanged — but the tool is what should be used from now on, and it is the only practical
  way to produce a schema for a class with non-trivial field types.)

  Without the new JSON the *next* version bump cannot construct its `VNContent` and will fail to
  compile.

- **`Distribution/Data/MLQuests.cfg`** — deliberately *not* edited. Upstream ML quests are wired
  to their NPCs by tab-separated lines in that file. Custom quests register through the
  equivalent public API instead, from `Custom/Quests/CustomQuestRegistry.cs`.

- **`Distribution/Data/Spawns/custom/`** — a new folder, not a change to an existing spawn file.
  Import it in-game with `[GenerateSpawners Data/Spawns/custom/trammel/**.json`. The admin gump's
  world-generation button will not pick it up; it only globs the `uoml`, `post-uoml` and `shared`
  folders.

## Upstream seams this shard depends on

Not modifications — but if an upstream merge breaks one of these, the feature named beside it
silently stops working. Re-check them after pulling from `upstream`.

### ML collect-quest auto-counting (`Custom/Quests/AutoCollect*.cs`)

Auto-counting is installed by wrapping every registered `CollectObjective` at startup, with no
edit to the ML quest engine or to any of the ~100 quest definitions. It relies on:

1. `CollectObjective` and `CollectObjectiveInstance` being `public` and unsealed, with public
   constructors — `Engines/ML Quests/Objectives/CollectObjective.cs`.
2. `CollectObjective.CreateInstance` being `public virtual`, with its single call site in the
   `MLQuestInstance` constructor (`Engines/ML Quests/MLQuestEntry.cs:43`) dispatching
   polymorphically.
3. No concrete-type checks against `CollectObjectiveInstance` anywhere in `Projects/`. The only
   type tests are against the *base* `CollectObjective` (`Gumps/BaseQuestGump.cs:184`,
   `MLQuest.cs:68` `HasObjective<CollectObjective>()`), which the wrapper satisfies — that is
   what keeps `RequiresCollection` and the gump layout correct.
4. `MLQuest.Objectives` being a mutable `public List<BaseObjective> { get; set; }`.
5. `BaseObjectiveInstance.WriteTimeRemaining` being `public static`, since
   `AutoCollectObjectiveInstance` cannot reach `BaseObjectiveInstance.WriteToGump` through
   `base` and has to replicate that three-line body.

Two hard constraints that must not be violated in `Custom/`:

- **Never set `Item.QuestItem` automatically.** `Item.Nontransferable => QuestItem`
  (`Projects/Server/Items/Item.cs:545`), so flagging an item makes it undroppable, un-bankable,
  untradeable, unsellable, undyeable and **unusable** (`Mobile.Use` returns early at
  `Mobile.cs:4936`), and turns it blue-green in the pack. The feature auto-*counts* and never
  writes the flag, which is why none of those behaviours change.
- **Never override `Serialize` or `ExtraDataType` on an objective instance.**
  `BaseObjectiveInstance.Deserialize` is `public static` with a closed `switch` over a fixed
  four-value `DataType` enum, so extra fields could never be read back and would desync the world
  save stream.

### Staff quest-reset commands (`Custom/Commands/ResetQuestCommands.cs`)

`[ResetQuest` and `[ResetAllQuests` mutate a player's `MLQuestContext` through public API only.
They depend on:

1. `MLQuestContext.RemoveDoneQuest(MLQuest)` being `public` — the only way to clear a completion
   record from outside the engine.
2. `MLQuestInstance.Cancel(bool removeChain)` being `public`, and `Cancel(true)` running
   `OnQuestCancelled` on each objective (which un-marks quest items) plus dropping the chain
   offer. Prefer it over `Remove()`, which skips that cleanup.
3. `MLQuestContext.QuestInstances` / `.ChainOffers` and `MLQuestSystem.Quests` /
   `.GetContext(PlayerMobile)` all being public.
4. `MLQuest.OnCancel` being an empty virtual with no overrides in the tree — so cancelling an
   instance can never write a fresh completion record. If an upstream quest ever overrides it to
   penalise cancelling, re-check the cancel-then-remove ordering in `ResetQuestTarget`.

Known gap, deliberately worked around rather than patched upstream: `MLQuestContext` has no way
to *enumerate* the completed list (`m_DoneQuests` is private with no accessor), so
`[ResetAllQuests` clears it by iterating `MLQuestSystem.Quests.Values` and calling
`RemoveDoneQuest` for each. That is complete, because records whose quest type no longer resolves
are already discarded at load time by `MLDoneQuestInfo.Deserialize`. A public `DoneQuests`
enumerator or a `ClearDoneQuests()` upstream would let this be a single call.

### Restricted zones (`Custom/Zones/`)

Restricted zones register their own regions at runtime and hand offenders to the upstream
`JailSystem`. No upstream edit was needed. The seams relied on:

1. `JailSystem.JailPlayer(Mobile, PlayerMobile, string)` and `JailSystem.IsPlayerJailed(PlayerMobile)`
   staying `public static` — they are the entire public surface of that system.
2. ~~`GenericPersistence` being publicly subclassable~~ - **no longer relied on.** Zones moved to
   `Data/Custom/restricted-zones.json` so they are diffable, hand-editable and editable by the
   shard editor. Records load in `Configure()`; regions are built in `Initialize()`, because a
   zone region resolves its parent with `Region.Find` and the town regions it needs are loaded by
   `RegionJsonSerializer` *after* the Configure sweep. `Saves/RestrictedZones/` is now dead - the
   old `.bin` can be deleted once the server has been restarted on this code.
3. `BaseRegion`'s `(name, map, parent, ReadOnlySpan<Rectangle2D>)` constructor, and
   `Region.Register()`/`Unregister()` re-resolving mobiles already standing in the affected
   sectors — that is what makes a newly drawn zone apply to its current occupants.
4. `BoundingBoxPicker.Begin`, `EventSink.Disconnected`, and `PlayerMobile.PlayerLoginEvent`.

**Three upstream JailSystem bugs this code works around.** They barely affect a human typing
`[Jail`, but an automated caller hits them. If any are fixed upstream, the workarounds here become
redundant rather than wrong:

- **Re-jailing an active prisoner releases them early.** `UnfreezePlayer` overwrites
  `JailTimers[player]` without stopping the previous timer, so the old sentence's timer still
  fires. `TryJail` guards with `IsPlayerJailed` so this shard never triggers it.
- **After a restart, a mid-sentence prisoner can never be jailed again.** `Deserialize` adds them
  to the private `CurrentlyBeingJailed` latch and only `UnfreezePlayer` removes them, which is
  unreachable without a fresh jail sequence. `JailPlayer` then returns silently — no exception, no
  log. `TryJail` asserts `IsPlayerJailed` immediately after calling and logs an error plus notifies
  staff if the call was swallowed.
- **A sentence expiring while the server is down strands the player in jail.** No release timer is
  re-armed and there is no login check. Not fixable from `Custom/`; staff must move them out.

Also note `JailPlayer` is called with `from: null` (no staff member is behind an automatic jail),
which makes JailSystem's own `CommandLogging` call throw internally and swallow the entry. This
system writes its own `LogFactory` line instead.

### Admin API listener (`Custom/AdminApi/`) - a vetted background thread

`AdminApiServer` runs one dedicated `HttpListener` thread so the shard editor can read and write
the config files and reload the systems that own them without a restart. Audit rule #10 says a new
worker is recorded in the vetted table in `dev-docs/threading-model.md`; that is an upstream file,
so the entry lives here instead to keep the fork's diff against upstream to `Custom/` only.

**Why a thread at all.** Not for performance - `HttpListener.GetContext` is a blocking call, and
the alternative is polling it from the game loop. The thread exists to *keep* the blocking off the
loop, which is the opposite of the usual justification, so the measurement rule does not apply.

**How it obeys the six rules.**

1. *No game state off-thread.* The listener thread touches only sockets and files. Static files and
   map tiles are disk reads. Everything else goes through `AdminApiLoop.TryRun`, which posts to
   `Core.LoopContext` and waits for the result.
2. *Policy on the loop.* Shape projection, config validation and reloads all run inside the posted
   action, so nothing rule-dependent is decided off-thread.
3. *Parks on a kernel wait.* `GetContext()` blocks; it never spins.
4. *Yields to world saves.* Every non-GET request is refused with 503 unless
   `World.WorldState == WorldState.Running`. Deliberately not `World.Saving`, which covers only the
   freeze and misses `PendingSave`.
5. *Bounded.* One thread, one request at a time, request bodies capped at 4 MB.
6. *Everything it calls is safe off-thread.* The only off-loop work is `File`/`Path`/`HttpListener`.

**The one subtlety worth keeping.** `AdminApiLoop` hands results back with a
`TaskCompletionSource`, not a `ManualResetEventSlim`. If a request times out and the loop runs the
posted work afterwards anyway, setting a result nobody is waiting on is harmless - whereas
signalling a disposed reset event would throw *on the game thread*, from a request that had already
given up. `RunContinuationsAsynchronously` keeps the waiter's continuation off the loop.

**Upstream seams relied on:** `Core.LoopContext.Post`, `World.WorldState`, `EventSink.Shutdown`,
`NetState.Instances`, `ServerConfiguration.GetOrUpdateSetting`/`SetSetting`, and
`ImportSpawnersCommand.ImportFile` (which is `internal`, so this only works from inside
`UOContent`). `SpawnerJsonSerializer.SerializeCompact` is used to write spawn files back in their
on-disk layout; plain indented JSON would turn every save into a whole-file diff.

### JSON config binding (`Custom/DailyLife/`, `Custom/Zones/`)

`JsonConfig.GetOptions()` sets neither `PropertyNameCaseInsensitive` nor a `PropertyNamingPolicy`,
so binding is case-**sensitive**. A PascalCase property therefore binds nothing against a
camelCase key, and `JsonSerializer` reports success: every section comes back null and the feature
is silently inert. `TownScheduleConfig` shipped in exactly that state - the whole daily life
feature was dead on arrival and the only symptom was an empty Britain.

Every config member in `Custom/` must carry an explicit `[JsonPropertyName]`, matching the repo
convention (`MapLoader.MapDefinition`, `SpawnerDto`, `BanConfiguration`); the contract is spelled
out in `Server.Tests/Tests/Network/Bans/BanConfigurationTests.cs`. Both config loaders now
validate before replacing the live config, and `UOContent.Tests/Tests/Custom/` asserts that the
shipped files still bind and validate, so schema drift fails in CI rather than in Britain.

### Britain daily life (`Custom/DailyLife/`)

A day schedule (dawn/day/dusk/night) driving tavern patrons, a night watch, route-walking
townsfolk, and shopkeepers who go home at dusk. No upstream edit. The seams relied on:

1. `Clock.GetTime(Map, x, y, out hours, out minutes)` staying public — the single source of
   in-game time. Note it is **not** uniform: it adds `map.MapIndex * 320` minutes per facet and
   `x / 16` for longitude, so the schedule is sampled at one anchor point per town.
2. Phase boundaries matching `LightCycle.ComputeLevelFor` (night <4, dawn <6, day <22, dusk).
   **If upstream changes that curve, change `DayPhaseExtensions.FromHour` to match**, or NPC
   behaviour and the visible sky will disagree.
3. `LightCycle.LevelOverride` staying public and settable, for `[DayPhase`'s forced phases.
4. `BaseCreature.Home` / `RangeHome` / `Spawner` / `AIObject` and `BaseAI.MoveToPoint` staying
   public — that combination is what lets us drive upstream vendors without subclassing.
5. `BaseSpawner` having **no distance check** in `Defrag`/`OnDefragSpawn`. A vendor who walks
   home still counts against the spawner, so no duplicate appears at the empty shop. If upstream
   ever adds a range-based despawn, shops would start growing second shopkeepers at dusk.
6. `BaseVendor` being `FightMode.None`, which exempts it from `BaseCreature`'s `IsSpawnerBound`
   return-home path.

Behaviours to re-check after an upstream merge:

- **`MoveToPoint` compares `Path?.Goal` by reference.** Both route drivers cache a boxed goal; if
  that comparison becomes value-based the caching is merely redundant, but if the signature
  changes the drivers need revisiting.
- **`PlayerRangeSensitive` stops a creature's AI timer when no player is in its sector.** Our own
  NPCs override it to `false`; the shop driver sidesteps it entirely by running on its own timer
  rather than the vendor's `OnThink`. Both are load-bearing for NPCs moving unobserved.
- **"Closed" is soft.** `CheckVendorAccess` only greys the context-menu entry —
  `VendorBuyEntry.OnClick` calls `VendorBuy` without rechecking it, and the `vendor buy` speech
  command bypasses the menu. Absence is the real enforcement. If upstream ever gates `VendorBuy`
  on `CheckVendorAccess`, the greying becomes a real block and this note can go.

**XmlSpawner evaluated and rejected** (SHARD.md roadmap item 3). `upstream/kbatman/muo_xml_spawner`
does not compile against current main (`ISpawner` gained `SpawnBounds`/`IsInSpawnBounds`/
`WalkingRange`/`Running`; the branch implements the old `HomeLocation`/`HomeRange`, and
`IXmlQuest` is referenced but undefined), is 709 commits stale after one day's work in Feb 2024,
ships no XmlAttachments or XmlQuest, and hand-rolls serialization on a 12k-line class. Critically,
`BaseCreature.IsSpawnerBound()` hard-casts `(Spawner as Spawner)`, so XmlSpawner-spawned creatures
would lose return-to-home entirely — fixing that means editing `BaseCreature.cs`. Its one good
idea, `TODStart`/`TODEnd`/`TODMode` (~70 lines with a correct midnight wrap and a `Gametime` mode),
is worth cribbing onto a `TimedSpawner : Spawner` in `Custom/` if we ever want time-gated spawners.
