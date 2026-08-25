param(
    [string]$FirstOutputPath = (Join-Path $PSScriptRoot '..\.tmp\real-images\btrfs-multi-1.raw'),

    [string]$SecondOutputPath = (Join-Path $PSScriptRoot '..\.tmp\real-images\btrfs-multi-2.raw'),

    [ValidateSet('Single', 'Raid0', 'Raid1')]
    [string]$Profile = 'Raid1',

    [string]$Distribution = 'Ubuntu-24.04',

    [ValidateRange(256, 4096)]
    [int]$SizeMiB = 256
)

$ErrorActionPreference = 'Stop'
$resolvedFirstOutput = [IO.Path]::GetFullPath($FirstOutputPath)
$resolvedSecondOutput = [IO.Path]::GetFullPath($SecondOutputPath)
if ([string]::Equals($resolvedFirstOutput, $resolvedSecondOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'FirstOutputPath and SecondOutputPath must be different files.'
}

$generator = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'new-btrfs-multi-device-regression-fixture.sh'))
if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) {
    throw "Btrfs multi-device fixture generator was not found: $generator"
}

foreach ($output in @($resolvedFirstOutput, $resolvedSecondOutput)) {
    if (Test-Path -LiteralPath $output) {
        throw "Refusing to overwrite an existing fixture: $output"
    }

    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force | Out-Null
}

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
$linuxFirstOutput = ConvertTo-WslPath $resolvedFirstOutput
$linuxSecondOutput = ConvertTo-WslPath $resolvedSecondOutput
& wsl.exe `
    --distribution $Distribution `
    --user root `
    --exec bash $linuxGenerator $linuxFirstOutput $linuxSecondOutput $SizeMiB $($Profile.ToLowerInvariant())
if ($LASTEXITCODE -ne 0) {
    throw "Btrfs multi-device fixture generation failed with exit code $LASTEXITCODE."
}

Get-FileHash -LiteralPath $resolvedFirstOutput, $resolvedSecondOutput -Algorithm SHA256
Get-Item -LiteralPath $resolvedFirstOutput, $resolvedSecondOutput |
    Select-Object FullName, Length, LastWriteTime
