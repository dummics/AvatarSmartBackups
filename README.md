# Avatar Smart Backups - Version System

An intelligent backup system for Unity projects with automatic, Git-like versioning integrated into the Editor.

## 🚀 Features

- Automatic Backups: Create snapshots automatically while you work
- Version Timeline: Visual interface to browse saved versions
- Smart Restore: Preview and selectively restore specific files
- Efficient Storage: Delta compression to reduce disk usage
- SQLite Database: Local metadata database for fast queries

## 📋 Requirements

- Unity 2022.3 or newer
- Windows Editor (Editor-only features)
- System.Data.SQLite.dll (included)

## ⚡ Quick Start

### Basic setup
1. Open Tools → Avatar Smart Backup
2. In Settings, enable `Enable Version System`
3. Configure automatic backup frequency

### Run quick tests
```
Tools → Avatar Smart Backup → Quick Tests
├── ⚡ Quick SQLite Test - verifies the database
└── 🚀 Test Version System Ready - full readiness check
```

### Main interfaces
- 🕒 Version Timeline: Browse saved versions
- 📊 Version Stats: Storage and performance statistics
- 💾 Manual Commit: Create a manual snapshot with a comment

## 🛠️ Architecture

- VersionManager: Orchestrates commits and restores
- VersionDatabase: SQLite access layer
- DeltaStorageManager: Compression and efficient storage
- VersionTimelineWindow: Timeline UI for navigation

## 📂 File structure

```
Assets/AvatarSmartBackups/
├── Editor/
│   ├── Backup/           # Existing backup system
│   ├── Versioning/       # New versioning system
│   ├── Windows/          # UI windows
+│   └── Utils/            # Tests and utilities
└── Plugins/SQLite/       # Native database library
```

## 🧪 Testing

Included tests verify:
- SQLite connection
- Database and table creation
- Automatic and manual commits
- Timeline navigation

## 📝 Notes

- The system is Editor-only and does not affect builds
- Backups are saved in a separate folder from the project contents
- Local SQLite database for optimal performance
- Delta compression is used to minimize disk usage
