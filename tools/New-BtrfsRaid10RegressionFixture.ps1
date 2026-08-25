param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\.tmp\real-images\raid10'),

    [string]$Distribution = 'Ubuntu-24.04',

    [ValidateRange(256, 4096)]
    [int]$SizeMiB = 256
)

$ErrorActionPreference = 'Stop'
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$generator = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'new-btrfs-raid10-regression-fixture.sh'))
if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) {
    throw "Btrfs RAID10 fixture generator was not found: $generator"
}

$outputs = 1..4 | ForEach-Object {
    [IO.Path]::Combine($resolvedOutputDirectory, "btrfs-raid10-$_.raw")
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
    --exec bash $linuxGenerator $SizeMiB @linuxOutputs
if ($LASTEXITCODE -ne 0) {
    throw "Btrfs RAID10 fixture generation failed with exit code $LASTEXITCODE."
}

Get-FileHash -LiteralPath $outputs -Algorithm SHA256
Get-Item -LiteralPath $outputs | Select-Object FullName, Length, LastWriteTime
