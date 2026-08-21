param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [ValidateSet('XtsAes128', 'XtsAes256')]
    [string]$EncryptionMethod = 'XtsAes128',

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$RecoveryPasswordEnvironmentVariable,

    [ValidateScript({ [string]::IsNullOrEmpty($_) -or $_ -match '^[A-Za-z_][A-Za-z0-9_]*$' })]
    [string]$PasswordEnvironmentVariable = '',

    [ValidateScript({ [string]::IsNullOrEmpty($_) -or $_ -match '^[A-Za-z_][A-Za-z0-9_]*$' })]
    [string]$StartupKeyPathEnvironmentVariable = '',

    [string]$StartupKeyDirectory = '',

    [string]$FixtureText = "BitLocker recovery fixture`n",

    [ValidateRange(256, 4096)]
    [int]$SizeMiB = 512
)

$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'BitLocker fixture generation requires an elevated PowerShell session.'
}
if ([string]::IsNullOrEmpty($FixtureText)) {
    throw 'FixtureText must not be empty.'
}
if ([string]::IsNullOrWhiteSpace($StartupKeyPathEnvironmentVariable) -ne [string]::IsNullOrWhiteSpace($StartupKeyDirectory)) {
    throw 'StartupKeyPathEnvironmentVariable and StartupKeyDirectory must be specified together.'
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $resolvedOutput) {
    throw "Refusing to overwrite an existing VHDX: $resolvedOutput"
}

$mounted = $false
$completed = $false
$recoveryPassword = $null
$password = $null
$securePassword = $null
$resolvedStartupKeyDirectory = $null
$generatedStartupKeyPath = $null
try {
    New-VHD -Path $resolvedOutput -Dynamic -SizeBytes ($SizeMiB * 1MB) | Out-Null
    $vhd = Mount-VHD -Path $resolvedOutput -Passthru
    $mounted = $true
    $disk = $vhd | Get-Disk
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT | Out-Null
    $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
    $label = if ($EncryptionMethod -eq 'XtsAes256') { 'VDT_BL256' } else { 'VDT_BL128' }
    $volume = Format-Volume `
        -Partition $partition `
        -FileSystem NTFS `
        -NewFileSystemLabel $label `
        -Confirm:$false `
        -Force
    $mountPoint = "$($volume.DriveLetter):"
    $fixturePath = Join-Path "$mountPoint\" 'fixture.txt'
    [IO.File]::WriteAllText($fixturePath, $FixtureText, [Text.UTF8Encoding]::new($false))
    $fixtureHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash

    Enable-BitLocker `
        -MountPoint $mountPoint `
        -EncryptionMethod $EncryptionMethod `
        -RecoveryPasswordProtector `
        -UsedSpaceOnly `
        -SkipHardwareTest `
        -WarningAction SilentlyContinue `
        -Confirm:$false | Out-Null

    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    do {
        Start-Sleep -Milliseconds 500
        $bitLocker = Get-BitLockerVolume -MountPoint $mountPoint
        Write-Output "BitLocker status=$($bitLocker.VolumeStatus), encrypted=$($bitLocker.EncryptionPercentage)%"
        if ([DateTime]::UtcNow -ge $deadline) {
            throw 'Timed out while waiting for BitLocker encryption to finish.'
        }
    } while ($bitLocker.VolumeStatus -ne 'FullyEncrypted' -or $bitLocker.EncryptionPercentage -lt 100)

    $recoveryProtector = $bitLocker.KeyProtector |
        Where-Object KeyProtectorType -eq 'RecoveryPassword' |
        Select-Object -First 1
    $recoveryPassword = $recoveryProtector.RecoveryPassword
    if ([string]::IsNullOrWhiteSpace($recoveryPassword)) {
        throw 'BitLocker recovery password protector was not created.'
    }

    if (-not [string]::IsNullOrWhiteSpace($PasswordEnvironmentVariable)) {
        $randomPasswordBytes = [byte[]]::new(24)
        try {
            [Security.Cryptography.RandomNumberGenerator]::Fill($randomPasswordBytes)
            $password = [Convert]::ToBase64String($randomPasswordBytes) + 'aA1!'
        }
        finally {
            [Security.Cryptography.CryptographicOperations]::ZeroMemory($randomPasswordBytes)
        }
        $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
        Add-BitLockerKeyProtector `
            -MountPoint $mountPoint `
            -PasswordProtector `
            -Password $securePassword `
            -WarningAction SilentlyContinue | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($StartupKeyPathEnvironmentVariable)) {
        $resolvedStartupKeyDirectory = [IO.Path]::GetFullPath($StartupKeyDirectory)
        New-Item -ItemType Directory -Path $resolvedStartupKeyDirectory -Force | Out-Null
        $existingStartupKeys = @(
            Get-ChildItem -LiteralPath $resolvedStartupKeyDirectory -Filter '*.bek' -File -Force |
                ForEach-Object FullName
        )
        Add-BitLockerKeyProtector `
            -MountPoint $mountPoint `
            -StartupKeyProtector `
            -StartupKeyPath $resolvedStartupKeyDirectory `
            -WarningAction SilentlyContinue | Out-Null
        $newStartupKeys = @(
            Get-ChildItem -LiteralPath $resolvedStartupKeyDirectory -Filter '*.bek' -File -Force |
                Where-Object { $_.FullName -notin $existingStartupKeys }
        )
        if ($newStartupKeys.Count -ne 1) {
            throw "Expected one new BitLocker startup key, but found $($newStartupKeys.Count)."
        }
        $generatedStartupKeyPath = $newStartupKeys[0].FullName
    }

    Lock-BitLocker -MountPoint $mountPoint -ForceDismount | Out-Null
    Unlock-BitLocker -MountPoint $mountPoint -RecoveryPassword $recoveryPassword | Out-Null
    $verified = Get-BitLockerVolume -MountPoint $mountPoint
    if ($verified.LockStatus -ne 'Unlocked') {
        throw 'The generated recovery password could not unlock the BitLocker fixture.'
    }

    if ($securePassword -is [IDisposable]) {
        Lock-BitLocker -MountPoint $mountPoint -ForceDismount | Out-Null
        Unlock-BitLocker -MountPoint $mountPoint -Password $securePassword | Out-Null
        $verified = Get-BitLockerVolume -MountPoint $mountPoint
        if ($verified.LockStatus -ne 'Unlocked') {
            throw 'The generated password could not unlock the BitLocker fixture.'
        }
        [Environment]::SetEnvironmentVariable(
            $PasswordEnvironmentVariable,
            $password,
            [EnvironmentVariableTarget]::User)
    }

    if (-not [string]::IsNullOrWhiteSpace($generatedStartupKeyPath)) {
        Lock-BitLocker -MountPoint $mountPoint -ForceDismount | Out-Null
        Unlock-BitLocker -MountPoint $mountPoint -RecoveryKeyPath $generatedStartupKeyPath | Out-Null
        $verified = Get-BitLockerVolume -MountPoint $mountPoint
        if ($verified.LockStatus -ne 'Unlocked') {
            throw 'The generated startup key could not unlock the BitLocker fixture.'
        }
        [Environment]::SetEnvironmentVariable(
            $StartupKeyPathEnvironmentVariable,
            $generatedStartupKeyPath,
            [EnvironmentVariableTarget]::User)
    }

    [Environment]::SetEnvironmentVariable(
        $RecoveryPasswordEnvironmentVariable,
        $recoveryPassword,
        [EnvironmentVariableTarget]::User)

    Write-Output "encryption_method=$EncryptionMethod"
    Write-Output "fixture_size=$((Get-Item -LiteralPath $fixturePath).Length)"
    Write-Output "fixture_sha256=$fixtureHash"
    Write-Output "recovery_password_environment_variable=$RecoveryPasswordEnvironmentVariable"
    Write-Output 'recovery_password_unlock_verified=true'
    if (-not [string]::IsNullOrWhiteSpace($PasswordEnvironmentVariable)) {
        Write-Output "password_environment_variable=$PasswordEnvironmentVariable"
        Write-Output 'password_unlock_verified=true'
    }
    if (-not [string]::IsNullOrWhiteSpace($StartupKeyPathEnvironmentVariable)) {
        Write-Output "startup_key_path_environment_variable=$StartupKeyPathEnvironmentVariable"
        Write-Output 'startup_key_unlock_verified=true'
    }
    $completed = $true
}
finally {
    $recoveryPassword = $null
    $password = $null
    if ($securePassword -is [IDisposable]) {
        $securePassword.Dispose()
    }
    if ($mounted) {
        Dismount-VHD -Path $resolvedOutput
    }
    if (-not $completed -and (Test-Path -LiteralPath $resolvedOutput)) {
        Remove-Item -LiteralPath $resolvedOutput -Force
    }
    if (-not $completed -and -not [string]::IsNullOrWhiteSpace($generatedStartupKeyPath) -and (Test-Path -LiteralPath $generatedStartupKeyPath)) {
        $resolvedGeneratedKey = [IO.Path]::GetFullPath($generatedStartupKeyPath)
        if (-not [string]::IsNullOrWhiteSpace($resolvedStartupKeyDirectory) -and
            [IO.Path]::GetDirectoryName($resolvedGeneratedKey) -eq $resolvedStartupKeyDirectory) {
            Remove-Item -LiteralPath $resolvedGeneratedKey -Force
        }
    }
}

Get-Item -LiteralPath $resolvedOutput | Select-Object FullName, Length, LastWriteTime
