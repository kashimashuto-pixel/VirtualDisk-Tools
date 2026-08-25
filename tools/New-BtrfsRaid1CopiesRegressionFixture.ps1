param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\.tmp\real-images'),

    [ValidateSet('Raid1C3', 'Raid1C4')]
    [string]$Profile = 'Raid1C3',

    [string]$Distribution = 'Ubuntu-24.04',

    [ValidateRange(256, 4096)]
    [int]$SizeMiB = 256
)

$ErrorActionPreference = 'Stop'
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$generator = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'new-btrfs-raid1-copies-regression-fixture.sh'))
if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) {
    throw "Btrfs RAID1C3/C4 fixture generator was not found: $generator"
}

$copyCount = if ($Profile -eq 'Raid1C3') { 3 } else { 4 }
$profileName = $Profile.ToLowerInvariant()
$outputs = 1..$copyCount | ForEach-Object {
    [IO.Path]::Combine($resolvedOutputDirectory, "btrfs-$profileName-$_.raw")
}
foreach ($output in $outputs) {
    if (Test-Path -LiteralPath $output) {
        throw "Refusing to overwrite an existing fixture: $output"
    }
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

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
$linuxOutputs = $outputs | ForEach-Object { ConvertTo-WslPath $_ }
& wsl.exe `
    --distribution $Distribution `
    --user root `
    --exec bash $linuxGenerator $profileName $SizeMiB @linuxOutputs
if ($LASTEXITCODE -ne 0) {
    throw "Btrfs $Profile fixture generation failed with exit code $LASTEXITCODE."
}

Get-FileHash -LiteralPath $outputs -Algorithm SHA256
Get-Item -LiteralPath $outputs | Select-Object FullName, Length, LastWriteTime
