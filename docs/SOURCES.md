# 参考コード・公式仕様・採否

参考コードの確認履歴: 2026-10-15。以下は当時の公開GitHub APIから取得したHEAD、ソース、LICENSE本文の記録で、今回そのHEADを再取得したという意味ではない。Xは閲覧していない。参考リポジトリのコードは実行・インストールしていない。

公式契約の再確認・実装照合: 2026-10-18。画像/Box/Choice/NoulとWindows APIの一次資料を読み、模擬テストへ反映した。実API・課金・実データ送信、Windows実機操作は行っていない。

| 参考 | 確認したHEAD | ライセンス / 読んだ実装 | 採用した考え方 |
| --- | --- | --- | --- |
| [DECRUX9812/typesafe-skill-router](https://github.com/DECRUX9812/typesafe-skill-router) | `94fe114b3c53b3d6b81ba72890eb6db596cbaa12` | MIT / `typesafe_router/router.py` | Choiceの候補順位、Noulの適合検査、none、勝者自身の適合を確認 |
| [kerpopule/hermes-jev-skills](https://github.com/kerpopule/hermes-jev-skills) | `98ce26aac5e1e4a764d982797c2c45e727e5a765` | MIT / `skills/jev-computer-use/scripts/jev_gui_agent.py` | 現在の観察からGUI候補を作る、提案・操作・結果観察を分ける |
| [browser-use/jev-ultrafast](https://github.com/browser-use/jev-ultrafast) | `1231850a0bf1a0c0341fe408ef1668dbbfdfac46` | MIT / `jev_ultrafast/agent.py`, `jev_ultrafast/snapshot.js` | DOMの意味と生存中の要素ID、predict/act分離、承認消費後の重複抑止、入力生成の分離 |

**本プロジェクトの実装は新規作成。ソースのコピー、vendor化、SDK/パッケージ依存は追加していない。** 上記は着想・構造の参考。将来コードを移植するときは当該版のMIT著作権表示と許諾文を同梱する。

MIT本文の著作権者は順に Ritesh Patel (2026)、Steve Darlow (2026)、Browser Use (2026)。HEADと本文への固定リンク:

- [router source](https://github.com/DECRUX9812/typesafe-skill-router/blob/94fe114b3c53b3d6b81ba72890eb6db596cbaa12/typesafe_router/router.py) / [LICENSE](https://github.com/DECRUX9812/typesafe-skill-router/blob/94fe114b3c53b3d6b81ba72890eb6db596cbaa12/LICENSE)
- [GUI source](https://github.com/kerpopule/hermes-jev-skills/blob/98ce26aac5e1e4a764d982797c2c45e727e5a765/skills/jev-computer-use/scripts/jev_gui_agent.py) / [LICENSE](https://github.com/kerpopule/hermes-jev-skills/blob/98ce26aac5e1e4a764d982797c2c45e727e5a765/LICENSE)
- [agent source](https://github.com/browser-use/jev-ultrafast/blob/1231850a0bf1a0c0341fe408ef1668dbbfdfac46/jev_ultrafast/agent.py) / [DOM source](https://github.com/browser-use/jev-ultrafast/blob/1231850a0bf1a0c0341fe408ef1668dbbfdfac46/jev_ultrafast/snapshot.js) / [LICENSE](https://github.com/browser-use/jev-ultrafast/blob/1231850a0bf1a0c0341fe408ef1668dbbfdfac46/LICENSE)

routerは既存スキルの推薦であり、教示からのスキル自動作成製品ではない。hermes-jev-skillsのGUIコードにはMacのcua-driver探索・AXロールとLinuxのAT-SPI向け処理があるが、この調査からWindows教示ナビの完成を裏付けない。Hermes全体の依存は採用しない。Handwiredは導入していない。

## 公式資料と反映先

- [Skill suggestion](https://docs.typesafe.ai/cookbooks/skill_suggestion): 候補推薦の参考。教示スキル生成と区別。`buildSkillChoice`へ参考構造を反映。
- [Choice](https://docs.typesafe.ai/primitives/choice): 最大255選択肢、確率分布とconfidence、独立質問。本試作は254候補+noneまで。
- [Noul](https://docs.typesafe.ai/primitives/noul): yesの確率を`answers.<id>.noul`で受ける。独自confidenceを仮定しない。
- [Box](https://docs.typesafe.ai/primitives/box): 1対象の正規化矩形、不在でも矩形が返り得る。存在Noulとnone/one/severalのChoiceで一意性を検査し、`acceptBox`で存在/確信度/有限値/辺順/範囲を検査。Boxのconfidenceだけで対象の存在を保証しない。
- [Images](https://docs.typesafe.ai/images): PNG/JPEG静止、各辺280〜1568px、78,400〜2,458,624画素、画像16MiB、request/processed/expandedは各32MiB。UUIDのassetをstateへ一度だけ置く。画像数の固定上限を推測しない。本試作は1枚限定。
- [Jev 1.14](https://typesafe.ai/blog/jev-1-14): 以前参照したリリース説明。現在のImages/Box公式ページも画像・Boxを実験的な位置付けとしており、実運用精度の保証として扱わない。

Boxの公式ページは`jev-preview` / `jev-1.14.0`を明示。固定モデルを採用。画像またはBoxを使う場合には`TypeSafe-Danger-Zone-Images: true`。エンドポイントは`https://api.typesafe.ai/v1/systemone`。Choiceを先に実行して初めて分かる対象は後続のBoxリクエストに渡す。独立質問の並列評価を逐次処理とみなさない。

- [OpenAI Structured Outputs / JSON mode](https://developers.openai.com/api/docs/guides/structured-outputs): Responses APIの`text.format.type=json_object`を確認。モデル固有の対応やアカウント権限は未確認。Lunaの公開API名は決めていない。ローカル検証に失敗した出力を採用しない。
- [AutomationElement.FromPoint](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.frompoint?view=windowsdesktop-8.0): 物理画面座標、UIAスレッドの注意を反映。
- [Extended Window Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles): overlayのtransparent/no-activate/toolwindow。
- [SpeechRecognitionEngine](https://learn.microsoft.com/en-us/dotnet/api/system.speech.recognition.speechrecognitionengine?view=netframework-4.8.1): 端末内の認識エンジン、明示開始・停止・Dispose。

- [KBDLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct): 注入フラグとキー離上を区別する。一般アプリ承認で注入イベントを物理キーのラッチ更新に使わないための根拠。
- [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook): ウィンドウ破棄通知、out-of-context配信、メッセージループの条件。登録ウィンドウの寿命管理へ反映。
- [UI Automation Threading Issues](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-threading-issues): UIA呼出をGUIスレッドから分離する根拠。専用MTAと待機境界の設計へ反映。
- [Value Control Pattern](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/implementing-the-ui-automation-value-control-pattern): `ValuePattern`と読み取り専用の条件。入力全文を1操作で設定し、値を読み戻せない場合は成功を推測しない。

## 今回の契約確認と検証の区別

[公式契約レビュー](JEV_CONTRACT_REVIEW.md)に、質問の独立性、Boxの不在・複数、画像最小辺280、ヘッダー、モデル、ローカル事前検査の限界と模擬テストを記載した。Imagesのメタデータ除去・EXIF方向の扱いも、現在画像と画面座標の対応を維持する際の前提である。

今回のNodeテストは47件で、provider/coreの模擬契約はそのうち44件。純C# 98件とMicrosoft net48参照によるLinuxクロスコンパイルは、UIA・キーフック・マイク・DPIの実測ではない。新しいWindows CIは設定追加のみで実行待ち。[Windows実機チェック表](WINDOWS_CHECKS.md)の項目は今回全て未実施。

この記録は公開資料の確認とコード契約の検証結果であり、モデル推論や一般アプリの動作確認結果ではない。実API・課金・実データの外部送信が必要になった時点で利用者の確認を受ける。
