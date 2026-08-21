param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\.tmp\real-images'),

    [string]$Distribution = 'Ubuntu-24.04'
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$generator = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'new-linux-regression-fixtures.sh'))

if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) {
    throw "Linux fixture generator was not found: $generator"
}

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

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
    --exec bash $linuxGenerator $linuxOutput
if ($LASTEXITCODE -ne 0) {
    throw "Linux fixture generation failed with exit code $LASTEXITCODE."
}

Get-Item `
    -LiteralPath (Join-Path $resolvedOutput 'xfs-bigtime.raw'), (Join-Path $resolvedOutput 'large-xfs.dd.lzo') |
    Select-Object FullName, Length, LastWriteTime
