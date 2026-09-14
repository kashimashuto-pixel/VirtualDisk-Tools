[CmdletBinding()]
param(
    [string]$Distribution = 'Ubuntu-24.04',
    [string]$WorkingDirectory,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
    $WorkingDirectory = Join-Path $repositoryRoot '.tmp\virtual-disk-creation'
}

$workingRoot = [IO.Path]::GetFullPath($WorkingDirectory)
$testProject = Join-Path $repositoryRoot 'tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj'
$testDll = Join-Path $repositoryRoot 'tests\Qcow2Explorer.Tests\bin\Release\net10.0-windows\Qcow2Explorer.Tests.dll'
$validator = Join-Path $repositoryRoot 'tools\validate-virtual-disk-creation.sh'
$runDirectory = Join-Path $workingRoot ("vdt-create-" + [Guid]::NewGuid().ToString('N'))
$completed = $false

foreach ($path in $testProject, $validator) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required test input was not found: $path"
    }
}

if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    throw 'wsl.exe is required for file-system creation validation.'
}

New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
try {
    & dotnet build $testProject -c Release
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $testDll -PathType Leaf)) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }

    & dotnet $testDll --virtual-disk-create-smoke $runDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Virtual-disk creation smoke test failed with exit code $LASTEXITCODE."
    }

    $wslRunDirectory = (& wsl.exe `
        --distribution $Distribution `
        --user root `
        --exec wslpath -a -- $runDirectory).Trim()
    $wslValidator = (& wsl.exe `
        --distribution $Distribution `
        --user root `
        --exec wslpath -a -- $validator).Trim()
    if ([string]::IsNullOrWhiteSpace($wslRunDirectory) -or
        [string]::IsNullOrWhiteSpace($wslValidator)) {
        throw 'Could not convert validation paths for WSL.'
    }

    & wsl.exe `
        --distribution $Distribution `
        --user root `
        --exec bash $wslValidator $wslRunDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Independent Linux validation failed with exit code $LASTEXITCODE."
    }

    $completed = $true
    Write-Host 'Virtual-disk creation integration tests passed.'
}
finally {
    if ($completed -and -not $KeepArtifacts) {
        $verifiedRunDirectory = [IO.Path]::GetFullPath($runDirectory)
        $verifiedParentInfo = [IO.Directory]::GetParent($verifiedRunDirectory)
        $verifiedParent = if ($null -eq $verifiedParentInfo) {
            $null
        }
        else {
            $verifiedParentInfo.FullName
        }
        if ($verifiedParent -cne $workingRoot -or
            -not $verifiedRunDirectory.StartsWith(
                $workingRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean an unexpected integration-test path: $verifiedRunDirectory"
        }

        if (Test-Path -LiteralPath $verifiedRunDirectory -PathType Container) {
            Remove-Item -LiteralPath $verifiedRunDirectory -Recurse -Force
        }
    }
    elseif (-not $completed) {
        Write-Host "Integration-test artifacts retained at: $runDirectory"
    }
}
