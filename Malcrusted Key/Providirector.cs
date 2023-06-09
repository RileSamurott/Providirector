﻿using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using RoR2;
using RoR2.CameraModes;
using R2API.Utils;
using UnityEngine;
using System;
using System.Collections.ObjectModel;
using UnityEngine.Networking;
using ProvidirectorGame;
using RoR2.CharacterAI;
using RoR2.Stats;
using RoR2.UI;
using HarmonyLib;
using RiskOfOptions;
using RiskOfOptions.Options;
using TMPro;
using System.Xml.Linq;
using KinematicCharacterController;

#pragma warning disable Publicizer001

namespace DacityP
{
    /*
     * PregameController - Controls Pregame actions, including char select
     * CharacterSelectController - Controls Character Selection screen
     */
    [NetworkCompatibility(CompatibilityLevel.NoNeedForSync)]
    [BepInDependency("com.bepis.r2api", BepInDependency.DependencyFlags.HardDependency)]
    // I'll add a more elegant way to enable and disable the mod later, but for now the setting is in modoptions.
    [BepInDependency("com.rune580.riskofoptions", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin("com.DacityP.Providirector", "Providirector", "0.0.1")]
    public class Providirector : BaseUnityPlugin
    {
        private static ConfigEntry<bool> _modenabled;
        private static bool _modenabledfallback = true;
        public static bool modenabled => _modenabled == null ? _modenabledfallback : _modenabled.Value;
        
        private static ConfigEntry<bool> _debugenabled;
        private static bool _debugenabledfallback = false;
        public static bool debugEnabled => _debugenabled == null ? _debugenabledfallback : _debugenabled.Value;

        public static bool runIsActive = false;
        private static Harmony harmonyinst;
        private static GameObject hud;
        //private static GameObject commandPanelPrefab;
        private LocalUser localuser => LocalUserManager.readOnlyLocalUsersList[0];
        private NetworkUser dirpnuser => localuser.currentNetworkUser;
        private GameObject spectatetarget;
        private CharacterMaster currentmaster;
        private CharacterMaster defaultmaster;

        private PlayerCharacterMasterController currentcontroller => currentmaster?.playerCharacterMasterController;
        private CharacterBody currentbody
        {
            get
            {
                if (currentmaster) currentmaster.GetBody();
                return null;
            }
        }
        private CameraRigController maincam => dirpnuser.cameraRigController;
        private BaseAI currentai;
        private AssetBundle assets;
        private AssetBundle icons;
        private GameObject activehud;
        private HealthBar targethb;
        private TextMeshProUGUI spnamelabel;

        private bool _addPlayerControlToNextSpawnCardSpawn;
        private bool voidlingSpecial;
        public bool addPlayerControlToNextSpawnCardSpawn
        {
            get
            {
                return _addPlayerControlToNextSpawnCardSpawn || voidlingSpecial;
            }
            set
            {
                _addPlayerControlToNextSpawnCardSpawn = value;
            }
        }

        public static Providirector instance;

        // Compat
        private PluginInfo umbralMithrix;

        public void Awake()
        {
            RoR2Application.isModded = true;
            RoR2.Run.onRunDestroyGlobal += Run_onRunDestroyGlobal;
            Run.onServerGameOver += Run_onServerGameOver;
            On.RoR2.Run.Start += Run_Start;
            CommandHelper.AddToConsoleWhenReady();

            var path = System.IO.Path.GetDirectoryName(Info.Location);
            assets = AssetBundle.LoadFromFile(System.IO.Path.Combine(path, "providirectorui"));
            hud = assets.LoadAsset<GameObject>("ProvidirectorUIRoot");
            icons = AssetBundle.LoadFromFile(System.IO.Path.Combine(path, "monstericons"));
            MonsterIcon.AddIconsFromBundle(icons);

            harmonyinst = new Harmony(Info.Metadata.GUID);

            _modenabled = Config.Bind<bool>("General", "Mod Enabled", true, "If checked, the mod is enabled and will be started in any multiplayer games where there are 2 or more players, and you are the host.");
            _debugenabled = Config.Bind<bool>("General", "Debug Mode", false, "Whether or not debug mode is enabled. This enables the mod to run in singleplayer games and enables more controls for Director mode (targeting non-player bodies, debug Lemurian, etc.)\nNOTE: DO NOT LEAVE THIS ON DURING REGULAR GAMEPLAY!\nTHIS MODE PREVENTS GAME OVERS AND IS PRONE TO SOFTLOCKS.");
            if (Chainloader.PluginInfos.ContainsKey("com.rune580.riskofoptions")) SetupRiskOfOptions();
            RunHookSetup();
        }

        public void Start()
        {
            if (Chainloader.PluginInfos.TryGetValue("com.Nuxlar.UmbralMithrix", out var _umbralMithrix)) umbralMithrix = _umbralMithrix;
        }

        private void RunHookSetup()
        {
            RoR2Application.onUpdate += RoR2Application_onUpdate;
            On.RoR2.Run.OnServerSceneChanged += Run_OnServerSceneChanged;
            On.RoR2.RunCameraManager.Update += RunCameraManager_Update;
            On.RoR2.Run.OnUserAdded += Run_OnUserAdded;
            On.RoR2.Run.BeginGameOver += Run_BeginGameOver;
            On.RoR2.CombatDirector.Awake += CombatDirector_Awake;
            On.RoR2.CharacterSpawnCard.GetPreSpawnSetupCallback += NewPrespawnSetup;
            On.RoR2.MapZone.TeleportBody += MapZone_TeleportBody;
            On.RoR2.VoidRaidGauntletController.Start += VoidRaidGauntletController_Start;
            On.EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState.PreEncounterBegin += BrotherEncounterPhaseBaseState_PreEncounterBegin;
            On.EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState.OnMemberAddedServer += MithrixPlayerControlSetup;
            On.EntityStates.Missions.BrotherEncounter.PreEncounter.OnEnter += PreEncounterCollisionDisable;
            On.EntityStates.Missions.BrotherEncounter.Phase1.OnEnter += LockOnPhase1;
            On.EntityStates.Missions.BrotherEncounter.Phase2.OnEnter += ActivateBoostPhase2;
            On.EntityStates.Missions.BrotherEncounter.Phase3.OnEnter += LockOnPhase3;
            On.EntityStates.Missions.BrotherEncounter.EncounterFinished.OnEnter += ActivateFinalBoost;
            if (harmonyinst != null) harmonyinst.PatchAll(typeof(HarmonyPatches));
        }

        private void VoidRaidGauntletController_Start(On.RoR2.VoidRaidGauntletController.orig_Start orig, VoidRaidGauntletController self)
        {
            orig(self);
            if (runIsActive)
            {
                voidlingSpecial = true;
                foreach (ScriptedCombatEncounter sce in self.phaseEncounters) sce.combatSquad.onMemberAddedServer += delegate (CharacterMaster c)
                {
                    if (defaultmaster)
                    {
                        Debug.Log("Successfully found the director master!");
                        defaultmaster.GetBodyObject().layer = LayerIndex.noCollision.intVal;
                    }
                    AddPlayerControl(c);
                    c.inventory.GiveItem(RoR2Content.Items.TeleportWhenOob);
                };
                Debug.Log("Voidling Special setup complete!");
            }
        }

        private void PreEncounterCollisionDisable(On.EntityStates.Missions.BrotherEncounter.PreEncounter.orig_OnEnter orig, EntityStates.Missions.BrotherEncounter.PreEncounter self)
        {
            orig(self);
            if (runIsActive && defaultmaster && defaultmaster.GetBodyObject()) defaultmaster.GetBodyObject().layer = LayerIndex.noCollision.intVal;
            else Debug.LogError("Unable to find the default master for the director!");
        }

        private void MapZone_TeleportBody(On.RoR2.MapZone.orig_TeleportBody orig, MapZone self, CharacterBody characterBody)
        {
            // Special exception
            if (characterBody == defaultmaster.GetBody())
            {
                Debug.LogWarning("In-zone TP cancelled for the director.");
                return;
            }
            orig(self, characterBody);
        }

        private void Run_BeginGameOver(On.RoR2.Run.orig_BeginGameOver orig, Run self, GameEndingDef gameEndingDef)
        {
            if (debugEnabled && runIsActive)
            {
                //Debug.LogWarning("Game Over prevented by Providirector's Debug Mode. To turn this off, ask the server host to disable the Providirector debug mode in mod settings.");
                return;
            }
            orig(self, gameEndingDef);
        }

        private void SetupRiskOfOptions()
        {
            Debug.LogWarning("Setting up Risk of Options for Providirector!");
            ModSettingsManager.AddOption(new CheckBoxOption(_modenabled));
            ModSettingsManager.AddOption(new CheckBoxOption(_debugenabled));
        }

        private void Run_Start(On.RoR2.Run.orig_Start orig, Run self)
        {
            if (modenabled && NetworkServer.active && (self.participatingPlayerCount > 1 || debugEnabled))
            {
                runIsActive = true;
                Debug.Log("Providirector has been set up for this run!");
                if (LocalUserManager.readOnlyLocalUsersList == null) { Debug.Log("No local users! Something is terribly wrong."); return; }
            }
            orig(self);
        }

        private void Run_onServerGameOver(Run run, GameEndingDef ending)
        {
            Run_onRunDestroyGlobal(run);
        }

        private void Run_onRunDestroyGlobal(Run obj)
        {
            if (activehud) Destroy(activehud);
            activehud = null;
            runIsActive = false;
            spectatetarget = null;
            currentmaster = null;
            defaultmaster = null;
            voidlingSpecial = false;
            addPlayerControlToNextSpawnCardSpawn = false;
        }

        void OnEnable()
        {
            instance = this;
        }

        void OnDisable()
        {
            instance = null;
        }

        private void ActivateFinalBoost(On.EntityStates.Missions.BrotherEncounter.EncounterFinished.orig_OnEnter orig, EntityStates.Missions.BrotherEncounter.EncounterFinished self)
        {
            orig(self);
            if (DirectorState.instance) DirectorState.instance.rateModifier = DirectorState.RateModifier.TeleporterBoosted;
        }

        private void LockOnPhase3(On.EntityStates.Missions.BrotherEncounter.Phase3.orig_OnEnter orig, EntityStates.Missions.BrotherEncounter.Phase3 self)
        {
            orig(self);
            if (DirectorState.instance) DirectorState.instance.rateModifier = DirectorState.RateModifier.Locked;
        }

        private void ActivateBoostPhase2(On.EntityStates.Missions.BrotherEncounter.Phase2.orig_OnEnter orig, EntityStates.Missions.BrotherEncounter.Phase2 self)
        {
            orig(self);
            if (DirectorState.instance)
            {
                if (umbralMithrix != null) DirectorState.instance.rateModifier = DirectorState.RateModifier.Locked;
                else DirectorState.instance.rateModifier = DirectorState.RateModifier.TeleporterBoosted;
            }
        }

        private void LockOnPhase1(On.EntityStates.Missions.BrotherEncounter.Phase1.orig_OnEnter orig, EntityStates.Missions.BrotherEncounter.Phase1 self)
        {
            orig(self);
            if (DirectorState.instance) DirectorState.instance.rateModifier = DirectorState.RateModifier.Locked;
        }

        private Action<CharacterMaster> NewPrespawnSetup(On.RoR2.CharacterSpawnCard.orig_GetPreSpawnSetupCallback orig, CharacterSpawnCard self)
        {
            return (CharacterMaster c) =>
            {
                PlayerCharacterMasterController cmc = c.GetComponent<PlayerCharacterMasterController>();
                PlayerStatsComponent psc = c.GetComponent<PlayerStatsComponent>();
                if (addPlayerControlToNextSpawnCardSpawn)
                {
                    if (!cmc) cmc = c.gameObject.AddComponent<PlayerCharacterMasterController>();
                    cmc.enabled = false;
                    if (!psc) c.gameObject.AddComponent<PlayerStatsComponent>();
                    Debug.LogFormat("Added player controls to {0}", c.name);
                    addPlayerControlToNextSpawnCardSpawn = false;
                }
            };
        }

        private void MithrixPlayerControlSetup(On.EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState.orig_OnMemberAddedServer orig, EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState self, CharacterMaster master)
        {
            orig(self, master);
            if ((self.phaseControllerChildString == "Phase2" && umbralMithrix == null) || !runIsActive) return;
            AddPlayerControl(master);
        }

        private void BrotherEncounterPhaseBaseState_PreEncounterBegin(On.EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState.orig_PreEncounterBegin orig, EntityStates.Missions.BrotherEncounter.BrotherEncounterPhaseBaseState self)
        {
            orig(self);
            if ((self.phaseControllerChildString == "Phase2" && umbralMithrix == null) || !runIsActive) return;
            addPlayerControlToNextSpawnCardSpawn = true;
        }

        private void CombatDirector_Awake(On.RoR2.CombatDirector.orig_Awake orig, CombatDirector self)
        {
            if (runIsActive)
            {
                self.creditMultiplier *= 0.85f;
            }
            orig(self);
        }

        private void RoR2Application_onUpdate()
        {
            if (!runIsActive) return;
            if (dirpnuser == null) return;

            // Purely meant for figuring out how to get game over screen
            /*
            string text = "============\n";
            foreach (PlayerCharacterMasterController pcmc in PlayerCharacterMasterController.instances)
            {
                text += string.Format("{0}: {1}\n", pcmc.master, pcmc.preventGameOver);
            }
            
            Debug.Log(text);
            */
            if (spectatetarget == null && !currentmaster) ChangeNextTarget(); // Attempt to lock on to anything at all, every frame - This is to prevent problems with the camera not auto-locking
            InputManager.SwapPage.PushState(Input.GetKey(KeyCode.Space));
            InputManager.Slot1.PushState(Input.GetKey(KeyCode.Alpha1));
            InputManager.Slot2.PushState(Input.GetKey(KeyCode.Alpha2));
            InputManager.Slot3.PushState(Input.GetKey(KeyCode.Alpha3));
            InputManager.Slot4.PushState(Input.GetKey(KeyCode.Alpha4));
            InputManager.Slot5.PushState(Input.GetKey(KeyCode.Alpha5));
            InputManager.Slot6.PushState(Input.GetKey(KeyCode.Alpha6));
            InputManager.DebugSpawn.PushState(Input.GetKey(KeyCode.Alpha0));
            InputManager.BoostTarget.PushState(Input.GetKey(KeyCode.B));
            InputManager.ToggleAffixCommon.PushState(Input.GetKey(KeyCode.C));
            InputManager.ToggleAffixRare.PushState(Input.GetKey(KeyCode.V));
            InputManager.NextTarget.PushState(Input.GetKey(KeyCode.Mouse0));
            InputManager.PrevTarget.PushState(Input.GetKey(KeyCode.Mouse1));
            InputManager.FocusTarget.PushState(Input.GetKey(KeyCode.F));
            Vector3 pos = Vector3.zero;
            Quaternion rot = Quaternion.identity;
            if (dirpnuser && maincam)
            {
                pos = maincam.sceneCam.transform.position;
                rot = maincam.sceneCam.transform.rotation;
                pos = pos + rot * new Vector3(0, 0, 5);
            }
            if (DirectorState.instance == null) return;
            if (InputManager.ToggleAffixCommon.justPressed) DirectorState.instance.eliteTier = DirectorState.EliteTier.Level1;
            if (InputManager.ToggleAffixCommon.justReleased) DirectorState.instance.eliteTier = DirectorState.EliteTier.Basic;
            if (InputManager.ToggleAffixRare.justPressed) DirectorState.instance.eliteTier = DirectorState.EliteTier.Level2;
            if (InputManager.ToggleAffixRare.justReleased) DirectorState.instance.eliteTier = DirectorState.EliteTier.Basic;
            if ((localuser.eventSystem && localuser.eventSystem.isCursorVisible) || currentmaster != defaultmaster) return;
            if (InputManager.DebugSpawn.justPressed && debugEnabled) AddPlayerControl(Spawn("LemurianMaster", "LemurianBody", pos, rot));
            if (InputManager.NextTarget.justPressed) ChangeNextTarget();
            if (InputManager.PrevTarget.justPressed) ChangePreviousTarget();
            if (InputManager.Slot1.justPressed) DirectorState.instance.TrySpawn(0, pos, rot);
            if (InputManager.Slot2.justPressed) DirectorState.instance.TrySpawn(1, pos, rot);
            if (InputManager.Slot3.justPressed) DirectorState.instance.TrySpawn(2, pos, rot);
            if (InputManager.Slot4.justPressed) DirectorState.instance.TrySpawn(3, pos, rot);
            if (InputManager.Slot5.justPressed) DirectorState.instance.TrySpawn(4, pos, rot);
            if (InputManager.Slot6.justPressed) DirectorState.instance.TrySpawn(5, pos, rot);
            if (InputManager.SwapPage.justPressed) DirectorState.instance.secondPage = !DirectorState.instance.secondPage;
            if (InputManager.FocusTarget.justPressed)
            {
                if (spectatetarget) {
                    CharacterMaster target = spectatetarget.GetComponent<CharacterMaster>();
                    foreach (CharacterMaster c in DirectorState.instance.spawnedCharacters)
                    {
                        if (target == c) continue;
                        foreach (BaseAI ai in c.aiComponents)
                        {
                            ai.currentEnemy.gameObject = spectatetarget;
                            ai.enemyAttentionDuration = 10f;
                        }
                    }
                }
            }
            if (InputManager.BoostTarget.justPressed) DirectorState.instance.ApplyFrenzy();
        }

        private void Run_OnUserAdded(On.RoR2.Run.orig_OnUserAdded orig, Run self, NetworkUser user)
        {
            orig(self, user);
            if (!user.master) { Debug.LogWarning("No master found on the spawned player!"); return; }
            if (!runIsActive) return;
            else if (user != dirpnuser)
            {
                if (user) spectatetarget = user.master.GetBodyObject();
                return;
            }
                // At this point we know that the user being added is the player who will be the director

            defaultmaster = user.master;
            defaultmaster.bodyPrefab = BodyCatalog.FindBodyPrefab("WispBody");
            defaultmaster.godMode = true;
            
            defaultmaster.teamIndex = TeamIndex.Neutral;
            currentmaster = defaultmaster;
            currentmaster.inventory.GiveItem(RoR2Content.Items.TeleportWhenOob);
            currentai = null;
            var bodysetupdel = (CharacterBody body) =>
            {
                if (!body)
                {
                    Debug.LogWarning("No body object found!");
                    return;
                }
                body.AddBuff(RoR2Content.Buffs.Cloak);
                body.AddBuff(RoR2Content.Buffs.Intangible);
                body.AddBuff(RoR2Content.Buffs.Entangle);
                Debug.LogWarning("Added buffs.");
                body.teamComponent.teamIndex = TeamIndex.Neutral;
                body.skillLocator.primary = null;
                body.skillLocator.secondary = null;
                body.skillLocator.utility = null;
                body.skillLocator.special = null;
                body.master.preventGameOver = false;
                body.gameObject.layer = LayerIndex.noCollision.intVal;
                ChangeNextTarget();
                Debug.LogFormat("Setting up in {0}", Stage.instance.sceneDef.baseSceneName);
                if (Stage.instance.sceneDef.baseSceneName.Equals("moon2"))
                {
                    Debug.Log("Moon setup called");
                    body.gameObject.layer = LayerIndex.playerBody.intVal;
                    TeleportHelper.TeleportGameObject(body.gameObject, new Vector3(-47.0f, 524.0f, -23.0f));
                }
                else if (Stage.instance.sceneDef.baseSceneName.Equals("voidraid"))
                {
                    Debug.Log("Voidling setup called");
                    body.gameObject.layer = LayerIndex.playerBody.intVal;
                    TeleportHelper.TeleportGameObject(body.gameObject, new Vector3(-81.0f, 50.0f, 82.0f));
                }
                Debug.Log("Setup complete!");
            };
            defaultmaster.onBodyStart += bodysetupdel;
        }

        private void Run_OnServerSceneChanged(On.RoR2.Run.orig_OnServerSceneChanged orig, Run self, string sceneName)
        {
            orig(self, sceneName);
            //if (sceneName == "moon2") viewingOverride = moon2FightActivate;
            //else if (sceneName == "moon") viewingOverride = moonFightActivate;
            //else viewingOverride = Vector3.zero;
            if (runIsActive) SetupSceneChange();
        }

        private void RunCameraManager_Update(On.RoR2.RunCameraManager.orig_Update orig, RunCameraManager self)
        {
            if (!runIsActive)
            {
                orig(self);
                return;
            }
            // Copied code with a single exception implemented for the new player
            bool flag = Stage.instance;
            CameraRigController[] cameras = self.cameras;
            if (flag)
            {
                int i = 0;
                for (int count = CameraRigController.readOnlyInstancesList.Count; i < count; i++)
                {
                    if (CameraRigController.readOnlyInstancesList[i].suppressPlayerCameras)
                    {
                        flag = false;
                        return;
                    }
                }
            }
            if (flag)
            {
                int num = 0;
                ReadOnlyCollection<NetworkUser> readOnlyLocalPlayersList = NetworkUser.readOnlyLocalPlayersList;
                for (int j = 0; j < readOnlyLocalPlayersList.Count; j++)
                {
                    NetworkUser networkUser = readOnlyLocalPlayersList[j];
                    CameraRigController cameraRigController = cameras[num];
                    if (!cameraRigController)
                    {
                        cameraRigController = UnityEngine.Object.Instantiate(LegacyResourcesAPI.Load<GameObject>("Prefabs/Main Camera")).GetComponent<CameraRigController>();
                        cameras[num] = cameraRigController;
                    }
                    cameraRigController.viewer = networkUser;
                    networkUser.cameraRigController = cameraRigController;
                    GameObject networkUserBodyObject = RunCameraManager.GetNetworkUserBodyObject(networkUser);
                    ForceSpectate forceSpectate = InstanceTracker.FirstOrNull<ForceSpectate>();

                    if ((bool)forceSpectate)
                    {
                        cameraRigController.nextTarget = forceSpectate.target;
                        cameraRigController.cameraMode = CameraModePlayerBasic.spectator;
                    }
                    else if ((bool)networkUserBodyObject && defaultmaster != null && networkUserBodyObject != defaultmaster.GetBodyObject())
                    {
                        cameraRigController.nextTarget = networkUserBodyObject;
                        cameraRigController.cameraMode = CameraModePlayerBasic.playerBasic;
                    } else if (runIsActive) {
                        cameraRigController.nextTarget = spectatetarget;
                        cameraRigController.cameraMode = CameraModeDirector.director;
                    }
                    else if (!cameraRigController.disableSpectating)
                    {
                        cameraRigController.cameraMode = CameraModePlayerBasic.spectator;
                        if (!cameraRigController.target)
                        {
                            cameraRigController.nextTarget = CameraRigControllerSpectateControls.GetNextSpectateGameObject(networkUser, null);
                        }
                    }
                    else
                    {
                        cameraRigController.cameraMode = CameraModeNone.instance;
                    }
                    num++;
                }
                int num2 = num;
                for (int k = num; k < cameras.Length; k++)
                {
                    ref CameraRigController reference = ref cameras[num];
                    if ((object)reference != null)
                    {
                        if ((bool)reference)
                        {
                            UnityEngine.Object.Destroy(cameras[num].gameObject);
                        }
                        reference = null;
                    }
                }
                Rect[] array = RunCameraManager.screenLayouts[num2];
                for (int l = 0; l < num2; l++)
                {
                    cameras[l].viewport = array[l];
                }
                return;
            }
            for (int m = 0; m < cameras.Length; m++)
            {
                if ((bool)cameras[m])
                {
                    UnityEngine.Object.Destroy(cameras[m].gameObject);
                }
            }
        }

        private void SetupSceneChange()
        {
            DirectorState.UpdateMonsterSelection();
            voidlingSpecial = false;
            if (activehud == null)
            {
                activehud = Instantiate(hud);
                Debug.LogWarning("Instantiated new hud.");
            }
            Invoke("PostStart", 0.7f);
        }

        private void PostStart()
        {
            DisengagePlayerControl();
            Debug.Log("Starting post-init");
            activehud.GetComponent<Canvas>().worldCamera = maincam.uiCam;
            Debug.Log("Camera set.");
            if (DirectorState.instance != null) DirectorState.instance.RefreshForNewStage();
            else Debug.LogWarning("No DirectorState exists yet.");
            Debug.Log("UI Instantiated.");
            ChildLocator t = activehud.GetComponent<ChildLocator>();
            targethb = t.FindChild(0).GetComponent<HealthBar>();
            spnamelabel = t.FindChild(1).GetComponent<TextMeshProUGUI>();
            ChangeNextTarget();
            HUDEnable();
            SetBaseUIVisible(false);
        }

        private void SetBaseUIVisible(bool value)
        {
            Transform root = maincam.hud.mainContainer.transform;
            Transform basicstats = root.Find("MainUIArea/SpringCanvas/BottomLeftCluster/BarRoots");
            Transform skillicons = root.Find("MainUIArea/SpringCanvas/BottomRightCluster");
            Transform notifs = root.Find("NotificationArea");
            Transform spectateinfo = root.Find("MainUIArea/SpringCanvas/BottomCenterCluster");
            if (basicstats) basicstats.gameObject.SetActive(value);
            if (skillicons) skillicons.gameObject.SetActive(value);
            if (notifs) notifs.gameObject.SetActive(value);
            if (spectateinfo) spectateinfo.gameObject.SetActive(value);
        }

        private void ChangeNextTarget()
        {
            ReadOnlyCollection<CharacterBody> readOnlyInstancesList = CharacterBody.readOnlyInstancesList;
            if (readOnlyInstancesList.Count == 0) return;
            CharacterBody characterBody = spectatetarget ? spectatetarget.GetComponent<CharacterBody>() : null;
            int num = (characterBody ? readOnlyInstancesList.IndexOf(characterBody) : 0);
            for (int i = num + 1; i < readOnlyInstancesList.Count; i++)
            {
                if ((readOnlyInstancesList[i].teamComponent && readOnlyInstancesList[i].teamComponent.teamIndex == TeamIndex.Player) || debugEnabled)
                {
                    spectatetarget = readOnlyInstancesList[i].gameObject;
                    //Debug.LogFormat("Now spectating {0} on team {1}", readOnlyInstancesList[i].name, readOnlyInstancesList[i].teamComponent.teamIndex);
                    UpdateHUD();
                    return;
                }
            }
            for (int j = 0; j <= num; j++)
            {
                if ((readOnlyInstancesList[j].teamComponent && readOnlyInstancesList[j].teamComponent.teamIndex == TeamIndex.Player) || debugEnabled)
                {
                    spectatetarget = readOnlyInstancesList[j].gameObject;
                    //Debug.LogFormat("Now spectating {0} on team {1}", readOnlyInstancesList[j].name, readOnlyInstancesList[j].teamComponent.teamIndex);
                    UpdateHUD();
                    return;
                }
            }
        }

        private void ChangePreviousTarget()
        {
            ReadOnlyCollection<CharacterBody> readOnlyInstancesList = CharacterBody.readOnlyInstancesList;
            if (readOnlyInstancesList.Count == 0)
            {
                spectatetarget = null;
                return;
            }
            CharacterBody characterBody = spectatetarget ? spectatetarget.GetComponent<CharacterBody>() : null;
            int num = (characterBody ? readOnlyInstancesList.IndexOf(characterBody) : 0);
            for (int i = num - 1; i >= 0; i--)
            {
                if ((readOnlyInstancesList[i].teamComponent && readOnlyInstancesList[i].teamComponent.teamIndex == TeamIndex.Player) || debugEnabled)
                {
                    spectatetarget = readOnlyInstancesList[i].gameObject;
                    //Debug.LogFormat("Now spectating {0} on team {1}", readOnlyInstancesList[i].name, readOnlyInstancesList[i].teamComponent.teamIndex);
                    UpdateHUD();
                    return;
                }
            }
            for (int j = readOnlyInstancesList.Count - 1; j >= num; j--)
            {
                if ((readOnlyInstancesList[j].teamComponent && readOnlyInstancesList[j].teamComponent.teamIndex == TeamIndex.Player) || debugEnabled)
                {
                    spectatetarget = readOnlyInstancesList[j].gameObject;
                    //Debug.LogFormat("Now spectating {0} on team {1}", readOnlyInstancesList[j].name, readOnlyInstancesList[j].teamComponent.teamIndex);
                    UpdateHUD();
                    return;
                }
            }
        }

        private void UpdateHUD()
        {
            if (spectatetarget && targethb) targethb.source = spectatetarget.GetComponent<HealthComponent>();
            if (spectatetarget && spnamelabel) spnamelabel.text = Util.GetBestBodyName(spectatetarget);
        }

        private void AddPlayerControl(CharacterMaster c)
        {
            if (c == null || c == currentmaster)
            {
                Debug.LogWarning("Attempt to switch control onto a nonexistent or already present character!");
                return;
            }
            Debug.LogFormat("Attempting to take control of CharacterMaster {0}", c.name);
            if (currentmaster) DisengagePlayerControl(revertfallback: false);
            else Debug.Log("No currently set master - we can proceed as normal.");
            currentmaster = c;
            currentai = currentmaster.GetComponent<BaseAI>();
            currentmaster.playerCharacterMasterController = currentmaster.GetComponent<PlayerCharacterMasterController>();
            PlayerStatsComponent playerStatsComponent = currentmaster.GetComponent<PlayerStatsComponent>();
            if (!currentcontroller)
            {
                Debug.LogWarningFormat("CharacterMaster {0} does not have a PCMC! Instantiating one now... though this will lead to desyncs between the client and server.", c.name);
                currentmaster.playerCharacterMasterController = c.gameObject.AddComponent<PlayerCharacterMasterController>();
            }
            if (!playerStatsComponent)
            {
                Debug.LogWarningFormat("CharacterMaster {0} does not have a PSC! Instantiating one now... though this will lead to desyncs between the client and server.", c.name);
                playerStatsComponent = c.gameObject.AddComponent<PlayerStatsComponent>();
            }
            GameObject oldprefab = c.bodyPrefab;
            currentcontroller.LinkToNetworkUserServer(dirpnuser);
            currentcontroller.master.bodyPrefab = oldprefab; // RESET
            currentmaster.preventGameOver = false;
            if (c != defaultmaster) HUDDisable();
            else
            {
                HUDEnable();
                ChangeNextTarget();
            }
            currentcontroller.enabled = true;
            playerStatsComponent.enabled = true;
            Run.instance.userMasters[dirpnuser.id] = c;
            AIDisable();
            if (currentai) currentai.onBodyDiscovered += AIDisable;
            GlobalEventManager.onCharacterDeathGlobal += DisengagePlayerControl;
            if (currentbody) {
                //currentbody.networkIdentity.localPlayerAuthority = true;
                //currentbody.networkIdentity.AssignClientAuthority(dirpnuser.connectionToClient);
            }
            currentmaster.onBodyStart += delegate(CharacterBody b) {
                b.master.preventGameOver = false;
                //b.networkIdentity.localPlayerAuthority = true;
                //b.networkIdentity.AssignClientAuthority(dirpnuser.connectionToClient);
            };
            SetBaseUIVisible(c != defaultmaster);
            if (c == defaultmaster) ChangeNextTarget();
            Debug.LogFormat("{0} set as new master.", currentmaster);
        }

        private void DisengagePlayerControl(bool revertfallback = true)
        {
            Debug.LogFormat("Disengaging player control from {0}...", currentmaster);
            if (currentmaster)
            {
                if (currentmaster != defaultmaster)
                {
                    Debug.Log("Non-default character, performing special remove...");
                    GlobalEventManager.onCharacterDeathGlobal -= DisengagePlayerControl;
                    if (currentai) currentai.onBodyDiscovered -= AIDisable;
                    AIEnable();
                    currentai = null;
                    if (currentbody && currentbody.networkIdentity) currentbody.networkIdentity.RemoveClientAuthority(dirpnuser.connectionToClient);
                    currentmaster.playerCharacterMasterController = null;
                }
                if (currentcontroller) currentcontroller.enabled = false;
                Debug.LogFormat("Characterbody disengaged! There are now {0} active PCMCs", PlayerCharacterMasterController.instances.Count);
                currentmaster = null;
            }
            //Debug.Log("Deactivated.");
            if ((currentmaster == null) && revertfallback)
            {
                Debug.Log("Reverting to default master...");
                AddPlayerControl(defaultmaster);
            }
        }

        private void AIDisable()
        {
            if (currentai)
            {
                if (currentbody) currentai.OnBodyLost(currentbody);
                currentai.enabled = false;
                //Debug.Log("AI Disabled.");
            }
            else
            {
                Debug.LogWarning("Warning: No AI component to disable.");
            }
        }

        private void AIDisable(CharacterBody _) { AIDisable(); }
        
        private void AIEnable()
        {
            if (currentai)
            {
                currentai.enabled = true;
                if (currentbody) currentai.OnBodyStart(currentbody);
                //Debug.Log("AI Enabled.");
            }
        }

        private void DisengagePlayerControl(DamageReport dr)
        {
            if (dr.victimMaster == currentmaster) DisengagePlayerControl();
        }

        public CharacterMaster Spawn(string mastername, string bodyname, Vector3 position, Quaternion rotation, EliteDef eliteDef = null, int levelbonus = 0, bool includePlayerControlInterface = true)
        {
            // Modified code taken from DebugToolkit
            GameObject preinst = MasterCatalog.FindMasterPrefab(mastername);
            GameObject preinstbody = BodyCatalog.FindBodyPrefab(bodyname);
            if (!preinst || !preinstbody) return null;
            GameObject bodyGameObject = Instantiate(preinst, position, rotation);
            CharacterMaster master = bodyGameObject.GetComponent<CharacterMaster>();
            if (includePlayerControlInterface)
            {
                bodyGameObject.AddComponent<PlayerCharacterMasterController>().enabled = false;
                if (!bodyGameObject.GetComponent<PlayerStatsComponent>())
                {
                    Debug.Log("CharacterMaster does not have stat component. Adding...");
                    bodyGameObject.AddComponent<PlayerStatsComponent>().enabled = false;
                }
            }
            NetworkServer.Spawn(bodyGameObject);
            master.bodyPrefab = preinstbody;
            master.SpawnBody(position, Quaternion.identity);
            master.inventory.GiveItem(RoR2Content.Items.UseAmbientLevel);
            if (eliteDef)
            {
                master.inventory.SetEquipmentIndex(eliteDef.eliteEquipmentDef.equipmentIndex);
                master.inventory.GiveItem(RoR2Content.Items.BoostHp, Mathf.RoundToInt((eliteDef.healthBoostCoefficient - 1) * 10));
                master.inventory.GiveItem(RoR2Content.Items.BoostDamage, Mathf.RoundToInt(eliteDef.damageBoostCoefficient - 1) * 10);
            }
            if (levelbonus > 0) master.inventory.GiveItem(RoR2Content.Items.LevelBonus, levelbonus);
            master.teamIndex = TeamIndex.Monster;
            master.GetBody().teamComponent.teamIndex = TeamIndex.Monster;
            return master;
        }

        private void HUDDisable()
        {
            if (activehud)
            {
                activehud.SetActive(false);
            }
        }

        private void HUDEnable()
        {
            if (activehud)
            {
                activehud.SetActive(true);
            }
        }

        [ConCommand(commandName = "check_cameras", flags = ConVarFlags.None, helpText = "Checks the state of all currently active CRCs.")]
        private static void CCCheckCameras(ConCommandArgs args)
        {
            int i = 0;
            foreach (CameraRigController c in CameraRigController.readOnlyInstancesList)
            {
                Camera scenecam = c.sceneCam;
                Debug.LogFormat("Camera in Slot {0} ===", i);
                Debug.Log("sceneCam Scene: " + scenecam.scene.name);
                Debug.Log("sceneCam Position + Rot: " + scenecam.transform.position + " -- " + scenecam.transform.rotation);
                Debug.Log("Viewer: " + (c.viewer ? c.viewer.userName : "null"));
                Debug.Log("Target: " + (c.target ? c.target.name : "null"));
                Debug.Log("CameraMode: " + c.cameraMode.GetType());
                Debug.Log("Is Being Overridden: " + (c.hasOverride ? "Yes" : "No"));
                Debug.Log("In a Cutscene: " + (c.isCutscene ? "Yes" : "No"));
                i++;
            }
        }

        [ConCommand(commandName = "prvd_rundata", flags = ConVarFlags.None, helpText = "Check the data of the current run.")]
        private static void CCRunData(ConCommandArgs args)
        {
            if (!Run.instance)
            {
                Debug.Log("-- No run data present");
                return;
            }
            Debug.LogFormat("");
            Debug.LogFormat("Time: {0}", Run.instance.GetRunStopwatch());
            Debug.LogFormat("Players: {0}", Run.instance.participatingPlayerCount);
            Debug.LogFormat("PCMC Instances: {0}", PlayerCharacterMasterController.instances.Count);
            Debug.LogFormat("Stages Cleared: {0}", Run.instance.stageClearCount);
            Debug.LogFormat("Difficulty Scaling: {0}", Run.instance.difficultyCoefficient);
            Debug.LogFormat("Difficulty Level: {0}", Run.instance.ambientLevel);
        }

        [ConCommand(commandName = "prvd_dump", flags = ConVarFlags.None, helpText = "Dumps director data.")]
        private static void CCDumpVars(ConCommandArgs args)
        {
            Debug.Log("Providirector Data ---");
            Debug.LogFormat("Mod Enabled: {0} {1}", modenabled, debugEnabled ? "Debug" : "Normal");
            Debug.LogFormat("Active Run: ", runIsActive);
            Debug.LogFormat("Director CMaster: {0}", instance.currentmaster);
            Debug.LogFormat("Director DMaster: {0}", instance.defaultmaster);
            Debug.LogFormat("SpectateTarget: {0}", instance.spectatetarget);
        }

        [ConCommand(commandName = "prvd_players", flags = ConVarFlags.None, helpText = "Dumps player data.")]
        private static void CCDumpPlayer(ConCommandArgs args)
        {
            Debug.Log("Player Data ---");
            Debug.LogFormat("PCMC Count: {0}", PlayerCharacterMasterController.instances.Count);
            Debug.LogFormat("In-game count: {0}", Run.instance.participatingPlayerCount);
            Debug.Log("---------------");
            foreach (PlayerCharacterMasterController pcmc in PlayerCharacterMasterController.instances)
            {
                Debug.LogFormat("{0}: {1}", pcmc.GetDisplayName(), pcmc.preventGameOver);
            }
        }
    }
}

