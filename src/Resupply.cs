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
        private static readonly HashSet<Unit> owned = new HashSet<Unit>();
        private static int warnings, served, noSource;
        private static float nextReport;

        public static void Reset()
        {
            lastServed.Clear();
            candidates.Clear();
            nextTick = 0f;
            warnings = served = noSource = 0;
            nextReport = 0f;
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
            owned.Clear();
            foreach (var u in purchased) if (u != null) { candidates.Add(u); owned.Add(u); }
            foreach (var u in factionBought) if (u != null) { candidates.Add(u); owned.Add(u); }

            float radius = Mathf.Max(Plugin.ResupplyRadius.Value, 10f);
            foreach (var unit in candidates)
            {
                try
                {
                    Serve(unit, hq, rmc, radius);
                }
                catch (Exception e)
                {
                    Warn("unit " + (unit != null ? unit.unitName : "?"), e);
                }
            }
            if (Time.time >= nextReport)
            {
                nextReport = Time.time + 60f;
                if (served > 0 || noSource > 0)
                {
                    Plugin.Log.LogInfo($"[Resupply] last minute: {served} rearmed, {noSource} waited with no ammo source within {radius:0} m; sources known: {rmc.Rearmers.Count}");
                }
                served = noSource = 0;
            }
        }

        private static void Serve(Unit unit, FactionHQ hq, RearmMissionController rmc, float radius)
        {
            if (unit.disabled || unit.NetworkHQ != hq || unit is Missile) return;
            if (!(unit is GroundVehicle) && !(unit is Ship) && !(unit is Aircraft)) return;
            if (unit.GetComponent<Rearmer>() != null && !(unit is GroundVehicle)) return;
            // aircraft-based units: only the vehicles bought with this mod and not driven by a player
            // (players land at a base / rearm the normal way)
            if (unit is Aircraft ac && (!owned.Contains(unit) || ac.Player != null)) return;
            if (!(unit is Ship))
            {
                // ground vehicles and aircraft-based vehicles (KAR / BOTE, parked planes) must stand on the ground
                if (unit.radarAlt > 3f || unit.speed > 2f) return;
            }
            else if (unit.speed > 3f)
            {
                return;
            }
            if (!MissingAmmo(unit, out bool brokenStation)) return;
            if (lastServed.TryGetValue(unit, out float t) && Time.time - t < 8f) return;

            GlobalPosition at = unit.GlobalPosition();
            Rearmer best = null;
            float bestDist = radius;
            for (int i = rmc.Rearmers.Count - 1; i >= 0; i--)
            {
                var r = rmc.Rearmers[i];
                try
                {
                    if (r == null || r.Unit == null || r.Unit == unit || r.Unit.disabled || r.Capacity <= 0f) continue;
                    if (r.Unit.NetworkHQ != null && r.Unit.NetworkHQ != hq) continue;
                    float d = FastMath.Distance(at, PositionOf(r));
                    if (d <= bestDist)
                    {
                        best = r;
                        bestDist = d;
                    }
                }
                catch (Exception e)
                {
                    Warn("rearmer check", e);
                }
            }
            if (best == null)
            {
                noSource++;
                return;
            }
            lastServed[unit] = Time.time;
            bool done;
            // the game's rearm message for aircraft-based units touches cockpit/countermeasure parts
            // that AI-driven KAR / BOTE vehicles may not have — refill those directly
            if (brokenStation || unit is Aircraft)
            {
                done = RearmManually(best, unit);
            }
            else
            {
                try
                {
                    done = best.ProcessRearmRequest(unit, out _);
                }
                catch (Exception e)
                {
                    Warn("game rearm", e);
                    done = RearmManually(best, unit);
                }
            }
            if (done)
            {
                served++;
                Plugin.Log.LogInfo($"[Resupply] {unit.unitName} rearmed from {best.Unit.unitName} ({bestDist:0} m)");
            }
        }

        private static GlobalPosition PositionOf(Rearmer r)
        {
            try
            {
                return r.GetPosition();
            }
            catch
            {
                // some containers have no access point set — use the object itself
                return r.Unit != null ? r.Unit.GlobalPosition() : r.transform.GlobalPosition();
            }
        }

        /// <summary>Fallback when the game's routine can't handle a unit (e.g. a station without weapon info):
        /// refill every normal station, paying with the rearmer's capacity.</summary>
        private static bool RearmManually(Rearmer rearmer, Unit unit)
        {
            float capacity = rearmer.Capacity;
            var stations = new int[unit.weaponStations.Count];
            bool any = false;
            for (int i = 0; i < unit.weaponStations.Count; i++)
            {
                var ws = unit.weaponStations[i];
                if (ws == null || ws.WeaponInfo == null || ws.WeaponInfo.cargo || ws.WeaponInfo.massPerRound <= 0f || ws.WeaponInfo.nuclear) continue;
                int need = ws.FullAmmo - ws.GetAmmoTotal();
                if (need <= 0) continue;
                int can = Mathf.Min(need, Mathf.FloorToInt(capacity / ws.WeaponInfo.massPerRound));
                if (can <= 0) continue;
                stations[i] = can;
                capacity -= can * ws.WeaponInfo.massPerRound;
                any = true;
            }
            if (!any) return false;
            bool full = true;
            for (int i = 0; i < unit.weaponStations.Count; i++)
            {
                var ws = unit.weaponStations[i];
                if (ws == null) continue;
                try
                {
                    if (stations[i] > 0) ws.Rearm(stations[i]);
                    if (ws.WeaponInfo != null && !ws.WeaponInfo.cargo && ws.GetAmmoTotal() < ws.FullAmmo) full = false;
                }
                catch (Exception e)
                {
                    Warn("station rearm", e);
                }
            }
            if (full)
            {
                unit.HasRequestedRearm = false;
                try
                {
                    unit.NetworkHQ?.RearmMissionController?.DeregisterNeedsRearm(unit);
                }
                catch
                {
                }
            }
            rearmer.Unit.RpcUpdateRearmerCapacity(capacity);
            return true;
        }

        private static void Warn(string where, Exception e)
        {
            warnings++;
            if (warnings <= 5)
            {
                Plugin.Log.LogWarning($"[Resupply] {where}: {e}");
            }
        }

        private static bool MissingAmmo(Unit unit, out bool brokenStation)
        {
            brokenStation = false;
            bool missing = false;
            if (unit.weaponStations == null) return false;
            foreach (var ws in unit.weaponStations)
            {
                if (ws == null || ws.WeaponInfo == null || ws.Weapons == null || ws.Weapons.Count == 0 || ws.Weapons[0] == null)
                {
                    brokenStation = true;
                    continue;
                }
                if (ws.WeaponInfo.cargo || ws.WeaponInfo.massPerRound == 0f) continue;
                if (ws.GetAmmoTotal() < ws.FullAmmo) missing = true;
            }
            return missing;
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
