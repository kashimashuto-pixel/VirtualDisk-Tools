# 次回対応予定

- 最終更新: 2026-08-25
- 基準ブランチ: `main`

この文書は、次回の開発作業へ引き継ぐための優先順位付きロードマップです。
本ソフトは引き続き、ディスクイメージと内部ファイルシステムを変更しない読み取り専用ツールとして実装します。

## 対応済み

### イメージ読み込み処理のキャンセル

大容量イメージを開いたとき、次の処理を途中で安全にキャンセルできるようにします。

- LZO高速モードの一時RAW展開
- LZO省容量モードの索引作成
- OVAの一時展開
- VMAの解析
- パーティションおよびファイルシステム解析

実装方針:

- 検索・コピーとは別の読み込み専用`CancellationTokenSource`を使用する
- 画面上部に「読み込みキャンセル」を表示する
- キャンセル確認を各長時間ループへ追加する
- キャンセルまたは失敗時は、作成途中の一時ファイルと専用一時フォルダーを削除する
- 新しいイメージの読み込みに失敗しても、可能な限り現在表示中のイメージを維持する
- アプリ終了時も同じキャンセル・後処理経路を利用する

完了条件:

- LZO展開中、OVA展開中、解析中の各段階でキャンセルできる
- キャンセル後に不完全な一時ファイルが残らない
- 検索キャンセル、コピーキャンセル、読み込みキャンセルが互いに干渉しない
- 自動テストでキャンセルと一時ファイル削除を確認できる

### BitLocker 48桁回復パスワード対応

- 回復パスワード保護子を検出した場合に、伏字表示と表示切替を備えた入力ダイアログを表示
- 8組の6桁ブロック、値の上限、11の倍数、末尾チェック数字を検証し、入力箇所を具体的に表示
- 回復パスワードからstretch keyを導出し、AES-CCMでVMK/FVEKを解除して既存のAES-XTS読み取りへ接続
- 誤った回復パスワードは再入力またはキャンセル可能
- 回復パスワード、VMK、FVEKは設定・ログ・解析レポートへ保存せず、一時キー配列と復号リーダーのキーを破棄時に消去
- 既知の回復パスワード、stretch-key、AES-CCM/XTS解除、誤入力、キャンセル、クリアキー回帰、破棄後アクセスを自動テストで確認

### BitLocker通常パスワード・スタートアップキー対応

- 通常パスワードのUTF-16LE二重SHA-256とstretch key導出に対応
- `.BEK`のversion 1ヘッダー、外部キーエントリ、256-bitキーデータを境界検証付きで解析
- `.BEK`識別子とスタートアップキー保護VMKの識別子が一致する場合だけAES-CCM解除を実行
- UIから解除方法を選び、`.BEK`をファイルダイアログで指定可能
- パスワード・外部キー・VMK・FVEKを使用後に消去し、設定・ログ・解析レポートへ保存しない
- Windows生成のXTS-AES 256 VHDXと`.BEK`で、Windows自身の解除と実イメージ回帰を確認

### LUKS1パスフレーズ対応

- version 1ヘッダー、8個のkey slot、payload/key material境界をbig-endianで厳格に検証
- AES-XTS/plain64の256/512-bit合成キーと512-byte sectorを読み取り専用で復号
- PBKDF2（SHA-1/SHA-256/SHA-512）、master key digest照合、標準4000 AF stripesのmergeに対応
- 伏字表示・表示切替・再試行を備えたパスフレーズ入力ダイアログを追加
- パスフレーズ、key slot派生キー、master keyを保存・ログ出力せず、使用後に一時配列を消去
- cryptsetup 2.7生成LUKS1 + ext4 fixtureをcryptsetup自身と本アプリの双方で解除して検証

### LUKS2 PBKDF2パスフレーズ対応（第1段階）

- 4096-byte binary header、primary/secondary metadata、sequence ID、SHA-256/SHA-512 checksumを検証
- JSON metadataの境界、zero padding、keyslot area、digest、単一dynamic crypt segmentを厳格に検証
- PBKDF2（SHA-1/SHA-256/SHA-512）、標準4000 AF stripes、AES-XTS/plain64の解除に対応
- primary破損時は検証済みsecondaryへ復旧し、両方が破損している場合は安全に拒否
- Argon2 keyslotはクラッシュや誤復号をせず、未対応理由を表示
- cryptsetup 2.7生成LUKS2 + ext4 fixtureをcryptsetup自身と本アプリの双方で解除して検証

### LUKS2 Argon2idパスフレーズ対応（第2段階）

- MIT・純managed・lane単位の連続メモリを使うKonscious Argon2idを固定versionで導入
- memory 1 GiB、time cost 10、parallelism 16のmetadata上限と実行前メモリ余力検査を追加
- 小容量Argon2id合成fixture、誤パスフレーズ、異常memory cost、秘密情報非出力を検証
- cryptsetup 2.7のcalibrated default（検証環境では1 GiB、7 passes、4 lanes）で生成・解除し、内部ext4を実イメージ回帰で確認
- NuGetの既知脆弱性・非推奨package監査、Debug/Release、self-contained配布を確認

### LZO高速モードのキャッシュ再利用

- 「終了時に削除」「検証済みキャッシュとして保持・再利用」「指定場所へ通常RAWとして保存」を選択可能
- 元LZOのフルパス、サイズ、更新日時、SHA-256と、RAWサイズ、完了状態をメタデータへ保存
- 条件がすべて一致したキャッシュだけを再利用し、元LZO更新、未完成メタデータ、RAW破損を検出した場合は再展開
- キャッシュ一覧、状態、使用容量、選択削除、未完成・破損項目の削除UIを追加
- キャッシュ作成または通常RAW保存のキャンセル・失敗時は、作成途中のファイルを清掃
- 初回展開、再利用、更新検知、未完成拒否、RAWサイズ破損、孤立キャッシュ、通常RAW保存、削除、キャンセルを自動テストで確認

### 品質改善

- DiscUtils系パッケージを`1.0.84`からNuGet上の最新`1.0.88`へ更新し、全テストを確認
- GitHub ActionsでWindows、.NET 10、フォーマット検査、Debugビルド、自動テストを実行
- LZOキャッシュの破損・途中切れ・異常状態に対する回帰テストを追加
- 実イメージをGitへ追加せず、SHA-256、形式、パーティション、ファイルシステム、ファイル内容・更新日時をmanifestで検証する任意実行ランナーを追加
- BitLocker回復パスワードはmanifestへ保存せず環境変数から読み込み、LZO実イメージは初回展開・再利用時間とキャンセル後の清掃を検証可能

### E01/EWF読み取り対応

- libyalの一次仕様を基に、外部native依存を追加しないmanaged readerを実装
- EWF1/EVF EnCase 6の分割segment、deflate/非圧縮chunk、section・table・chunkのAdler-32を検証
- 生成multipartテストと、`ewfacquire` / `ewfverify`由来fixtureのlogical SHA-256回帰を追加
- EWF2/Ex01、bzip2、L01、暗号化EWF、旧table配置は明示的に未対応

### Btrfs読み取り対応（第1段階）

- 公式on-disk formatとLinux UAPIを基に、primary superblock、system chunk array、chunk/root/FS/checksum treeを境界検証付きで解析
- 単一デバイス・single profileのインライン、通常、preallocated、スパースextentを読み取り専用で扱う
- superblockとmetadata tree block、およびchecksum treeに記録されたdata sectorのCRC32Cを検証
- 合成fixtureで階層・sector境界・スパース領域・時刻と、superblock/tree/data破損の拒否を自動テスト
- `mkfs.btrfs -m single -d single`由来fixtureを生成し、`btrfs check --readonly`と実イメージ回帰で確認
- zlib圧縮extentを128 KiBの展開上限、CRC32C先行検証、部分読出しキャッシュ付きで追加し、圧縮stream破損も拒否
- LZO圧縮extentの全体／segment長、4 KiB境界padding、128 KiB展開上限を検証し、通常・inline extentと破損payloadを回帰テスト
- zstd圧縮extentのframe header、window、content size、block境界、割当末尾paddingを検証し、通常・inline extentと破損payloadを回帰テスト
- `btrfs-progs 6.6.3`由来のzstd通常・inline extentを実イメージ回帰で確認
- tree IDとinode番号の複合参照へ移行し、subvolume間で重複するinode番号を分離
- ROOT_REF/ROOT_BACKREF、親DIR_INDEX、ROOT_ITEM世代を相互検証し、subvolume・入れ子subvolume・snapshotを読み取り
- root treeの`default` DIR_ITEMからdefault subvolumeを解決し、snapshot内の入れ子subvolume境界は空ディレクトリとして扱う
- `btrfs-progs 6.6.3`由来のdefault・入れ子subvolume・snapshotを実イメージ回帰で確認
- primaryが無効な場合だけ64 MiB／256 GiBのbackup superblockを検証し、同一FSIDの最新世代へ読み取り専用で復旧
- primary有効時はbackupの世代にかかわらずprimaryを優先し、backup間のFSID・同一世代tree state不一致は拒否
- primary checksum／magic、backup checksum、全superblock破損、primary優先を合成回帰テストで確認
- 複数デバイスのsuperblock dev_item、chunk tree DEVICE_ITEM、devid、FSID、device UUID、single profile stripeを相互検証
- 複数partition readerをdevidで経路選択し、不足device、別FS、重複devid、stripe UUID不一致を安全に拒否
- メタデータをdevid 1、通常・圧縮data extentをdevid 2へ置いた2デバイス合成fixtureで、reader順序に依存しない読み取りを確認
- 2-copy RAID1 chunkの2 stripeを異なるdeviceとして検証し、論理アドレスを各mirrorへ読み取り専用で写像
- metadata tree blockはCRC32Cとheader、data sectorはchecksum treeのCRC32Cをmirrorごとに検証し、片系不一致時は健全なmirrorへ切替
- RAID1の片系metadata/data破損からの復旧、両系破損、同一deviceの重複stripeを2デバイス合成fixtureで回帰確認
- ツールバーの「Btrfs複数RAW」で同一FSIDのRAW一式を選択し、画面上のprimary partitionを複数device readerで開く経路を追加
- 実イメージ回帰manifestへ`companionImages`を追加し、各RAWのSHA-256と全入力に共通するFSIDを検証
- 2台用`mkfs.btrfs -m single -d single`／`-m raid1 -d raid1` fixture生成スクリプトを追加し、両profileを`btrfs check --readonly`と実イメージ回帰で確認
- 欠損deviceのUUIDをDEVICE_ITEMで検証し、すべてのchunkに利用可能なstripeが残る場合だけdegraded読み取りを許可
- RAID1のどちらか1台だけ、および未使用deviceだけが欠損したsingle profileを実`mkfs.btrfs` fixtureで読み取り確認
- 残存RAID1 mirrorのmetadata/data破損と、欠損側にsingle chunkがある構成を合成fixtureで安全に拒否
- RAID1C3／RAID1C4の3・4コピーを異なるdeviceとして検証し、CRC32C／data checksumに基づくmirror選択を一般化
- C3で2台欠損、C4で3台欠損したdegraded読み取りと、C3の残存metadata破損拒否を合成fixtureで確認
- 実`mkfs.btrfs -m/-d raid1c3`／`raid1c4`生成スクリプトを追加し、全device入力と最大device欠損のmanifest回帰を実行
- RAID10の`stripe_length`単位のstripingと`sub_stripes`単位のmirror選択を実装し、chunkごとに異なるstripe組を読み取り
- 各mirror組に少なくとも1台が残る場合だけdegraded読み取りを許可し、片側破損時はCRC32C／data checksumで代替mirrorへ切替
- 4台の合成fixtureと実`mkfs.btrfs -m/-d raid10` fixtureで完全構成・2台欠損構成・LZO／zstd／subvolumeを回帰確認
- RAID0の`stripe_length`単位のdevice切替と物理範囲検証を追加し、全stripe deviceが揃わない構成は安全に拒否
- 2台の合成fixtureでstripe境界・checksum破損・device欠損を、実`mkfs.btrfs -m/-d raid0` fixtureで通常／圧縮extentとsubvolumeを回帰確認

### Linux md RAID1読み取り対応

- 「Btrfs複数RAW」を汎用の「複数ディスク」入力へ拡張し、RAW以外の既存対応イメージ形式もcompanionとして選択可能
- metadata 1.0／1.1／1.2のsuperblock位置、checksum、array/device UUID、role、event、data offsetと範囲を検証
- 同一eventのactive memberだけでRAID1を組み立て、1台欠損のdegraded読み取りとmirror内容不一致の拒否に対応
- RAID1上のパーティションテーブルまたは直接配置FSを既存のFAT／ext／XFS等の検出・読み取りへ接続
- checksum破損、旧event、mirror不一致、完全／degraded構成を合成fixtureで回帰確認
- 実`mdadm 4.3 --metadata=1.2` RAID1 + ext4を生成し、`e2fsck -fn`と完全／degraded manifest回帰を実行

### Linux md RAID0読み取り対応

- metadata 1.xのRAID0 layout feature、chunk size、deviceごとのdata sizeと物理範囲を検証
- Linux kernelのstrip zone生成と同じ規則で、同容量memberと異容量memberのmulti-zoneを構築
- original layout／alternate multi-zone layoutのchunk写像を読み取り専用readerへ実装
- 全active roleと同一eventが揃う場合だけ組み立て、欠損・旧event memberがあるarrayは安全に拒否
- 2台合成fixtureで入力順非依存、64 KiB境界、全論理領域、内部FAT16を検証
- 3台異容量合成fixtureでoriginal／alternate multi-zone layoutの差を回帰確認
- 実`mdadm 4.3 --metadata=1.2 --level=0 --chunk=64` + ext4を生成し、300 MiBのstripe横断ファイルをmanifest回帰で確認

### Linux md RAID10 near layout読み取り対応

- Linux kernelの`__raid10_find_phys`と同じnear copyのrole・device sector写像を実装
- near copies 2以上／far copies 1のlayoutを検証し、far／offset／far-set layoutは理由付きで拒否
- 各論理chunkのmirror groupに同一eventのactive memberが1台以上ある場合だけdegraded組み立てを許可
- 複数mirrorが入力されている場合は読み取った内容を比較し、不一致時は停止
- 4台完全構成、mirror groupごとに1台残したdegraded構成、mirror group欠損、mirror不一致を合成回帰
- 3台near-2構成でrole wrap時にcopy側のdevice sectorが進む写像を合成回帰
- 実`mdadm 4.3 --metadata=1.2 --level=10 --layout=n2 --chunk=64` + ext4を生成し、完全／2台degraded構成と300 MiBファイルをmanifest回帰で確認

### LVM2複数PV対応

- 「複数ディスク」で指定した全入力をDiscUtilsのVolumeManagerへ登録し、VGに必要なPVを横断して検出
- companion側のパーティションもLVM2検出・メタデータ診断対象へ追加
- 複数PVに複数segmentでまたがるlinear LVを既存のファイルシステム検出・読み取りへ接続
- 必要PVが不足している構成を安全に拒否し、メタデータ上の必要数を診断へ表示
- 2個の合成PVに分割したFAT16 LVで、PV境界をまたぐ読み取りと欠損PVを回帰確認
- 実`lvm2 2.03.16`で2個のPVにまたがるext4 LVを生成し、`e2fsck -fn`と160 MiBの境界横断ファイルをmanifest回帰で確認

### LVM2 striped LV対応

- LVM2 label、PV header、data/metadata area、metadata header CRC、active metadata text CRCを検証する組み込みreaderを追加
- 最新seqnoのVG metadataを選択し、同一seqnoのmetadata copyが不一致なら安全に拒否
- `stripe_count > 1`と`stripe_size`から論理offsetをPV・physical extentへ写像し、segment／stripe境界をまたぐ読み取りに対応
- PV UUID、`pe_start`、`pe_count`、segment連続性、stripe数、物理data area範囲を相互検証
- 片方のmetadata textが破損していても、別PVのCRC検証済みcopyを使ってLVを組み立て
- 2台64 KiB stripeの合成FAT16 fixtureで全体写像、境界、入力順、PV欠損、metadata copy破損、同一seqno不一致を回帰確認
- 実`lvm2 2.03.16`で2台・64 KiB stripe・384 MiB ext4 LVを生成し、300 MiBファイルをmanifest回帰で確認

## 次回の推奨作業

### 1. Linux md RAID10 far／offset layout

- near layoutの基盤を使い、far copyのstride、far set、offset mappingを段階的に追加する
- 実`mdadm --layout=f2`／`o2` fixtureとdegraded組み合わせを基準に安全条件を検証する

### 2. LVM2 thin volume

- thin-pool metadata LVのsuperblock、space map、mapping btreeを読み取り専用で検証する
- 最初に通常thin volume、その後snapshotと外部originを段階的に追加する

### 3. Linux md RAID5

- 全memberが揃った正常arrayの主要parity layoutから開始し、その後1台欠損のXOR復元を追加する
- dirtyかつdegradedなarrayなど、parityを安全に信頼できない状態は拒否する

## 保守・品質改善

機能追加と並行して、次を独立したコミットで行います。

- 破損・途中切れ・異常サイズの入力テストを増やす
- XFS bigtime、BitLockerなどの実イメージ回帰テストを追加する
- 外部由来の実イメージは、再配布条件と機密情報を確認してからテスト資産へ追加する

## 将来の対応形式候補

利用目的に応じ、次の順で検討します。

1. Linux md RAIDの拡張
   - RAID0／RAID1／RAID10 near対応を基盤に、RAID10 far/offset、その後RAID5/6を検討する
2. LVM2の拡張
   - striped対応を基盤に、thin、snapshot、cache、mirror、RAID segmentを段階的に対応する
3. 証拠・暗号化形式の拡張
   - EWF2/Ex01、LUKS detached header／複数segmentを検討する

## 維持する既存仕様

- ディスクイメージと内部ファイルシステムは読み取り専用
- 選択コピーと表示フォルダーコピーではハッシュ計算を行わない
- コピーはバックグラウンドキューで処理し、追加コピーで既存コピーをキャンセルしない
- 検索、コピー、読み込みには独立したキャンセルトークンを使用する
- コピー中はプログレスバー、転送速度、推定残り時間を表示する
- 動作中の処理を、新しいビルドやテストのために強制終了しない

## 次回開始時の確認手順

1. `git fetch origin`を実行し、`main`と`origin/main`の差分を確認する
2. 作業ツリーにユーザーの未コミット変更がないか確認する
3. `dotnet build Qcow2Explorer.sln --configuration Debug`を実行する
4. `dotnet run --project tests\Qcow2Explorer.Tests\Qcow2Explorer.Tests.csproj --configuration Debug --no-build`を実行する
5. `tests/REAL_IMAGE_REGRESSION.md`に従い、利用可能な実イメージfixtureをローカルmanifestへ登録する
