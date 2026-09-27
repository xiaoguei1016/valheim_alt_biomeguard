using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace AltBiomeGuard
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class AltBiomeGuardPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "reaor.altbiomeguard";
        public const string PluginName = "AltBiomeGuard";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;
        internal static readonly HashSet<ZoneKey> ProtectedZones = new HashSet<ZoneKey>();

        // Resolved once per GenerateAltBiomes() pass. BiomeSector.MinZone/MaxZone
        // can't be used for this: the game's own GenerateSectors() sets
        // MaxZone from sector.Min instead of sector.Max (see AltBiomeWorldData.cs),
        // so both fields end up identical and a bounding-box test never matches.
        // Resolving the real BiomeSector reference via WorldGenerator.GetBiomeSector()
        // and comparing by reference sidesteps that bug entirely.
        internal static readonly HashSet<BiomeSector> ResolvedProtectedSectors = new HashSet<BiomeSector>();

        private ConfigEntry<string> _protectedZonesConfig;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            _protectedZonesConfig = Config.Bind(
                "General",
                "ProtectedZones",
                "",
                "Semicolon-separated list of zone coordinates (x,z) that must never receive an Alternative Biome. " +
                "Use the same zone coordinates shown by Upgrade World's minimap overlay. Example: \"3,-3;4,-3;4,-4\"");

            ParseProtectedZones(_protectedZonesConfig.Value);
            _protectedZonesConfig.SettingChanged += (sender, args) => ParseProtectedZones(_protectedZonesConfig.Value);

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(AltBiomeGuardPlugin).Assembly);

            Log.LogInfo(PluginName + " " + PluginVersion + " loaded. Protected zones: " + ProtectedZones.Count);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private static void ParseProtectedZones(string raw)
        {
            ProtectedZones.Clear();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            string[] entries = raw.Split(';');
            foreach (string entry in entries)
            {
                string trimmed = entry.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                string[] parts = trimmed.Split(',');
                short x, z;
                if (parts.Length == 2 &&
                    short.TryParse(parts[0].Trim(), out x) &&
                    short.TryParse(parts[1].Trim(), out z))
                {
                    ProtectedZones.Add(new ZoneKey(x, z));
                    if (Log != null)
                    {
                        Log.LogInfo("Protecting zone " + x + "," + z + " from Alternative Biome assignment.");
                    }
                }
                else if (Log != null)
                {
                    Log.LogWarning("Could not parse zone entry '" + trimmed + "' in ProtectedZones config.");
                }
            }
        }

        // Runs at the start of every AltBiomeWorldData.GenerateAltBiomes() pass,
        // i.e. after GenerateSectors() has already flood-filled PointSectors, so
        // WorldGenerator.instance.GetBiomeSector() resolves real sectors here.
        internal static void ResolveProtectedSectors()
        {
            ResolvedProtectedSectors.Clear();
            if (WorldGenerator.instance == null)
            {
                return;
            }

            foreach (ZoneKey zone in ProtectedZones)
            {
                float worldX = zone.X * 64f;
                float worldZ = zone.Z * 64f;
                BiomeSector sector = WorldGenerator.instance.GetBiomeSector(worldX, worldZ);
                if (IsRealSector(sector))
                {
                    ResolvedProtectedSectors.Add(sector);
                    if (Log != null)
                    {
                        Log.LogInfo("Zone " + zone.X + "," + zone.Z + " resolved to biome sector '" + sector.GetName(true) + "'.");
                    }
                }
                else if (Log != null)
                {
                    Log.LogWarning("Zone " + zone.X + "," + zone.Z + " did not resolve to a real biome sector (biome data not ready yet?).");
                }
            }
        }

        private static bool IsRealSector(BiomeSector sector)
        {
            return sector != null
                && sector != BiomeSector.Empty
                && sector != BiomeSector.EmptyMeadows
                && sector != BiomeSector.EmptyBlackForest
                && sector != BiomeSector.EmptyEdge;
        }

        // Safety net: strip any AltBiome that still ended up on a protected sector
        // (e.g. assigned through a path other than CanAddModifier) once generation
        // for this pass is fully done, keeping AltBiome.Sectors bookkeeping in sync.
        internal static void StripProtectedSectors()
        {
            foreach (BiomeSector sector in ResolvedProtectedSectors)
            {
                if (sector.AltBiomes.Count == 0)
                {
                    continue;
                }

                foreach (AltBiome altBiome in sector.AltBiomes.ToList())
                {
                    altBiome.Sectors.Remove(sector);
                    if (Log != null)
                    {
                        Log.LogInfo("Removed alt biome '" + altBiome.m_name + "' from protected sector '" + sector.GetName(true) + "'.");
                    }
                }

                sector.AltBiomes.Clear();
            }
        }

        internal struct ZoneKey : IEquatable<ZoneKey>
        {
            public readonly short X;
            public readonly short Z;

            public ZoneKey(short x, short z)
            {
                X = x;
                Z = z;
            }

            public bool Equals(ZoneKey other)
            {
                return X == other.X && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is ZoneKey && Equals((ZoneKey)obj);
            }

            public override int GetHashCode()
            {
                return (X << 16) ^ (ushort)Z;
            }
        }
    }

    // Resolve protected zone coordinates to live BiomeSector references right
    // before the random alt-biome assignment pass runs.
    [HarmonyPatch(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateAltBiomes))]
    internal static class Patch_AltBiomeWorldData_GenerateAltBiomes
    {
        private static void Prefix()
        {
            AltBiomeGuardPlugin.ResolveProtectedSectors();
        }

        private static void Postfix()
        {
            AltBiomeGuardPlugin.StripProtectedSectors();
        }
    }

    // BiomeSector.CanAddModifier() decides whether a biome sector is eligible to
    // receive a given AltBiome. Blocking it here (before AddModifier ever runs)
    // keeps every downstream consumer consistent - minimap, weather (EnvMan),
    // and location/vegetation placement (ZoneSystem genloc) - since they all
    // read back BiomeSector.AltBiomes. Nothing on disk (ZDOs, chunks) is touched.
    [HarmonyPatch(typeof(BiomeSector), nameof(BiomeSector.CanAddModifier))]
    internal static class Patch_BiomeSector_CanAddModifier
    {
        private static void Postfix(BiomeSector __instance, ref bool __result)
        {
            if (__result && AltBiomeGuardPlugin.ResolvedProtectedSectors.Contains(__instance))
            {
                __result = false;
            }
        }
    }
}
