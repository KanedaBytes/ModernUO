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

## Notes on files that look like modifications but aren't

- **`Projects/UOContent/Migrations/Server.Custom.*.v0.json`** — the serialization schema record
  for each `[SerializationGenerator]` class, which lives in the upstream `Migrations/` folder
  because `UOContent.csproj` wires it there
  (`<AdditionalFiles Include="Migrations/*.v*.json" />`). These are *additive* files, not edits
  to upstream ones, and should be committed alongside the class they describe.

  Note: in this tree the build does **not** write these — it only reads them, as the input the
  generator diffs against when you bump a version. Verified on both Debug and Release builds of
  `UOContent.csproj`: nothing under `Migrations/` is created or touched. So when you add a new
  serializable class, hand-write its stub to match the upstream shape, e.g.

  ```json
  { "version": 0, "type": "Server.Custom.OldMarta" }
  ```

  The build succeeds without it, but the file is the baseline a future `MigrateFrom` needs.

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
2. `GenericPersistence` being publicly subclassable, and a `Persistence` self-registering in its
   constructor. The singleton must be built in `Configure()`, which runs before `World.Load()`.
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
