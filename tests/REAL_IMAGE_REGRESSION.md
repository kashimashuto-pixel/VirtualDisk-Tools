# 実イメージ回帰テスト

実環境由来のディスクイメージはGitへ追加せず、ローカルmanifestから明示的に実行します。
manifestにはイメージのSHA-256、期待する形式・ファイルシステム・ファイル情報だけを記録します。
コンテナー展開後の仮想ディスク全体も照合する場合は`expectedLogicalSha256`を指定します。

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj `
  --configuration Debug --no-build -- `
  --real-image-regression tests\real-images.local.json
```

`tests/real-images*.local.json`は`.gitignore`対象です。絶対パスや機密情報をGitへ追加しないでください。
イメージの`path`はmanifestからの相対パス、または`%VDT_FIXTURE_ROOT%`などの環境変数を使用できます。

## ローカルfixtureの生成

生成物は既定で`.tmp/real-images`へ保存され、Gitには追加されません。
生成ツールは既存のfixtureを上書きしないため、作り直す場合は保存済みの回復パスワードと対象を確認してから古いfixtureを削除してください。

### XFS bigtimeとLZO

WSL 2のUbuntu 24.04を用意し、必要なLinuxツールをインストールします。

```powershell
wsl --install --distribution Ubuntu-24.04 --no-launch
wsl --distribution Ubuntu-24.04 --user root -- sh -lc `
  "apt-get update && apt-get install -y xfsprogs lzop util-linux"

.\tools\New-LinuxRegressionFixtures.ps1
```

スクリプトは、2045年の更新日時を持つXFS bigtime RAWと、そのRAWを圧縮したLZOを生成します。

### Btrfs single profile

WSL 2のUbuntu 24.04へ`btrfs-progs`をインストールし、単一デバイス・single profileのRAW fixtureを生成します。

```powershell
wsl --distribution Ubuntu-24.04 --user root -- sh -lc `
  "apt-get update && apt-get install -y btrfs-progs util-linux"

.\tools\New-BtrfsRegressionFixture.ps1 `
  -OutputPath .\.tmp\real-images\btrfs-single.raw
```

スクリプトはインラインファイル、通常extent、スパースファイル、zlib圧縮ファイル、通常・inline LZO／zstd圧縮ファイルを作成し、`btrfs check --readonly`と各ファイルのSHA-256を表示します。
既存fixtureは上書きせず、生成物とローカルmanifestはGitへ追加しません。

### BitLocker XTS-AES

BitLocker fixtureの生成は管理者PowerShellで実行します。
回復パスワードは出力せず、指定したユーザー環境変数へ保存します。

```powershell
.\tools\New-BitLockerRegressionFixture.ps1 `
  -OutputPath .\.tmp\real-images\bitlocker-xts128.vhdx `
  -EncryptionMethod XtsAes128 `
  -RecoveryPasswordEnvironmentVariable VDT_BITLOCKER_XTS128_RECOVERY

.\tools\New-BitLockerRegressionFixture.ps1 `
  -OutputPath .\.tmp\real-images\bitlocker-xts256.vhdx `
  -EncryptionMethod XtsAes256 `
  -RecoveryPasswordEnvironmentVariable VDT_BITLOCKER_XTS256_RECOVERY `
  -PasswordEnvironmentVariable VDT_BITLOCKER_XTS256_PASSWORD `
  -FixtureText "BitLocker XTS-AES 256 fixture`n"

.\tools\New-BitLockerRegressionFixture.ps1 `
  -OutputPath .\.tmp\real-images\bitlocker-startup-xts256.vhdx `
  -EncryptionMethod XtsAes256 `
  -RecoveryPasswordEnvironmentVariable VDT_BITLOCKER_STARTUP_RECOVERY `
  -StartupKeyPathEnvironmentVariable VDT_BITLOCKER_STARTUP_KEY_PATH `
  -StartupKeyDirectory .\.tmp\real-images\startup-keys `
  -FixtureText "BitLocker startup-key XTS-AES 256 fixture`n"
```

`PasswordEnvironmentVariable`を指定すると、ランダムな通常パスワード保護子も追加し、Windowsでの解除確認後に値を指定したユーザー環境変数へ保存します。
ローカルmanifestでは回復パスワードの代わりに`"passwordEnvironmentVariable": "VDT_BITLOCKER_XTS256_PASSWORD"`を指定して、通常パスワード経路を検証できます。
`StartupKeyPathEnvironmentVariable`と`StartupKeyDirectory`を指定すると、Windowsが生成した`.BEK`保護子も追加します。Windows自身で解除確認後、`.BEK`のパスだけを指定したユーザー環境変数へ保存します。
ローカルmanifestでは`"startupKeyPathEnvironmentVariable": "VDT_BITLOCKER_STARTUP_KEY_PATH"`を指定して、スタートアップキー経路を検証できます。

生成後は`Get-FileHash -Algorithm SHA256`でイメージのハッシュを取得し、ローカルmanifestへ登録します。
fixture本体、manifest、回復パスワード、通常パスワード、`.BEK`をコミットしないでください。

### LUKS1 AES-XTS

WSL 2のUbuntu 24.04へ`cryptsetup`をインストールし、LUKS1 + ext4のRAW fixtureを生成します。

```powershell
wsl --distribution Ubuntu-24.04 --user root -- sh -lc `
  "apt-get update && apt-get install -y cryptsetup e2fsprogs util-linux"

.\tools\New-Luks1RegressionFixture.ps1 `
  -OutputPath .\.tmp\real-images\luks1-xts256.raw `
  -PassphraseEnvironmentVariable VDT_LUKS1_PASSPHRASE
```

スクリプトはランダムなパスフレーズを生成し、cryptsetupでLUKS1の生成・解除を確認してから、値を指定したユーザー環境変数へ保存します。パスフレーズ自体は出力しません。
fixtureを高速に再生成するためPBKDF2の短いiteration targetを使用しており、実運用向けのセキュリティ設定例ではありません。
ローカルmanifestでは`"luksPassphraseEnvironmentVariable": "VDT_LUKS1_PASSPHRASE"`を指定します。fixture、manifest、パスフレーズをコミットしないでください。

### LUKS2 PBKDF2 AES-XTS

同じWSL環境で、PBKDF2 keyslotを明示したLUKS2 + ext4のRAW fixtureを生成します。

```powershell
.\tools\New-Luks2RegressionFixture.ps1 `
  -OutputPath .\.tmp\real-images\luks2-pbkdf2-xts256.raw `
  -PassphraseEnvironmentVariable VDT_LUKS2_PASSPHRASE
```

スクリプトは`--type luks2 --pbkdf pbkdf2 --cipher aes-xts-plain64 --key-size 512`を明示し、cryptsetup自身で生成・解除を確認します。
PBKDF2の短いiteration targetはローカルfixtureを高速に再生成するためのもので、実運用向けのセキュリティ設定例ではありません。
ローカルmanifestでは`"luksPassphraseEnvironmentVariable": "VDT_LUKS2_PASSPHRASE"`を指定します。fixture、manifest、パスフレーズをコミットしないでください。

### LUKS2 Argon2id AES-XTS

`-Kdf Argon2id`を指定すると、cryptsetupのcalibrated defaultを使用した通常のLUKS2 fixtureを生成します。

```powershell
.\tools\New-Luks2RegressionFixture.ps1 `
  -OutputPath .\.tmp\real-images\luks2-argon2id-default.raw `
  -PassphraseEnvironmentVariable VDT_LUKS2_ARGON2ID_PASSPHRASE `
  -Kdf Argon2id
```

環境に応じて最大1 GiB程度のメモリを使用し、生成時と回帰実行時の解除に数秒以上かかります。
ローカルmanifestでは`"luksPassphraseEnvironmentVariable": "VDT_LUKS2_ARGON2ID_PASSPHRASE"`を指定します。

### EnCase 6 E01

WSL 2のUbuntu 24.04へ`ewf-tools`をインストールし、既存のRAW fixtureをE01へ変換します。

```powershell
wsl --distribution Ubuntu-24.04 --user root -- sh -lc `
  "apt-get update && apt-get install -y ewf-tools"

.\tools\New-EwfRegressionFixture.ps1 `
  -SourcePath .\.tmp\real-images\sample-fat16.img `
  -OutputPath .\.tmp\real-images\ewf-fat16.E01 `
  -SegmentSizeMiB 2048
```

スクリプトはEnCase 6形式を生成し、`ewfverify`で検証して、元RAWと各segmentのSHA-256を表示します。
分割読取を実イメージでも確認する場合は、小さい`SegmentSizeMiB`を指定します。manifestの`sha256`にはE01第1 segment、`expectedLogicalSha256`には元RAWのSHA-256を記録します。
fixture本体とローカルmanifestはコミットしないでください。

## Manifest例

```json
{
  "version": 1,
  "cases": [
    {
      "name": "XFS bigtime fixture",
      "path": "%VDT_FIXTURE_ROOT%\\xfs-bigtime.raw",
      "sha256": "REPLACE_WITH_64_HEX_CHARACTERS",
      "expectedFormatContains": "raw/dd",
      "expectedPartitionCount": 1,
      "partitions": [
        {
          "number": 1,
          "expectedFileSystem": "XFS",
          "files": [
            {
              "path": "/bigtime.txt",
              "expectedDirectory": false,
              "expectedLength": 12,
              "sha256": "REPLACE_WITH_64_HEX_CHARACTERS",
              "expectedModifiedUtc": "2026-08-20T09:23:09.1234567Z",
              "timestampToleranceSeconds": 0
            }
          ]
        }
      ]
    },
    {
      "name": "BitLocker recovery fixture",
      "path": "%VDT_FIXTURE_ROOT%\\bitlocker.vhdx",
      "sha256": "REPLACE_WITH_64_HEX_CHARACTERS",
      "expectedFormatContains": "VHDX",
      "expectedPartitionCount": 2,
      "partitions": [
        {
          "number": 2,
          "expectedFileSystem": "BitLocker/FVE -> NTFS",
          "recoveryPasswordEnvironmentVariable": "VDT_BITLOCKER_RECOVERY",
          "files": [
            {
              "path": "/fixture.txt",
              "expectedDirectory": false,
              "sha256": "REPLACE_WITH_64_HEX_CHARACTERS"
            }
          ]
        }
      ]
    },
    {
      "name": "Btrfs single fixture",
      "path": "%VDT_FIXTURE_ROOT%\\btrfs-single.raw",
      "sha256": "REPLACE_WITH_64_HEX_CHARACTERS",
      "expectedFormatContains": "raw/dd",
      "expectedPartitionCount": 1,
      "partitions": [
        {
          "number": 1,
          "expectedFileSystem": "Btrfs",
          "files": [
            {
              "path": "/hello.txt",
              "expectedDirectory": false,
              "expectedLength": 17,
              "sha256": "1E7E7CAE2B00EF869EE050726D5D1747FDE1CBCE9726DAC9E05C088046EEA037",
              "expectedModifiedUtc": "2046-02-03T04:05:06Z",
              "timestampToleranceSeconds": 0
            },
            {
              "path": "/nested/regular.bin",
              "expectedDirectory": false,
              "expectedLength": 131072,
              "sha256": "FA43239BCEE7B97CA62F007CC68487560A39E19F74F3DDE7486DB3F98DF8E471"
            },
            {
              "path": "/compressed-zlib.bin",
              "expectedDirectory": false,
              "expectedLength": 262144,
              "sha256": "8A39D2ABD3999AB73C34DB2476849CDDF303CE389B35826850F9A700589B4A90"
            },
            {
              "path": "/compressed-lzo.bin",
              "expectedDirectory": false,
              "expectedLength": 262144,
              "sha256": "8A39D2ABD3999AB73C34DB2476849CDDF303CE389B35826850F9A700589B4A90"
            },
            {
              "path": "/compressed-inline-lzo.bin",
              "expectedDirectory": false,
              "expectedLength": 1024,
              "sha256": "5F70BF18A086007016E948B04AED3B82103A36BEA41755B6CDDFAF10ACE3C6EF"
            },
            {
              "path": "/compressed-zstd.bin",
              "expectedDirectory": false,
              "expectedLength": 262144,
              "sha256": "8A39D2ABD3999AB73C34DB2476849CDDF303CE389B35826850F9A700589B4A90"
            },
            {
              "path": "/compressed-inline-zstd.bin",
              "expectedDirectory": false,
              "expectedLength": 1024,
              "sha256": "5F70BF18A086007016E948B04AED3B82103A36BEA41755B6CDDFAF10ACE3C6EF"
            },
            {
              "path": "/sparse.bin",
              "expectedDirectory": false,
              "expectedLength": 1048588,
              "sha256": "EC33A976122A647689AE42EFAFF7F114E6F5460B313690EB212C96EADA0C375C"
            }
          ]
        }
      ]
    },
    {
      "name": "Large LZO cache fixture",
      "path": "%VDT_FIXTURE_ROOT%\\large.dd.lzo",
      "sha256": "REPLACE_WITH_64_HEX_CHARACTERS",
      "expectedFormatContains": "lzop",
      "verifyLzopCacheReuse": true,
      "verifyLzopCacheCancellation": true,
      "partitions": []
    }
  ]
}
```

BitLockerの回復パスワード・通常パスワード・`.BEK`パスはmanifestへ直接書かず、指定した環境変数から実行時だけ読み込みます。
ランナーは入力イメージのSHA-256を先に照合し、回復キー・パスワード・外部キーを使用後に消去します。
LUKS1/LUKS2パスフレーズも環境変数から実行時だけ読み込み、使用後に文字配列を消去します。
LZOキャッシュcaseでは、初回展開時間と再利用時間を表示し、任意でキャンセル後に部分ファイルが残らないことも確認します。
