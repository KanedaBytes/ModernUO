# Upstream modifications

Per `SHARD.md`, all custom content lives under `Projects/UOContent/Custom/` (namespace
`Server.Custom`). This file logs any change that had to be made to an upstream file because it
genuinely could not be done any other way.

## Modifications

None.

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
