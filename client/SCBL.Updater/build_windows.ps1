$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    dotnet publish .\SCBL.Updater.csproj -c Release -r win-x86 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=none /p:DebugSymbols=false -o .\publish
    if (!(Test-Path .\publish\SCBL.Updater.exe)) { throw "SCBL.Updater.exe was not created" }
}
finally {
    Pop-Location
}
