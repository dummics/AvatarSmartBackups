#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AvatarSmartBackup
{
    internal sealed class ModernInfoBanner
    {
        readonly GUIStyle _container;
        readonly GUIStyle _titleStyle;
        readonly GUIStyle _bodyStyle;

        public ModernInfoBanner()
        {
            _container = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
            };
            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = EditorStyles.label.fontSize + 1,
            };
            _bodyStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
        }

        public Scope Scope(MessageType type, string content, string? title = null)
        {
            return Scope(type, content, title, null);
        }

        public Scope Scope(MessageType type, string content, string? title, Texture icon)
        {
            return Scope(type, content, title, icon);
        }

        Scope Scope(MessageType type, string content, string? title, Texture? icon)
        {
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = GetBackground(type);
            EditorGUILayout.BeginVertical(_container);
            GUI.backgroundColor = previous;

            if (!string.IsNullOrEmpty(title))
            {
                EditorGUILayout.LabelField(title, _titleStyle);
            }

            if (!string.IsNullOrEmpty(content))
            {
                EditorGUILayout.LabelField(content, _bodyStyle, GUILayout.ExpandWidth(true));
            }

            return new Scope(EditorGUILayout.EndVertical);
        }

        Color GetBackground(MessageType type)
        {
            return type switch
            {
                MessageType.Error => new Color(0.45f, 0.1f, 0.1f, 0.25f),
                MessageType.Warning => new Color(0.6f, 0.4f, 0.1f, 0.25f),
                MessageType.Info => new Color(0.15f, 0.3f, 0.6f, 0.18f),
                _ => new Color(0.2f, 0.2f, 0.2f, 0.12f)
            };
        }

        internal readonly struct Scope : IDisposable
        {
            readonly Action _onDispose;

            public Scope(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                _onDispose?.Invoke();
            }
        }
    }
}
#endif
