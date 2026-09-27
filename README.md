# AltBiomeGuard

A BepInEx/Harmony plugin for Valheim that permanently prevents specific, already-explored areas from ever being assigned an **Alternative Biome** (e.g. a Meadows patch that always storms) - without touching any player-built structures, terrain, or objects.

## How it works

Valheim decides which biome sectors get an Alternative Biome purely at runtime, from the world seed (`AltBiomeWorldData.GenerateAltBiomes()`). Nothing about this assignment is written to the save file (`.fwl`/`.db`/chunks) - it's recomputed every time the world loads. This plugin patches `BiomeSector.CanAddModifier()` with Harmony so that, for the sectors you list in the config, the game is told "no" every time it tries to attach an Alternative Biome. Because the check happens before assignment, every downstream system that reads `BiomeSector.AltBiomes` - the minimap name/coloring, forced weather (`EnvMan`), and location/vegetation placement - stays consistent automatically.

No ZDOs, chunks, or player objects are ever touched. This only affects the in-memory biome calculation, so it's completely safe for anything you've built.

### Important: protection scope

An Alternative Biome is assigned to an entire **biome sector** - a whole contiguous landmass of the same base biome type (e.g. one connected stretch of Meadows), not a single 64m zone. The zone coordinate you configure is only used to *locate* which sector to protect; the whole connected sector containing that zone is what actually gets protected. If that landmass is large, the effect applies to all of it, not just the area around your build.

## Requirements

- [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx/releases) installed for Valheim (mono build, x64)
- No other dependencies (Harmony/`0Harmony.dll` ships with BepInEx 5.x)

## Installation

1. Make sure BepInEx is installed and has been run at least once (so `BepInEx/plugins` and `BepInEx/config` exist).
2. Copy `bin/AltBiomeGuard.dll` into:
   ```
   <Valheim game folder>/BepInEx/plugins/AltBiomeGuard.dll
   ```
3. Launch the game once to generate the config file at:
   ```
   <Valheim game folder>/BepInEx/config/reaor.altbiomeguard.cfg
   ```

## Configuration

Open `reaor.altbiomeguard.cfg` and set `ProtectedZones` under `[General]`:

```ini
[General]

## Semicolon-separated list of zone coordinates (x,z) that must never receive an Alternative Biome.
ProtectedZones = 63,-38;64,-38
```

- Format: `x,z` pairs, separated by `;`. You can list as many zones as you want (each one just needs to point to a sector you want protected - you don't need to list every zone inside a large landmass).
- Changes take effect the next time you load the world (either restart the game, or use a config-reload tool if you have one installed).

### Finding a zone's coordinates

- If you have [Upgrade World](https://valheim.thunderstore.io/package/JereKuusela/Upgrade_World/) installed, it adds the current position and zone index directly to the minimap - just stand where the Alternative Biome is and read it off.
- Otherwise, get your world position (e.g. via the `pos` console command from Server Devcommands) and compute:
  ```
  zoneX = floor((worldX + 32) / 64)
  zoneZ = floor((worldZ + 32) / 64)
  ```

## Verifying it worked

Check `BepInEx/LogOutput.log` after loading the world. You should see lines like:

```
[Info :AltBiomeGuard] Zone 63,-38 resolved to biome sector 'Meadows'.
```

If an Alternative Biome had already been assigned to that sector, you'll also see:

```
[Info :AltBiomeGuard] Removed alt biome 'xxx' from protected sector 'Meadows'.
```

No "Removed" line simply means the sector never got an Alternative Biome in the first place during this pass (that's success too, not a failure). If instead you see:

```
[Warning:AltBiomeGuard] Zone 63,-38 did not resolve to a real biome sector (biome data not ready yet?).
```

the world's biome data hasn't been generated yet when the check ran - this is normally handled automatically (Valheim calls this on every world load), but if you see it consistently, try reloading the world once more.

Then in-game, walk to the area and confirm the permanent weather effect is gone and the region's name on the map has reverted to its normal biome name.

## Notes / Caveats

- This is an unofficial plugin built by decompiling the game's own code. If a future Valheim update changes `BiomeSector`, `AltBiomeWorldData`, or their method signatures, this plugin will need to be recompiled against the new game assembly.
- It only prevents *future* assignment during the game's own generation pass - it doesn't need to "undo" anything on disk, since there was never anything on disk to undo.
- If you remove a zone from `ProtectedZones` later, that sector becomes eligible for Alternative Biomes again the next time the world's biome data is (re)computed.
