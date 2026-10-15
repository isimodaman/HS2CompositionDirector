# Composition Director for GAME

A BepInEx plugin for **Honey Select 2 / Honey Select 2 Libido DX** that saves and restores
character placement, camera composition and FOV **per animation (position)**.

Honey Select 2 / Honey Select 2 Libido DX 向けの BepInEx プラグイン。
Hシーンにおける**キャラクター位置・カメラ構図・FOV を体位単位で保存／復元**します。

This plugin is the direct evolution of **Position Keeper**, which was released earlier.

このプラグインは、以前にリリースした **Position Keeper** の正統進化版です。

> **Version 0.15.0.0 (beta)** — usable, but see [Known limitations](#known-limitations--既知の制限).
> **v0.15.0.0（β版）** — 実用可能ですが [既知の制限](#known-limitations--既知の制限) を確認してください。

---

## Concept / コンセプト

**Record several favourite spots and camera angles for every animation on every map,
then flip between them at speed with a hotkey.**

**各マップで、体位ごとにお気に入りの立ち位置とカメラアングルを複数記録させ、
ホットキーで高速に切り替え呼び出しできるようにするプラグイン。**

That is the whole idea. Everything else in this plugin exists to make those two things —
recording and recalling — reliable enough to use without thinking about them.

やることはこれだけです。他の機能はすべて、この「記録する」「呼び出す」の2つを
意識せずに使える精度まで引き上げるために存在しています。

A set of angles for one animation is built once and then reused. Cycling through them
during play costs a single key or mouse button, so changing the shot stops being a
setup task and becomes part of how you play.

1つの体位に対するアングル一式は一度作れば使い回せます。再生中の切り替えはキーか
マウスボタン1つなので、構図の変更が「準備作業」ではなく操作の一部になります。

---

## What it does / 何ができるか

Framing a shot in an H scene is easy. Getting it back after you change position is not —
the game resets everything. This plugin remembers it for you.

Hシーンで構図を作るのは簡単ですが、体位を変えると全部リセットされます。
このプラグインはそれを覚えておいて、戻ってきたときに復元します。

It is especially useful on map MODs for the main game, where the per-animation placement
has often not been dialled in properly.

特に、体位ごとの座標設定がきちんと詰められていないゲーム本編用マップMODに
適用するととても有用です。

| Feature | 機能 | Saved per / 保存単位 |
|---|---|---|
| **Position** | キャラクター位置 | animation × slot / 体位 × スロット |
| **Camera** | カメラ構図 + FOV | animation × position slot × camera slot |
| **BasisPos** | 基準位置 | map / マップ単位 |

### How the data nests / データの入れ子構造

```
Map  マップ
   BasisPos 1 / 2 / 3 ...          Per map. Independent of the animation.
                                   マップ単位。体位とは無関係。
   Animation  体位 A
      Position 1                   Character placement
         Camera 1                  Composition + FOV
         Camera 2
         Camera 3
      Position 2
         Camera 1                  A different entry from A / Position 1 / Camera 1
         Camera 2                  体位A / Position 1 / Camera 1 とは別データ
   Animation  体位 B
      Position 1
         Camera 1
```

Read it downwards: a camera belongs to one position slot, and a position slot belongs
to one animation. The same slot number under a different parent is a different entry.

上から下へ読みます。Camera は1つの Position に属し、Position は1つの体位に属します。
**親が違えば、同じスロット番号でも別のデータ**です。

`BasisPos` sits outside that chain — it is stored per map and does not change with the
animation.

`BasisPos` だけはこの連なりの外側にあり、マップ単位で保存されて体位では変わりません。

Camera data is **subordinate to** position data: `Position 1 / Camera 1` and
`Position 2 / Camera 1` are entirely separate entries.

Camera は Position に従属します。`Position 1 - Camera 1` と `Position 2 - Camera 1` は別データです。

### Beyond simple save/restore / 単なる保存・復元にとどまらない機能

- **Camera follows the character.** Move her manually and the framing stays put —
  3-axis translation and Y rotation are both absorbed.
  **キャラ移動へのカメラ追従。** 手動で動かしても構図が保たれます。3軸移動とY軸回転の両方に対応。
- **Camera sway.** A subtle handheld-style shake driven by the male character's motion.
  **カメラ揺動。** 男性キャラの動きに合わせた、控えめな手持ちカメラ風の揺れ。
- **Copy a whole camera set** from one position slot to another.
  **カメラ設定の一括コピー。** Position スロット間で設定をまとめて移植。
- **FOV persists** across position changes.
  **FOV の永続化。** 体位を変えても画角が維持されます。
- **Step forwards and backwards** through slots, from the panel or with hotkeys.
  Hotkeys can be combined with Shift / Ctrl / Alt.
  **スロットの送り／戻し。** パネルとホットキーの両方から。ホットキーは Shift / Ctrl / Alt と組み合わせられます。
- **A separate composition set per HS2 Custom Map Director config.**
  HS2 Custom Map Director is a plugin scheduled for a later release.
  **HS2 Custom Map Director の設定ごとに、別の構図セットを持てます。**
  HS2 Custom Map Director は後日リリース予定のプラグインです。

---

## Requirements / 動作環境

| | |
|---|---|
| Game / ゲーム | Honey Select 2, Honey Select 2 Libido DX |
| Platform | Unity 2018.4.11f1 base |
| BepInEx | **5.4.23.2 (Mono x64)** — built and tested / ビルド・動作確認 |
| Recommended | [ConfigurationManager](https://github.com/BepInEx/BepInEx.ConfigurationManager) (tested with 18.3 / 18.3 で確認) |

BepInEx 5.4.22 has also been used successfully. ConfigurationManager is effectively required —
`01. General / Enable Plugin` defaults to `false` and has to be turned on before the plugin does
anything, and the other settings are easiest to change from it.

BepInEx 5.4.22 でも実績があります。**ConfigurationManager は実質必須**です。
`01. General / Enable Plugin` の既定値が `false` で、これを有効にしないとプラグインが動作せず、
他の設定もここから変えるのが手軽なためです。

---

## Installation / インストール

1. Extract the zip into the game folder (the one containing `HoneySelect2.exe`).
   `Composition Director.dll` ends up in `BepInEx\plugins\CompositionDirector\`.
   zip をゲームのフォルダ（`HoneySelect2.exe` があるところ）に展開します。
   `Composition Director.dll` が `BepInEx\plugins\CompositionDirector\` に入ります。
2. Launch the game once so the config file is generated.
   一度ゲームを起動して設定ファイルを生成させます。
3. **Set `01. General / Enable Plugin` to `true`.**
   **`01. General / Enable Plugin` を `true` にします。**

> ⚠ Step 3 is not optional. `Enable Plugin` defaults to `false`, so **the plugin does
> nothing out of the box.** This is deliberate.
>
> ⚠ 手順3は必須です。`Enable Plugin` の既定値は `false` なので、
> **導入しただけでは何も起こりません。** 意図的な設計です。

The panel itself is shown from the start, and remembers both whether you left it open and
where you dragged it. No hotkey has to be assigned.

パネルは既定で表示されます。開いたまま終了したか、どこへドラッグしたかも記憶して
次回に引き継ぎます。ホットキーの割り当ては必須ではありません。

---

## Usage / 使い方

The panel appears once an H scene starts.

Hシーンに入るとパネルが出ます。

```
CAM  ◀ 1 ▶  [SAVE]
POS  ◀ 1 ▶  [SAVE]
BAS  ◀ 1 ▶  [SAVE]
[CAM Copy]   [x] Shake
Map: Gymnasium Noon
[Make Pair]  [x] Link
```

- **`CAM` / `POS` / `BAS` rows** — Camera, Position and BasisPos. The number is the slot you
  are on. **`▶` goes to the next slot, `◀` to the previous one**, and the slot is applied
  immediately.
  **`CAM` / `POS` / `BAS` の行** — Camera・Position・BasisPos です。数字が現在のスロットで、
  **`▶` で次へ、`◀` で前へ**切り替え、即座に適用します。

- **`SAVE`** — Writes the current state into the slot shown on that row. Each row has
  its own SAVE; they do not affect each other.
  **`SAVE`** — その行に表示中のスロットへ現在の状態を保存します。行ごとに独立していて、
  互いに影響しません。

- **`CAM Copy`** — **Copies a whole camera set from one Position slot to another Position
  slot within the same animation.** That is the only thing it does, and the only thing it
  is for. It takes every configured Camera under the current Position slot at once.
  **`CAM Copy`** — **同一体位の別 Pos スロットへ、Cam 設定を丸ごと引き写すための機能です。**
  用途はそれだけです。現在の Position 配下にある設定済み Camera を、まとめて控えます。

- **`CAM Paste`** — The same button, relabelled once something has been copied. Writes the
  set into the Position slot you are on now and saves it. The clipboard is cleared after.
  **`CAM Paste`** — 控えがあるときに同じボタンの文言が変わったものです。現在の Position
  スロットへ書き込んで保存します。実行後、控えはクリアされます。

- **`Shake`** — Turns [camera sway](#camera-sway--カメラ揺動) on and off.
  **`Shake`** — [カメラ揺動](#camera-sway--カメラ揺動)のオンオフです。

- **`Map:`** — Which composition set is in use: the map's name, or an HS2 Custom Map Director
  config name. When the name is too long, the `Map:` label is dropped to make room.
  **`Map:`** — いま使っている構図セットの名前です。マップ名か、HS2 Custom Map Director の
  設定名が出ます。長い名前のときは `Map:` の文字を省いて名前に幅を回します。

- **`Make Pair` / `Link`** — For [HS2 Custom Map Director](#hs2-custom-map-director--hs2-custom-map-director-との連携),
  a plugin scheduled for a later release. Without it, `Make Pair` stays greyed out and `Link`
  has no effect.
  **`Make Pair` / `Link`** — [HS2 Custom Map Director](#hs2-custom-map-director--hs2-custom-map-director-との連携)
  （後日リリース予定のプラグイン）用です。これが無い環境では `Make Pair` は押せず、`Link` も何もしません。

The window is draggable; its position is remembered.
ウィンドウはドラッグで移動でき、位置は記憶されます。

### Typical workflow / 基本的な流れ

```
1. Place the characters, POS row -> SAVE    立ち位置を決めて POS 行の SAVE
2. Set up a shot you like, CAM row -> SAVE  構図を作って CAM 行の SAVE
3. Change position / play on                体位を変える・遊ぶ
4. Come back to the same position           同じ体位に戻ってくる
   -> placement and shot are restored       -> 立ち位置と構図が復元される
```

**Save the Position too.** When the animation changes, the camera is restored automatically only
if the current Position slot has saved data. Switching cameras with `◀` / `▶` or the hotkeys
works without it.

**Position も保存してください。** 体位を変えたときのカメラの自動復元は、現在の Position スロットに
保存データがあるときだけ働きます。`◀` / `▶` やホットキーでの Camera の切り替えは、
Position が未保存でも使えます。

To build a set of angles for one position, save into Camera slots 1–5, then cycle
between them with `◀` / `▶` or the hotkeys.

1つの体位に複数のアングルを用意するなら、Camera スロット1〜5に保存し、
`◀` / `▶` かホットキーで切り替えます。

### Copying a camera set / カメラ設定の一括コピー

```
1. Position 1 with several cameras saved   Position 1 に複数の Camera を保存済み
2. [CAM Copy]                              控える
3. Switch to Position 2                    Position 2 へ切替
4. [CAM Paste]                             まとめて移植・自動保存
```

Because camera data is stored **relative to the female character**, the angles are
reproduced correctly even though the characters are placed differently in the other slot.

カメラデータは**女性キャラ基準の相対座標**で保存されているため、
コピー先でキャラの配置が違っても、同じアングルが正しく再現されます。

The clipboard is cleared when you paste, when the animation changes, or when the scene ends.
Copying only works **within the same animation**.

控えは「ペースト時」「体位変更時」「シーン終了時」にクリアされます。
コピーは**同一体位内でのみ**動作します。

### Hotkeys / ホットキー

| Action | 動作 | Default / 既定 |
|---|---|---|
| Toggle UI | UI表示切替 | *(none — optional / 任意)* |
| Next camera slot | 次の Camera スロット | `Mouse3` |
| Previous camera slot | 前の Camera スロット | *(none — optional / 任意)* |
| Next position slot | 次の Position スロット | `Mouse4` |
| Previous position slot | 前の Position スロット | *(none — optional / 任意)* |
| Toggle camera sway | 揺動のオンオフ | *(none — optional / 任意)* |
| Sway stronger (hold) | 揺動を強く（押しっぱなし） | *(none — optional / 任意)* |
| Sway weaker (hold) | 揺動を弱く（押しっぱなし） | *(none — optional / 任意)* |
| BasisPos slot | BasisPos スロット切替 | *(panel only / パネルのみ)* |

When the animation changes, the Position is applied automatically, followed by the Camera if
the Position has saved data.
体位が変わると Position が自動で適用され、Position が保存済みなら Camera も続けて適用されます。

#### Modifier keys / 修飾キー

Each of the four slot hotkeys has a **`... Modifiers`** setting directly below it, shown as
**Shift / Ctrl / Alt checkboxes** in ConfigurationManager.

スロット切替の4つのホットキーには、それぞれすぐ下に **`... Modifiers`** の設定があり、
ConfigurationManager では **Shift / Ctrl / Alt のチェックボックス**として表示されます。

This is meant for multi-button mice whose side buttons are fixed to shortcuts such as
`Ctrl+C` / `Ctrl+V`. To use such a button, set the hotkey to `C` and tick `Ctrl`.

多ボタンマウスのサイドボタンが `Ctrl+C` / `Ctrl+V` などに固定されている場合のための機能です。
そのボタンを使うなら、ホットキーを `C` にして `Ctrl` にチェックを入れます。

- Left and right keys are not distinguished. / 左右のキーは区別しません。
- The combination must match exactly: `Ctrl` alone does not fire on `Ctrl+Shift`.
  組み合わせは完全一致です。`Ctrl` だけの割り当ては `Ctrl+Shift` では反応しません。
- Leave all boxes unchecked to keep the plain hotkey. / 全部外せば従来どおりの単独キーです。
- Make sure no other plugin uses the same combination in H scenes.
  Hシーンで同じ組み合わせを使う他のプラグインが無いか確認してください。

#### While typing / 文字入力中

While a text field has keyboard focus (for example ConfigurationManager's search box, or another
plugin's input field), **keyboard hotkeys are ignored**, so typing does not trigger them.
Hotkeys on mouse buttons still work.

文字入力欄にキーボードの入力先があるあいだ（ConfigurationManager の検索欄や、他のプラグインの
入力欄など）は、**キーボードに割り当てたホットキーは反応しません。** 入力した文字で誤作動しないためです。
マウスボタンに割り当てたホットキーは反応します。

For the same reason, the automatic camera-focus key press after a position change
(`09. Camera Focus`) is skipped while you are typing — otherwise it would be typed into the field.

同じ理由で、体位を変えたあとの自動フォーカスのキー押し（`09. Camera Focus`）も、
入力中は行いません。行うと入力欄に文字が入ってしまうためです。

---

## Camera sway / カメラ揺動

Adds a slight handheld-camera shake by following one of the male character's bones.
**The composition is not changed** — only a small offset is added on top.

男性キャラのボーンの動きを拾って、控えめな手持ちカメラ風の揺れを加えます。
**構図そのものは変わりません。** 微小なオフセットが上乗せされるだけです。

Turn it on with the `Shake` toggle in the panel. Assign a key to `21. Sway / Sway Toggle Hotkey`
if you would rather use one.

パネルの `Shake` で切り替えます。キーで操作したい場合は
`21. Sway / Sway Toggle Hotkey` に任意のキーを割り当ててください。

**Strength can be adjusted while you play.** Assign keys to `21. Sway / Amplitude Up Hotkey`
and `Amplitude Down Hotkey`; holding one changes `Amplitude` continuously, and
`23. Sway / Amplitude Step` sets how fast it moves (percent per second). Watch the shake
itself as you hold the key — that is the feedback. Nothing is written to the config file
until you let go.

**強さはプレイ中に調整できます。** `21. Sway / Amplitude Up Hotkey` と
`Amplitude Down Hotkey` にキーを割り当てると、押している間 `Amplitude` が連続で変化します。
変化の速さは `23. Sway / Amplitude Step`（%/秒）。画面の揺れ方そのものを見ながら
決めてください。設定ファイルへはキーを離した時点で一度だけ書き込まれます。

| Setting | 既定 | What it does / 効果 |
|---|---|---|
| Target Bone | `Kosi01` | Which bone drives the motion / 追従するボーン |
| Amplitude | `60`% | Strength. Raise for a stronger shake / 揺れの強さ |
| Max Offset | `0.4` | Hard cap. Prevents blow-ups / 暴走を防ぐ上限 |
| Suppress Tail | `0.4`s | Sway is muted for this long after a pose change / ポーズ切替後に揺れを止める時間 |
| Smoothing | `18` | Higher = the camera settles back faster / 高いほど早く戻る |

The defaults were tuned by hand and are a good starting point. `Amplitude` is the one
most worth adjusting to taste.

既定値は実機で調整したものです。好みで変えるなら `Amplitude` が第一候補です。

Sway is automatically suppressed while the pose is switching, so status changes
(insertion, orgasm, speed changes) do not jolt the camera.

ポーズ切替中は自動的に抑制されるので、ステータス変化（挿入・絶頂・速度変更）で
カメラが跳ねることはありません。

---

## HS2 Custom Map Director / HS2 Custom Map Director との連携

**HS2 Custom Map Director is a plugin scheduled for a later release.**
Composition Director works fully without it; this section only applies once it is installed.

**HS2 Custom Map Director は後日リリース予定のプラグインです。**
Composition Director はこれが無くても全機能が動作します。この節は導入した場合の話です。

With it, a customised space saved as a config can have **its own composition set**, separate
from the base map's. The panel's bottom two rows are for this.

これを使うと、設定として保存したカスタム空間ごとに、ベースのマップとは**別の構図セット**を
持てます。パネル下の2行がそのための操作です。

- **`Link`** (on by default) — When HS2 Custom Map Director loads or saves a config, Composition
  Director switches to the composition set with that config's name. Turn it off to keep using
  the map's own set. Loading or saving a config turns it back on.
  **`Link`**（既定でオン） — HS2 Custom Map Director が設定を読み込む・保存すると、その設定名の
  構図セットへ切り替わります。オフにするとマップ本来のセットを使い続けます。
  設定を読み込む・保存すると、自動でオンに戻ります。

- **`Make Pair`** — Saves the composition set you are using now under the current config's name.
  Once the pair exists, the button reads **`Save Pair`**.
  **`Make Pair`** — いま使っている構図セットを、現在の設定名で保存します。
  保存済みならボタンは **`Save Pair`** になります。

  To start a customised space from the base map's compositions instead of from scratch,
  load the config in HS2 Custom Map Director, turn **`Link` off** (the map's own set comes back),
  then press `Make Pair`. With `Link` on, a config that has no pair yet starts with an empty set.
  カスタム空間の構図を、一から作らずにベースのマップの構図から始めるには、HS2 Custom Map Director で
  設定を読み込んだあと **`Link` をオフ**にし（マップ本来のセットに戻ります）、`Make Pair` を押します。
  `Link` がオンのままだと、まだペアの無い設定は空のセットから始まります。

  After pressing it, the pair is the set in use — the `Map:` row shows the config name — even while
  `Link` is off. To go back to the map's own set, turn `Link` on and then off again.
  押したあとは、`Link` がオフでもそのペアのセットを使います（`Map:` の行に設定名が出ます）。
  マップ本来のセットへ戻すには、`Link` をいったんオンにしてからオフにしてください。

The `Map:` row tells you which set is active. Config-specific sets are stored as
`<config name>_custom.txt` (see [Saved data](#saved-data--データの保存先)).

どのセットを使っているかは `Map:` の行で分かります。設定ごとのセットは
`<設定名>_custom.txt` として保存されます（[データの保存先](#saved-data--データの保存先)）。

---

## Configuration / 設定項目

| # | Section | Key | Default | Notes / 備考 |
|---|---|---|---|---|
| 01 | General | Enable Plugin | `false` | **Must be enabled / 要有効化** |
| 02 | General | Enable Camera Function | `true` | Position-only mode if `false` |
| 03 | Hotkeys | Toggle UI | `None` | Optional / 任意 |
| 04 | Cam Hotkey | Camera Change | `Mouse3` | Next camera slot / 次へ |
| 04 | Cam Hotkey | Camera Change Modifiers | `None` | Shift / Ctrl / Alt |
| 04 | Cam Hotkey | Camera Change (Prev) | `None` | Previous camera slot / 前へ |
| 04 | Cam Hotkey | Camera Change (Prev) Modifiers | `None` | Shift / Ctrl / Alt |
| 05 | Slots | Max Camera Slots | `5` | 1–9 |
| 06 | Pos Hotkey | Position Change | `Mouse4` | Next position slot / 次へ |
| 06 | Pos Hotkey | Position Change Modifiers | `None` | Shift / Ctrl / Alt |
| 06 | Pos Hotkey | Position Change (Prev) | `None` | Previous position slot / 前へ |
| 06 | Pos Hotkey | Position Change (Prev) Modifiers | `None` | Shift / Ctrl / Alt |
| 07 | Slots | Max Position Slots | `4` | 1–9 |
| 08 | Slots | Max Basis Position Slots | `4` | 1–9 |
| 09 | Camera Focus | Auto Camera Focus Key | `true` | |
| 10 | Camera Focus | Camera Focus Key | `W_Chest` | `Q_Face` / `W_Chest` / `E_Crotch` |
| 11 | Timing | Camera Focus Delay | `0.2` | seconds (0.01–2) |
| 12 | Timing | Animation Change Delay | `0.6` | seconds (0.1–3) |
| 15 | UI | Window Position X | `300` | Saved when you drag the panel / ドラッグで保存 |
| 15 | UI | Show Panel | `true` | Remembers the last state / 前回の状態を引き継ぐ |
| 16 | UI | Window Position Y | `200` | Saved when you drag the panel / ドラッグで保存 |
| 17 | Debug | Show Debug Info | `false` | Verbose logging |
| 18 | Debug | CamDat Diagnostics | `false` | Coordinate-system diagnostics |
| 19 | Debug | Bone Diagnostics | `false` | Bone / animation diagnostics |
| 20 | Sway | Enable Camera Sway | `false` | |
| 21 | Sway | Sway Toggle Hotkey | `None` | Optional / 任意 |
| 21 | Sway | Amplitude Up Hotkey | `None` | Hold to strengthen / 押している間ずっと強く |
| 21 | Sway | Amplitude Down Hotkey | `None` | Hold to weaken / 押している間ずっと弱く |
| 22 | Sway | Target Bone | `Kosi01` | |
| 23 | Sway | Amplitude | `60` | % (0–1000) |
| 23 | Sway | Amplitude Step | `500` | Hotkey adjust rate, %/sec (1–1000) |
| 24 | Sway | Max Offset | `0.4` | 0–5 |
| 25 | Sway | Suppress Tail | `0.4` | seconds (0–2) |
| 26 | Sway | Smoothing | `18` | 0.5–20 |
| 27 | Camera Follow | Follow Character Movement | `true` | 3-axis + Y rotation |
| 28 | Data | Data Directory | *(empty)* | Where saved data goes. See [Saved data](#saved-data--データの保存先) / 保存先 |
| 29 | Custom Map Director | Follow CMD Config | `true` | Same as the panel's `Link` / パネルの `Link` と同じ |

**13 and 14 are intentionally absent** — two settings were removed during development and the
numbering was left as it was.

**13 と 14 は欠番**です。開発中に設定を2つ削除しましたが、番号は詰めていません。

> ⚠ **Lowering a slot count is safe.** Data beyond the new limit is kept in memory and
> written back; raise the count again and it reappears. Camera and Position arrays also grow
> on demand when you raise the limit mid-session.
>
> ⚠ **スロット数を減らしても安全です。** 上限を超えるデータはメモリ上に保持されて書き戻されるため、
> 設定を戻せば再び使えます。プレイ中に増やした場合も配列がその場で拡張されます。

---

## Saved data / データの保存先

```
<GameRoot>\UserData\CompositionDirector_save\<MapKey>.txt
<GameRoot>\UserData\CompositionDirector_save\<config name>_custom.txt
```

`<GameRoot>` is the game folder. The second form is only created through the
HS2 Custom Map Director link.

`<GameRoot>` はゲームのフォルダです。2行目の形式は HS2 Custom Map Director との連携でだけ作られます。

To keep the data somewhere else, set `28. Data / Data Directory`. A leading `<GameRoot>` in that
setting is replaced with the game folder, so `<GameRoot>\UserData\MyCompositions` works as written.

保存先を変えるなら `28. Data / Data Directory` を指定します。先頭に `<GameRoot>` と書くと
ゲームのフォルダに置き換わるので、`<GameRoot>\UserData\MyCompositions` のように書けます。

One plain-text file per map, human-readable and hand-editable. The format is documented in a
comment header inside each file.

マップごとに1ファイル。プレーンテキストで手編集も可能です。書式は各ファイル先頭のコメントに記載。

```
Pos_[MapKey]_[ActionID]_[PosSlot]=f1x,f1y,f1z,f1rot|f2...|m1...|m2...
Cam_[MapKey]_[ActionID]_[PosSlot]_[CamSlot]=posx,posy,posz|rotx,roty,rotz|dirx,diry,dirz|fov
BasisPos_[MapKey]_[BasisSlot]=f1x,f1y,f1z,f1rot|f2...|m1...|m2...
```

Slot numbers in the file start at 0: slot 1 on the panel is `0` in the file.
ファイル内のスロット番号は0始まりです（パネルの 1 がファイルの 0）。

To reset everything, delete the folder. To reset one map, delete that map's file.
全消去はフォルダごと、マップ単位の消去はそのファイルを削除してください。

Data is keyed by map name, so **changing your MOD set can change the key** and make old data
stop matching. If saved compositions stop loading, check the name shown in the panel's `Map:` row.

データはマップ名で引くため、**MOD構成を変えるとキーが変わる**ことがあります。
保存した構図が読み込まれなくなったら、パネルの `Map:` の行に出ている名前を確認してください。

---

## Known limitations / 既知の制限

This is a beta. The following are known and understood.
β版です。以下は把握済みの制限です。

- **When a position has no saved camera data**, the game keeps whatever camera state the
  previous position left behind, then auto-focus runs. This is intentional — camera handling
  on that path is left to the game's own behaviour.
  **Camera 未設定の体位では**、直前の体位のカメラ状態が残ったままオートフォーカスが走ります。
  意図的な仕様で、この経路はゲーム本来の挙動に委ねています。

- **BasisPos has no hotkey.** Panel only.
  **BasisPos にホットキーはありません。** パネルからのみ操作します。

- Up to **2 female and 2 male** characters are tracked. Camera framing is relative to the
  first female character.
  追跡対象は**女性2名・男性2名**まで。カメラ構図は1人目の女性キャラ基準です。

- **Camera sway in VR is untested** and may cause discomfort. Leave it off in VR.
  **VR での揺動は未検証**です。酔う可能性があるので VR では切っておいてください。

- Camera settings can only be copied **within the same animation**.
  カメラ設定のコピーは**同一体位内**のみです。

- Keyboard hotkeys stay disabled while a text field keeps keyboard focus. Hotkeys on mouse
  buttons are not affected.
  文字入力欄が入力先を持ったままだと、キーボードのホットキーは止まったままになります。
  マウスボタンのホットキーは影響を受けません。

---

## Building from source / ソースからのビルド

Game and BepInEx assemblies are **not redistributed** in this repository. Copy them into
`Composition Director\lib\` first — see
[`lib/README.md`](Composition%20Director/lib/README.md) for the file list.

ゲーム本体および BepInEx のアセンブリは**同梱していません**。
まず `Composition Director\lib\` へコピーしてください。必要なファイルは
[`lib/README.md`](Composition%20Director/lib/README.md) にあります。

```bash
dotnet build "Composition Director/Composition Director.csproj"
```

Optionally deploy straight into your game folder:
ゲームフォルダへ直接配置する場合:

```bash
dotnet build "Composition Director/Composition Director.csproj" -p:HS2GameDir="D:\Games\HoneySelect2"
```

Target framework is .NET Framework 4.6, C# 7.3. The whole plugin is a single source file.
ターゲットは .NET Framework 4.6 / C# 7.3。実装は単一ソースファイルです。

---

## Credits / クレジット

- Design, specification, UI/UX — **isimodaman**
  企画・仕様策定・UI/UX設計 — **いしもだマン**
- Implementation — **Claude (Anthropic)**
  コーディング・実装 — **Claude (Anthropic)**
- Testing — **isimodaman**
  動作確認・テスト — **いしもだマン**

Derived from **Position Keeper for HS2DX v1.1.1** (isimodaman, MIT), previously distributed
through HS2 community uploaders.

**Position Keeper for HS2DX v1.1.1**（いしもだマン作・MIT）から派生しています。

---

## License / ライセンス

MIT License — see [LICENSE](LICENSE).

Note that this licence covers **this plugin's source code only**. Game assemblies referenced
at build time remain the property of their respective owners and are not covered.

このライセンスが及ぶのは**本プラグインのソースコードのみ**です。
ビルド時に参照するゲーム側アセンブリは各権利者に帰属し、対象外です。
