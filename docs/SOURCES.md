# 参考コード・公式仕様・採否

確認日: 2026-10-15。公開GitHub APIからHEADを取得し、以下のSHAのソースとLICENSE本文を読み取った。Xは閲覧していない。参考リポジトリのコードは実行・インストールしていない。

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
- [Box](https://docs.typesafe.ai/primitives/box): 1対象の正規化矩形、不在でも矩形が返る。`acceptBox`で存在/確信度/辺の順序を検査。
- [Images](https://docs.typesafe.ai/images): PNG/JPEG静止、各辺256〜1568px、78,400〜2,458,624画素、画像16MiB、request/processed/expandedは各32MiB。UUIDのassetをstateへ一度だけ置く。画像数の固定上限を推測しない。本試作は1枚限定。
- [Jev 1.14](https://typesafe.ai/blog/jev-1-14): 画像・Boxは実験的な位置付け。実運用精度の保証として扱わない。

Boxの公式ページは`jev-preview` / `jev-1.14.0`を明示。固定モデルを採用。画像には`TypeSafe-Danger-Zone-Images: true`。エンドポイントは`https://api.typesafe.ai/v1/systemone`。Choiceを先に実行して初めて分かる対象は後続のBoxリクエストに渡す。独立質問の並列評価を逐次処理とみなさない。

- [OpenAI Structured Outputs / JSON mode](https://developers.openai.com/api/docs/guides/structured-outputs): Responses APIの`text.format.type=json_object`を確認。モデル固有の対応やアカウント権限は未確認。Lunaの公開API名は決めていない。ローカル検証に失敗した出力を採用しない。
- [AutomationElement.FromPoint](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.frompoint?view=windowsdesktop-8.0): 物理画面座標、UIAスレッドの注意を反映。
- [Extended Window Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles): overlayのtransparent/no-activate/toolwindow。
- [SpeechRecognitionEngine](https://learn.microsoft.com/en-us/dotnet/api/system.speech.recognition.speechrecognitionengine?view=netframework-4.8.1): 端末内の認識エンジン、明示開始・停止・Dispose。

この記録は公開資料の確認結果。モデル推論、Windowsマウスフック、マイク、DPIの実測結果ではない。
