using System;
using System.Collections.Generic;
using System.Linq;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace FieldCommand
{
    public enum UnitCategory
    {
        Ground,
        Naval
    }

    public class CatalogEntry
    {
        public UnitDefinition Definition;
        public UnitCategory Category;   // decides land/water placement rules
        public bool Playable;           // KAR / BOTE unit the player drives himself
        public bool Static;             // building / container: never moves
        public bool Supply;             // containers & depots: just objects, they don't take a unit slot
        public string Name;
        public string RoleKey;
        public string TypeName => GameApi.RoleLabel(RoleKey);
        public float Price;
        public int GroupOrder;
        internal Classifier.Group Group;
        public string GroupName => Group.Name ?? "";
        public string Faction;          // faction name, FactionResolver.Shared, or null (unknown)
        public string FactionTag;
        public bool AllowedForMe;
    }

    /// <summary>Thin wrapper around the game's own systems.</summary>
    internal static class GameApi
    {
        private static int spawnCounter;

        public static bool InMission()
        {
            try
            {
                return NetworkSceneSingleton<Spawner>.i != null && Encyclopedia.Lookup != null && (GameManager.gameState == GameState.SinglePlayer || GameManager.gameState == GameState.Multiplayer);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsServer()
        {
            try
            {
                return NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetLocal(out Player player, out FactionHQ hq)
        {
            hq = null;
            if (!GameManager.GetLocalPlayer<Player>(out player))
            {
                return false;
            }
            return GameManager.GetLocalHQ(out hq) && hq != null;
        }

        // ---------- money ----------

        public static float GetFunds()
        {
            if (!TryGetLocal(out var player, out var hq))
            {
                return 0f;
            }
            return Plugin.Funds.Value == FundsSource.Faction ? hq.factionFunds : player.Allocation;
        }

        public static void Spend(float amount)
        {
            if (!TryGetLocal(out var player, out var hq))
            {
                return;
            }
            if (Plugin.Funds.Value == FundsSource.Faction)
            {
                hq.AddFunds(-amount);
            }
            else
            {
                player.AddAllocation(-amount);
            }
        }

        public static string Money(float millions)
        {
            return "$" + millions.ToString("0.0") + "M";
        }

        // ---------- catalog ----------

        public static List<CatalogEntry> BuildCatalog()
        {
            var result = new List<CatalogEntry>();
            var seen = new HashSet<UnitDefinition>();
            IEnumerable<UnitDefinition> defs = Encyclopedia.Lookup.Values;
            try
            {
                var enc = Encyclopedia.i;
                if (enc != null)
                {
                    defs = defs.Concat(enc.vehicles.Cast<UnitDefinition>()).Concat(enc.ships.Cast<UnitDefinition>()).Concat(enc.aircraft.Cast<UnitDefinition>())
                               .Concat(enc.buildings.Cast<UnitDefinition>()).Concat(enc.otherUnits);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Encyclopedia lists unavailable: " + e.Message);
            }

            foreach (var def in defs)
            {
                if (def == null || def.unitPrefab == null || !seen.Add(def))
                {
                    continue;
                }
                UnitCategory cat;
                bool playable = false;
                bool isStatic = false;
                Classifier.Group group;
                if (def is VehicleDefinition)
                {
                    cat = UnitCategory.Ground;
                    group = Classifier.Classify(def);
                }
                else if (def is ShipDefinition)
                {
                    cat = UnitCategory.Naval;
                    group = Classifier.Classify(def);
                }
                else if (def is BuildingDefinition || (!(def is AircraftDefinition) && !(def is MissileDefinition) && def.unitPrefab.GetComponent<Container>() != null))
                {
                    if (!Plugin.EnableStatic.Value) continue;
                    var g = Classifier.ClassifyStatic(def);
                    if (g == null) continue;
                    group = g.Value;
                    cat = UnitCategory.Ground;
                    isStatic = true;
                }
                else if (def is AircraftDefinition ad && Plugin.EnablePlayable.Value)
                {
                    var kind = Classifier.DetectPlayable(ad);
                    if (kind == Classifier.PlayableKind.None) continue;
                    playable = true;
                    cat = kind == Classifier.PlayableKind.Naval ? UnitCategory.Naval : UnitCategory.Ground;
                    group = kind == Classifier.PlayableKind.Naval ? Classifier.PlayNaval : Classifier.PlayGround;
                }
                else
                {
                    continue;
                }
                bool allowed = true;
                try
                {
                    allowed = def.IsAllowed(MissionManager.AllowEventContent);
                }
                catch
                {
                }
                if (!allowed)
                {
                    continue;
                }
                string name = string.IsNullOrEmpty(def.unitName) ? def.name : def.unitName;
                result.Add(new CatalogEntry
                {
                    Definition = def,
                    Category = cat,
                    Playable = playable,
                    Static = isStatic,
                    Supply = isStatic && (!(def is BuildingDefinition) || group.Order == Classifier.Depots.Order),
                    Name = name,
                    RoleKey = playable ? "" : RoleTag(def),
                    Price = PriceOf(def),
                    GroupOrder = group.Order,
                    Group = group
                });
            }

            ApplyFactions(result);

            result.Sort((a, b) =>
            {
                int c = a.GroupOrder.CompareTo(b.GroupOrder);
                if (c != 0) return c;
                c = a.Price.CompareTo(b.Price);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });
            return result;
        }

        private static void ApplyFactions(List<CatalogEntry> entries)
        {
            Dictionary<UnitDefinition, string> map;
            try
            {
                map = FactionResolver.Resolve(entries.Select(e => e.Definition));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Faction resolve failed: " + e);
                map = new Dictionary<UnitDefinition, string>();
            }
            TryGetLocal(out _, out var hq);
            string mine = FactionResolver.FactionNameOf(hq);
            bool restrict = Plugin.RestrictToFaction.Value;
            bool unknownOk = Plugin.UnknownUnits.Value == UnknownUnitPolicy.AllowForAll;
            var log = new System.Text.StringBuilder("Faction table (key | name | faction):\n");
            foreach (var e in entries)
            {
                map.TryGetValue(e.Definition, out var f);
                e.Faction = f;
                e.FactionTag = f == null ? "?" : FactionResolver.TagOf(f);
                if (!restrict) e.AllowedForMe = true;
                else if (f == null) e.AllowedForMe = unknownOk;
                else e.AllowedForMe = f == FactionResolver.Shared || f == mine;
                log.Append("  ").Append(FactionResolver.KeyOf(e.Definition)).Append(" | ").Append(e.Name).Append(" | ").Append(e.FactionTag).Append('\n');
            }
            if (Plugin.LogFactionTable.Value)
            {
                Plugin.Log.LogInfo(log.ToString());
            }
        }

        private static string RoleTag(UnitDefinition def)
        {
            var r = def.roleIdentity;
            if (r.antiAir >= 0.5f && r.antiSurface < 0.5f) return "aa";
            if (r.antiRadar >= 0.5f) return "arm";
            if (r.antiSurface >= 0.5f) return "strike";
            if (r.antiMissile >= 0.5f) return "abm";
            return "";
        }

        public static string RoleLabel(string key)
        {
            switch (key)
            {
                case "aa": return L.T("AA", "ПВО");
                case "arm": return L.T("Anti-radar", "ПРЛ");
                case "strike": return L.T("Strike", "Удар");
                case "abm": return L.T("Anti-missile", "ПРО");
                default: return "";
            }
        }

        public static float PriceOf(UnitDefinition def)
        {
            return Mathf.Max(def.value * Plugin.PriceMultiplier.Value, Plugin.MinPrice.Value);
        }

        // ---------- placement validation ----------

        public struct Placement
        {
            public bool Ok;
            public string Reason;
            public GlobalPosition Position;
            public Quaternion Rotation;
        }

        public static bool IsWaterAt(Vector3 local, out float depth)
        {
            float sea = Datum.LocalSeaY;
            if (PathfindingAgent.RaycastTerrain(local, out var hit))
            {
                depth = sea - hit.point.y;
                return depth > 0f;
            }
            depth = 1000f; // no terrain at all = open ocean
            return true;
        }

        /// <param name="global">Cursor position (global). y is ignored and resolved here.</param>
        public static Placement Validate(CatalogEntry entry, GlobalPosition global, float headingDeg, List<Unit> purchased)
        {
            var p = new Placement { Rotation = Quaternion.Euler(0f, headingDeg, 0f) };
            if (!IsServer())
            {
                p.Reason = L.T("Single-player / host only", "Только одиночная игра / хост");
                return p;
            }
            if (!TryGetLocal(out _, out var hq))
            {
                p.Reason = L.T("No faction", "Нет фракции");
                return p;
            }

            if (!entry.AllowedForMe)
            {
                p.Reason = L.T("Other faction's vehicle", "Техника другой фракции");
                return p;
            }

            if (entry.Playable && TryGetLocal(out var me, out _) && me.Aircraft != null && !me.Aircraft.disabled)
            {
                p.Reason = L.T("Leave your current vehicle first", "Сначала выйди из текущей техники");
                return p;
            }

            Vector3 local = global.ToLocalPosition();
            local.y = Datum.LocalSeaY;
            var def = entry.Definition;

            if (entry.Category == UnitCategory.Naval)
            {
                float half = Mathf.Max(def.length * 0.5f, 5f);
                Vector3 fwd = p.Rotation * Vector3.forward;
                float minDepth = float.MaxValue;
                foreach (var pt in new[] { local, local + fwd * half, local - fwd * half })
                {
                    if (!IsWaterAt(pt, out float d))
                    {
                        p.Reason = L.T("Ships — water only", "Корабль — только на воду");
                        return p;
                    }
                    minDepth = Mathf.Min(minDepth, d);
                }
                if (minDepth < Plugin.MinShipDepth.Value)
                {
                    p.Reason = L.T($"Too shallow ({minDepth:0} m)", $"Мелко ({minDepth:0} м)");
                    return p;
                }
                local.y = Datum.LocalSeaY;
            }
            else
            {
                if (!PathfindingAgent.RaycastTerrain(local, out var hit) || hit.point.y < Datum.LocalSeaY + 0.3f)
                {
                    p.Reason = L.T("Ground units — land only", "Наземка — только на сушу");
                    return p;
                }
                if (Vector3.Angle(hit.normal, Vector3.up) > (entry.Static ? 20f : 35f))
                {
                    p.Reason = L.T("Slope is too steep", "Слишком крутой склон");
                    return p;
                }
                local = hit.point + Vector3.up * (def.spawnOffset.y + 0.5f);
            }
            p.Position = local.ToGlobalPosition();

            if (Plugin.RestrictToTerritory.Value && !IsInTerritory(hq, p.Position, entry.Category, out string why))
            {
                p.Reason = why;
                return p;
            }

            int limit = entry.Supply ? 0 : (PendingForFaction ? Plugin.MaxFactionUnits.Value : Plugin.MaxOwnUnits.Value);
            if (limit > 0 && purchased.Count >= limit)
            {
                p.Reason = L.T("Unit limit reached", "Достигнут лимит юнитов");
                return p;
            }

            if (GetFunds() < TotalPrice(entry))
            {
                p.Reason = L.T("Not enough money", "Недостаточно денег");
                return p;
            }

            p.Ok = true;
            p.Reason = L.T("OK", "Можно");
            return p;
        }

        private static bool IsAnchorUnit(Unit u)
        {
            return u is GroundVehicle || u is Ship || u is Building;
        }

        public static bool IsInTerritory(FactionHQ hq, GlobalPosition pos, UnitCategory cat, out string reason)
        {
            reason = L.T("Outside your territory", "Вне своей территории");
            Vector3 p = pos.AsVector3();
            float baseR = cat == UnitCategory.Naval ? Plugin.NavalAirbaseRadius.Value : Plugin.AirbaseRadius.Value;
            float unitR = Plugin.FriendlyUnitRadius.Value;
            float enemyR = Plugin.MinEnemyDistance.Value;
            bool inside = false;

            foreach (var ab in FactionRegistry.airbaseLookup.Values)
            {
                if (ab == null || ab.disabled || ab.CurrentHQ != hq || ab.center == null)
                {
                    continue;
                }
                if (FlatDistSq(ab.center.GlobalPosition().AsVector3(), p) <= baseR * baseR)
                {
                    inside = true;
                    break;
                }
            }

            var units = UnitRegistry.allUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.disabled || !IsAnchorUnit(u))
                {
                    continue;
                }
                var uhq = u.NetworkHQ;
                if (uhq == null)
                {
                    continue;
                }
                float d2 = FlatDistSq(u.GlobalPosition().AsVector3(), p);
                if (uhq == hq)
                {
                    if (!inside && d2 <= unitR * unitR)
                    {
                        inside = true;
                    }
                }
                else if (enemyR > 0f && d2 < enemyR * enemyR)
                {
                    reason = L.T("Too close to the enemy", "Слишком близко к врагу");
                    return false;
                }
            }
            return inside;
        }

        private static float FlatDistSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        public static float DefaultHeading(GlobalPosition from)
        {
            // face the nearest enemy airbase
            if (!TryGetLocal(out _, out var hq))
            {
                return 0f;
            }
            Vector3 p = from.AsVector3();
            float best = float.MaxValue;
            Vector3 dir = Vector3.forward;
            foreach (var ab in FactionRegistry.airbaseLookup.Values)
            {
                if (ab == null || ab.center == null || ab.CurrentHQ == null || ab.CurrentHQ == hq)
                {
                    continue;
                }
                Vector3 q = ab.center.GlobalPosition().AsVector3();
                float d = FlatDistSq(p, q);
                if (d < best)
                {
                    best = d;
                    dir = q - p;
                }
            }
            dir.y = 0f;
            if (dir.sqrMagnitude < 1f)
            {
                return 0f;
            }
            float h = Quaternion.LookRotation(dir).eulerAngles.y;
            float step = Mathf.Max(Plugin.RotateStep.Value, 1f);
            return Mathf.Round(h / step) * step;
        }

        // ---------- spawning ----------

        // options for the next KAR/BOTE purchase (set by the loadout window)
        public static Loadout PendingLoadout;
        public static float PendingExtraCost;
        /// <summary>true = hand the unit to the faction AI (it moves on its own)</summary>
        public static bool PendingForFaction;

        public static float TotalPrice(CatalogEntry e)
        {
            return e.Price + (e.Playable ? PendingExtraCost : 0f);
        }

        public static Unit Spawn(CatalogEntry entry, Placement placement) => SpawnInternal(entry, placement, true);

        public static Unit SpawnNoCharge(CatalogEntry entry, Placement placement) => SpawnInternal(entry, placement, false);

        public static void Refund(float amount) => Spend(-amount);

        private static Unit SpawnInternal(CatalogEntry entry, Placement placement, bool charge)
        {
            var spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null || !TryGetLocal(out _, out var hq))
            {
                return null;
            }
            string baseName = entry.Definition.unitPrefab.name + "_FC";
            string name;
            do
            {
                name = baseName + (++spawnCounter);
            }
            while (UnitRegistry.customIDLookup.ContainsKey(name));

            bool hold = !PendingForFaction && Plugin.SpawnHoldingPosition.Value;
            if (hold && Plugin.SupplyTrucksAutonomous.Value && Resupply.IsSupplyTruck(entry.Definition.unitPrefab))
            {
                // the game's supply-truck AI only starts when the truck is not holding position
                hold = false;
            }
            Unit unit;
            if (entry.Playable)
            {
                unit = SpawnPlayable(spawner, entry, placement, hq, name);
            }
            else if (entry.Definition is BuildingDefinition)
            {
                unit = spawner.SpawnBuilding(entry.Definition.unitPrefab, placement.Position, placement.Rotation, hq, null, name, Plugin.StaticCapturable.Value, null);
            }
            else if (entry.Static)
            {
                // containers: sit the box ON the ground (its pivot is in the middle) and keep it fixed,
                // otherwise the physics pushes it through the terrain
                var cpos = placement.Position;
                cpos.y += Mathf.Max(entry.Definition.height, 1f) * 0.5f + 0.2f;
                var container = spawner.SpawnContainer(entry.Definition.unitPrefab, cpos, placement.Rotation, hq, name);
                if (container != null)
                {
                    try
                    {
                        container.Networkkinematic = true;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Container kinematic: " + e.Message);
                    }
                }
                unit = container;
            }
            else if (entry.Category == UnitCategory.Naval)
            {
                unit = spawner.SpawnShip(entry.Definition.unitPrefab, placement.Position, placement.Rotation, hq, name, 1f, hold);
            }
            else
            {
                unit = spawner.SpawnVehicle(entry.Definition.unitPrefab, placement.Position, placement.Rotation, Vector3.zero, hq, name, 1f, hold, null);
            }
            if (unit != null)
            {
                if (charge) Spend(TotalPrice(entry));
                try
                {
                    UnitRegistry.RegisterCustomID(name, unit);
                }
                catch
                {
                }
                Plugin.Log.LogInfo($"Bought {entry.Name} for {Money(TotalPrice(entry))} at {placement.Position}");
            }
            return unit;
        }

        private static Unit SpawnPlayable(Spawner spawner, CatalogEntry entry, Placement placement, FactionHQ hq, string name)
        {
            if (!TryGetLocal(out var player, out _))
            {
                return null;
            }
            var def = (AircraftDefinition)entry.Definition;
            GlobalPosition pos = placement.Position;
            Quaternion rot = placement.Rotation;
            if (entry.Category == UnitCategory.Naval)
            {
                pos.y = Datum.SeaLevel.y + def.spawnOffset.y; // same as BOTE's own hangar spawn
            }
            else
            {
                rot *= Quaternion.Euler(def.restRotation); // height already resolved in Validate

            }
            Loadout loadout = PendingLoadout;
            var aircraft = spawner.SpawnAircraft(player, def.unitPrefab, loadout, 1f, default(LiveryKey), pos, rot, Vector3.zero, null, hq, name, 1f, 0.5f);
            if (aircraft != null)
            {
                if (loadout == null)
                {
                    try
                    {
                        aircraft.Networkloadout = aircraft.weaponManager.SelectAIAircraftWeapons(null);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Default loadout failed: " + e.Message);
                    }
                }
            }
            return aircraft;
        }

        // ---------- orders ----------

        public static bool GetHold(Unit u)
        {
            if (u is GroundVehicle gv) return gv.GetHoldPosition();
            if (u is Ship s) return s.holdPosition;
            return false;
        }

        public static void SetHold(Unit u, bool hold)
        {
            if (u is GroundVehicle gv) gv.SetHoldPosition(hold);
            else if (u is Ship s) s.SetHoldPosition(hold);
        }

        public static void MoveTo(Unit u, GlobalPosition pos)
        {
            if (u is ICommandable c && c.UnitCommand != null && !u.disabled)
            {
                c.UnitCommand.SetDestination(pos, true);
            }
        }

        public static void Stop(Unit u)
        {
            MoveTo(u, u.GlobalPosition());
        }
    }
}
