# Avatar Smart Backup - Version System 2.0

## 🎯 Panoramica

Il nuovo **Version System 2.0** porta un sistema di versionamento intelligente simile a Git direttamente in Unity Editor. Ora puoi:

- ✅ **Navigare nella timeline** dei backup con interfaccia Git-like
- ✅ **Saltare indietro nel tempo** di 20+ versioni 
- ✅ **Ispezionare i file** di versioni specifiche
- ✅ **Ripristino selettivo** di singoli file o directory
- ✅ **Compressione delta** per efficienza di storage
- ✅ **Database SQLite** per metadati e performance
- ✅ **Backup automatici** con cleanup intelligente

## 🚀 Come Iniziare

### 1. Attivazione Sistema
Vai in **Avatar Smart Backup Settings** e attiva:
```
☑️ Enable Version System
```

### 2. Configurazione
Personalizza le impostazioni:
- **Max Version History**: 50 (numero massimo di commit)
- **Version Retention Days**: 30 (giorni di conservazione)
- **Max Version Storage MB**: 2048 (limite storage)
- **Auto Create Versions**: true (backup automatici)

### 3. Accesso Timeline
Clicca il pulsante **🕒 Version Timeline** nella finestra principale o usa:
```
Tools → Avatar Smart Backup → Version System → Open Timeline
```

## 🎮 Interfaccia Timeline

### Pannello Timeline
- **Lista Commit**: Cronologia dei backup con hash, timestamp, e messaggi
- **File Browser**: Esplora i file di ogni commit specifico
- **Anteprima Dettagli**: Informazioni su dimensioni, modifiche, e statistiche

### Comandi Disponibili
- **🔍 Preview in Temp**: Apri i file selezionati in cartella temporanea
- **↩️ Restore to Project**: Ripristina i file selezionati nel progetto
- **🗂️ Select All/None**: Selezione rapida file
- **📊 Show Commit Details**: Statistiche dettagliate del commit

## 🛠️ Menu Tools

### Comandi Rapidi
```
Tools → Avatar Smart Backup → Version System →
├── Open Timeline          (Interfaccia principale)
├── Create Manual Commit   (Backup manuale con messaggio)
├── Show Statistics        (Statistiche sistema)
├── Test Auto Commit       (Test funzionalità automatiche)
└── Open Storage Folder    (Cartella di storage)
```

## 🏗️ Architettura Tecnica

### Core Components
1. **VersionManager.cs** - Orchestrazione principale
2. **VersionDatabase.cs** - Database SQLite per metadati
3. **DeltaStorageManager.cs** - Compressione e storage
4. **VersionTimelineWindow.cs** - Interfaccia timeline

### Database Schema
```sql
commits (id, hash, timestamp, message, file_count, total_size, parent_id)
files (id, commit_id, path, hash, size, action, delta_path)
deltas (id, base_hash, target_hash, delta_data, compression_type)
settings (key, value)
```

### Storage Layout
```
! BACKUP SCRIPT Backups/
├── Current/              (Backup tradizionali)
└── Versions/            (Version System 2.0)
    ├── index.db         (Database SQLite)
    ├── commits/         (Metadati commit)
    ├── deltas/          (Delta compressi)
    └── temp/            (File temporanei)
```

## 🎨 Workflow Artist-Friendly

### Scenario Tipico
1. **Lavori normalmente** - Il sistema traccia automaticamente i cambiamenti
2. **Backup Automatico** - Ogni modifica significativa crea un commit
3. **Ispezione Timeline** - Naviga visualmente tra le versioni
4. **Ripristino Selettivo** - Ripristina solo i file che servono
5. **Preview Sicuro** - Anteprima in cartella temporanea prima del ripristino

### Best Practices
- ✅ Usa **messaggi commit** descrittivi per backup manuali
- ✅ **Controlla le statistiche** periodicamente per monitorare lo storage
- ✅ **Testa sempre** in preview prima di ripristinare al progetto
- ✅ **Configura limiti** appropriati per il tuo workflow

## ⚡ Performance & Ottimizzazioni

### Compressione Delta
- **Binary Diff Algorithm** per file simili
- **GZip Compression** per riduzione dimensioni
- **Hash-based Deduplication** per file identici
- **Incremental Storage** solo per le differenze

### Gestione Memoria
- **Async Operations** per operazioni pesanti
- **Streaming I/O** per file grandi
- **Connection Pooling** per database
- **Background Cleanup** per manutenzione

## 🔧 Integrazione & Estensioni

### Collector System
Il sistema usa i collector esistenti:
- **ScenesCollector** - Scene Unity
- **MaterialsCollector** - Materials e Textures  
- **AnimClipsCollector** - Animation Clips
- **ControllersCollector** - Animator Controllers
- **VRCAssetsCollector** - Asset VRChat (se abilitati)
- **DllCollector** - DLL e script (se abilitati)

### Settings Integration
Tutte le impostazioni sono integrate nel sistema esistente:
```csharp
public bool enableVersionSystem = true;
public int maxVersionHistory = 50;
public int versionRetentionDays = 30;
public long maxVersionStorageMB = 2048;
public bool autoCreateVersions = true;
public string versionCommitTemplate = "Auto: {reason} ({count} files) [{time}]";
```

## 🔍 Debugging & Troubleshooting

### Log Messages
Il sistema logga tutte le operazioni:
- `Log.Info()` - Operazioni principali
- `Log.Debug()` - Dettagli tecnici
- `Log.Warn()` - Situazioni anomale
- `Log.Error()` - Errori critici

### Common Issues
1. **Database Lock** - Chiudi altre istanze dell'editor
2. **Storage Full** - Aumenta limite o pulisci commit vecchi
3. **Corruption** - Il database ha recovery automatico
4. **Performance** - Regola i limiti di retention

## 🎉 Vantaggi vs Sistema Precedente

### Before (Backup Classico)
- ❌ Solo snapshot completi
- ❌ Spreco di spazio disco
- ❌ Difficile navigazione
- ❌ Ripristino tutto-o-niente

### After (Version System 2.0)
- ✅ **Delta compression** per efficienza
- ✅ **Timeline navigabile** stile Git
- ✅ **Ripristino selettivo** per file specifici
- ✅ **Interfaccia artist-friendly** con preview
- ✅ **Scalabilità** per progetti grandi
- ✅ **Metadata ricchi** per ogni commit

---

## 📝 Note Sviluppatore

Questo sistema è sviluppato nel branch `versioning-system-dev` per sicurezza. Una volta testato e validato, verrà integrato nel branch principale.

**Prossimi Step**:
1. ✅ Integrazione UI completata
2. 🔄 Testing e validazione workflow
3. 📚 Documentazione utente finale
4. 🚀 Merge in main branch

**Autore**: GitHub Copilot  
**Data**: 2024  
**Version**: 2.0.0-dev
