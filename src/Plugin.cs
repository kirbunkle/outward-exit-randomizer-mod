using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using static OutwardExitRandomizerMod.Plugin;

namespace OutwardExitRandomizerMod
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.kirbunkle.outward-exit-randomizer-mod";
        public const string NAME = "Outward Exit Randomizer";
        public const string VERSION = "1.0.0";
        public const int OUTSIDE_THRESHOLD = 10;

        internal static ManualLogSource Log;

        public static ConfigEntry<bool> RemoveTravelRationRestriction;
        public static ConfigEntry<bool> ForceAllExitsToChange;
        public static ConfigEntry<bool> AlwaysStartInCierzo;
        public static ConfigEntry<bool> AlwaysKillOnHardcore;
        public static ConfigEntry<bool> SkipToFactionChoice;
        public static ConfigEntry<bool> RandomlyChooseFaction;
        public static ConfigEntry<bool> RandomlyChooseBreakthroughSkills;
        public static ConfigEntry<bool> AutomaticallyStartVendavelQuest;
        public static ConfigEntry<bool> AutomaticallyStartRustLichQuest;

        public static AreaSpawnMap RandomizedExits = null;

        public static string SaveFileName = $"{GUID}.{VERSION}.savedata";

        // AreaSpawn
        //
        // This is an AreaEnum-Int pair. The game uses this to determine which area and which spot in that area to send
        // the player when the player interacts with an exit entity.
        public class AreaSpawn : IEquatable<AreaSpawn>, IComparer<AreaSpawn>
        {
            public AreaManager.AreaEnum AreaEnum { get; set; }
            public int SpawnPoint { get; set; }

            public AreaSpawn(AreaManager.AreaEnum areaEnum = AreaManager.AreaEnum.ChersoDungeonsSmall, int spawnPoint = 0)
            {
                AreaEnum = areaEnum;
                SpawnPoint = spawnPoint;
            }

            public AreaSpawn(string dataString)
            {
                string[] splitStrings = dataString.Split(':');
                AreaEnum = (AreaManager.AreaEnum)int.Parse(splitStrings[0]);
                if (splitStrings.Length > 1)
                {
                    SpawnPoint = int.Parse(splitStrings[1]);
                }
                else
                {
                    SpawnPoint = 0;
                }
            }

            public override int GetHashCode()
            {
                return ((int)AreaEnum * 100) + SpawnPoint;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as AreaSpawn);
            }

            public bool Equals(AreaSpawn obj)
            {
                return (obj != null) && (obj.AreaEnum == this.AreaEnum) && (obj.SpawnPoint == this.SpawnPoint);
            }

            public int Compare(AreaSpawn x, AreaSpawn y)
            {
                return x.GetHashCode() - y.GetHashCode();
            }

            public override string ToString()
            {
                return $"{(int)AreaEnum}:{SpawnPoint}";
            }
        }

        // Exit
        //
        // This is a pair of AreaSpawns, a "From" location and a "To" location. The "From" AreaSpawn denotes where
        // the exit entity is, as well as the destination of another exit entity. The "To" AreaSpawn denotes where you
        // will go if you interact with the exit entity at the "From" location.
        public class Exit
        {
            public AreaSpawn From { get; set; }
            public AreaSpawn To { get; set; }

            public Exit(AreaSpawn from = null, AreaSpawn to = null)
            {
                From = from;
                To = to;
            }
        }

        // AreaGroup
        //
        // This is collection of exits, each labelled by a unique AreaGroupEnum identifier. The purpose of an area group is to
        // identify which Exits are accessible to each other from both directions. An example would be Chersonese outside, which
        // has many Exits from that area to other areas seperated only by walking.
        //
        // Some AreaGroups have a collection of AreaGroupEnums called ConnectedAreasViaOneWay. These are other AreaGroups that can
        // be reached from this area by a gate that can be opened, effectively combining the areas.
        public class AreaGroup 
        {
            public AreaGroupEnum AreaGroupEnum { get; set; }
            public Exit[] Exits { get; set; }
            public AreaGroupEnum[] ConnectedAreasViaOneWay { get; set; }

            public AreaGroup(AreaGroupEnum areaGroupEnum = AreaGroupEnum.CierzoHyenaBurrow, Exit[] exits = null, AreaGroupEnum[] connectedAreasViaOneWay = null)
            {
                AreaGroupEnum = areaGroupEnum;
                Exits = exits;
                ConnectedAreasViaOneWay = connectedAreasViaOneWay;
            }
        }

        // AreaSpawnMap
        //
        // This is the final result of shuffling the exits. It is stored in memory so that whenever an exit entity is loaded in
        // the game it gets its exit changed to something else.
        //
        // This contains a Serialize to convert the exit data into a string format, and a Deserialize to convert the string into
        // the exit data again. This is how saving and loading the exit data per save file is performed.
        public class AreaSpawnMap
        {
            public Dictionary<AreaSpawn, AreaSpawn> AreaSpawns { get; set; }

            public string AreaSpawnsSaveString { get; set; }

            public AreaSpawnMap(Dictionary<AreaSpawn, AreaSpawn> areaSpawns)
            {
                AreaSpawns = areaSpawns;
                AreaSpawnsSaveString = Serialize();
            }

            public AreaSpawnMap(string areaSpawnsSaveString)
            {
                AreaSpawnsSaveString = areaSpawnsSaveString;
                AreaSpawns = Deserialize();
            }

            public string Serialize()
            {
                if (AreaSpawns == null)
                {
                    return null;
                }

                string[] resultArray = new string[AreaSpawns.Count];
                int idx = 0;
                foreach (KeyValuePair<AreaSpawn, AreaSpawn> exitRef in AreaSpawns)
                {
                    resultArray[idx++] = $"{exitRef.Key}={exitRef.Value}";
                }

                return string.Join(",", resultArray);
            }

            public Dictionary<AreaSpawn, AreaSpawn> Deserialize()
            {
                if (AreaSpawnsSaveString == null)
                {
                    return null;
                }

                Dictionary<AreaSpawn, AreaSpawn> exitMap = new();
                string[] resultArray = AreaSpawnsSaveString.Split(',');
                foreach (string exitStr in resultArray)
                {
                    string[] spawnPoints = exitStr.Split('=');
                    exitMap.Add(new(spawnPoints[0]), new(spawnPoints[1]));
                }
                return exitMap;
            }
        }

        // AreaGroupShuffler
        //
        // This is where the heavy lifting of shuffling every exit occurs. It takes a list of exit groups and randomly arranges
        // the exits in a way that prevents areas from being inaccessible, turning the result into a AreaSpawnMap object.
        public class AreaGroupShuffler
        {
            public AreaGroup[] AreaGroups { get; set; }
            public AreaSpawn StartSpawn { get; set; }
            
            private Dictionary<AreaGroupEnum, AreaGroup> AreaGroupEnumToAreaGroupIndex = null;
            private Dictionary<AreaSpawn, AreaGroupEnum> AreaSpawnToAreaGroupEnumIndex = null;
            private Dictionary<AreaGroupEnum, AreaGroup> MultipleExitAreaGroupIndex = null;
            private Dictionary<AreaGroupEnum, AreaGroup> OutsideExitAreaGroupIndex = null;
            private Dictionary<AreaGroupEnum, AreaGroup> SingleExitAreaGroupIndex = null;
            private List<Exit> CoreExitList = null;
            private Dictionary<AreaSpawn, AreaSpawn> AreaSpawnToToFrom = null;
            private List<AreaSpawn> UnusedAreaSpawns = null;

            public AreaGroupShuffler(AreaGroup[] areaGroups = null, AreaSpawn startSpawn = null)
            {
                AreaGroups = areaGroups;
                StartSpawn = startSpawn;
            }

            public Dictionary<AreaSpawn, AreaSpawn> Shuffle(AreaSpawn startSpawn = null)
            {
                Dictionary<AreaSpawn, AreaSpawn> newExits = new();
                if (!ResetIndexes(newExits))
                {
                    Log.LogError($"Cannot shuffle without indexes.");
                    return null;
                }

                if (startSpawn != null)
                {
                    StartSpawn = startSpawn;
                }

                if (StartSpawn == null)
                {
                    Log.LogError($"Cannot shuffle without start spawn.");
                    return null;
                }

                AreaGroupEnum startingAreaGroupEnum = AreaSpawnToAreaGroupEnumIndex[StartSpawn];
                AreaGroup startingAreaGroup = AreaGroupEnumToAreaGroupIndex[startingAreaGroupEnum];

                if (startingAreaGroup == null)
                {
                    Log.LogError($"Cannot find starting area.");
                    return null;
                }

                AddAreaGroupToCoreExitList(startingAreaGroup);
                Exit previousExit = null;
                AreaGroup nextAreaGroup = null;
                bool setLevant4 = true;
                int outsideBuffer = UnityEngine.Random.Range(1, 3);

                Log.LogDebug($"Unused Area Spawns Before: {UnusedAreaSpawns.Count}");

                // First, connect every area group that has multiple exits. This ensures every area is accessible.
                while (MultipleExitAreaGroupIndex.Count > 0)
                {
                    // Select a previous area group from the core groups
                    previousExit = FindCoreExit();

                    if (previousExit == null)
                    {
                        Log.LogError($"Unable to find core area group for remaining multi-exit areas: {MultipleExitAreaGroupIndex.Count}");
                        break;
                    }

                    nextAreaGroup = null;
                    if (setLevant4 && outsideBuffer <= 0)
                    {
                        // Connect to outside area
                        nextAreaGroup = OutsideExitAreaGroupIndex.ElementAt(UnityEngine.Random.Range(0, OutsideExitAreaGroupIndex.Count)).Value;
                    }

                    // Connect to any multi-exit area
                    nextAreaGroup ??= MultipleExitAreaGroupIndex.ElementAt(UnityEngine.Random.Range(0, MultipleExitAreaGroupIndex.Count)).Value;

                    AddAreaGroupToCoreExitList(nextAreaGroup);

                    ConnectExitToAreaGroup(previousExit, nextAreaGroup, newExits);

                    if (setLevant4 && (nextAreaGroup.Exits.Length >= OUTSIDE_THRESHOLD) && (nextAreaGroup.AreaGroupEnum != AreaGroupEnum.LevantOutside))
                    {
                        // This area needs to be accessible during chapter 4 when some LevantOutside doors lock. Connect it here.
                        if (SingleExitAreaGroupIndex.ContainsKey(AreaGroupEnum.LevantCastleSecretEntrance))
                        {
                            AreaGroup levant4AreaGroup = AreaGroupEnumToAreaGroupIndex[AreaGroupEnum.LevantCastleSecretEntrance];
                            ConnectExitToAreaGroup(levant4AreaGroup.Exits[0], nextAreaGroup, newExits);
                            RemoveFromIndexes(levant4AreaGroup.AreaGroupEnum);
                        }
                        setLevant4 = false;
                    }

                    outsideBuffer--;
                }

                Log.LogDebug($"Unused Area Spawns After Connective Pass: {UnusedAreaSpawns.Count}");

                // Next, connect all single-exit area groups to the core area structure
                while (SingleExitAreaGroupIndex.Count > 0)
                {
                    previousExit = FindCoreExit();                    

                    if (previousExit == null)
                    {
                        Log.LogError($"Unable to find core area group for remaining single-exit areas: {SingleExitAreaGroupIndex.Count}");
                        foreach (KeyValuePair<AreaGroupEnum, AreaGroup> singleExitAreaGroupRef in SingleExitAreaGroupIndex)
                        {
                            Log.LogError($"AreaGroup: {singleExitAreaGroupRef.Key}, Available Exits: {AvailableExitCount(singleExitAreaGroupRef.Value, true)}");
                        }
                        break;
                    }

                    nextAreaGroup = SingleExitAreaGroupIndex.ElementAt(UnityEngine.Random.Range(0, SingleExitAreaGroupIndex.Count)).Value;
                    RemoveFromIndexes(nextAreaGroup.AreaGroupEnum);
                    ConnectExitToAreaGroup(previousExit, nextAreaGroup, newExits);
                }

                Log.LogDebug($"Unused Area Spawns After Single-exit Pass: {UnusedAreaSpawns.Count}");

                // Finally, shuffle the remaining exits. These should all be exits from multi-exit areas.
                ShuffleUnconnectedExits(newExits);
                Log.LogDebug($"Unused Area Spawns Last of all: {UnusedAreaSpawns.Count}");

                return newExits;
            }

            private void ConnectExitToAreaGroup(Exit sourceExit, AreaGroup destAreaGroup, Dictionary<AreaSpawn, AreaSpawn> newExits)
            {
                // If configured, we need to retry the connection if the exits are connected in the vanilla game. Just try twice.
                int attempts = 0;
                while (attempts < 2)
                {
                    // Choose a random available exit from the dest
                    Exit destExit = null;
                    int destExitIdx = 0;
                    int destExitMod = UnityEngine.Random.Range(0, destAreaGroup.Exits.Length);
                    while (destExitIdx < destAreaGroup.Exits.Length)
                    {
                        Exit potentialDestExit = destAreaGroup.Exits[(destExitIdx + destExitMod) % destAreaGroup.Exits.Length];
                        if (AreaSpawnIsAvailable(potentialDestExit.To))
                        {
                            destExit = potentialDestExit;
                            break;
                        }
                        destExitIdx++;
                    }

                    if (destExit == null)
                    {
                        Log.LogError($"Cannot find valid exit for AreaGroup {destAreaGroup.AreaGroupEnum}.");
                        return;
                    }

                    // Connect the exits
                    if (sourceExit != null && destExit != null)
                    {
                        if (!ForceAllExitsToChange.Value || (sourceExit.To != destExit.From) || (attempts >= 1))
                        {
                            // Only connect if we aren't configured to validate the exits being connected, or they are not connected in the vanilla game.
                            ConnectExits(sourceExit, destExit, newExits);
                            RemoveFromIndexes(destAreaGroup.AreaGroupEnum);
                            break;
                        }
                    }
                    else
                    {
                        Log.LogError($"Failed to connect exits to {destAreaGroup.AreaGroupEnum}.");
                    }

                    attempts++;
                }
            }

            private Exit FindCoreExit()
            {
                if (CoreExitList.Count > 0)
                {
                    return CoreExitList[UnityEngine.Random.Range(0, CoreExitList.Count)];
                }
                return null;
            }

            private void AddAreaGroupToCoreExitList(AreaGroup areaGroup)
            {
                RemoveFromIndexes(areaGroup.AreaGroupEnum);

                foreach (Exit exit in areaGroup.Exits)
                {
                    if (AreaSpawnIsAvailable(exit.To) && !CoreExitList.Contains(exit))
                    {
                        CoreExitList.Add(exit);
                    }
                }

                if (areaGroup.ConnectedAreasViaOneWay != null)
                {
                    foreach (AreaGroupEnum connectedAreaGroupEnum in areaGroup.ConnectedAreasViaOneWay)
                    {
                        AreaGroup connectedAreaGroup = AreaGroupEnumToAreaGroupIndex[connectedAreaGroupEnum];
                        AddAreaGroupToCoreExitList(connectedAreaGroup);
                    }
                }
            }

            private void ConnectExits(Exit sourceExit, Exit destExit, Dictionary<AreaSpawn, AreaSpawn> newExits)
            {
                
                newExits[sourceExit.To] = destExit.From;
                newExits[destExit.To] = sourceExit.From;
                UseAreaSpawn(sourceExit.To);
                UseAreaSpawn(destExit.To);
                CoreExitList.Remove(sourceExit);
                CoreExitList.Remove(destExit);
            }

            private void RemoveFromIndexes(AreaGroupEnum areaGroupEnum)
            {
                MultipleExitAreaGroupIndex.Remove(areaGroupEnum);
                SingleExitAreaGroupIndex.Remove(areaGroupEnum);
                OutsideExitAreaGroupIndex.Remove(areaGroupEnum);
            }

            private bool AreaSpawnIsAvailable(AreaSpawn areaSpawn)
            {
                return UnusedAreaSpawns.Contains(areaSpawn);
            }

            private int AvailableExitCount(AreaGroup areaGroup, bool checkConnected = false)
            {
                int count = 0;
                foreach (Exit exit in areaGroup.Exits)
                {
                    if (AreaSpawnIsAvailable(exit.To))
                    {
                        count++;
                    }
                }
                if (checkConnected && areaGroup.ConnectedAreasViaOneWay != null)
                {
                    foreach (AreaGroupEnum connectedAreaGroupEnum in areaGroup.ConnectedAreasViaOneWay)
                    {
                        AreaGroup connectedAreaGroup = AreaGroupEnumToAreaGroupIndex[connectedAreaGroupEnum];
                        count += AvailableExitCount(connectedAreaGroup, true);
                    }
                }
                return count;
            }

            public void UseAreaSpawn(AreaSpawn areaSpawn)
            {
                if (UnusedAreaSpawns.Contains(areaSpawn))
                {
                    UnusedAreaSpawns.Remove(areaSpawn);
                }
                else
                {
                    Log.LogWarning($"Tried to remove Area Spawn, but it wasn't in UnusedAreaSpawns list: {areaSpawn.AreaEnum} - {areaSpawn.SpawnPoint}");
                }
            }

            private void ShuffleUnconnectedExits(Dictionary<AreaSpawn, AreaSpawn> newExits)
            {
                int randomNewExitMod;
                int newExitIdx;
                int unusedExitIdx;
                AreaSpawn unusedFrom;
                AreaSpawn unusedTo;
                KeyValuePair<AreaSpawn, AreaSpawn> newExitRef;

                while (UnusedAreaSpawns.Count > 0)
                {
                    unusedExitIdx = UnityEngine.Random.Range(0, UnusedAreaSpawns.Count);
                    unusedTo = UnusedAreaSpawns[unusedExitIdx];
                    UseAreaSpawn(unusedTo);

                    if (!newExits.ContainsKey(unusedTo))
                    {
                        Log.LogWarning($"To-Exit not found in newExits list: {unusedTo.AreaEnum} - {unusedTo.SpawnPoint}");
                    }
                    else if(!AreaSpawnToToFrom.ContainsKey(unusedTo))
                    {
                        Log.LogWarning($"To-Exit not found in AreaSpawnToToFrom list: {unusedTo.AreaEnum} - {unusedTo.SpawnPoint}");
                    }
                    else
                    {
                        unusedFrom = AreaSpawnToToFrom[unusedTo];
                        randomNewExitMod = UnityEngine.Random.Range(0, newExits.Count);
                        newExitIdx = 0;

                        while (newExitIdx < newExits.Count)
                        {
                            newExitRef = newExits.ElementAt((newExitIdx + randomNewExitMod) % newExits.Count);
                            if (newExitRef.Value == null && !newExitRef.Key.Equals(unusedTo))
                            {
                                if (!AreaSpawnToToFrom.ContainsKey(newExitRef.Key))
                                {
                                    Log.LogWarning($"To-Exit not found in newExits or AreaSpawnToToFrom list: {newExitRef.Key.AreaEnum} - {newExitRef.Key.SpawnPoint}");
                                }
                                else
                                {
                                    // Check if connection exists in the base game, don't use it unless it's the last one.
                                    if (!ForceAllExitsToChange.Value || (unusedTo != AreaSpawnToToFrom[newExitRef.Key]) || (UnusedAreaSpawns.Count <= 1))
                                    {
                                        newExits[newExitRef.Key] = unusedFrom;
                                        UseAreaSpawn(newExitRef.Key);
                                        newExits[unusedTo] = AreaSpawnToToFrom[newExitRef.Key];
                                        break;
                                    }
                                }
                            }
                            newExitIdx++;
                        }
                    }
                }

                if (UnusedAreaSpawns.Count > 0)
                {
                    Log.LogWarning($"{UnusedAreaSpawns.Count} exits were not connected.");
                }
            }

            private bool ResetIndexes(Dictionary<AreaSpawn, AreaSpawn> newExits)
            {
                if (AreaGroups != null)
                {
                    AreaGroupEnumToAreaGroupIndex = new Dictionary<AreaGroupEnum, AreaGroup>();
                    AreaSpawnToAreaGroupEnumIndex = new Dictionary<AreaSpawn, AreaGroupEnum>();
                    MultipleExitAreaGroupIndex = new Dictionary<AreaGroupEnum, AreaGroup>();
                    OutsideExitAreaGroupIndex = new Dictionary<AreaGroupEnum, AreaGroup>();
                    SingleExitAreaGroupIndex = new Dictionary<AreaGroupEnum, AreaGroup>();
                    CoreExitList = new List<Exit>();
                    AreaSpawnToToFrom = new Dictionary<AreaSpawn, AreaSpawn>();
                    UnusedAreaSpawns = new List<AreaSpawn>();

                    foreach (AreaGroup areaGroup in AreaGroups)
                    {
                        AreaGroupEnumToAreaGroupIndex.Add(areaGroup.AreaGroupEnum, areaGroup);

                        if (areaGroup.Exits.Length > 1 || areaGroup.ConnectedAreasViaOneWay != null)
                        {
                            MultipleExitAreaGroupIndex.Add(areaGroup.AreaGroupEnum, areaGroup);
                            if (areaGroup.Exits.Length >= OUTSIDE_THRESHOLD)
                            {
                                OutsideExitAreaGroupIndex.Add(areaGroup.AreaGroupEnum, areaGroup);
                            }
                        }
                        else
                        {
                            SingleExitAreaGroupIndex.Add(areaGroup.AreaGroupEnum, areaGroup);
                        }

                        foreach (Exit exit in areaGroup.Exits)
                        {
                            if (newExits.ContainsKey(exit.To))
                            {
                                Log.LogWarning($"Duplicate To-Exit found! AreaEnum: {exit.To.AreaEnum}, SpawnPoint: {exit.To.SpawnPoint}");
                            }
                            else
                            {
                                newExits.Add(exit.To, null);
                                if (UnusedAreaSpawns.Contains(exit.To))
                                {
                                    Log.LogWarning($"Duplicate To-Exit found! AreaEnum: {exit.To.AreaEnum}, SpawnPoint: {exit.To.SpawnPoint}");
                                }
                                else
                                {
                                    UnusedAreaSpawns.Add(exit.To);
                                }
                                if (AreaSpawnToToFrom.ContainsKey(exit.To))
                                {
                                    Log.LogWarning($"Duplicate To-Exit found! AreaEnum: {exit.To.AreaEnum}, SpawnPoint: {exit.To.SpawnPoint}");
                                }
                                else
                                {
                                    AreaSpawnToToFrom.Add(exit.To, exit.From);
                                }
                                AreaSpawnToAreaGroupEnumIndex.Add(exit.From, areaGroup.AreaGroupEnum);
                            }
                        }
                    }
                    return true;
                }

                Log.LogWarning($"No AreaGroups Found.");
                return false;
            }
        }

        internal void Awake()
        {
            Log = this.Logger;
            Log.LogMessage($"Begin Initializing {NAME} {VERSION}");

            ForceAllExitsToChange = Config.Bind("Map Generation Settings", "ForceAllExitsToChange", true, "If true, (almost) always ensures that no exit that could be randomized goes where it does in the vanilla game.");

            SkipToFactionChoice = Config.Bind("Randomizer Settings", "SkipToFactionChoice", true, "If true, automatically skips the introduction quest and allows the player to keep the Cierzo house.");
            RandomlyChooseFaction = Config.Bind("Randomizer Settings", "RandomlyChooseFaction", true, "If true, automatically skips the introduction quest, keeps the Cierzo house, and joins a random faction. Supersedes SkipToFactionChoice.");
            RandomlyChooseBreakthroughSkills = Config.Bind("Randomizer Settings", "RandomlyChooseBreakthroughSkills", true, "If true, automatically awards the player 3 random breakthrough skills and uses all their breakthrough points.");
            AutomaticallyStartVendavelQuest = Config.Bind("Randomizer Settings", "AutomaticallyStartVendavelQuest", true, "If true, the Vendavel quest appears in the quest log when the game starts. Only done if RandomlyChooseFaction is true.");
            AutomaticallyStartRustLichQuest = Config.Bind("Randomizer Settings", "AutomaticallyStartRustLichQuest", true, "If true, the Rust and Vengeance quest appears in the quest log when the game starts. Only done if RandomlyChooseFaction is true.");

            AlwaysStartInCierzo = Config.Bind("Gameplay Settings", "AlwaysStartInCierzo", false, "If true, start in Cierzo. If false, start at a random location.");
            RemoveTravelRationRestriction = Config.Bind("Gameplay Settings", "RemoveTravelRationRestriction", true, "If true, disables the requirement for travel rations when switching areas. Time also won't pass when travelling. (Doesn't apply to Merchant Fast Travel)");
            AlwaysKillOnHardcore = Config.Bind("Gameplay Settings", "AlwaysKillOnHardcore", false, "The true rogue-like experience. If true, playing hardcore mode will always result in the player save being deleted when the player is defeated.");
            new Harmony(GUID).PatchAll();
        }

        [HarmonyPatch(typeof(InteractionSwitchArea), nameof(InteractionSwitchArea.AwakeInit))]
        public class InteractionSwitchArea_AwakeInit
        {
            static bool Prefix(ref InteractionSwitchArea __instance)
            {
                Log.LogDebug($"InteractionSwitchArea_AwakeInit Prefix");

                if (RandomizedExits != null)
                {
                    AreaSpawn oldExit = new(__instance.Area, __instance.SpawnPoint);
                    if (RandomizedExits.AreaSpawns.ContainsKey(oldExit))
                    {
                        AreaSpawn newExit = RandomizedExits.AreaSpawns[oldExit];
                        Log.LogDebug($"Changing Exit ({__instance.Area} - {__instance.SpawnPoint}) to ({newExit.AreaEnum} - {newExit.SpawnPoint})");
                        __instance.Area = newExit.AreaEnum;
                        __instance.SpawnPoint = newExit.SpawnPoint;
                        if (RemoveTravelRationRestriction.Value)
                        {
                            __instance.FastTravelTime = 0;
                        }
                    }
                    else
                    {
                        Log.LogWarning($"Could not find mapping for exit: ({__instance.Area} - {__instance.SpawnPoint})");
                    }
                }
                else
                {
                    Log.LogDebug($"Exits not randomized.");
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(NetworkLevelLoader), nameof(NetworkLevelLoader.LoadLevel), new Type[] {
            typeof(string),
            typeof(int),
            typeof(float),
            typeof(bool)
        })]
        public class NetworkLevelLoader_LoadLevel
        {
            static bool Prefix(ref NetworkLevelLoader __instance, ref string _levelName, ref int _spawnPoint, ref float _spawnOffset, ref bool _save)
            {
                Log.LogDebug($"NetworkLevelLoader_LoadLevel Prefix");

                if (_levelName == "CierzoTutorial")
                {
                    AreaSpawn startSpawn;

                    if (AlwaysStartInCierzo.Value)
                    {
                        startSpawn = new(AreaManager.AreaEnum.CierzoVillage, 1);
                    }
                    else
                    {
                        // Warp to Random Start Location
                        AreaGroup startAreaGroup = DefaultAreaGroups[UnityEngine.Random.Range(0, DefaultAreaGroups.Length)];
                        Exit startExit = startAreaGroup.Exits[UnityEngine.Random.Range(0, startAreaGroup.Exits.Length)];

                        startSpawn = startExit.From;
                        _levelName = AreaManager.Instance.GetArea(startSpawn.AreaEnum).SceneName;
                        _spawnPoint = startSpawn.SpawnPoint;

                        Log.LogMessage($"Skipping intro, warping to area: ({startSpawn.AreaEnum} - {startSpawn.SpawnPoint})");
                    }

                    // Shuffle Exits
                    AreaGroupShuffler exitShuffler = new(DefaultAreaGroups, startSpawn);
                    RandomizedExits = new(exitShuffler.Shuffle());

                    if (SkipToFactionChoice.Value || RandomlyChooseFaction.Value)
                    {
                        SkipIntroQuest();
                        if (RandomlyChooseFaction.Value)
                        {
                            JoinRandomFaction();
                            if (AutomaticallyStartVendavelQuest.Value)
                            {
                                StartVendavelQuest();
                            }
                            if (AutomaticallyStartRustLichQuest.Value)
                            {
                                StartRustLichQuest();
                            }
                        }
                        //Give Backpack
                        CharacterManager.Instance.GetWorldHostCharacter().Inventory.ReceiveItemReward(5300000, 1, true);
                        //Give Lantern
                        CharacterManager.Instance.GetWorldHostCharacter().Inventory.ReceiveItemReward(5100010, 1, true);
                    }
                    if (RandomlyChooseBreakthroughSkills.Value)
                    {
                        ObtainRandomBreakthroughSkills();
                    }
                    OpenSirocco();
                    OpenHarmattan();
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(SaveInstance), nameof(SaveInstance.Save))]
        public class SaveInstance_Save
        {
            static void Postfix(ref SaveInstance __instance)
            {
                Log.LogDebug($"SaveInstance_Save Postfix");

                if (RandomizedExits != null)
                {
                    Log.LogMessage($"Saving Randomized map data to save file.");
                    string savePath = $"{__instance.SavePath}{SaveFileName}";
                    File.WriteAllText(savePath, RandomizedExits.AreaSpawnsSaveString);
                }
            }
        }

        [HarmonyPatch(typeof(SaveInstance), nameof(SaveInstance.PreLoadEnvironment))]
        public class SaveInstance_PreLoadEnvironment
        {
            static void Postfix(ref SaveInstance __instance)
            {
                Log.LogDebug($"SaveInstance_PreLoadEnvironment Postfix");

                string savePath = $"{__instance.SavePath}{SaveFileName}";
                if (File.Exists(savePath))
                {
                    Log.LogMessage($"Loading Randomized map data from save file.");
                    RandomizedExits = new(File.ReadAllText(savePath));
                }
            }
        }

        [HarmonyPatch(typeof(DefeatScenariosManager), nameof(DefeatScenariosManager.ActivateDefeatScenario))]
        public class DefeatScenariosManager_ActivateDefeatScenario
        {
            static bool Prefix(ref DefeatScenariosManager __instance, ref DefeatScenario _scenario)
            {
                Log.LogDebug($"DefeatScenariosManager_ActivateDefeatScenario Prefix");

                if (_scenario && CharacterManager.Instance.HardcoreMode && _scenario.SupportHardcore && AlwaysKillOnHardcore.Value)
                {
                    __instance.photonView.RPC("DefeatHardcoreDeath", PhotonTargets.All, Array.Empty<object>());
                    return false;
                }

                return true;
            }
        }

        public static void SkipIntroQuest()
        {
            Log.LogMessage($"Skipping intro quest.");
            Character worldHostCharacter = CharacterManager.Instance.GetWorldHostCharacter();
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011001);
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011002);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("sm812Cio9ki5ssbsiPr3Fw"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("HteYicnCK0atCgd4j5TV1Q"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("z23QoIdtkU6cUPoUOfDn6w"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("nt9KhXoJtkOalZ-wtfueDA"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("YQD53MKgwke6juWiSWI7jQ"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("n_3w-BcFfEW52Ht4Q3ZCjw"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("8GvHUbDz90OOJWurd-RZlg"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("g3EX5w1mwUaYW1o0cpj0SQ"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("seEIFfM9SkeZxc4CpR40oQ"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("jNfNsLGBpk2iMRts9kkerw"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("3_soGcNagk-KcYSeqpEgMg"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("Bo4-Xvq4_kudPDnOgkI3VA"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("JqL0_JD55US2gL0-GbOBow"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("JlFMC_51RUalSa8yLkhTmg"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("BgpOoGQF10O7IQyiB9HODw"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("gAJAjuzl7ESFpMooq1oOCg"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("QMe_j2TIWEKpssXkLHMMZA"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("h8jI-dDsfkStb3XkCjqMPw"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("nr9KDCbQzUae1Gwf-6yOIQ"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("zoKR1_bqiUiAetJ3uxw-Ug"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("ZYzrMi1skUiJ4BgXXQ3sfw"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("fo2uI7yiw0WSRE7MsbYFyw"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("1a6Zs9A_gEmScBetAraQaw"), 1);
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011400);
        }

        public static void OpenSirocco()
        {
            Log.LogMessage($"Opening New Sirocco.");
            Character worldHostCharacter = CharacterManager.Instance.GetWorldHostCharacter();
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011700);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("aQU8xfhzYUSiMIlEqI45cA"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("Z3flMjR08UGADVVuPAd_BA"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("hs9Mw1naPUaTVquXAze8QA"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("2m36LE1eYE-VrIc3c5e6JA"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("VjJooMEPME2bov4V8LptBQ"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("_Ps1AThRe0WlmNsZpZSWgA"), 1);
        }

        public static void OpenHarmattan()
        {
            Log.LogMessage($"Opening Harmattan Back Entrances.");
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("aJKcAf-07ECnN-BaDuamUQ"), 1);
        }

        public static void JoinRandomFaction()
        {
            Log.LogMessage($"Choosing a random faction to join.");
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("bjVloYMQxk6KXx0gph2A1Q"), 1);
            switch (UnityEngine.Random.Range(0, 4))
            {
                case 0:
                    JoinBerg();
                    break;
                case 1:
                    JoinMonsoon();
                    break;
                case 2:
                    JoinLevant();
                    break;
                case 3:
                    JoinHarmattan();
                    break;
            }
        }

        public static void JoinBerg()
        {
            Log.LogMessage($"Joining the Blue Chamber Collective.");
            Character worldHostCharacter = CharacterManager.Instance.GetWorldHostCharacter();
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("nllwi4FnR0qN7968EsJ07A"), 1);
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011201);
            worldHostCharacter.Inventory.ReceiveItemReward(5600029, 1, false);            
        }

        public static void JoinMonsoon()
        {
            Log.LogMessage($"Joining the Holy Mission.");
            Character worldHostCharacter = CharacterManager.Instance.GetWorldHostCharacter();
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("1tkTEWMPbEGKmWItwxy4kQ"), 1);
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011101);
            worldHostCharacter.Inventory.ReceiveSkillReward(8200100);
            worldHostCharacter.Inventory.ReceiveSkillReward(8200190);
        }

        public static void JoinLevant()
        {
            Log.LogMessage($"Joining the Heroic Kingdom.");
            Character worldHostCharacter = CharacterManager.Instance.GetWorldHostCharacter();
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("SV3XUncnQU60ozhj9xTRpg"), 1);
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011301);
        }

        public static void JoinHarmattan()
        {
            Log.LogMessage($"Joining the Sorobor Academy.");
            Character worldHostCharacter = CharacterManager.Instance.GetWorldHostCharacter();
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("TSqqSaAA-kekbG6ml8yQ6Q"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("KmeS9S2inEmHPf0ArwPl1A"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("K6ZuTbf0vEqJroGUEJ1vqA"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("mg0BGfurUU-48FJeK4MGyQ"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("QiqkSB6gWEGd_KSzMSXD6g"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("yeyU2tBijUiGgW0MsjyrvQ"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("FpvZOVJiX02OstqL-cDWEA"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("XawfXT79G0mlLLi3cExISQ"), 1);
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011401);
        }

        public static void StartVendavelQuest()
        {
            Log.LogMessage($"Starting Vendavel Quest.");
            Character worldHostCharacter = CharacterManager.Instance.GetWorldHostCharacter();
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011004);
        }

        public static void StartRustLichQuest()
        {
            Log.LogMessage($"Starting Rust and Vengeance Quest.");
            Character worldHostCharacter = CharacterManager.Instance.GetWorldHostCharacter();
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("ITpcSadBYEKyY4-y8CiOHg"), 1);
            QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("mWQxfJPWO0igZVCAj0Yw6Q"), 1);
            //QuestEventManager.Instance.AddEvent(QuestEventDictionary.GetQuestEvent("dPxmTUXaSUK8K7ZFCDhBFQ"), 1);
            worldHostCharacter.Inventory.QuestKnowledge.ReceiveQuest(7011406);
        }

        public static void ObtainRandomBreakthroughSkills()
        {
            Log.LogMessage($"Choosing random breakthrough skills.");
            Character worldHostCharacter = CharacterManager.Instance.GetWorldHostCharacter();
            List<Skill> breakthroughSkills = new()
            {
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8205120"], // Swift foot - Mercenary
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8205150"], // Boon amplification - Cabal Hermit
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8205402"], // Daredevil - Speedster
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8205020"], // All stats up - Spellblade
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8205060"], // Stamina up - Warrior Monk
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8205090"], // Mana regen - Philosopher
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8205100"], // Health up - Hunter
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8205110"], // Mana up - Rune Sage
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8201053"], // Sacred fumes - Primal Ritualist
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8201027"], // Blood lust - Hex Mage
                (Skill)ResourcesPrefabManager.ITEM_PREFABS["8205130"], // Feather dodge - Rogue Engineer
            };

            int breakthroughPoints = 3;

            while (breakthroughPoints > 0)
            {
                Skill skill = breakthroughSkills[UnityEngine.Random.Range(0, breakthroughSkills.Count)];
                breakthroughSkills.Remove(skill);
                worldHostCharacter.Inventory.TryUnlockSkill(skill);
                worldHostCharacter.PlayerStats.UseBreakthrough();
                breakthroughPoints--;
            }
        }

        /////////////////////////////////////////////////////////////////////////////////
        // Area Data
        /////////////////////////////////////////////////////////////////////////////////

        // AreaGroupEnum
        //
        // Used by this mod to uniquely identify each AreaGroup
        public enum AreaGroupEnum
        {
            Cierzo = 0,
            CierzoOutside,
            CierzoOutsideNearCabal,
            CierzoOutsideTombGate,
            CierzoVendavelFort,
            CierzoVendavelTrap,
            CierzoBlisterBurrow,
            CierzoGhostPass,
            CierzoConfluxCommon,
            CierzoConfluxCommonHMCliff,
            CierzoVoltaicHatchery,
            CierzoCorruptedTombs,
            CierzoCorruptedTombsBackEntrance,
            CierzoStorage,
            CierzoStorageBackEntrance,
            CierzoMontcalmFort,
            CierzoConfluxBCEntrance,
            CierzoConfluxBCMaze,
            CierzoConfluxHMEntrance,
            CierzoConfluxHMBackEntrance,
            CierzoConfluxLVEntrance,
            CierzoConfluxLVBackEntrance,
            CierzoHyenaBurrow,
            CierzoWendigoJail,
            CierzoPirateCave,
            CierzoPylonGhostTomb,
            CierzoWineCellar,
            CierzoTrogInfiltration,
            CierzoTideCave,
            CierzoCabalTower,
            CierzoImmaculateCamp,
            CierzoTrogInfiltrationCliff,
            Monsoon,
            MonsoonOutside,
            MonsoonOutsideToBerg,
            MonsoonOutsideSpireBarrier,
            MonsoonJadeQuarry,
            MonsoonGiantVillage,
            MonsoonReptileLairBottom,
            MonsoonReptileLairMid,
            MonsoonReptileLairTop,
            MonsoonDarkZigguratTop,
            MonsoonDarkZigguratLeft,
            MonsoonDarkZigguratRight,
            MonsoonSpireOfLight,
            MonsoonSpireOfLightBackEntrance,
            MonsoonZigguratPassBackEntrance,
            MonsoonZigguratPass,
            MonsoonDeadRoots,
            MonsoonDeadRootsLocked,
            MonsoonSmallZiggurat,
            MonsoonFloodedCellar,
            MonsoonDinoBurrow,
            MonsoonHollowedLotus,
            MonsoonWendigoHouse,
            MonsoonDeadTree,
            MonsoonGiantsRuins,
            MonsoonImmaculateCamp,
            Levant,
            LevantFrontDoorLocking,
            LevantBackDoorLocking,
            LevantPlayerHouse,
            LevantCastleSecretEntrance,
            LevantOutside,
            LevantOutsideElectricLab,
            LevantOutsideZagisFort,
            LevantUndercityPassage,
            LevantUndercityPassageLocking,
            LevantUndercityPassageToPlayerHouse,
            LevantUndercityPassageLibrary,
            LevantElectricLab,
            LevantElectricLabToTop,
            LevantSlide,
            LevantSlideDark,
            LevantSlideGate,
            LevantSlideWater,
            LevantStoneTitanTop,
            LevantStoneTitanBottom,
            LevantAncientHive,
            LevantSandRose,
            LevantSideHive,
            LevantWreckedShip,
            LevantZagisFort,
            LevantOasisRiver,
            LevantBanditCamp,
            LevantImmaculateCamp,
            LevantDocks,
            LevantCabalTower,
            Harmattan,
            HarmattanLockedGate1,
            HarmattanLockedGate2,
            HarmattanOutside,
            HarmattanOutsideRuinedWarehouseGate,
            HarmattanOutsideStadium,
            HarmattanOutsidePylon,
            HarmattanOutsideManaTransferGate,
            HarmattanDungeonMain,
            HarmattanDungeonLoadingDocks,
            HarmattanDungeonManFacility,
            HarmattanDungeonManaStation,
            HarmattanDungeonRuinedWarehouse,
            HarmattanBloodMageHideout,
            HarmattanWendigoLair,
            HarmattanVeaberCave,
            HarmattanKaziteCamp,
            HarmattanAbandonedStorage,
            HarmattanOldHarmattanEntrance,
            HarmattanImmaculateSettlement,
            HarmattanImmaculateCamp,
            Berg,
            BergOutside,
            BergOutsideCorruptHiveZone,
            BergRoyalManticoreLair,
            BergForestHives,
            BergForestHivesSecretEntrance,
            BergCabalTemple,
            BergCabalTempleBlockedExit,
            BergFaceOfAncients,
            BergAncestorRestingPlace,
            BergNecropolis,
            BergHunterLodge,
            BergHunterLodgeGhost,
            BergHunterLodgeWorkbench,
            BergGraveTowerUnderground,
            BergSideHive,
            BergBurningForestCabin,
            BergImmaculateCamp,
            BergOneTreeIsland,
            BergVigilPylon,
            Sirocco,
            SiroccoOutside,
            SiroccoOutsideBehindOldCity,
            SiroccoOutsideBehindMyrmitaur,
            SiroccoOutsideBehindRegret,
            SiroccoSteamBathTunnels,
            SiroccoSulphuricCaverns,
            SiroccoEldestBrother,
            SiroccoChalcedonyGrotto,
            SiroccoMyrmitaurHaven,
            SiroccoMyrmitaurHavenLocked,
            SiroccoMyrmitaurHavenBack,
            SiroccoOilRefineryFront,
            SiroccoOilRefineryBack,
            SiroccoVaultOfStone,
            SiroccoOldCity,
            SiroccoOldCityBack,
            SiroccoOldCityBack2,
            SiroccoOilyCavern,
            SiroccoCalygaryColusseum,
            SiroccoRedRiver,
            SiroccoImmaculateCamp,
            SiroccoSilkwormRefuge,
            SiroccoGiantSauna,
            SiroccoTowerOfRegret,
            SiroccoTowerOfRegretBack,
            SiroccoTowerOfRegretTop,
            SiroccoRitualistHut,
        }

        // AreaList
        //
        // A Collection of all AreaEnums that the game uses
        public static AreaManager.AreaEnum[] AreaList = {
            AreaManager.AreaEnum.CierzoVillage,
            AreaManager.AreaEnum.CierzoOutside,
            AreaManager.AreaEnum.ChersoDungeon1,
            AreaManager.AreaEnum.ChersoDungeon2,
            AreaManager.AreaEnum.ChersoDungeon3,
            AreaManager.AreaEnum.ChersoDungeon4,
            AreaManager.AreaEnum.ChersoDungeon5,
            AreaManager.AreaEnum.ChersoDungeon6,
            AreaManager.AreaEnum.ChersoDungeon8,
            AreaManager.AreaEnum.ChersoDungeon9,
            AreaManager.AreaEnum.ChersoDungeon4_BC,
            AreaManager.AreaEnum.ChersoDungeon4_HM,
            AreaManager.AreaEnum.ChersoDungeon4_LV,
            AreaManager.AreaEnum.ChersoDungeonsSmall,
            AreaManager.AreaEnum.Monsoon,
            AreaManager.AreaEnum.HallowedMarsh,
            AreaManager.AreaEnum.HallowedDungeon1,
            AreaManager.AreaEnum.HallowedDungeon2,
            AreaManager.AreaEnum.HallowedDungeon3,
            AreaManager.AreaEnum.HallowedDungeon4_Interior,
            AreaManager.AreaEnum.HallowedDungeon5,
            AreaManager.AreaEnum.HallowedDungeon6,
            AreaManager.AreaEnum.HallowedDungeon7,
            AreaManager.AreaEnum.HallowedDungeonsSmall,
            AreaManager.AreaEnum.Levant,
            AreaManager.AreaEnum.Abrassar,
            AreaManager.AreaEnum.AbrassarDungeon1,
            AreaManager.AreaEnum.AbrassarDungeon2,
            AreaManager.AreaEnum.AbrassarDungeon3,
            AreaManager.AreaEnum.AbrassarDungeon4,
            AreaManager.AreaEnum.AbrassarDungeon5,
            AreaManager.AreaEnum.AbrassarDungeon6,
            AreaManager.AreaEnum.AbrassarDungeon7,
            AreaManager.AreaEnum.AbrassarDungeon8,
            AreaManager.AreaEnum.AbrassarDungeonsSmall,
            AreaManager.AreaEnum.Harmattan,
            AreaManager.AreaEnum.AntiqueField,
            AreaManager.AreaEnum.AntiqueFieldDungeon1,
            AreaManager.AreaEnum.AntiqueFieldDungeon2,
            AreaManager.AreaEnum.AntiqueFieldDungeon3,
            AreaManager.AreaEnum.AntiqueFieldDungeon4,
            AreaManager.AreaEnum.AntiqueFieldDungeon5,
            AreaManager.AreaEnum.AntiqueFieldDungeon6,
            AreaManager.AreaEnum.AntiqueFieldDungeon7,
            AreaManager.AreaEnum.AntiqueFieldDungeon8,
            AreaManager.AreaEnum.AntiqueFieldDungeon9,
            AreaManager.AreaEnum.AntiqueFieldDungeonsSmall,
            AreaManager.AreaEnum.Berg,
            AreaManager.AreaEnum.Emercar,
            AreaManager.AreaEnum.EmercarDungeon1,
            AreaManager.AreaEnum.EmercarDungeon2,
            AreaManager.AreaEnum.EmercarDungeon3,
            AreaManager.AreaEnum.EmercarDungeon4,
            AreaManager.AreaEnum.EmercarDungeon5,
            AreaManager.AreaEnum.EmercarDungeon6,
            AreaManager.AreaEnum.EmercarDungeon7,
            AreaManager.AreaEnum.EmercarDungeonsSmall,
            AreaManager.AreaEnum.NewSirocco,
            AreaManager.AreaEnum.Caldera,
            AreaManager.AreaEnum.CalderaDungeon1,
            AreaManager.AreaEnum.CalderaDungeon2,
            AreaManager.AreaEnum.CalderaDungeon3,
            AreaManager.AreaEnum.CalderaDungeon4,
            AreaManager.AreaEnum.CalderaDungeon5,
            AreaManager.AreaEnum.CalderaDungeon6,
            AreaManager.AreaEnum.CalderaDungeon7,
            AreaManager.AreaEnum.CalderaDungeon8,
            AreaManager.AreaEnum.CalderaDungeon9,
            AreaManager.AreaEnum.CalderaDungeon10,
            AreaManager.AreaEnum.CalderaDungeonsSmall,
        };

        // DefaultAreaGroups
        // This is a collection of most of the spawn points in the game. Using this data, the exit shuffler will convert
        // all exit entity destinations that are present in this list to new destinations that are also present in this
        // list.
        //
        // The data is a collection of AreaGroups, which are collections of Exits, which are pairs of AreaSpawns.
        //
        // An AreaSpawn object is one AreaEnum denoting which area to load, and a SpawnPoint: a numeric identifier
        // which determines where the game warps you to if you interact with the exit entity.
        //
        // An Exit object is two AreaSpawns, a "From" location and a "To" location. The "From" AreaSpawn denotes where
        // the exit entity is, as well as the destination of another exit entity. The "To" AreaSpawn denotes where you
        // will go if you interact with the exit entity at the "From" location.
        //
        // An AreaGroup object is a collection of Exits that can be reached from any other Exit in the area. Organizing
        // the data this way allows the exit shuffler to prevent any areas from becoming unreachable.
        //
        // Some AreaGroup objects contain another array of AreaGroupEnum's. This identifies a one-way openable connection
        // to another AreaGroup. What this is in the game is an area where you can start at one end and open a gate that
        // opens a path to the other end. This way, the exit shuffler will allow areas behind the gate to be connections
        // to other areas, but only in one direction. In other words, you will never get into a situation where sections
        // of the map are blocked off because you enter the one-way area through the wrong direction. I mean, you might
        // still encounter that, but the main entrance will be accessible from somewhere else in the game.
        //
        // Some areas are accessible from another area, but only in one direction, like past a cliff where you cannot
        // climb back up. These are considered separate areas, since you can never go through the area in reverse, and
        // all exits of an area need to be accessible by every other exit in the area for the shuffler to work.
        public static AreaGroup[] DefaultAreaGroups = {

            // Cierzo
            new( AreaGroupEnum.Cierzo,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CierzoVillage, 1 ), new( AreaManager.AreaEnum.CierzoOutside, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoVillage, 2 ), new( AreaManager.AreaEnum.ChersoDungeon8, 0 ) ),
                }
            ),

            // Main Chersonese Area
            new( AreaGroupEnum.CierzoOutside,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CierzoOutside, 0 ), new( AreaManager.AreaEnum.CierzoVillage, 1 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 2 ), new( AreaManager.AreaEnum.Emercar, 3 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 13 ), new( AreaManager.AreaEnum.ChersoDungeon8, 1 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 3 ), new( AreaManager.AreaEnum.ChersoDungeon1, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 9 ), new( AreaManager.AreaEnum.ChersoDungeon4_HM, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 5 ), new( AreaManager.AreaEnum.ChersoDungeon2, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 15 ), new( AreaManager.AreaEnum.ChersoDungeon5, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 6 ), new( AreaManager.AreaEnum.ChersoDungeon3, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 10 ), new( AreaManager.AreaEnum.ChersoDungeon4_LV, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 31 ), new( AreaManager.AreaEnum.ChersoDungeon9, 1 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 27 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 7 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 23 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 3 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 24 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 4 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 26 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 6 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 1 ), new( AreaManager.AreaEnum.HallowedMarsh, 21 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 30 ), new( AreaManager.AreaEnum.ChersoDungeon9, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 8 ), new( AreaManager.AreaEnum.ChersoDungeon4_BC, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 29 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 9 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 21 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 1 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 22 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 2 ) ),
                }
            ),

            // Sub Chersonese Area, near Cabal of Wind Tower
            new( AreaGroupEnum.CierzoOutsideNearCabal,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CierzoOutside, 7 ), new( AreaManager.AreaEnum.ChersoDungeon3, 1 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 17 ), new( AreaManager.AreaEnum.ChersoDungeon6, 0 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 25 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 5 ) ),
                    new( new( AreaManager.AreaEnum.CierzoOutside, 28 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 8 ) ),
                }
            ),

            // Sub Chersonese Area, Gated section by Corrupted Tombs
            new( AreaGroupEnum.CierzoOutsideTombGate,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CierzoOutside, 16 ), new( AreaManager.AreaEnum.ChersoDungeon6, 1 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.CierzoOutside }
            ),

            // Vendavel Fortress Main
            new( AreaGroupEnum.CierzoVendavelFort,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon1, 0 ), new( AreaManager.AreaEnum.CierzoOutside, 3 ) ),
                }
            ),

            // Vendavel Fortress Trap
            new( AreaGroupEnum.CierzoVendavelTrap,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon1, 1 ), new( AreaManager.AreaEnum.ChersoDungeonsSmall, 10 ) ),
                }
            ),

            // Blister Burrow
            new( AreaGroupEnum.CierzoBlisterBurrow,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon2, 0 ), new( AreaManager.AreaEnum.CierzoOutside, 5 ) ),
                }
            ),

            // Ghost Pass
            new( AreaGroupEnum.CierzoGhostPass,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon3, 0 ), new( AreaManager.AreaEnum.CierzoOutside, 6 ) ),
                    new( new( AreaManager.AreaEnum.ChersoDungeon3, 1 ), new( AreaManager.AreaEnum.CierzoOutside, 7 ) ),
                }
            ),

            // Conflux Chambers Common Path
            new( AreaGroupEnum.CierzoConfluxCommon,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon4, 0 ), new( AreaManager.AreaEnum.ChersoDungeon4_BC, 1 ) ),
                    new( new( AreaManager.AreaEnum.ChersoDungeon4, 2 ), new( AreaManager.AreaEnum.ChersoDungeon4_LV, 1 ) ),
                }
            ),

            // Conflux Chambers Common Path (Separate section for Holy Mission)
            new( AreaGroupEnum.CierzoConfluxCommonHMCliff,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon4, 1 ), new( AreaManager.AreaEnum.ChersoDungeon4_HM, 1 ) ),
                }
            ),

            // Voltaic Hatchery
            new( AreaGroupEnum.CierzoVoltaicHatchery,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon5, 0 ), new( AreaManager.AreaEnum.CierzoOutside, 15 ) ),
                }
            ),

            // Corrupted Tombs
            new( AreaGroupEnum.CierzoCorruptedTombs,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon6, 0 ), new( AreaManager.AreaEnum.CierzoOutside, 17 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.CierzoCorruptedTombsBackEntrance }
            ),

            // Corrupted Tombs (Section past the barrier)
            new( AreaGroupEnum.CierzoCorruptedTombsBackEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon6, 1 ), new( AreaManager.AreaEnum.CierzoOutside, 16 ) ),
                }
            ),

            // Cierzo Storage
            new( AreaGroupEnum.CierzoStorage,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon8, 0 ), new( AreaManager.AreaEnum.CierzoVillage, 2 ) ),
                }
            ),

            // Cierzo Storage (Back Section)
            new( AreaGroupEnum.CierzoStorageBackEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon8, 1 ), new( AreaManager.AreaEnum.CierzoOutside, 13 ) ),
                }
            ),

            // Montcalm Clan Fort
            new( AreaGroupEnum.CierzoMontcalmFort,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon9, 0 ), new( AreaManager.AreaEnum.CierzoOutside, 30 ) ),
                    new( new( AreaManager.AreaEnum.ChersoDungeon9, 1 ), new( AreaManager.AreaEnum.CierzoOutside, 31 ) ),
                }
            ),

            // Blue Chamber Conflux Path (Entrance)
            new( AreaGroupEnum.CierzoConfluxBCEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon4_BC, 0 ), new( AreaManager.AreaEnum.CierzoOutside, 8 ) ),
                }
            ),

            // Blue Chamber Conflux Path (Maze)
            new( AreaGroupEnum.CierzoConfluxBCMaze,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon4_BC, 1 ), new( AreaManager.AreaEnum.ChersoDungeon4, 0 ) ),
                }
            ),

            // Holy Mission Conflux Path
            new( AreaGroupEnum.CierzoConfluxHMEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon4_HM, 0 ), new( AreaManager.AreaEnum.CierzoOutside, 9 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.CierzoConfluxHMBackEntrance }
            ),

            // Holy Mission Conflux Path (Locked Back Entrance)
            new( AreaGroupEnum.CierzoConfluxHMBackEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon4_HM, 1 ), new( AreaManager.AreaEnum.ChersoDungeon4, 1 ) ),
                }
            ),

            // Heroic Kingdom Conflux Path
            new( AreaGroupEnum.CierzoConfluxLVEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon4_LV, 0 ), new( AreaManager.AreaEnum.CierzoOutside, 10 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.CierzoConfluxLVBackEntrance }
            ),

            // Heroic Kingdom Conflux Path (Locked Back Way)
            new( AreaGroupEnum.CierzoConfluxLVBackEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeon4_LV, 1 ), new( AreaManager.AreaEnum.ChersoDungeon4, 2 ) ),
                }
            ),

            // Hyena Burrow
            new( AreaGroupEnum.CierzoHyenaBurrow,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 1 ), new( AreaManager.AreaEnum.CierzoOutside, 21 ) ),
                }
            ),

            // Bandit Camp Prison with Wendigo
            new( AreaGroupEnum.CierzoWendigoJail,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 2 ), new( AreaManager.AreaEnum.CierzoOutside, 22 ) ),
                }
            ),

            // Pirate Cave
            new( AreaGroupEnum.CierzoPirateCave,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 3 ), new( AreaManager.AreaEnum.CierzoOutside, 23 ) ),
                }
            ),

            // Chersonese Pylon Ghost Tomb
            new( AreaGroupEnum.CierzoPylonGhostTomb,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 4 ), new( AreaManager.AreaEnum.CierzoOutside, 24 ) ),
                }
            ),

            // Wine Cellar
            new( AreaGroupEnum.CierzoWineCellar,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 5 ), new( AreaManager.AreaEnum.CierzoOutside, 25 ) ),
                }
            ),

            // Trog Cave Under Vendavel
            new( AreaGroupEnum.CierzoTrogInfiltration,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 6 ), new( AreaManager.AreaEnum.CierzoOutside, 26 ) ),
                }
            ),

            // Blue Sand Tide Cave
            new( AreaGroupEnum.CierzoTideCave,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 7 ), new( AreaManager.AreaEnum.CierzoOutside, 27 ) ),
                }
            ),

            // Cabal Tower Interior
            new( AreaGroupEnum.CierzoCabalTower,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 8 ), new( AreaManager.AreaEnum.CierzoOutside, 28 ) ),
                }
            ),

            // Chersonese Immaculate Camp
            new( AreaGroupEnum.CierzoImmaculateCamp,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 9 ), new( AreaManager.AreaEnum.CierzoOutside, 29 ) ),
                }
            ),

            // Trog Cave Under Vendavel (On Cliff)
            new( AreaGroupEnum.CierzoTrogInfiltrationCliff,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.ChersoDungeonsSmall, 10 ), new( AreaManager.AreaEnum.ChersoDungeon1, 1 ) ),
                }
            ),

            // Monsoon
            new( AreaGroupEnum.Monsoon,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Monsoon, 0 ), new( AreaManager.AreaEnum.HallowedMarsh, 0 ) ),
                }
            ),

            // Hallowed Marsh
            new( AreaGroupEnum.MonsoonOutside,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 0 ), new( AreaManager.AreaEnum.Monsoon, 0 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 16 ), new( AreaManager.AreaEnum.HallowedDungeonsSmall, 5 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 5 ), new( AreaManager.AreaEnum.HallowedDungeon4_Interior, 2 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 18 ), new( AreaManager.AreaEnum.HallowedDungeonsSmall, 7 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 9 ), new( AreaManager.AreaEnum.HallowedDungeon7, 0 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 6 ), new( AreaManager.AreaEnum.HallowedDungeon5, 0 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 4 ), new( AreaManager.AreaEnum.HallowedDungeon4_Interior, 1 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 3 ), new( AreaManager.AreaEnum.HallowedDungeon4_Interior, 0 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 20 ), new( AreaManager.AreaEnum.HallowedDungeonsSmall, 9 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 14 ), new( AreaManager.AreaEnum.HallowedDungeonsSmall, 3 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 12 ), new( AreaManager.AreaEnum.HallowedDungeonsSmall, 1 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 25 ), new( AreaManager.AreaEnum.HallowedDungeon3, 2 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 23 ), new( AreaManager.AreaEnum.HallowedDungeon2, 0 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 21 ), new( AreaManager.AreaEnum.CierzoOutside, 1 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 1 ), new( AreaManager.AreaEnum.HallowedDungeon1, 0 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 15 ), new( AreaManager.AreaEnum.HallowedDungeonsSmall, 4 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 13 ), new( AreaManager.AreaEnum.HallowedDungeonsSmall, 2 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 10 ), new( AreaManager.AreaEnum.HallowedDungeon7, 1 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 7 ), new( AreaManager.AreaEnum.HallowedDungeon6, 0 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 2 ), new( AreaManager.AreaEnum.HallowedDungeon3, 0 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 24 ), new( AreaManager.AreaEnum.HallowedDungeon3, 1 ) ),
                }
            ),

            // Hallowed Marsh (Gated by Spire and Bridge)
            new( AreaGroupEnum.MonsoonOutsideToBerg,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 8 ), new( AreaManager.AreaEnum.HallowedDungeon6, 1 ) ),
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 22 ), new( AreaManager.AreaEnum.Emercar, 21 ) ),
                }
            ),

            // Hallowed Marsh (Spire of Light Barrier Bridge)
            new( AreaGroupEnum.MonsoonOutsideSpireBarrier,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedMarsh, 26 ), new( AreaManager.AreaEnum.HallowedDungeon5, 1 ) ),
                }
            ),

            // Jade Quarry
            new( AreaGroupEnum.MonsoonJadeQuarry,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon1, 0 ), new( AreaManager.AreaEnum.HallowedMarsh, 1 ) ),
                }
            ),

            // Giant Village
            new( AreaGroupEnum.MonsoonGiantVillage,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon2, 0 ), new( AreaManager.AreaEnum.HallowedMarsh, 23 ) ),
                }
            ),

            // Reptilian Lair (Bottom)
            new( AreaGroupEnum.MonsoonReptileLairBottom,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon3, 0 ), new( AreaManager.AreaEnum.HallowedMarsh, 2 ) ),
                }
            ),

            // Reptilian Lair (Mid)
            new( AreaGroupEnum.MonsoonReptileLairMid,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon3, 1 ), new( AreaManager.AreaEnum.HallowedMarsh, 24 ) ),
                }
            ),

            // Reptilian Lair (Top)
            new( AreaGroupEnum.MonsoonReptileLairTop,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon3, 2 ), new( AreaManager.AreaEnum.HallowedMarsh, 25 ) ),
                }
            ),

            // Dark Ziggurat (Top)
            new( AreaGroupEnum.MonsoonDarkZigguratTop,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon4_Interior, 0 ), new( AreaManager.AreaEnum.HallowedMarsh, 3 ) ),
                }
            ),

            // Dark Ziggurat (Left)
            new( AreaGroupEnum.MonsoonDarkZigguratLeft,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon4_Interior, 1 ), new( AreaManager.AreaEnum.HallowedMarsh, 4 ) ),
                }
            ),

            // Dark Ziggurat (Right)
            new( AreaGroupEnum.MonsoonDarkZigguratRight,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon4_Interior, 2 ), new( AreaManager.AreaEnum.HallowedMarsh, 5 ) ),
                }
            ),

            // Spire of Light (Main)
            new( AreaGroupEnum.MonsoonSpireOfLight,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon5, 0 ), new( AreaManager.AreaEnum.HallowedMarsh, 6 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.MonsoonSpireOfLightBackEntrance }
            ),

            // Spire of Light (Back)
            new( AreaGroupEnum.MonsoonSpireOfLightBackEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon5, 1 ), new( AreaManager.AreaEnum.HallowedMarsh, 26 ) ),
                }
            ),

            // Ziggurat Passage (Locked Front)
            new( AreaGroupEnum.MonsoonZigguratPassBackEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon6, 0 ), new( AreaManager.AreaEnum.HallowedMarsh, 7 ) ),
                }
            ),

            // Ziggurat Passage (Back)
            new( AreaGroupEnum.MonsoonZigguratPass,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon6, 1 ), new( AreaManager.AreaEnum.HallowedMarsh, 8 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.MonsoonZigguratPassBackEntrance }
            ),

            // Dead Roots
            new( AreaGroupEnum.MonsoonDeadRoots,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeon7, 0 ), new( AreaManager.AreaEnum.HallowedMarsh, 9 ) ),
                    new( new( AreaManager.AreaEnum.HallowedDungeon7, 1 ), new( AreaManager.AreaEnum.HallowedMarsh, 10 ) ),
                }
            ),

            // Dead Roots (Behind locked door)
            //new( AreaGroupEnum.MonsoonDeadRootsLocked,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.HallowedDungeon7, 2 ), new( AreaManager.AreaEnum.HallowedDungeonsSmall, 6 ) ),
            //    }
            //),

            // Small Ziggurat
            new( AreaGroupEnum.MonsoonSmallZiggurat,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeonsSmall, 1 ), new( AreaManager.AreaEnum.HallowedMarsh, 12 ) ),
                }
            ),

            // Marsh Bandit Jail
            new( AreaGroupEnum.MonsoonFloodedCellar,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeonsSmall, 2 ), new( AreaManager.AreaEnum.HallowedMarsh, 13 ) ),
                }
            ),

            // Dino Burrow
            new( AreaGroupEnum.MonsoonDinoBurrow,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeonsSmall, 3 ), new( AreaManager.AreaEnum.HallowedMarsh, 14 ) ),
                }
            ),

            // Lotus
            new( AreaGroupEnum.MonsoonHollowedLotus,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeonsSmall, 4 ), new( AreaManager.AreaEnum.HallowedMarsh, 15 ) ),
                }
            ),

            // Abandoned Wendigo House
            new( AreaGroupEnum.MonsoonWendigoHouse,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeonsSmall, 5 ), new( AreaManager.AreaEnum.HallowedMarsh, 16 ) ),
                }
            ),

            // Corruption Beneath Dead Roots
            // Makes more sense to have this bundled in with the Purifier Dead Roots segment.
            //new( AreaGroupEnum.MonsoonDeadTree,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.HallowedDungeonsSmall, 6 ), new( AreaManager.AreaEnum.HallowedDungeon7, 2 ) ),
            //    }
            //),

            // 5 Gems, 5 Trogs
            new( AreaGroupEnum.MonsoonGiantsRuins,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeonsSmall, 7 ), new( AreaManager.AreaEnum.HallowedMarsh, 18 ) ),
                }
            ),

            // Hallowed Marsh Immaculate Camp
            new( AreaGroupEnum.MonsoonImmaculateCamp,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.HallowedDungeonsSmall, 9 ), new( AreaManager.AreaEnum.HallowedMarsh, 20 ) ),
                }
            ),

            // Levant
            new( AreaGroupEnum.Levant,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Levant, 2 ), new( AreaManager.AreaEnum.AbrassarDungeon1, 1 ) ),
                    new( new( AreaManager.AreaEnum.Levant, 0 ), new( AreaManager.AreaEnum.Abrassar, 0 ) ),
                    new( new( AreaManager.AreaEnum.Levant, 1 ), new( AreaManager.AreaEnum.Abrassar, 7 ) ),
                }
            ),

            // Levant (Inside Player House)
            // Leaving this out because you can get soft-locked going through the door (only open if player house is open)
            //new( AreaGroupEnum.LevantPlayerHouse,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.Levant, 3 ), new( AreaManager.AreaEnum.AbrassarDungeon1, 2 ) ),
            //    }
            //),

            // Levant (Inside Castle)
            new( AreaGroupEnum.LevantCastleSecretEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Levant, 4 ), new( AreaManager.AreaEnum.AbrassarDungeon1, 3 ) ),
                }
            ),

            // Abrassar
            new( AreaGroupEnum.LevantOutside,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Abrassar, 4 ), new( AreaManager.AreaEnum.AbrassarDungeon4, 1 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 15 ), new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 1 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 23 ), new( AreaManager.AreaEnum.AbrassarDungeon1, 0 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 6 ), new( AreaManager.AreaEnum.AbrassarDungeon5, 0 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 8 ), new( AreaManager.AreaEnum.AbrassarDungeon2, 0 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 14 ), new( AreaManager.AreaEnum.AbrassarDungeon6, 0 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 2 ), new( AreaManager.AreaEnum.Emercar, 4 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 18 ), new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 4 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 20 ), new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 6 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 10 ), new( AreaManager.AreaEnum.AbrassarDungeon3, 0 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 0 ), new( AreaManager.AreaEnum.Levant, 0 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 22 ), new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 8 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 16 ), new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 2 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 5 ), new( AreaManager.AreaEnum.AbrassarDungeon4, 2 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 12 ), new( AreaManager.AreaEnum.AbrassarDungeon3, 3 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 13 ), new( AreaManager.AreaEnum.AbrassarDungeon3, 1 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 21 ), new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 7 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 7 ), new( AreaManager.AreaEnum.Levant, 1 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 3 ), new( AreaManager.AreaEnum.AbrassarDungeon4, 0 ) ),
                    new( new( AreaManager.AreaEnum.Abrassar, 19 ), new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 5 ) ),
                }
            ),

            // Abrassar (On top of Electric Lab)
            new( AreaGroupEnum.LevantOutsideElectricLab,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Abrassar, 9 ), new( AreaManager.AreaEnum.AbrassarDungeon2, 1 ) ),
                }
            ),

            // Abrassar Near Zagis' Bandit Fort
            // Can't open this direction without joining Levant
            //new( AreaGroupEnum.LevantOutsideZagisFort,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.Abrassar, 11 ), new( AreaManager.AreaEnum.AbrassarDungeon3, 2 ) ),
            //        new( new( AreaManager.AreaEnum.Abrassar, 17 ), new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 3 ) ),
            //    }
            //),
            
            // Undercity Passage
            new( AreaGroupEnum.LevantUndercityPassage,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon1, 1 ), new( AreaManager.AreaEnum.Levant, 2 ) ),
                }
            ),

            // Undercity Passage (Locking exit)
            new( AreaGroupEnum.LevantUndercityPassageLocking,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon1, 0 ), new( AreaManager.AreaEnum.Abrassar, 23 ) ),
                }
            ),

            // Undercity Passage (To Player House)
            // Leaving this out because you can get soft-locked going through the door (only open if player house is open)
            //new( AreaGroupEnum.LevantUndercityPassageToPlayerHouse,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.AbrassarDungeon1, 2 ), new( AreaManager.AreaEnum.Levant, 3 ) ),
            //    },
            //    new AreaGroupEnum[] { AreaGroupEnum.LevantUndercityPassage }
            //),

            // Undercity Passage (Library)
            new( AreaGroupEnum.LevantUndercityPassageLibrary,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon1, 3 ), new( AreaManager.AreaEnum.Levant, 4 ) ),
                }
            ),

            // Electric Lab
            new( AreaGroupEnum.LevantElectricLab,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon2, 0 ), new( AreaManager.AreaEnum.Abrassar, 8 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.LevantElectricLabToTop }
            ),

            // Electric Lab (Behind locked gate)
            new( AreaGroupEnum.LevantElectricLabToTop,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon2, 1 ), new( AreaManager.AreaEnum.Abrassar, 9 ) ),
                }
            ),

            // The Slide
            new( AreaGroupEnum.LevantSlide,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon3, 0 ), new( AreaManager.AreaEnum.Abrassar, 10 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.LevantSlideDark, /*AreaGroupEnum.LevantSlideGate,*/ AreaGroupEnum.LevantSlideWater }
            ),

            // The Slide (Locked Dark Path)
            new( AreaGroupEnum.LevantSlideDark,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon3, 1 ), new( AreaManager.AreaEnum.Abrassar, 13 ) ),
                }
            ),

            //// The Slide (Locked Behind Gate)
            // Can't open this direction without joining Levant
            //new( AreaGroupEnum.LevantSlideGate,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.AbrassarDungeon3, 2 ), new( AreaManager.AreaEnum.Abrassar, 11 ) ),
            //    }
            //),

            // The Slide (Blocked by Water)
            new( AreaGroupEnum.LevantSlideWater,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon3, 3 ), new( AreaManager.AreaEnum.Abrassar, 12 ) ),
                }
            ),

            // Stone Titan Cave (Top Half)
            new( AreaGroupEnum.LevantStoneTitanTop,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon4, 0 ), new( AreaManager.AreaEnum.Abrassar, 3 ) ),
                    new( new( AreaManager.AreaEnum.AbrassarDungeon4, 1 ), new( AreaManager.AreaEnum.Abrassar, 4 ) ),
                }
            ),

            // Stone Titan Cave (Bottom Half)
            new( AreaGroupEnum.LevantStoneTitanBottom,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon4, 2 ), new( AreaManager.AreaEnum.Abrassar, 5 ) ),
                }
            ),

            // Ancient Hive
            new( AreaGroupEnum.LevantAncientHive,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon5, 0 ), new( AreaManager.AreaEnum.Abrassar, 6 ) ),
                }
            ),

            // Sand Rose Cave
            new( AreaGroupEnum.LevantSandRose,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeon6, 0 ), new( AreaManager.AreaEnum.Abrassar, 14 ) ),
                }
            ),

            // Hive Side Cave
            new( AreaGroupEnum.LevantSideHive,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 1 ), new( AreaManager.AreaEnum.Abrassar, 15 ) ),
                }
            ),

            // Wrecked Desert Ship
            new( AreaGroupEnum.LevantWreckedShip,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 2 ), new( AreaManager.AreaEnum.Abrassar, 16 ) ),
                }
            ),

            // Zagis' Bandit Fort
            // Can't go here without joining Levant
            //new( AreaGroupEnum.LevantZagisFort,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 3 ), new( AreaManager.AreaEnum.Abrassar, 17 ) ),
            //    }
            //),

            // Oasis River Cave
            new( AreaGroupEnum.LevantOasisRiver,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 4 ), new( AreaManager.AreaEnum.Abrassar, 18 ) ),
                }
            ),

            // Desert Bandit Camp
            new( AreaGroupEnum.LevantBanditCamp,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 5 ), new( AreaManager.AreaEnum.Abrassar, 19 ) ),
                }
            ),

            // Abrassar Immaculate Camp
            new( AreaGroupEnum.LevantImmaculateCamp,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 6 ), new( AreaManager.AreaEnum.Abrassar, 20 ) ),
                }
            ),

            // Old Desert Docks
            new( AreaGroupEnum.LevantDocks,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 7 ), new( AreaManager.AreaEnum.Abrassar, 21 ) ),
                }
            ),

            // Abrassar Cabal Tower
            new( AreaGroupEnum.LevantCabalTower,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AbrassarDungeonsSmall, 8 ), new( AreaManager.AreaEnum.Abrassar, 22 ) ),
                }
            ),

            // Harmattan
            new( AreaGroupEnum.Harmattan,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Harmattan, 0 ), new( AreaManager.AreaEnum.AntiqueField, 0 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.HarmattanLockedGate1, AreaGroupEnum.HarmattanLockedGate2 }
            ),

            // Harmattan Locked Gate 1
            new( AreaGroupEnum.HarmattanLockedGate1,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Harmattan, 1 ), new( AreaManager.AreaEnum.AntiqueField, 21 ) ),
                }
            ),

            // Harmattan Locked Gate 2
            new( AreaGroupEnum.HarmattanLockedGate2,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Harmattan, 2 ), new( AreaManager.AreaEnum.AntiqueField, 22 ) ),
                }
            ),

            // Antique Plateau
            new( AreaGroupEnum.HarmattanOutside,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueField, 14 ), new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 4 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 22 ), new( AreaManager.AreaEnum.Harmattan, 2 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 3 ), new( AreaManager.AreaEnum.AntiqueFieldDungeon3, 0 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 16 ), new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 6 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 13 ), new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 3 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 21 ), new( AreaManager.AreaEnum.Harmattan, 1 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 7 ), new( AreaManager.AreaEnum.AntiqueFieldDungeon7, 0 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 18 ), new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 8 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 0 ), new( AreaManager.AreaEnum.Harmattan, 0 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 1 ), new( AreaManager.AreaEnum.AntiqueFieldDungeon1, 0 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 11 ), new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 1 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 20 ), new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 7 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 12 ), new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 2 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueField, 15 ), new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 5 ) ),
                }
            ),

            // Antique Plateau (Locked Section For Ruined Warehouse)
            new( AreaGroupEnum.HarmattanOutsideRuinedWarehouseGate,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueField, 4 ), new( AreaManager.AreaEnum.AntiqueFieldDungeon4, 0 ) ),
                }
            ),

            // Antique Plateau (Rope Drop from Stadium)
            new( AreaGroupEnum.HarmattanOutsideStadium,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueField, 5 ), new( AreaManager.AreaEnum.AntiqueFieldDungeon5, 0 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.HarmattanOutside }
            ),

            // Antique Plateau (Bridge Drop from Pylon)
            new( AreaGroupEnum.HarmattanOutsidePylon,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueField, 6 ), new( AreaManager.AreaEnum.AntiqueFieldDungeon6, 0 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.HarmattanOutside }
            ),

            // Antique Plateau (Locked Gate near Mana Transfer Station)
            new( AreaGroupEnum.HarmattanOutsideManaTransferGate,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueField, 8 ), new( AreaManager.AreaEnum.AntiqueFieldDungeon8, 0 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.HarmattanOutside }
            ),

            // Harmattan Underground Dungeon Main Group
            new( AreaGroupEnum.HarmattanDungeonMain,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeon1, 0 ), new( AreaManager.AreaEnum.AntiqueField, 1 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeon3, 0 ), new( AreaManager.AreaEnum.AntiqueField, 3 ) ),
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeon7, 0 ), new( AreaManager.AreaEnum.AntiqueField, 7 ) ),
                },
                new AreaGroupEnum[] {
                    AreaGroupEnum.HarmattanDungeonLoadingDocks,
                    AreaGroupEnum.HarmattanDungeonManFacility,
                    AreaGroupEnum.HarmattanDungeonManaStation
                }
            ),

            // Ruined Warehouse
            new( AreaGroupEnum.HarmattanDungeonRuinedWarehouse,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeon4, 0 ), new( AreaManager.AreaEnum.AntiqueField, 4 ) )
                }
            ),

            // Manufacturing Facility
            new( AreaGroupEnum.HarmattanDungeonManFacility,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeon5, 0 ), new( AreaManager.AreaEnum.AntiqueField, 5 ) )
                }
            ),

            // Loading Docks
            new( AreaGroupEnum.HarmattanDungeonLoadingDocks,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeon6, 0 ), new( AreaManager.AreaEnum.AntiqueField, 6 ) )
                }
            ),

            // Mana Transfer Station
            new( AreaGroupEnum.HarmattanDungeonManaStation,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeon8, 0 ), new( AreaManager.AreaEnum.AntiqueField, 8 ) )
                }
            ),

            // Vampire Lair
            new( AreaGroupEnum.HarmattanBloodMageHideout,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 1 ), new( AreaManager.AreaEnum.AntiqueField, 11 ) )
                }
            ),

            // Mountain Wendigo Cave
            new( AreaGroupEnum.HarmattanWendigoLair,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 2 ), new( AreaManager.AreaEnum.AntiqueField, 12 ) )
                }
            ),

            // Veaber Cave
            new( AreaGroupEnum.HarmattanVeaberCave,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 3 ), new( AreaManager.AreaEnum.AntiqueField, 13 ) )
                }
            ),

            // Kazite Camp
            new( AreaGroupEnum.HarmattanKaziteCamp,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 4 ), new( AreaManager.AreaEnum.AntiqueField, 14 ) )
                }
            ),

            // Butcher's House
            new( AreaGroupEnum.HarmattanAbandonedStorage,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 5 ), new( AreaManager.AreaEnum.AntiqueField, 15 ) )
                }
            ),

            // Old Harmattan Entrance
            new( AreaGroupEnum.HarmattanOldHarmattanEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 6 ), new( AreaManager.AreaEnum.AntiqueField, 16 ) )
                }
            ),

            // Immaculate Settlement
            new( AreaGroupEnum.HarmattanImmaculateSettlement,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 7 ), new( AreaManager.AreaEnum.AntiqueField, 20 ) )
                }
            ), 

            // Antique Plateau Immaculate Camp
            new( AreaGroupEnum.HarmattanImmaculateCamp,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.AntiqueFieldDungeonsSmall, 8 ), new( AreaManager.AreaEnum.AntiqueField, 18 ) )
                }
            ),

            new( AreaGroupEnum.Berg,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Berg, 0 ), new( AreaManager.AreaEnum.Emercar, 0 ) ),
                    new( new( AreaManager.AreaEnum.Berg, 1 ), new( AreaManager.AreaEnum.Emercar, 1 ) ),
                    new( new( AreaManager.AreaEnum.Berg, 2 ), new( AreaManager.AreaEnum.Emercar, 2 ) ),
                    //new( new( AreaManager.AreaEnum.Berg, 3 ), new( AreaManager.AreaEnum.EmercarDungeon6, 0 ) ), // If this is a dead end, you can't get the best ending for chapter 1 of Levant
                }
            ),

            // Enmercar Main Area
            new( AreaGroupEnum.BergOutside,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Emercar, 0 ), new( AreaManager.AreaEnum.Berg, 0 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 1 ), new( AreaManager.AreaEnum.Berg, 1 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 2 ), new( AreaManager.AreaEnum.Berg, 2 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 16 ), new( AreaManager.AreaEnum.EmercarDungeonsSmall, 4 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 11 ), new( AreaManager.AreaEnum.EmercarDungeon5, 0 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 4 ), new( AreaManager.AreaEnum.Abrassar, 2 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 7 ), new( AreaManager.AreaEnum.EmercarDungeon2, 1 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 15 ), new( AreaManager.AreaEnum.EmercarDungeonsSmall, 3 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 6 ), new( AreaManager.AreaEnum.EmercarDungeon2, 0 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 13 ), new( AreaManager.AreaEnum.EmercarDungeonsSmall, 1 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 23 ), new( AreaManager.AreaEnum.EmercarDungeonsSmall, 9 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 18 ), new( AreaManager.AreaEnum.EmercarDungeonsSmall, 6 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 14 ), new( AreaManager.AreaEnum.EmercarDungeonsSmall, 2 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 25 ), new( AreaManager.AreaEnum.Caldera, 33 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 20 ), new( AreaManager.AreaEnum.EmercarDungeonsSmall, 8 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 3 ), new( AreaManager.AreaEnum.CierzoOutside, 2 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 19 ), new( AreaManager.AreaEnum.EmercarDungeonsSmall, 7 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 21 ), new( AreaManager.AreaEnum.HallowedMarsh, 22 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 9 ), new( AreaManager.AreaEnum.EmercarDungeon3, 0 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 17 ), new( AreaManager.AreaEnum.EmercarDungeonsSmall, 5 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 10 ), new( AreaManager.AreaEnum.EmercarDungeon4, 0 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 5 ), new( AreaManager.AreaEnum.EmercarDungeon1, 0 ) ),
                    new( new( AreaManager.AreaEnum.Emercar, 22 ), new( AreaManager.AreaEnum.EmercarDungeon3, 1 ) ),
                }
            ),

            // Enmercar Corrupt Hive Zone
            new( AreaGroupEnum.BergOutsideCorruptHiveZone,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Emercar, 8 ), new( AreaManager.AreaEnum.EmercarDungeon2, 2 ) ),
                }
            ),

            // Royal Manticore Lair
            new( AreaGroupEnum.BergRoyalManticoreLair,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeon1, 0 ), new( AreaManager.AreaEnum.Emercar, 5 ) ),
                }
            ),

            // Forest Hives
            new( AreaGroupEnum.BergForestHives,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeon2, 0 ), new( AreaManager.AreaEnum.Emercar, 6 ) ),
                    new( new( AreaManager.AreaEnum.EmercarDungeon2, 2 ), new( AreaManager.AreaEnum.Emercar, 8 ) ),
                }
            ),

            // Forest Hives Other One-way Entrance
            new( AreaGroupEnum.BergForestHivesSecretEntrance,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeon2, 1 ), new( AreaManager.AreaEnum.Emercar, 7 ) ),
                }
            ),

            // Cabal of Wind Temple
            new( AreaGroupEnum.BergCabalTemple,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeon3, 0 ), new( AreaManager.AreaEnum.Emercar, 9 ) )
                },
                new AreaGroupEnum[] { AreaGroupEnum.BergCabalTempleBlockedExit }
            ),

            // Cabal of Wind Temple (Blocked Exit)
            new( AreaGroupEnum.BergCabalTempleBlockedExit,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeon3, 1 ), new( AreaManager.AreaEnum.Emercar, 22 ) )
                }
            ),

            // Face of the Ancients
            new( AreaGroupEnum.BergFaceOfAncients,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeon4, 0 ), new( AreaManager.AreaEnum.Emercar, 10 ) )
                }
            ),

            // Ancestor's Resting Place
            new( AreaGroupEnum.BergAncestorRestingPlace,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeon5, 0 ), new( AreaManager.AreaEnum.Emercar, 11 ) )
                }
            ),

            // Necropolis
            // If this is a dead end, you can't get the best ending for chapter 1 of Levant
            //new( AreaGroupEnum.BergNecropolis,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.EmercarDungeon6, 0 ), new( AreaManager.AreaEnum.Berg, 3 ) )
            //    }
            //),

            // Hunter's Lodge
            new( AreaGroupEnum.BergHunterLodge,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeonsSmall, 1 ), new( AreaManager.AreaEnum.Emercar, 13 ) )
                }
            ),

            // Hunter's Lodge With Ghost Warrior
            new( AreaGroupEnum.BergHunterLodgeGhost,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeonsSmall, 2 ), new( AreaManager.AreaEnum.Emercar, 14 ) )
                }
            ),

            // Hunter's Lodge With Workbench
            new( AreaGroupEnum.BergHunterLodgeWorkbench,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeonsSmall, 3 ), new( AreaManager.AreaEnum.Emercar, 15 ) )
                }
            ),

            // Grave Tower Underground
            new( AreaGroupEnum.BergGraveTowerUnderground,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeonsSmall, 4 ), new( AreaManager.AreaEnum.Emercar, 16 ) )
                }
            ),

            // Side Hive
            new( AreaGroupEnum.BergSideHive,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeonsSmall, 5 ), new( AreaManager.AreaEnum.Emercar, 17 ) )
                }
            ),

            // Burning Forest Cabin
            new( AreaGroupEnum.BergBurningForestCabin,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeonsSmall, 6 ), new( AreaManager.AreaEnum.Emercar, 18 ) )
                }
            ),

            // Enmercar Immaculate Camp
            new( AreaGroupEnum.BergImmaculateCamp,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeonsSmall, 7 ), new( AreaManager.AreaEnum.Emercar, 19 ) )
                }
            ),

            // One Tree Island
            new( AreaGroupEnum.BergOneTreeIsland,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeonsSmall, 8 ), new( AreaManager.AreaEnum.Emercar, 20 ) )
                }
            ),

            // Vigil Pylon
            new( AreaGroupEnum.BergVigilPylon,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.EmercarDungeonsSmall, 9 ), new( AreaManager.AreaEnum.Emercar, 23 ) )
                }
            ),

            // New Sirocco
            new( AreaGroupEnum.Sirocco,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.NewSirocco, 0 ), new( AreaManager.AreaEnum.Caldera, 10 ) )
                }
            ),

            // Caldera Region
            new( AreaGroupEnum.SiroccoOutside,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Caldera, 2 ), new( AreaManager.AreaEnum.CalderaDungeon2, 0 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 7 ), new( AreaManager.AreaEnum.CalderaDungeon7, 0 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 16 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 5 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 20 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 11 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 5 ), new( AreaManager.AreaEnum.CalderaDungeon5, 0 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 17 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 6 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 6 ), new( AreaManager.AreaEnum.CalderaDungeon6, 0 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 10 ), new( AreaManager.AreaEnum.NewSirocco, 0 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 14 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 3 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 9 ), new( AreaManager.AreaEnum.CalderaDungeon9, 0 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 15 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 4 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 13 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 2 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 33 ), new( AreaManager.AreaEnum.Emercar, 25 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 8 ), new( AreaManager.AreaEnum.CalderaDungeon8, 0 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 30 ), new( AreaManager.AreaEnum.CalderaDungeon7, 1 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 12 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 1 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 11 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 0 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 1 ), new( AreaManager.AreaEnum.CalderaDungeon1, 0 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 29 ), new( AreaManager.AreaEnum.CalderaDungeon6, 2 ) ),
                }
            ),

            // Caldera Behind Old Sirocco
            new( AreaGroupEnum.SiroccoOutsideBehindOldCity,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Caldera, 31 ), new( AreaManager.AreaEnum.CalderaDungeon9, 1 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.SiroccoOutside }
            ),

            // Caldera Behind Myrmitaur Haven
            // Leaving this out because you can get soft-locked going the wrong way through the gate
            //new( AreaGroupEnum.SiroccoOutsideBehindMyrmitaur,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.Caldera, 28 ), new( AreaManager.AreaEnum.CalderaDungeon6, 1 ) ),
            //    }
            //),

            // Caldera Behind Tower of Regrets
            new( AreaGroupEnum.SiroccoOutsideBehindRegret,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.Caldera, 19 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 12 ) ),
                    new( new( AreaManager.AreaEnum.Caldera, 32 ), new( AreaManager.AreaEnum.CalderaDungeonsSmall, 7 ) ),
                }
            ),

            // Steam Bath Tunnels
            new( AreaGroupEnum.SiroccoSteamBathTunnels,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon1, 0 ), new( AreaManager.AreaEnum.Caldera, 1 ) )
                }
            ),

            // Sulphuric Caverns
            new( AreaGroupEnum.SiroccoSulphuricCaverns,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon2, 0 ), new( AreaManager.AreaEnum.Caldera, 2 ) )
                }
            ),

            // The Eldest Brother
            new( AreaGroupEnum.SiroccoEldestBrother,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon3, 0 ), new( AreaManager.AreaEnum.CalderaDungeon9, 2 ) )
                }
            ),

            // The Grotto Of Chalcedony
            new( AreaGroupEnum.SiroccoChalcedonyGrotto,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon5, 0 ), new( AreaManager.AreaEnum.Caldera, 5 ) )
                }
            ),

            // Myrmitaur's Haven
            new( AreaGroupEnum.SiroccoMyrmitaurHaven,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon6, 0 ), new( AreaManager.AreaEnum.Caldera, 6 ) ),
                },
                new AreaGroupEnum[] { /*AreaGroupEnum.SiroccoMyrmitaurHavenLocked,*/ AreaGroupEnum.SiroccoMyrmitaurHavenBack }
            ),

            // Myrmitaur's Haven Locked Gate
            // Leaving this out because you can get soft-locked going the wrong way through the gate
            //new( AreaGroupEnum.SiroccoMyrmitaurHavenLocked,
            //    new Exit[] {
            //        new( new( AreaManager.AreaEnum.CalderaDungeon6, 1 ), new( AreaManager.AreaEnum.Caldera, 28 ) ),
            //    }
            //),

            // Myrmitaur's Haven Back Exit
            new( AreaGroupEnum.SiroccoMyrmitaurHavenBack,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon6, 2 ), new( AreaManager.AreaEnum.Caldera, 29 ) ),
                }
            ),

            // Oil Refinery (Front)
            new( AreaGroupEnum.SiroccoOilRefineryFront,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon7, 0 ), new( AreaManager.AreaEnum.Caldera, 7 ) )
                }
            ),

            // Oil Refinery (Back)
            new( AreaGroupEnum.SiroccoOilRefineryBack,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon7, 1 ), new( AreaManager.AreaEnum.Caldera, 30 ) )
                }
            ),

            // Vault of Stone
            new( AreaGroupEnum.SiroccoVaultOfStone,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon8, 0 ), new( AreaManager.AreaEnum.Caldera, 8 ) )
                }
            ),

            // Old Sirocco
            new( AreaGroupEnum.SiroccoOldCity,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon9, 0 ), new( AreaManager.AreaEnum.Caldera, 9 ) )
                },
                new AreaGroupEnum[] { AreaGroupEnum.SiroccoOldCityBack }
            ),

            // Old Sirocco (Behind Locked Gate)
            new( AreaGroupEnum.SiroccoOldCityBack,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon9, 1 ), new( AreaManager.AreaEnum.Caldera, 31 ) ),
                }
            ),

            // Old Sirocco (Behind Locked Gate 2)
            new( AreaGroupEnum.SiroccoOldCityBack2,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeon9, 2 ), new( AreaManager.AreaEnum.CalderaDungeon3, 0 ) ),
                }
            ),

            // Oily Cavern
            new( AreaGroupEnum.SiroccoOilyCavern,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 0 ), new( AreaManager.AreaEnum.Caldera, 11 ) )
                }
            ),

            // Calygary Colusseum
            new( AreaGroupEnum.SiroccoCalygaryColusseum,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 1 ), new( AreaManager.AreaEnum.Caldera, 12 ) )
                }
            ),

            // Red River
            new( AreaGroupEnum.SiroccoRedRiver,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 2 ), new( AreaManager.AreaEnum.Caldera, 13 ) )
                }
            ),

            // Caldera Immaculate's Camp
            new( AreaGroupEnum.SiroccoImmaculateCamp,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 3 ), new( AreaManager.AreaEnum.Caldera, 14 ) )
                }
            ),

            // Silkworm Refuge
            new( AreaGroupEnum.SiroccoSilkwormRefuge,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 4 ), new( AreaManager.AreaEnum.Caldera, 15 ) )
                }
            ),

            // Giant's Sauna
            new( AreaGroupEnum.SiroccoGiantSauna,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 5 ), new( AreaManager.AreaEnum.Caldera, 16 ) )
                }
            ),

            // Tower of Regret
            new( AreaGroupEnum.SiroccoTowerOfRegret,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 6 ), new( AreaManager.AreaEnum.Caldera, 17 ) ),
                }
            ),

            // Tower of Regret Back
            new( AreaGroupEnum.SiroccoTowerOfRegretBack,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 7 ), new( AreaManager.AreaEnum.Caldera, 32 ) ),
                }
            ),

            // Tower of Regret (Top Entrance)
            new( AreaGroupEnum.SiroccoTowerOfRegretTop,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 12 ), new( AreaManager.AreaEnum.Caldera, 19 ) ),
                },
                new AreaGroupEnum[] { AreaGroupEnum.SiroccoTowerOfRegret, AreaGroupEnum.SiroccoTowerOfRegretBack }
            ),

            // Ritualist's Hut
            new( AreaGroupEnum.SiroccoRitualistHut,
                new Exit[] {
                    new( new( AreaManager.AreaEnum.CalderaDungeonsSmall, 11 ), new( AreaManager.AreaEnum.Caldera, 20 ) )
                }
            ),

        };
    }
}
