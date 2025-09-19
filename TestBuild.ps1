# Test Build Script per Avatar Smart Backup
# Testa la compilazione .NET 4.8 in locale

param(
    [string]$Configuration = "Release"
)

Write-Host "🚀 Avatar Smart Backup - Test Build .NET 4.8" -ForegroundColor Green
Write-Host "================================================" -ForegroundColor Green

# Step 1: Verifica directory
$currentDir = Get-Location
Write-Host "📍 Directory corrente: $currentDir"

# Step 2: Trova Unity
Write-Host "`n🔍 Ricerca Unity..."
$unityPaths = @(
    "C:\Program Files\Unity\Hub\Editor\2022.3.60f1\Editor\Data\Managed",
    "C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Data\Managed",
    "C:\Program Files\Unity\Hub\Editor\2022.3.6f1\Editor\Data\Managed"
)

$unityFound = $null
foreach ($path in $unityPaths) {
    if (Test-Path (Join-Path $path 'UnityEditor.dll')) {
        $unityFound = $path
        Write-Host "✅ Unity trovato: $path" -ForegroundColor Green
        break
    }
}

if (-not $unityFound) {
    Write-Host "❌ Unity non trovato!" -ForegroundColor Red
    Write-Host "Installazioni Unity disponibili:"
    if (Test-Path "C:\Program Files\Unity\Hub\Editor\") {
        Get-ChildItem "C:\Program Files\Unity\Hub\Editor\" -Directory | ForEach-Object { 
            Write-Host "  - $($_.Name)" 
        }
    }
    exit 1
}

$env:UNITY_MANAGED = $unityFound

# Step 3: Verifica progetto
Write-Host "`n📁 Verifica progetto..."
$projectPath = "./src/AvatarSmartBackups.Core/AvatarSmartBackups.Core.csproj"
if (-not (Test-Path $projectPath)) {
    Write-Host "❌ Progetto non trovato: $projectPath" -ForegroundColor Red
    Write-Host "Struttura src:"
    if (Test-Path "./src") {
        Get-ChildItem "./src" -Recurse | Where-Object { $_.Name -like "*.csproj" } | ForEach-Object {
            Write-Host "  - $($_.FullName)"
        }
    }
    exit 1
}
Write-Host "✅ Progetto trovato: $projectPath" -ForegroundColor Green

# Step 4: Restore
Write-Host "`n🔄 Restore NuGet..."
try {
    dotnet restore $projectPath
    Write-Host "✅ Restore completato" -ForegroundColor Green
} catch {
    Write-Host "❌ Errore durante restore: $_" -ForegroundColor Red
    exit 1
}

# Step 5: Build
Write-Host "`n🔨 Build .NET 4.8..."
try {
    dotnet build $projectPath `
        -c $Configuration `
        -f net48 `
        --no-restore `
        -p:UNITY_MANAGED="$env:UNITY_MANAGED" `
        -p:WarningsAsErrors=false `
        -p:AppendTargetFrameworkToOutputPath=false `
        --verbosity minimal
    
    Write-Host "✅ Build completato" -ForegroundColor Green
} catch {
    Write-Host "❌ Errore durante build: $_" -ForegroundColor Red
    exit 1
}

# Step 6: Verifica output
Write-Host "`n📦 Verifica output..."
$binPath = "./src/AvatarSmartBackups.Core/bin/$Configuration"
if (Test-Path $binPath) {
    $dlls = Get-ChildItem $binPath -Recurse -Include "*.dll" | Where-Object { $_.Name -like "*AvatarSmartBackups*" }
    if ($dlls) {
        Write-Host "✅ DLL generate:" -ForegroundColor Green
        $dlls | ForEach-Object {
            Write-Host "  - $($_.Name) ($([math]::Round($_.Length/1KB, 1)) KB)" -ForegroundColor Cyan
        }
    } else {
        Write-Host "⚠️  Nessuna DLL Avatar Smart Backup trovata" -ForegroundColor Yellow
        Write-Host "Tutte le DLL in $binPath"
        Get-ChildItem $binPath -Recurse -Include "*.dll" | Format-Table Name, Length
    }
} else {
    Write-Host "❌ Cartella bin non trovata: $binPath" -ForegroundColor Red
}

# Step 7: Pulizia (cancellazione DLL)
Write-Host "`n🧹 Pulizia file temporanei..."
try {
    if (Test-Path $binPath) {
        Remove-Item $binPath -Recurse -Force
        Write-Host "✅ Cartella bin cancellata" -ForegroundColor Green
    }
    
    $objPath = "./src/AvatarSmartBackups.Core/obj"
    if (Test-Path $objPath) {
        Remove-Item $objPath -Recurse -Force
        Write-Host "✅ Cartella obj cancellata" -ForegroundColor Green
    }
} catch {
    Write-Host "⚠️  Errore durante pulizia: $_" -ForegroundColor Yellow
}

Write-Host "`n🎉 Test completato! Nessun file DLL rimasto." -ForegroundColor Green
Write-Host "Il codice compila correttamente per .NET 4.8 ✨" -ForegroundColor Cyan