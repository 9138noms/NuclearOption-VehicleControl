using System;
using System.Reflection;
using UnityEngine;

namespace VehicleControl
{
    public class InputController
    {
        private float currentThrottle = 0f;
        private float currentSteering = 0f;
        private float currentBrake = 0f;
        private bool cruiseControl = false;

        private const float THROTTLE_SPEED = 1.5f;
        private const float STEER_SPEED = 3f;
        private const float DECAY_SPEED = 2f;

        public float Throttle => currentThrottle;
        public float Steering => currentSteering;
        public bool CruiseControl => cruiseControl;

        public void Update(PossessionManager pm)
        {
            if (!pm.IsPossessing) return;

            bool isShip = pm.PossessedType == UnitType.Ship;

            // Throttle
            if (isShip)
            {
                // Ship: W/S adjusts throttle lever, stays when released
                if (Input.GetKey(KeyCode.W))
                    currentThrottle = Mathf.MoveTowards(currentThrottle, 1f, THROTTLE_SPEED * Time.deltaTime);
                else if (Input.GetKey(KeyCode.S))
                    currentThrottle = Mathf.MoveTowards(currentThrottle, -1f, THROTTLE_SPEED * Time.deltaTime);
                // No decay — throttle holds position
            }
            else
            {
                // Vehicle: W/S with decay on release (unless cruise control)
                if (Input.GetKeyDown(KeyCode.C))
                {
                    cruiseControl = !cruiseControl;
                    Plugin.Log.LogInfo($"Cruise control: {(cruiseControl ? "ON" : "OFF")}");
                }

                float throttleInput = 0f;
                if (Input.GetKey(KeyCode.W)) throttleInput = 1f;
                else if (Input.GetKey(KeyCode.S)) { throttleInput = -1f; cruiseControl = false; }

                if (throttleInput != 0f)
                    currentThrottle = Mathf.MoveTowards(currentThrottle, throttleInput, THROTTLE_SPEED * Time.deltaTime);
                else if (!cruiseControl)
                    currentThrottle = Mathf.MoveTowards(currentThrottle, 0f, DECAY_SPEED * Time.deltaTime);
            }

            // Steering: A=left, D=right for vehicles; reversed for ships
            float steerInput = 0f;
            if (Input.GetKey(KeyCode.A)) steerInput = -1f;
            else if (Input.GetKey(KeyCode.D)) steerInput = 1f;
            if (isShip) steerInput = -steerInput;

            if (steerInput != 0f)
                currentSteering = Mathf.MoveTowards(currentSteering, steerInput, STEER_SPEED * Time.deltaTime);
            else
                currentSteering = Mathf.MoveTowards(currentSteering, 0f, STEER_SPEED * Time.deltaTime);

            // Brake: Space
            currentBrake = Input.GetKey(KeyCode.Space) ? 1f : 0f;
            if (isShip && Input.GetKey(KeyCode.Space))
            {
                // Ship: Space = all stop (throttle to 0)
                currentThrottle = Mathf.MoveTowards(currentThrottle, 0f, DECAY_SPEED * 3f * Time.deltaTime);
            }
            else if (!isShip && Input.GetKey(KeyCode.Space))
            {
                currentThrottle = Mathf.MoveTowards(currentThrottle, 0f, DECAY_SPEED * 3f * Time.deltaTime);
            }

            // Apply to unit
            pm.SetThrottle(currentThrottle);
            pm.SetSteering(currentSteering);
            pm.SetBrake(currentBrake);
        }

        public void Reset()
        {
            currentThrottle = 0f;
            currentSteering = 0f;
            currentBrake = 0f;
            cruiseControl = false;
        }
    }
}
