using System;
using System.Collections.Generic;
using UnityEngine;

namespace FieldCommand
{
    /// <summary>
    /// Makes ammo containers, depots and supply trucks actually resupply nearby units.
    /// The game only rearms a unit when it stands inside a rearmer's (small) service radius and has asked
    /// for ammo; purchased units often stood just outside it and waited forever. Every few seconds we look
    /// for friendly units that are short of ammo and let the nearest friendly rearmer within the
    /// configured radius serve them, using the game's own rearm routine (same cost and capacity rules).
    /// </summary>
    internal static class Resupply
    {
        private const float Interval = 4f;
        private static float nextTick;
        private static readonly Dictionary<Unit, float> lastServed = new Dictionary<Unit, float>();
        private static readonly HashSet<Unit> candidates = new HashSet<Unit>();

        public static void Reset()
        {
            lastServed.Clear();
            candidates.Clear();
            nextTick = 0f;
        }

        public static void Tick(List<Unit> purchased, List<Unit> factionBought)
        {
            if (!Plugin.EnableResupply.Value || Time.time < nextTick) return;
            nextTick = Time.time + Interval;
            if (!GameApi.IsServer() || !GameApi.TryGetLocal(out _, out var hq) || hq == null) return;
            var rmc = hq.RearmMissionController;
            if (rmc == null || rmc.Rearmers == null || rmc.Rearmers.Count == 0) return;

            candidates.Clear();
            try
            {
                foreach (var u in rmc.UnitsNeedingRearm)
                {
                    if (u != null) candidates.Add(u);
                }
            }
            catch
            {
                // list changed while reading — try again next tick
            }
            foreach (var u in purchased) if (u != null) candidates.Add(u);
            foreach (var u in factionBought) if (u != null) candidates.Add(u);

            float radius = Mathf.Max(Plugin.ResupplyRadius.Value, 10f);
            foreach (var unit in candidates)
            {
                try
                {
                    Serve(unit, hq, rmc, radius);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[Resupply] " + e.Message);
                }
            }
        }

        private static void Serve(Unit unit, FactionHQ hq, RearmMissionController rmc, float radius)
        {
            if (unit.disabled || unit.NetworkHQ != hq || unit is Aircraft || unit is Missile) return;
            if (!(unit is GroundVehicle) && !(unit is Ship)) return;
            if (unit.radarAlt > 2f) return;
            if (unit is GroundVehicle && unit.speed > 2f) return;
            if (!MissingAmmo(unit)) return;
            if (lastServed.TryGetValue(unit, out float t) && Time.time - t < 10f) return;

            GlobalPosition at = unit.GlobalPosition();
            Rearmer best = null;
            float bestDist = radius;
            foreach (var r in rmc.Rearmers)
            {
                if (r == null || r.Unit == null || r.Unit == unit || r.Unit.disabled || r.Capacity <= 0f) continue;
                float d = FastMath.Distance(at, r.GetPosition());
                if (d <= bestDist)
                {
                    best = r;
                    bestDist = d;
                }
            }
            if (best == null) return;
            lastServed[unit] = Time.time;
            bool done = best.ProcessRearmRequest(unit, out _);
            if (done)
            {
                Plugin.Log.LogInfo($"[Resupply] {unit.unitName} rearmed from {best.Unit.unitName} ({bestDist:0} m)");
            }
        }

        private static bool MissingAmmo(Unit unit)
        {
            if (unit.weaponStations == null) return false;
            foreach (var ws in unit.weaponStations)
            {
                if (ws == null || ws.WeaponInfo == null || ws.WeaponInfo.cargo || ws.WeaponInfo.massPerRound == 0f) continue;
                if (ws.GetAmmoTotal() < ws.FullAmmo) return true;
            }
            return false;
        }

        /// <summary>Supply trucks carry the game's RearmVehicleAI; it only starts when the truck is not holding position.</summary>
        public static bool IsSupplyTruck(GameObject prefab)
        {
            try
            {
                return prefab != null && prefab.GetComponentInChildren<RearmVehicleAI>(true) != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
