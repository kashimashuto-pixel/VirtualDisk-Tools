# 実イメージ回帰テスト

実環境由来のディスクイメージはGitへ追加せず、ローカルmanifestから明示的に実行します。
manifestにはイメージのSHA-256、期待する形式・ファイルシステム・ファイル情報だけを記録します。

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj `
  --configuration Debug --no-build -- `
  --real-image-regression tests\real-images.local.json
```

`tests/real-images*.local.json`は`.gitignore`対象です。絶対パスや機密情報をGitへ追加しないでください。
イメージの`path`はmanifestからの相対パス、または`%VDT_FIXTURE_ROOT%`などの環境変数を使用できます。

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
      "partitions": [
        {
          "number": 1,
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

BitLocker回復パスワード自体はmanifestへ書かず、指定した環境変数から実行時だけ読み込みます。
ランナーは入力イメージのSHA-256を先に照合し、回復キーのバイト配列を使用後に消去します。
LZOキャッシュcaseでは、初回展開時間と再利用時間を表示し、任意でキャンセル後に部分ファイルが残らないことも確認します。
