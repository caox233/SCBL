$ErrorActionPreference = "Stop"

$Project = Join-Path $PSScriptRoot "SplinterCellCNLauncher.csproj"
$Out = Join-Path $PSScriptRoot "publish-single"
$Bin = Join-Path $PSScriptRoot "bin"
$Obj = Join-Path $PSScriptRoot "obj"

function Remove-FolderSafe {
    param(
        [Parameter(Mandatory=$true)]
        [string]$Path
    )

    if (!(Test-Path -LiteralPath $Path)) {
        return
    }

    Write-Host "Cleaning $Path"

    for ($i = 1; $i -le 5; $i++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            Write-Warning ("Clean attempt {0}/5 failed for {1}: {2}" -f $i, $Path, $_.Exception.Message)
            Start-Sleep -Seconds 1
        }
    }

    $Backup = "{0}.locked.{1}" -f $Path, (Get-Date -Format "yyyyMMdd_HHmmss")
    Write-Warning "Could not fully remove $Path. Trying to rename it to $Backup"
    try {
        Rename-Item -LiteralPath $Path -NewName (Split-Path -Leaf $Backup) -Force -ErrorAction Stop
    }
    catch {
        throw ("Cannot clean or rename {0}. Please reboot Windows and build again. Last error: {1}" -f $Path, $_.Exception.Message)
    }
}

if (!(Test-Path -LiteralPath $Project)) {
    throw "Project file not found: $Project"
}

# Preflight the source files referenced directly by MainWindow before cleaning/building.
# This produces a clear packaging error instead of a later CS0246 compiler message.
$RequiredSourceFiles = @(
    (Join-Path $PSScriptRoot "MainWindow.xaml.cs"),
    (Join-Path $PSScriptRoot "Services\UpdaterBootstrapService.cs"),
    (Join-Path $PSScriptRoot "Services\RemoteClientUpdateService.cs")
)

foreach ($RequiredSourceFile in $RequiredSourceFiles) {
    if (!(Test-Path -LiteralPath $RequiredSourceFile)) {
        throw ("Required launcher source file is missing: {0}. Reapply the latest complete patch or source package." -f $RequiredSourceFile)
    }
}

Remove-FolderSafe $Out
Remove-FolderSafe $Bin
Remove-FolderSafe $Obj

Write-Host "Publishing SplinterCellCNLauncher ..."

& dotnet publish $Project -c Release -r win-x86 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:DebugType=none /p:DebugSymbols=false -o $Out
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed"
}

$Exe = Join-Path $Out "SplinterCellCNLauncher.exe"
if (!(Test-Path -LiteralPath $Exe)) {
    throw "Launcher output was not created: $Exe"
}

Write-Host "Output: $Out"
