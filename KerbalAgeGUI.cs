using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using KSP;
using KSP.UI.Screens;
using AGING_DFWrapper;  // For DeepFreeze integration

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
    // UI Styles
    private GUIStyle headerStyle;
    private GUIStyle rowStyle;
    private GUIStyle columnStyle;
    private GUIStyle statusStyle;  // For the STATUS column in the list

    // -----------------------------------------------------------------------
    // 1. Internal Data Class
    // -----------------------------------------------------------------------
    private class KerbalData
    {
        public int currentAge;
        public int deathAge;
        public bool isAlive;
        public int birthday;     // 1..426
        public int yearAdded;    // The year they joined or were discovered
        public int birthYear;    // Calculated = yearAdded - currentAge
        public bool blessed;
        public bool immortal;    // Immortal for debug

        public double deathUT;   // UT of death
        public bool unknownDeath;  // If death date was discovered after the fact
        public bool unknownBirth;  // If birth date was never assigned
    }

    private Dictionary<string, KerbalData> kerbalAges = new Dictionary<string, KerbalData>();

    private const double KERBIN_YEAR_SECONDS = 426.0 * 6.0 * 3600.0;
    private double lastUpdateUT = -1;

    // -----------------------------------------------------------------------
    // 2. Configurable Ranges (Defaults)
    // -----------------------------------------------------------------------
    private int startAgeMin = 22;
    private int startAgeMax = 35;
    private int deathAgeMin = 300;
    private int deathAgeMax = 330;

    private string startAgeMinStr = "22";
    private string startAgeMaxStr = "35";
    private string deathAgeMinStr = "350";
    private string deathAgeMaxStr = "370";

    // -----------------------------------------------------------------------
    // 3. Lock Settings
    // -----------------------------------------------------------------------
    private bool settingsLocked = false;
    private bool showLockConfirm = false;

    // -----------------------------------------------------------------------
    // 4. Stock Toolbar Button
    // -----------------------------------------------------------------------
    private ApplicationLauncherButton appButton;

    // -----------------------------------------------------------------------
    // 5. IMGUI Window
    // -----------------------------------------------------------------------
    private bool showGUI = false;
    private Rect windowRect = new Rect(100, 100, 720, 500);
    private readonly int windowID = 12345678;

    private enum GuiTab { KerbalList, Settings }
    private GuiTab currentTab = GuiTab.KerbalList;

    private bool showAlive = true;
    private Vector2 listScrollPos = Vector2.zero;

    private bool showDebug = false;
    private Vector2 debugScrollPos = Vector2.zero;
    private string debugAgeInput = "0";
    private string selectedKerbal = null;
    private string debugFrozenInput = "0";

    private Vector2 settingsScrollPos = Vector2.zero;

    // DeepFreeze integration
    private bool dfInitialized = false;
    private bool dfInitAttempted = false;

    // Flag to ensure FixFrozenMarkedDead is called only once
    private bool fixedFrozenKerbals = false;

    // Sorting
    private enum SortMode { OldestFirst, YoungestFirst, AZ, ZA, Frozen }
    private SortMode currentSort = SortMode.OldestFirst;

    // -----------------------------------------------------------------------
    // Setup and Teardown
    // -----------------------------------------------------------------------
    public override void OnAwake()
    {
        base.OnAwake();
        GameEvents.onGUIApplicationLauncherReady.Add(OnAppLauncherReady);

        // Listen for changes to kerbals' status
        GameEvents.onKerbalStatusChange.Add(OnKerbalStatusChange);
    }

    public override void OnLoad(ConfigNode node)
    {
        base.OnLoad(node);

        // -------------------------
        // 1) Load existing mod data
        // -------------------------
        ConfigNode ageNode = node.GetNode("KERBAL_AGE_DATA");
        if (ageNode != null)
        {
            kerbalAges.Clear();
            foreach (ConfigNode kNode in ageNode.GetNodes("KERBAL"))
            {
                string name = kNode.GetValue("name");
                ProtoCrewMember pcm = HighLogic.CurrentGame?.CrewRoster[name];

                // Skip if kerbal is excluded (Tourist or Applicant) or does not exist in the roster
                if (IsKerbalExcluded(pcm) || pcm == null)
                    continue;

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

            // **New Addition:** Remove any kerbals from the dictionary that are now excluded
            List<string> excludedKerbals = kerbalAges.Keys
                .Where(name => IsKerbalExcluded(name))
                .ToList();

            foreach (string kerbal in excludedKerbals)
            {
                kerbalAges.Remove(kerbal);
                Debug.Log($"[KerbalAgingMod] Removed excluded kerbal '{kerbal}' from aging data.");
            }
        }

        // -------------------------
        // 2) Load other mod settings
        // -------------------------
        if (node.HasValue("startAgeMin")) int.TryParse(node.GetValue("startAgeMin"), out startAgeMin);
        if (node.HasValue("startAgeMax")) int.TryParse(node.GetValue("startAgeMax"), out startAgeMax);
        if (node.HasValue("deathAgeMin")) int.TryParse(node.GetValue("deathAgeMin"), out deathAgeMin);
        if (node.HasValue("deathAgeMax")) int.TryParse(node.GetValue("deathAgeMax"), out deathAgeMax);
        if (node.HasValue("settingsLocked")) bool.TryParse(node.GetValue("settingsLocked"), out settingsLocked);

        startAgeMinStr = startAgeMin.ToString();
        startAgeMaxStr = startAgeMax.ToString();
        deathAgeMinStr = deathAgeMin.ToString();
        deathAgeMaxStr = deathAgeMax.ToString();

        // -------------------------
        // 3) Scan for kerbals that are already dead
        // -------------------------
        ScanForKerbalsMarkedDead();
    }

    public override void OnSave(ConfigNode node)
    {
        base.OnSave(node);

        ConfigNode ageNode = node.HasNode("KERBAL_AGE_DATA")
            ? node.GetNode("KERBAL_AGE_DATA")
            : node.AddNode("KERBAL_AGE_DATA");

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
        GameEvents.onKerbalStatusChange.Remove(OnKerbalStatusChange);

        if (appButton != null && ApplicationLauncher.Instance != null)
        {
            ApplicationLauncher.Instance.RemoveModApplication(appButton);
            appButton = null;
        }
    }

    // -----------------------------------------------------------------------
    // 6. Main Update Loop
    // -----------------------------------------------------------------------
    public void Update()
    {
        double currentUT = Planetarium.GetUniversalTime();
        if (currentUT <= 0) return;

        // Attempt DeepFreeze initialization only once
        if (!dfInitialized && !dfInitAttempted)
        {
            dfInitialized = DFWrapper.InitDFWrapper();
            dfInitAttempted = true;
            Debug.Log(dfInitialized
                ? "[KerbalAgingMod] DeepFreeze successfully initialized."
                : "[KerbalAgingMod] DeepFreeze initialization failed or not installed.");
        }

        if (HighLogic.CurrentGame == null) return;
        EnsureKerbalAgesAssigned();

        // Call FixFrozenMarkedDead only once after DeepFreeze is initialized
        if (!fixedFrozenKerbals && dfInitialized && DFWrapper.APIReady)
        {
            FixFrozenMarkedDead();
            fixedFrozenKerbals = true;
        }

        if (lastUpdateUT < 0)
        {
            lastUpdateUT = currentUT;
            return;
        }

        IncrementKerbalAges(lastUpdateUT, currentUT);
        lastUpdateUT = currentUT;
    }

    // -----------------------------------------------------------------------
    // 7. Detect Kerbal Death
    // -----------------------------------------------------------------------
    /// <summary>
    /// Called whenever a kerbal’s status changes. If going from alive to Dead
    /// under the mod's watch, we record an actual death date (unknownDeath=false).
    /// </summary>
    private void OnKerbalStatusChange(ProtoCrewMember pcm, ProtoCrewMember.RosterStatus from, ProtoCrewMember.RosterStatus to)
    {
        // If they changed status to Dead, mark them dead with a real date
        if (to == ProtoCrewMember.RosterStatus.Dead && from != ProtoCrewMember.RosterStatus.Dead)
        {
            if (pcm != null && kerbalAges.ContainsKey(pcm.name))
            {
                KerbalData data = kerbalAges[pcm.name];

                // FIX: Ensure the kerbal is not frozen and not excluded before marking as dead
                if (data.isAlive && !IsKerbalFrozen(pcm.name) && !IsKerbalExcluded(pcm))
                {
                    data.isAlive = false;
                    data.deathAge = data.currentAge;

                    // We discovered this death in real time => proper date
                    MarkKerbalDead(pcm.name, discoveredOffline: false);
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // 8. Assign Ages for New (or Frozen) Kerbals
    // -----------------------------------------------------------------------
    private void EnsureKerbalAgesAssigned()
    {
        // Iterate through all relevant roster lists
        List<ProtoCrewMember> allKerbals = new List<ProtoCrewMember>();
        allKerbals.AddRange(HighLogic.CurrentGame.CrewRoster.Crew);

        int currentYear = (int)(Planetarium.GetUniversalTime() / KERBIN_YEAR_SECONDS) + 1;

        foreach (ProtoCrewMember pcm in allKerbals)
        {
            // Skip excluded kerbals (Tourists and Applicants)
            if (IsKerbalExcluded(pcm)) continue;

            // If not in our dictionary, assign new data
            if (!kerbalAges.ContainsKey(pcm.name))
            {
                bool isFrozen = IsKerbalFrozen(pcm.name);

                // Handle frozen kerbals separately
                if (isFrozen)
                {
                    // If kerbal is frozen but not in the dictionary, assign them as alive and frozen
                    kerbalAges[pcm.name] = new KerbalData
                    {
                        currentAge = UnityEngine.Random.Range(startAgeMin, startAgeMax + 1),
                        deathAge = UnityEngine.Random.Range(deathAgeMin, deathAgeMax + 1),
                        isAlive = true,
                        birthday = UnityEngine.Random.Range(1, 426),
                        yearAdded = currentYear,
                        birthYear = currentYear - UnityEngine.Random.Range(startAgeMin, startAgeMax + 1),
                        blessed = (UnityEngine.Random.Range(0, 50) == 0), // 1 in 50 chance
                        immortal = false,
                        unknownBirth = false,
                        unknownDeath = false,
                        deathUT = -1
                    };
                    continue;
                }

                int randomBirthday = UnityEngine.Random.Range(1, 426);
                if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                {
                    // They were discovered as dead => unknown
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
                        unknownBirth = true,   // never assigned
                        unknownDeath = true,   // died before we saw them
                        deathUT = -1
                    };
                }
                else
                {
                    // They are alive => assign normal range
                    int startAge = UnityEngine.Random.Range(startAgeMin, startAgeMax + 1);
                    bool blessed = (UnityEngine.Random.Range(0, 50) == 0); // 1 in 50 chance
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
                // If the mod says they're alive, but the game says dead, fix that:
                KerbalData data = kerbalAges[pcm.name];
                bool isFrozen = IsKerbalFrozen(pcm.name);

                if (data.isAlive && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead && !isFrozen)
                {
                    data.isAlive = false;
                    data.deathAge = data.currentAge;
                    MarkKerbalDead(pcm.name, discoveredOffline: true);
                }
            }
        }

        // **New Addition:** Remove any kerbals from the dictionary that are now excluded
        List<string> excludedKerbals = kerbalAges.Keys
            .Where(name => IsKerbalExcluded(name))
            .ToList();

        foreach (string kerbal in excludedKerbals)
        {
            kerbalAges.Remove(kerbal);
            Debug.Log($"[KerbalAgingMod] Removed excluded kerbal '{kerbal}' from aging data.");
        }
    }

    /// <summary>
    /// Determines if a kerbal should be excluded from the mod (Tourist or Applicant).
    /// </summary>
    /// <param name="pcm">The ProtoCrewMember to check.</param>
    /// <returns>True if the kerbal is a Tourist or Applicant; otherwise, false.</returns>
    private bool IsKerbalExcluded(ProtoCrewMember pcm)
    {
        if (pcm == null)
            return false;

        // Exclude based on trait and roster status
        return pcm.trait.Equals("Tourist", StringComparison.OrdinalIgnoreCase) ||
               HighLogic.CurrentGame.CrewRoster.Applicants.Contains(pcm);
    }

    /// <summary>
    /// Determines if a kerbal is a Tourist or Applicant by name.
    /// </summary>
    /// <param name="kerbalName">The name of the kerbal.</param>
    /// <returns>True if the kerbal is a Tourist or Applicant; otherwise, false.</returns>
    private bool IsKerbalExcluded(string kerbalName)
    {
        var pcm = HighLogic.CurrentGame?.CrewRoster[kerbalName];
        return IsKerbalExcluded(pcm);
    }

    private bool IsKerbalFrozen(string kerbalName)
    {
        try
        {
            if (DFWrapper.APIReady)
            {
                var frozen = DFWrapper.DeepFreezeAPI.FrozenKerbals;
                if (frozen != null && frozen.ContainsKey(kerbalName))
                    return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    // -----------------------------------------------------------------------
    // 9. Aging
    // -----------------------------------------------------------------------
    private void IncrementKerbalAges(double startTime, double endTime)
    {
        foreach (var kvp in kerbalAges)
        {
            KerbalData data = kvp.Value;
            if (!data.isAlive) continue;

            // Skip if frozen
            if (IsKerbalFrozen(kvp.Key))
                continue;

            int birthdays = CountBirthdaysBetween(data.birthday, startTime, endTime);
            data.currentAge += birthdays;

            if (data.currentAge >= data.deathAge)
            {
                if (!data.immortal)
                {
                    data.isAlive = false;
                    MarkKerbalDead(kvp.Key, discoveredOffline: false);
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

    // -----------------------------------------------------------------------
    // 10. Mark Kerbal Dead
    // -----------------------------------------------------------------------
    /// <summary>
    /// Marks a kerbal as dead in both the roster and our dictionary.
    /// If discoveredOffline == false, we set a real death date (unknownDeath=false).
    /// If discoveredOffline == true, we set unknownDeath=true and don't record a UT.
    /// </summary>
    private void MarkKerbalDead(string kerbalName, bool discoveredOffline)
    {
        // FIX: Ensure the kerbal is not frozen and not excluded before marking as dead
        if (IsKerbalFrozen(kerbalName) || IsKerbalExcluded(kerbalName))
        {
            Debug.Log($"[KerbalAgingMod] Attempted to mark frozen or excluded kerbal '{kerbalName}' as dead. Operation aborted.");
            return;
        }

        var pcm = HighLogic.CurrentGame?.CrewRoster?[kerbalName];
        if (pcm != null)
        {
            // Remove from any vessel
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
            KerbalData data = kerbalAges[kerbalName];
            data.isAlive = false;

            if (discoveredOffline)
            {
                // We do not know exactly when they died => unknown
                data.unknownDeath = true;
                data.deathUT = -1;
            }
            else
            {
                // We discovered the death in real time => known date
                data.unknownDeath = false;
                data.deathUT = Planetarium.GetUniversalTime();
            }
        }
    }

    // -----------------------------------------------------------------------
    // 11. Scan For Kerbals Already Dead (e.g. from old saves)
    // -----------------------------------------------------------------------
    private void ScanForKerbalsMarkedDead()
    {
        var roster = HighLogic.CurrentGame?.CrewRoster;
        if (roster == null) return;

        // Gather all kerbals from the existing lists
        List<ProtoCrewMember> allKerbals = new List<ProtoCrewMember>();
        allKerbals.AddRange(roster.Crew);

        // If they are flagged Dead by the game but not in our dictionary (or incorrectly in our dictionary),
        // mark them with unknown death info.
        foreach (ProtoCrewMember pcm in allKerbals)
        {
            // FIX: Skip excluded kerbals (Tourists and Applicants) and frozen kerbals
            if (IsKerbalExcluded(pcm) || IsKerbalFrozen(pcm.name)) continue;

            if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
            {
                // If not in our dictionary => brand new discovery
                if (!kerbalAges.TryGetValue(pcm.name, out KerbalData data))
                {
                    int randomDeadAge = UnityEngine.Random.Range(23, 81);
                    kerbalAges[pcm.name] = new KerbalData
                    {
                        currentAge = randomDeadAge,
                        deathAge = randomDeadAge,
                        isAlive = false,
                        birthday = UnityEngine.Random.Range(1, 426),
                        yearAdded = (int)(Planetarium.GetUniversalTime() / KERBIN_YEAR_SECONDS) + 1,
                        birthYear = 0,
                        blessed = false,
                        immortal = false,
                        deathUT = -1,
                        unknownDeath = true,
                        unknownBirth = true
                    };
                }
                else
                {
                    // In dictionary but says isAlive => must correct it
                    if (data.isAlive)
                    {
                        // FIX: Ensure the kerbal is not frozen or excluded before marking as dead
                        if (!IsKerbalFrozen(pcm.name) && !IsKerbalExcluded(pcm))
                        {
                            data.isAlive = false;
                            data.deathAge = data.currentAge;
                            // Because we only just discovered it, treat it as unknown
                            MarkKerbalDead(pcm.name, discoveredOffline: true);
                        }
                    }
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // NEW: Fix kerbals mistakenly marked as dead but are frozen
    // -----------------------------------------------------------------------
    private void FixFrozenMarkedDead()
    {
        var frozenList = DFWrapper.DeepFreezeAPI.FrozenKerbals;
        if (frozenList == null || frozenList.Count == 0) return;

        foreach (var kvp in frozenList)
        {
            string kerbalName = kvp.Key;

            if (kerbalAges.ContainsKey(kerbalName))
            {
                KerbalData data = kerbalAges[kerbalName];
                if (!data.isAlive)
                {
                    Debug.Log($"[KerbalAgingMod] Fixing kerbal '{kerbalName}' mistakenly marked as dead while frozen.");

                    // Set alive
                    data.isAlive = true;

                    // Clear death UT
                    data.deathUT = -1;

                    // Set unknownDeath to false, since death is now cleared
                    data.unknownDeath = false;

                    // Assign a new random deathAge
                    data.deathAge = UnityEngine.Random.Range(deathAgeMin, deathAgeMax + 1);

                    Debug.Log($"[KerbalAgingMod] Assigned new death age {data.deathAge} to kerbal '{kerbalName}'.");
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // 12. Stock App Launcher
    // -----------------------------------------------------------------------
    private void OnAppLauncherReady()
    {
        if (ApplicationLauncher.Instance == null || appButton != null) return;
        appButton = ApplicationLauncher.Instance.AddModApplication(
            onTrue: () => { showGUI = true; },
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

    // -----------------------------------------------------------------------
    // 13. OnGUI
    // -----------------------------------------------------------------------
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
            rowStyle = new GUIStyle(GUI.skin.box)
            {
                normal =
                {
                    background = MakeTex(2, 2, new Color(0.2f, 0.2f, 0.2f, 0.5f))
                }
            };
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
        for (int i = 0; i < pix.Length; i++)
            pix[i] = col;
        Texture2D result = new Texture2D(width, height);
        result.SetPixels(pix);
        result.Apply();
        return result;
    }

    private void WindowFunction(int id)
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(currentTab == GuiTab.KerbalList, "Kerbal List", GUI.skin.button, GUILayout.Width(100)))
            currentTab = GuiTab.KerbalList;
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
            case GuiTab.KerbalList:
                DrawKerbalListTab();
                break;
            case GuiTab.Settings:
                DrawSettingsTab();
                break;
        }

        GUI.DragWindow();
    }

    // -----------------------------------------------------------------------
    // 14. Kerbal List GUI
    // -----------------------------------------------------------------------
    private void DrawKerbalListTab()
    {
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
        GUILayout.EndHorizontal();

        GUILayout.Space(5);
        GUILayout.Label(showAlive ? "Alive Kerbals:" : "Deceased Kerbals:");
        GUILayout.Space(5);

        listScrollPos = GUILayout.BeginScrollView(listScrollPos, GUILayout.ExpandHeight(true));

        if (showAlive)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", headerStyle, GUILayout.Width(220));
            GUILayout.Label("Current Age", headerStyle, GUILayout.Width(100));
            GUILayout.Label("Date of Birth", headerStyle, GUILayout.Width(180));
            if (DFWrapper.APIReady) GUILayout.Label("STATUS", headerStyle, GUILayout.Width(100));
            GUILayout.EndHorizontal();
        }
        else
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", headerStyle, GUILayout.Width(220));
            GUILayout.Label("Age at Death", headerStyle, GUILayout.Width(100));
            GUILayout.Label("Date of Birth", headerStyle, GUILayout.Width(180));
            GUILayout.Label("Date of Death", headerStyle, GUILayout.Width(120));
            GUILayout.EndHorizontal();
        }

        // Filter the dictionary
        List<KeyValuePair<string, KerbalData>> list = new List<KeyValuePair<string, KerbalData>>();
        foreach (var kvp in kerbalAges)
        {
            KerbalData data = kvp.Value;

            // Exclude kerbals that should not be displayed
            ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[kvp.Key];
            if (pcm == null) continue;
            if (IsKerbalExcluded(pcm)) continue;

            if ((showAlive && data.isAlive) || (!showAlive && !data.isAlive))
            {
                list.Add(kvp);
            }
        }

        // Sort
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
                    // If same age, compare birthYear
                    int yearCompare = a.Value.birthYear.CompareTo(b.Value.birthYear);
                    if (yearCompare != 0) return yearCompare;
                    return a.Value.birthday.CompareTo(b.Value.birthday);
                });
                break;
            case SortMode.YoungestFirst:
                list.Sort((a, b) =>
                {
                    int ageComparison = a.Value.currentAge.CompareTo(b.Value.currentAge);
                    if (ageComparison != 0)
                        return ageComparison;
                    // If same, compare birthYear
                    int yearCompare = b.Value.birthYear.CompareTo(a.Value.birthYear);
                    if (yearCompare != 0) return yearCompare;
                    return a.Value.birthday.CompareTo(b.Value.birthday);
                });
                break;
            case SortMode.Frozen:
                list = list
                    .Where(k => IsKerbalFrozen(k.Key))
                    .OrderBy(k => k.Key)
                    .ToList();
                break;
        }

        // Draw each row
        foreach (var kvp in list)
        {
            DrawKerbalRow(kvp.Key, kvp.Value);
        }

        GUILayout.EndScrollView();
    }

    private void DrawKerbalRow(string kerbalName, KerbalData data)
    {
        GUILayout.BeginHorizontal(rowStyle);

        // Color the name if they are close to death
        if (data.isAlive && !data.immortal)
        {
            int yearsLeft = data.deathAge - data.currentAge;
            if (yearsLeft <= 4)
                GUILayout.Label($"<color=#FF0000>{kerbalName}</color>", GUILayout.Width(220));
            else if (yearsLeft <= 10)
                GUILayout.Label($"<color=#FFFF00>{kerbalName}</color>", GUILayout.Width(220));
            else
                GUILayout.Label(kerbalName, GUILayout.Width(220));
        }
        else
        {
            // Deceased or immortal, no special color
            GUILayout.Label(kerbalName, GUILayout.Width(220));
        }

        // Age
        GUILayout.Label(data.currentAge.ToString(), columnStyle, GUILayout.Width(100));

        // Birth date
        string birthInfo;
        if (!data.isAlive && data.unknownBirth)
        {
            birthInfo = "UNKNOWN";
        }
        else
        {
            if (data.birthYear <= 0)
            {
                // Negative year means earlier than Year 1
                int displayYear = -data.birthYear + 1;
                birthInfo = $"Y{displayYear} B.S.C. DAY {data.birthday}";
            }
            else
            {
                birthInfo = $"Y{data.birthYear}, DAY {data.birthday}";
            }
        }
        GUILayout.Label(birthInfo, columnStyle, GUILayout.Width(180));

        // If alive, show if Frozen
        if (data.isAlive && DFWrapper.APIReady)
        {
            GUILayout.Label(IsKerbalFrozen(kerbalName) ? "FROZEN" : "", statusStyle, GUILayout.Width(100));
        }

        // If dead, show date of death
        if (!data.isAlive)
        {
            // Already have final currentAge as Age at Death
            string deathDateStr;
            if (data.unknownDeath || data.deathUT < 0)
            {
                deathDateStr = "UNKNOWN";
            }
            else
            {
                double dUT = data.deathUT;
                int deathYear = (int)(dUT / KERBIN_YEAR_SECONDS) + 1;
                double yearStartUT = (deathYear - 1) * KERBIN_YEAR_SECONDS;
                int deathDay = (int)((dUT - yearStartUT) / (6 * 3600)) + 1;
                deathDateStr = $"Y{deathYear}, DAY {deathDay}";
            }
            GUILayout.Label(deathDateStr, columnStyle, GUILayout.Width(120));
        }

        GUILayout.EndHorizontal();
    }

    private string GetSortLabel()
    {
        switch (currentSort)
        {
            case SortMode.AZ: return "A to Z";
            case SortMode.ZA: return "Z to A";
            case SortMode.OldestFirst: return "Oldest First";
            case SortMode.YoungestFirst: return "Youngest First";
            case SortMode.Frozen: return "Frozen";
        }
        return "";
    }

    private void CycleSortMode()
    {
        int enumCount = Enum.GetNames(typeof(SortMode)).Length;
        do
        {
            currentSort = (SortMode)(((int)currentSort + 1) % enumCount);
        } while (currentSort == SortMode.Frozen && (!DFWrapper.APIReady || !HasFrozenKerbals()));
    }

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

    // -----------------------------------------------------------------------
    // 15. Settings GUI
    // -----------------------------------------------------------------------
    private void DrawSettingsTab()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Configure Kerbal Aging Ranges:", headerStyle);
        GUILayout.EndHorizontal();

        GUILayout.Space(10);

        settingsScrollPos = GUILayout.BeginScrollView(settingsScrollPos, GUILayout.ExpandHeight(true));

        if (settingsLocked)
        {
            GUILayout.Label("<color=#FF0000>Settings are LOCKED!</color>");
        }
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
            {
                ApplyAgingRange();
            }

            GUILayout.Space(10);

            // Debug menu
            showDebug = GUILayout.Toggle(showDebug, "Debug Menu");
            if (showDebug)
            {
                DrawDebugMenu();
            }

            GUILayout.Space(10);

            GUILayout.FlexibleSpace();
            if (!showLockConfirm)
            {
                if (GUILayout.Button("Lock Settings", GUILayout.Width(150)))
                    showLockConfirm = true;
            }
            else
            {
                GUILayout.Label("<color=#FF0000>WARNING: ONCE SETTINGS ARE LOCKED THEY CANNOT BE UNLOCKED.\n" +
                                "THIS WILL DISABLE THE DEBUG MENU.\nARE YOU SURE YOU WANT TO LOCK YOUR SETTINGS?</color>");

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

        // Ensure min <= max
        if (startAgeMin > startAgeMax) startAgeMax = startAgeMin;
        if (deathAgeMin > deathAgeMax) deathAgeMax = deathAgeMin;

        // Update any currently living kerbals
        foreach (var kvp in kerbalAges)
        {
            var data = kvp.Value;
            if (data.isAlive)
            {
                data.deathAge = UnityEngine.Random.Range(deathAgeMin, deathAgeMax + 1);
                // If the new range instantly kills them:
                if (data.currentAge >= data.deathAge && !data.immortal && !IsKerbalFrozen(kvp.Key))
                {
                    data.isAlive = false;
                    MarkKerbalDead(kvp.Key, discoveredOffline: false);
                }
            }
        }

        HighLogic.CurrentGame.Updated();
        GamePersistence.SaveGame(HighLogic.CurrentGame, "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
    }

    // -----------------------------------------------------------------------
    // 16. Debug Menu
    // -----------------------------------------------------------------------
    private void DrawDebugMenu()
    {
        GUILayout.Label("Manual Kerbal Age Adjustment:");
        debugScrollPos = GUILayout.BeginScrollView(debugScrollPos, GUILayout.Height(150));

        // List living kerbals for clicking
        foreach (var kvp in kerbalAges)
        {
            var data = kvp.Value;

            // Exclude kerbals that should not be displayed
            ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[kvp.Key];
            if (pcm == null) continue;
            if (IsKerbalExcluded(pcm)) continue;

            if (data.isAlive)
            {
                // Include frozen kerbals in the debug menu
                if (IsKerbalFrozen(kvp.Key))
                {
                    // Indicate they are frozen
                    if (GUILayout.Button($"{kvp.Key} (FROZEN)", GUILayout.Width(220)))
                    {
                        selectedKerbal = kvp.Key;
                        debugAgeInput = data.currentAge.ToString();
                    }
                }
                else
                {
                    if (GUILayout.Button(kvp.Key, GUILayout.Width(220)))
                    {
                        selectedKerbal = kvp.Key;
                        debugAgeInput = data.currentAge.ToString();
                    }
                }
            }
        }

        GUILayout.EndScrollView();

        if (selectedKerbal != null && kerbalAges.ContainsKey(selectedKerbal))
        {
            var data = kerbalAges[selectedKerbal];
            if (!data.isAlive)
            {
                GUILayout.Label($"Selected: {selectedKerbal} (Deceased)");
            }
            else
            {
                GUILayout.Label($"Selected: {selectedKerbal}");

                GUILayout.BeginHorizontal();
                GUILayout.Label("New Age:", GUILayout.Width(80));
                debugAgeInput = GUILayout.TextField(debugAgeInput, GUILayout.Width(60), GUILayout.Height(22));
                if (GUILayout.Button("Apply", GUILayout.Width(50)))
                {
                    if (int.TryParse(debugAgeInput, out int newAge))
                    {
                        int currentYear = (int)(Planetarium.GetUniversalTime() / KERBIN_YEAR_SECONDS) + 1;
                        // If we reduce the age, pretend they joined more recently
                        if (newAge < data.currentAge)
                        {
                            data.yearAdded = currentYear;
                        }
                        data.currentAge = Mathf.Max(0, newAge);
                        data.birthYear = data.yearAdded - data.currentAge;

                        // If that kills them
                        if (data.currentAge >= data.deathAge && !data.immortal && !IsKerbalFrozen(selectedKerbal))
                        {
                            data.isAlive = false;
                            MarkKerbalDead(selectedKerbal, discoveredOffline: false);
                        }
                    }
                }
                GUILayout.EndHorizontal();

                // Example: adjusting "frozen time"
                if (data.isAlive && DFWrapper.APIReady)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Add Frozen Years:", GUILayout.Width(100));
                    debugFrozenInput = GUILayout.TextField(debugFrozenInput, GUILayout.Width(60), GUILayout.Height(22));
                    if (GUILayout.Button("Apply", GUILayout.Width(50)))
                    {
                        if (int.TryParse(debugFrozenInput, out int frozenYears))
                        {
                            // Simulate adding frozen years by decreasing age
                            data.currentAge = Mathf.Max(0, data.currentAge - frozenYears);
                            data.birthYear = data.yearAdded - data.currentAge;

                            // If that kills them
                            if (data.currentAge >= data.deathAge && !data.immortal && !IsKerbalFrozen(selectedKerbal))
                            {
                                data.isAlive = false;
                                MarkKerbalDead(selectedKerbal, discoveredOffline: false);
                            }
                        }
                    }
                    GUILayout.EndHorizontal();
                }

                // Blessed toggle
                GUILayout.BeginHorizontal();
                GUILayout.Label("Blessed:", GUILayout.Width(80));
                bool newBlessed = GUILayout.Toggle(data.blessed, "");
                if (newBlessed != data.blessed)
                {
                    data.blessed = newBlessed;
                    if (newBlessed)
                    {
                        data.deathAge += 50;
                    }
                    else
                    {
                        // Un-bless: don't allow going below currentAge
                        data.deathAge = Math.Max(data.currentAge + 1, data.deathAge - 50);
                    }
                }
                GUILayout.EndHorizontal();

                // Immortal toggle
                GUILayout.BeginHorizontal();
                GUILayout.Label("Immortal:", GUILayout.Width(80));
                bool newImmortal = GUILayout.Toggle(data.immortal, "");
                if (newImmortal != data.immortal)
                {
                    data.immortal = newImmortal;
                }
                GUILayout.EndHorizontal();
            }
        }
    }
}
