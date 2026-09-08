# 次回対応予定

- 最終更新: 2026-09-08
- 基準ブランチ: `experimental/write-support`

この文書は、次回の開発作業へ引き継ぐための優先順位付きロードマップです。
通常の解析は読み取り専用を維持し、書き込み機能は原本を変更しないコピーオンライト方式で段階的に実装します。

## 書き込み対応ロードマップ

### 対応済み: ext4／XFS／FAT16／FAT32／NTFS／exFATの同サイズファイル置換

- 原本を変更しない64 KiBページ単位のコピーオンライトブロックデバイス
- ext4 extent／legacy block pointerとXFS inline／B+tree extentの物理位置へ内容を書き込み
- スパース、未初期化extent、XFS realtime／共有reflink、暗号化などを事前に拒否
- XFS logのcycleからheadを確認し、直前の正常unmount recordを認識できる単純なclean状態だけを許可（未回収logと複雑な循環状態は安全側で拒否）
- 置換後の仮想ファイルをSHA-256で読み戻し検証してから、新規RAWへ原子的に保存
- 物理ディスク、RAID、LVM、復号パーティション、既存出力の上書きを拒否
- C#生成ext4 fixtureの原本不変・保存後再読込テスト
- Linux生成ext4／XFSで`e2fsck -fn`、`xfs_repair -n`、読み取り専用マウント後の内容一致を検証
- FAT16／FAT32はclean状態、複数FAT copyの一致、cluster chainの範囲・loop・長さを検証して割り当て済みdataだけを置換
- C#合成FAT16／FAT32とLinux生成fixtureを`fsck.fat -vn`、読み取り専用マウント、内容一致で検証
- NTFSはclean volume、単一MFT recordの非resident通常data、runlist全体、`$Bitmap`割り当てを検証し、resident／圧縮／暗号化／sparse／attribute listを拒否
- C#合成NTFSと`ntfs-3g 2022.10.3`生成fixtureを、`ntfsfix -n`、読み取り専用マウント、内容一致で検証
- exFATはdirty／media-failure状態とサイズを事前検証し、書き込み可能streamをコピーオンライトだけへ公開
- 実`exfatprogs 1.2.2`生成fixtureを`fsck.exfat -n`、読み取り専用マウント、内容一致で検証

### 次段階

1. ext4の空きblock／inode bitmap、group descriptor、superblock checksumを更新し、新規ファイル作成とサイズ変更へ対応
2. ext4 directory entry、link count、extent tree、journalを安全に更新し、削除／移動へ対応
3. XFS allocation groupのfree-space／inode btree、rmap／refcount、directory、inode CRC、log更新へ対応
4. XFSの新規作成、サイズ変更、削除、reflink CoWへ段階的に対応
5. RAW以外のコンテナーを同形式で保存するwriterを追加

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

### Linux md RAID10 far／offset layout読み取り対応

- Linux kernelの`__raid10_find_phys`と同じfar copy stride、offset配置、far-set wrapを実装
- original／legacy／fixed far-set modeを解釈し、near×far copy数、component size、全物理mapping範囲を検証
- 論理chunkごとのcopy roleを使ってdegraded可否を判定し、入力された複数copyの内容が一致しなければ停止
- far、offset、legacy／fixed far-set、無効なfar-set modeを合成fixtureで検証
- 実`mdadm 4.3 --layout=f2`／`o2`の2台構成を生成し、完全／1台degraded構成と160 MiBファイルをmanifest回帰で確認

### Linux md RAID5読み取り対応（第1段階）

- metadata 1.xのRAID5 level、3台以上のmember、chunk size、resync完了状態、物理data範囲を検証
- left/right asymmetric、left/right symmetric、parity-first、parity-last layoutの正常arrayを読み取り専用で写像
- 1台欠損時はXORで欠損data chunkを復元し、2台以上欠損または未対応layoutは安全に拒否
- 合成FAT16 fixtureで完全構成、1台欠損、2台欠損、未対応layoutを自動テスト定義に追加

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

### LVM2 thin volume対応

- hiddenな`pool_tmeta`／`pool_tdata`を検証済みlinear／striped readerで組み立て、visible thin LVへ接続
- 4 KiB thin-pool superblock、version、transaction ID、data block size、metadata/data space-map rootsを検証
- B-tree、metadata index、bitmapのCRC32C、block number、entry geometry、昇順key、循環、深さ、space-map割当を検証
- device detailsと2段mapping B-treeからlogical blockをpool data blockへ写像し、未割当blockをゼロとして読み取り
- 合成FAT16 fixtureで全体写像、64 KiB境界、未割当block、superblock／B-tree／space-map破損、transaction不一致、範囲外／未割当data blockを回帰確認
- 実`lvm2 2.03.16`／`thin-provisioning-tools 0.9.0`で384 MiB thin LV + ext4を生成し、`thin_check`と160 MiBファイルのmanifest回帰を確認

## 次回の推奨作業

### 1. Linux md RAID5

- 実`mdadm 4.3 --level=5` fixtureを生成し、主要parity layout、完全構成、1台degraded構成をmanifest回帰で確認する
- dirtyかつdegradedなarrayなど、parityを安全に信頼できない状態の実metadata表現を確認し、明示的に拒否する

### 2. LVM2 thin snapshot／external origin

- 通常thin volumeの2段mapping B-treeを再利用し、snapshot固有の共有mappingを実fixtureで検証する
- external originは未割当blockをorigin LVへフォールバックし、origin長とpool block sizeを検証する

### 3. LVM2 snapshot／cache／mirror segment

- thin volumeの基盤を再利用してthin snapshotと外部originを段階的に追加する
- 通常snapshot、cache、mirror、RAID segmentは個別にmappingと欠損device条件を検証する

### 4. XFS Raw Readerの回帰強化

- format-3 inodeのbmap B+treeを含む合成XFS fixtureを追加し、inline extent、B+tree、sparse/unwritten extent、複数回・範囲横断読み取りを自動テストする
- 30 GiB級、2 GiB超の単一extent、`int.MaxValue`超のファイルoffsetを含むfixtureで、block countとbyte offsetの計算が64-bitで行われることを回帰確認する
- 実XFSイメージでRaw ReaderとLinuxの読み取り結果をファイル単位SHA-256で比較する任意回帰手順を追加する
- DiscUtils XFS fallbackは、Raw Readerが解決済みのnodeを読み直さない経路を優先し、fallback失敗時もRaw Readerの読み取りを妨げないようにする

### 5. LZO省容量モードのindex cache管理

- 圧縮LZO block index sidecarの使用容量、作成日時、元ファイル検証状態を一覧表示し、選択削除できるUIを追加する
- sidecar cacheのヒット、破棄、再構築、元ファイル変更、破損を自動テストで確認する
- LZO検証用の単一block書き出しを公式`lzop`で復号して、指定logical offsetのSHA-256が一致するテストを追加する

## 保守・品質改善

機能追加と並行して、次を独立したコミットで行います。

- 破損・途中切れ・異常サイズの入力テストを増やす
- XFS bigtime、BitLockerなどの実イメージ回帰テストを追加する
- 外部由来の実イメージは、再配布条件と機密情報を確認してからテスト資産へ追加する
- ディスクoffset、filesystem block番号、論理ファイル長は`long`/`ulong`で保持し、配列長と一回のI/Oサイズは`int`に限定する。乗算・加算は縮小変換より前に64-bitへ昇格し、`checked`で検証する
- 巨大なQCOW2 L1 tableや圧縮clusterなど、`byte[]`の上限を超える入力は不透明なoverflowではなく理由付きで拒否する。対応時は一括確保ではなくchunked readを設計する
- 診断ログは読み取り処理を停止させない。GUI購読者の例外を分離し、高頻度イベントは集約・抑制してコピー性能とUI応答性を維持する
- ビルド・テスト用の`artifacts`と配布用の`publish`はGit管理外とし、大型fixtureがローカル容量を圧迫しないよう実行後に清掃する

## 将来の対応形式候補

利用目的に応じ、次の順で検討します。

1. Linux md RAIDの拡張
   - RAID0／RAID1／RAID10 near/far/offset対応を基盤に、RAID5/6を検討する
2. LVM2の拡張
   - striped／通常thin対応を基盤に、thin snapshot、external origin、通常snapshot、cache、mirror、RAID segmentを段階的に対応する
3. 証拠・暗号化形式の拡張
   - EWF2/Ex01、LUKS detached header／複数segmentを検討する
4. Linux・クロスプラットフォーム対応
   - filesystem/image readerのCore層をWindows UIから分離し、`net10.0`のcross-platform libraryとしてLinux上のCLI・自動回帰から利用可能にする
   - WinForms/ProjFS/物理ディスク列挙はWindows固有adapterとして維持し、LinuxではFUSE、mount helper、またはCLI exportを別実装として検討する
   - CIへLinux runnerを追加し、RAW、QCOW2、LZO、EWF、LUKS、ext、XFS、Btrfs、md RAID、LVMの読み取り回帰を実行する
   - 実イメージ検証では`xfs_repair -n`、`btrfs check --readonly`、`e2fsck -fn`、`cryptsetup`、`mdadm`、`lvm`などLinux標準ツールとの相互検証を継続する

## 維持する既存仕様

- 通常の解析経路と物理ディスクは読み取り専用
- 書き込みは原本と既存ファイルを変更せず、新規出力へのコピーオンライト確定だけを許可
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
