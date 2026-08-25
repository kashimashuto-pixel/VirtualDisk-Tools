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

スクリプトはインラインファイル、通常extent、スパースファイル、zlib圧縮ファイル、通常・inline LZO／zstd圧縮ファイル、subvolume、入れ子subvolume、snapshotを作成し、作成したsubvolumeをdefaultに設定します。最後に`btrfs check --readonly`と各ファイルのSHA-256を表示します。
64 MiB mirrorが収まるfixtureでは、primaryを変更せず`btrfs inspect-internal dump-super -s 1`でbackup superblockも確認できます。破損回帰用コピーを作る場合も、元fixtureは保持してください。
既存fixtureは上書きせず、生成物とローカルmanifestはGitへ追加しません。

### Btrfs multiple device single／RAID0／RAID1

同じWSL環境で2個のRAWとloop deviceを使うfixtureを生成します。`-Profile Single`はmetadata/dataともsingle、`-Profile Raid0`はstriping、`-Profile Raid1`は2-copy mirrorです。

```powershell
.\tools\New-BtrfsMultiDeviceRegressionFixture.ps1 `
  -FirstOutputPath .\.tmp\real-images\btrfs-raid1-1.raw `
  -SecondOutputPath .\.tmp\real-images\btrfs-raid1-2.raw `
  -Profile Raid1
```

生成処理は2台を同時にloop deviceへ割り当て、`btrfs check --readonly`と各RAW・内部ファイルのSHA-256を表示します。single profileは`-Profile Single`で別名の出力先を指定してください。
manifestではprimaryを従来の`path`／`sha256`に置き、残りを`companionImages`へ記録します。ランナーは全RAWのSHA-256を照合し、すべてに共通するBtrfs FSIDがない構成を拒否します。
2-copy RAID1の片方だけを通常の`path`として登録し`companionImages`を省略すると、degraded読み取りも回帰確認できます。欠損deviceがあっても、すべてのchunkに利用可能なstripeが残る場合だけ開きます。single profileでも、欠損device上に割り当て済みchunkがなければ同じ基準で読み取れます。
RAID0は全stripe deviceを必要とするため、2個とも登録してください。`-Profile Raid0`で生成したfixtureは、64 KiB単位のdevice切替と通常・zlib・LZO・zstd extentを回帰確認します。

RAID1C3／RAID1C4は専用スクリプトで3個／4個のRAWを生成します。

```powershell
.\tools\New-BtrfsRaid1CopiesRegressionFixture.ps1 `
  -OutputDirectory .\.tmp\real-images\raid1c34 `
  -Profile Raid1C3

.\tools\New-BtrfsRaid1CopiesRegressionFixture.ps1 `
  -OutputDirectory .\.tmp\real-images\raid1c34 `
  -Profile Raid1C4
```

各生成処理は`btrfs check --readonly`とSHA-256を表示します。完全なdevice setは2個目以降を`companionImages`へ並べます。C3では1個、C4では1個だけを`path`へ登録して残りを省略すると、最大device欠損時のdegraded読み取りも確認できます。

RAID10は専用スクリプトで4個のRAWを生成します。

```powershell
.\tools\New-BtrfsRaid10RegressionFixture.ps1 `
  -OutputDirectory .\.tmp\real-images\raid10
```

完全構成では残り3個を`companionImages`へ並べます。degraded回帰では、すべてのchunkに対して各mirror組から1台以上残る組み合わせを使用してください。stripeの組み合わせはchunkごとに異なる場合があるため、単純にdevidだけから耐障害性を判断せず、ランナーのchunk検証を通過する構成を登録します。

### Linux md RAID1

WSL 2のUbuntu 24.04へ`mdadm`と`e2fsprogs`をインストールし、metadata 1.2のRAID1とext4を生成します。

```powershell
wsl --distribution Ubuntu-24.04 --user root -- sh -lc `
  "apt-get update && apt-get install -y mdadm e2fsprogs util-linux"

.\tools\New-MdRaid1RegressionFixture.ps1 `
  -FirstOutputPath .\.tmp\real-images\md-raid1-1.raw `
  -SecondOutputPath .\.tmp\real-images\md-raid1-2.raw
```

スクリプトはRAID1同期完了後にext4と検証ファイルを作成し、`e2fsck -fn`、`mdadm --detail`／`--examine`、各RAWと内部ファイルのSHA-256を表示します。manifestでは`"deviceSet": "Linux md RAID1"`を指定し、2台目を`companionImages`へ登録します。1台だけのcaseも登録するとdegraded読み取りを確認できます。

### Linux md RAID0

同じWSL環境で、metadata 1.2、64 KiB chunkのRAID0とext4を生成します。

```powershell
.\tools\New-MdRaid0RegressionFixture.ps1 `
  -FirstOutputPath .\.tmp\real-images\md-raid0-1.raw `
  -SecondOutputPath .\.tmp\real-images\md-raid0-2.raw
```

スクリプトはstripeを横断する300 MiBファイルを作成し、`e2fsck -fn`、`mdadm --detail`／`--examine`、各RAWと内部ファイルのSHA-256を表示します。manifestでは`"deviceSet": "Linux md RAID0"`を指定し、2台目を`companionImages`へ登録します。RAID0は全memberが必要なため、欠損構成は成功caseとして登録できません。

### Linux md RAID10

metadata 1.2、64 KiB chunk、4台のnear-2 RAID10とext4を生成します。

```powershell
.\tools\New-MdRaid10RegressionFixture.ps1 `
  -OutputDirectory .\.tmp\real-images\mdraid10
```

スクリプトはstripeとmirror groupを横断する300 MiBファイルを作成し、`e2fsck -fn`、`mdadm --detail`／`--examine`、各RAWと内部ファイルのSHA-256を表示します。完全構成は`"deviceSet": "Linux md RAID10"`と残り3台の`companionImages`を指定します。near-2の4台構成ではrole 1と3のように各mirror groupから1台ずつ登録するとdegraded回帰も確認できます。同じmirror groupの2台だけでは組み立てを拒否します。

far-2／offset-2 layoutは、2台構成の専用fixtureで検証します。

```powershell
.\tools\New-MdRaid10ExtendedRegressionFixture.ps1 `
  -Layout Far `
  -OutputDirectory .\.tmp\real-images\mdraid10-far

.\tools\New-MdRaid10ExtendedRegressionFixture.ps1 `
  -Layout Offset `
  -OutputDirectory .\.tmp\real-images\mdraid10-offset
```

各スクリプトはstripe境界を横断する160 MiBファイルを作成し、`e2fsck -fn`、`mdadm --detail`／`--examine`、各RAWと内部ファイルのSHA-256を表示します。完全構成では2台目を`companionImages`へ指定し、1台だけのcaseも登録するとfar／offsetそれぞれのdegraded読み取りを確認できます。

### LVM2 multiple PV

WSL 2のUbuntu 24.04へ`lvm2`と`e2fsprogs`をインストールし、2個のPVにまたがるlinear LVとext4を生成します。

```powershell
wsl --distribution Ubuntu-24.04 --user root -- sh -lc `
  "apt-get update && apt-get install -y lvm2 e2fsprogs python3 util-linux"

.\tools\New-LvmMultiPvRegressionFixture.ps1 `
  -FirstOutputPath .\.tmp\real-images\lvm-multi-pv-1.raw `
  -SecondOutputPath .\.tmp\real-images\lvm-multi-pv-2.raw
```

スクリプトは各PVへ128 MiBずつ配置した256 MiBのlinear LVを作り、PV境界をまたぐ160 MiBファイルを書き込みます。`e2fsck -fn`、PV/VG/LV UUID、segmentと各ファイル・RAWのSHA-256を表示します。manifestでは`"deviceSet": "LVM2"`を指定し、2台目を`companionImages`へ登録してください。ランナーはLVを組み立て、内部ファイルのSHA-256まで検証します。

### LVM2 striped LV

同じWSL環境で、2個のPVに64 KiB単位でstripingする384 MiBのLVとext4を生成します。

```powershell
.\tools\New-LvmStripedRegressionFixture.ps1
```

スクリプトは2台のPVへ交互に配置される300 MiBファイルを作成し、`e2fsck -fn`、PV/VG/LV UUID、stripe数・stripe size・device割り当て、各ファイル・RAWのSHA-256を表示します。manifestでは`"deviceSet": "LVM2"`を指定し、2台目を`companionImages`へ登録してください。ランナーはmetadata header/text CRC、PV UUID、extent範囲を検証してLVを組み立て、内部ファイルのSHA-256まで確認します。

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
              "path": "/subvolume.txt",
              "expectedDirectory": false,
              "expectedLength": 17,
              "sha256": "765E8528AC299FB0EEA4CE08845B694C107F577FB39CC1C5558E82DD048074D8"
            },
            {
              "path": "/nested-subvol",
              "expectedDirectory": true
            },
            {
              "path": "/nested-subvol/nested.txt",
              "expectedDirectory": false,
              "expectedLength": 24,
              "sha256": "87B1422F3A4F5CD3F295930A9BC7A9C918138C2497E27A288760BE59A9C1A6A5"
            }
          ]
        }
      ]
    },
    {
      "name": "Btrfs RAID1 two-device fixture",
      "path": "%VDT_FIXTURE_ROOT%\\btrfs-raid1-1.raw",
      "sha256": "REPLACE_WITH_64_HEX_CHARACTERS",
      "companionImages": [
        {
          "path": "%VDT_FIXTURE_ROOT%\\btrfs-raid1-2.raw",
          "sha256": "REPLACE_WITH_64_HEX_CHARACTERS"
        }
      ],
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
              "expectedLength": 30,
              "sha256": "REPLACE_WITH_64_HEX_CHARACTERS"
            }
          ]
        }
      ]
    },
    {
      "name": "Linux md RAID1 fixture",
      "path": "%VDT_FIXTURE_ROOT%\\md-raid1-1.raw",
      "sha256": "REPLACE_WITH_64_HEX_CHARACTERS",
      "companionImages": [
        {
          "path": "%VDT_FIXTURE_ROOT%\\md-raid1-2.raw",
          "sha256": "REPLACE_WITH_64_HEX_CHARACTERS"
        }
      ],
      "deviceSet": "Linux md RAID1",
      "expectedPartitionCount": 1,
      "partitions": [
        {
          "number": 1,
          "expectedFileSystem": "ext4",
          "files": [
            {
              "path": "/hello.txt",
              "expectedDirectory": false,
              "sha256": "REPLACE_WITH_64_HEX_CHARACTERS"
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
