# イメージの関連ファイルへのアクセス制限

VirtualDisk.GuiとQcow2Explorerは、イメージに記録されている親ディスクや分割ファイルのパスを、共通コアの`ImageFileAccessPolicy`で検査します。

## 読み込み時の動作

| 参照先 | 動作 |
|---|---|
| 選択したイメージのフォルダーと、その配下にある通常ファイル | 自動で読み取り可能 |
| 上記の範囲外にあるローカルの通常ファイル | 自動では開かない。GUIで必要なファイルを選び直した場合だけ読み取り可能 |
| OVAの内部ディスクから、そのOVAの展開先の外への参照 | 拒否。外部ファイルを許可するボタンは表示しない |
| UNC、ネットワークドライブ、デバイスパス、Windows予約デバイス名、代替データストリーム | 拒否 |
| シンボリックリンク、ジャンクション等の再解析ポイントを含むパス | 拒否。実体のファイルを選ぶ必要がある |

「同じフォルダー内」の許可にはサブフォルダーも含まれます。未知のイメージは、無関係なファイルが入っていない専用フォルダーに置いて開いてください。この仕組みはイメージを「安全」と認定したり、フォルダー内のデータの読み込みを禁止するものではありません。

関連ファイルの確認画面では、参照先を自動で許可せず、利用者にファイルを選び直してもらいます。確認画面の既定操作は中止/キャンセルです。選択したファイルだけを許可し、そのファイルがあるフォルダー全体へのアクセスは許可しません。元の参照先とは異なる場所にある正規の親ディスクも選択できます。

許可は今回の読み込みに限られ、設定ファイルには保存されません。同じイメージを開き直すと再確認します。複数ディスクとして利用者が一緒に選択したファイルは、その読み込みで明示的に選ばれたファイルとして扱います。追加で選べるファイルは最大64個です。

書き込みの許可とは独立しており、関連ファイルは読み取り専用で開きます。既存の物理ディスク専用メニューの明示操作は、この関連ファイル参照制限とは別の経路です。

## 実装対象

- QCOW2: backing file、external data file、再帰的なbacking chain。
- VHD/VHDX/AVHDX/VMDK: DiscUtilsに制限付きの読み取り専用ファイルシステムを渡し、親/extentの存在確認・属性確認・オープンを検査。
- Parallels HDD: descriptorとlayerファイル。
- EWF: 自動検出するsegmentファイル。
- OVA: 展開先を専用の許可ルートにし、初期ディスクと後から選ぶディスクの両方に適用。
- CLIも同じ既定制限を使います。範囲外のファイルを対話的に許可するCLIオプションは追加していません。

関連ファイルを拒否した場合や、利用者が選択をキャンセルした場合、読み込み途中のリーダーを破棄します。現在開いているイメージは、新しい読み込みが成功するまで置き換えません。

## 自動テスト

リポジトリ直下で実行します。

```powershell
dotnet build Qcow2Explorer.slnx -c Release
dotnet run --project tests/VirtualDisk.Core.Tests/VirtualDisk.Core.Tests.csproj -c Release --no-build
dotnet run --project tests/Qcow2Explorer.Tests/Qcow2Explorer.Tests.csproj -c Release --no-build

# 新しい参照制限のテストだけを実行
dotnet run --project tests/VirtualDisk.Core.Tests/VirtualDisk.Core.Tests.csproj -c Release --no-build -- --reference-access-only
```

`ImageReferenceAccessTests`は範囲外参照の拒否、明示選択、選択先の差し替え、許可の非永続性、選択先ディレクトリへ許可が拡大しないこと、同一フォルダーの正規参照、ネットワーク/デバイス/ADSの拒否、OVA内外の境界、QCOW2外部データ、VMDK extent、VHDX parent、Parallels layerを検証します。シンボリックリンクの作成権限がない環境ではそのケースだけスキップを表示します。

## Windows GUIの手動確認

今回の作業環境ではComputer Useが `Computer Use native pipe is unavailable: failed to connect native pipe: 指定されたファイルが見つかりません。 (os error 2)` を2回返し、GUIの自動操作は実施できませんでした。以下は利用者による確認が必要です。

起動するビルド済みアプリ:

- Avalonia版: `src/VirtualDisk.Gui/bin/Release/net10.0/VirtualDisk.Gui.exe`
- WinForms版: `src/Qcow2Explorer/bin/Release/net10.0-windows/Qcow2Explorer.exe`

検証用イメージはこの作業フォルダーの `.security-review/gui-fixtures/` に用意しています。実データは含まれず、既存テストが生成したFAT16イメージのコピーです。両アプリで次の操作を行ってください。

| 手順 | 開くファイル/操作 | 期待する表示・動作 |
|---|---|---|
| 1 | `images/01-local.qcow2` | 確認ダイアログなしで開き、FAT16パーティションと内容を表示できる |
| 2 | 続いて `images/02-external.qcow2` | 「関連ファイルの確認」が出る。表示された参照先を自動で開かない |
| 3 | 確認画面でキャンセル/中止 | 読み込みが中止され、手順1のイメージを引き続き操作できる |
| 4 | 再度 `images/02-external.qcow2` を開き、関連ファイルを選択する側のボタンを押す。ファイル選択画面はキャンセル | 同様に読み込みを中止し、既存の表示を維持する |
| 5 | 再度 `images/02-external.qcow2` を開き、`host-data/parent.raw` を自分で選ぶ | 読み込みが再開し、FAT16の内容を表示できる |
| 6 | 同じ `images/02-external.qcow2` を開き直す | 再び関連ファイルの確認が出る。前回の許可が自動保存されていない |
| 7 | `images/03-network.qcow2` | ネットワーク参照を拒否するエラー。許可/選択を促さない |
| 8 | `images/04-external.ova` | OVA展開先外の参照を拒否するエラー。外部ファイルの選択を促さない |
| 9 | `images/05-junction.qcow2` | ジャンクション経由の参照を拒否するエラー |
| 10 | 拒否後に `images/01-local.qcow2` を開く | アプリが操作可能で、正常な読み込みを再開できる |

Avalonia版の確認画面には「開くのを中止」「関連ファイルを選択」があり、WinForms版には説明付きのOK/キャンセルがあります。WinForms版ではOKの後に関連ファイルの選択画面が開きます。

同じコアを使用するCLIでは、上の5ファイルのうち正常な`01-local.qcow2`だけが終了コード0、それ以外の4件は拒否を示す終了コード1になることを確認しました。GUIの確認ボタン・キャンセル・再開の操作そのものは未確認です。

## 制限

- 検証はこのWindows x64環境で行っています。Linux/macOSの実機GUI確認は未実施です。
- ローカルの別プロセスが検査直後にパスをリンクへ交換する競合や、許可済みファイルの内容を同時に変更する攻撃への完全な隔離は提供しません。OSサンドボックスの代替ではありません。
- ハードリンクを元のファイルの別名として識別する機能はありません。
- ネットワーク上のイメージやリンク経由のイメージは、ローカルの実体ファイルとしてコピーして利用してください。
