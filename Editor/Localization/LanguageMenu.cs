#if UNITY_EDITOR
using UnityEditor;

namespace AvatarSmartBackup.Localization
{
    public static class LanguageMenu
    {
        const string Root = "Tools/Avatar Smart Backups/Language/";

        [MenuItem(Root + "English", priority = 10)]
        public static void SetEnglish()
        {
            L.Current = Language.English;
        }

        [MenuItem(Root + "English", validate = true)]
        public static bool ValidateEnglish()
        {
            Menu.SetChecked(Root + "English", L.Current == Language.English);
            return true;
        }

        [MenuItem(Root + "Italian", priority = 11)]
        public static void SetItalian()
        {
            L.Current = Language.Italian;
        }

        [MenuItem(Root + "Italian", validate = true)]
        public static bool ValidateItalian()
        {
            Menu.SetChecked(Root + "Italian", L.Current == Language.Italian);
            return true;
        }
    }
}
#endif
