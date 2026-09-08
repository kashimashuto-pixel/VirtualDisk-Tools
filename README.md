# Virtual Disk Explorer

C# / Windows Forms で作成した、通常は原本を変更しない仮想ディスク解析ツールです。実験的な物理ディスク直接編集だけは、厳格な確認後に選択した媒体へ差分を書き込みます。
外部アプリは使用せず、商用利用しやすいライブラリだけを使う方針です。

次回以降の対応候補と優先順位は、[NEXT_STEPS.md](NEXT_STEPS.md) にまとめています。

## できること

- 仮想ディスクの概要表示
- Windows物理ディスク (`\\.\PhysicalDriveN`) の通常時読み取り専用解析と、明示確認・復旧ジャーナル付きの実験的な直接編集
- ext4／XFS／FAT16／FAT32／NTFS／exFAT内のファイル内容変更（拡大・縮小を含む）／追加／削除、ディレクトリ作成／空ディレクトリ削除、ファイル・ディレクトリの移動／名前変更、属性・更新日時変更を行い、新しいRAWへ保存（実験的）
- 仮想ディスクデータの Hex 表示
- MBR / GPT パーティション一覧の表示
- ファイルシステム検出と読み取り
  - FAT16 / FAT32
  - NTFS
  - exFAT
  - XFS（従来形式・bigtimeの更新日時に対応）
  - Btrfs（単一／複数デバイスのsingle profile、RAID0／RAID1／RAID1C3／RAID1C4／RAID10、subvolume／snapshot／default subvolume、backup superblock復旧、インライン／通常／スパース／zlib・LZO・zstd圧縮extent、CRC32C検証に対応）
  - ext2 / ext3 / ext4
  - SquashFS
  - BitLocker/FVE はクリアキーの自動解除、48桁回復パスワード、通常パスワード、スタートアップキー（`.BEK`）による解除に対応
  - 回復パスワードは8組の6桁ブロックを検証し、VMK/FVEKを解除して内部FSを読み取り
  - `.BEK`はヘッダー・外部キー・保護子IDを検証し、対応するVMK/FVEKだけを解除
  - LUKS1 はAES-XTS/plain64、PBKDF2（SHA-1/SHA-256/SHA-512）、標準4000 AF stripesのパスフレーズ解除に対応
  - LUKS2 は冗長headerとJSON metadataを検証し、PBKDF2/Argon2id keyslot、単一crypt segment、AES-XTS/plain64のパスフレーズ解除に対応
- LVM2 論理ボリュームの検出と読み取り
  - 単一PV、複数PVにまたがるlinear構成、`stripe_count > 1`のstriped LV、および通常thin LVを読み取り
  - striped LVは`stripe_size`単位でPVを切り替え、PV UUID、extent割り当て、data area、metadata header/text CRCを検証
  - thin LVはthin-pool superblock、space map、device details、2段mapping B-treeのCRC32Cとtransaction IDを検証し、未割当blockをゼロとして読み取り
  - 「複数ディスク」入力からPVをVG UUIDで束ね、必要なPVが揃ったLVを組み立て
  - 読めない場合は不足PV、未対応segment type、メタデータ破損、メタデータ未検出、または内部例外を警告欄と解析レポートへ表示
- Linux md RAID0／RAID1／RAID10の検出・読み取り（metadata 1.x、RAID0 multi-zone、RAID10 near／far／offset、RAID1／RAID10 degraded対応）
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
- VHDX / Hyper-V checkpoint (`.vhdx` / `.avhdx`)
  - AVHDXから親VHDX／AVHDXを自動解決し、親由来データと各差分層を読み取り専用で合成
  - 各層のUnique ID、親Unique ID、容量、論理sector size、チェーン終端を検証し、概要に全レイヤーパスを表示
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
- Expert Witness Format / EnCase E01 (`.E01`、連番の`.E02`以降を自動検出)
  - EWF1/EVFのEnCase 6 `volume` / `sectors` / `table`構成を読み取り
  - deflate圧縮chunkと非圧縮chunkのAdler-32、section・tableのAdler-32を検証
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

- 物理ディスクは通常時には読み取り専用ハンドルだけで開きます。「物理ディスクへ適用」を明示的に実行した期間だけ、別の書き込み用ハンドルを作成します。
- Windowsの仕様上、物理ディスクの読み取りには管理者権限が必要です。権限がない場合は、確認後に `runas` で再起動して選択したディスクを引き継ぎます。
- MBR / GPT と既存の対応ファイルシステムを、ディスクイメージと同じ画面で解析できます。
- 512バイトおよび4Kn論理セクターのLBA計算に対応します。
- OSや別アプリが使用中のディスクは解析中にも内容が変化するため、表示が一時的に整合しない場合があります。

## 実験的なext4／XFS／FAT16／FAT32／NTFS／exFAT書き込み

エクスプローラーの「編集（実験）」から、通常ファイルの内容変更（拡大・縮小を含む）／追加／削除、ディレクトリ作成／空ディレクトリ削除、ファイル・ディレクトリの移動／名前変更、属性・更新日時変更を変更予定へ登録できます。「変更予定」タブでは登録順、入力または設定内容、入力サイズを確認し、選択項目／最後の操作／全操作を取り消せます。保存時は全操作を上から順に同じコピーオンライト領域へ適用し、新しいRAWイメージを1個生成します。

- 通常の保存では原本、物理ディスク、既存の出力ファイルへは書き込みません。変更はメモリ上のコピーオンライト領域へ保持し、最後に新しいRAWイメージとして保存します。
- 各操作の直後にファイルシステムを再オープンし、内容変更／追加／移動はサイズとSHA-256、作成／削除はパス状態、属性／更新日時は読み戻し値を検証します。全操作後も出力RAWを再オープンして最終状態を検証できた場合だけ完成名へ移動します。失敗・キャンセル時は完成ファイルを公開しません。
- 対象は各writerが安全に更新できる単純な構成に限定されます。dirty状態、メタデータ矛盾、スパース／未初期化extent、圧縮・暗号化・共有reflinkなどは形式に応じて事前に拒否します。XFSはv5 CRC、finobt／rmapbt、完全なinode chunk、shortform directory、単純なinline extent、正常unmount済みのclean logという制限があります。複雑なdirectory／extent btreeやreflinkは未対応です。
- FAT16／FAT32／exFAT／NTFSではReadOnly・Hidden・System・Archive属性、ext4／XFSではUnix modeへ対応付けたReadOnly属性を編集できます。ディレクトリ種別そのものは属性変更できません。
- RAID、LVM、BitLocker／LUKSなどの合成・復号パーティションでは、member、PV、暗号化コンテナーへは書き戻しません。選択した組み立て済み／復号済み論理ボリュームをコピーオンライトで編集し、平坦化した平文の論理RAWとして新規保存します。元のRAID/LVM構成や暗号化形式へ戻す機能ではないため、出力RAWの保管には注意してください。
- 物理ディスクから直接開いた通常パーティションは、「変更予定」タブの「物理ディスクへ適用」から同じディスクへ差分を直接適用できます。リムーバブル／ホットプラグと固定データディスクの両方に対応しますが、実行中Windowsのシステムディスクは拒否します。別のWindows環境からオフラインディスクとして接続した場合は固定ディスクとして扱えます。
- 直接適用では、対象の番号・容量・論理セクターサイズ・型番・接続種別・シリアル番号・Storage Device IDを再確認し、対象ディスクに属する全Windowsボリュームを排他ロックしてアンマウントします。シリアル番号とStorage Device IDの両方を取得できない場合、または1個でもボリュームをロックできない場合は書き込みません。
- 置換・追加するファイルは、排他ロック前に復旧ジャーナルと同じ安全なディスクへ一時退避します。退避元が書き込み対象ディスク上にあってもロック後に読み直さないためですが、その合計サイズ分の空き容量が必要です。一時退避は処理終了時に削除します。
- 書き込み前に全変更ページの原本一致を検証し、対象とは別のローカルディスクへ変更前・変更後ページとSHA-256を含む復旧ジャーナルを同期保存します。セクター境界へ揃えた差分だけを書き込み、ディスクへのflush、全差分の読み戻し、ファイルシステム最終状態の再検証後に完了扱いとします。
- 書き込み・最終検証に失敗した場合は、同じ排他セッション内で変更前ページを自動復旧して読み戻します。プロセス停止や停電で中断した場合は、ツールバーの「物理ディスク復旧」からジャーナルを選択できます。ただし媒体故障や書き込み中の停電に対して完全な原子性を保証するものではありません。
- qcow2、VHDXなどを開いた場合も、出力はコンテナー形式ではなく展開済みの論理ディスク全体を格納するRAWです。出力先には元の仮想ディスクと同程度の空き容量が必要です。
- 実験的機能のため、重要なイメージでは使用せず、出力RAWもLinux標準ツールで検査してから利用してください。

Linux／WSLで実ext4・XFS・FAT16・FAT32・NTFS・exFATイメージを生成し、編集後に`e2fsck -fn`、`xfs_repair -n`、`fsck.fat -vn`、`ntfsfix -n`、`fsck.exfat -n`、読み取り専用マウントと内容比較を行う検証スクリプトも用意しています。

```bash
sudo ./tools/new-write-regression-fixtures.sh /path/to/fixtures
# テストプロジェクトの --batch-edit-smoke で各形式の変更済みRAWを生成
# ディレクトリ・移動・属性・日時は --metadata-edit-smoke <source.raw> <output.raw> で確認
sudo ./tools/validate-write-regression-output.sh /path/to/fixtures
```

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

XFS bigtime、Btrfs、BitLocker、LUKS1/LUKS2、E01、LZOキャッシュなどを実環境由来イメージで回帰確認する場合は、
[実イメージ回帰テスト手順](tests/REAL_IMAGE_REGRESSION.md)を参照してください。
イメージとローカルmanifestはGitへ追加せず、SHA-256と期待値を照合して任意実行します。
BtrfsやLinux mdの複数デバイス構成はツールバーの「複数ディスク」から同じ構成に属するイメージをすべて選択します。先頭の選択ファイルを画面表示用ディスクとし、BtrfsはFSID・devid・device UUID、Linux mdはarray UUID・role・event・device UUIDを照合したcompanionを読み取り時に自動使用します。

## ProjFS マウント

ProjFS マウントは Windows の Client-ProjFS 機能を使い、選択したパーティションを既存フォルダ配下へ読み取り専用で投影します。
ドライブ文字を割り当てる実マウントではありません。

- Client-ProjFS が無効な場合、アプリから有効化コマンドを管理者権限で起動できます。
- マウント先フォルダは空のフォルダを選んでください。
- 解除時は ProjFS の仮想化ルートを通常フォルダへ戻す後処理を行います。
- アプリ終了時はマウント使用中の可能性を確認してから解除します。

## 現在の制限

- 通常の解析・コピー・マウントは読み取り専用です。実験的なext4／XFS／FAT16／FAT32／NTFS／exFAT編集は新規RAWを生成でき、物理ディスクから直接開いた通常パーティションでは厳格な確認後に差分を同じディスクへ適用できます。
- 物理ディスクを開くには管理者権限が必要です。使用中ディスクの一貫したスナップショットは作成しません。
- qcow2コンテナー自体の暗号化機能は未対応です。パーティションとして格納された対応形式のLUKS1/LUKS2は読み取れます。
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
- Btrfsは単一／複数デバイスのsingle profile、RAID0、2-copy RAID1、3-copy RAID1C3、4-copy RAID1C4、striped mirrorのRAID10を読み取り専用で扱い、DEVICE_ITEM、devid、FSID、device UUIDとstripeを相互検証します。mirror profileではmetadata tree blockのCRC32Cまたはdata checksumが一致するコピーだけを採用し、破損時は検証済みの代替コピーへ切り替えます。RAID0／RAID10はchunkごとの`stripe_length`に従ってdeviceを切り替え、RAID10はさらに`sub_stripes`単位でmirrorを選択します。複数RAWはツールバーから一組として指定でき、実イメージ回帰manifestでも各companionのSHA-256を検証します。RAID0は全stripe deviceが必要です。mirror profileはchunk単位ですべてのmirror組に利用可能なstripeが残る場合だけdegraded読み取りで開き、欠損devidを警告へ表示します。subvolume、snapshot、default subvolumeとzlib・LZO・zstd圧縮extentを読み取れ、snapshot内に残る入れ子subvolume境界は元subvolumeへ誤接続せず空ディレクトリとして表示します。RAID5／6、暗号化extent、書き込みは未対応です。
- Btrfsのsuperblock、metadata tree block、checksum treeに記録されたdata sectorのCRC32Cを検証し、不一致は読み取りを中止します。primary superblockが壊れている場合だけ、公式mirror位置（64 MiB／256 GiB）の検証済みbackupへ復旧し、primaryが有効なら常にprimaryを優先します。
- SquashFS はライブラリが対応する圧縮形式のみ読み取れます。
- Linux mdはmetadata 1.0／1.1／1.2のRAID0／RAID1／RAID10を読み取り専用で組み立てます。superblock checksum、array/device UUID、role、event counter、data/super offset、device範囲を検証し、同一eventのactive memberだけを使用します。RAID0はchunk単位のstriping、サイズが異なるmemberのmulti-zone original／alternate layoutに対応し、全active roleが揃わない場合は拒否します。RAID1は1台欠損、RAID10はnear／far／offset layoutとkernelのoriginal／legacy／fixed far-set配置を扱い、各mirror groupに1台以上残る構成をdegraded読み取りします。legacy far-setはLinux kernelと同じ配置を再現しますが、構成によって冗長性が低下するため、実際の論理chunkごとに利用可能なcopyを検証します。複数mirrorの内容が一致しなければ停止します。RAID4／5／6、reshape・recovery中、replacement、bad-block log付きarray、metadata 0.90は未対応です。
- BitLockerはAES-XTS（128/256）に対応します。TPM単独保護、TPMとの複合保護、AES-CBC/Elephant Diffuserは未対応です。
- BitLocker回復パスワード、通常パスワード、スタートアップキー、VMK、FVEKは設定・ログ・解析レポートへ保存しません。不要になったキー配列は可能な範囲で消去します。
- LUKS1はAES-XTS/plain64の256/512-bit合成キーに対応します。detached header、AES-CBC、ESSIV、plain/plain64以外のIV方式は未対応です。
- LUKS2は有効なprimary/secondary headerのうちsequence IDが新しいものを使用し、PBKDF2/Argon2id keyslot、標準4000 AF stripes、単一dynamic crypt segment、AES-XTS/plain64に対応します。Argon2idはmemory 1 GiB、time cost 10、parallelism 16を上限とし、実行時のメモリ余力も確認します。Argon2i/d、reencryption、複数・固定長segment、detached headerは未対応です。
- LUKS1/LUKS2パスフレーズとvolume keyは設定・ログ・解析レポートへ保存せず、一時配列と復号リーダーのキーを使用後に消去します。
- E01はEWF1/EVFのEnCase 6形式、deflateまたは非圧縮chunk、連続したsegmentに対応します。安全なメモリ使用のためchunk数上限は4,194,304です。EWF2/Ex01、bzip2、論理証拠ファイル（L01）、暗号化EWF、旧形式など異なるtable配置は未対応です。
- NTFSの主 `$MFT` 先頭レコードが破損している場合は `$MFTMirr` から復旧を試みます。ルートレコードなど主MFTの必須データ自体が欠落しているイメージは、元ディスクまたはバックアップからの再取得が必要です。
- NTFS削除済みファイルはMFTに残っている情報を表示します。削除後に再利用されたクラスタの内容は復旧できません。
- LVM2 は、単一または複数の入力ディスク内に必要なPVがすべてあるlinear／striped／通常thin LVを読み取ります。複数PVは各PVを「複数ディスク」で同時に指定すると組み立て、`stripe_size`、segment、thin data block境界をまたぐ読み取りに対応します。label、PV UUID、data/metadata area、metadata header/text CRC、最新seqno、extent範囲を検証し、1個のmetadata copyが壊れていても別PVの検証済み同世代copyから復旧します。thin-poolは4 KiB metadata block、superblock／B-tree／bitmap／indexのCRC32C、space-map割当、device ID、transaction ID、data block範囲を検証し、`needs-check`または未知のincompatibility flagがあるpoolを拒否します。
- 一部PVが入力されていないVG、thin snapshot／external origin、通常snapshot、cache、mirror、RAID segmentは未対応です。検出できたメタデータから該当理由を表示します。
- Parallels HDD は単一 Storage の Plain / Compressed image を読み取ります。split image、未知の image type、仕様外の拡張は未対応です。
- OVAは読み取り中に内容を一時フォルダへ展開するため、アーカイブ内のファイル容量と同程度の空き容量が必要です。一時ファイルはイメージを閉じると削除します。
- AVHDXは単体で完結せず、チェックポイント作成時の親VHDX／AVHDXチェーン全体が必要です。親ファイルが見つからない、移動後のパスを解決できない、Unique IDや容量が一致しない場合は読み取りを拒否します。実行中VMが使用しているファイルを直接開かず、整合した状態でチェーン全体をコピーしてから解析してください。
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
- `Konscious.Security.Cryptography.Argon2`
- `Konscious.Security.Cryptography.Blake2`

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

### Konscious.Security.Cryptography.Argon2 / Blake2

Project: https://github.com/kmaragon/Konscious.Security.Cryptography

```text
MIT License

Copyright (c) 2017 Keef Aragon

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
- BitLockerスタートアップキー保護子: https://learn.microsoft.com/en-us/powershell/module/bitlocker/add-bitlockerkeyprotector
- BitLocker/FVEメタデータ・鍵導出形式: https://github.com/libyal/libbde/blob/main/documentation/BitLocker%20Drive%20Encryption%20%28BDE%29%20format.asciidoc
- LUKS1 on-disk format: https://cdn.kernel.org/pub/linux/utils/cryptsetup/LUKS_docs/on-disk-format.pdf
- LUKS2 on-disk format: https://gitlab.com/cryptsetup/cryptsetup/-/blob/master/docs/on-disk-format-luks2.pdf
- Expert Witness Compression Format (EWF): https://github.com/libyal/libewf/blob/main/documentation/Expert%20Witness%20Compression%20Format%20%28EWF%29.asciidoc
- Btrfs on-disk format: https://btrfs.readthedocs.io/en/latest/dev/On-disk-format.html
- Linux Btrfs zstd implementation: https://github.com/torvalds/linux/blob/master/fs/btrfs/zstd.c
- Linux md metadata 1.x: https://github.com/torvalds/linux/blob/master/include/uapi/linux/raid/md_p.h
- Linux md RAID0 mapping: https://github.com/torvalds/linux/blob/master/drivers/md/raid0.c
- Linux md RAID10 mapping: https://github.com/torvalds/linux/blob/master/drivers/md/raid10.c
- LVM2 striped segment implementation: https://github.com/lvmteam/lvm2/blob/main/lib/striped/striped.c
- LVM2 thin segment implementation: https://github.com/lvmteam/lvm2/blob/main/lib/thin/thin.c
- Linux device-mapper thin metadata: https://github.com/torvalds/linux/blob/master/drivers/md/dm-thin-metadata.c
- thin-provisioning-tools metadata reader: https://github.com/jthornber/thin-provisioning-tools/tree/main/src/thin
- Zstandard compression format: https://github.com/facebook/zstd/blob/dev/doc/zstd_compression_format.md
- Argon2 reference implementation: https://github.com/P-H-C/phc-winner-argon2
- Konscious Argon2 for .NET: https://github.com/kmaragon/Konscious.Security.Cryptography
