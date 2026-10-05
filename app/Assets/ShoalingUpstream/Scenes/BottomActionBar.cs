using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShoalingUpstream.Simulation
{
    public sealed class BottomAction
    {
        public readonly string Label;
        public readonly Action Invoke;
        public readonly bool Enabled, Selected;
        public BottomAction(string label, Action invoke, bool enabled = true, bool selected = false)
        { Label = label; Invoke = invoke; Enabled = enabled; Selected = selected; }
    }

    public static class BottomActionBar
    {
        public static void Draw(IReadOnlyList<BottomAction> actions)
        {
            if (actions == null || actions.Count == 0) return;
            Rect safe = Screen.safeArea;
            float scale = Mathf.Clamp(safe.width / 680f, 0.8f, 1.4f);
            float gap = 10f * scale, margin = 12f * scale, buttonHeight = 60f * scale;
            float available = Mathf.Min(safe.width - margin * 2f, 980f * scale);
            int columns = Mathf.Max(1, Mathf.Min(actions.Count, Mathf.FloorToInt((available + gap) / (160f * scale + gap))));
            int rows = Mathf.CeilToInt((float)actions.Count / columns);
            float buttonWidth = (available - gap * (columns - 1)) / columns;
            float top = Screen.height - safe.y - margin - rows * buttonHeight - (rows - 1) * gap;
            float left = safe.x + (safe.width - available) * 0.5f;
            var style = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(22f * scale), wordWrap = true };
            bool enabled = GUI.enabled;
            Color color = GUI.backgroundColor;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                GUI.enabled = enabled && action.Enabled;
                GUI.backgroundColor = action.Selected ? new Color(0.4f, 0.8f, 0.55f) : color;
                Rect rect = new(left + (i % columns) * (buttonWidth + gap), top + (i / columns) * (buttonHeight + gap), buttonWidth, buttonHeight);
                if (GUI.Button(rect, action.Selected ? "✓ " + action.Label : action.Label, style)) action.Invoke?.Invoke();
            }
            GUI.enabled = enabled;
            GUI.backgroundColor = color;
        }

        public static void Message(string text, bool centre = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            Rect safe = Screen.safeArea;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 28, wordWrap = true, alignment = TextAnchor.MiddleCenter };
            if (centre)
            {
                style.fontStyle = FontStyle.Bold;
                float width = Screen.width / 3f;
                float measured = style.CalcSize(new GUIContent(text)).x;
                style.fontSize = Mathf.Max(1, Mathf.RoundToInt(style.fontSize * width / Mathf.Max(1f, measured)));
                float height = style.CalcSize(new GUIContent(text)).y + 8f;
                GUI.Label(new Rect((Screen.width - width) * .5f, (Screen.height - height) * .5f, width, height), text, style);
            }
            else GUI.Label(new Rect(safe.x + 12f, Screen.height - safe.y - 110f, safe.width - 24f, 80f), text, style);
        }
    }
}
