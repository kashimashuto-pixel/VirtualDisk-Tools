param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\.tmp\real-images\btrfs-single.raw'),

    [string]$Distribution = 'Ubuntu-24.04',

    [ValidateRange(128, 4096)]
    [int]$SizeMiB = 256
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
$generator = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'new-btrfs-regression-fixture.sh'))
if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) {
    throw "Btrfs fixture generator was not found: $generator"
}
if (Test-Path -LiteralPath $resolvedOutput) {
    throw "Refusing to overwrite an existing fixture: $resolvedOutput"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

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
$linuxOutput = ConvertTo-WslPath $resolvedOutput
& wsl.exe `
    --distribution $Distribution `
    --user root `
    --exec bash $linuxGenerator $linuxOutput $SizeMiB
if ($LASTEXITCODE -ne 0) {
    throw "Btrfs fixture generation failed with exit code $LASTEXITCODE."
}

Get-FileHash -LiteralPath $resolvedOutput -Algorithm SHA256
Get-Item -LiteralPath $resolvedOutput | Select-Object FullName, Length, LastWriteTime
