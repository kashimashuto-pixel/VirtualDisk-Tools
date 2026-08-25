param(
    [ValidateSet('Far', 'Offset')]
    [string]$Layout = 'Far',

    [string]$OutputDirectory,

    [string]$Distribution = 'Ubuntu-24.04',

    [ValidateRange(256, 4096)]
    [int]$SizeMiB = 256
)

$ErrorActionPreference = 'Stop'
$layoutName = $Layout.ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "..\.tmp\real-images\mdraid10-$layoutName"
}

$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$generator = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'new-md-raid10-extended-regression-fixture.sh'))
if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) {
    throw "Linux md RAID10 extended-layout fixture generator was not found: $generator"
}

$outputs = 1..2 | ForEach-Object {
    [IO.Path]::Combine($resolvedOutputDirectory, "md-raid10-$layoutName-$_.raw")
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
    --exec bash $linuxGenerator $layoutName $SizeMiB @linuxOutputs
if ($LASTEXITCODE -ne 0) {
    throw "Linux md RAID10 $layoutName fixture generation failed with exit code $LASTEXITCODE."
}

Get-FileHash -LiteralPath $outputs -Algorithm SHA256
Get-Item -LiteralPath $outputs | Select-Object FullName, Length, LastWriteTime
