# 次回対応予定

- 最終更新: 2026-08-21
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

## 次回の推奨作業

### 1. Btrfs対応の第2段階

- RAID1は検証済みmirrorから読み取り、checksum不一致時の代替mirror選択を追加する
- 複数RAWイメージを一組として指定するUIとmanifest形式を設計してから実装する
- `mkfs.btrfs -m single -d single`の複数loop device由来fixtureを作成し、`btrfs check --readonly`と実イメージ回帰を追加する

## 保守・品質改善

機能追加と並行して、次を独立したコミットで行います。

- 破損・途中切れ・異常サイズの入力テストを増やす
- XFS bigtime、BitLockerなどの実イメージ回帰テストを追加する
- 外部由来の実イメージは、再配布条件と機密情報を確認してからテスト資産へ追加する

## 将来の対応形式候補

利用目的に応じ、次の順で検討します。

1. Btrfs
   - 圧縮、サブボリューム、backup superblock、複数デバイスを段階的に扱う
2. Linux md RAIDの実読み取り
   - まずRAID1から開始し、その後RAID0/5/6を検討する
3. LVM2の拡張
   - 複数PV、thin、snapshot、cache、mirror、RAID segmentを段階的に対応する

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
