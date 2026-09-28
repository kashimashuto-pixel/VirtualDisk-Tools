[CmdletBinding()]
param(
    [string]$WorkingDirectory = (Join-Path $PSScriptRoot '..\.tmp\physical-vhdx-integration'),
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workingRoot = [IO.Path]::GetFullPath($WorkingDirectory)
$testProject = Join-Path $repositoryRoot 'tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj'
$testDll = Join-Path $repositoryRoot 'tests\Qcow2Explorer.Tests\bin\Release\net10.0-windows\Qcow2Explorer.Tests.dll'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This integration test must be run from an elevated PowerShell window.'
}

foreach ($command in 'New-VHD', 'Mount-VHD', 'Dismount-VHD', 'Get-VHD', 'Get-Disk',
         'Initialize-Disk', 'New-Partition', 'Format-Volume', 'Add-PartitionAccessPath',
         'Remove-PartitionAccessPath') {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required Windows command is unavailable: $command"
    }
}

New-Item -ItemType Directory -Path $workingRoot -Force | Out-Null
$runName = "vdt-$([Guid]::NewGuid().ToString('N'))"
$runDirectory = [IO.Path]::GetFullPath((Join-Path $workingRoot $runName))
$workingPrefix = $workingRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $runDirectory.StartsWith($workingPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($runDirectory)).StartsWith('vdt-', [StringComparison]::Ordinal)) {
    throw "Refusing to use an unsafe integration-test directory: $runDirectory"
}

New-Item -ItemType Directory -Path $runDirectory | Out-Null
$vhdxPath = Join-Path $runDirectory 'target.vhdx'
$mountPath = Join-Path $runDirectory 'volume'
$journalPath = Join-Path $runDirectory 'recovery.vdt-recovery'
New-Item -ItemType Directory -Path $mountPath | Out-Null
$mounted = $false
$accessPathAdded = $false
$completed = $false
$partition = $null
try {
    & dotnet build $testProject --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "The integration-test build failed with exit code $LASTEXITCODE."
    }

    $diskNumbersBefore = @(Get-Disk | Select-Object -ExpandProperty Number)
    New-VHD -Path $vhdxPath -Fixed -SizeBytes 96MB | Out-Null
    $mountedVhd = Mount-VHD -Path $vhdxPath -PassThru
    $mounted = $true
    $diskNumber = [int]$mountedVhd.DiskNumber
    if ($diskNumber -in $diskNumbersBefore) {
        throw "The mounted VHDX reused a pre-existing disk number: $diskNumber"
    }

    $vhdState = Get-VHD -Path $vhdxPath
    if (-not $vhdState.Attached -or [int]$vhdState.DiskNumber -ne $diskNumber) {
        throw 'The VHDX-to-disk-number association could not be verified.'
    }

    $disk = Get-Disk -Number $diskNumber
    if ([long]$disk.Size -ne 96MB -or $disk.IsSystem -or $disk.IsBoot) {
        throw "The newly attached disk failed the size/system safety checks: $diskNumber"
    }

    $disk | Initialize-Disk -PartitionStyle GPT | Out-Null
    $partition = New-Partition -DiskNumber $diskNumber -Size 32MB
    $partition | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'VDT-INTEGRATION' -Confirm:$false | Out-Null
    $partition | Add-PartitionAccessPath -AccessPath $mountPath
    $accessPathAdded = $true

    $marker = "VDT-PHYSICAL-INTEGRATION-$([Guid]::NewGuid().ToString('N'))"
    $markerPath = Join-Path $mountPath 'integration-marker.txt'
    [IO.File]::WriteAllText($markerPath, $marker, [Text.UTF8Encoding]::new($false))
    $writeOffset = 64MB
    $devicePath = "\\.\PhysicalDrive$diskNumber"

    & dotnet $testDll --physical-vhdx-integration `
        $devicePath ([long]$disk.Size) $writeOffset `
        $markerPath $marker $journalPath
    if ($LASTEXITCODE -ne 0) {
        throw "The VHDX physical-write integration test failed with exit code $LASTEXITCODE."
    }

    $systemDriveLetter = ([IO.Path]::GetPathRoot($env:SystemRoot)).TrimEnd('\').TrimEnd(':')
    $systemDisk = Get-Partition -DriveLetter $systemDriveLetter | Get-Disk
    $systemDevicePath = "\\.\PhysicalDrive$($systemDisk.Number)"
    & dotnet $testDll --physical-system-disk-refusal $systemDevicePath
    if ($LASTEXITCODE -ne 0) {
        throw "The system-disk refusal test failed with exit code $LASTEXITCODE."
    }

    $completed = $true
    Write-Host "Physical VHDX integration tests passed for $devicePath."
}
finally {
    if ($accessPathAdded -and $null -ne $partition) {
        try {
            $partition | Remove-PartitionAccessPath -AccessPath $mountPath -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not remove the temporary volume access path: $($_.Exception.Message)"
        }
    }

    if ($mounted) {
        try {
            Dismount-VHD -Path $vhdxPath -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not detach the temporary VHDX: $($_.Exception.Message)"
        }
    }

    if ($completed -and -not $KeepArtifacts) {
        $verifiedRunDirectory = [IO.Path]::GetFullPath($runDirectory)
        if ($verifiedRunDirectory.StartsWith($workingPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            ([IO.Path]::GetFileName($verifiedRunDirectory)).StartsWith('vdt-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $verifiedRunDirectory -Recurse -Force
        }
    }
    elseif (Test-Path -LiteralPath $runDirectory) {
        Write-Host "Integration-test artifacts retained at: $runDirectory"
    }
}
