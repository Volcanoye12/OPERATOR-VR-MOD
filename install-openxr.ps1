param(
    [Parameter(Mandatory = $false)]
    [string]$OpenXRPackageDir,

    [Parameter(Mandatory = $true)]
    [string]$GameDir
)

$ErrorActionPreference = 'Stop'
$packageUrl = 'https://download.packages.unity.com/com.unity.xr.openxr/-/com.unity.xr.openxr-1.19.0.tgz'
$temporaryRoot = $null

try {
    if (-not (Test-Path -LiteralPath $GameDir -PathType Container)) {
        throw "Game directory not found: $GameDir"
    }

    $gameRoot = (Resolve-Path -LiteralPath $GameDir).Path
    $gameData = Join-Path $gameRoot 'OPERATOR_Data'
    if (-not (Test-Path -LiteralPath $gameData -PathType Container)) {
        throw "OPERATOR_Data was not found in the game directory: $gameRoot"
    }

    if ([string]::IsNullOrWhiteSpace($OpenXRPackageDir)) {
        if (-not (Get-Command tar.exe -ErrorAction SilentlyContinue)) {
            throw 'Windows tar.exe was not found. Update Windows or provide an extracted package with -OpenXRPackageDir.'
        }

        $temporaryRoot = Join-Path $env:TEMP ('OperatorVR-OpenXR-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
        $archive = Join-Path $temporaryRoot 'openxr-package.tgz'
        Write-Host 'Downloading the official Unity OpenXR 1.19.0 package...'
        Invoke-WebRequest -Uri $packageUrl -OutFile $archive
        & tar.exe -xzf $archive -C $temporaryRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Could not extract the Unity OpenXR package (tar exit code $LASTEXITCODE)."
        }
        $packageRoot = Join-Path $temporaryRoot 'package'
    }
    else {
        if (-not (Test-Path -LiteralPath $OpenXRPackageDir -PathType Container)) {
            throw "OpenXR package directory not found: $OpenXRPackageDir"
        }
        $packageRoot = (Resolve-Path -LiteralPath $OpenXRPackageDir).Path
    }

    $manifestSource = Join-Path $packageRoot 'Runtime\UnitySubsystemsManifest.json'
    if (-not (Test-Path -LiteralPath $manifestSource -PathType Leaf)) {
        throw "Unity OpenXR manifest not found: $manifestSource. Use package version 1.19.0."
    }
    $manifest = Get-Content -LiteralPath $manifestSource -Raw | ConvertFrom-Json
    if ($manifest.libraryName -ne 'UnityOpenXR') {
        throw "Unexpected Unity subsystem manifest libraryName '$($manifest.libraryName)' in $manifestSource"
    }

    $pluginDir = Join-Path $gameData 'Plugins\x86_64'
    $subsystemDir = Join-Path $gameData 'UnitySubsystems\UnityOpenXR'
    $files = @(
        @{
            Source = Join-Path $packageRoot 'Runtime\windows\x64\UnityOpenXR.dll'
            Destination = Join-Path $pluginDir 'UnityOpenXR.dll'
        },
        @{
            Source = Join-Path $packageRoot 'RuntimeLoaders\windows\x64\openxr_loader.dll'
            Destination = Join-Path $pluginDir 'openxr_loader.dll'
        },
        @{
            Source = $manifestSource
            Destination = Join-Path $subsystemDir 'UnitySubsystemsManifest.json'
        }
    )

    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file.Source -PathType Leaf)) {
            throw "Required Unity OpenXR file not found: $($file.Source). Use package version 1.19.0."
        }
    }

    New-Item -ItemType Directory -Force -Path $pluginDir, $subsystemDir | Out-Null
    foreach ($file in $files) {
        Copy-Item -LiteralPath $file.Source -Destination $file.Destination -Force
        Write-Host "Installed $($file.Destination)"
    }

    Write-Host 'Unity OpenXR is ready.'
}
finally {
    if ($temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
