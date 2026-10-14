# lib/ — Reference assemblies (not included)

The DLLs required to build this plugin belong to the game and to BepInEx, and are
**not redistributed here**. Copy them from your own installation into this folder.

このプラグインのビルドに必要な DLL は、ゲーム本体および BepInEx に属するものです。
**リポジトリには同梱していません。** 各自のインストール先からこのフォルダへコピーしてください。

## Required files / 必要なファイル

| File | Copy from / コピー元 |
|---|---|
| `Assembly-CSharp.dll` | `<Game>\HoneySelect2_Data\Managed\` |
| `IL.dll` | `<Game>\HoneySelect2_Data\Managed\` |
| `UnityEngine.dll` | `<Game>\HoneySelect2_Data\Managed\` |
| `UnityEngine.CoreModule.dll` | `<Game>\HoneySelect2_Data\Managed\` |
| `UnityEngine.IMGUIModule.dll` | `<Game>\HoneySelect2_Data\Managed\` |
| `UnityEngine.AnimationModule.dll` | `<Game>\HoneySelect2_Data\Managed\` |
| `BepInEx.dll` | `<Game>\BepInEx\core\` |
| `0Harmony.dll` | `<Game>\BepInEx\core\` |

`<Game>` is your Honey Select 2 root, e.g. `D:\Games\HoneySelect2`.

`<Game>` は Honey Select 2 のインストール先（例: `D:\Games\HoneySelect2`）。

## Version note / バージョンについて

Built and tested against **BepInEx 5.4.23.2 (Mono x64)** on **HS2DX / Unity 2018.4.11f1**.
Other BepInEx 5.4.x releases are expected to work; 5.4.22 has been used successfully.

**BepInEx 5.4.23.2（Mono版 x64）** / **HS2DX（Unity 2018.4.11f1 ベース）** で
ビルド・動作確認しています。他の 5.4.x でも動作すると思われ、5.4.22 での実績もあります。
