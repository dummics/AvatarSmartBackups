#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup;
using AvatarSmartBackup.Backup;

namespace AvatarSmartBackups.Tests.Editor
{
    public class BackupWindowSnapshotTests
    {
        static void InvokePrivate(object instance, string methodName)
        {
            var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(instance.GetType().FullName, methodName);
            method.Invoke(instance, null);
        }

        static BackupWindowContext GetContext(EditorWindow window)
        {
            var field = typeof(AvatarSmartBackupWindow).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(typeof(AvatarSmartBackupWindow).FullName, "_context");
            return (BackupWindowContext)field.GetValue(window);
        }

        static void Render(EditorWindow window)
        {
            var onGui = typeof(AvatarSmartBackupWindow).GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.NonPublic);
            if (onGui == null)
                throw new MissingMethodException(typeof(AvatarSmartBackupWindow).FullName, "OnGUI");

            foreach (var type in new[] { EventType.Layout, EventType.Repaint })
            {
                var previous = Event.current;
                try
                {
                    Event.current = new Event { type = type };
                    onGui.Invoke(window, null);
                }
                finally
                {
                    Event.current = previous;
                }
            }
        }

        [Test]
        public void EasyModeLayoutSnapshot()
        {
            var window = ScriptableObject.CreateInstance<AvatarSmartBackupWindow>();
            InvokePrivate(window, "OnEnable");
            try
            {
                var context = GetContext(window);
                context.LayoutRecordingEnabled = true;
                context.ClearLayoutMarkers();
                context.Settings.easyMode = true;
                context.Settings.AdvancedMode = false;
                Render(window);

                var markers = context.LayoutMarkers.ToArray();
                CollectionAssert.IsSupersetOf(markers, new[] { "ModeSelector", "SchedulerSection", "PrimaryActions", "VersionsOverview", "EasyFooter" });
                Assert.IsFalse(markers.Contains("AdvancedOverview"), "Easy mode should not render advanced overview.");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void AdvancedModeLayoutSnapshot()
        {
            var window = ScriptableObject.CreateInstance<AvatarSmartBackupWindow>();
            InvokePrivate(window, "OnEnable");
            try
            {
                var context = GetContext(window);
                context.LayoutRecordingEnabled = true;
                context.ClearLayoutMarkers();
                context.Settings.easyMode = false;
                context.Settings.AdvancedMode = true;
                Render(window);

                var markers = context.LayoutMarkers.ToArray();
                CollectionAssert.IsSupersetOf(markers, new[]
                {
                    "ModeSelector", "SchedulerSection", "PrimaryActions", "VersionsOverview", "AdvancedOverview", "AdvancedSettings", "VersioningPolicy", "PerformanceSection", "ScopeSection", "DebugToolsSection"
                });
            }
            finally
            {
                window.Close();
            }
        }
    }
}
#endif
