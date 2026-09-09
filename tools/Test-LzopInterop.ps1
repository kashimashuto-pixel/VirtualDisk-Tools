param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\.tmp\lzop-interop'),
    [string]$Distribution = 'Ubuntu-24.04',
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testProject = Join-Path $repositoryRoot 'tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj'
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$createdOutputDirectory = -not (Test-Path -LiteralPath $resolvedOutput)
if ($createdOutputDirectory) {
    New-Item -ItemType Directory -Path $resolvedOutput | Out-Null
}
elseif ((Get-ChildItem -LiteralPath $resolvedOutput -Force | Select-Object -First 1)) {
    throw "LZO interop output directory must be empty: $resolvedOutput"
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

$succeeded = $false
try {
    & dotnet build $testProject --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }

    & dotnet run `
        --project $testProject `
        --configuration Release `
        --no-build `
        -- `
        --prepare-lzop-interop $resolvedOutput
    if ($LASTEXITCODE -ne 0) {
        throw "LZO interop fixture preparation failed with exit code $LASTEXITCODE."
    }

    & wsl.exe `
        --distribution $Distribution `
        --user root `
        --exec /usr/bin/lzop --version
    if ($LASTEXITCODE -ne 0) {
        throw "Official lzop was not found in WSL distribution '$Distribution'."
    }

    $blocks = @(Get-ChildItem -LiteralPath $resolvedOutput -Filter 'block-*.lzo' -File | Sort-Object Name)
    if ($blocks.Count -lt 2) {
        throw "At least two exported LZO blocks are required; found $($blocks.Count)."
    }

    foreach ($block in $blocks) {
        $baseName = [IO.Path]::GetFileNameWithoutExtension($block.Name)
        $expectedPath = Join-Path $resolvedOutput "$baseName.expected.raw"
        $officialPath = Join-Path $resolvedOutput "$baseName.official.raw"
        if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
            throw "Expected raw block was not generated: $expectedPath"
        }

        $linuxBlock = ConvertTo-WslPath $block.FullName
        $linuxOfficial = ConvertTo-WslPath $officialPath
        & wsl.exe `
            --distribution $Distribution `
            --user root `
            --exec /usr/bin/lzop -d -f -o $linuxOfficial $linuxBlock
        if ($LASTEXITCODE -ne 0) {
            throw "Official lzop failed to decompress: $($block.FullName)"
        }

        $expected = Get-Item -LiteralPath $expectedPath
        $official = Get-Item -LiteralPath $officialPath
        $expectedHash = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash
        $officialHash = (Get-FileHash -LiteralPath $officialPath -Algorithm SHA256).Hash
        if ($expected.Length -ne $official.Length -or $expectedHash -ne $officialHash) {
            throw "LZO block mismatch: block=$($block.Name), expectedLength=$($expected.Length), officialLength=$($official.Length), expectedSHA256=$expectedHash, officialSHA256=$officialHash"
        }

        Write-Host "passed: $($block.Name), $($official.Length) bytes, SHA-256 $($officialHash.ToLowerInvariant())"
    }

    $succeeded = $true
    Write-Host "Official lzop interoperability passed for $($blocks.Count) blocks."
}
finally {
    if ($succeeded -and -not $KeepArtifacts) {
        foreach ($file in Get-ChildItem -LiteralPath $resolvedOutput -File) {
            Remove-Item -LiteralPath $file.FullName -Force
        }

        if ($createdOutputDirectory -and -not (Get-ChildItem -LiteralPath $resolvedOutput -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $resolvedOutput
        }
    }
    elseif (-not $succeeded) {
        Write-Warning "LZO interop artifacts were kept for diagnosis: $resolvedOutput"
    }
}
