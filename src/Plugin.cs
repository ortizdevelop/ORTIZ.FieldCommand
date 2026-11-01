using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace FieldCommand
{
    public enum FundsSource
    {
        Player,
        Faction
    }

    public enum UnknownUnitPolicy
    {
        AllowForAll,
        Hide
    }

    [BepInPlugin(Guid, "ORTIZ Ops - FieldCommand", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ortiz.fieldcommand";
        public const string OldGuid = "com.nikita.fieldcommand";
        public const string Version = "0.10.3";
        public const int MaxUnitLimit = 150;

        internal static ManualLogSource Log;

        // Controls
        internal static ConfigEntry<KeyboardShortcut> MenuKey;
        internal static ConfigEntry<KeyCode> RotateKey;
        internal static ConfigEntry<KeyCode> CameraPlaceKey;
        internal static ConfigEntry<KeyCode> CancelKey;
        internal static ConfigEntry<KeyCode> HoldKey;
        internal static ConfigEntry<KeyCode> AttackKey;
        internal static ConfigEntry<KeyboardShortcut> LeaveKey;
        internal static ConfigEntry<KeyCode> MissileDefenseKey;

        // Factions
        internal static ConfigEntry<bool> RestrictToFaction;
        internal static ConfigEntry<UnknownUnitPolicy> UnknownUnits;
        internal static ConfigEntry<float> FactionShareThreshold;
        internal static ConfigEntry<string> FactionOverrides;
        internal static ConfigEntry<bool> LogFactionTable;
        internal static ConfigEntry<bool> ShowOtherFaction;

        // Playable
        internal static ConfigEntry<bool> EnablePlayable;
        internal static ConfigEntry<bool> EnableStatic;
        internal static ConfigEntry<bool> ShowCivilBuildings;
        internal static ConfigEntry<bool> StaticCapturable;


        // Economy
        internal static ConfigEntry<FundsSource> Funds;
        internal static ConfigEntry<float> PriceMultiplier;
        internal static ConfigEntry<float> MinPrice;
        internal static ConfigEntry<int> MaxOwnUnits;
        internal static ConfigEntry<int> MaxFactionUnits;
        internal static ConfigEntry<float> ScrapRefundPercent;
        internal static ConfigEntry<KeyCode> ScrapKey;
        internal static ConfigEntry<float> KillRewardPercent;
        internal static ConfigEntry<bool> RewardFromFactionUnits;

        // Territory
        internal static ConfigEntry<bool> RestrictToTerritory;
        internal static ConfigEntry<float> AirbaseRadius;
        internal static ConfigEntry<float> NavalAirbaseRadius;
        internal static ConfigEntry<float> FriendlyUnitRadius;
        internal static ConfigEntry<float> MinEnemyDistance;
        internal static ConfigEntry<float> MinShipDepth;

        // Behaviour
        internal static ConfigEntry<bool> SpawnHoldingPosition;
        internal static ConfigEntry<float> GroundAttackStandoff;
        internal static ConfigEntry<float> NavalAttackStandoff;
        internal static ConfigEntry<float> RotateStep;
        internal static ConfigEntry<bool> EnableResupply;
        internal static ConfigEntry<float> ResupplyRadius;
        internal static ConfigEntry<bool> SupplyTrucksAutonomous;

        private void Awake()
        {
            Log = Logger;
            MigrateOldConfig();

            BindConfig(Config);

            try
            {
                new HarmonyLib.Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            }
            catch (System.Exception e)
            {
                Log.LogError("Harmony patching failed: " + e);
            }

            Stratagems.Bind(Config);

            var go = new GameObject("[FieldCommand]");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<FieldCommandController>();

            Log.LogInfo($"ORTIZ Ops - FieldCommand v{Version} loaded");
        }
    
        /// <summary>All settings (static so they can be checked outside the game).</summary>
        internal static void BindConfig(ConfigFile Config)
        {
            L.BindLanguage(Config);
            const string sCtl = "1. Controls", rCtl = "1. Управление";
            const string sEco = "2. Economy", rEco = "2. Экономика";
            const string sTer = "3. Territory", rTer = "3. Территория";
            const string sBeh = "4. Behaviour", rBeh = "4. Поведение";
            const string sFac = "5. Factions", rFac = "5. Фракции";
            const string sPla = "6. Playable vehicles", rPla = "6. Играбельная техника";
            const string sDef = "8. Defences and supplies", rDef = "8. Оборона и снабжение";

            MenuKey = L.Bind(Config, sCtl, rCtl, "Purchase menu", "Меню покупки", new KeyboardShortcut(KeyCode.F6),
                "Open / close the purchase menu", "Открыть/закрыть меню покупки юнитов");
            RotateKey = L.Bind(Config, sCtl, rCtl, "Rotate", "Повернуть", KeyCode.R,
                "Rotate the unit while placing it (Shift — the other way)", "Повернуть юнит при расстановке (Shift — в обратную сторону)");
            CameraPlaceKey = L.Bind(Config, sCtl, rCtl, "Place (camera)", "Поставить (камера)", KeyCode.G,
                "Place the unit at the camera's aim point", "Поставить юнит в точку прицела в режиме камеры");
            CancelKey = L.Bind(Config, sCtl, rCtl, "Cancel", "Отмена", KeyCode.X,
                "Leave placement mode", "Выйти из режима расстановки");
            HoldKey = L.Bind(Config, sCtl, rCtl, "Hold position", "Держать позицию", KeyCode.H,
                "On the map: toggle hold position for the selected own units", "На карте: переключить «держать позицию» для выделенных своих юнитов");
            AttackKey = L.Bind(Config, sCtl, rCtl, "Attack", "Атаковать", KeyCode.K,
                "On the map: selected units open fire at the enemy / missile under the cursor from where they stand. Shift+key — advance to firing range. On empty ground — clear the target",
                "На карте: выделенные юниты открывают огонь по врагу/ракете под курсором с текущей позиции. Shift+клавиша — выдвинуться на дистанцию огня. По пустому месту — сбросить цель");
            MissileDefenseKey = L.Bind(Config, sCtl, rCtl, "Missile defence", "Перехват ракет", KeyCode.L,
                "On the map: selected units pick and shoot down enemy missiles themselves (strategic and nuclear first)",
                "На карте: выделенные юниты сами выбирают и сбивают вражеские ракеты (стратегические и ядерные — первыми)");

            RestrictToFaction = L.Bind(Config, sFac, rFac, "Own faction equipment only", "Только техника своей фракции", true,
                "BDF buys only BDF equipment, PALA only PALA (shared equipment is available to both)",
                "BDF покупает только технику BDF, PALA — только PALA (общая техника доступна обеим)");
            UnknownUnits = L.Bind(Config, sFac, rFac, "Units with unknown faction", "Техника без известной фракции", UnknownUnitPolicy.AllowForAll,
                "Units never seen with any faction (usually from mods): AllowForAll — available to both, Hide — hide them",
                "Что делать с юнитами, которые ни разу не встречались ни у одной фракции (обычно — из модов): AllowForAll — доступны обеим, Hide — скрыть");
            FactionShareThreshold = L.Bind(Config, sFac, rFac, "Ownership threshold", "Порог принадлежности", 0.75f,
                "If a faction uses a unit in at least this share of cases (missions, convoys), it is that faction's equipment; otherwise it is shared",
                "Если юнит используется фракцией в ≥ этой доли случаев (миссии, конвои), он считается её техникой; иначе — общей",
                new AcceptableValueRange<float>(0.5f, 1f));
            FactionOverrides = L.Bind(Config, sFac, rFac, "Manual overrides", "Ручная привязка", "",
                "Comma separated: key:BDF, key:PALA, key:ALL. Unit keys are written to LogOutput.log when \"Write faction table to log\" is on. Unit names work too",
                "Через запятую: ключ:BDF, ключ:PALA, ключ:ALL. Ключи юнитов видны в LogOutput.log, если включить «Писать таблицу фракций в лог». Можно писать и имя юнита");
            LogFactionTable = L.Bind(Config, sFac, rFac, "Write faction table to log", "Писать таблицу фракций в лог", false,
                "Write the list key | name | faction to BepInEx\\LogOutput.log", "Выводить в BepInEx\\LogOutput.log список: ключ | название | фракция");
            ShowOtherFaction = L.Bind(Config, sFac, rFac, "Show other faction equipment", "Показывать чужую технику", false,
                "Show the other faction's units in the list (greyed out, can't be bought)", "Показывать технику другой фракции в списке (серым, купить нельзя)");

            EnablePlayable = L.Bind(Config, sPla, rPla, "Enable", "Включить", true,
                "Show modded units you can drive yourself (KAR — ground, BOTE — ships). Choose modules, buy — and you are inside right away",
                "Показывать технику из модов, за которую можно играть самому (KAR — наземка, BOTE — корабли). Выбираешь модули, покупаешь — и сразу садишься в неё");

            LeaveKey = L.Bind(Config, sCtl, rCtl, "Leave KAR/BOTE vehicle", "Выйти из техники KAR/BOTE", new KeyboardShortcut(KeyCode.J),
                "Leave the current KAR/BOTE vehicle — it stays where it is without a crew", "Выйти из текущей техники KAR/BOTE — она останется стоять на месте без экипажа");

            EnableStatic = L.Bind(Config, sDef, rDef, "Enable", "Включить", true,
                "Buy static air defence, gun positions, radars, depots and containers (ammo, fuel, repair)",
                "Покупка стационарной ПВО, огневых точек, радаров, складов и контейнеров (боеприпасы, топливо, ремонт)");
            ShowCivilBuildings = L.Bind(Config, sDef, rDef, "Show civil buildings", "Показывать гражданские постройки", false,
                "Add ordinary buildings without weapons to the list", "Добавить в список обычные здания без оружия");
            StaticCapturable = L.Bind(Config, sDef, rDef, "Buildings can be captured", "Постройки можно захватить", false,
                "If on, the enemy can capture your buildings like ordinary base buildings", "Если включено, враг может захватить твои постройки, как обычные здания на базах");

            Funds = L.Bind(Config, sEco, rEco, "Funds source", "Источник денег", FundsSource.Player,
                "Player — your personal budget (like buying aircraft), Faction — the faction treasury",
                "Player — личный бюджет игрока (как при покупке самолётов), Faction — казна фракции");
            PriceMultiplier = L.Bind(Config, sEco, rEco, "Price multiplier", "Множитель цены", 1f,
                "Price = the unit's in-game cost × multiplier", "Цена = стоимость юнита в игре × множитель", new AcceptableValueRange<float>(0f, 10f));
            MinPrice = L.Bind(Config, sEco, rEco, "Minimum price", "Минимальная цена", 0.5f,
                "Minimum unit price (millions) if the unit costs 0 in the game", "Минимальная цена юнита (млн), если у юнита в игре стоимость 0");
            MaxOwnUnits = L.Bind(Config, sEco, rEco, "Own unit limit", "Лимит своих юнитов", 60,
                "How many units bought for yourself can be alive at the same time (1–" + MaxUnitLimit + "). Also adjustable in the purchase menu",
                "Сколько юнитов, купленных себе, может быть живо одновременно (1–" + MaxUnitLimit + "). Меняется и прямо в меню покупки",
                new AcceptableValueRange<int>(1, MaxUnitLimit));
            MaxFactionUnits = L.Bind(Config, sEco, rEco, "Faction unit limit", "Лимит юнитов для фракции", 60,
                "How many units bought for the faction can be alive at the same time (1–" + MaxUnitLimit + "). Counted separately from yours. Also adjustable in the purchase menu",
                "Сколько юнитов, купленных для фракции, может быть живо одновременно (1–" + MaxUnitLimit + "). Считается отдельно от своих. Меняется и прямо в меню покупки",
                new AcceptableValueRange<int>(1, MaxUnitLimit));
            ScrapRefundPercent = L.Bind(Config, sEco, rEco, "Scrap refund, %", "Возврат при списании, %", 50f,
                "Share of the purchase price you get back when you scrap one of your units", "Сколько процентов цены покупки возвращается, если списать (убрать) своего юнита",
                new AcceptableValueRange<float>(0f, 100f));
            ScrapKey = L.Bind(Config, sCtl, rCtl, "Scrap units", "Списать юнитов", KeyCode.Delete,
                "On the map: remove the selected purchased units and get part of the money back", "На карте: убрать выделенных купленных юнитов с возвратом части денег");

            KillRewardPercent = L.Bind(Config, sEco, rEco, "Kill reward share, %", "Доля награды за уничтожения, %", 50f,
                "When your purchased units destroy an enemy or a missile you get this % of what a pilot would get for the same kill (0 — off)",
                "Когда твои купленные юниты уничтожают врага или ракету, ты получаешь этот % от того, что получил бы пилот-игрок за то же уничтожение (0 — выключить)",
                new AcceptableValueRange<float>(0f, 200f));
            RewardFromFactionUnits = L.Bind(Config, sEco, rEco, "Reward for faction units too", "Награда и за юнитов, купленных фракции", true,
                "Also get the share for kills by units you bought for the faction (controlled by the AI)",
                "Получать долю и за уничтожения юнитами, которых ты купил для фракции (ими управляет ИИ)");

            RestrictToTerritory = L.Bind(Config, sTer, rTer, "Own territory only", "Только своя территория", true,
                "Allow placement only near your own bases and units", "Разрешать расстановку только рядом со своими базами и юнитами");
            AirbaseRadius = L.Bind(Config, sTer, rTer, "Radius from base (ground), m", "Радиус от базы (наземка), м", 12000f,
                "Ground units can be placed within this radius of your airbase", "Наземную технику можно ставить в этом радиусе от своей авиабазы");
            NavalAirbaseRadius = L.Bind(Config, sTer, rTer, "Radius from base (naval), m", "Радиус от базы (флот), м", 30000f,
                "Ships can be placed within this radius of your airbase — you can build a fleet even without living carriers",
                "Корабли можно ставить в этом радиусе от своей авиабазы — флот можно отстроить даже без живых авианосцев");
            FriendlyUnitRadius = L.Bind(Config, sTer, rTer, "Radius from own units, m", "Радиус от своих юнитов, м", 3000f,
                "You can place near your own vehicles, ships and buildings", "Ставить можно рядом со своей наземной техникой, кораблями и зданиями");
            MinEnemyDistance = L.Bind(Config, sTer, rTer, "Min. distance to enemy, m", "Мин. дистанция до врага, м", 2000f,
                "Can't place closer to enemy ground units / ships / buildings (0 — no limit)", "Нельзя ставить ближе к вражеским наземным юнитам/кораблям/зданиям (0 — без ограничения)");
            MinShipDepth = L.Bind(Config, sTer, rTer, "Min. water depth for a ship, m", "Мин. глубина для корабля, м", 8f,
                "Minimum water depth under a ship (checked at bow, stern and centre)", "Минимальная глубина под кораблём (проверяется по носу, корме и центру)");

            SpawnHoldingPosition = L.Bind(Config, sBeh, rBeh, "Spawn in hold position", "Спавнить в режиме «держать позицию»", true,
                "Purchased units stay put and don't drive off on AI tasks until you give an order",
                "Купленные юниты стоят на месте и не уезжают выполнять задачи ИИ, пока ты не дашь приказ");
            GroundAttackStandoff = L.Bind(Config, sBeh, rBeh, "Attack distance (ground), m", "Дистанция атаки (наземка), м", 1500f,
                "How far from the target ground units stop when ordered to attack", "На каком расстоянии от цели останавливается наземка при приказе «Атаковать»");
            NavalAttackStandoff = L.Bind(Config, sBeh, rBeh, "Attack distance (naval), m", "Дистанция атаки (флот), м", 9000f,
                "How far from the target ships stop when ordered to attack", "На каком расстоянии от цели останавливаются корабли при приказе «Атаковать»");
            RotateStep = L.Bind(Config, sBeh, rBeh, "Rotation step, deg", "Шаг поворота, град", 45f,
                "How many degrees the rotate key turns the unit", "На сколько градусов поворачивать юнит клавишей поворота");
            EnableResupply = L.Bind(Config, sBeh, rBeh, "Resupply from nearby ammo sources", "Пополнение боезапаса от ближайших источников", true,
                "Stopped friendly units that are short of ammo are rearmed by the nearest friendly ammo container, depot or supply truck within the radius below",
                "Стоящие союзные юниты, у которых не хватает боезапаса, пополняются от ближайшего союзного контейнера с боеприпасами, склада или машины снабжения в радиусе ниже");
            ResupplyRadius = L.Bind(Config, sBeh, rBeh, "Resupply radius, m", "Радиус пополнения, м", 150f,
                "How close a unit must be to an ammo source to be rearmed", "Насколько близко юнит должен стоять к источнику боеприпасов");
            SupplyTrucksAutonomous = L.Bind(Config, sBeh, rBeh, "Supply trucks work on their own", "Машины снабжения работают сами", true,
                "Purchased supply trucks ignore hold position and drive to friendly units that need ammo, like in the base game",
                "Купленные машины снабжения не держат позицию, а сами ездят к союзникам, которым нужны боеприпасы, как в обычной игре");
        }

        /// <summary>Settings of the old "com.nikita.fieldcommand" build are carried over once.</summary>
        private void MigrateOldConfig()
        {
            try
            {
                string oldPath = System.IO.Path.Combine(Paths.ConfigPath, OldGuid + ".cfg");
                string newPath = Config.ConfigFilePath;
                string marker = newPath + ".migrated";
                if (System.IO.File.Exists(oldPath) && !System.IO.File.Exists(marker))
                {
                    System.IO.File.Copy(oldPath, newPath, true);
                    System.IO.File.WriteAllText(marker, "settings copied from " + OldGuid);
                    Config.Reload();
                    Log.LogInfo("Settings migrated from " + OldGuid);
                }
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Settings migration failed: " + e.Message);
            }
        }
}
}
