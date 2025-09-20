#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using AvatarSmartBackup;

namespace AvatarSmartBackup.Shared
{
    internal sealed class StatusBanner
    {
        readonly ModernInfoBanner _banner;

        public StatusBanner(ModernInfoBanner banner)
        {
            _banner = banner;
        }

        public void Draw(MessageType type, string content, string? title = null, Texture? icon = null, float spacing = 2f)
        {
            if (string.IsNullOrEmpty(content))
                return;

            if (icon != null)
            {
                using (_banner.Scope(type, content, title, icon))
                {
                    if (spacing > 0f)
                        GUILayout.Space(spacing);
                }
            }
            else
            {
                using (_banner.Scope(type, content, title))
                {
                    if (spacing > 0f)
                        GUILayout.Space(spacing);
                }
            }
        }
    }
}
#endif
