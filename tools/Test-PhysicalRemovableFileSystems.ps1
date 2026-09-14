[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$DiskNumber,

    [Parameter(Mandatory)]
    [long]$ExpectedSize,

    [Parameter(Mandatory)]
    [string]$ExpectedModel,

    [Parameter(Mandatory)]
    [string]$Confirmation,

    [ValidateSet('FAT16', 'exFAT', 'NTFS', 'ext4', 'XFS')]
    [string[]]$FileSystems = @('FAT16', 'exFAT', 'NTFS', 'ext4', 'XFS'),

    [string]$Distribution = 'Ubuntu-24.04',
    [string]$WorkingDirectory = (Join-Path $PSScriptRoot '..\.tmp\physical-removable-filesystems'),
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workingRoot = [IO.Path]::GetFullPath($WorkingDirectory)
$testProject = Join-Path $repositoryRoot 'tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj'
$testDll = Join-Path $repositoryRoot 'tests\Qcow2Explorer.Tests\bin\Release\net10.0-windows\Qcow2Explorer.Tests.dll'
$fixtureGenerator = Join-Path $repositoryRoot 'tools\new-write-regression-fixtures.sh'
$requiredConfirmation = "ERASE USB DISK $DiskNumber $ExpectedSize FOR FAT16 EXFAT NTFS EXT4 XFS"
if ($Confirmation -cne $requiredConfirmation) {
    throw "Confirmation mismatch. Required exact text: $requiredConfirmation"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This destructive removable file-system test must be run from an elevated PowerShell window.'
}

foreach ($command in 'Get-Disk', 'Clear-Disk', 'Initialize-Disk', 'New-Partition',
         'Get-Partition', 'Get-CimInstance', 'Update-Disk', 'wsl.exe') {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required Windows command is unavailable: $command"
    }
}

if (-not (Test-Path -LiteralPath $fixtureGenerator -PathType Leaf)) {
    throw "Fixture generator was not found: $fixtureGenerator"
}

function Get-ValidatedTarget {
    $disk = Get-Disk -Number $DiskNumber -ErrorAction Stop
    if ($disk.Number -ne $DiskNumber -or
        [string]$disk.FriendlyName -cne $ExpectedModel -or
        [long]$disk.Size -ne $ExpectedSize -or
        [string]$disk.BusType -ne 'USB' -or
        $disk.IsSystem -or $disk.IsBoot -or $disk.IsReadOnly -or $disk.IsOffline -or
        [string]$disk.OperationalStatus -ne 'Online' -or
        [string]$disk.HealthStatus -ne 'Healthy') {
        throw "Disk $DiskNumber no longer matches the explicitly approved removable target."
    }

    $cimDisk = Get-CimInstance Win32_DiskDrive | Where-Object Index -eq $DiskNumber | Select-Object -First 1
    if ($null -eq $cimDisk -or
        [string]$cimDisk.InterfaceType -ne 'USB' -or
        [string]::IsNullOrWhiteSpace([string]$cimDisk.Model) -or
        [string]::IsNullOrWhiteSpace([string]$cimDisk.SerialNumber)) {
        throw "Disk $DiskNumber does not expose the required USB hardware identity."
    }

    return [pscustomobject]@{
        ManagementModel = ([string]$cimDisk.Model).Trim()
        SerialNumber = ([string]$cimDisk.SerialNumber).Trim()
    }
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

& dotnet build $testProject --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "The integration-test build failed with exit code $LASTEXITCODE."
}

$approved = Get-ValidatedTarget
$approvedManagementModel = $approved.ManagementModel
$approvedSerial = $approved.SerialNumber

New-Item -ItemType Directory -Path $workingRoot -Force | Out-Null
$runName = "vdt-fs-$([Guid]::NewGuid().ToString('N'))"
$runDirectory = [IO.Path]::GetFullPath((Join-Path $workingRoot $runName))
$workingPrefix = $workingRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $runDirectory.StartsWith($workingPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($runDirectory)).StartsWith('vdt-fs-', [StringComparison]::Ordinal)) {
    throw "Refusing to use an unsafe integration-test directory: $runDirectory"
}

New-Item -ItemType Directory -Path $runDirectory | Out-Null
$fixtureDirectory = Join-Path $runDirectory 'fixtures'
$linuxGenerator = ConvertTo-WslPath $fixtureGenerator
$linuxFixtureDirectory = ConvertTo-WslPath $fixtureDirectory
$completed = $false
try {
    & wsl.exe `
        --distribution $Distribution `
        --user root `
        --exec bash $linuxGenerator $linuxFixtureDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture generation failed with exit code $LASTEXITCODE."
    }

    $fixtureNames = @{
        FAT16 = 'fat16-source.raw'
        exFAT = 'exfat-source.raw'
        NTFS = 'ntfs-source.raw'
        ext4 = 'ext4-source.raw'
        XFS = 'xfs-source.raw'
    }
    $replacementPath = Join-Path $fixtureDirectory 'replacement.bin'
    $finalContentPath = Join-Path $fixtureDirectory 'final-content.bin'
    foreach ($fileSystem in $FileSystems) {
        $before = Get-ValidatedTarget
        if ($before.ManagementModel -cne $approvedManagementModel -or
            $before.SerialNumber -cne $approvedSerial) {
            throw "Disk $DiskNumber hardware identity changed before testing $fileSystem."
        }

        $fixturePath = Join-Path $fixtureDirectory $fixtureNames[$fileSystem]
        $fixture = Get-Item -LiteralPath $fixturePath -ErrorAction Stop
        if ($fixture.Length % 512 -ne 0 -or $fixture.Length -gt $ExpectedSize - 4MB) {
            throw "Fixture size is invalid for the approved target: $fixturePath"
        }

        $existingPartitions = @(Get-Partition -DiskNumber $DiskNumber -ErrorAction SilentlyContinue)
        if ($existingPartitions.Count -gt 0) {
            Clear-Disk -Number $DiskNumber -RemoveData -RemoveOEM -Confirm:$false
        }

        $clearedDisk = Get-Disk -Number $DiskNumber -ErrorAction Stop
        if ([string]$clearedDisk.PartitionStyle -eq 'RAW') {
            Initialize-Disk -Number $DiskNumber -PartitionStyle GPT | Out-Null
        }
        elseif ([string]$clearedDisk.PartitionStyle -ne 'GPT') {
            throw "Disk $DiskNumber has an unexpected partition style: $($clearedDisk.PartitionStyle)"
        }

        if (@(Get-Partition -DiskNumber $DiskNumber -ErrorAction SilentlyContinue).Count -ne 0) {
            throw "Disk $DiskNumber still has partitions before preparing $fileSystem."
        }

        $partition = New-Partition `
            -DiskNumber $DiskNumber `
            -Size $fixture.Length `
            -GptType '{0FC63DAF-8483-4772-8E79-3D69D8477DE4}'
        $journalPath = Join-Path $runDirectory "$($fileSystem.ToLowerInvariant()).vdt-recovery"
        $devicePath = "\\.\PhysicalDrive$DiskNumber"
        Write-Host "Testing $fileSystem on disk $DiskNumber using $($fixture.Length) bytes."
        & dotnet $testDll --physical-filesystem-integration `
            $devicePath $ExpectedSize ([long]$partition.Offset) ([long]$partition.Size) `
            $approvedManagementModel $fileSystem $fixturePath $replacementPath $finalContentPath $journalPath
        if ($LASTEXITCODE -ne 0) {
            throw "Physical $fileSystem integration failed with exit code $LASTEXITCODE."
        }

        $after = Get-ValidatedTarget
        if ($after.ManagementModel -cne $approvedManagementModel -or
            $after.SerialNumber -cne $approvedSerial) {
            throw "Disk $DiskNumber hardware identity changed after testing $fileSystem."
        }

        if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
            Remove-Item -LiteralPath $journalPath -Force
        }

        Update-Disk -Number $DiskNumber
        Write-Host "Passed and restored: $fileSystem"
    }

    $completed = $true
    Write-Host "Physical removable file-system tests passed: $($FileSystems -join ', ')"
}
finally {
    if ($completed -and -not $KeepArtifacts) {
        $verifiedRunDirectory = [IO.Path]::GetFullPath($runDirectory)
        if ($verifiedRunDirectory.StartsWith($workingPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            ([IO.Path]::GetFileName($verifiedRunDirectory)).StartsWith('vdt-fs-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $verifiedRunDirectory -Recurse -Force
        }
    }
    elseif (Test-Path -LiteralPath $runDirectory) {
        Write-Host "Integration-test artifacts retained at: $runDirectory"
        if (Get-ChildItem -LiteralPath $runDirectory -Filter '*.vdt-recovery' -File -ErrorAction SilentlyContinue | Select-Object -First 1) {
            Write-Warning "Disk $DiskNumber may require recovery using a retained .vdt-recovery journal."
        }
    }
}
