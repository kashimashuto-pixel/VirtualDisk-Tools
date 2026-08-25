param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\.tmp\real-images\lvm-thin\lvm-thin.raw'),

    [string]$Distribution = 'Ubuntu-24.04',

    [ValidateRange(384, 4096)]
    [int]$SizeMiB = 384
)

$ErrorActionPreference = 'Stop'
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$generator = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'new-lvm-thin-regression-fixture.sh'))
if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) {
    throw "LVM2 thin fixture generator was not found: $generator"
}

if (Test-Path -LiteralPath $resolvedOutputPath) {
    throw "Refusing to overwrite an existing fixture: $resolvedOutputPath"
}

$parentDirectory = Split-Path -Parent $resolvedOutputPath
New-Item -ItemType Directory -Path $parentDirectory -Force | Out-Null

function ConvertTo-WslPath([string]$WindowsPath) {
    $converted = & wsl.exe `
        --distribution $Distribution `
        --user root `
        --exec wslpath -a -u $WindowsPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not convert a Windows path for WSL: $WindowsPath"
    }

    return ($converted | Out-String).Trim()
}

$linuxGenerator = ConvertTo-WslPath $generator
$linuxOutput = ConvertTo-WslPath $resolvedOutputPath
& wsl.exe `
    --distribution $Distribution `
    --user root `
    --exec bash $linuxGenerator $SizeMiB $linuxOutput
if ($LASTEXITCODE -ne 0) {
    throw "LVM2 thin fixture generation failed with exit code $LASTEXITCODE."
}

Get-FileHash -LiteralPath $resolvedOutputPath -Algorithm SHA256
Get-Item -LiteralPath $resolvedOutputPath | Select-Object FullName, Length, LastWriteTime
