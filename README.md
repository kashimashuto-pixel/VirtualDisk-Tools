# Qcow2 Explorer

C# / Windows Forms で作成した、読み取り専用の qcow2 解析ツールです。
外部アプリは使用していません。ライブラリは商用利用しやすいものだけを選ぶ方針です。

## できること

- qcow2 ヘッダー情報の表示
- L1/L2 テーブルを使った仮想ディスクデータの Hex 表示
- MBR / GPT パーティション一覧の表示
- ファイルシステム検出
  - NTFS
  - FAT16 / FAT32
  - XFS
  - ext2 / ext3 / ext4
  - SquashFS
  - exFAT は検出のみ
  - LVM2 PV / Linux md RAID は検出のみ
- 読み取り専用エクスプローラー画面
  - FAT16 / FAT32
  - NTFS の通常ファイル
  - XFS
  - ext2 / ext3 / ext4 の extents / 基本 block pointer
  - SquashFS は対応圧縮形式のみ
- ファイルの Hex プレビュー
- qcow2 内のファイル/フォルダをホスト側へコピー
- qcow2 の deflate 圧縮クラスタ読み取り

## 起動

```powershell
dotnet run --project src\Qcow2Explorer\Qcow2Explorer.csproj
```

Visual Studio で開く場合は `Qcow2Explorer.sln` を使ってください。

## テスト

テストは qemu-img などを使わず、最小 qcow2 イメージを C# で生成して確認します。

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj
```

任意の qcow2 の構造確認:

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj -- "C:\path\to\image.qcow2"
```

小さいファイルのコピー確認も行う場合:

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj -- "C:\path\to\image.qcow2" --copy-smoke
```

## HAOS 18.0 qcow2 での確認

`C:\Users\multi\Downloads\haos_ova-18.0.qcow2\haos_ova-18.0.qcow2` で確認した結果:

- GPT 8 パーティションを認識
- `hassos-boot`: FAT16 として一覧表示、`CMDLINE.TXT` のコピー成功
- `hassos-overlay`: ext4 として一覧表示
- `hassos-data`: ext4 として `docker` / `supervisor` などを一覧表示、`engine-id` のコピー成功
- `hassos-kernel0`: SquashFS + LZO 圧縮を検出。ただし現在は LZO SquashFS の展開未対応
- `hassos-system0` / `hassos-kernel1` / `hassos-system1` / `hassos-bootstate`: 先頭が 0 埋めで、通常ファイルシステムとしては未検出

## AlmaLinux 10 GenericCloud qcow2 での確認

`C:\Users\multi\Downloads\AlmaLinux-10-GenericCloud-latest.x86_64.qcow2` で確認した結果:

- qcow2 の deflate 圧縮クラスタを展開して GPT を認識
- GPT 4 パーティションを認識
- `EFI System Partition`: FAT16 として一覧表示、`BOOTX64.CSV` のコピー成功
- `boot`: XFS として検出。現時点のサンプルでは root entries 0
- `root`: XFS として `/etc` / `/root` / `/usr` / `/var` などを一覧表示、`/root/.bash_logout` のコピー成功
- `biosboot`: BIOS boot 用のため通常ファイルシステムではありません

## 現在の制限

- 読み取り専用です。qcow2 や内部ファイルシステムへの書き込みはしません。
- qcow2 の deflate 圧縮クラスタは対応済みです。
- qcow2 の zstd 圧縮クラスタ、暗号化、external data file、Extended L2 Entries は未対応です。
- backing file がある qcow2 は開けますが、未割り当て領域は backing file ではなく 0 として扱います。
- NTFS は MFT を先頭から最大 250,000 レコードまで読みます。
- ext4 の journal replay は行いません。
- XFS の symlink は解決できない場合があります。その場合は 0 バイト項目として表示されることがあります。
- LVM2 PV / Linux md RAID は検出のみです。論理ボリュームや RAID アレイの展開は未実装です。
- SquashFS の LZO 圧縮は未対応です。
- Windows のドライブ文字としての実マウントは未実装です。

## ライブラリ

- `LTRData.DiscUtils.SquashFs` を SquashFS 読み取り用に使用しています。
- `LTRData.DiscUtils.Xfs` を XFS 読み取り用に使用しています。
- DiscUtils は MIT License です。
- LZO SquashFS 対応は追加調査中です。`lzo.net` は MIT ですが、SquashFS の raw LZO ブロックにはそのまま接続できませんでした。

## マウント機能の調査メモ

実マウントはユーザーモード C# だけでは完結せず、ファイルシステムドライバが必要です。
現時点では、今回追加したホスト側コピー機能を優先するのが実用的です。

- Dokan: MIT / LGPL 系。商用利用はしやすい候補ですが、ドライバのインストールが必要です。
- WinFsp: GPLv3 + FLOSS 例外。非 GPL の商用製品に組み込む場合は商用ライセンス確認が必要です。ドライバのインストールが必要です。
- Microsoft Projected File System / ProjFS: Windows 標準機能として使える可能性がありますが、ドライブ文字のマウントというより、既存フォルダ配下に仮想ファイルを投影する方式です。
  - この環境では `projectedfslib.dll` が見つからず、`Client-ProjFS` の状態確認/有効化にも管理者権限が必要でした。そのため実行テストは未実施です。

## 参考

- qcow2 形式: https://www.qemu.org/docs/master/interop/qcow2.html
- Home Assistant OS partition layout: https://developers.home-assistant.io/docs/operating-system/partition
- DiscUtils: https://github.com/LTRData/DiscUtils
- Dokan: https://dokan-dev.github.io/
- WinFsp: https://winfsp.dev/
- ProjFS: https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system
