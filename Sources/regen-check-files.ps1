# Regenerates the Lunar .lfc check files for every component of a mod.
# Same algorithm as LunarFramework's LunarDevTools.CreateCheckFile:
#   checkFile = MD5(componentFile) XOR MD5(About/Version.txt)
# Run this after rebuilding any DLL in <mod>/<version>/Lunar/Components/.

param(
    [Parameter(Mandatory = $true)][string]$ModRoot
)

$ErrorActionPreference = 'Stop'

$versionFile = Join-Path $ModRoot 'About\Version.txt'
$version = ([IO.File]::ReadAllText($versionFile)).Trim()

$md5 = [Security.Cryptography.MD5]::Create()
$versionBytes = $md5.ComputeHash([Text.Encoding]::ASCII.GetBytes($version))

function Write-CheckFile([string]$file) {
    $bytes = $md5.ComputeHash([IO.File]::ReadAllBytes($file))
    for ($i = 0; $i -lt 16; $i++) {
        $bytes[$i] = $bytes[$i] -bxor $versionBytes[$i]
    }
    [IO.File]::WriteAllBytes([IO.Path]::ChangeExtension($file, '.lfc'), $bytes)
}

$mutex = New-Object Threading.Mutex($false, 'LunarRegenCheckFiles')
$mutex.WaitOne([TimeSpan]::FromMinutes(1)) | Out-Null

try {
    Get-ChildItem $ModRoot -Directory | ForEach-Object {
        $lunarDir = Join-Path $_.FullName 'Lunar'
        if (!(Test-Path $lunarDir)) { return }

        $manifest = Join-Path $lunarDir 'Manifest.xml'
        if (Test-Path $manifest) { Write-CheckFile $manifest }

        $componentsDir = Join-Path $lunarDir 'Components'
        if (Test-Path $componentsDir) {
            Get-ChildItem $componentsDir -Filter *.dll | ForEach-Object { Write-CheckFile $_.FullName }
        }
    }
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
