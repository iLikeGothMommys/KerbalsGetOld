using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using KSP;
using KSP.UI.Screens;
using AGING_DFWrapper;

[KSPScenario(
    ScenarioCreationOptions.AddToAllGames,
    new GameScenes[] {
        GameScenes.SPACECENTER,
        GameScenes.FLIGHT,
        GameScenes.TRACKSTATION
    }
)]
public class KerbalAgingMod : ScenarioModule
{
    // --- UI Styles ---
    private GUIStyle headerStyle;
    private GUIStyle rowStyle;
    private GUIStyle columnStyle;
    private GUIStyle statusStyle;

    // --- Internal Data for Each Kerbal ---
    private class KerbalData
    {
        public int currentAge;
        public int deathAge;
        public bool isAlive;
        public int birthday;
        public int yearAdded;
        public int birthYear;
        public bool blessed;
        public bool immortal;
        public double deathUT;
        public bool unknownDeath;
        public bool unknownBirth;
    }
    private Dictionary<string, KerbalData> kerbalAges = new Dictionary<string, KerbalData>();

    private const double KERBIN_YEAR_SECONDS = 426.0 * 6.0 * 3600.0;
    private double lastUpdateUT = -1;

    // --- Configurable Ranges ---
    private int startAgeMin = 22;
    private int startAgeMax = 35;
    private int deathAgeMin = 300;
    private int deathAgeMax = 330;
    private string startAgeMinStr = "22";
    private string startAgeMaxStr = "35";
    private string deathAgeMinStr = "350";
    private string deathAgeMaxStr = "370";

    // --- Lock Settings ---
    private bool settingsLocked = false;
    private bool showLockConfirm = false;

    // --- Stock Toolbar Button ---
    private ApplicationLauncherButton appButton;

    // --- IMGUI Window Fields ---
    private bool showGUI = false;
    private Rect windowRect = new Rect(100, 100, 720, 500);
    private readonly int windowID = 12345678;
    private enum GuiTab { Kerbals, Settings }
    private GuiTab currentTab = GuiTab.Kerbals;
    private bool showAlive = true;
    private Vector2 listScrollPos = Vector2.zero;

    // --- Search Field ---
    private string searchQuery = "";

    // --- VIEW Button ---
    private string currentViewMode = "All";

    // --- Debug Fields ---
    private bool showDebug = false;
    private Vector2 debugScrollPos = Vector2.zero;
    private string debugAgeInput = "0";
    private string selectedKerbal = null;
    private string debugFrozenInput = "0";

    // --- Settings Tab Scroll ---
    private Vector2 settingsScrollPos = Vector2.zero;

    // --- DeepFreeze Integration ---
    private bool dfInitialized = false;
    private bool dfInitAttempted = false;
    private Dictionary<string, DFWrapper.KerbalInfo> cachedFrozenKerbals = null;

    // --- Sorting (only basic modes now) ---
    private enum SortMode { OldestFirst, YoungestFirst, AZ, ZA }
    private SortMode currentSort = SortMode.OldestFirst;

    // --- Cycle through Sort Modes ---
    private void CycleSortMode()
    {
        int enumCount = Enum.GetNames(typeof(SortMode)).Length;
        currentSort = (SortMode)(((int)currentSort + 1) % enumCount);
    }
    private string GetSortLabel()
    {
        switch (currentSort)
        {
            case SortMode.AZ: return "A to Z";
            case SortMode.ZA: return "Z to A";
            case SortMode.OldestFirst: return "Oldest First";
            case SortMode.YoungestFirst: return "Youngest First";
            default: return "";
        }
    }

    // --- DeepFreeze Check ---
    private bool HasFrozenKerbals()
    {
        try
        {
            if (DFWrapper.APIReady)
            {
                var frozen = DFWrapper.DeepFreezeAPI.FrozenKerbals;
                return frozen != null && frozen.Count > 0;
            }
        }
        catch (Exception) { }
        return false;
    }

    // --- KSP Lifecycle Methods ---
    public override void OnAwake()
    {
        base.OnAwake();
        GameEvents.onGUIApplicationLauncherReady.Add(OnAppLauncherReady);
    }
    public override void OnLoad(ConfigNode node)
    {
        base.OnLoad(node);
        ConfigNode ageNode = node.GetNode("KERBAL_AGE_DATA");
        if (ageNode != null)
        {
            kerbalAges.Clear();
            foreach (ConfigNode kNode in ageNode.GetNodes("KERBAL"))
            {
                string name = kNode.GetValue("name");
                int currentAge = int.Parse(kNode.GetValue("currentAge"));
                int dAge = int.Parse(kNode.GetValue("deathAge"));
                bool isAlive = bool.Parse(kNode.GetValue("isAlive"));
                int birthday = 1;
                int yearAdded = (int)(Planetarium.GetUniversalTime() / KERBIN_YEAR_SECONDS) + 1;
                int birthYear = 0;
                bool blessed = false;
                bool immortal = false;
                double deathUT = -1;
                bool unknownDeath = false;
                bool unknownBirth = false;
                if (kNode.HasValue("birthday"))
                    int.TryParse(kNode.GetValue("birthday"), out birthday);
                if (kNode.HasValue("yearAdded"))
                    int.TryParse(kNode.GetValue("yearAdded"), out yearAdded);
                if (kNode.HasValue("birthYear"))
                    int.TryParse(kNode.GetValue("birthYear"), out birthYear);
                if (kNode.HasValue("blessed"))
                    bool.TryParse(kNode.GetValue("blessed"), out blessed);
                if (kNode.HasValue("immortal"))
                    bool.TryParse(kNode.GetValue("immortal"), out immortal);
                if (kNode.HasValue("deathUT"))
                    double.TryParse(kNode.GetValue("deathUT"), out deathUT);
                if (kNode.HasValue("unknownDeath"))
                    bool.TryParse(kNode.GetValue("unknownDeath"), out unknownDeath);
                if (kNode.HasValue("unknownBirth"))
                    bool.TryParse(kNode.GetValue("unknownBirth"), out unknownBirth);
                kerbalAges[name] = new KerbalData
                {
                    currentAge = currentAge,
                    deathAge = dAge,
                    isAlive = isAlive,
                    birthday = birthday,
                    yearAdded = yearAdded,
                    birthYear = birthYear,
                    blessed = blessed,
                    immortal = immortal,
                    deathUT = deathUT,
                    unknownDeath = unknownDeath,
                    unknownBirth = unknownBirth
                };
            }
        }
        if (node.HasValue("startAgeMin")) int.TryParse(node.GetValue("startAgeMin"), out startAgeMin);
        if (node.HasValue("startAgeMax")) int.TryParse(node.GetValue("startAgeMax"), out startAgeMax);
        if (node.HasValue("deathAgeMin")) int.TryParse(node.GetValue("deathAgeMin"), out deathAgeMin);
        if (node.HasValue("deathAgeMax")) int.TryParse(node.GetValue("deathAgeMax"), out deathAgeMax);
        if (node.HasValue("settingsLocked")) bool.TryParse(node.GetValue("settingsLocked"), out settingsLocked);
        startAgeMinStr = startAgeMin.ToString();
        startAgeMaxStr = startAgeMax.ToString();
        deathAgeMinStr = deathAgeMin.ToString();
        deathAgeMaxStr = deathAgeMax.ToString();
        if (HighLogic.CurrentGame != null)
            CleanKerbalAges();
    }
    public override void OnSave(ConfigNode node)
    {
        base.OnSave(node);
        ConfigNode ageNode = node.HasNode("KERBAL_AGE_DATA") ? node.GetNode("KERBAL_AGE_DATA") : node.AddNode("KERBAL_AGE_DATA");
        ageNode.ClearData();
        ageNode.ClearNodes();
        foreach (var kvp in kerbalAges)
        {
            ConfigNode kNode = ageNode.AddNode("KERBAL");
            kNode.AddValue("name", kvp.Key);
            kNode.AddValue("currentAge", kvp.Value.currentAge);
            kNode.AddValue("deathAge", kvp.Value.deathAge);
            kNode.AddValue("isAlive", kvp.Value.isAlive);
            kNode.AddValue("birthday", kvp.Value.birthday);
            kNode.AddValue("yearAdded", kvp.Value.yearAdded);
            kNode.AddValue("birthYear", kvp.Value.birthYear);
            kNode.AddValue("blessed", kvp.Value.blessed);
            kNode.AddValue("immortal", kvp.Value.immortal);
            kNode.AddValue("deathUT", kvp.Value.deathUT);
            kNode.AddValue("unknownDeath", kvp.Value.unknownDeath);
            kNode.AddValue("unknownBirth", kvp.Value.unknownBirth);
        }
        node.AddValue("startAgeMin", startAgeMin);
        node.AddValue("startAgeMax", startAgeMax);
        node.AddValue("deathAgeMin", deathAgeMin);
        node.AddValue("deathAgeMax", deathAgeMax);
        node.AddValue("settingsLocked", settingsLocked);
    }
    public void OnDestroy()
    {
        GameEvents.onGUIApplicationLauncherReady.Remove(OnAppLauncherReady);
        if (appButton != null && ApplicationLauncher.Instance != null)
        {
            ApplicationLauncher.Instance.RemoveModApplication(appButton);
            appButton = null;
        }
    }

    // --- Crew Roster Updates & DeepFreeze Caching ---
    private double crewRosterUpdateInterval = 60.0;
    private double nextCrewRosterUpdateTime = 0.0;
    private void CleanKerbalAges()
    {
        if (HighLogic.CurrentGame == null) return;
        var roster = HighLogic.CurrentGame.CrewRoster;
        foreach (string key in kerbalAges.Keys.ToList())
        {
            ProtoCrewMember pcm = roster[key];
            if (pcm == null || pcm.trait == "Tourist")
            {
                kerbalAges.Remove(key);
                Debug.Log("[KerbalAgingMod] Removing kerbal " + key);
            }
        }
    }
    public void Update()
    {
        double currentUT = Planetarium.GetUniversalTime();
        if (currentUT <= 0) return;
        if (!dfInitialized && !dfInitAttempted)
        {
            dfInitialized = DFWrapper.InitDFWrapper();
            dfInitAttempted = true;
            Debug.Log(dfInitialized ? "[KerbalAgingMod] DeepFreeze successfully initialized." : "[KerbalAgingMod] DeepFreeze initialization failed or not installed.");
        }
        if (currentUT >= nextCrewRosterUpdateTime)
        {
            EnsureKerbalAgesAssigned();
            nextCrewRosterUpdateTime = currentUT + crewRosterUpdateInterval;
        }
        if (dfInitialized && DFWrapper.APIReady)
        {
            try
            {
                cachedFrozenKerbals = DFWrapper.DeepFreezeAPI.FrozenKerbals;
            }
            catch (Exception ex)
            {
                Debug.LogError("[KerbalAgingMod] Error caching frozen kerbals: " + ex);
                cachedFrozenKerbals = null;
            }
        }
        else
        {
            cachedFrozenKerbals = null;
        }
        if (HighLogic.CurrentGame == null) return;
        if (lastUpdateUT < 0)
        {
            lastUpdateUT = currentUT;
            return;
        }
        IncrementKerbalAges(lastUpdateUT, currentUT);
        lastUpdateUT = currentUT;
    }
    private void EnsureKerbalAgesAssigned()
    {
        CleanKerbalAges();
        var roster = HighLogic.CurrentGame.CrewRoster.Crew;
        int currentYear = (int)(Planetarium.GetUniversalTime() / KERBIN_YEAR_SECONDS) + 1;
        foreach (ProtoCrewMember pcm in roster)
        {
            if (pcm.trait == "Tourist") continue;
            if (!kerbalAges.ContainsKey(pcm.name))
            {
                int randomBirthday = UnityEngine.Random.Range(1, 426);
                if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                {
                    int randomDeadAge = UnityEngine.Random.Range(23, 81);
                    kerbalAges[pcm.name] = new KerbalData
                    {
                        currentAge = randomDeadAge,
                        deathAge = randomDeadAge,
                        isAlive = false,
                        birthday = randomBirthday,
                        yearAdded = currentYear,
                        birthYear = currentYear - randomDeadAge,
                        blessed = false,
                        immortal = false,
                        unknownBirth = true,
                        unknownDeath = true,
                        deathUT = -1
                    };
                }
                else
                {
                    int startAge = UnityEngine.Random.Range(startAgeMin, startAgeMax + 1);
                    bool blessed = UnityEngine.Random.Range(0, 50) == 0;
                    if (blessed) Debug.Log($"Kerbal {pcm.name} assigned as blessed.");
                    int baseDeathAge = UnityEngine.Random.Range(deathAgeMin, deathAgeMax + 1);
                    int dAge = blessed ? baseDeathAge + 50 : baseDeathAge;
                    kerbalAges[pcm.name] = new KerbalData
                    {
                        currentAge = startAge,
                        deathAge = dAge,
                        isAlive = true,
                        birthday = randomBirthday,
                        yearAdded = currentYear,
                        birthYear = currentYear - startAge,
                        blessed = blessed,
                        immortal = false,
                        unknownBirth = false,
                        unknownDeath = false,
                        deathUT = -1
                    };
                }
            }
            else
            {
                KerbalData data = kerbalAges[pcm.name];
                if (data.isAlive && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                {
                    data.isAlive = false;
                    data.deathAge = data.currentAge;
                    MarkKerbalDead(pcm.name);
                }
            }
        }
        if (dfInitialized && DFWrapper.APIReady && cachedFrozenKerbals != null)
        {
            foreach (var kvp in cachedFrozenKerbals)
            {
                string name = kvp.Key;
                ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[name];
                if (pcm == null || pcm.trait == "Tourist")
                    continue;
                if (!kerbalAges.ContainsKey(name))
                {
                    int randomBirthday = UnityEngine.Random.Range(1, 426);
                    int startAge = UnityEngine.Random.Range(startAgeMin, startAgeMax + 1);
                    bool blessed = UnityEngine.Random.Range(0, 50) == 0;
                    if (blessed) Debug.Log($"Frozen Kerbal {name} assigned as blessed.");
                    int baseDeathAge = UnityEngine.Random.Range(deathAgeMin, deathAgeMax + 1);
                    int dAge = blessed ? baseDeathAge + 50 : baseDeathAge;
                    kerbalAges[name] = new KerbalData
                    {
                        currentAge = startAge,
                        deathAge = dAge,
                        isAlive = true,
                        birthday = randomBirthday,
                        yearAdded = currentYear,
                        birthYear = currentYear - startAge,
                        blessed = blessed,
                        immortal = false,
                        unknownBirth = false,
                        unknownDeath = false,
                        deathUT = -1
                    };
                }
            }
        }
    }
    private bool IsKerbalFrozen(string kerbalName)
    {
        if (DFWrapper.APIReady && cachedFrozenKerbals != null)
            return cachedFrozenKerbals.ContainsKey(kerbalName);
        return false;
    }
    private void IncrementKerbalAges(double startTime, double endTime)
    {
        foreach (var kvp in kerbalAges)
        {
            KerbalData data = kvp.Value;
            if (data.isAlive)
            {
                if (IsKerbalFrozen(kvp.Key))
                    continue;
                int birthdays = CountBirthdaysBetween(data.birthday, startTime, endTime);
                data.currentAge += birthdays;
                if (data.currentAge >= data.deathAge)
                {
                    if (!data.immortal)
                    {
                        data.isAlive = false;
                        MarkKerbalDead(kvp.Key);
                    }
                }
            }
        }
    }
    private int CountBirthdaysBetween(int birthday, double startUT, double endUT)
    {
        double year = KERBIN_YEAR_SECONDS;
        double daySeconds = 6.0 * 3600.0;
        int count = 0;
        int startYear = (int)(startUT / year);
        int endYear = (int)(endUT / year);
        for (int y = startYear; y <= endYear; y++)
        {
            double birthdayTime = y * year + (birthday - 1) * daySeconds;
            if (birthdayTime > startUT && birthdayTime <= endUT)
                count++;
        }
        return count;
    }
    private void MarkKerbalDead(string kerbalName)
    {
        var pcm = HighLogic.CurrentGame?.CrewRoster?[kerbalName];
        if (pcm != null)
        {
            if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned)
            {
                Vessel vessel = null;
                foreach (var v in FlightGlobals.Vessels)
                {
                    if (v.GetVesselCrew().Contains(pcm))
                    {
                        vessel = v;
                        break;
                    }
                }
                if (vessel != null)
                {
                    foreach (Part part in vessel.parts)
                    {
                        if (part.protoModuleCrew.Contains(pcm))
                        {
                            part.RemoveCrewmember(pcm);
                            break;
                        }
                    }
                }
            }
            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;
        }
        if (kerbalAges.ContainsKey(kerbalName))
        {
            var data = kerbalAges[kerbalName];
            data.isAlive = false;
            data.deathUT = Planetarium.GetUniversalTime();
            data.unknownDeath = false;
        }
    }

    // --- Application Launcher Setup ---
    private void OnAppLauncherReady()
    {
        if (ApplicationLauncher.Instance == null || appButton != null) return;
        appButton = ApplicationLauncher.Instance.AddModApplication(
            onTrue: () =>
            {
                // Set default view mode on open.
                if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null &&
                    FlightGlobals.ActiveVessel.GetVesselCrew().Any(p => p.trait != "Tourist"))
                {
                    currentViewMode = "Active Vessel";
                }
                else
                {
                    currentViewMode = "All";
                }
                showGUI = true;
            },
            onFalse: () => { showGUI = false; },
            onHover: null,
            onHoverOut: null,
            onEnable: null,
            onDisable: null,
            visibleInScenes:
                ApplicationLauncher.AppScenes.SPACECENTER |
                ApplicationLauncher.AppScenes.FLIGHT |
                ApplicationLauncher.AppScenes.TRACKSTATION,
            texture: GameDatabase.Instance.GetTexture("KerbalAgingMod/Icons/Icon", false)
        );
    }

    // --- Main GUI Window ---
    public void OnGUI()
    {
        if (!showGUI) return;
        if (headerStyle == null)
        {
            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
        }
        if (rowStyle == null)
        {
            rowStyle = new GUIStyle(GUI.skin.box);
            rowStyle.normal.background = MakeTex(2, 2, new Color(0.2f, 0.2f, 0.2f, 0.5f));
        }
        if (columnStyle == null)
        {
            columnStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter
            };
        }
        if (statusStyle == null)
        {
            statusStyle = new GUIStyle(columnStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 5, 0, 0)
            };
        }
        GUI.skin = HighLogic.Skin;
        windowRect = GUILayout.Window(
            windowID,
            windowRect,
            WindowFunction,
            "Kerbal Aging",
            GUILayout.Width(720),
            GUILayout.Height(500)
        );
    }
    private Texture2D MakeTex(int width, int height, Color col)
    {
        Color[] pix = new Color[width * height];
        for (int i = 0; i < pix.Length; i++) pix[i] = col;
        Texture2D result = new Texture2D(width, height);
        result.SetPixels(pix);
        result.Apply();
        return result;
    }
    private void WindowFunction(int id)
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(currentTab == GuiTab.Kerbals, "Kerbals", GUI.skin.button, GUILayout.Width(100)))
            currentTab = GuiTab.Kerbals;
        if (GUILayout.Toggle(currentTab == GuiTab.Settings, "Settings", GUI.skin.button, GUILayout.Width(100)))
            currentTab = GuiTab.Settings;
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Close", GUILayout.Width(80)))
        {
            showGUI = false;
            if (appButton != null && appButton.toggleButton.Value)
                appButton.SetFalse(false);
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(5);
        switch (currentTab)
        {
            case GuiTab.Kerbals:
                DrawKerbalListTab();
                break;
            case GuiTab.Settings:
                DrawSettingsTab();
                break;
        }
        GUI.DragWindow();
    }

    // --- Kerbal List Tab ---
    private void DrawKerbalListTab()
    {
        if (HighLogic.CurrentGame != null)
            CleanKerbalAges();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(showAlive ? "Show Deceased" : "Show Alive", GUILayout.Width(120)))
        {
            showAlive = !showAlive;
            listScrollPos = Vector2.zero;
        }
        if (GUILayout.Button($"SORT: {GetSortLabel()}", GUILayout.Width(160)))
        {
            CycleSortMode();
            listScrollPos = Vector2.zero;
        }
        // Build VIEW button options:
        List<string> viewOptions = new List<string>();

        // Determine if we are in flight and have a valid active vessel with at least one non-tourist kerbal.
        bool showActiveVessel = false;
        if (showAlive && HighLogic.LoadedSceneIsFlight)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != null)
            {
                var nonTouristCrew = active.GetVesselCrew().Where(k => k.trait != "Tourist").ToList();
                if (nonTouristCrew.Count > 0)
                    showActiveVessel = true;
            }
        }

        // For alive kerbals, build role options only from those kerbals;
        // similarly for deceased kerbals.
        var roleOptions = kerbalAges.Where(kvp =>
        {
            ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[kvp.Key];
            if (pcm == null || pcm.trait == "Tourist") return false;
            return (showAlive && kvp.Value.isAlive) || (!showAlive && !kvp.Value.isAlive);
        })
        .Select(kvp => HighLogic.CurrentGame.CrewRoster[kvp.Key].trait)
        .Distinct()
        .OrderBy(r => r)
        .ToList();

        // Build the view options list.
        if (showAlive)
        {
            if (showActiveVessel)
            {
                viewOptions.Add("Active Vessel");
                viewOptions.Add("All");
                viewOptions.Add("Frozen");
            }
            else
            {
                viewOptions.Add("All");
                viewOptions.Add("Frozen");
            }
        }
        else
        {
            // For deceased kerbals, omit "Frozen".
            viewOptions.Add("All");
        }
        // Add role options.
        viewOptions.AddRange(roleOptions);

        if (GUILayout.Button("VIEW: " + currentViewMode, GUILayout.Width(160)))
        {
            int index = viewOptions.IndexOf(currentViewMode);
            index = (index + 1) % viewOptions.Count;
            currentViewMode = viewOptions[index];
            listScrollPos = Vector2.zero;
        }
        // Search field.
        {
            GUI.SetNextControlName("SearchField");
            Rect searchRect = GUILayoutUtility.GetRect(new GUIContent(searchQuery), GUI.skin.textField, GUILayout.Width(150));
            searchQuery = GUI.TextField(searchRect, searchQuery, GUI.skin.textField);
            if (string.IsNullOrEmpty(searchQuery) && GUI.GetNameOfFocusedControl() != "SearchField")
            {
                GUI.Label(searchRect, "Search", new GUIStyle(GUI.skin.textField) { normal = { textColor = Color.gray } });
            }
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(5);
        GUILayout.Label(showAlive ? "Alive Kerbals:" : "Deceased Kerbals:");
        GUILayout.Space(5);
        listScrollPos = GUILayout.BeginScrollView(listScrollPos, GUILayout.ExpandHeight(true));
        // Table header.
        if (showAlive)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", headerStyle, GUILayout.Width(160));
            GUILayout.Label("Current Age", headerStyle, GUILayout.Width(100));
            GUILayout.Label("Date of Birth", headerStyle, GUILayout.Width(150));
            GUILayout.Label("Skill", headerStyle, GUILayout.Width(100));
            if (DFWrapper.APIReady) GUILayout.Label("STATUS", headerStyle, GUILayout.Width(100));
            GUILayout.EndHorizontal();
        }
        else
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", headerStyle, GUILayout.Width(160));
            GUILayout.Label("Age at Death", headerStyle, GUILayout.Width(100));
            GUILayout.Label("Date of Birth", headerStyle, GUILayout.Width(150));
            GUILayout.Label("Date of Death", headerStyle, GUILayout.Width(100));
            GUILayout.Label("Skill", headerStyle, GUILayout.Width(100));
            GUILayout.EndHorizontal();
        }
        List<KeyValuePair<string, KerbalData>> list = new List<KeyValuePair<string, KerbalData>>();
        foreach (var kvp in kerbalAges)
        {
            ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[kvp.Key];
            if (pcm == null || pcm.trait == "Tourist")
                continue;
            KerbalData data = kvp.Value;
            bool alive = data.isAlive;
            if (!string.IsNullOrEmpty(searchQuery) && !kvp.Key.ToLower().Contains(searchQuery.ToLower()))
                continue;
            // Apply VIEW filter.
            if (currentViewMode != "All")
            {
                if (currentViewMode == "Frozen")
                {
                    if (!IsKerbalFrozen(kvp.Key))
                        continue;
                }
                else if (currentViewMode == "Active Vessel")
                {
                    if (FlightGlobals.ActiveVessel == null)
                        continue;
                    bool isOnActive = false;
                    if (!IsKerbalFrozen(kvp.Key))
                    {
                        isOnActive = FlightGlobals.ActiveVessel.GetVesselCrew().Any(p => p.name == kvp.Key && p.trait != "Tourist");
                    }
                    else
                    {
                        DFWrapper.KerbalInfo frozenInfo;
                        if (cachedFrozenKerbals.TryGetValue(kvp.Key, out frozenInfo))
                        {
                            if (frozenInfo.vesselID == FlightGlobals.ActiveVessel.id)
                                isOnActive = true;
                        }
                    }
                    if (!isOnActive)
                        continue;
                }
                else
                {
                    if (!string.Equals(pcm.trait, currentViewMode, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
            }
            if ((showAlive && alive) || (!showAlive && !alive))
                list.Add(kvp);
        }
        // Sorting by sort mode.
        switch (currentSort)
        {
            case SortMode.AZ:
                list.Sort((a, b) => a.Key.CompareTo(b.Key));
                break;
            case SortMode.ZA:
                list.Sort((a, b) => b.Key.CompareTo(a.Key));
                break;
            case SortMode.OldestFirst:
                list.Sort((a, b) =>
                {
                    int ageComparison = b.Value.currentAge.CompareTo(a.Value.currentAge);
                    if (ageComparison != 0)
                        return ageComparison;
                    if (a.Value.birthYear != b.Value.birthYear)
                        return a.Value.birthYear.CompareTo(b.Value.birthYear);
                    if (a.Value.birthYear <= 0)
                        return b.Value.birthday.CompareTo(a.Value.birthday);
                    return a.Value.birthday.CompareTo(b.Value.birthday);
                });
                break;
            case SortMode.YoungestFirst:
                list.Sort((a, b) =>
                {
                    int ageComparison = a.Value.currentAge.CompareTo(b.Value.currentAge);
                    if (ageComparison != 0)
                        return ageComparison;
                    if (a.Value.birthYear != b.Value.birthYear)
                        return b.Value.birthYear.CompareTo(a.Value.birthYear);
                    if (a.Value.birthYear <= 0)
                        return a.Value.birthday.CompareTo(b.Value.birthday);
                    return b.Value.birthday.CompareTo(a.Value.birthday);
                });
                break;
        }
        foreach (var kvp in list)
        {
            DrawKerbalRow(kvp.Key, kvp.Value);
        }
        GUILayout.EndScrollView();
    }

    // --- Draw a Single Kerbal Row ---
    private void DrawKerbalRow(string kerbalName, KerbalData data)
    {
        GUILayout.BeginHorizontal(rowStyle);

        // Draw Name: if alive, use conditional coloring; if deceased, always white.
        if (data.isAlive)
        {
            if (!data.immortal)
            {
                int yearsLeft = data.deathAge - data.currentAge;
                if (yearsLeft <= 4)
                    GUILayout.Label($"<color=#FF0000>{kerbalName}</color>", GUILayout.Width(160));
                else if (yearsLeft <= 10)
                    GUILayout.Label($"<color=#FFFF00>{kerbalName}</color>", GUILayout.Width(160));
                else
                    GUILayout.Label(kerbalName, GUILayout.Width(160));
            }
            else
            {
                GUILayout.Label(kerbalName, GUILayout.Width(160));
            }
        }
        else
        {
            GUILayout.Label($"<color=#FFFFFF>{kerbalName}</color>", GUILayout.Width(160));
        }

        if (data.isAlive)
        {
            GUILayout.Label(data.currentAge.ToString(), columnStyle, GUILayout.Width(100));
            GUILayout.Label(GetBirthString(data), columnStyle, GUILayout.Width(150));
            ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[kerbalName];
            string skill = (pcm != null) ? pcm.trait : "N/A";
            GUILayout.Label(skill, columnStyle, GUILayout.Width(100));

            if (DFWrapper.APIReady)
            {
                string status = IsKerbalFrozen(kerbalName) ? "FROZEN" : "";
                GUILayout.Label(status, statusStyle, GUILayout.Width(100));
            }
        }
        else
        {
            GUILayout.Label(data.currentAge.ToString(), columnStyle, GUILayout.Width(100));
            GUILayout.Label(GetBirthString(data), columnStyle, GUILayout.Width(150));

            string deathDateStr;
            if (data.unknownDeath || data.deathUT < 0)
                deathDateStr = "UNKNOWN";
            else
            {
                double dUT = data.deathUT;
                int deathYear = (int)(dUT / KERBIN_YEAR_SECONDS) + 1;
                double yearStartUT = (deathYear - 1) * KERBIN_YEAR_SECONDS;
                int deathDay = (int)((dUT - yearStartUT) / (6 * 3600)) + 1;
                deathDateStr = $"Y{deathYear}, DAY{deathDay}";
            }
            GUILayout.Label(deathDateStr, columnStyle, GUILayout.Width(100));

            ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[kerbalName];
            string skill = (pcm != null) ? pcm.trait : "N/A";
            GUILayout.Label(skill, columnStyle, GUILayout.Width(100));
        }

        GUILayout.EndHorizontal();
    }

    // --- Helper for Birth Date ---
    private string GetBirthString(KerbalData data)
    {
        if (!data.isAlive && data.unknownBirth)
            return "UNKNOWN";
        if (data.birthYear <= 0)
        {
            int displayYear = (-data.birthYear + 1);
            return $"Y{displayYear} B.S.C. DAY {data.birthday}";
        }
        else
        {
            return $"Y{data.birthYear}, DAY {data.birthday}";
        }
    }

    // --- Settings Tab ---
    private void DrawSettingsTab()
    {
        settingsScrollPos = GUILayout.BeginScrollView(settingsScrollPos, GUILayout.ExpandHeight(true));
        if (settingsLocked)
            GUILayout.Label("<color=#FF0000>Settings are LOCKED!</color>");
        else
        {
            GUILayout.Label("Configure Kerbal Aging Ranges:");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Start Age Min:", GUILayout.Width(120));
            startAgeMinStr = GUILayout.TextField(startAgeMinStr, GUILayout.Width(50));
            GUILayout.Label("Max:", GUILayout.Width(40));
            startAgeMaxStr = GUILayout.TextField(startAgeMaxStr, GUILayout.Width(50));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Death Age Min:", GUILayout.Width(120));
            deathAgeMinStr = GUILayout.TextField(deathAgeMinStr, GUILayout.Width(50));
            GUILayout.Label("Max:", GUILayout.Width(40));
            deathAgeMaxStr = GUILayout.TextField(deathAgeMaxStr, GUILayout.Width(50));
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Apply Aging Range", GUILayout.Width(150)))
                ApplyAgingRange();
            GUILayout.Space(10);
            showDebug = GUILayout.Toggle(showDebug, "Debug Menu");
            if (showDebug)
                DrawDebugMenu();
            GUILayout.Space(10);
            GUILayout.FlexibleSpace();
            if (!showLockConfirm)
            {
                if (GUILayout.Button("Lock Settings", GUILayout.Width(150)))
                    showLockConfirm = true;
            }
            else
            {
                GUILayout.Label("<color=#FF0000>WARNING: ONCE SETTINGS ARE LOCKED THEY CAN NOT BE UNLOCKED.\nTHIS WILL DISABLE THE DEBUG MENU.\nARE YOU SURE YOU WANT TO LOCK YOUR SETTINGS?</color>");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Yes", GUILayout.Width(80)))
                {
                    LockSettings();
                    showLockConfirm = false;
                }
                if (GUILayout.Button("No", GUILayout.Width(80)))
                {
                    showLockConfirm = false;
                }
                GUILayout.EndHorizontal();
            }
        }
        GUILayout.EndScrollView();
    }
    private void LockSettings()
    {
        settingsLocked = true;
        HighLogic.CurrentGame.Updated();
        GamePersistence.SaveGame(HighLogic.CurrentGame, "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
    }
    private void ApplyAgingRange()
    {
        int tmp;
        if (int.TryParse(startAgeMinStr, out tmp)) startAgeMin = tmp;
        if (int.TryParse(startAgeMaxStr, out tmp)) startAgeMax = tmp;
        if (int.TryParse(deathAgeMinStr, out tmp)) deathAgeMin = tmp;
        if (int.TryParse(deathAgeMaxStr, out tmp)) deathAgeMax = tmp;
        if (startAgeMin > startAgeMax) startAgeMax = startAgeMin;
        if (deathAgeMin > deathAgeMax) deathAgeMax = deathAgeMin;
        foreach (var kvp in kerbalAges)
        {
            var data = kvp.Value;
            if (data.isAlive)
            {
                data.deathAge = UnityEngine.Random.Range(deathAgeMin, deathAgeMax + 1);
                if (data.currentAge >= data.deathAge)
                {
                    if (!data.immortal)
                    {
                        data.isAlive = false;
                        MarkKerbalDead(kvp.Key);
                    }
                }
            }
        }
        HighLogic.CurrentGame.Updated();
        GamePersistence.SaveGame(HighLogic.CurrentGame, "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
    }
    private void DrawDebugMenu()
    {
        GUILayout.Label("Manual Kerbal Age Adjustment:");
        // Order kerbals alphabetically (A to Z) by name.
        debugScrollPos = GUILayout.BeginScrollView(debugScrollPos, GUILayout.Height(150));
        foreach (var kvp in kerbalAges.OrderBy(kvp => kvp.Key))
        {
            ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[kvp.Key];
            if (pcm == null || pcm.trait == "Tourist")
                continue;
            var data = kvp.Value;
            if (!data.isAlive) continue;
            if (GUILayout.Button(kvp.Key, GUILayout.Width(220)))
            {
                selectedKerbal = kvp.Key;
                debugAgeInput = data.currentAge.ToString();
            }
        }
        GUILayout.EndScrollView();
        if (selectedKerbal != null && kerbalAges.ContainsKey(selectedKerbal))
        {
            var data = kerbalAges[selectedKerbal];
            if (!data.isAlive)
                GUILayout.Label($"Selected: {selectedKerbal} (Deceased)");
            else
            {
                GUILayout.Label($"Selected: {selectedKerbal}");
                GUILayout.BeginHorizontal();
                GUILayout.Label("New Age:", GUILayout.Width(80));
                debugAgeInput = GUILayout.TextField(debugAgeInput, GUILayout.Width(60), GUILayout.Height(22));
                if (GUILayout.Button("Apply", GUILayout.Width(50)))
                {
                    int newAge;
                    if (int.TryParse(debugAgeInput, out newAge))
                    {
                        int currentYear = (int)(Planetarium.GetUniversalTime() / KERBIN_YEAR_SECONDS) + 1;
                        if (newAge < data.currentAge)
                            data.yearAdded = currentYear;
                        data.currentAge = Mathf.Max(0, newAge);
                        data.birthYear = data.yearAdded - data.currentAge;
                        if (data.currentAge >= data.deathAge)
                        {
                            if (!data.immortal)
                            {
                                data.isAlive = false;
                                MarkKerbalDead(selectedKerbal);
                            }
                        }
                    }
                }
                GUILayout.EndHorizontal();
                if (data.isAlive && DFWrapper.APIReady)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Add Frozen Years:", GUILayout.Width(80));
                    debugFrozenInput = GUILayout.TextField(debugFrozenInput, GUILayout.Width(60), GUILayout.Height(22));
                    if (GUILayout.Button("Apply", GUILayout.Width(50)))
                    {
                        int frozenYears;
                        if (int.TryParse(debugFrozenInput, out frozenYears))
                        {
                            data.currentAge = Mathf.Max(0, data.currentAge - frozenYears);
                            if (data.currentAge >= data.deathAge)
                            {
                                if (!data.immortal)
                                {
                                    data.isAlive = false;
                                    MarkKerbalDead(selectedKerbal);
                                }
                            }
                        }
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label("Blessed:", GUILayout.Width(80));
                bool newBlessed = GUILayout.Toggle(data.blessed, "");
                if (newBlessed != data.blessed)
                {
                    data.blessed = newBlessed;
                    if (data.blessed)
                        data.deathAge += 50;
                    else
                        data.deathAge -= 50;
                }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Immortal:", GUILayout.Width(80));
                bool newImmortal = GUILayout.Toggle(data.immortal, "");
                if (newImmortal != data.immortal)
                    data.immortal = newImmortal;
                GUILayout.EndHorizontal();
            }
        }
    }
}
