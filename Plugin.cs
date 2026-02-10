using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using VehicleControl.Patches;

namespace VehicleControl
{
    [BepInPlugin("com.yuulf.vehiclecontrol", "Vehicle Control", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private Harmony harmony;

        private PossessionManager possessionManager;
        private InputController inputController;
        private TargetManager targetManager;
        private VehicleHUD hud;

        private void Awake()
        {
            Log = Logger;

            harmony = new Harmony("com.yuulf.vehiclecontrol");
            harmony.PatchAll();

            // Manual patch: suppress WASD camera movement during possession
            CameraPatchHelper.ApplyManualPatches(harmony);

            possessionManager = new PossessionManager();
            inputController = new InputController();
            targetManager = new TargetManager();
            hud = new VehicleHUD();

            Log.LogInfo("Vehicle Control v1.2.0 loaded — F8 to possess nearest ship/vehicle");
        }

        private void Update()
        {
            // F8: toggle possession
            if (Input.GetKeyDown(KeyCode.F8))
            {
                possessionManager.TogglePossession();
                if (!possessionManager.IsPossessing)
                {
                    inputController.Reset();
                    targetManager.Reset();
                }
            }

            if (possessionManager.IsPossessing)
            {
                possessionManager.Tick();
                inputController.Update(possessionManager);
                targetManager.Update(possessionManager);
            }
        }

        private void FixedUpdate()
        {
            // Apply physics forces to possessed ground vehicle
            if (possessionManager != null)
                possessionManager.FixedTick();
        }

        private void OnGUI()
        {
            hud.Draw(possessionManager, inputController, targetManager);
        }

        private void OnDestroy()
        {
            if (possessionManager != null && possessionManager.IsPossessing)
                possessionManager.Unpossess();
            harmony?.UnpatchSelf();
        }
    }
}
