# Goblin Gang shard notes

Sean's personal ModernUO shard. Fork of modernuo/ModernUO; `upstream` remote tracks the original.

## Conventions
- All custom content lives in `Projects/UOContent/Custom/` (namespace `Server.Custom`). Never edit upstream files unless a change genuinely can't be done any other way; if so, note it in `Projects/UOContent/Custom/MODIFICATIONS.md`.
- Expansion is ML (Mondain's Legacy). Maps: Felucca, Trammel, Ilshenar, Malas, Tokuno.
- Target deployment is Linux, so no Windows-only APIs.
- I'm new to UO server development. Explain the "why" briefly when introducing a ModernUO pattern for the first time.
- Trammel is the primary facet. All custom content, zones and spawners target Trammel unless stated otherwise.

## Workflow
- I run git myself. Give me commit messages separately from code.
- Build: `.\publish.cmd release win x64` from repo root, then run `Distribution\ModernUO.exe`.
- The server must be stopped before rebuilding.
- Map tiles for the shard editor:
  `dotnet run --project Projects/MapExport -c Release -- --out Distribution/web/tiles`
  (`--help` for options). Safe to run with the shard up: MapExport builds its copy of the server
  into its own `bin/deps`, so it never touches `Distribution/`. It is deliberately **not** in
  `ModernUO.slnx`, to keep upstream merges clean. Takes about 6 seconds for all five facets and
  produces ~20 MB; the output is gitignored.
- Shard editor: set `adminapi.enabled` to `true` in `Distribution/Configuration/modernuo.json`,
  start the server, browse <http://127.0.0.1:8081>. The bearer token is `adminapi.token` in that
  same (gitignored) file.

## Roadmap
1. Custom NPC with keyword-triggered dialogue and a simple fetch quest (learning exercise)
2. Restricted zones: enter → 30s warning → auto-jail (uses JailSystem's stock escalation)
3. Evaluate XmlSpawner-for-ModernUO for data-driven quests/events
4. Linux hosting
5. Shard editor: local web map editor with live push to the running server
   (`Projects/MapExport` renders the map tiles, `Custom/AdminApi` serves the API and files,
   `ShardEditor/` is the browser UI)