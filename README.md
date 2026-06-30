# Virtual Disk Explorer

C# / Windows Forms で作成した、読み取り専用の仮想ディスク解析ツールです。
外部アプリは使用せず、商用利用しやすいライブラリだけを使う方針です。

## できること

- 仮想ディスクの概要表示
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
- Linux md RAID の検出
- 読み取り専用エクスプローラー画面
- ファイルの Hex プレビュー
- 仮想ディスク内のファイル/フォルダをホスト側へコピー
- ProjFS による読み取り専用のフォルダ投影型マウント
- qcow2 の deflate 圧縮クラスタ読み取り

## 対応ディスク形式

- qcow2 / qcow
- VHD
- VHDX
- VMDK
- raw / dd / img
- dd.lzo は検出のみです。現在はアプリ内 LZO 展開に未対応です。

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
- qcow2 の zstd 圧縮クラスタ、暗号化、external data file、Extended L2 Entries は未対応です。
- backing file がある qcow2 は開けますが、未割り当て領域は backing file ではなく 0 として扱います。
- dd.lzo はアプリ内展開に未対応です。
- ext4 の journal replay は行いません。
- SquashFS はライブラリが対応する圧縮形式のみ読み取れます。
- Linux md RAID は検出のみです。
- LVM2 は DiscUtils が扱える単一/複数物理ボリューム構成の範囲で対応します。
- ProjFS マウントはフォルダ投影型です。Windows のドライブ文字としての実マウントではありません。

## 依存ライブラリとライセンス

このプロジェクトは以下の NuGet パッケージを使用しています。

- `LTRData.DiscUtils.ExFat`
- `LTRData.DiscUtils.Lvm`
- `LTRData.DiscUtils.Ntfs`
- `LTRData.DiscUtils.SquashFs`
- `LTRData.DiscUtils.Vhd`
- `LTRData.DiscUtils.Vhdx`
- `LTRData.DiscUtils.Vmdk`
- `LTRData.DiscUtils.Xfs`
- `Microsoft.Windows.ProjFS`

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

## 参考

- qcow2 形式: https://www.qemu.org/docs/master/interop/qcow2.html
- Home Assistant OS partition layout: https://developers.home-assistant.io/docs/operating-system/partition
- DiscUtils: https://github.com/LTRData/DiscUtils
- ProjFS: https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system
