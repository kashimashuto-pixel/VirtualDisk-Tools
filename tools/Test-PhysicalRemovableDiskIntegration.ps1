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

    [string]$WorkingDirectory = (Join-Path $PSScriptRoot '..\.tmp\physical-removable-integration'),
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workingRoot = [IO.Path]::GetFullPath($WorkingDirectory)
$testProject = Join-Path $repositoryRoot 'tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj'
$testDll = Join-Path $repositoryRoot 'tests\Qcow2Explorer.Tests\bin\Release\net10.0-windows\Qcow2Explorer.Tests.dll'
$requiredConfirmation = "ERASE USB DISK $DiskNumber $ExpectedSize"
if ($Confirmation -cne $requiredConfirmation) {
    throw "Confirmation mismatch. Required exact text: $requiredConfirmation"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This destructive removable-disk integration test must be run from an elevated PowerShell window.'
}

foreach ($command in 'Get-Disk', 'Clear-Disk', 'Initialize-Disk', 'New-Partition',
         'Format-Volume', 'Add-PartitionAccessPath', 'Remove-PartitionAccessPath',
         'Get-CimInstance') {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required Windows command is unavailable: $command"
    }
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
        Disk = $disk
        ManagementModel = ([string]$cimDisk.Model).Trim()
        SerialNumber = ([string]$cimDisk.SerialNumber).Trim()
    }
}

& dotnet build $testProject --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "The integration-test build failed with exit code $LASTEXITCODE."
}

$approved = Get-ValidatedTarget
$approvedManagementModel = $approved.ManagementModel
$approvedSerial = $approved.SerialNumber
$approved = $null

New-Item -ItemType Directory -Path $workingRoot -Force | Out-Null
$runName = "vdt-usb-$([Guid]::NewGuid().ToString('N'))"
$runDirectory = [IO.Path]::GetFullPath((Join-Path $workingRoot $runName))
$workingPrefix = $workingRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $runDirectory.StartsWith($workingPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($runDirectory)).StartsWith('vdt-usb-', [StringComparison]::Ordinal)) {
    throw "Refusing to use an unsafe integration-test directory: $runDirectory"
}

New-Item -ItemType Directory -Path $runDirectory | Out-Null
$mountPath = Join-Path $runDirectory 'volume'
$rawJournalPath = Join-Path $runDirectory 'raw-committed.vdt-recovery'
$interruptedJournalPath = Join-Path $runDirectory 'raw-interrupted.vdt-recovery'
$fileEditJournalPath = Join-Path $runDirectory 'file-edit.vdt-recovery'
New-Item -ItemType Directory -Path $mountPath | Out-Null
$partition = $null
$accessPathAdded = $false
$completed = $false
try {
    $immediate = Get-ValidatedTarget
    if ($immediate.ManagementModel -cne $approvedManagementModel -or
        $immediate.SerialNumber -cne $approvedSerial) {
        throw "Disk $DiskNumber hardware identity changed before destructive preparation."
    }

    Write-Host "ERASING approved target: disk=$DiskNumber, model=$ExpectedModel, size=$ExpectedSize, bus=USB"
    $existingPartitions = @(Get-Partition -DiskNumber $DiskNumber -ErrorAction SilentlyContinue)
    if ($existingPartitions.Count -gt 0) {
        Clear-Disk -Number $DiskNumber -RemoveData -RemoveOEM -Confirm:$false
    }
    else {
        Write-Host "Disk $DiskNumber is already empty; continuing with guarded preparation."
    }

    $clearedDisk = Get-Disk -Number $DiskNumber -ErrorAction Stop
    $remainingPartitions = @(Get-Partition -DiskNumber $DiskNumber -ErrorAction SilentlyContinue)
    if ($remainingPartitions.Count -ne 0) {
        throw "Disk $DiskNumber still contains partitions after Clear-Disk."
    }

    if ([string]$clearedDisk.PartitionStyle -eq 'RAW') {
        Initialize-Disk -Number $DiskNumber -PartitionStyle GPT | Out-Null
    }
    elseif ([string]$clearedDisk.PartitionStyle -ne 'GPT') {
        throw "Disk $DiskNumber has an unexpected partition style after Clear-Disk: $($clearedDisk.PartitionStyle)"
    }

    $partition = New-Partition -DiskNumber $DiskNumber -Size 64MB
    $partition | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'VDT-USB-TEST' -Force -Confirm:$false | Out-Null
    $partition | Add-PartitionAccessPath -AccessPath $mountPath
    $accessPathAdded = $true

    $prepared = Get-ValidatedTarget
    if ($prepared.ManagementModel -cne $approvedManagementModel -or
        $prepared.SerialNumber -cne $approvedSerial) {
        throw "Disk $DiskNumber hardware identity changed during destructive preparation."
    }

    $marker = "VDT-REMOVABLE-INTEGRATION-$([Guid]::NewGuid().ToString('N'))"
    $markerPath = Join-Path $mountPath 'VDT-MARKER.TXT'
    [IO.File]::WriteAllText($markerPath, $marker, [Text.UTF8Encoding]::new($false))
    $writeOffset = 128MB
    $partitionEnd = [long]$partition.Offset + [long]$partition.Size
    if ($writeOffset -lt $partitionEnd + 1MB -or $writeOffset -gt $ExpectedSize - 2MB) {
        throw 'The guarded raw-write offset is not safely inside the unpartitioned test area.'
    }

    $devicePath = "\\.\PhysicalDrive$DiskNumber"
    & dotnet $testDll --physical-removable-integration `
        $devicePath $ExpectedSize $writeOffset $markerPath $marker `
        $rawJournalPath $interruptedJournalPath $fileEditJournalPath $approvedManagementModel
    if ($LASTEXITCODE -ne 0) {
        throw "The removable physical-write integration test failed with exit code $LASTEXITCODE."
    }

    $finished = Get-ValidatedTarget
    if ($finished.ManagementModel -cne $approvedManagementModel -or
        $finished.SerialNumber -cne $approvedSerial) {
        throw "Disk $DiskNumber hardware identity changed after the integration test."
    }

    $completed = $true
    Write-Host "Physical removable-disk integration tests passed for $devicePath."
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

    if ($completed -and -not $KeepArtifacts) {
        $verifiedRunDirectory = [IO.Path]::GetFullPath($runDirectory)
        if ($verifiedRunDirectory.StartsWith($workingPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            ([IO.Path]::GetFileName($verifiedRunDirectory)).StartsWith('vdt-usb-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $verifiedRunDirectory -Recurse -Force
        }
    }
    elseif (Test-Path -LiteralPath $runDirectory) {
        Write-Host "Integration-test artifacts retained at: $runDirectory"
        if (Get-ChildItem -LiteralPath $runDirectory -Filter '*.vdt-recovery' -File -ErrorAction SilentlyContinue | Select-Object -First 1) {
            Write-Warning "Disk $DiskNumber may require recovery using a retained .vdt-recovery journal."
        }
        else {
            Write-Host 'No recovery journal was created before the failure.'
        }
    }
}
