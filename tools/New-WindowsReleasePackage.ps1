param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\.tmp\release'),

    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.0.0-local',

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $repositoryRoot 'src\Qcow2Explorer\Qcow2Explorer.csproj'
$readme = Join-Path $repositoryRoot 'README.md'
$packageName = "VirtualDisk-Tools-$RuntimeIdentifier"
$publishDirectory = Join-Path $resolvedOutput $packageName
$archivePath = Join-Path $resolvedOutput "$packageName.zip"
$checksumPath = "$archivePath.sha256"

if (Test-Path -LiteralPath $resolvedOutput) {
    throw "Refusing to overwrite an existing release output directory: $resolvedOutput"
}

New-Item -ItemType Directory -Path $resolvedOutput | Out-Null
$completed = $false
try {
    & dotnet publish $project `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --output $publishDirectory `
        --no-restore `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Copy-Item -LiteralPath $readme -Destination (Join-Path $publishDirectory 'README.md')
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archivePath))" |
        Set-Content -LiteralPath $checksumPath -Encoding ascii -NoNewline
    $completed = $true
}
finally {
    if (-not $completed -and (Test-Path -LiteralPath $resolvedOutput)) {
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }
}

Get-Item -LiteralPath $archivePath, $checksumPath |
    Select-Object FullName, Length, LastWriteTime
