param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$PassphraseEnvironmentVariable,

    [string]$Distribution = 'Ubuntu-24.04',

    [ValidateSet('Pbkdf2', 'Argon2id')]
    [string]$Kdf = 'Pbkdf2',

    [ValidateRange(128, 4096)]
    [int]$SizeMiB = 256
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
$generator = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'new-luks2-regression-fixture.sh'))
if (-not (Test-Path -LiteralPath $generator -PathType Leaf)) {
    throw "LUKS2 fixture generator was not found: $generator"
}
if (Test-Path -LiteralPath $resolvedOutput) {
    throw "Refusing to overwrite an existing fixture: $resolvedOutput"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

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

$randomBytes = [byte[]]::new(32)
$passphraseBytes = $null
$passphrase = $null
$temporaryKeyPath = [IO.Path]::GetTempFileName()
try {
    [Security.Cryptography.RandomNumberGenerator]::Fill($randomBytes)
    $passphrase = [Convert]::ToBase64String($randomBytes) + 'aA1!'
    $passphraseBytes = [Text.UTF8Encoding]::new($false).GetBytes($passphrase)
    [IO.File]::WriteAllBytes($temporaryKeyPath, $passphraseBytes)

    $linuxGenerator = ConvertTo-WslPath $generator
    $linuxOutput = ConvertTo-WslPath $resolvedOutput
    $linuxKeyFile = ConvertTo-WslPath $temporaryKeyPath
    $linuxKdf = $Kdf.ToLowerInvariant()
    & wsl.exe `
        --distribution $Distribution `
        --user root `
        --exec bash $linuxGenerator $linuxOutput $linuxKeyFile $SizeMiB $linuxKdf
    if ($LASTEXITCODE -ne 0) {
        throw "LUKS2 fixture generation failed with exit code $LASTEXITCODE."
    }

    [Environment]::SetEnvironmentVariable(
        $PassphraseEnvironmentVariable,
        $passphrase,
        [EnvironmentVariableTarget]::User)
    Write-Output "passphrase_environment_variable=$PassphraseEnvironmentVariable"
    Write-Output "kdf=$Kdf"
    Write-Output 'cryptsetup_unlock_verified=true'
}
finally {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($randomBytes)
    if ($passphraseBytes -is [byte[]]) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($passphraseBytes)
    }
    $passphrase = $null
    if (Test-Path -LiteralPath $temporaryKeyPath) {
        Remove-Item -LiteralPath $temporaryKeyPath -Force
    }
}

Get-Item -LiteralPath $resolvedOutput | Select-Object FullName, Length, LastWriteTime
