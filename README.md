# Avatar Smart Backups - Version System

Un sistema di backup intelligente per progetti Unity con versioning automatico stile Git integrato nell'Editor.

## 🚀 Caratteristiche

- **Backup Automatici**: Creazione automatica di snapshots durante il lavoro
- **Version Timeline**: Interfaccia visuale per navigare tra le versioni salvate 
- **Restore Intelligente**: Preview e ripristino selettivo di file specifici
- **Storage Efficiente**: Compressione delta per ridurre lo spazio utilizzato
- **Database SQLite**: Metadata organizzati in database locale per ricerche veloci

## 📋 Requisiti

- Unity 2022.3 o superiore
- Sistema Windows (Editor only)
- System.Data.SQLite.dll (inclusa)

## ⚡ Utilizzo Rapido

### Configurazione Base
1. Apri **Tools → Avatar Smart Backup**
2. Nelle Settings, attiva `Enable Version System`
3. Configura la frequenza di backup automatico

### Test del Sistema
```
Tools → Avatar Smart Backup → Quick Tests
├── ⚡ Quick SQLite Test - Verifica database
└── 🚀 Test Version System Ready - Test completo
```

### Interfacce Principali
- **🕒 Version Timeline**: Naviga tra le versioni salvate
- **📊 Version Stats**: Statistiche storage e performance
- **💾 Manual Commit**: Crea snapshot manuale con commento

## 🛠️ Architettura

- **VersionManager**: Orchestrazione commit/restore
- **VersionDatabase**: Layer di accesso SQLite 
- **DeltaStorageManager**: Compressione e storage efficiente
- **VersionTimelineWindow**: UI timeline per navigazione

## 📂 Struttura File

```
Assets/AvatarSmartBackups/
├── Editor/
│   ├── Backup/           # Sistema backup esistente
│   ├── Versioning/       # Sistema versioning nuovo
│   ├── Windows/          # Interfacce UI
│   └── Utils/            # Test e utility
└── Plugins/SQLite/       # Database nativo
```

## 🧪 Testing

Il sistema include test per verificare funzionalità:
- Connessione SQLite
- Creazione database e tabelle
- Commit automatici e manuali
- Timeline navigation

## 📝 Note

- Il sistema è **Editor-only** e non influisce sui build
- I backup vengono salvati in una cartella separata dal progetto
- SQLite database locale per performance ottimali
- Delta compression per minimizzare spazio disco
