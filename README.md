# Virtual Disk Explorer

C# / Windows Forms で作成した、読み取り専用の仮想ディスク解析ツールです。
外部アプリは使用せず、商用利用しやすいライブラリだけを使う方針です。

次回以降の対応候補と優先順位は、[NEXT_STEPS.md](NEXT_STEPS.md) にまとめています。

## できること

- 仮想ディスクの概要表示
- Windows物理ディスク (`\\.\PhysicalDriveN`) の読み取り専用解析
- 仮想ディスクデータの Hex 表示
- MBR / GPT パーティション一覧の表示
- ファイルシステム検出と読み取り
  - FAT16 / FAT32
  - NTFS
  - exFAT
  - XFS（従来形式・bigtimeの更新日時に対応）
  - ext2 / ext3 / ext4
  - SquashFS
  - BitLocker/FVE はクリアキーの自動解除と48桁回復パスワードによる解除に対応
  - 回復パスワードは8組の6桁ブロックを検証し、VMK/FVEKを解除して内部FSを読み取り
- LVM2 論理ボリュームの検出と読み取り
  - 通常のlinear構成（LVMメタデータ上は`striped`、`stripe_count = 1`）を読み取り
  - 読めない場合は不足PV、未対応segment type、複数stripe、メタデータ未検出、または内部例外を警告欄と解析レポートへ表示
- Linux md RAID の検出
- Windows Explorer風の左ツリー・右詳細一覧画面
  - `Alt + ↑`で親フォルダー、`Alt + ←`で戻る、`Alt + →`で進む
  - 画面上の「戻る」「進む」「上へ」ボタンでも同じ操作が可能
  - 一覧の「場所」に仮想ディスク内のフルパスを表示
  - 一覧または検索結果を右クリックして、保存されているフォルダーへ移動可能
- パーティション全体のファイル名検索（最大5,000件、キャンセル可能）
- イメージ読み込みのキャンセル
  - LZO索引・一時RAW展開、OVA展開、VMA索引、パーティション／ファイルシステム解析を安全に中止
  - キャンセルまたは失敗時は作成途中の専用一時フォルダーを削除し、現在表示中のイメージを維持
- ファイルの Hex プレビュー
- エクスプローラー一覧でEnterを押すと、フォルダーを開くかファイルをプレビュー
- テキスト、Word (`.docx`)、Excel (`.xlsx` / `.xlsm`) の読み取り専用別窓プレビュー
  - 拡張子が不明でも、内容から安全にテキストと判定できたファイルは別窓表示
  - UTF-8 / UTF-16 / UTF-32 / Shift-JISなどを判定し、バイナリと判断した場合は従来のHexプレビューへ戻る
  - Wordは本文と表、Excelはシートごとのセルを表示
  - Officeやマクロを起動せず、.NET標準のZIP/XML機能だけで解析
- 仮想ディスク内の選択項目または表示フォルダをホスト側へバックグラウンドコピー
  - 追加コピーを待機キューへ登録可能
  - コピー中のプログレスバー、累積転送量、平均転送速度、推定残り時間を表示
  - 検索とは独立したコピーキャンセル、エラー継続、エラーログ
- ディスク、パーティション、警告のJSON解析レポート保存
- NTFSの削除済みMFTレコード検出（実験的）
- ProjFS による読み取り専用のフォルダ投影型マウント
- qcow2 のdeflate/zstd圧縮クラスタ、backing file、external data file、Extended L2 Entriesの読み取り
- qcow2内部スナップショットの一覧表示と選択
- Proxmox VMA内の複数仮想ディスクの一覧表示と選択
- Proxmox `efidisk` / EDK II OVMF変数ストアの読み取り
  - 通常形式と認証付き形式のUEFI変数を一覧表示
  - 現行変数と削除済み・履歴レコードの表示切り替え
  - `BootOrder`、`Boot####`、Secure Boot状態、PK/KEK/db/dbx署名リストを解釈
  - X.509証明書情報と未解析データのHex表示
- Proxmox `tpmstate` / swtpm線形状態ストアの読み取り
  - Permanent / Volatile / Save stateの割り当て状況を表示
  - 内部Blobのversion、フラグ、全長、TLV構成、SHA-256、先頭Hexを表示
  - ローカル鍵・移行鍵による暗号化と128-bit / 256-bit鍵フラグを判定
  - 暗号化されている場合は、コンテナー解析が可能でも復号にswtpmの対応鍵が必要であることを表示

## 対応ディスク形式

- qcow2 / qcow
- VHD
- VHDX
- VMDK
- VDI
- OVA (`.ova`)
  - tarアーカイブを安全な一時フォルダへ展開し、OVFが参照するVMDKなどを既存の読み取り処理で解析
  - 複数の仮想ディスクを含む場合は、ツールバーの「OVAディスク」から切り替え
- Parallels HDD / HDS (`.hdd` フォルダ、`.hds`)
- Proxmox VMA (`.vma` / `.vma.lzo`)
  - VMAヘッダーとエクステントのMD5を検証
  - 最大容量の仮想ディスクを初期選択し、ツールバーの「VMAディスク」から格納ディスクを切り替え
  - 疎な4 KiBブロックを元の仮想ディスク位置へ読み取り専用で復元
- raw / dd / img
- lzop/LZO1X圧縮された `.dd.lzo` / `.img.lzo` / `.raw.lzo` / `.lzo`
  - 開く際に、全体を一時RAWへ展開する「高速モード」と必要なブロックだけを展開する「省容量モード」を選択
  - 高速モードでは「終了時に削除」「検証済みキャッシュとして保持・再利用」「指定場所へ通常RAWとして保存」を選択
  - キャッシュは元LZOのフルパス、サイズ、更新日時、SHA-256、展開RAWサイズ、完了状態が一致する場合だけ再利用
  - 読み込みダイアログの「キャッシュ管理」から一覧、状態、使用容量を確認し、選択項目や未完成・破損キャッシュを削除
  - 一時・キャッシュ・通常RAWの保存先を指定でき、展開前に必要領域を事前確認・確保
  - 読み込みと解析はバックグラウンドで行い、一時RAW展開・索引作成・ブロック展開の進捗を画面上部に表示
  - キャッシュ作成または通常RAW保存のキャンセル・失敗時は、作成途中のRAWを再利用せず削除
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

## Windows配布パッケージ

`v`で始まるタグ（例: `v1.0.0`）をpushすると、GitHub ActionsがWindows x64向けの自己完結ZIPとSHA-256ファイルを作成し、GitHub Releaseへ追加します。
自己完結パッケージの実行に.NET SDKや.NETランタイムは不要です。

ローカルで同じ形式のパッケージを作成する場合:

```powershell
dotnet restore Qcow2Explorer.sln --runtime win-x64
.\tools\New-WindowsReleasePackage.ps1 -Version 1.0.0
```

既定の出力先は`.tmp/release`です。生成ツールは既存の出力先を上書きしません。
配布前に`.zip.sha256`の値とダウンロードしたZIPの`Get-FileHash -Algorithm SHA256`を照合してください。
現在の配布物はコード署名されていないため、Windowsが発行元の警告を表示する場合があります。

## テスト

テストは qemu-img などを使わず、最小 qcow2/raw イメージを C# で生成して確認します。

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj
```

任意のディスクイメージの構造確認:

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj -- "<image-path>"
```

VMA内の特定デバイスを番号で選択する場合:

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj -- "<archive.vma.lzo>" --vma-device=3
```

小さいファイルのコピー確認も行う場合:

```powershell
dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj -- "<image-path>" --copy-smoke
```

XFS bigtime、BitLocker回復パスワード、LZOキャッシュなどを実環境由来イメージで回帰確認する場合は、
[実イメージ回帰テスト手順](tests/REAL_IMAGE_REGRESSION.md)を参照してください。
イメージとローカルmanifestはGitへ追加せず、SHA-256と期待値を照合して任意実行します。

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
- lzop高速モードは、展開後の仮想ディスクと同程度の一時空き容量が必要です。
- lzopキャッシュ再利用時も元ファイル変更の誤判定を避けるため、圧縮ファイル全体のSHA-256を計算します。再展開はしませんが、元LZOの順次読み取りは行います。
- lzopの変換フィルター付きストリーム、multipartフラグ、ファイル自体を分割した複数パートは未対応です。
- VMAのVMメモリ状態 (`vmstate`) は仮想ディスク一覧から除外します。VMA内のディスクイメージ抽出や書き戻しは行いません。
- UEFI変数ストアは読み取り専用です。変数、起動順序、Secure Boot鍵データの追加・削除・書き換えは行いません。
- Secure BootのPK/KEK/dbに通常保存される公開鍵・証明書は表示できますが、署名用の秘密鍵を抽出する機能ではありません。
- swtpm状態ストアは読み取り専用です。外側の線形ストアと内側のBlob/TLVを検証して表示しますが、TPM状態の変更や書き戻しは行いません。
- 暗号化されたswtpm状態は、暗号方式と必要な鍵長までは判定できます。swtpmで設定されたファイル鍵または移行鍵がない場合、内部状態は復号できません。
- 平文のTPM状態データは存在と構造を表示できますが、libtpmsのversion依存な内部構造を秘密鍵単位まで展開する機能ではありません。
- ext4 の journal replay は行いません。
- SquashFS はライブラリが対応する圧縮形式のみ読み取れます。
- Linux md RAID は検出のみです。
- BitLockerはAES-XTS（128/256）に対応します。TPM単独保護、パスワード保護、スタートアップキー、AES-CBC/Elephant Diffuserは未対応です。
- BitLocker回復パスワード、VMK、FVEKは設定・ログ・解析レポートへ保存しません。不要になったキー配列は可能な範囲で消去します。
- NTFSの主 `$MFT` 先頭レコードが破損している場合は `$MFTMirr` から復旧を試みます。ルートレコードなど主MFTの必須データ自体が欠落しているイメージは、元ディスクまたはバックアップからの再取得が必要です。
- NTFS削除済みファイルはMFTに残っている情報を表示します。削除後に再利用されたクラスタの内容は復旧できません。
- LVM2 は、現在の入力内に必要なPVがすべてあり、LVが単一stripeのlinear相当である構成を読み取ります。
- 複数ディスクにまたがり一部PVが入力されていないVG、複数stripe、thin/snapshot/cache/mirror/RAID segmentは未対応です。検出できたメタデータから該当理由を表示します。
- Parallels HDD は単一 Storage の Plain / Compressed image を読み取ります。split image、未知の image type、仕様外の拡張は未対応です。
- OVAは読み取り中に内容を一時フォルダへ展開するため、アーカイブ内のファイル容量と同程度の空き容量が必要です。一時ファイルはイメージを閉じると削除します。
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
- Proxmox VMA 形式: https://github.com/proxmox/pve-qemu/blob/master/vma_spec.txt
- EDK II UEFI変数形式: https://github.com/tianocore/edk2/blob/master/MdeModulePkg/Include/Guid/VariableFormat.h
- swtpm線形ストア形式: https://github.com/stefanberger/swtpm/blob/master/src/swtpm/swtpm_nvstore_linear.h
- swtpm Blob/TLV形式: https://github.com/stefanberger/swtpm/blob/master/src/swtpm/swtpm_nvstore.c
- swtpm TLV定義: https://github.com/stefanberger/swtpm/blob/master/src/swtpm/tlv.h
- EDK II Firmware Volume形式: https://github.com/tianocore/edk2/blob/master/MdePkg/Include/Pi/PiFirmwareVolume.h
- Parallels HDD descriptor: https://www.qemu.org/docs/master/interop/prl-xml.html
- Parallels expandable image: https://www.qemu.org/docs/master/interop/parallels.html
- Home Assistant OS partition layout: https://developers.home-assistant.io/docs/operating-system/partition
- DiscUtils: https://github.com/LTRData/DiscUtils
- ProjFS: https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system
- Windows物理ディスクの直接アクセス: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
- 物理ディスクサイズ取得: https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_disk_get_length_info
- lzop形式: https://www.lzop.org/
- LZO1Xデコーダ移植元: https://github.com/AxioDL/lzokay
- BitLocker回復パスワードの検証規則: https://learn.microsoft.com/en-us/windows/win32/secprov/protectkeywithnumericalpassword-win32-encryptablevolume
- BitLocker/FVEメタデータ・鍵導出形式: https://github.com/libyal/libbde/blob/main/documentation/BitLocker%20Drive%20Encryption%20%28BDE%29%20format.asciidoc
