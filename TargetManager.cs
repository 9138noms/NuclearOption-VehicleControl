using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VehicleControl
{
    public class TargetManager
    {
        public Unit CurrentTarget { get; private set; }
        public string TargetName { get; private set; } = "";
        public float TargetDistance { get; private set; }

        private List<Unit> potentialTargets = new List<Unit>();
        private int targetIndex = -1;
        private float lastScanTime = 0f;
        private const float SCAN_INTERVAL = 2f;
        private const float MAX_TARGET_RANGE = 20000f;

        // Reflection for setting AI target
        private FieldInfo shipAITargetField;

        public TargetManager()
        {
            CacheReflection();
        }

        private void CacheReflection()
        {
            try
            {
                shipAITargetField = typeof(ShipAI).GetField("currentTarget",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (shipAITargetField == null)
                {
                    // Try alternate names
                    foreach (string name in new[] { "target", "Target", "_target", "attackTarget" })
                    {
                        shipAITargetField = typeof(ShipAI).GetField(name,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (shipAITargetField != null) break;
                    }
                }

                Plugin.Log.LogInfo($"TargetManager: currentTarget field={shipAITargetField != null}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"TargetManager reflection failed: {e.Message}");
            }
        }

        public void Update(PossessionManager pm)
        {
            if (!pm.IsPossessing) return;

            // Tab: cycle targets
            if (Input.GetKeyDown(KeyCode.Tab))
                CycleTarget(pm);

            // T: clear target
            if (Input.GetKeyDown(KeyCode.T))
                ClearTarget(pm);

            // Periodic scan
            if (Time.time - lastScanTime > SCAN_INTERVAL)
            {
                lastScanTime = Time.time;
                ScanTargets(pm);
            }

            // Update distance and force-set target every frame
            // (AI Update overwrites currentTarget each frame, so we must keep re-setting it)
            if (CurrentTarget != null && pm.PossessedUnit != null)
            {
                if (CurrentTarget.gameObject.activeInHierarchy)
                {
                    TargetDistance = Vector3.Distance(
                        pm.PossessedUnit.transform.position,
                        CurrentTarget.transform.position);

                    // Re-apply target every frame to prevent AI from overwriting
                    SetAITarget(pm, CurrentTarget);
                }
                else
                {
                    // Target destroyed
                    ClearTarget(pm);
                }
            }
        }

        private void ScanTargets(PossessionManager pm)
        {
            potentialTargets.Clear();
            if (pm.PossessedUnit == null) return;

            Vector3 myPos = pm.PossessedUnit.transform.position;

            var allUnits = UnityEngine.Object.FindObjectsOfType<Unit>();
            foreach (var unit in allUnits)
            {
                if (unit == null || !unit.gameObject.activeInHierarchy) continue;
                if (unit == pm.PossessedUnit) continue;

                // Check disabled
                try
                {
                    var disabledField = typeof(Unit).GetField("disabled",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (disabledField != null && (bool)disabledField.GetValue(unit))
                        continue;
                }
                catch { }

                // Check enemy faction
                if (IsSameFaction(unit, pm.PossessedUnit))
                    continue;

                float dist = Vector3.Distance(unit.transform.position, myPos);
                if (dist > MAX_TARGET_RANGE) continue;

                potentialTargets.Add(unit);
            }

            // Sort by distance
            potentialTargets.Sort((a, b) =>
                Vector3.Distance(a.transform.position, myPos)
                    .CompareTo(Vector3.Distance(b.transform.position, myPos)));

            // Validate current target
            if (CurrentTarget != null && !potentialTargets.Contains(CurrentTarget))
            {
                ClearTarget(pm);
            }
        }

        private void CycleTarget(PossessionManager pm)
        {
            if (potentialTargets.Count == 0)
            {
                ScanTargets(pm);
                if (potentialTargets.Count == 0) return;
            }

            targetIndex = (targetIndex + 1) % potentialTargets.Count;
            CurrentTarget = potentialTargets[targetIndex];
            TargetName = CurrentTarget.name.Replace("(Clone)", "").Trim();

            // Set AI target
            SetAITarget(pm, CurrentTarget);

            Plugin.Log.LogInfo($"Target: {TargetName} ({TargetDistance:F0}m)");
        }

        private void ClearTarget(PossessionManager pm)
        {
            CurrentTarget = null;
            TargetName = "";
            TargetDistance = 0f;
            targetIndex = -1;

            // Clear AI target (let AI choose its own)
            SetAITarget(pm, null);
        }

        private void SetAITarget(PossessionManager pm, Unit target)
        {
            if (pm.PossessedType == UnitType.Ship && pm.PossessedShipAI != null)
            {
                if (shipAITargetField != null)
                {
                    try
                    {
                        shipAITargetField.SetValue(pm.PossessedShipAI, target);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"SetAITarget failed: {e.Message}");
                    }
                }
            }

            // For GroundVehicle, try to find AI component and set target
            if (pm.PossessedType == UnitType.GroundVehicle && pm.PossessedUnit != null)
            {
                var components = pm.PossessedUnit.GetComponents<MonoBehaviour>();
                foreach (var comp in components)
                {
                    string typeName = comp.GetType().Name;
                    if (!typeName.Contains("AI")) continue;

                    var targetField = comp.GetType().GetField("currentTarget",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (targetField == null)
                        targetField = comp.GetType().GetField("target",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    if (targetField != null)
                    {
                        try
                        {
                            targetField.SetValue(comp, target);
                            Plugin.Log.LogInfo($"Set {typeName}.target = {target?.name ?? "null"}");
                        }
                        catch { }
                    }
                }
            }
        }

        private bool IsSameFaction(Unit a, Unit b)
        {
            try
            {
                var hqField = typeof(Unit).GetField("HQ",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (hqField == null) return false;

                var hqA = hqField.GetValue(a);
                var hqB = hqField.GetValue(b);
                if (hqA == null || hqB == null) return false;

                var valueProp = hqA.GetType().GetProperty("Value",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (valueProp == null) return false;

                var factionA = valueProp.GetValue(hqA);
                var factionB = valueProp.GetValue(hqB);
                return factionA == factionB;
            }
            catch
            {
                return false;
            }
        }

        public void Reset()
        {
            CurrentTarget = null;
            TargetName = "";
            TargetDistance = 0f;
            targetIndex = -1;
            potentialTargets.Clear();
        }
    }
}
