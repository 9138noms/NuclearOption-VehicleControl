using System;
using System.Reflection;
using UnityEngine;
using VehicleControl.Patches;

namespace VehicleControl
{
    public enum UnitType { None, Ship, GroundVehicle }

    public class PossessionManager
    {
        public static PossessionManager Instance;

        public bool IsPossessing { get; private set; }
        public Unit PossessedUnit { get; private set; }
        public UnitType PossessedType { get; private set; }
        public ShipAI PossessedShipAI { get; private set; }

        // Ship inputs
        private object shipInputs;
        private FieldInfo shipThrottleField;
        private FieldInfo shipSteeringField;

        // GroundVehicle — native memory access via PtrAllocation
        private Rigidbody vehicleRB;
        private float vehicleTopSpeed;

        // Vehicle fields for AI suppression
        private FieldInfo holdPositionField;
        private FieldInfo mobileField;
        private FieldInfo topSpeedOnroadField;
        private bool vehicleHoldPositionOriginal;
        private bool vehicleMobileOriginal;

        // Native memory access
        private FieldInfo jobFieldsFieldInfo;
        private FieldInfo ptrFieldInfo; // PtrAllocation.ptr
        private int nativeInputsOffset = -1;  // offset of 'inputs' within GroundVehicleFields
        private int nativeThrottleOffset;     // offset of 'inputs.throttle'
        private int nativeSteeringOffset;     // offset of 'inputs.steering'
        private int nativeBrakeOffset;        // offset of 'inputs.brake'
        private int nativeMobileOffset = -1;  // offset of 'mobile' within GroundVehicleFields

        // Reflection cache
        private MethodInfo shipGetInputsMethod;
        private Type groundVehicleType;

        // Camera
        private object cameraStateManager;
        private MethodInfo setFollowingUnitMethod;
        private Aircraft playerAircraft;

        // Vehicle input state (set by InputController, applied in FixedTick)
        private float vehicleThrottleInput;
        private float vehicleSteeringInput;
        private float vehicleBrakeInput;

        // Game AI uses steering in [-10, 10] range; our InputController sends [-1, 1]
        private const float STEERING_SCALE = 10f;

        private const float MAX_POSSESS_DISTANCE = 15000f;

        public PossessionManager()
        {
            Instance = this;
            CacheReflection();
        }

        private void CacheReflection()
        {
            try
            {
                var asm = typeof(Unit).Assembly;
                var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // Ship inputs
                shipGetInputsMethod = typeof(Ship).GetMethod("GetInputs",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                var shipInputsType = asm.GetType("ShipInputs");
                if (shipInputsType != null)
                {
                    shipThrottleField = shipInputsType.GetField("throttle", allFlags);
                    shipSteeringField = shipInputsType.GetField("steering", allFlags);
                }
                Plugin.Log.LogInfo($"Ship reflection: GetInputs={shipGetInputsMethod != null}, throttle={shipThrottleField != null}, steering={shipSteeringField != null}");

                // GroundVehicle type
                groundVehicleType = asm.GetType("GroundVehicle");
                if (groundVehicleType != null)
                {
                    holdPositionField = groundVehicleType.GetField("holdPosition", allFlags);
                    mobileField = groundVehicleType.GetField("mobile", allFlags);
                    topSpeedOnroadField = groundVehicleType.GetField("topSpeedOnroad", allFlags);
                    jobFieldsFieldInfo = groundVehicleType.GetField("JobFields", allFlags);

                    // Pre-compute native memory offsets for VehicleInputs
                    CacheNativeOffsets(asm);

                    Plugin.Log.LogInfo($"GroundVehicle reflection: type=True, holdPosition={holdPositionField != null}, mobile={mobileField != null}, JobFields={jobFieldsFieldInfo != null}, nativeOffsets={nativeInputsOffset >= 0}");
                }
                else
                {
                    Plugin.Log.LogInfo("GroundVehicle type not found in assembly");
                }

                // Camera
                var csmType = asm.GetType("CameraStateManager");
                if (csmType != null)
                {
                    var instanceProp = csmType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static) ??
                        csmType.GetProperty("instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                    if (instanceProp != null)
                    {
                        cameraStateManager = instanceProp.GetValue(null);
                    }
                    else
                    {
                        var instanceField = csmType.GetField("Instance",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) ??
                            csmType.GetField("instance",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (instanceField != null)
                            cameraStateManager = instanceField.GetValue(null);
                    }

                    setFollowingUnitMethod = csmType.GetMethod("SetFollowingUnit",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    Plugin.Log.LogInfo($"Camera reflection: CSM={cameraStateManager != null}, SetFollowingUnit={setFollowingUnitMethod != null}");
                }

                // TargetManager cached separately
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Reflection cache failed: {e.Message}");
            }
        }

        private void CacheNativeOffsets(System.Reflection.Assembly asm)
        {
            try
            {
                var allFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                var gvfType = asm.GetType("NuclearOption.Jobs.GroundVehicleFields");
                if (gvfType == null)
                {
                    Plugin.Log.LogWarning("GroundVehicleFields type not found");
                    return;
                }

                var viType = asm.GetType("GroundVehicle+VehicleInputs");
                if (viType == null)
                {
                    Plugin.Log.LogWarning("VehicleInputs type not found");
                    return;
                }

                // Compute offsets using proper per-field alignment (sequential layout rules)
                int offset = 0;
                var gvfFields = gvfType.GetFields(allFlags);
                foreach (var f in gvfFields)
                {
                    // Align to this field's natural alignment BEFORE placing it
                    int align = GetFieldAlignment(f.FieldType);
                    if (align > 1)
                        offset = (offset + align - 1) & ~(align - 1);

                    if (f.Name == "mobile")
                        nativeMobileOffset = offset;
                    if (f.Name == "inputs")
                    {
                        nativeInputsOffset = offset;
                        break;
                    }
                    offset += GetFieldSize(f.FieldType);
                }

                if (nativeInputsOffset < 0)
                {
                    Plugin.Log.LogWarning("Could not compute inputs offset");
                    return;
                }

                // VehicleInputs: float throttle, float brake, float steering (sequential, 4 bytes each)
                var viFields = viType.GetFields(allFlags);
                int viOffset = 0;
                foreach (var f in viFields)
                {
                    if (f.Name == "throttle") nativeThrottleOffset = nativeInputsOffset + viOffset;
                    if (f.Name == "brake") nativeBrakeOffset = nativeInputsOffset + viOffset;
                    if (f.Name == "steering") nativeSteeringOffset = nativeInputsOffset + viOffset;
                    viOffset += 4;
                }

                // Cache PtrAllocation's ptr field
                if (jobFieldsFieldInfo != null)
                {
                    var ptrAllocType = jobFieldsFieldInfo.FieldType;
                    ptrFieldInfo = ptrAllocType.GetField("ptr", allFlags);
                }

                Plugin.Log.LogInfo($"Native offsets: mobile={nativeMobileOffset}, inputs={nativeInputsOffset}, throttle={nativeThrottleOffset}, steering={nativeSteeringOffset}, brake={nativeBrakeOffset}, ptr={ptrFieldInfo != null}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"CacheNativeOffsets failed: {e.Message}");
                nativeInputsOffset = -1;
            }
        }

        /// <summary>
        /// Returns the natural alignment for a type in sequential layout.
        /// bool=1, float/int=4, pointer/long=8, structs=max(field alignments).
        /// </summary>
        private int GetFieldAlignment(Type t)
        {
            if (t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte)) return 1;
            if (t == typeof(short) || t == typeof(ushort)) return 2;
            if (t == typeof(float) || t == typeof(int) || t == typeof(uint)) return 4;
            if (t == typeof(double) || t == typeof(long) || t == typeof(ulong)) return 8;
            if (t == typeof(Vector3) || t == typeof(Quaternion)) return 4;
            if (t.IsEnum) return 4;
            if (t.IsPointer) return IntPtr.Size;

            // Nullable<T>: alignment = max(1, alignment of T)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                Type inner = t.GetGenericArguments()[0];
                return Math.Max(1, GetFieldAlignment(inner));
            }

            // Structs: alignment = max alignment of all instance fields
            if (t.IsValueType)
            {
                int maxAlign = 1;
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    int fa = GetFieldAlignment(f.FieldType);
                    if (fa > maxAlign) maxAlign = fa;
                }
                return maxAlign;
            }

            return IntPtr.Size;
        }

        /// <summary>
        /// Returns the size of a type in sequential layout (including internal padding).
        /// Handles Nullable&lt;T&gt;, Vector3, and falls back to Marshal.SizeOf.
        /// </summary>
        private int GetFieldSize(Type t)
        {
            if (t == typeof(float)) return 4;
            if (t == typeof(int) || t == typeof(uint)) return 4;
            if (t == typeof(bool)) return 1;
            if (t == typeof(double)) return 8;
            if (t == typeof(long) || t == typeof(ulong)) return 8;
            if (t == typeof(short) || t == typeof(ushort)) return 2;
            if (t == typeof(byte) || t == typeof(sbyte)) return 1;
            if (t == typeof(Vector3)) return 12;
            if (t == typeof(Quaternion)) return 16;
            if (t.IsEnum) return 4;
            if (t.IsPointer) return IntPtr.Size;

            // Nullable<T> = bool hasValue + padding + T value
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                Type inner = t.GetGenericArguments()[0];
                int innerSize = GetFieldSize(inner);
                int innerAlign = GetFieldAlignment(inner);
                // hasValue is 1 byte, then pad to inner's alignment, then inner
                int valueStart = (1 + innerAlign - 1) & ~(innerAlign - 1);
                return valueStart + innerSize;
            }

            if (t.IsValueType)
            {
                // Try Marshal.SizeOf first (works for blittable types)
                try { return System.Runtime.InteropServices.Marshal.SizeOf(t); }
                catch
                {
                    // Manual computation: sum field sizes with alignment
                    int size = 0;
                    int maxAlign = 1;
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        int fa = GetFieldAlignment(f.FieldType);
                        if (fa > maxAlign) maxAlign = fa;
                        if (fa > 1)
                            size = (size + fa - 1) & ~(fa - 1);
                        size += GetFieldSize(f.FieldType);
                    }
                    // Struct total size is aligned to its max field alignment
                    if (maxAlign > 1)
                        size = (size + maxAlign - 1) & ~(maxAlign - 1);
                    return size > 0 ? size : 8;
                }
            }
            return IntPtr.Size;
        }

        public void TogglePossession()
        {
            if (IsPossessing)
                Unpossess();
            else
                TryPossess();
        }

        private void TryPossess()
        {
            playerAircraft = FindPlayerAircraft();
            Vector3 refPos = playerAircraft != null
                ? playerAircraft.transform.position
                : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);

            Unit bestUnit = null;
            UnitType bestType = UnitType.None;
            float bestDist = MAX_POSSESS_DISTANCE;

            // Search ships
            var allShips = UnityEngine.Object.FindObjectsOfType<Ship>();
            foreach (var ship in allShips)
            {
                if (!IsValidTarget(ship, refPos)) continue;
                float dist = Vector3.Distance(ship.transform.position, refPos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestUnit = ship;
                    bestType = UnitType.Ship;
                }
            }

            // Search ground vehicles
            if (groundVehicleType != null)
            {
                var allVehicles = UnityEngine.Object.FindObjectsOfType(groundVehicleType);
                foreach (var obj in allVehicles)
                {
                    var vehicle = obj as Unit;
                    if (vehicle == null || !IsValidTarget(vehicle, refPos)) continue;
                    float dist = Vector3.Distance(vehicle.transform.position, refPos);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestUnit = vehicle;
                        bestType = UnitType.GroundVehicle;
                    }
                }
            }

            if (bestUnit == null)
            {
                Plugin.Log.LogInfo("No friendly ship/vehicle found nearby");
                return;
            }

            // Possess
            PossessedUnit = bestUnit;
            PossessedType = bestType;

            if (bestType == UnitType.Ship)
                PossessShip((Ship)bestUnit);
            else if (bestType == UnitType.GroundVehicle)
                PossessVehicle(bestUnit);

            // Camera
            SetCameraFollow(bestUnit);

            IsPossessing = true;
            string unitName = bestUnit.name.Replace("(Clone)", "").Trim();
            Plugin.Log.LogInfo($"Possessed {bestType}: {unitName} (distance: {bestDist:F0}m)");
        }

        private void PossessShip(Ship ship)
        {
            PossessedShipAI = ship.GetComponent<ShipAI>();
            if (PossessedShipAI == null)
                PossessedShipAI = ship.GetComponentInChildren<ShipAI>();

            AcquireShipInputs(ship);

            if (PossessedShipAI != null)
                ShipAI_Steer_Patch.SuppressedShipAI = PossessedShipAI;
        }

        private void PossessVehicle(Unit vehicle)
        {
            // Get Rigidbody for speed display
            vehicleRB = vehicle.GetComponent<Rigidbody>();
            if (vehicleRB == null)
            {
                var rbField = typeof(Unit).GetField("<rb>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (rbField != null)
                    vehicleRB = rbField.GetValue(vehicle) as Rigidbody;
            }

            if (topSpeedOnroadField != null)
                vehicleTopSpeed = (float)topSpeedOnroadField.GetValue(vehicle);
            else
                vehicleTopSpeed = 20f;

            // Save holdPosition state and suppress AI
            if (holdPositionField != null)
            {
                vehicleHoldPositionOriginal = (bool)holdPositionField.GetValue(vehicle);
                holdPositionField.SetValue(vehicle, false);
            }

            // Set mobile=false to prevent job's Inputs() from overwriting our values
            // Inputs() has: if (!fields.mobile) return; — skips all AI input computation
            // The suspension physics (Math2) still runs, using our native memory inputs
            if (mobileField != null)
            {
                vehicleMobileOriginal = (bool)mobileField.GetValue(vehicle);
                mobileField.SetValue(vehicle, false);
            }

            // Test native memory access
            bool nativeOK = TestNativeAccess(vehicle);

            Plugin.Log.LogInfo($"Vehicle possessed: RB={vehicleRB != null}, topSpeed={vehicleTopSpeed:F1}, nativeAccess={nativeOK}, inputsOffset={nativeInputsOffset}");
        }

        private unsafe bool TestNativeAccess(Unit vehicle)
        {
            if (jobFieldsFieldInfo == null || ptrFieldInfo == null || nativeInputsOffset < 0)
                return false;

            try
            {
                object ptrAllocBoxed = jobFieldsFieldInfo.GetValue(vehicle);
                if (ptrAllocBoxed == null) return false;

                // Check IsCreated
                var isCreatedProp = ptrAllocBoxed.GetType().GetProperty("IsCreated");
                if (isCreatedProp != null && !(bool)isCreatedProp.GetValue(ptrAllocBoxed))
                {
                    Plugin.Log.LogWarning("PtrAllocation not created yet");
                    return false;
                }

                // Get the raw pointer
                object ptrObj = ptrFieldInfo.GetValue(ptrAllocBoxed);
                IntPtr rawPtr = (IntPtr)System.Reflection.Pointer.Unbox(ptrObj);

                if (rawPtr == IntPtr.Zero)
                {
                    Plugin.Log.LogWarning("Native ptr is null");
                    return false;
                }

                // Read current values
                float curThrottle = ReadFloat(rawPtr, nativeThrottleOffset);
                float curSteering = ReadFloat(rawPtr, nativeSteeringOffset);
                float curBrake = ReadFloat(rawPtr, nativeBrakeOffset);
                Plugin.Log.LogInfo($"Native read OK: throttle={curThrottle:F2}, steering={curSteering:F2}, brake={curBrake:F2}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Native access test failed: {e.Message}");
                return false;
            }
        }

        private unsafe float ReadFloat(IntPtr basePtr, int offset)
        {
            float* p = (float*)((byte*)basePtr + offset);
            return *p;
        }

        private unsafe void WriteFloat(IntPtr basePtr, int offset, float value)
        {
            float* p = (float*)((byte*)basePtr + offset);
            *p = value;
        }

        private unsafe void WriteByte(IntPtr basePtr, int offset, byte value)
        {
            byte* p = (byte*)basePtr + offset;
            *p = value;
        }

        private unsafe byte ReadByte(IntPtr basePtr, int offset)
        {
            byte* p = (byte*)basePtr + offset;
            return *p;
        }

        private unsafe IntPtr GetNativePtr(Unit vehicle)
        {
            if (jobFieldsFieldInfo == null || ptrFieldInfo == null) return IntPtr.Zero;

            try
            {
                object ptrAllocBoxed = jobFieldsFieldInfo.GetValue(vehicle);
                if (ptrAllocBoxed == null) return IntPtr.Zero;

                object ptrObj = ptrFieldInfo.GetValue(ptrAllocBoxed);
                return (IntPtr)System.Reflection.Pointer.Unbox(ptrObj);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private void AcquireShipInputs(Ship ship)
        {
            if (shipGetInputsMethod != null)
                shipInputs = shipGetInputsMethod.Invoke(ship, null);

            if (shipInputs == null)
            {
                var inputsField = typeof(Ship).GetField("inputs",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (inputsField != null)
                    shipInputs = inputsField.GetValue(ship);
            }

            if (shipInputs == null && PossessedShipAI != null)
            {
                var aiInputsField = typeof(ShipAI).GetField("inputs",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (aiInputsField != null)
                    shipInputs = aiInputsField.GetValue(PossessedShipAI);
            }

            Plugin.Log.LogInfo($"ShipInputs acquired: {shipInputs != null}");
        }

        public void SetThrottle(float value)
        {
            if (PossessedType == UnitType.Ship && shipInputs != null && shipThrottleField != null)
                shipThrottleField.SetValue(shipInputs, value);
            else if (PossessedType == UnitType.GroundVehicle)
                vehicleThrottleInput = value;
        }

        public void SetSteering(float value)
        {
            if (PossessedType == UnitType.Ship && shipInputs != null && shipSteeringField != null)
                shipSteeringField.SetValue(shipInputs, value);
            else if (PossessedType == UnitType.GroundVehicle)
                vehicleSteeringInput = value;
        }

        public void SetBrake(float value)
        {
            if (PossessedType == UnitType.GroundVehicle)
                vehicleBrakeInput = value;
        }

        /// <summary>
        /// Called from Plugin.FixedUpdate() — writes inputs to native memory every physics frame.
        /// Also forces mobile=false in native memory to guarantee job's Inputs() is a no-op.
        /// </summary>
        public unsafe void FixedTick()
        {
            if (!IsPossessing || PossessedType != UnitType.GroundVehicle) return;
            if (PossessedUnit == null || nativeInputsOffset < 0) return;

            IntPtr nativePtr = GetNativePtr(PossessedUnit);
            if (nativePtr == IntPtr.Zero) return;

            // Force mobile=false in native memory — kills AI's Inputs() completely
            if (nativeMobileOffset >= 0)
                WriteByte(nativePtr, nativeMobileOffset, 0);

            // Clamp throttle to match job limits: [-0.7, 1.0]
            float clampedThrottle = Mathf.Clamp(vehicleThrottleInput, -0.7f, 1f);
            // Match the job's auto-brake formula: brake = 1 - |throttle|
            float autoBrake = Mathf.Clamp01(1f - Mathf.Abs(clampedThrottle));

            WriteFloat(nativePtr, nativeThrottleOffset, clampedThrottle);
            WriteFloat(nativePtr, nativeSteeringOffset, vehicleSteeringInput * STEERING_SCALE);
            WriteFloat(nativePtr, nativeBrakeOffset, autoBrake);
        }

        /// <summary>
        /// Called from Harmony Postfix on GroundVehicle.UpdateJobFields() —
        /// runs right AFTER MB state is copied to native, BEFORE job is scheduled.
        /// This is the perfect timing to override values.
        /// </summary>
        public unsafe void OverrideNativeForJob(object vehicleInstance)
        {
            if (!IsPossessing || PossessedType != UnitType.GroundVehicle) return;
            if (PossessedUnit == null || (object)PossessedUnit != vehicleInstance) return;
            if (nativeInputsOffset < 0) return;

            IntPtr nativePtr = GetNativePtr(PossessedUnit);
            if (nativePtr == IntPtr.Zero) return;

            // Force mobile=false in native memory
            if (nativeMobileOffset >= 0)
                WriteByte(nativePtr, nativeMobileOffset, 0);

            // Write inputs
            float clampedThrottle = Mathf.Clamp(vehicleThrottleInput, -0.7f, 1f);
            float autoBrake = Mathf.Clamp01(1f - Mathf.Abs(clampedThrottle));

            WriteFloat(nativePtr, nativeThrottleOffset, clampedThrottle);
            WriteFloat(nativePtr, nativeSteeringOffset, vehicleSteeringInput * STEERING_SCALE);
            WriteFloat(nativePtr, nativeBrakeOffset, autoBrake);
        }

        public void Unpossess()
        {
            if (!IsPossessing) return;

            // Reset ship inputs
            if (PossessedType == UnitType.Ship)
            {
                SetThrottle(0f);
                SetSteering(0f);
            }

            // Restore vehicle state
            if (PossessedType == UnitType.GroundVehicle && PossessedUnit != null)
            {
                RestoreVehicle(PossessedUnit);
            }

            ShipAI_Steer_Patch.SuppressedShipAI = null;

            if (playerAircraft != null && playerAircraft.gameObject.activeInHierarchy)
                SetCameraFollow(playerAircraft);

            PossessedUnit = null;
            PossessedType = UnitType.None;
            PossessedShipAI = null;
            shipInputs = null;
            vehicleRB = null;
            vehicleThrottleInput = 0f;
            vehicleSteeringInput = 0f;
            vehicleBrakeInput = 0f;
            IsPossessing = false;

            Plugin.Log.LogInfo("Unpossessed");
        }

        public void ForceUnpossess(string reason)
        {
            Plugin.Log.LogInfo($"Force unpossess: {reason}");

            if (PossessedType == UnitType.GroundVehicle && PossessedUnit != null)
            {
                try { RestoreVehicle(PossessedUnit); } catch { }
            }

            ShipAI_Steer_Patch.SuppressedShipAI = null;
            PossessedUnit = null;
            PossessedType = UnitType.None;
            PossessedShipAI = null;
            shipInputs = null;
            vehicleRB = null;
            vehicleThrottleInput = 0f;
            vehicleSteeringInput = 0f;
            vehicleBrakeInput = 0f;
            IsPossessing = false;

            if (playerAircraft != null && playerAircraft.gameObject.activeInHierarchy)
                SetCameraFollow(playerAircraft);
        }

        private unsafe void RestoreVehicle(Unit vehicle)
        {
            // Zero out our inputs and restore mobile in native memory
            IntPtr nativePtr = GetNativePtr(vehicle);
            if (nativePtr != IntPtr.Zero && nativeInputsOffset >= 0)
            {
                WriteFloat(nativePtr, nativeThrottleOffset, 0f);
                WriteFloat(nativePtr, nativeSteeringOffset, 0f);
                WriteFloat(nativePtr, nativeBrakeOffset, 0f);

                // Restore mobile=true in native memory
                if (nativeMobileOffset >= 0)
                    WriteByte(nativePtr, nativeMobileOffset, (byte)(vehicleMobileOriginal ? 1 : 0));
            }

            // Restore holdPosition and mobile on MonoBehaviour
            if (holdPositionField != null)
                holdPositionField.SetValue(vehicle, vehicleHoldPositionOriginal);
            if (mobileField != null)
                mobileField.SetValue(vehicle, vehicleMobileOriginal);

            Plugin.Log.LogInfo("Restored vehicle state");
        }

        public void Tick()
        {
            if (!IsPossessing) return;
            if (PossessedUnit == null || !PossessedUnit.gameObject.activeInHierarchy)
            {
                ForceUnpossess("Unit no longer valid");
                return;
            }
        }

        private void SetCameraFollow(Unit unit)
        {
            try
            {
                if (cameraStateManager == null)
                {
                    var csmType = typeof(Unit).Assembly.GetType("CameraStateManager");
                    if (csmType != null)
                        cameraStateManager = UnityEngine.Object.FindObjectOfType(csmType);
                }

                if (cameraStateManager != null && setFollowingUnitMethod != null)
                {
                    setFollowingUnitMethod.Invoke(cameraStateManager, new object[] { unit });
                    Plugin.Log.LogInfo($"Camera following: {unit.name}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Camera switch failed: {e.Message}");
            }
        }

        private bool IsValidTarget(Unit unit, Vector3 refPos)
        {
            if (unit == null || !unit.gameObject.activeInHierarchy) return false;

            try
            {
                var disabledField = typeof(Unit).GetField("disabled",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (disabledField != null && (bool)disabledField.GetValue(unit))
                    return false;
            }
            catch { }

            if (playerAircraft != null)
            {
                try
                {
                    if (!IsSameFaction(unit, playerAircraft))
                        return false;
                }
                catch { }
            }

            return true;
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

                // HQ is NetworkBehaviorSyncvar — unwrap .Value to get FactionHQ
                object factionHqA = hqA;
                object factionHqB = hqB;

                var valueProp = hqA.GetType().GetProperty("Value",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (valueProp != null)
                {
                    factionHqA = valueProp.GetValue(hqA);
                    factionHqB = valueProp.GetValue(hqB);
                }

                if (factionHqA == null || factionHqB == null) return false;

                // Same FactionHQ instance = same faction
                if (ReferenceEquals(factionHqA, factionHqB)) return true;

                // Compare FactionHQ.faction (Faction is a ScriptableObject singleton per faction)
                var factionField = factionHqA.GetType().GetField("faction",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (factionField == null) return false;

                var facA = factionField.GetValue(factionHqA);
                var facB = factionField.GetValue(factionHqB);
                if (facA == null || facB == null) return false;

                return ReferenceEquals(facA, facB);
            }
            catch
            {
                return false;
            }
        }

        private Aircraft FindPlayerAircraft()
        {
            try
            {
                var allAircraft = UnityEngine.Object.FindObjectsOfType<Aircraft>();
                foreach (var ac in allAircraft)
                {
                    var refField = typeof(Aircraft).GetField("playerRef",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (refField != null)
                    {
                        var playerRef = refField.GetValue(ac);
                        if (playerRef != null)
                        {
                            var hasPlayerProp = playerRef.GetType().GetProperty("HasValue",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (hasPlayerProp != null && (bool)hasPlayerProp.GetValue(playerRef))
                                return ac;
                        }
                    }

                    var pilotsField = typeof(Aircraft).GetField("pilots",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pilotsField != null)
                    {
                        var pilots = pilotsField.GetValue(ac) as Array;
                        if (pilots != null)
                        {
                            foreach (var pilot in pilots)
                            {
                                if (pilot == null) continue;
                                var pcField = pilot.GetType().GetField("playerControlled",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (pcField != null && (bool)pcField.GetValue(pilot))
                                    return ac;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"FindPlayerAircraft error: {e.Message}");
            }
            return null;
        }
    }
}
