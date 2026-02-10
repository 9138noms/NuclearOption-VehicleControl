using System;
using System.Reflection;
using HarmonyLib;

namespace VehicleControl.Patches
{
    [HarmonyPatch(typeof(ShipAI), "Steer")]
    public class ShipAI_Steer_Patch
    {
        public static ShipAI SuppressedShipAI = null;

        static bool Prefix(ShipAI __instance)
        {
            if (SuppressedShipAI != null && __instance == SuppressedShipAI)
                return false;
            return true;
        }
    }

    [HarmonyPatch(typeof(Unit), "DisableUnit")]
    public class Unit_DisableUnit_Patch
    {
        static void Postfix(Unit __instance)
        {
            if (PossessionManager.Instance != null && PossessionManager.Instance.IsPossessing)
            {
                if (__instance == PossessionManager.Instance.PossessedUnit)
                {
                    PossessionManager.Instance.ForceUnpossess("Unit destroyed");
                }
            }
        }
    }

    /// <summary>
    /// Prevent WASD from moving the orbit camera when possessing a unit.
    /// CameraOrbitState.AnyMoveInput() returns true on WASD → switches to free camera.
    /// We return false to block this.
    /// </summary>
    public static class CameraPatchHelper
    {
        private static bool cameraPatched = false;
        private static bool vehicleJobPatched = false;

        public static void ApplyManualPatches(Harmony harmony)
        {
            ApplyCameraPatches(harmony);
            ApplyVehicleJobPatches(harmony);
        }

        private static void ApplyCameraPatches(Harmony harmony)
        {
            if (cameraPatched) return;

            try
            {
                var cameraOrbitType = typeof(Unit).Assembly.GetType("CameraOrbitState");
                if (cameraOrbitType == null)
                {
                    Plugin.Log.LogWarning("CameraOrbitState type not found");
                    return;
                }

                var anyMoveInputMethod = cameraOrbitType.GetMethod("AnyMoveInput",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                if (anyMoveInputMethod != null)
                {
                    var prefix = typeof(CameraOrbit_AnyMoveInput_Patch).GetMethod("Prefix",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(anyMoveInputMethod, prefix: new HarmonyMethod(prefix));
                    Plugin.Log.LogInfo("Patched CameraOrbitState.AnyMoveInput — WASD won't move camera");
                    cameraPatched = true;
                }
                else
                {
                    Plugin.Log.LogWarning("CameraOrbitState.AnyMoveInput not found");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Camera patch failed: {e.Message}");
            }
        }

        /// <summary>
        /// Postfix on GroundVehicle.UpdateJobFields() — runs right after MB state is copied to
        /// native memory, but BEFORE the job is scheduled. Perfect timing to override mobile + inputs.
        /// </summary>
        private static void ApplyVehicleJobPatches(Harmony harmony)
        {
            if (vehicleJobPatched) return;

            try
            {
                var gvType = typeof(Unit).Assembly.GetType("GroundVehicle");
                if (gvType == null)
                {
                    Plugin.Log.LogWarning("GroundVehicle type not found for job patch");
                    return;
                }

                var updateJobFieldsMethod = gvType.GetMethod("UpdateJobFields",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (updateJobFieldsMethod != null)
                {
                    var postfix = typeof(GroundVehicle_UpdateJobFields_Patch).GetMethod("Postfix",
                        BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(updateJobFieldsMethod, postfix: new HarmonyMethod(postfix));
                    Plugin.Log.LogInfo("Patched GroundVehicle.UpdateJobFields — native override active");
                    vehicleJobPatched = true;
                }
                else
                {
                    Plugin.Log.LogWarning("GroundVehicle.UpdateJobFields not found");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Vehicle job patch failed: {e.Message}");
            }
        }
    }

    public class CameraOrbit_AnyMoveInput_Patch
    {
        static bool Prefix(ref bool __result)
        {
            if (PossessionManager.Instance != null && PossessionManager.Instance.IsPossessing)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Runs right after GroundVehicle.UpdateJobFields() copies MB state to native memory.
    /// This is BEFORE the job is scheduled, so our native memory writes are guaranteed to
    /// be seen by the job. We force mobile=false and write our input values.
    /// </summary>
    public class GroundVehicle_UpdateJobFields_Patch
    {
        static void Postfix(object __instance)
        {
            if (PossessionManager.Instance != null)
                PossessionManager.Instance.OverrideNativeForJob(__instance);
        }
    }
}
