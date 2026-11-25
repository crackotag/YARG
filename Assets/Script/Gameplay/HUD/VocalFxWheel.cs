using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Audio.BASS;
using YARG.Input;

namespace YARG.Gameplay.HUD
{
    internal sealed class VocalFxWheel
    {
        private static readonly VocalFxPreset?[] Choices =
        {
            null, VocalFxPreset.Standard, VocalFxPreset.Reverb, VocalFxPreset.Delay,
            VocalFxPreset.Distortion, VocalFxPreset.AutoTune, VocalFxPreset.Talkbox
        };
        private static readonly string[] Titles = { "Auto", "Standard", "Reverb", "Delay", "Distortion", "Auto-Tune", "Talkbox" };
        private static readonly string[] Horizontal = { "", "", "Room size", "Time", "Drive", "Strength", "Carrier pitch" };
        private static readonly string[] Vertical = { "", "", "Wet level", "Feedback", "Mix", "Retune speed", "Mix" };
        private readonly Vector2[] _values = new Vector2[Choices.Length];
        private int _selected;
        private float _hideAt;
        private Texture2D _face;
        private GUIStyle _label;

        public VocalFxPreset? Selection => Choices[_selected];
        public Vector2 Values => _values[_selected];

        public VocalFxWheel()
        {
            for (int i = 0; i < _values.Length; i++)
                _values[i] = new Vector2(0.5f, 0.5f);
        }

        public void Cycle() => _selected = (_selected + 1) % Choices.Length;

        public void Read(PlayerDeviceInfo devices)
        {
            foreach (var controller in devices.Controllers)
            {
                if (controller is not Gamepad pad || !pad.added || !pad.enabled)
                    continue;
                Vector2 left = pad.leftStick.ReadValue();
                Vector2 right = pad.rightStick.ReadValue();
                bool select = left.sqrMagnitude >= 0.55f * 0.55f;
                if (!select && right.sqrMagnitude <= 0.22f * 0.22f)
                    continue;

                _hideAt = Time.unscaledTime + 1.25f;
                if (select)
                {
                    float turns = Mathf.Atan2(left.x, left.y) / (Mathf.PI * 2f);
                    _selected = (Mathf.RoundToInt(turns * Choices.Length) + Choices.Length) % Choices.Length;
                }

                if (_selected >= 2)
                {
                    Vector2 values = Values;
                    float step = Mathf.Min(Time.unscaledDeltaTime, 0.05f) * 0.7f;
                    values.x = Mathf.Clamp01(values.x + Axis(right.x) * step);
                    values.y = Mathf.Clamp01(values.y + Axis(right.y) * step);
                    _values[_selected] = values;
                }
                return;
            }
        }

        private static float Axis(float value) =>
            Mathf.Sign(value) * Mathf.InverseLerp(0.18f, 1f, Mathf.Abs(value));

        private Texture2D MakeFace()
        {
            const int size = 192;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var point = new Vector2(x + 0.5f - size / 2f, y + 0.5f - size / 2f);
                float radius = point.magnitude / (size / 2f);
                if (radius > 1f) continue;
                float sector = Mathf.Atan2(point.x, point.y) * Choices.Length / (2f * Mathf.PI);
                bool border = radius > 0.985f || Mathf.Abs(radius - 0.46f) < 0.009f ||
                    (radius > 0.46f && Mathf.Abs(sector - Mathf.Round(sector)) > 0.49f);
                pixels[y * size + x] = border ? new Color32(92, 177, 192, 235) : new Color32(10, 19, 29, 230);
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        public void Draw(string singer, int index, int slots)
        {
            if (Time.unscaledTime >= _hideAt || Event.current.type != EventType.Repaint)
                return;
            if (_face == null) _face = MakeFace();
            _label ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            int columns = Mathf.Min(4, Mathf.Max(1, slots));
            int rows = Mathf.CeilToInt(slots / (float) columns);
            float cell = Screen.width / (float) columns;
            float diameter = Mathf.Min(Screen.height * 0.38f, cell * 0.94f, Screen.height * 0.85f / rows);
            float cx = cell * (index % columns + 0.5f);
            float cy = Screen.height - diameter * (0.56f + index / columns);
            var saved = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(cx - diameter / 2, cy - diameter / 2, diameter, diameter), _face);
            _label.fontSize = Mathf.Max(10, Mathf.RoundToInt(diameter * 0.045f));
            for (int i = 0; i < Choices.Length; i++)
            {
                float angle = 2f * Mathf.PI * i / Choices.Length;
                float x = cx + diameter * 0.35f * Mathf.Sin(angle);
                float y = cy - diameter * 0.35f * Mathf.Cos(angle);
                _label.normal.textColor = i == _selected ? Color.cyan : Color.white;
                _label.fontStyle = i == _selected ? FontStyle.Bold : FontStyle.Normal;
                GUI.Label(new Rect(x - diameter * 0.18f, y - diameter * 0.05f, diameter * 0.36f, diameter * 0.1f), Titles[i], _label);
            }
            _label.normal.textColor = Color.white;
            _label.fontStyle = FontStyle.Normal;
            string info = singer;
            if (_selected >= 2)
                info += $"\n↔ {Horizontal[_selected]} {Values.x:P0}\n↕ {Vertical[_selected]} {Values.y:P0}";
            GUI.Label(new Rect(cx - diameter * 0.25f, cy - diameter * 0.19f, diameter * 0.5f, diameter * 0.38f), info, _label);
            GUI.color = saved;
        }

        public void Dispose()
        {
            if (_face != null) Object.Destroy(_face);
        }
    }
}
