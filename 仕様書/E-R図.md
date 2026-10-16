# E-R図

アプリの状態は`%LOCALAPPDATA%\MailSentinel\state.db`（SQLite）の8つの表に保存する。中心はjudgement（判定履歴）で、該当項目・移動記録・誤判定がこれにぶら下がる。アカウントと項目の定義は設定ファイル側にあり、DBにはIDだけを持つ。

&#91;embedded content: E-R図 · SQLiteの8つの表\]

設定で項目を消しても過去の履歴が読めるよう、judgement\_itemには判定時点の表示名と優先度を控えとして残す。

## 表定義

日時はすべてISO 8601の文字列（UTC）で保存する。列の型はSQLiteの型で示す。

### judgement（判定履歴）

| 列 | 型 | 内容 |
| --- | --- | --- |
| id | INTEGER PK | 自動採番 |
| account\_id / folder | TEXT | 設定のアカウントIDと元のフォルダー名 |
| uidvalidity / uid | INTEGER | 判定時の位置 |
| message\_id | TEXT | 重複判定と「元に戻す」の予備に使う |
| received\_at | TEXT | 受信日時 |
| from\_addr / subject | TEXT | 差出人と件名（件名は先頭200文字）。本文は保存しない |
| is\_advertisement | INTEGER | 0/1 |
| confidence | TEXT | high / medium / low。判定失敗時はNULL |
| summary / reason | TEXT | AIの要約と判定理由 |
| outcome | TEXT | 下の「結果の値」のいずれか |
| target\_item\_id / target\_folder | TEXT | 移動先に選ばれた項目とフォルダー。移動しない場合はNULL |
| trial\_run\_id | INTEGER FK | お試し仕分けの判定ならその回。新着は NULL |
| model\_name / prompt\_version | TEXT | どのモデルと指示文の版で判定したか |
| duration\_ms | INTEGER | 判定にかかった時間 |
| judged\_at | TEXT | 判定日時 |
| keep | INTEGER | 1なら保持期間を過ぎても消さない（誤判定が記録されたとき） |

結果の値（outcome）:

| 値 | 意味 |
| --- | --- |
| moved | 項目のフォルダーへ移動した |
| moved\_ad | 広告用フォルダーへ移動した |
| planned | お試し（結果の表示のみ）で、移動予定先だけ決めた |
| kept\_ad | 広告なので受信トレイに残した |
| kept\_low\_confidence | 確信度がしきい値未満なので残した |
| kept\_no\_match | 該当項目がないので残した |
| kept\_no\_folder | 該当項目の振り分け先が未設定またはサーバーにないので残した |
| failed | 判定または移動に失敗した |

### judgement\_item（該当項目）

| 列 | 型 | 内容 |
| --- | --- | --- |
| judgement\_id | INTEGER PK, FK | 親の判定。親の削除で一緒に消える |
| item\_id | TEXT PK | 設定の項目ID |
| item\_label | TEXT | 判定時点の表示名 |
| priority | INTEGER | 判定時点の優先度 |

### move\_record（移動記録）

| 列 | 型 | 内容 |
| --- | --- | --- |
| id | INTEGER PK | 自動採番 |
| judgement\_id | INTEGER FK UNIQUE | 1つの判定に移動は最大1回 |
| account\_id | TEXT | 移動したアカウント（戻すときも同じアカウントだけを使う） |
| from\_folder / to\_folder | TEXT | 移動元と移動先 |
| dest\_uidvalidity / dest\_uid | INTEGER | 移動先での位置（サーバーがCOPYUIDを返したときだけ。なければNULL） |
| moved\_at | TEXT | 移動日時 |
| undone\_at | TEXT | 受信トレイに戻した日時。未実施はNULL |
| undo\_result | TEXT | restored / not\_found / failed |

### correction（誤判定）

| 列 | 型 | 内容 |
| --- | --- | --- |
| id | INTEGER PK | 自動採番 |
| judgement\_id | INTEGER FK | 対象の判定 |
| correct\_item\_id | TEXT | 本来の項目ID。NULLは「どの項目でもない」、adは「広告」 |
| source | TEXT | undo（元に戻したとき） / manual（画面から記録） |
| created\_at | TEXT | 記録日時 |

### trial\_run（お試しの回）

| 列 | 型 | 内容 |
| --- | --- | --- |
| id | INTEGER PK | 自動採番 |
| period\_from / period\_to | TEXT | 対象の受信日（日付のみ） |
| accounts / folders | TEXT | 対象アカウントとフォルダー（JSON配列） |
| mode | TEXT | preview（結果の表示のみ） / move（実際に移動） |
| max\_messages | INTEGER | 件数の上限 |
| status | TEXT | running / done / cancelled |
| total / processed | INTEGER | 対象件数と処理済み件数 |
| created\_at / finished\_at | TEXT | 開始と終了の日時 |
| rolled\_back\_at | TEXT | まとめて戻した日時。未実施はNULL |

### queue\_item（処理待ち）

| 列 | 型 | 内容 |
| --- | --- | --- |
| id | INTEGER PK | 自動採番。この順（古い順）に処理する |
| account\_id / folder | TEXT | 対象の位置 |
| uidvalidity / uid | INTEGER | 対象のメール |
| trial\_run\_id | INTEGER FK | お試しの対象ならその回。新着はNULL |
| state | TEXT | waiting / processing / failed |
| attempts / last\_error | INTEGER / TEXT | 試行回数と直前のエラー |
| enqueued\_at | TEXT | 登録日時 |

処理が終わった行は削除する（結果はjudgementに残る）。同じメールが二重に入らないよう、(account\_id, folder, uidvalidity, uid, trial\_run\_id) を一意にする。SQLiteの一意制約はNULLどうしを別物として扱うため、trial\_run\_idはIFNULL(trial\_run\_id, 0)とした式で一意索引を張る。起動時にprocessingのまま残った行はwaitingに戻す。

### folder\_state（監視状態）

| 列 | 型 | 内容 |
| --- | --- | --- |
| account\_id / folder | TEXT PK | 監視するフォルダー（現状は各アカウントの受信トレイのみ） |
| uidvalidity | INTEGER | 前回の値。変わったら再走査する |
| baseline\_uid / baseline\_at | INTEGER / TEXT | 初回起動時の起点UIDとその日時 |
| last\_processed\_uid | INTEGER | キューに登録した最大のUID |
| updated\_at | TEXT | 更新日時 |

### processed\_message（処理済みメール）

| 列 | 型 | 内容 |
| --- | --- | --- |
| account\_id / message\_id | TEXT PK | UIDVALIDITYが変わったときの重複防止に使う |
| excluded | INTEGER | 1なら除外リストに一致した（judgementには残らない） |
| first\_seen\_at | TEXT | 初めて処理した日時 |

## 索引と保守

- 索引: judgement(judged\_at)、judgement(account\_id, message\_id)、judgement(trial\_run\_id)、queue\_item(state, id)
- 保持: 毎日1回、judged\_atが保持日数（既定90日）より古く、keepが0のjudgementを消す。子の表も一緒に消える。processed\_messageも同じ日数で消す
- 版管理: スキーマの版をSQLiteの`PRAGMA user_version`に持ち、起動時に不足分の移行SQLを順に当てる
- 書き込み: WALモード。判定1件分（judgement、judgement\_item、move\_record、queue\_itemの削除）は1つのトランザクションにまとめる
