using System;
using System.IO;
using UnityEngine;

namespace AvatarSmartBackup.Editor.Utils
{
    /// <summary>
    /// Utility per leggere informazioni dal package.json
    /// </summary>
    public static class PackageInfo
    {
        [Serializable]
        private class PackageData
        {
            public string name = string.Empty;
            public string displayName = string.Empty;
            public string version = string.Empty;
            public string description = string.Empty;
            public string author = string.Empty;
        }
        
        private static PackageData? _cachedData;
        private static bool _triedToLoad = false;

        /// <summary>
        /// Ottiene la versione del package dal package.json
        /// </summary>
        public static string GetVersion()
        {
            LoadPackageData();
            return _cachedData?.version ?? "Unknown";
        }

        /// <summary>
        /// Ottiene il nome display del package
        /// </summary>
        public static string GetDisplayName()
        {
            LoadPackageData();
            return _cachedData?.displayName ?? "Avatar Smart Backup";
        }

        /// <summary>
        /// Ottiene la descrizione del package
        /// </summary>
        public static string GetDescription()
        {
            LoadPackageData();
            return _cachedData?.description ?? "";
        }

        /// <summary>
        /// Ottiene l'autore del package
        /// </summary>
        public static string GetAuthor()
        {
            LoadPackageData();
            return _cachedData?.author ?? "Dummics";
        }

        /// <summary>
        /// Ottiene il nome del package (identificatore)
        /// </summary>
        public static string GetPackageName()
        {
            LoadPackageData();
            return _cachedData?.name ?? "dum.incb.system";
        }

        private static void LoadPackageData()
        {
            if (_triedToLoad) return;
            _triedToLoad = true;

            try
            {
                // Cerca il package.json nella directory del package
                var scriptPath = GetScriptDirectory();
                var packageJsonPath = Path.Combine(scriptPath, "..", "..", "package.json");
                packageJsonPath = Path.GetFullPath(packageJsonPath);

                if (File.Exists(packageJsonPath))
                {
                    var json = File.ReadAllText(packageJsonPath);
                    _cachedData = JsonUtility.FromJson<PackageData>(json);
                }
                else
                {
                    Debug.LogWarning($"[PackageInfo] package.json non trovato in: {packageJsonPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PackageInfo] Errore nel leggere package.json: {ex.Message}");
            }
        }

        private static string GetScriptDirectory()
        {
            // Ottiene la directory di questo script
            var script = UnityEditor.MonoScript.FromScriptableObject(ScriptableObject.CreateInstance<PackageInfoHelper>());
            var scriptPath = UnityEditor.AssetDatabase.GetAssetPath(script);
            return Path.GetDirectoryName(scriptPath);
        }
    }

    // Helper class per ottenere il percorso dello script
    internal class PackageInfoHelper : ScriptableObject { }
}