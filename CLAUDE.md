# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A BepInEx/Harmony mod for the Unity game **Outward** that randomizes where the game's
area exits lead — an "exit randomizer" that turns the interconnected world into a
shuffled, roguelike-style experience while guaranteeing the map stays fully traversable.
GUID `com.kirbunkle.outward-exit-randomizer-mod`. It compiles to a single DLL dropped into
`BepInEx/plugins/`.

> Note: `README.md` is stale — it still describes the generic "Outward Mod Template" this
> was forked from (wrong name, references a non-existent `OutwardModTemplate.sln`). Don't
> trust it for project specifics.

## Build & dev workflow (Linux / Arch-based)

The project targets **.NET Framework 4.7.2** (`net472`, Windows-era framework), built with
the modern `dotnet` SDK. All game/modding dependencies come from NuGet (the BepInEx feed is
configured in `src/nuget.config`), so no local copy of Outward's DLLs is needed.

```bash
cd src
dotnet build -c Release      # output -> ../Release/OutwardExitRandomizerMod.dll
```

- Building `net472` on Linux requires the .NET Framework reference assemblies. These are
  supplied by the `Microsoft.NETFramework.ReferenceAssemblies` NuGet package, already added
  to `OutwardExitRandomizerMod.csproj` (`PrivateAssets="all"`). Without it the build fails
  with `MSB3644: reference assemblies ... not found`.
- The built DLL is **symlinked** into the local install so builds go live with no copy step:
  `~/.local/share/Steam/steamapps/common/Outward/Outward_Defed/BepInEx/plugins/OutwardExitRandomizerMod.dll`
  → `Release/OutwardExitRandomizerMod.dll`. Each `dotnet build` overwrites the Release DLL in
  place; the symlink reflects it. (Deleting `Release/` dangles the link until the next build.)
- **Close Outward before rebuilding** — the game loads the DLL at startup, so changes only
  take effect on relaunch.

There are no tests, lint config, or CI in this repo.

## Architecture

Everything lives in **`src/Plugin.cs`** (~2,450 lines). Two halves: ~900 lines of logic,
then ~1,500 lines of hand-authored area/exit data tables.

### Exit model (the key abstraction)

- **`AreaSpawn`** = `(AreaManager.AreaEnum, SpawnPoint int)` — an area + a specific spot in it.
- **`Exit`** = a `From` AreaSpawn (where the exit entity physically sits) and a `To` AreaSpawn
  (its vanilla destination). In-game, an `InteractionSwitchArea` entity's `Area`/`SpawnPoint`
  fields *are* its destination (the `To`).
- The randomizer's output is a `Dictionary<AreaSpawn, AreaSpawn>` **keyed by `To`**, mapping
  each exit entity's vanilla destination to a new destination. Every `To` must be globally
  unique (it identifies the entity). `ConnectExits(a, b)` makes a two-way link by setting
  `newExits[a.To] = b.From` and `newExits[b.To] = a.From` — i.e. the doorway at `a.From` now
  leads to `b.From` and vice-versa.
- **`AreaGroup`** = a set of `Exit`s mutually reachable in-game, keyed by an `AreaGroupEnum`.
  Grouping is what lets the shuffler avoid making areas unreachable. Some groups carry
  `ConnectedAreasViaOneWay` (gates you open from one side) — when a group is added to the
  reachable "core", its one-way-connected groups are pulled in recursively.

### The shuffle (`AreaGroupShuffler.Shuffle`)

Runs in three passes against `DefaultAreaGroups`, growing a reachable `CoreExitList` from the
start spawn:
1. **Connective pass** — connect every multi-exit group into one component (guarantees every
   area is reachable). Includes special-casing to inject the big overworld zones early and to
   keep `LevantCastleSecretEntrance` reachable when Levant doors lock in chapter 4.
2. **Single-exit pass** — attach all one-exit groups to the core.
3. **`ShuffleUnconnectedExits`** — randomly pair every remaining unused exit (all belong to
   already-reachable areas, so pairing any two is safe).

`ForceAllExitsToChange` (config) makes connections retry to avoid reproducing vanilla pairs.

### Harmony patches & lifecycle

- `NetworkLevelLoader.LoadLevel` (prefix) — on new-game load (`CierzoTutorial`): picks the
  start location, runs the shuffle into the static `RandomizedExits`, and applies the
  quest-skip / random-faction / breakthrough-skill / open-Sirocco / open-Harmattan helpers.
- `InteractionSwitchArea.AwakeInit` (prefix) — rewrites each exit entity's destination using
  `RandomizedExits`.
- `SaveInstance.Save` / `PreLoadEnvironment` — the shuffled map is serialized to a sidecar
  `<GUID>.<VERSION>.savedata` file next to the save and reloaded on load.
- `DefeatScenariosManager.ActivateDefeatScenario` (prefix) — optional always-permadeath on
  hardcore.

**Important:** randomization is generated and baked into the save **only at new-game
creation**. Config changes (including the feature below) require starting a **new game** to
take effect; existing saves load their stored map verbatim.

### Area data tables

`DefaultAreaGroups` (the bulk of the file) defines every shuffled exit. Many entries are
commented out with notes explaining *why* (one-way drops / quest-locked doors that would
cause soft-locks) — respect these when editing; the author was deliberate. `AreaGroupEnum`,
`AreaList`, and the per-region group definitions (Cierzo/Chersonese, Monsoon/Hallowed Marsh,
Levant/Abrassar, Harmattan/Antique Plateau, Berg/Emercar, Sirocco/Caldera) are all here.

## Feature: `CombineAllTownLocations` (config, default false)

Wires the six towns (Cierzo, Monsoon, Levant, Harmattan, Berg, New Sirocco) into one sealed
cluster connected to the rest of the world by **exactly two** exits. Implemented as a pre-pass
(`ConnectTownCluster`, run from `Shuffle` before the connective pass):

- `TownHubs` defines each town as the set of area groups physically inside it (the "fully
  sealed" variant includes Harmattan's two lockable wall gates and Levant's castle secret
  entrance). Editing this array is how you tune what's in the cluster.
- It builds a random spanning tree across the hubs plus one extra link, which leaves exactly
  two town exits unused (the bridges). Those bridges are handed back to the normal shuffler —
  as a synthetic multi-exit `AreaGroup` if the player starts outside the cluster, or seeded
  into `CoreExitList` if they start inside it — so the rest of the world still connects and
  everything stays reachable. (Single-exit hubs can only be leaves; this is why `TryLinkToHost`
  attaches to the highest-spare host.)
- Known caveats of the fully-sealed variant: the chapter-4 Levant safeguard goes dormant
  (its castle entrance is now inside the cluster), and if that entrance is quest-gated in-game
  one internal link could be temporarily impassable. Dropping `LevantCastleSecretEntrance` /
  the Harmattan gates from `TownHubs` reverts toward "main town gates only" behavior.
