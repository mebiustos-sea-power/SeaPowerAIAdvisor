# Mission Creator ミッションファイル暫定仕様

**状態: 暫定・未実装の検討資料 / 初版: 2026-10-28**

## 1. この文書の位置付け

この文書は、AI Mission Creatorがミッションを生成・修正するための調査結果と検討事項を整理する。実装を拘束する確定要件書でも、すべてのミッション形式を網羅した公式仕様書でもない。

**この文書の設計案を実現するためだけにコードを複雑化しない。** 試作でより簡潔で妥当な方法が分かった場合は、目的とゲームの実仕様を確認したうえで文書を更新する。クラス構成、抽象化の数、独自の編集コマンド体系、状態管理や互換処理の追加を、この文書から義務として導かない。

以下を区別する。

| 表記 | 意味 |
|---|---|
| 確認済・コード | 現在のゲームDLLを読み取り専用で確認した内容。実機での成功を意味しない |
| 確認済・実例 | 同梱ミッションに存在する構造や値。全体の制約・必須条件を意味しない |
| 方針・設計案 | 現時点での実装候補。試作結果に応じて変更する |
| 未確認 | 推測で補完せず、必要になった段階で確認する内容 |

ゲームの読み込み仕様に由来する制約と、MOD側の設計上の選択は別に扱う。文書に載っていない項目というだけで、ゲーム上有効なデータを拒否する仕様にはしない。

## 2. 対象と現在の到達点

対象は、ゲーム標準ミッションエディターで作成・読み込み・保存する通常のミッションINI。戦闘途中のセーブ `.sav`、キャンペーン定義、すべての特殊用途INIを同じ形式として扱わない。

現在のCreatorは独立MODの起動ボタンと空のウィンドウまで。ファイル生成・編集内容の取得・読み込み連携・AI通信は未実装。この調査ではゲームの起動、保存、AIへの送信、生成ファイルの実機ロードを行っていない。

調査対象DLLのSHA-256:

```text
eab2297ba92c6013958bdb2cf16d5794a4731330818ce353f89dc2241e16a3bd
```

パス: `Sea Power_Data/Managed/Seapower-Scripts.dll`。ゲーム更新後も同じ仕様だとは仮定しない。

## 3. 基本となる処理経路

**方針・設計案:** ミッションファイルを生成・更新し、標準エディターへ読み込む方式を第一候補とする。あらゆる編集項目を直接操作するAPIを先に作ることは前提にしない。

```text
ユーザーの依頼 + ファイル形式の説明 + 利用できるデータ + 必要なら現在のミッション
    -> AIによる生成・修正案
    -> Creator側で出力の確認・保存
    -> 標準エディターで読み込み・確認・編集
```

確認済・コード:

- `MissionEditorViewModel.LoadMission(string filename, bool ...)` は `Filename` を設定して `MissionEditorModel.ReadFromFile(viewModel)` を呼ぶ。部分更新専用の入口ではない。
- `MissionEditorModel.SetupMission(viewModel)` は編集モデル、日時・天候、ユニット・トリガーなどを準備し、`SetupMissionBriefing()` と `WriteToFile(...)` を呼ぶ。
- `WriteToFile(string path, bool isDynamicCampaignMission)` はINIを書き出す。ただし、`Globals.currentMissionFilePath` の更新や保存用ユニット参照リストの整理を伴う。
- `SetupMissionBriefing()` は必要なフォルダーを準備し、ブリーフィングのファイルを書き出す。

**未確認:** 標準の保存経路を、未保存の編集内容を取り出すための一時出力に安全に利用できるか。保存先・副ファイル・モデル同期への影響を確認する前に、「ファイルを読むだけの処理」「保存先を戻せば元通り」とは扱わない。独自シリアライザーや完全な状態復元を作ることも、現時点では決定しない。

## 4. ファイルの大枠

確認済・実例と読み書き処理から整理した、代表的な構成。以下が全種類、すべて必須、すべて一つのファイル内で完結する、という意味ではない。

| セクション・項目群 | 役割・代表例 | 根拠 |
|---|---|---|
| `[Language_en]`、`[Language_ja]` など | ミッション名、説明、目標・イベントの表示文、ブリーフィング参照 | E1, E2 |
| `[Environment]` | 日時、天候、海況、中央位置、背景データの利用など | E1, E2 |
| `[Mission]` | プレイヤー・敵の陣営、ユニット数、グループ、トリガー数など | E1, E2 |
| `[Taskforce1Vessel1]` など | ユニットの種類、バリアント、配置、設定、経路など | E2, E3 |
| `<unit>_WeaponSystem...` など | 個別兵装設定。基本ユニット節とは別の参照関係を持つ | E1, E3 |
| `[Trigger1]` など | 条件、条件式、成立時の動作 | E1, E5 |
| `[Taskforce1_Objectives]` など | 目標IDと得点・状態トークン・属性 | E1, E5 |
| `[BackgroundData]` | 都市、港、空港、施設などの外部データ参照 | E1, E2 |
| 地図要素・航空運用など | 対応する読み書き処理がある。詳細書式は本版で未整理 | E2 |

### 4.1 INIの読み方と省略値

確認済・実例:

- `[section]` と `key=value` のテキスト形式。
- 値にはカンマ、`|`、`/` など、項目固有の区切りがある。すべてを同じ分割規則で解釈しない。
- 同梱 `Sub Duel JMSDF 1985.ini` には `[Language_fr]` の重複がある。この事実だけから、重複時の優先順位は決めない。

未確認:

- 全項目の必須性、既定値、大小文字、重複時の扱い、エスケープ規則、許容する文字コードの網羅的な仕様。
- エディターと実プレイのローダーが、欠落・不正値をすべて同じように扱うか。

方針・設計案:

- 新規出力は読みやすく曖昧さの少ない形式にする。ただし、既存ファイルに独自の厳格なINI規則を後付けして拒否しない。
- 標準保存で出力される項目と、実際に必須の項目を区別する。実例にあるという理由だけで必須項目にしない。
- 未指定項目を省略した際に「既存の編集内容が維持される」とは仮定しない。

## 5. 識別子・陣営・件数

### 5.1 識別子の区別

| 種類 | 実例 | 意味 |
|---|---|---|
| ミッション内ユニット参照 | `Taskforce1Submarine1` | そのミッション内で、編隊・条件・攻撃対象などから参照する識別子 |
| ユニット定義ID | `Type=jmsdf_ss_yushio` | ゲームデータの艦種・機種などを選ぶ識別子 |
| バリアント参照 | `VariantReference=Variant2` | そのユニット定義に属する選択肢。全機種共通の意味や年代番号ではない |
| 目標ID | `DestroyBothEnemySubs` | 目標表示・達成・失敗指定などで共有するミッション内のID |
| 表示名 | ユニット名・ミッション名など | 人に見せる文字列。上記IDの代わりにはしない |

確認済・コード: `WriteUnitData` は `Unit.IniRef` を `Type` として保存する。`GetMissionUnitReference` などは陣営・種別・番号からミッション内参照を作る。これらをAdvisorの戦闘中ユニットIDと混同しない。

標準的なユニット節は `Taskforce1` / `Taskforce2` / `Neutral` に `Vessel` / `Submarine` / `Aircraft` / `Helicopter` / `LandUnit` / `Biologic` と番号を組み合わせる。エディター内の列挙名とファイル上の表記は必ずしも同じではない。例えば列挙名 `Aircrafts` と節名の `Aircraft` を区別する。[E2, E3]

### 5.2 陣営と件数

確認済・実例:

```ini
[Mission]
PlayerTaskforce=Taskforce1
EnemyTaskforce=Taskforce2
NumberOfTaskforce1Submarines=1
NumberOfTaskforce2Submarines=2
NumberOfTriggers=5
```

`Taskforce1` と `Taskforce2` は国家名ではない。「プレイヤーは日本側」という依頼では、プレイヤー陣営の指定と、配置する艦種・機種の選定を別々に考える。

方針・設計案: 新規生成・ユニット追加削除では件数と参照を整合させる。ただし、全ローダーが件数だけでユニットを列挙するとは断定しない。エディターの `LoadSingleUnits` には、陣営に対応する節を走査する経路がある。[E2]

## 6. 座標と単位

### 6.1 中央位置と相対配置

確認済・コード/実例:

```ini
[Environment]
MapCenterLatitude=44.01
MapCenterLongitude=140.01

[Taskforce1Submarine1]
RelativePositionInNM=40.84,shallow,98.81
```

- 中央位置は緯度・経度。ユニットの `RelativePositionInNM` は、その中央位置を基準とする `x, vertical, z` の配置であり、緯度・経度の組ではない。
- `x` と `z` は水平の相対距離（NM）。読み込みでは両成分を内部距離へ変換し、中央位置へ加えて地理座標へ戻す。
- 数値の中央成分は高度のft値として変換する経路がある。**項目名にNMが含まれていても、3成分すべてがNMではない。**
- 深度は `shallow`、`BelowLayer` などのトークンになる場合がある。`LoadUnitData` は数値と深度名の読み取りを分け、深度列挙値の解析には大文字小文字を区別しない指定を使う。
- 配置には `PositionsList_...` の参照や `RandomSpawnCenter` などの別経路もある。単純な3数値だけが合法な形式ではない。

根拠: `LoadUnitData`、`WriteUnitRelString`、`Utils.RelNMFromGeoPos`。[E3, E4]

方針・設計案: AIへは、使う座標表現・基準点・単位を明示する。中央位置だけ変更して相対位置をそのまま残すと、各ユニットの地理的な配置も変わる。緯度経度からの変換をAIに任せるか、Creator側の支援を用意するかは、試作で決める。近似式や変換用の独自基盤を、この文書だけを理由に追加しない。

### 6.2 速力・高度・深度

確認済・コード:

- `Telegraph` は直接のkt値ではない。船舶・潜水艦・生物について、標準保存には `SpeedSettings` の整数を保存する経路がある。
- 固定翼機では `SpeedSettingsAircraft` に1を加えて保存する経路がある。同じ整数をすべての種別で同じ速力だと扱わない。ヘリ・地上ユニットの全保存経路は本版で未整理。
- 深度には `Surface` / `Periscope` / `Shallow` / `AboveLayer` / `BelowLayer` / `Deep` / `VeryDeep` がある。変温層との関係などの意味を、単一の固定深度へ置き換えない。

実際のkt値、利用可能な設定、プリセットと高度・深度の対応はユニットや条件に依存する。列挙値だけを渡して時間・燃料・探索効果を正しく計画できるとは限らない。

未確認: 全設定の書式・数値の許容範囲、各ユニットでの有効性、経路上の設定が有効になるタイミング。この文書では一律の速度・高度・深度を定義しない。

### 6.3 日時と環境

確認済・実例: `Date=1985,6,26`、`Time=10,0`、`ConvertTimeToLocal=False`、`SeaState=4`、`Clouds=Scattered`、`WindDirection=NE`、`Weather=Fog`。

日時にはローカル時刻とZulu時刻の変換処理がある。UTC・現地時刻を説明なしに混在させない。各項目の全候補や省略値は未整理であり、上の値は固定の既定値ではない。`Heading` には負値の実例もあるため、根拠なく0～360だけを許容する検証を追加しない。

## 7. 経路・グループ・トリガー・目標

確認済・実例:

```ini
Waypoints=39.7052,Shallow,94.4336|170.5908,Shallow,98.8178
```

`Waypoints` は点を `|` で連結する形式を持ち、点に `/SetTelegraph,3` などの指示が付く例もある。全動作の文法や実行時点をこの例から一般化しない。

グループには `Neutral_Formation1=NeutralVessel1,NeutralVessel2,NeutralVessel3|Fishing Boats W` のような参照がある。保存処理には、名前以外に編隊や間隔などを出力する経路もあるため、単純な名前一覧だけとして再構成しない。[E3]

トリガーは条件と動作をまとめ、ユニット参照・目標ID・他トリガーなどとの関係を持つ。例:

```ini
[Trigger2]
Name=Both subs destroyed player victory
Condition_Condition1_Type=UnitDestroyed
Condition_Condition1_Units=Taskforce2Submarine2,Taskforce2Submarine1
ConditionsCompleted=<Condition1>
Action_EndMission=True
Action_Victory=Taskforce1
Action_ObjectivesCompleted=DestroyBothEnemySubs,MochishioMustSurvive
```

これは同梱ミッションの構造説明であり、単独で読み込める最小ファイルではない。`ConditionsCompleted` の式全体の文法、全条件・動作の引数は今後整理する。

目標には、例として `DestroyBothEnemySubs=20,-20,Complete,Main` がある。読み書き処理は完了得点・失敗得点・状態トークン・`Main` / `Hidden` を扱う。ただし、`Complete` の実プレイ開始時の意味を本版では確定しない。「生成時点ですでに達成済み」とAIへ説明しない。[E5]

確認済・コード/付属資料: 日時条件を開始日時との差分秒で保存する経路がある。付属ガイドも、条件の秒数をミッション開始からの経過秒として説明している。トリガーの種類すべてに同じ時間解釈を広げない。[E5, E6]

方針・設計案: 一部修正でユニットや目標のIDを不用意に変えない。削除・改名が必要な場合は、関連する参照も検討する。戦力配置が正しくても、終了条件が成立しないミッションは別の問題として確認する。

## 8. INI以外のファイルと往復保存

確認済・コード/実例:

- `MissionBriefingAssetsDirectory`、`MissionBriefingLeftPane`、`MissionBriefingRightPane` などによる外部参照がある。
- 標準の準備・保存経路では `BriefingText_<language>.xml` や `BriefingMap_<language>.xml` に関係する処理があり、INIだけを任意の場所へ移しても参照先が揃うとは限らない。
- 背景データにはゲームの既存ファイルへの参照もある。既存ファイルを参照することと、Creatorが新しくそのファイルを生成することは別。
- `CaptureMissionEditorPassthroughData` / `RestoreMissionEditorPassthroughData` が存在し、標準の読み書きでも保持用処理を使っている。未知の項目がすべて無損失で保存されるとまでは確認していない。

方針・設計案: 初期試験で外部ファイルを必須にするかは、最小ミッションのロード結果で決める。すべてを単一INIへ押し込むことも、最初から汎用の複数ファイル配布基盤を作ることも決定しない。

## 9. AIへ渡す資料と責務

### 9.1 AIへ渡す内容の候補

- ユーザーの依頼と明示条件。新規作成か既存ミッションの修正か。
- その作業に使うファイル構造、座標・単位、ID・参照、利用する機能の説明。
- 実際に使用可能なユニット定義・バリアント・装備などのカタログ。
- 修正では、基準となるミッションと、必要なら選択中のユニット・グループを特定する情報。

ミッション制作者に必要な陣営・敵配置・条件などは、Advisorの「プレイヤー既知情報だけ」という制限とは目的が異なる。通信実装を共有しても、Advisor用プロンプトや情報開示方針をそのままCreatorへ流用しない。

この仕様書の全文を毎回AIへ送るとは決定しない。常時必要な説明と、作業に応じて必要な詳細を分けられるが、オンデマンド照会の仕組みを追加することも現時点では確定しない。

### 9.2 分担の方向性

| 担当 | 役割の案 |
|---|---|
| AI | 依頼に沿ったミッションの構成、戦力・配置・経路・条件の計画、修正案の生成 |
| Creator | 編集対象の取得、出力の受け取り、必要な機械的検証、保存先・ファイルの管理、エディターへの読み込み |
| ゲーム | 自身の読込・編集・保存・実プレイの処理 |
| Shared | 実際に共用する通信・保存などの汎用処理。ミッション固有の項目解釈はCreator側 |

国・年代・地理・戦術としての妥当性と、ゲームが受理するファイルとしての妥当性を区別する。ゲーム上可能な設定を、MOD独自の歴史観や推測だけで機械的に拒否しない。

## 10. 新規生成と細かい修正

方針・設計案:

- 新規作成では、標準エディターで開いて編集できるファイルを目指す。
- 修正では現在の内容を基準とし、指示と無関係な部分を維持する。ただし、対象変更に付随する件数・参照の更新まで禁止するものではない。
- 「現在の内容」が保存済みファイルなのか、未保存の手動編集を含むのかを曖昧にしない。未保存内容の取得経路が未確立なら、取得できていると扱わない。
- AIの応答待ち中に手動編集された場合、古い内容を無条件に反映しない。
- 生成案と元ファイルを分けて扱う方法を優先して試す。細かなUI・確認手順・取り消し方法は未決定。

出力をミッション本文にするか、構造化データからファイルへ変換するか、修正を全文・部分変更のどちらで受けるかは未決定。最初から独自の汎用パッチ言語、完全な差分マージ、完全なロールバックを実装する要件にはしない。実際のAI出力と修正試験で必要性を判断する。

## 11. 検証の考え方と次の調査

ファイルが読めること、エディターで意図どおり表示できること、保存・再読込できること、実プレイで成立することを区別する。コードの静的確認を、これらの実機確認の代わりにしない。

最初の小さな試験候補は「既存データで選んだ少数ユニットを配置し、ロード・保存・再読込した後、1項目だけ修正する」。これは試験用の例であり、製品のユニット数や対応機能を固定する仕様ではない。

| 未確定事項 | 判断に必要な確認 |
|---|---|
| 最小の有効なミッションと省略値 | 標準保存ファイルとの比較、エディターロード、実プレイ |
| 未保存の編集内容の取得 | 標準保存経路の同期・書込先・副ファイルの影響を試験 |
| AIの出力形式 | 実際の生成結果・部分修正の安定性・処理の簡潔さを比較 |
| 座標支援 | 実際の配置結果を確認し、必要な支援だけを追加 |
| カタログの取得・提供方法 | 標準エディターの機種一覧・バリアント・装備と照合 |
| 目標・トリガー・経路の詳細 | 必要になった種類ごとに読み書き処理と実行結果を確認 |
| XMLなどの副ファイル | 最初の用途で必要か、参照が保存・移動後も解決するか確認 |
| 修正時の保持範囲と競合 | 手動編集後のAI修正、待機中の手動変更、未知項目の保持を試験 |

検証・実装の範囲は、その段階の目的と実際に見つかった問題に合わせる。網羅的な対応表や独自検証器を完成させないと試作へ進めない、という進め方にはしない。

## 12. 根拠と調査限界

| ID | 確認対象 |
|---|---|
| E1 | `original/missions/JMSDF/Sub Duel JMSDF 1985.ini`、`original/missions/Campaign Scenarios/Pacific Strike/01A Senkaku Run.ini`、`original/missions/Demo/Attack Waypoints Test.ini` |
| E2 | `MissionEditorViewModel.LoadMission`、`MissionEditorModel.ReadFromFile`、`SetupMission`、`WriteToFile`、`LoadSingleUnits`、`LoadFormationsForTaskForce` |
| E3 | `MissionEditorModel.LoadUnitData`、`WriteUnitData`、`WriteUnitRelString`、`GetMissionUnitReference`、`getUnitCategoryString`、`getFormationLine` |
| E4 | `Utils.RelNMFromGeoPos`、`MissionEditorModel.LoadUnitData`、`WriteUnitRelString` |
| E5 | `MissionEditorModel.WriteTrigger`、`WriteTriggerActions`、`WriteCondition`、`WriteObjectives`、`LoadObjectivesForTaskforce` |
| E6 | `original/documentation/Mission Editor. Triggers and conditions.docx`。文書内改訂0.2、2024-09-13。現行の全機能を網羅すると扱わない |
| E7 | `MissionEditorModel.SetupMissionBriefing`、`EnsureBriefingAssetsFolder`、`CaptureMissionEditorPassthroughData`、`RestoreMissionEditorPassthroughData` |

`original/...` は `Sea Power_Data/StreamingAssets/` 配下の同梱ファイル。調査証跡はローカル専用 `artifacts/mission-creator-spec-research-20261028/` に保存し、Gitや配布物へ含めない。ゲーム本体のソース・DLL・ミッションは変更していない。

本版は静的調査と既存ファイルの確認に基づく。AIへこの仕様を渡した試験、生成ファイルのロード、未保存内容の取得、保存の往復、実プレイの検証はまだ実施していない。今後の確認で誤りや不足が見つかった場合は、実装を文書へ無理に合わせず、根拠と文書を修正する。
