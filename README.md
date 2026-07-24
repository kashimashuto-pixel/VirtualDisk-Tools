# Virtual Disk Explorer

C# / Windows Forms で作成した、読み取り専用の仮想ディスク解析ツールです。
外部アプリは使用せず、商用利用しやすいライブラリだけを使う方針です。

## できること

- 仮想ディスクの概要表示
- Windows物理ディスク (`\\.\PhysicalDriveN`) の読み取り専用解析
- 仮想ディスクデータの Hex 表示
- MBR / GPT パーティション一覧の表示
- ファイルシステム検出と読み取り
  - FAT16 / FAT32
  - NTFS
  - exFAT
  - XFS
  - ext2 / ext3 / ext4
  - SquashFS
  - BitLocker/FVE はクリアキーを検出できる場合のみ内部 FS を読み取り
- LVM2 論理ボリュームの検出と読み取り
  - 通常のlinear構成（LVMメタデータ上は`striped`、`stripe_count = 1`）を読み取り
  - 読めない場合は不足PV、未対応segment type、複数stripe、メタデータ未検出、または内部例外を警告欄と解析レポートへ表示
- Linux md RAID の検出
- Windows Explorer風の左ツリー・右詳細一覧画面
- パーティション全体のファイル名検索（最大5,000件、キャンセル可能）
- ファイルの Hex プレビュー
- テキスト、Word (`.docx`)、Excel (`.xlsx` / `.xlsm`) の読み取り専用別窓プレビュー
  - Wordは本文と表、Excelはシートごとのセルを表示
  - Officeやマクロを起動せず、.NET標準のZIP/XML機能だけで解析
- 仮想ディスク内のファイル/フォルダをホスト側へコピー
  - コピーのキャンセル、エラー継続、SHA-256マニフェスト、エラーログ
- ディスク、パーティション、警告のJSON解析レポート保存
- NTFSの削除済みMFTレコード検出（実験的）
- ProjFS による読み取り専用のフォルダ投影型マウント
- qcow2 のdeflate/zstd圧縮クラスタ、backing file、external data file、Extended L2 Entriesの読み取り
- qcow2内部スナップショットの一覧表示と選択

## 対応ディスク形式

- qcow2 / qcow
- VHD
- VHDX
- VMDK
- VDI
- Parallels HDD / HDS (`.hdd` フォルダ、`.hds`)
- raw / dd / img
- lzop/LZO1X圧縮された `.dd.lzo` / `.img.lzo` / `.raw.lzo` / `.lzo`
  - 全体を一時展開せず、必要なブロックだけをオンデマンド展開
  - 読み込みと解析はバックグラウンドで行い、索引作成・ブロック展開の進捗を画面上部に表示
  - 展開後のMBR/GPT、ext4などを通常のrawディスクと同じ経路で解析

## 物理ディスク

ツールバーの「物理ディスク」から、Windowsが認識しているディスクを選択できます。

- 物理ディスクは常に読み取り専用で開き、書き込み用ハンドルは作成しません。
- Windowsの仕様上、物理ディスクの読み取りには管理者権限が必要です。権限がない場合は、確認後に `runas` で再起動して選択したディスクを引き継ぎます。
- MBR / GPT と既存の対応ファイルシステムを、ディスクイメージと同じ画面で解析できます。
- 512バイトおよび4Kn論理セクターのLBA計算に対応します。
- OSや別アプリが使用中のディスクは解析中にも内容が変化するため、表示が一時的に整合しない場合があります。

## 起動

```powershell
dotnet run --project src\Qcow2Explorer\Qcow2Explorer.csproj
```

Visual Studio で開く場合は `Qcow2Explorer.sln` を使ってください。

## テスト

テストは qemu-img などを使わず、最小 qcow2/raw イメージを C# で生成して確認します。

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj
```

任意のディスクイメージの構造確認:

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj -- "<image-path>"
```

小さいファイルのコピー確認も行う場合:

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj -- "<image-path>" --copy-smoke
```

## ProjFS マウント

ProjFS マウントは Windows の Client-ProjFS 機能を使い、選択したパーティションを既存フォルダ配下へ読み取り専用で投影します。
ドライブ文字を割り当てる実マウントではありません。

- Client-ProjFS が無効な場合、アプリから有効化コマンドを管理者権限で起動できます。
- マウント先フォルダは空のフォルダを選んでください。
- 解除時は ProjFS の仮想化ルートを通常フォルダへ戻す後処理を行います。
- アプリ終了時はマウント使用中の可能性を確認してから解除します。

## 現在の制限

- 読み取り専用です。ディスクイメージや内部ファイルシステムへの書き込みはしません。
- 物理ディスクを開くには管理者権限が必要です。使用中ディスクの一貫したスナップショットは作成しません。
- 暗号化qcow2（AES/LUKS）は未対応です。
- qcow2 external data fileは、ヘッダー拡張にファイル名があり、同じPCから参照できる場合に読み取ります。
- backing fileは相対パスまたは絶対パスから読み取ります。親イメージがない場合は開けません。
- Extended L2 Entriesは32サブクラスタの割り当て／ゼロビットマップを読み取ります。
- lzopの変換フィルター付きストリーム、multipartフラグ、ファイル自体を分割した複数パートは未対応です。
- ext4 の journal replay は行いません。
- SquashFS はライブラリが対応する圧縮形式のみ読み取れます。
- Linux md RAID は検出のみです。
- BitLockerはクリアキーまたは生FVEKを扱う内部処理がありますが、48桁の回復パスワード入力には未対応です。
- NTFS削除済みファイルはMFTに残っている情報を表示します。削除後に再利用されたクラスタの内容は復旧できません。
- LVM2 は、現在の入力内に必要なPVがすべてあり、LVが単一stripeのlinear相当である構成を読み取ります。
- 複数ディスクにまたがり一部PVが入力されていないVG、複数stripe、thin/snapshot/cache/mirror/RAID segmentは未対応です。検出できたメタデータから該当理由を表示します。
- Parallels HDD は単一 Storage の Plain / Compressed image を読み取ります。split image、未知の image type、仕様外の拡張は未対応です。
- ProjFS マウントはフォルダ投影型です。Windows のドライブ文字としての実マウントではありません。
- Office別窓プレビューは内容確認用です。Wordの画像・厳密なレイアウト・変更履歴、Excelの書式・グラフ・マクロ実行には対応しません。
- 旧バイナリOffice形式の `.doc` / `.xls` は別窓プレビュー対象外です。

## 依存ライブラリとライセンス

このプロジェクトは以下の NuGet パッケージを使用しています。

- `LTRData.DiscUtils.ExFat`
- `LTRData.DiscUtils.Lvm`
- `LTRData.DiscUtils.Ntfs`
- `LTRData.DiscUtils.SquashFs`
- `LTRData.DiscUtils.Vdi`
- `LTRData.DiscUtils.Vhd`
- `LTRData.DiscUtils.Vhdx`
- `LTRData.DiscUtils.Vmdk`
- `LTRData.DiscUtils.Xfs`
- `Microsoft.Windows.ProjFS`
- `ZstdSharp.Port`

これらは NuGet メタデータ上で MIT License として公開されています。
MIT License は著作権表示とライセンス表示の保持が必要なため、再配布時は下記の表示を含めてください。

### DiscUtils

Project: https://github.com/LTRData/DiscUtils

```text
Copyright (c) 2008-2011, Kenneth Bell
Copyright (c) 2014, Quamotion

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

### Microsoft.Windows.ProjFS

Project: https://github.com/microsoft/ProjFS-Managed-API

```text
ProjFS Managed API
MIT License
Copyright (c) Microsoft Corporation. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
IN THE SOFTWARE.
```

### ZstdSharp.Port

Project: https://github.com/oleg-st/ZstdSharp

```text
MIT License

Copyright (c) 2021 Oleg Stepanischev

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### lzokay由来のLZO1Xデコーダ

Project: https://github.com/AxioDL/lzokay

`Lzo1xDecoder.cs` は、外部アプリやGPLライブラリを組み込まずLZO1Xを展開するため、MITライセンスのlzokayデコーダをC#へ移植しています。

```text
The MIT License

Copyright (c) 2018 Jack Andersen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 参考

- qcow2 形式: https://www.qemu.org/docs/master/interop/qcow2.html
- Parallels HDD descriptor: https://www.qemu.org/docs/master/interop/prl-xml.html
- Parallels expandable image: https://www.qemu.org/docs/master/interop/parallels.html
- Home Assistant OS partition layout: https://developers.home-assistant.io/docs/operating-system/partition
- DiscUtils: https://github.com/LTRData/DiscUtils
- ProjFS: https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system
- Windows物理ディスクの直接アクセス: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
- 物理ディスクサイズ取得: https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_disk_get_length_info
- lzop形式: https://www.lzop.org/
- LZO1Xデコーダ移植元: https://github.com/AxioDL/lzokay
