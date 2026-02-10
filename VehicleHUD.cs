using UnityEngine;

namespace VehicleControl
{
    public class VehicleHUD
    {
        private GUIStyle boxStyle, labelStyle, headerStyle, valueStyle, helpStyle;
        private bool stylesInit = false;
        private Rect windowRect = new Rect(20, 20, 300, 200);

        private void InitStyles()
        {
            if (stylesInit) return;

            boxStyle = new GUIStyle(GUI.skin.box);
            Texture2D bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0.05f, 0.08f, 0.12f, 0.85f));
            bg.Apply();
            boxStyle.normal.background = bg;

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            headerStyle.normal.textColor = new Color(0.4f, 1f, 0.4f);

            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            labelStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);

            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };
            valueStyle.normal.textColor = Color.white;

            helpStyle = new GUIStyle(GUI.skin.label) { fontSize = 10 };
            helpStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);

            stylesInit = false; // Re-init each frame for safety
        }

        public void Draw(PossessionManager pm, InputController input, TargetManager target)
        {
            if (!pm.IsPossessing) return;

            InitStyles();
            stylesInit = true;

            windowRect = GUILayout.Window(9998, windowRect, (id) =>
            {
                DrawContent(pm, input, target);
                GUI.DragWindow();
            }, "", boxStyle);
        }

        private void DrawContent(PossessionManager pm, InputController input, TargetManager target)
        {
            string typeName = pm.PossessedType == UnitType.Ship ? "SHIP" : "VEHICLE";
            string unitName = pm.PossessedUnit != null
                ? pm.PossessedUnit.name.Replace("(Clone)", "").Trim()
                : "---";

            GUILayout.Label($"{typeName} CONTROL", headerStyle);
            GUILayout.Label(unitName, valueStyle);
            GUILayout.Space(5);

            // Speed
            if (pm.PossessedUnit != null)
            {
                var rb = pm.PossessedUnit.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    float speedKmh = rb.velocity.magnitude * 3.6f;
                    float speedKnots = rb.velocity.magnitude * 1.944f;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Speed:", labelStyle, GUILayout.Width(80));
                    GUILayout.Label($"{speedKnots:F0} kts ({speedKmh:F0} km/h)", valueStyle);
                    GUILayout.EndHorizontal();
                }

                // Heading
                float heading = pm.PossessedUnit.transform.eulerAngles.y;
                GUILayout.BeginHorizontal();
                GUILayout.Label("Heading:", labelStyle, GUILayout.Width(80));
                GUILayout.Label($"{heading:F0}°", valueStyle);
                GUILayout.EndHorizontal();
            }

            // Throttle/Steering
            GUILayout.BeginHorizontal();
            GUILayout.Label("Throttle:", labelStyle, GUILayout.Width(80));
            GUILayout.Label($"{input.Throttle * 100:F0}%", valueStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Rudder:", labelStyle, GUILayout.Width(80));
            string steerDir = input.Steering > 0.05f ? "STBD" : input.Steering < -0.05f ? "PORT" : "MID";
            GUILayout.Label($"{steerDir} ({input.Steering * 100:F0}%)", valueStyle);
            GUILayout.EndHorizontal();

            // Target
            GUILayout.Space(5);
            if (target.CurrentTarget != null)
            {
                var targetStyle = new GUIStyle(valueStyle);
                targetStyle.normal.textColor = new Color(1f, 0.4f, 0.4f);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Target:", labelStyle, GUILayout.Width(80));
                GUILayout.Label(target.TargetName, targetStyle);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Range:", labelStyle, GUILayout.Width(80));
                string rangeStr = target.TargetDistance > 1000f
                    ? $"{target.TargetDistance / 1000f:F1} km"
                    : $"{target.TargetDistance:F0} m";
                GUILayout.Label(rangeStr, targetStyle);
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("No target (Tab to select)", labelStyle);
            }

            // Controls help
            GUILayout.Space(8);
            GUILayout.Label("F8=Exit  WASD=Move  Space=Stop", helpStyle);
            GUILayout.Label("Tab=Target  T=Clear target", helpStyle);
        }
    }
}
