param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('(?i)\.E01$')]
    [string]$OutputPath,

    [string]$Distribution = 'Ubuntu-24.04',

    [ValidateRange(1, 8192)]
    [int]$SegmentSizeMiB = 2048
)

$ErrorActionPreference = 'Stop'
$resolvedSource = [IO.Path]::GetFullPath($SourcePath)
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
$outputStem = [IO.Path]::GetFileNameWithoutExtension($resolvedOutput)
$generator = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'new-ewf-regression-fixture.sh'))

if (-not (Test-Path -LiteralPath $resolvedSource -PathType Leaf)) {
    throw "Source RAW image was not found: $resolvedSource"
}
if ((Get-Item -LiteralPath $resolvedSource).Length -gt ([long]$SegmentSizeMiB * 1MB * 99)) {
    throw 'The fixture generator is limited to at most 99 numeric EWF segments; increase SegmentSizeMiB.'
}
if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) {
    throw "EWF fixture generator was not found: $generator"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$existingSegments = @(Get-ChildItem -LiteralPath $outputDirectory -File | Where-Object {
    $_.BaseName -eq $outputStem -and $_.Extension -match '^\.E\d\d$'
})
if ($existingSegments.Count -ne 0) {
    throw "Refusing to overwrite an existing EWF segment: $($existingSegments[0].FullName)"
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
$linuxSource = ConvertTo-WslPath $resolvedSource
$linuxOutput = ConvertTo-WslPath $resolvedOutput
& wsl.exe `
    --distribution $Distribution `
    --user root `
    --exec bash $linuxGenerator $linuxSource $linuxOutput $SegmentSizeMiB
if ($LASTEXITCODE -ne 0) {
    throw "EWF fixture generation failed with exit code $LASTEXITCODE."
}

Get-ChildItem -LiteralPath $outputDirectory -File | Where-Object {
    $_.BaseName -eq $outputStem -and $_.Extension -match '^\.E\d\d$'
} | Sort-Object Extension | Select-Object FullName, Length, LastWriteTime
