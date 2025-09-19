# Avatar Smart Backups – Localization (Editor)

This project includes a tiny, reusable localization helper for Editor UI.

## API

- Language enum: `AvatarSmartBackup.Localization.Language`
- Translate: `L.T(key, fallback, params)`
  - Looks up the current language in registered sources.
  - Falls back to `fallback` (or `key`) and supports `string.Format`.
- GUIContent helpers: `L.C(key, fallback)` and `L.C(key,fallback, tooltipKey, tooltipFallback)`
- Change language: Tools → Avatar Smart Backups → Language → English/Italian

## Add/extend translations

1) Add a source in code (e.g., dictionary):

```csharp
var en = new Dictionary<string,string>{{"ui.ok","OK"}};
var it = new Dictionary<string,string>{{"ui.ok","OK"}};
AvatarSmartBackup.Localization.L.AddSource(new DictionaryLocalizationSource(en,it));
```

2) Use in UI:

```csharp
GUILayout.Button(L.T("ui.ok", "OK"));
EditorGUILayout.HelpBox(L.T("help.autobackup", "Backups run in background."), MessageType.Info);
```

## Notes
- Language is stored in EditorPrefs and repaint is triggered on switch.
- You can plug other sources (ScriptableObject/JSON) by implementing `ILocalizationSource`.
- Keep English `fallback` meaningful; it ensures safe behavior if keys are missing.
