using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FieldCommand
{
    internal enum PlaceMode
    {
        None,
        Map,
        Camera
    }

    internal class FieldCommandController : MonoBehaviour
    {
        // ---- state ----
        private bool menuOpen;
        private Rect windowRect = new Rect(60, 80, 760, 700);
        private Vector2 scroll, ownedScroll;
        private int tab; // 0 ground, 1 naval, 2 playable, 3 my units
        private string search = "";
        private List<CatalogEntry> catalog;
        private float catalogBuiltAt = -999f;
        private CatalogEntry selected;
        private string catalogFaction;

        private PlaceMode mode = PlaceMode.None;
        private float heading;
        private bool headingSet;
        private bool repeatPlacement = true;
        private GameApi.Placement preview;
        private bool hasPreview;
        private Vector3 mouseDownPos;
        private bool mouseDownOnMap;

        private string toast;
        private float toastUntil;

        private readonly List<Unit> purchased = new List<Unit>();
        private readonly List<Unit> factionBought = new List<Unit>(); // handed to the faction AI
        private bool buyForFaction;
        private readonly Dictionary<Unit, float> paid = new Dictionary<Unit, float>();
        private Stratagem strike; // non-null while targeting a stratagem
        private readonly Dictionary<Unit, Unit> attackOrders = new Dictionary<Unit, Unit>();
        private float nextAttackUpdate;

        private GameObject ghost;
        private Material ghostMat;

        private GUIStyle okStyle, badStyle, boxStyle, headerStyle;

        // ================= Unity =================

        private Crew crew;

        private void Start()
        {
            crew = gameObject.AddComponent<Crew>();
            crew.Purchased = purchased;
            crew.Toast = t => Toast(t, 4f);
            KillReward.Toast = t => Toast(t, 3f);
        }

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }

        private void Tick()
        {
            bool inMission = GameApi.InMission();
            if (!inMission)
            {
                if (menuOpen || mode != PlaceMode.None || purchased.Count > 0)
                {
                    menuOpen = false;
                    ExitPlacement();
                    purchased.Clear();
                    factionBought.Clear();
                    KillReward.Reset();
                    Resupply.Reset();
                    paid.Clear();
                    Stratagems.ResetCooldowns();
                    attackOrders.Clear();
                    catalog = null;
                }
                return;
            }

            purchased.RemoveAll(u => u == null || u.disabled);
            factionBought.RemoveAll(u => u == null || u.disabled);
            foreach (var u in purchased) KillReward.Remember(u, false);
            foreach (var u in factionBought) KillReward.Remember(u, true);
            KillReward.Tick();
            Resupply.Tick(purchased, factionBought);

            if (Plugin.LeaveKey.Value.IsDown() && crew != null && mode == PlaceMode.None)
            {
                var mineNow = Crew.MyVehicle();
                if (mineNow != null && Crew.IsPlayable(mineNow))
                {
                    crew.Leave();
                }
            }

            if (Plugin.MenuKey.Value.IsDown())
            {
                if (mode != PlaceMode.None)
                {
                    ExitPlacement();
                }
                menuOpen = !menuOpen;
                if (menuOpen && (catalog == null || Time.time - catalogBuiltAt > 30f))
                {
                    RebuildCatalog();
                }
            }

            if (menuOpen && catalog != null)
            {
                GameApi.TryGetLocal(out _, out var hqNow);
                if (FactionResolver.FactionNameOf(hqNow) != catalogFaction)
                {
                    RebuildCatalog();
                }
            }

            if (menuOpen)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }

            switch (mode)
            {
                case PlaceMode.Map:
                    TickMapPlacement();
                    break;
                case PlaceMode.Camera:
                    TickCameraPlacement();
                    break;
            }

            if (mode == PlaceMode.None && DynamicMap.mapMaximized)
            {
                TickMapOrders();
            }

            TickAttackOrders();
            TickFocus();
        }

        private void RebuildCatalog()
        {
            try
            {
                GameApi.TryGetLocal(out _, out var hqNow);
                catalogFaction = FactionResolver.FactionNameOf(hqNow);
                catalog = GameApi.BuildCatalog();
                catalogBuiltAt = Time.time;
                try { Stratagems.LogMunitions(); } catch (Exception e) { Plugin.Log.LogWarning("[Stratagems] " + e.Message); }
                Plugin.Log.LogInfo($"Catalog ({FactionResolver.TagOf(catalogFaction)}): {catalog.Count(c => !c.Playable && c.Category == UnitCategory.Ground)} ground, {catalog.Count(c => !c.Playable && c.Category == UnitCategory.Naval)} naval, {catalog.Count(c => c.Playable)} playable, {catalog.Count(c => c.AllowedForMe)} available to me");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Catalog build failed: " + e);
                catalog = new List<CatalogEntry>();
            }
        }

        private void Toast(string text, float seconds = 3f)
        {
            toast = text;
            toastUntil = Time.unscaledTime + seconds;
        }

        // ================= placement =================

        private void BeginPlacement(CatalogEntry entry, PlaceMode m, bool keepPending = false)
        {
            strike = null;
            if (!keepPending)
            {
                GameApi.PendingLoadout = null;
                GameApi.PendingExtraCost = 0f;
            }
            selected = entry;
            mode = m;
            headingSet = false;
            hasPreview = false;
            menuOpen = false;
            if (m == PlaceMode.Map)
            {
                var map = SceneSingleton<DynamicMap>.i;
                if (map != null && !DynamicMap.mapMaximized)
                {
                    map.Maximize();
                }
                Toast(L.T("LMB — place, ", "ЛКМ — поставить, ") + Plugin.RotateKey.Value + L.T(" — rotate, ", " — повернуть, ") + Plugin.CancelKey.Value + L.T(" — cancel", " — отмена"), 5f);
            }
            else
            {
                var map = SceneSingleton<DynamicMap>.i;
                if (map != null && DynamicMap.mapMaximized)
                {
                    map.Minimize();
                }
                TrySwitchToFreeCamera();
                Toast(Plugin.CameraPlaceKey.Value + L.T(" — place at the aim point, ", " — поставить в прицел, ") + Plugin.RotateKey.Value + L.T(" — rotate, ", " — повернуть, ") + Plugin.CancelKey.Value + L.T(" — cancel", " — отмена"), 5f);
            }
        }

        private static string Lim(int v) => v > 0 ? "/" + v : "";

        /// <summary>Slider and −/+ buttons for a unit limit (1..Plugin.MaxUnitLimit); saved to the config at once.</summary>
        private static void DrawLimitRow(string label, BepInEx.Configuration.ConfigEntry<int> entry, int alive)
        {
            int max = Plugin.MaxUnitLimit;
            int v = Mathf.Clamp(entry.Value, 1, max);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(160));
            if (GUILayout.Button("−10", GUILayout.Width(38))) v -= 10;
            if (GUILayout.Button("−", GUILayout.Width(26))) v -= 1;
            v = Mathf.RoundToInt(GUILayout.HorizontalSlider(v, 1, max, GUILayout.Width(230), GUILayout.Height(18)));
            if (GUILayout.Button("+", GUILayout.Width(26))) v += 1;
            if (GUILayout.Button("+10", GUILayout.Width(38))) v += 10;
            v = Mathf.Clamp(v, 1, max);
            GUILayout.Label(L.T($"{v} (now {alive})", $"{v} (сейчас {alive})"), GUILayout.Width(120));
            GUILayout.EndHorizontal();
            if (v != entry.Value) entry.Value = v;
        }

        private List<Unit> AllBought()
        {
            var l = new List<Unit>(purchased.Count + factionBought.Count);
            l.AddRange(purchased);
            l.AddRange(factionBought);
            return l;
        }

        private void ExitPlacement()
        {
            mode = PlaceMode.None;
            strike = null;
            hasPreview = false;
            if (ghost != null)
            {
                ghost.SetActive(false);
            }
        }

        private void TrySwitchToFreeCamera()
        {
            try
            {
                if (GameManager.GetLocalAircraft(out _))
                {
                    return; // flying: keep the current camera, aim with screen centre
                }
                var cam = SceneSingleton<CameraStateManager>.i;
                if (cam != null && cam.currentState != cam.freeState)
                {
                    cam.SwitchState(cam.freeState);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Free camera switch failed: " + e.Message);
            }
        }

        private bool HandleCommonPlacementKeys()
        {
            if (Input.GetKeyDown(Plugin.CancelKey.Value) || Input.GetKeyDown(KeyCode.Backspace))
            {
                ExitPlacement();
                Toast(L.T("Placement cancelled", "Расстановка отменена"));
                return true;
            }
            if (strike != null && strike.HasArea)
            {
                if (Input.GetKeyDown(KeyCode.T)) strike.Point = !strike.Point;
                if (Input.GetKeyDown(KeyCode.RightBracket) || Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))
                {
                    strike.Point = false;
                    strike.Radius = Mathf.Min(strike.Radius * 1.25f, Stratagems.MaxRadius);
                }
                if (Input.GetKeyDown(KeyCode.LeftBracket) || Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
                {
                    strike.Point = false;
                    strike.Radius = Mathf.Max(strike.Radius / 1.25f, Stratagems.MinRadius);
                }
            }
            if (Input.GetKeyDown(Plugin.RotateKey.Value))
            {
                float step = Plugin.RotateStep.Value;
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                heading = Mathf.Repeat(heading + (shift ? -step : step), 360f);
                headingSet = true;
            }
            return false;
        }

        private void UpdatePreview(GlobalPosition cursor)
        {
            if (!headingSet)
            {
                heading = GameApi.DefaultHeading(cursor);
            }
            if (strike != null)
            {
                selected.Price = strike.CurrentPrice();
                preview = Stratagems.Validate(strike, cursor, heading);
                hasPreview = true;
                return;
            }
            GameApi.PendingForFaction = buyForFaction && !selected.Playable;
            preview = GameApi.Validate(selected, cursor, heading, GameApi.PendingForFaction ? factionBought : purchased);
            hasPreview = true;
        }

        private void TryPlace()
        {
            if (!hasPreview)
            {
                return;
            }
            if (!preview.Ok)
            {
                Toast(L.T("Not allowed: ", "Нельзя: ") + preview.Reason);
                return;
            }
            if (strike != null)
            {
                var st = strike;
                Stratagems.Launch(this, st, preview, (u, price) =>
                {
                    purchased.Add(u);
                    paid[u] = price;
                }, t => Toast(t, 4f));
                ExitPlacement();
                return;
            }
            var unit = GameApi.Spawn(selected, preview);
            if (unit == null)
            {
                Toast(L.T("Failed to spawn ", "Не удалось заспавнить ") + selected.Name);
                return;
            }
            if (selected.Supply)
            {
                // ammo / fuel / repair containers and depots are just objects — no unit slot, not in the lists
                Toast(L.T($"Placed: {selected.Name} for {GameApi.Money(GameApi.TotalPrice(selected))}", $"Поставлено: {selected.Name} за {GameApi.Money(GameApi.TotalPrice(selected))}"));
                bool sh = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (!repeatPlacement && !sh) ExitPlacement();
                return;
            }
            paid[unit] = GameApi.TotalPrice(selected);
            if (GameApi.PendingForFaction)
            {
                factionBought.Add(unit);
                Toast(L.T($"Bought for the faction: {selected.Name} for {GameApi.Money(GameApi.TotalPrice(selected))} — the AI controls it", $"Куплено для фракции: {selected.Name} за {GameApi.Money(GameApi.TotalPrice(selected))} — им управляет ИИ"));
            }
            else
            {
                purchased.Add(unit);
                Toast(L.T($"Bought for you: {selected.Name} for {GameApi.Money(GameApi.TotalPrice(selected))}", $"Куплено себе: {selected.Name} за {GameApi.Money(GameApi.TotalPrice(selected))}"));
            }
            if (selected.Playable)
            {
                ExitPlacement(); // you are inside it now
            }
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!repeatPlacement && !shift)
            {
                ExitPlacement();
            }
        }

        private void TickMapPlacement()
        {
            var map = SceneSingleton<DynamicMap>.i;
            if (map == null)
            {
                ExitPlacement();
                return;
            }
            if (!DynamicMap.mapMaximized)
            {
                // map closed by the player -> leave placement
                ExitPlacement();
                return;
            }
            if (HandleCommonPlacementKeys())
            {
                return;
            }
            if (!map.TryGetCursorCoordinates(out var cursor))
            {
                hasPreview = false;
                return;
            }
            UpdatePreview(cursor);

            // click (not drag, so map panning still works)
            if (Input.GetMouseButtonDown(0))
            {
                mouseDownPos = Input.mousePosition;
                mouseDownOnMap = !IsOverOwnGui(Input.mousePosition);
            }
            if (Input.GetMouseButtonUp(0) && mouseDownOnMap)
            {
                mouseDownOnMap = false;
                if ((Input.mousePosition - mouseDownPos).sqrMagnitude < 64f)
                {
                    TryPlace();
                }
            }
        }

        private void TickCameraPlacement()
        {
            if (DynamicMap.mapMaximized)
            {
                hasPreview = false;
                ShowGhost(false);
                if (HandleCommonPlacementKeys()) return;
                return;
            }
            if (HandleCommonPlacementKeys())
            {
                return;
            }
            Camera cam = GetCamera();
            if (cam == null)
            {
                hasPreview = false;
                return;
            }
            Vector3 screen = Cursor.visible && Cursor.lockState == CursorLockMode.None
                ? Input.mousePosition
                : new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
            if (!RaycastWorld(cam.ScreenPointToRay(screen), out Vector3 point))
            {
                hasPreview = false;
                ShowGhost(false);
                return;
            }
            UpdatePreview(point.ToGlobalPosition());
            UpdateGhost();

            if (Input.GetKeyDown(Plugin.CameraPlaceKey.Value) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                TryPlace();
            }
        }

        private static Camera GetCamera()
        {
            try
            {
                var csm = SceneSingleton<CameraStateManager>.i;
                if (csm != null && csm.mainCamera != null)
                {
                    return csm.mainCamera;
                }
            }
            catch
            {
            }
            return Camera.main;
        }

        private static bool RaycastWorld(Ray ray, out Vector3 point)
        {
            var sea = new Plane(Vector3.up, new Vector3(0f, Datum.LocalSeaY, 0f));
            if (Physics.Raycast(ray, out var hit, 60000f, PhysicsLayers.StaticsMask))
            {
                point = hit.point;
                if (point.y < Datum.LocalSeaY && sea.Raycast(ray, out float e))
                {
                    point = ray.GetPoint(e);
                }
                return true;
            }
            if (sea.Raycast(ray, out float enter) && enter < 60000f)
            {
                point = ray.GetPoint(enter);
                return true;
            }
            point = default;
            return false;
        }

        // ---- 3D ghost marker ----

        private void ShowGhost(bool show)
        {
            if (ghost != null && ghost.activeSelf != show)
            {
                ghost.SetActive(show);
            }
        }

        private void UpdateGhost()
        {
            if (!hasPreview || strike != null || selected?.Definition == null)
            {
                ShowGhost(false);
                return;
            }
            if (ghost == null)
            {
                CreateGhost();
                if (ghost == null) return;
            }
            var def = selected.Definition;
            float w = Mathf.Max(def.width, 3f), l = Mathf.Max(def.length, 5f), h = Mathf.Max(def.height, 2f);
            Vector3 pos = preview.Position.ToLocalPosition();
            if (selected.Category == UnitCategory.Naval)
            {
                pos.y = Datum.LocalSeaY;
            }
            ghost.transform.position = pos + Vector3.up * (h * 0.5f);
            ghost.transform.rotation = preview.Rotation;
            ghost.transform.localScale = new Vector3(w, h, l);
            if (ghostMat != null)
            {
                ghostMat.color = preview.Ok ? new Color(0.2f, 1f, 0.3f, 0.45f) : new Color(1f, 0.2f, 0.2f, 0.45f);
            }
            ShowGhost(true);
        }

        private void CreateGhost()
        {
            try
            {
                ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ghost.name = "[FieldCommand Ghost]";
                var col = ghost.GetComponent<Collider>();
                if (col != null) Destroy(col);
                // nose marker so heading is visible
                var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var ncol = nose.GetComponent<Collider>();
                if (ncol != null) Destroy(ncol);
                nose.transform.SetParent(ghost.transform, false);
                nose.transform.localPosition = new Vector3(0f, 0.6f, 0.55f);
                nose.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);

                Shader sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                var r = ghost.GetComponent<Renderer>();
                if (sh != null && r != null)
                {
                    ghostMat = new Material(sh);
                    r.sharedMaterial = ghostMat;
                    nose.GetComponent<Renderer>().sharedMaterial = ghostMat;
                }
                DontDestroyOnLoad(ghost);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ghost creation failed: " + e.Message);
                ghost = null;
            }
        }

        // ================= orders on the map =================

        private List<Unit> SelectedFriendlyCommandables()
        {
            var list = new List<Unit>();
            var map = SceneSingleton<DynamicMap>.i;
            if (map == null || !GameApi.TryGetLocal(out _, out var hq))
            {
                return list;
            }
            foreach (var icon in map.selectedIcons)
            {
                if (icon is UnitMapIcon ui && ui.unit != null && !ui.unit.disabled && ui.unit.NetworkHQ == hq && ui.unit is ICommandable)
                {
                    list.Add(ui.unit);
                }
            }
            return list;
        }

        private void TickMapOrders()
        {
            bool hold = Input.GetKeyDown(Plugin.HoldKey.Value);
            bool attack = Input.GetKeyDown(Plugin.AttackKey.Value);
            bool defense = Input.GetKeyDown(Plugin.MissileDefenseKey.Value);
            if (Input.GetKeyDown(Plugin.ScrapKey.Value))
            {
                var mineSel = SelectedFriendlyCommandables().Where(u => purchased.Contains(u) || factionBought.Contains(u)).ToList();
                if (mineSel.Count == 0)
                {
                    Toast(L.T("Select your purchased units on the map", "Выдели на карте своих купленных юнитов"));
                    return;
                }
                float total = mineSel.Sum(RefundFor);
                foreach (var u in mineSel) Scrap(u, false);
                Toast(L.T($"Scrapped units: {mineSel.Count}, refund {GameApi.Money(total)}", $"Списано юнитов: {mineSel.Count}, возврат {GameApi.Money(total)}"));
                return;
            }
            if (!hold && !attack && !defense)
            {
                // a vanilla right-click move order cancels "approach the target", but not the fire order
                if (Input.GetMouseButtonDown(1))
                {
                    foreach (var u in SelectedFriendlyCommandables())
                    {
                        attackOrders.Remove(u);
                    }
                }
                return;
            }
            var sel = SelectedFriendlyCommandables();
            if (sel.Count == 0)
            {
                Toast(L.T("Select your units on the map", "Выдели свои юниты на карте"));
                return;
            }
            if (hold)
            {
                bool newHold = !sel.All(GameApi.GetHold);
                foreach (var u in sel)
                {
                    attackOrders.Remove(u);
                    GameApi.SetHold(u, newHold);
                    if (newHold)
                    {
                        GameApi.Stop(u);
                    }
                }
                Toast(newHold ? L.T($"Hold position: {sel.Count}", $"Держать позицию: {sel.Count}") : L.T($"Free to move: {sel.Count}", $"Свободный режим: {sel.Count}"));
                return;
            }
            if (defense)
            {
                bool on = !sel.All(u => FocusFire.MissileDefense.Contains(u));
                foreach (var u in sel)
                {
                    if (on) FocusFire.MissileDefense.Add(u);
                    else FocusFire.MissileDefense.Remove(u);
                }
                int capable = sel.Count(u => FocusFire.MaxRange(u) > 0f);
                Toast(on ? L.T($"Missile defence ON: {sel.Count} (nuclear and ballistic first)", $"Перехват ракет ВКЛ: {sel.Count} (ядерные и баллистические — в первую очередь)") : L.T($"Missile defence OFF: {sel.Count}", $"Перехват ракет ВЫКЛ: {sel.Count}"));
                return;
            }
            var map = SceneSingleton<DynamicMap>.i;
            if (!map.TryGetCursorCoordinates(out var cursor) || !GameApi.TryGetLocal(out _, out var hq))
            {
                return;
            }
            float radius = 2500f;
            try
            {
                float metersPerPixel = map.mapDimension / (900f * map.mapImage.transform.lossyScale.x);
                if (metersPerPixel > 0f) radius = Mathf.Clamp(45f * metersPerPixel, 150f, 8000f);
            }
            catch
            {
            }
            Unit target = FindTargetNear(hq, cursor, radius);
            if (target == null)
            {
                foreach (var u in sel)
                {
                    FocusFire.Clear(u);
                    attackOrders.Remove(u);
                }
                Toast(L.T($"Target cleared ({sel.Count}). To pick a target, point at a visible enemy or missile", $"Цель сброшена ({sel.Count}). Чтобы выбрать цель — наведи курсор на видимого врага или ракету"));
                return;
            }
            bool approach = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            int inRange = 0;
            foreach (var u in sel)
            {
                FocusFire.Orders[u] = target;
                FocusFire.Nudge(u);
                if (FocusFire.CanEngageNow(u, target)) inRange++;
                if (approach && !(target is Missile)) attackOrders[u] = target;
                else attackOrders.Remove(u);
            }
            nextAttackUpdate = 0f;
            string tname = target is Missile ? L.T("missile ", "ракета ") + (target.definition?.unitName ?? "") : (target.definition?.unitName ?? target.name);
            string tail = approach ? L.T(", the rest advance to firing range", ", остальные выдвигаются на дистанцию огня") : (inRange < sel.Count ? L.T(", the rest open fire when the target is in range (Shift+", ", остальные откроют огонь, когда цель войдёт в зону (Shift+") + Plugin.AttackKey.Value + L.T(" — advance)", " — выдвинуться)") : "");
            Toast(L.T($"Firing at: {tname}. In range: {inRange} of {sel.Count}{tail}", $"Огонь по: {tname}. В зоне досягаемости {inRange} из {sel.Count}{tail}"));
        }

        private static Unit FindTargetNear(FactionHQ hq, GlobalPosition pos, float radius)
        {
            Unit best = null;
            float bestScore = float.MaxValue;
            Vector3 p = pos.AsVector3();
            foreach (var u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled || u.NetworkHQ == null || u.NetworkHQ == hq)
                {
                    continue;
                }
                if (!(u is GroundVehicle || u is Ship || u is Building || u is Missile || u is Aircraft))
                {
                    continue;
                }
                bool tracked = true;
                try
                {
                    tracked = hq.IsTargetBeingTracked(u);
                }
                catch
                {
                }
                if (!tracked)
                {
                    continue;
                }
                Vector3 q = u.GlobalPosition().AsVector3();
                float dx = q.x - p.x, dz = q.z - p.z, d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d > radius * (u is Missile ? 1.5f : 1f)) continue;
                float score = u is Missile ? d * 0.5f : d; // missiles are small and fast — prefer them
                if (score < bestScore)
                {
                    bestScore = score;
                    best = u;
                }
            }
            return best;
        }

        private float nextFocusTick;

        private void TickFocus()
        {
            if (Time.time < nextFocusTick) return;
            nextFocusTick = Time.time + 0.5f;
            FocusFire.Cleanup();
            FocusFire.UpdateMissileDefense();
            foreach (var u in FocusFire.Orders.Keys.Concat(FocusFire.MissileDefense).Distinct().ToList())
            {
                FocusFire.Nudge(u);
            }
        }

        private void TickAttackOrders()
        {
            if (attackOrders.Count == 0 || Time.time < nextAttackUpdate)
            {
                return;
            }
            nextAttackUpdate = Time.time + 5f;
            var done = new List<Unit>();
            foreach (var kv in attackOrders)
            {
                Unit u = kv.Key, t = kv.Value;
                if (u == null || u.disabled || t == null || t.disabled)
                {
                    done.Add(u);
                    continue;
                }
                if (FocusFire.CanEngageNow(u, t))
                {
                    continue; // already in range — stand and shoot, don't drive closer
                }
                float reach = FocusFire.MaxRange(u);
                float standoff = u is Ship ? Plugin.NavalAttackStandoff.Value : Plugin.GroundAttackStandoff.Value;
                if (reach > 0f) standoff = Mathf.Min(standoff, reach * 0.85f);
                Vector3 up = u.GlobalPosition().AsVector3();
                Vector3 tp = t.GlobalPosition().AsVector3();
                Vector3 dir = tp - up;
                dir.y = 0f;
                float dist = dir.magnitude;
                if (dist <= standoff)
                {
                    continue;
                }
                GameApi.SetHold(u, false);
                Vector3 dest = tp - dir / Mathf.Max(dist, 1f) * standoff;
                GameApi.MoveTo(u, new GlobalPosition(dest.x, 0f, dest.z));
            }
            foreach (var u in done)
            {
                attackOrders.Remove(u);
            }
        }

        // ================= GUI =================

        private Rect HudRect => new Rect(Screen.width * 0.5f - 300f, 10f, 600f, 92f);

        private bool IsOverOwnGui(Vector3 mouse)
        {
            Vector2 gui = new Vector2(mouse.x, Screen.height - mouse.y);
            return (mode != PlaceMode.None && HudRect.Contains(gui)) || (menuOpen && windowRect.Contains(gui)) || (menuOpen && draft != null && loadoutRect.Contains(gui));
        }

        private void EnsureStyles()
        {
            if (okStyle != null) return;
            okStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 14 };
            okStyle.normal.textColor = new Color(0.4f, 1f, 0.4f);
            badStyle = new GUIStyle(okStyle);
            badStyle.normal.textColor = new Color(1f, 0.4f, 0.35f);
            boxStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = true };
            boxStyle.normal.textColor = Color.white;
            headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
        }

        private void OnGUI()
        {
            if (!GameApi.InMission())
            {
                return;
            }
            EnsureStyles();

            if (menuOpen)
            {
                windowRect = GUILayout.Window(0x46434D, windowRect, DrawWindow, L.T("ORTIZ Ops - FieldCommand — buy units", "ORTIZ Ops - FieldCommand — покупка техники"));
                if (draft != null)
                {
                    loadoutRect = GUILayout.Window(0x46434E, loadoutRect, DrawLoadoutWindow, L.T("Modules and weapons", "Модули и вооружение"));
                }
            }

            if (mode != PlaceMode.None && selected != null)
            {
                DrawPlacementHud();
                if (hasPreview)
                {
                    if (mode == PlaceMode.Map) DrawMapCursor();
                    else DrawCameraMarker();
                }
            }

            if (toast != null && Time.unscaledTime < toastUntil)
            {
                var r = new Rect(Screen.width * 0.5f - 250f, Screen.height - 120f, 500f, 32f);
                GUI.Box(r, toast, boxStyle);
            }
        }

        private void DrawPlacementHud()
        {
            var r = HudRect;
            string how = mode == PlaceMode.Map
                ? L.T($"LMB — place | {Plugin.RotateKey.Value} — rotate | {Plugin.CancelKey.Value} — exit", $"ЛКМ — поставить | {Plugin.RotateKey.Value} — поворот | {Plugin.CancelKey.Value} — выход")
                : L.T($"{Plugin.CameraPlaceKey.Value}/Enter — place at the aim point | {Plugin.RotateKey.Value} — rotate | {Plugin.CancelKey.Value} — exit", $"{Plugin.CameraPlaceKey.Value}/Enter — поставить в прицел | {Plugin.RotateKey.Value} — поворот | {Plugin.CancelKey.Value} — выход");
            if (strike != null && strike.HasArea)
            {
                how = (strike.Point ? L.T("POINT", "ТОЧЕЧНО") : L.T($"AREA R={strike.Spread:0} m", $"ОБЛАСТЬ R={strike.Spread:0} м")) + L.T(" | T — point/area | [ ] or − + — radius | ", " | T — точечно/область | [ ] или − + — радиус | ") + how;
            }
            GUI.Box(r, "", boxStyle);
            GUILayout.BeginArea(new Rect(r.x + 8, r.y + 4, r.width - 16, r.height - 8));
            GUILayout.Label(L.T($"{selected.Name}  —  {GameApi.Money(GameApi.TotalPrice(selected))}    Budget: {GameApi.Money(GameApi.GetFunds())}    Heading: {heading:0}°", $"{selected.Name}  —  {GameApi.Money(GameApi.TotalPrice(selected))}    Бюджет: {GameApi.Money(GameApi.GetFunds())}    Курс: {heading:0}°"), headerStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label(how);
            repeatPlacement = GUILayout.Toggle(repeatPlacement, L.T("Place more", "Ставить ещё"), GUILayout.Width(110));
            GUILayout.EndHorizontal();
            if (!selected.Playable && strike == null)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Toggle(!buyForFaction, L.T(" For me (my orders)", " Себе (мои приказы)"), GUILayout.Width(170))) buyForFaction = false;
                if (GUILayout.Toggle(buyForFaction, L.T(" For the faction (AI controls)", " Фракции (ИИ сам управляет)"), GUILayout.Width(220))) buyForFaction = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }

        private void DrawMapCursor()
        {
            Vector3 m = Input.mousePosition;
            Vector2 c = new Vector2(m.x, Screen.height - m.y);
            DrawHeadingArrow(c, heading, preview.Ok ? Color.green : Color.red);
            if (strike != null && strike.HasArea)
            {
                var map = SceneSingleton<DynamicMap>.i;
                if (map != null)
                {
                    float mpp = map.mapDimension / (900f * map.mapImage.transform.lossyScale.x); // metres per screen pixel
                    Color col = preview.Ok ? new Color(1f, 0.55f, 0.1f) : Color.red;
                    if (strike.Kind == StratagemKind.Carpet)
                    {
                        float half = (strike.Point ? 100f : strike.Spread) / mpp;
                        float a = heading * Mathf.Deg2Rad;
                        Vector2 d = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
                        for (int i = -12; i <= 12; i++) Dot(c + d * (half * i / 12f), col, 4f);
                        for (int i = 0; i < 14; i++) Dot(c + d * (-half + 2f * half * i / 13f), Color.white, 6f);
                        Vector2 end = c + d * half, perp = new Vector2(-d.y, d.x);
                        for (int j = 0; j <= 8; j++)
                        {
                            float t = j / 8f;
                            Dot(Vector2.Lerp(end - d * 14f + perp * 9f, end, t), col, 4f);
                            Dot(Vector2.Lerp(end - d * 14f - perp * 9f, end, t), col, 4f);
                        }
                        Vector2 st = c - d * half;
                        GUI.Label(new Rect(st.x - 30, st.y + 4, 120, 22), L.T("START", "НАЧАЛО"), okStyle);
                        GUI.Label(new Rect(end.x - 20, end.y + 4, 120, 22), L.T("END", "КОНЕЦ"), badStyle);
                    }
                    else
                    {
                        DrawCircle(c, Mathf.Max(strike.Spread / mpp, 4f), col);
                    }
                }
            }
            GUI.Label(new Rect(c.x + 18, c.y + 6, 360, 24), preview.Ok ? "OK: " + selected.Name : L.T("NOT ALLOWED: ", "НЕЛЬЗЯ: ") + preview.Reason, preview.Ok ? okStyle : badStyle);
        }

        private static void Dot(Vector2 p, Color col, float size)
        {
            Color old = GUI.color;
            GUI.color = col;
            GUI.DrawTexture(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void DrawCircle(Vector2 c, float rPx, Color col)
        {
            int n = Mathf.Clamp((int)(rPx * 0.6f), 16, 96);
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                Dot(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rPx, col, 3f);
            }
        }

        private void DrawCameraMarker()
        {
            var cam = GetCamera();
            if (cam == null) return;
            // crosshair at screen centre when the cursor is locked
            if (!(Cursor.visible && Cursor.lockState == CursorLockMode.None))
            {
                GUI.Label(new Rect(Screen.width * 0.5f - 6, Screen.height * 0.5f - 12, 20, 24), "+", headerStyle);
            }
            Vector3 centre = preview.Position.ToLocalPosition();
            if (strike != null && strike.HasArea && strike.Kind != StratagemKind.Carpet)
            {
                Color col = preview.Ok ? new Color(1f, 0.55f, 0.1f) : Color.red;
                float r = strike.Spread;
                for (int i = 0; i < 64; i++)
                {
                    float a = i * Mathf.PI * 2f / 64f;
                    Vector3 w = centre + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (Stratagems.TryGroundY(w, out float gy)) w.y = gy + 1f;
                    Vector3 s2 = cam.WorldToScreenPoint(w);
                    if (s2.z > 0f) Dot(new Vector2(s2.x, Screen.height - s2.y), col, 3f);
                }
            }
            if (strike != null && strike.Kind == StratagemKind.Carpet)
            {
                DrawCarpetInCamera(cam, centre);
            }
            Vector3 sp = cam.WorldToScreenPoint(centre);
            if (sp.z <= 0f) return;
            Vector2 c = new Vector2(sp.x, Screen.height - sp.y);
            GUI.Label(new Rect(c.x + 16, c.y - 30, 360, 24), preview.Ok ? "OK: " + selected.Name : L.T("NOT ALLOWED: ", "НЕЛЬЗЯ: ") + preview.Reason, preview.Ok ? okStyle : badStyle);
        }

        private bool ToScreen(Camera cam, Vector3 world, out Vector2 gui)
        {
            Vector3 s2 = cam.WorldToScreenPoint(world);
            gui = new Vector2(s2.x, Screen.height - s2.y);
            return s2.z > 0f;
        }

        private Vector3 OnGround(Vector3 w)
        {
            // cheap terrain raycast only (this runs for ~130 points every GUI pass)
            if (PathfindingAgent.RaycastTerrain(w, out var hit)) w.y = Mathf.Max(hit.point.y, Datum.LocalSeaY) + 1.5f;
            return w;
        }

        /// <summary>Bomb line in the 3D view: every impact point, arrows along the flight direction, START and END.</summary>
        private void DrawCarpetInCamera(Camera cam, Vector3 centre)
        {
            Color col = preview.Ok ? new Color(1f, 0.55f, 0.1f) : Color.red;
            Vector3 fwd = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, fwd);
            float len = strike.Point ? 200f : strike.Spread * 2f;
            Vector3 a = centre - fwd * (len * 0.5f), b = centre + fwd * (len * 0.5f);
            // dense line
            for (int i = 0; i <= 60; i++)
            {
                if (ToScreen(cam, OnGround(Vector3.Lerp(a, b, i / 60f)), out var g)) Dot(g, col, 2f);
            }
            // the 14 bomb impacts
            for (int i = 0; i < 14; i++)
            {
                if (ToScreen(cam, OnGround(Vector3.Lerp(a, b, i / 13f)), out var g)) Dot(g, Color.white, 6f);
            }
            // chevrons ">" pointing the way the bombs walk
            for (int k = 1; k <= 4; k++)
            {
                Vector3 tip = Vector3.Lerp(a, b, k / 5f);
                Vector3 back = tip - fwd * Mathf.Max(len * 0.04f, 8f);
                for (int j = 0; j <= 6; j++)
                {
                    float t = j / 6f;
                    if (ToScreen(cam, OnGround(Vector3.Lerp(back + side * Mathf.Max(len * 0.03f, 6f), tip, t)), out var g1)) Dot(g1, col, 3f);
                    if (ToScreen(cam, OnGround(Vector3.Lerp(back - side * Mathf.Max(len * 0.03f, 6f), tip, t)), out var g2)) Dot(g2, col, 3f);
                }
            }
            if (ToScreen(cam, OnGround(a), out var ga)) GUI.Label(new Rect(ga.x - 30, ga.y - 26, 120, 22), L.T("START", "НАЧАЛО"), okStyle);
            if (ToScreen(cam, OnGround(b), out var gb)) GUI.Label(new Rect(gb.x - 20, gb.y - 26, 120, 22), L.T("END ►", "КОНЕЦ ►"), badStyle);
        }

        private static void DrawHeadingArrow(Vector2 centre, float headingDeg, Color color)
        {
            // map is north-up: heading 0 = up on screen
            Color old = GUI.color;
            GUI.color = color;
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(headingDeg, centre);
            GUI.DrawTexture(new Rect(centre.x - 1.5f, centre.y - 26f, 3f, 26f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(centre.x - 7f, centre.y - 28f, 14f, 3f), Texture2D.whiteTexture);
            GUI.matrix = m;
            GUI.DrawTexture(new Rect(centre.x - 5f, centre.y - 5f, 10f, 10f), Texture2D.whiteTexture);
            GUI.color = old;
        }

        private int groupFilter; // 0 = all
        private int lastTabForFilter = -1;

        private void DrawWindow(int id)
        {
            GameApi.TryGetLocal(out _, out var hq);
            GUILayout.BeginHorizontal();
            string fundsKind = Plugin.Funds.Value == FundsSource.Player ? L.T("personal", "личный") : L.T("faction", "фракции");
            GUILayout.Label(L.T($"Budget ({fundsKind}): {GameApi.Money(GameApi.GetFunds())}", $"Бюджет ({fundsKind}): {GameApi.Money(GameApi.GetFunds())}"), headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(L.T("Faction: ", "Фракция: ") + FactionResolver.TagOf(FactionResolver.FactionNameOf(hq)), headerStyle);
            GUILayout.Space(10);
            GUILayout.Label(L.T($"Mine: {purchased.Count}{Lim(Plugin.MaxOwnUnits.Value)}  Faction: {factionBought.Count}{Lim(Plugin.MaxFactionUnits.Value)}", $"Мои: {purchased.Count}{Lim(Plugin.MaxOwnUnits.Value)}  Фракции: {factionBought.Count}{Lim(Plugin.MaxFactionUnits.Value)}"));
            if (Plugin.KillRewardPercent.Value > 0f)
            {
                GUILayout.Label(L.T($"  Earned: {GameApi.Money(KillReward.SessionTotal)}", $"  Заработали: {GameApi.Money(KillReward.SessionTotal)}"));
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("Buy:", "Покупать:"), GUILayout.Width(70));
            if (GUILayout.Toggle(!buyForFaction, L.T(" For me — they hold and wait for my orders", " Себе — стоят на месте и ждут моих приказов"), GUILayout.Width(330))) buyForFaction = false;
            if (GUILayout.Toggle(buyForFaction, L.T(" For the faction — the AI decides where to go", " Фракции — ИИ сам решает, куда ехать"), GUILayout.Width(290))) buyForFaction = true;
            GUILayout.EndHorizontal();
            DrawLimitRow(L.T("My unit limit:", "Лимит своих юнитов:"), Plugin.MaxOwnUnits, purchased.Count);
            DrawLimitRow(L.T("Faction unit limit:", "Лимит юнитов фракции:"), Plugin.MaxFactionUnits, factionBought.Count);

            if (!GameApi.IsServer())
            {
                GUILayout.Label(L.T("The mod works only in single-player (or when you are the host).", "Мод работает только в одиночной игре (или если ты хост)."), badStyle);
            }

            tab = GUILayout.Toolbar(tab, new[] { L.T("Ground", "Наземная"), L.T("Naval", "Флот"), L.T("Defences/depots", "Оборона/склады"), L.T("Playable", "Играбельная"), L.T($"Mine ({purchased.Count})", $"Мои ({purchased.Count})"), L.T("Stratagems", "Стратегемы") });
            if (tab != lastTabForFilter)
            {
                groupFilter = 0;
                lastTabForFilter = tab;
            }

            if (tab == 4)
            {
                DrawOwned();
            }
            else if (tab == 5)
            {
                DrawStratagems();
            }
            else
            {
                DrawCatalog(tab);
            }

            GUILayout.Space(4);
            GUILayout.Label(L.T($"Map: select your units → RMB — move (Shift — waypoints), {Plugin.HoldKey.Value} — hold position, {Plugin.AttackKey.Value} — fire at the target/missile under the cursor (from place; Shift+{Plugin.AttackKey.Value} — advance; {Plugin.AttackKey.Value} on empty ground — clear), {Plugin.MissileDefenseKey.Value} — automatic missile defence, {Plugin.ScrapKey.Value} — scrap.", $"Карта: выдели свои юниты → ПКМ — двигаться (Shift — маршрут), {Plugin.HoldKey.Value} — держать позицию, {Plugin.AttackKey.Value} — огонь по цели/ракете под курсором (с места; Shift+{Plugin.AttackKey.Value} — выдвинуться; {Plugin.AttackKey.Value} по пустому месту — сбросить), {Plugin.MissileDefenseKey.Value} — авто-перехват ракет, {Plugin.ScrapKey.Value} — списать."));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("Refresh list", "Обновить список")))
            {
                RebuildCatalog();
            }
            if (GUILayout.Button(L.Ru ? "Language: English" : "Язык: русский", GUILayout.Width(150)))
            {
                L.Language.Value = L.Ru ? UiLanguage.English : UiLanguage.Russian;
            }
            if (GUILayout.Button(L.T("Close (", "Закрыть (") + Plugin.MenuKey.Value + ")"))
            {
                menuOpen = false;
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private bool InTab(CatalogEntry e, int t)
        {
            if (t == 3) return e.Playable;
            if (t == 2) return e.Static;
            if (e.Playable || e.Static) return false;
            return t == 0 ? e.Category == UnitCategory.Ground : e.Category == UnitCategory.Naval;
        }

        private bool Visible(CatalogEntry e)
        {
            if (e.AllowedForMe) return true;
            return Plugin.ShowOtherFaction.Value && e.Faction != null;
        }

        private void DrawCatalog(int t)
        {
            if (catalog == null)
            {
                return;
            }
            var groups = catalog.Where(e => InTab(e, t) && Visible(e)).Select(e => e.GroupName).Distinct().ToList();
            if (t == 3 && groups.Count == 0)
            {
                GUILayout.Label(L.T("No playable vehicles found (the KAR or BOTE mods are needed).", "Не найдено техники, за которую можно играть (нужны моды KAR или BOTE)."));
            }
            if (t != 3 && groups.Count > 1)
            {
                var labels = new List<string> { L.T("All", "Все") };
                labels.AddRange(groups);
                groupFilter = Mathf.Clamp(groupFilter, 0, labels.Count - 1);
                groupFilter = GUILayout.SelectionGrid(groupFilter, labels.ToArray(), 3);
            }
            if (t == 3)
            {
                GUILayout.Label(L.T("Pick modules, buy — and you appear inside right away (you must not be in another vehicle).", "Выбираешь модули, покупаешь — и сразу появляешься в технике (нужно не сидеть в другой)."), okStyle);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("Search:", "Поиск:"), GUILayout.Width(50));
            search = GUILayout.TextField(search ?? "");
            GUILayout.EndHorizontal();

            string onlyGroup = (t != 3 && groupFilter > 0 && groupFilter <= groups.Count) ? groups[groupFilter - 1] : null;
            float funds = GameApi.GetFunds();
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            string currentGroup = null;
            foreach (var e in catalog)
            {
                if (!InTab(e, t) || !Visible(e)) continue;
                if (onlyGroup != null && e.GroupName != onlyGroup) continue;
                if (!string.IsNullOrEmpty(search) && e.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && (e.Definition.code ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (e.GroupName != currentGroup)
                {
                    currentGroup = e.GroupName;
                    GUILayout.Space(4);
                    GUILayout.Label("— " + currentGroup + " —", headerStyle);
                }
                bool afford = funds >= e.Price;
                bool canBuy = afford && e.AllowedForMe;
                GUILayout.BeginHorizontal(GUI.skin.box);
                string tag = e.FactionTag == "?" ? "" : "  <" + e.FactionTag + ">";
                GUILayout.Label($"{e.Name}{(string.IsNullOrEmpty(e.TypeName) ? "" : "  [" + e.TypeName + "]")}{tag}", e.AllowedForMe ? GUI.skin.label : badStyle, GUILayout.Width(300));
                GUILayout.Label(e.AllowedForMe ? GameApi.Money(e.Price) : L.T("foreign", "чужая"), afford && e.AllowedForMe ? okStyle : badStyle, GUILayout.Width(80));
                GUI.enabled = canBuy;
                if (e.Playable)
                {
                    if (GUILayout.Button(L.T("Modules & buy", "Модули и покупка"), GUILayout.Width(164)))
                    {
                        OpenLoadout(e);
                    }
                }
                else
                {
                    if (GUILayout.Button(L.T("On map", "На карте"), GUILayout.Width(80)))
                    {
                        BeginPlacement(e, PlaceMode.Map);
                    }
                    if (GUILayout.Button(L.T("Camera", "Камерой"), GUILayout.Width(80)))
                    {
                        BeginPlacement(e, PlaceMode.Camera);
                    }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        // ================= loadout window (KAR / BOTE) =================

        private LoadoutDraft draft;
        private CatalogEntry draftEntry;
        private Rect loadoutRect = new Rect(800, 80, 520, 560);
        private Vector2 loadoutScroll;

        private void OpenLoadout(CatalogEntry e)
        {
            try
            {
                draft = new LoadoutDraft((AircraftDefinition)e.Definition);
                draftEntry = e;
                loadoutRect.x = Mathf.Min(windowRect.xMax + 10, Screen.width - loadoutRect.width - 10);
                loadoutRect.y = windowRect.y;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Loadout window failed: " + ex);
                Toast(L.T("Couldn't open modules: ", "Не удалось открыть модули: ") + ex.Message);
                draft = null;
            }
        }

        private void DrawLoadoutWindow(int id)
        {
            if (draft == null || draftEntry == null)
            {
                return;
            }
            float extra = draft.TotalCost();
            float total = draftEntry.Price + extra;
            float funds = GameApi.GetFunds();
            GUILayout.Label(draftEntry.Name, headerStyle);
            GUILayout.Label(L.T($"Vehicle: {GameApi.Money(draftEntry.Price)}   Modules: {GameApi.Money(extra)}   Total: {GameApi.Money(total)}", $"Техника: {GameApi.Money(draftEntry.Price)}   Модули: {GameApi.Money(extra)}   Итого: {GameApi.Money(total)}"), funds >= total ? okStyle : badStyle);

            loadoutScroll = GUILayout.BeginScrollView(loadoutScroll, GUILayout.ExpandHeight(true));
            if (draft.Sets.Length == 0)
            {
                GUILayout.Label(L.T("This vehicle has no weapon slots.", "У этой техники нет слотов вооружения."));
            }
            for (int i = 0; i < draft.Sets.Length; i++)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                int count = draft.Sets[i].hardpoints.Count;
                GUILayout.Label(draft.SetName(i) + (count > 1 ? $"  (×{count})" : ""));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("◄", GUILayout.Width(32))) draft.Step(i, -1);
                var m = draft.Current(i);
                GUILayout.Label(LoadoutDraft.MountName(m) + (m != null ? "   " + GameApi.Money(draft.MountCost(i, m)) : ""), GUILayout.ExpandWidth(true));
                if (GUILayout.Button("►", GUILayout.Width(32))) draft.Step(i, +1);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();

            GUILayout.Label(L.T("After buying you appear in this vehicle right away (you must not be in another one).", "После покупки ты сразу появишься в этой технике (нужно не сидеть в другой)."), okStyle);
            GUILayout.BeginHorizontal();
            GUI.enabled = funds >= total && draftEntry.AllowedForMe;
            if (GUILayout.Button(L.T("Place on map", "Поставить на карте")))
            {
                StartPlayablePlacement(PlaceMode.Map);
            }
            if (GUILayout.Button(L.T("Place with camera", "Поставить камерой")))
            {
                StartPlayablePlacement(PlaceMode.Camera);
            }
            GUI.enabled = true;
            if (GUILayout.Button(L.T("Close", "Закрыть"), GUILayout.Width(80)))
            {
                draft = null;
            }
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private void StartPlayablePlacement(PlaceMode m)
        {
            GameApi.PendingLoadout = draft.Build();
            GameApi.PendingExtraCost = draft.TotalCost();
            var e = draftEntry;
            draft = null;
            BeginPlacement(e, m, keepPending: true);
        }

        private void DrawOwned()
        {
            Aircraft mine = Crew.MyVehicle();
            if (mine != null && Crew.IsPlayable(mine))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(L.T("You are in: ", "Сейчас ты в: ") + (mine.definition?.unitName ?? mine.name), okStyle);
                GUI.enabled = !crew.Busy;
                if (GUILayout.Button(L.T("Get out (vehicle stays)", "Выйти (техника останется)"), GUILayout.Width(210)))
                {
                    menuOpen = false;
                    crew.Leave();
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            if (crew.Busy)
            {
                GUILayout.Label(L.T("Switching vehicles…", "Идёт пересадка…"), okStyle);
            }
            ownedScroll = GUILayout.BeginScrollView(ownedScroll, GUILayout.ExpandHeight(true));
            if (purchased.Count == 0)
            {
                GUILayout.Label(L.T("Nothing bought yet.", "Пока ничего не куплено."));
            }
            foreach (var u in purchased.ToList())
            {
                if (u == null) continue;
                GUILayout.BeginHorizontal(GUI.skin.box);
                if (u is Aircraft a)
                {
                    bool inside = a == mine;
                    string st = inside ? L.T("you are inside", "ты внутри") : a.Player != null ? L.T("occupied", "занята") : L.T("no crew", "без экипажа");
                    GUILayout.Label($"{u.definition?.unitName ?? u.name}  ({st})", GUILayout.Width(300));
                    GUI.enabled = !inside && a.Player == null && !crew.Busy;
                    if (GUILayout.Button(L.T("Get in", "Сесть"), GUILayout.Width(80)))
                    {
                        menuOpen = false;
                        crew.Enter(a);
                    }
                    GUI.enabled = true;
                    if (GUILayout.Button(L.T("On map", "На карте"), GUILayout.Width(80)))
                    {
                        ShowOnMap(u);
                    }
                    GUI.enabled = !inside && !crew.Busy;
                    if (GUILayout.Button(ScrapLabel(u), GUILayout.Width(110))) Scrap(u, true);
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                    continue;
                }
                if (u is Building || u is Container)
                {
                    string st2 = L.T("static", "стационарный");
                    if (FocusFire.Orders.TryGetValue(u, out var ft2) && ft2 != null && !ft2.disabled) st2 += L.T(", target: ", ", цель: ") + (ft2 is Missile ? L.T("missile", "ракета") : (ft2.definition?.unitName ?? ft2.name));
                    GUILayout.Label($"{u.definition?.unitName ?? u.name}  ({st2})", GUILayout.Width(300));
                    if (u.GetComponentInChildren<Turret>() != null)
                    {
                        bool pro2 = FocusFire.MissileDefense.Contains(u);
                        if (GUILayout.Button(pro2 ? L.T("ABM: on", "ПРО: вкл") : L.T("ABM: off", "ПРО: выкл"), GUILayout.Width(80)))
                        {
                            if (pro2) FocusFire.MissileDefense.Remove(u);
                            else FocusFire.MissileDefense.Add(u);
                        }
                    }
                    if (GUILayout.Button(L.T("On map", "На карте"), GUILayout.Width(70))) ShowOnMap(u);
                    if (GUILayout.Button(ScrapLabel(u), GUILayout.Width(110))) Scrap(u, true);
                    GUILayout.EndHorizontal();
                    continue;
                }
                bool hold = GameApi.GetHold(u);
                string state = hold ? L.T("holding position", "держит позицию") : L.T("free", "свободен");
                if (FocusFire.Orders.TryGetValue(u, out var ft) && ft != null && !ft.disabled)
                {
                    state += L.T(", target: ", ", цель: ") + (ft is Missile ? L.T("missile", "ракета") : (ft.definition?.unitName ?? ft.name)) + (FocusFire.CanEngageNow(u, ft) ? L.T(" (firing)", " (огонь)") : L.T(" (out of range)", " (вне досягаемости)"));
                }
                if (FocusFire.MissileDefense.Contains(u)) state += L.T(", ABM", ", ПРО");
                GUILayout.Label($"{u.definition?.unitName ?? u.name}  ({state})", GUILayout.Width(300));
                if (GUILayout.Button(hold ? L.T("Release", "Отпустить") : L.T("Hold", "Держать"), GUILayout.Width(80)))
                {
                    attackOrders.Remove(u);
                    GameApi.SetHold(u, !hold);
                    if (!hold) GameApi.Stop(u);
                }
                if (GUILayout.Button(L.T("Stop", "Стоп"), GUILayout.Width(55)))
                {
                    attackOrders.Remove(u);
                    FocusFire.Clear(u);
                    GameApi.Stop(u);
                }
                bool pro = FocusFire.MissileDefense.Contains(u);
                if (GUILayout.Button(pro ? L.T("ABM: on", "ПРО: вкл") : L.T("ABM: off", "ПРО: выкл"), GUILayout.Width(80)))
                {
                    if (pro) FocusFire.MissileDefense.Remove(u);
                    else FocusFire.MissileDefense.Add(u);
                }
                if (GUILayout.Button(L.T("On map", "На карте"), GUILayout.Width(70)))
                {
                    ShowOnMap(u);
                }
                if (GUILayout.Button(ScrapLabel(u), GUILayout.Width(110))) Scrap(u, true);
                GUILayout.EndHorizontal();
            }
            if (factionBought.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label(L.T($"— Bought for the faction ({factionBought.Count}) — controlled by the AI —", $"— Куплено для фракции ({factionBought.Count}) — ими управляет ИИ —"), headerStyle);
                foreach (var u in factionBought.ToList())
                {
                    if (u == null) continue;
                    GUILayout.BeginHorizontal(GUI.skin.box);
                    GUILayout.Label(u.definition?.unitName ?? u.name, GUILayout.Width(300));
                    if (GUILayout.Button(L.T("On map", "На карте"), GUILayout.Width(70))) ShowOnMap(u);
                    if (GUILayout.Button(ScrapLabel(u), GUILayout.Width(110))) Scrap(u, true);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
        }

        // ================= scrapping own units =================

        private float RefundFor(Unit u)
        {
            float price;
            if (!paid.TryGetValue(u, out price))
            {
                price = u.definition != null ? GameApi.PriceOf(u.definition) : 0f;
            }
            return price * Mathf.Clamp(Plugin.ScrapRefundPercent.Value, 0f, 100f) / 100f;
        }

        private string ScrapLabel(Unit u) => L.T("Scrap +", "Списать +") + GameApi.Money(RefundFor(u));

        private void Scrap(Unit u, bool toast)
        {
            if (u == null || u.disabled) return;
            if (u is Aircraft && Crew.MyVehicle() == u)
            {
                Toast(L.T("Get out of this vehicle first (", "Сначала выйди из этой техники (") + Plugin.LeaveKey.Value + ")");
                return;
            }
            float refund = RefundFor(u);
            purchased.Remove(u);
            factionBought.Remove(u);
            paid.Remove(u);
            attackOrders.Remove(u);
            FocusFire.Clear(u);
            FocusFire.MissileDefense.Remove(u);
            try
            {
                Crew.Unmanned.Remove(u.UniqueName ?? "");
                u.NetworkunitState = Unit.UnitState.Returned;
                u.Networkdisabled = true;
                StartCoroutine(DestroyLater(u, 1f));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Scrap: " + e.Message);
            }
            GameApi.Refund(refund);
            if (toast) Toast(L.T($"Scrapped: {u.definition?.unitName ?? u.name}, refund {GameApi.Money(refund)}", $"Списано: {u.definition?.unitName ?? u.name}, возврат {GameApi.Money(refund)}"));
        }

        private static IEnumerator DestroyLater(Unit u, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (u != null) Destroy(u.gameObject);
        }

        // ================= stratagems =================

        private Vector2 stratScroll;

        private void DrawStratagems()
        {
            float funds = GameApi.GetFunds();
            GUILayout.Label(L.T($"Call in support: pick a stratagem → mark the target on the map (LMB) or with the camera ({Plugin.CameraPlaceKey.Value}). The strike lands {Stratagems.Delay.Value:0} s after the call.", $"Вызов поддержки: выбери стратегему → укажи цель на карте (ЛКМ) или камерой ({Plugin.CameraPlaceKey.Value}). Удар через {Stratagems.Delay.Value:0} с после вызова."));
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("Projectile spawn height above ground:", "Высота появления снарядов над землёй:"), GUILayout.Width(270));
            Stratagems.SpawnHeight.Value = Mathf.Round(GUILayout.HorizontalSlider(Stratagems.SpawnHeight.Value, 100f, 8000f, GUILayout.Width(220)) / 50f) * 50f;
            GUILayout.Label(L.T($"{Stratagems.SpawnHeight.Value:0} m", $"{Stratagems.SpawnHeight.Value:0} м"), GUILayout.Width(70));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("…but not lower above sea level (above mountains):", "…но не ниже над уровнем моря (выше гор):"), GUILayout.Width(270));
            Stratagems.MinAltitude.Value = Mathf.Round(GUILayout.HorizontalSlider(Stratagems.MinAltitude.Value, 0f, 10000f, GUILayout.Width(220)) / 100f) * 100f;
            GUILayout.Label(L.T($"{Stratagems.MinAltitude.Value:0} m", $"{Stratagems.MinAltitude.Value:0} м"), GUILayout.Width(70));
            GUILayout.EndHorizontal();
            stratScroll = GUILayout.BeginScrollView(stratScroll, GUILayout.ExpandHeight(true));
            foreach (var s in Stratagems.All)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                float price = s.CurrentPrice();
                float cd = s.CooldownLeft;
                GUILayout.BeginHorizontal();
                GUILayout.Label(s.Name, headerStyle, GUILayout.Width(300));
                GUILayout.Label(GameApi.Money(price), funds >= price ? okStyle : badStyle, GUILayout.Width(90));
                GUILayout.Label(cd > 0f ? L.T($"cooldown {cd:0} s", $"перезарядка {cd:0} с") : L.T("ready", "готово"), cd > 0f ? badStyle : okStyle, GUILayout.Width(150));
                GUILayout.EndHorizontal();
                GUILayout.Label(s.Description);
                if (s.HasArea)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Toggle(s.Point, L.T(" Point", " Точечно"), GUILayout.Width(100))) s.Point = true;
                    if (GUILayout.Toggle(!s.Point, L.T(" Area, radius:", " По области, радиус:"), GUILayout.Width(160))) s.Point = false;
                    GUI.enabled = !s.Point;
                    s.Radius = GUILayout.HorizontalSlider(s.Radius, Stratagems.MinRadius, Stratagems.MaxRadius, GUILayout.Width(220));
                    GUILayout.Label(L.T($"{s.Radius:0} m", $"{s.Radius:0} м"), GUILayout.Width(70));
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }
                if (s.HasVolume)
                {
                    string what = s.Kind == StratagemKind.Carpet ? L.T("Bombs", "Бомб") : s.Kind == StratagemKind.MissileStrike ? L.T("Missiles", "Ракет") : L.T("Shells", "Снарядов");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(what + L.T(" per salvo:", " в залпе:"), GUILayout.Width(120));
                    if (GUILayout.Button("−", GUILayout.Width(26))) s.Shells.Value = Mathf.Max(s.MinShells, s.ShellCount - 1);
                    GUILayout.Label(s.ShellCount.ToString(), GUILayout.Width(30));
                    if (GUILayout.Button("+", GUILayout.Width(26))) s.Shells.Value = Mathf.Min(s.MaxShells, s.ShellCount + 1);
                    GUILayout.Space(20);
                    GUILayout.Label(L.T("Salvos:", "Залпов:"), GUILayout.Width(60));
                    if (GUILayout.Button("−", GUILayout.Width(26))) s.Salvos.Value = Mathf.Max(1, s.SalvoCount - 1);
                    GUILayout.Label(s.SalvoCount.ToString(), GUILayout.Width(30));
                    if (GUILayout.Button("+", GUILayout.Width(26))) s.Salvos.Value = Mathf.Min(Stratagem.MaxSalvos, s.SalvoCount + 1);
                    GUILayout.Space(20);
                    GUILayout.Label(L.T($"total {s.ShellCount * s.SalvoCount}, every {Stratagems.SalvoInterval.Value:0} s", $"всего {s.ShellCount * s.SalvoCount}, интервал {Stratagems.SalvoInterval.Value:0} с"), GUILayout.Width(200));
                    GUILayout.EndHorizontal();
                }
                if (s.Kind == StratagemKind.Airdrop)
                {
                    DrawAirdropPicker(s);
                }
                else
                {
                    var m = Stratagems.MunitionFor(s);
                    var cands = Stratagems.Candidates(s);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(L.T("Munition:", "Боеприпас:"), GUILayout.Width(80));
                    int mi = m != null ? cands.IndexOf(m) : -1;
                    if (cands.Count > 0 && GUILayout.Button("◄", GUILayout.Width(28)))
                    {
                        var pick = cands[(mi - 1 + cands.Count) % cands.Count];
                        s.Munition.Value = FactionResolver.KeyOf(pick);
                    }
                    GUILayout.Label(m != null ? (m.unitName ?? m.name) : L.T("not found in the game", "не найден в игре"), m != null ? GUI.skin.label : badStyle, GUILayout.Width(300));
                    if (cands.Count > 0 && GUILayout.Button("►", GUILayout.Width(28)))
                    {
                        var pick = cands[(mi + 1) % cands.Count];
                        s.Munition.Value = FactionResolver.KeyOf(pick);
                    }
                    if (!string.IsNullOrEmpty(s.Munition.Value) && GUILayout.Button(L.T("auto", "авто"), GUILayout.Width(50)))
                    {
                        s.Munition.Value = "";
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                GUI.enabled = cd <= 0f && funds >= price && (s.Kind != StratagemKind.Airdrop || s.DropUnit != null);
                if (GUILayout.Button(L.T("Target on map", "Цель на карте"), GUILayout.Width(140))) BeginStrike(s, PlaceMode.Map);
                if (GUILayout.Button(L.T("Target with camera", "Цель камерой"), GUILayout.Width(140))) BeginStrike(s, PlaceMode.Camera);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
        }

        private void DrawAirdropPicker(Stratagem s)
        {
            var options = catalog == null ? new List<CatalogEntry>() : catalog.Where(e => !e.Playable && !e.Static && e.Definition is VehicleDefinition && e.AllowedForMe).ToList();
            if (options.Count == 0)
            {
                GUILayout.Label(L.T("No ground vehicles available", "Нет доступной наземной техники"), badStyle);
                return;
            }
            int idx = s.DropUnit == null ? -1 : options.IndexOf(s.DropUnit);
            if (idx < 0)
            {
                idx = 0;
                s.DropUnit = options[0];
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◄", GUILayout.Width(30))) s.DropUnit = options[(idx - 1 + options.Count) % options.Count];
            GUILayout.Label($"{s.DropUnit.Name}  ({s.DropUnit.GroupName})", GUILayout.Width(380));
            if (GUILayout.Button("►", GUILayout.Width(30))) s.DropUnit = options[(idx + 1) % options.Count];
            GUILayout.Space(10);
            if (GUILayout.Button("−", GUILayout.Width(26))) s.DropCount = Mathf.Max(1, s.DropCount - 1);
            GUILayout.Label("× " + s.DropCount, GUILayout.Width(36));
            if (GUILayout.Button("+", GUILayout.Width(26))) s.DropCount = Mathf.Min(6, s.DropCount + 1);
            GUILayout.EndHorizontal();
        }

        private void BeginStrike(Stratagem s, PlaceMode m)
        {
            if (s.Kind == StratagemKind.Airdrop && Plugin.MaxOwnUnits.Value > 0 && purchased.Count + s.DropCount > Plugin.MaxOwnUnits.Value)
            {
                Toast(L.T("The airdrop would exceed your unit limit", "Десант превысит лимит своих юнитов"));
                return;
            }
            var dummy = new CatalogEntry
            {
                Name = s.Name,
                Price = s.CurrentPrice(),
                Category = UnitCategory.Ground,
                Definition = s.Kind == StratagemKind.Airdrop ? s.DropUnit?.Definition : null,
                AllowedForMe = true
            };
            BeginPlacement(dummy, m);
            strike = s;
            Toast(m == PlaceMode.Map ? L.T($"{s.Name}: LMB on the target, {Plugin.RotateKey.Value} — direction, {Plugin.CancelKey.Value} — cancel", $"{s.Name}: ЛКМ по цели, {Plugin.RotateKey.Value} — направление, {Plugin.CancelKey.Value} — отмена") : L.T($"{s.Name}: aim, {Plugin.CameraPlaceKey.Value} — call in", $"{s.Name}: наведи прицел, {Plugin.CameraPlaceKey.Value} — вызвать"), 5f);
        }

        private void ShowOnMap(Unit u)
        {
            var map = SceneSingleton<DynamicMap>.i;
            if (map == null) return;
            menuOpen = false;
            if (!DynamicMap.mapMaximized) map.Maximize();
            try
            {
                map.DeselectAllIcons();
                map.SelectIcon(u);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Select icon failed: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (ghost != null) Destroy(ghost);
        }
    }
}
