# Changelog

Versioning follows `Major.Minor.Patch.Hotfix` (not semantic versioning).
`0.x.x.x` denotes alpha/beta; `1.0.0.0` and above denotes a stable release.

本プロジェクトは `Major.Minor.Patch.Hotfix` 形式を採用しています（セマンティックバージョニングではありません）。
`0.x.x.x` はα版・β版、`1.0.0.0` 以降が正式リリースです。

| Increment / 刻み | Meaning / 意味 |
|---|---|
| `0.0.0.1` | Minor error fixes, typos / 細かいエラーフィックス、タイポ修正 |
| `0.0.1.0` | Bug fixes, small improvements / バグ修正、小規模改善 |
| `0.1.0.0` | New features, phase completion / 新機能追加、Phase完成 |
| `1.0.0.0` | Stable release, major revision / 正式リリース、大規模改定 |

---

## v0.15.0.0 — beta

First public release.
最初の公開版です。

Everything below was developed and verified before this release, newest first.
以下はこのリリースまでに実装・検証した内容です（新しい順）。

### Modifier keys for slot hotkeys / スロット切替ホットキーの修飾キー (v0.15.0.0)

The four slot hotkeys can now be combined with **Shift / Ctrl / Alt**, set with checkboxes
directly below each hotkey in ConfigurationManager. Meant for multi-button mice whose side
buttons are fixed to shortcuts such as `Ctrl+C`: set the hotkey to `C` and tick `Ctrl`.

スロット切替の4つのホットキーを **Shift / Ctrl / Alt** と組み合わせられるようになりました。
ConfigurationManager で各ホットキーのすぐ下にあるチェックボックスで設定します。
サイドボタンが `Ctrl+C` などに固定されている多ボタンマウス向けで、ホットキーを `C` にして
`Ctrl` にチェックを入れれば使えます。

Left and right keys are not distinguished, and the combination must match exactly.
With no box ticked the hotkey behaves exactly as before.

左右のキーは区別せず、組み合わせは完全一致で判定します。チェックが無ければ従来どおりです。

### Hotkeys ignored while typing / 文字入力中はホットキーが反応しない (v0.15.0.0)

While a text field has keyboard focus — ConfigurationManager's search box, another plugin's
input field — keyboard hotkeys are ignored, so typing a letter or `Ctrl+C` no longer fires them.
Hotkeys on mouse buttons still work.

文字入力欄（ConfigurationManager の検索欄や、他のプラグインの入力欄）にキーボードの入力先が
あるあいだは、キーボードのホットキーが反応しなくなりました。文字や `Ctrl+C` を打っても
誤作動しません。マウスボタンのホットキーは反応します。

The automatic camera-focus key press after a position change is also skipped while typing;
it used to be typed into the field.

体位変更後の自動フォーカスのキー押しも、入力中は行いません。以前は入力欄に文字が入っていました。

### HS2 Custom Map Director link / HS2 Custom Map Director との連携 (v0.14.0.0 – v0.14.4.0)

**HS2 Custom Map Director is a plugin scheduled for a later release.**
**HS2 Custom Map Director は後日リリース予定のプラグインです。**

With it installed, each of its configs can have its own composition set, separate from the
base map's. The panel gains a `Map:` row showing which set is in use, a `Make Pair` / `Save Pair`
button that saves the current set under the config's name, and a `Link` toggle
(`29. Custom Map Director / Follow CMD Config`). Without it, these do nothing and everything
else works as before.

導入すると、その設定ごとに、ベースのマップとは別の構図セットを持てます。パネルには
使用中のセットを示す `Map:` の行、現在のセットを設定名で保存する `Make Pair` / `Save Pair`、
`Link` のトグル（`29. Custom Map Director / Follow CMD Config`）が加わりました。
導入していなければこれらは何もせず、他の機能はこれまでどおり動作します。

### Saved data lives under UserData / 保存先は UserData 配下 (v0.14.0.0 – v0.14.4.0)

Compositions are saved to `<GameRoot>\UserData\CompositionDirector_save\`.
They used to sit next to the DLL in `BepInEx\plugins`, where updating a MOD pack could wipe them.

構図の保存先を `<GameRoot>\UserData\CompositionDirector_save\` にしました。
以前は `BepInEx\plugins` の DLL の横にあり、MODパックの更新で消えるおそれがありました。

`28. Data / Data Directory` chooses another location. A leading `<GameRoot>` in it is replaced
with the game folder.

`28. Data / Data Directory` で別の場所を指定できます。先頭の `<GameRoot>` はゲームのフォルダに置き換わります。

### Step back through slots / スロットの戻し操作 (v0.13.0.0)

Each slot row now has `◀` / `▶` buttons, and `Camera Change (Prev)` / `Position Change (Prev)`
hotkeys go back one slot. The previous-slot hotkeys are unassigned by default.

スロットの各行に `◀` / `▶` のボタンが付き、`Camera Change (Prev)` / `Position Change (Prev)` の
ホットキーで1つ前へ戻れるようになりました。戻し用のホットキーは既定では割り当てていません。

### Panel redesign / パネルの作り直し (v0.12.1.0 – v0.14.3.2)

- Fixed 160 px width, with the height trimmed to fit its contents.
  幅を 160px に固定し、高さは中身に合わせて詰めるようにしました。
- Camera sway is now a `Shake` toggle; the separate `ON` / `OFF` label is gone.
  揺動のオンオフを `Shake` のトグルにし、別にあった `ON` / `OFF` の表示をなくしました。
- Text in each row lines up on the same baseline.
  各行の文字の高さを揃えました。
- A long name in the `Map:` row drops the `Map:` label to make room, and is cut from the end.
  `Map:` の行は、名前が長いと `Map:` の文字を省いて名前に幅を回し、末尾から切ります。

### Camera follow fix / キャラ追従の修正 (v0.12.1.0)

With the game's move-step slider turned up, ordinary manual moves were sometimes mistaken
for teleports and the camera stopped following. The threshold was raised based on measured
movement, so following now works at any slider setting.

ゲーム側の移動量スライダを大きくすると、通常の手動移動が瞬間移動と誤判定され、カメラの
追従が止まることがありました。実測した移動量をもとに判定の閾値を見直し、スライダの設定に
関係なく追従するようになりました。

### Sway strength on a hotkey / 揺動の強さをホットキーで (v0.12.0.0 – v0.12.0.1)

Hold a key to change how strong the shake is, without opening the config screen.
The shake you are looking at is the feedback; nothing is written to disk until you
let the key go.

キーを押している間だけ揺動の強さが連続で変化します。設定画面を開く必要はありません。
見ている揺れ方そのものがフィードバックで、設定ファイルへはキーを離した時点で
一度だけ書き込まれます。

Unassigned by default.
既定では割り当てられていません。

### Panel and sway toggles have no default key / パネル開閉と揺動オンオフは既定でキー無し (v0.11.2.0)

Neither the panel toggle nor the sway toggle takes a key by default. The panel is meant to
stay visible, and the sway toggle sits on it as `Shake`, so nothing has to be bound.
Assign `03. Hotkeys / Toggle UI` or `21. Sway / Sway Toggle Hotkey` only if you want them.

パネル開閉・揺動オンオフとも、既定ではキーを占有しません。パネルは表示しっぱなしで
使う想定で、揺動はパネル上の `Shake` で切り替えられるためです。必要な人だけが
`03. Hotkeys / Toggle UI` と `21. Sway / Sway Toggle Hotkey` に任意のキーを登録してください。

### Panel state is remembered / パネル状態の記憶 (v0.11.1.0)

The panel is shown from the start and carries its state across sessions — whether you left
it open, and where you dragged it.

パネルは既定で表示され、開いたまま終了したか・どこへドラッグしたかを
次回起動へ引き継ぎます。

### Camera follows character movement / キャラ移動へのカメラ追従 (v0.11.0.0)

Moving the character manually now keeps the framing. 3-axis translation and Y rotation
are both absorbed. Enabled by default.

キャラを手動移動させても構図が保たれます。3軸移動とY軸回転の両方に対応。既定で有効。

Suppressed during animation changes and Position restores so the camera is not swung around.

体位変更や Position 復元の際は抑制され、カメラが振り回されません。

### Camera set copy / カメラ設定の一括コピー (v0.10.0.0)

`CAM Copy` / `CAM Paste` in the panel move every configured camera under one Position slot
to another, within the same animation.

パネルの `CAM Copy` / `CAM Paste` で、Position スロット配下の設定済み Camera を
まとめて別スロットへ移植します（同一体位内）。

Only meaningful because camera data is stored relative to the character — the angles are
reproduced correctly even though the characters sit differently in the other slot.

カメラデータがキャラ基準の相対座標だからこそ成立する機能です。
コピー先で配置が違っても、同じアングルが再現されます。

### Camera sway / カメラ揺動 (v0.9.0.0)

A subtle handheld-style shake driven by the male character's bone motion. Composition is
unchanged; only a small offset is added. Toggle it with the panel's `Shake` or `21. Sway / Sway Toggle Hotkey`.

男性キャラのボーンの動きを拾った、控えめな手持ちカメラ風の揺れ。
構図は変わらず、微小なオフセットが乗るだけです。パネルの `Shake`、または `21. Sway / Sway Toggle Hotkey` で切替。

Automatically suppressed while the pose is switching, so status changes do not jolt the camera.

ポーズ切替中は自動的に抑制されるため、ステータス変化でカメラが跳ねません。

### Character-relative camera coordinates / カメラ座標の相対化 (v0.8.3.0)

Camera compositions are now stored relative to the first female character instead of the
animation's base point. Recalling a camera reproduces the framing against the character,
wherever she has been moved to.

カメラ構図を、体位の基準点ではなく1人目の女性キャラ基準で保存するようになりました。
キャラをどこへ動かしていても、呼び出せばキャラに対する構図が再現されます。

The save format did not change and existing data is unaffected.

保存フォーマットは変わっておらず、既存データはそのまま使えます。

### Slot data safety / スロットデータの保護 (v0.8.2.0, v0.8.2.1)

- Lowering a slot count in config no longer discards data beyond the new limit.
  It is kept in memory, written back, and reappears if you raise the count again.
  スロット数を減らしても、上限を超えるデータが失われなくなりました。
  メモリ上に保持されて書き戻され、設定を戻せば再び使えます。
- Raising a slot count mid-session now works. Arrays grow on demand.
  プレイ中にスロット数を増やせるようになりました。配列がその場で拡張されます。
- Load errors no longer discard the whole map's data. Parsing is now per-line.
  読み込みエラーでマップ全体のデータが消えることがなくなりました。行単位で処理します。

### Cleanup / 整理 (v0.8.2.0)

Removed unused settings and dependencies, cached IMGUI styles, and fixed the assembly
version metadata.

未使用の設定と依存を削除し、IMGUI スタイルをキャッシュ化、
アセンブリのバージョン情報を修正しました。

### FOV control / FOV 制御 (v0.8.1.0 – v0.8.1.2)

FOV set by the user is now correctly retained across position and slot changes, and the
camera no longer jumps when a FOV key is pressed right after a composition is restored.

ユーザーが設定した FOV が、体位・スロット変更をまたいで正しく維持されるようになりました。
構図の復元直後に FOV キーを押しても画角が飛びません。

---

## Earlier versions / それ以前

Versions prior to this release were internal development builds and were not published.
This plugin is derived from **Position Keeper for HS2DX v1.1.1** (isimodaman, MIT).

本リリース以前のバージョンは内部開発版であり、公開していません。
本プラグインは **Position Keeper for HS2DX v1.1.1**（いしもだマン作・MIT）から派生しています。
