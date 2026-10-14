using AIChara;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Manager;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HS2_CompositionDirector
{
    // === VERSION DEFINITION ===
    public static class VersionInfo
    {
        // GUID は作者の名前空間 com.isimodaman.hs2.* で統一する（v0.11.2.0）。
        // 自作プラグイン群を揃えるための変更で、機能上の意味は無い。
        //
        // 注意: BepInEx の設定ファイル名は GUID から決まる
        // （BepInEx\config\<GUID>.cfg）。これを変えると既存の .cfg が
        // 別物として扱われ、設定が既定値に戻る。保存した構図データは
        // GUID に依存しない（保存先は UserData 配下。ResolveDataDirectory を参照）ので影響しない。
        public const string GUID = "com.isimodaman.hs2.compositiondirector";
        public const string PluginName = "Composition Director";
        public const string PluginVersion = "0.15.0.0";
    }

    // === Enum定義 ===
    public enum CameraFocusKey
    {
        Q_Face, W_Chest, E_Crotch
    }

    // カメラ揺動で追従する男性キャラのボーン。
    // 実測（v0.8.3.4）でのフレーム間振幅（rms 中央値）:
    //   Kosi01 0.00420 / Kosi03 0.00420 / Head 0.00480
    // Head は振幅が最大だがポーズ切替時の正味変位も大きい（最大 0.388）。
    // Kosi01 は切替時の正味変位がゼロで、抑制の負担が最も小さい。
    public enum SwayBone
    {
        Kosi01, Kosi02, Kosi03, Spine01, Spine03, Neck, Head
    }

    // スロット切替ホットキーに組み合わせる修飾キー（v0.15.0.0）。
    // [Flags] にしてあるので ConfigurationManager がチェックボックスで描く
    // （CM 18.3 の DrawFlagsField。0 の None は描かれず、全部外すと None になる）。
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Shift = 1,
        Ctrl = 2,
        Alt = 4
    }

    // ConfigurationManager へ渡す属性（v0.15.0.0）。
    // CM は Config の Tags から「型名が ConfigurationManagerAttributes のもの」を探し、
    // 同名の public フィールドを読む（CM 18.3 の DLL に型名の文字列定数があることを確認）。
    // 型そのものは CM の DLL には無く、各プラグインが自分で持つのが BepInEx の作法。
    // 今のところ使うのは並び順（Order。大きいほど上）だけ。
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }

    // === データ構造（v9.7仕様準拠：Position従属Camera構造） ===
    [Serializable]
    public class Vector3Data
    {
        public float x, y, z, rotY;

        public Vector3Data() { }

        public Vector3Data(Vector3 vector, float rotY = 0)
        {
            this.x = vector.x;
            this.y = vector.y;
            this.z = vector.z;
            this.rotY = rotY;
        }

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }
    }

    [Serializable]
    public class CameraState
    {
        public Vector3Data CamDatPos;
        public Vector3Data CamDatRot;
        public Vector3Data CamDatDir;
        public float CamDatFov;
        public bool HasData;

        public CameraState()
        {
            CamDatPos = new Vector3Data();
            CamDatRot = new Vector3Data();
            CamDatDir = new Vector3Data();
            CamDatFov = 23f;
            HasData = false;
        }
    }

    [Serializable]
    public class PositionSlotData
    {
        public Vector3Data Female1;
        public Vector3Data Female2;
        public Vector3Data Male1;
        public Vector3Data Male2;
        public CameraState[] CameraSlots;
        public bool[] HasCameraData;
        public bool HasPositionData;

        public PositionSlotData(int cameraSlots)
        {
            Female1 = new Vector3Data();
            Female2 = new Vector3Data();
            Male1 = new Vector3Data();
            Male2 = new Vector3Data();
            CameraSlots = new CameraState[cameraSlots];
            HasCameraData = new bool[cameraSlots];
            HasPositionData = false;

            for (int i = 0; i < cameraSlots; i++)
            {
                CameraSlots[i] = new CameraState();
                HasCameraData[i] = false;
            }
        }
    }

    [Serializable]
    public class CompositionSet
    {
        public PositionSlotData[] PositionSlots;
        public string LastModified;

        public CompositionSet(int positionSlots, int cameraSlots)
        {
            PositionSlots = new PositionSlotData[positionSlots];
            for (int i = 0; i < positionSlots; i++)
            {
                PositionSlots[i] = new PositionSlotData(cameraSlots);
            }
            LastModified = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    // === BasisPos専用データ構造 ===
    [Serializable]
    public class BasisPositionSet
    {
        public Vector3Data[] BasisPositions;
        public bool[] HasBasisPositionData;

        public BasisPositionSet(int basisSlots)
        {
            BasisPositions = new Vector3Data[basisSlots * 4];
            HasBasisPositionData = new bool[basisSlots];

            for (int i = 0; i < basisSlots * 4; i++)
            {
                BasisPositions[i] = new Vector3Data();
            }
        }
    }

    [BepInPlugin(VersionInfo.GUID, VersionInfo.PluginName, VersionInfo.PluginVersion)]
    [BepInProcess("HoneySelect2.exe")]
    [BepInProcess("HoneySelect2VR.exe")]
    public class CompositionDirector : BaseUnityPlugin
    {
        // === Win32 API定義 ===
        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        const uint KEYEVENTF_KEYDOWN = 0x00;
        const uint KEYEVENTF_KEYUP = 0x02;
        const byte VK_Q = 0x51;
        const byte VK_W = 0x57;
        const byte VK_E = 0x45;
        const byte VK_OEM_2 = 0xBF;

        // === GUIサイズ・座標定義 ===
        private const int UI_WIDTH = 160;

        // スロット送り／戻しの矢印。Unity 既定フォントに字形が無いと豆腐になるため、
        // 差し替えやすいよう定数にしてある。ASCII に落とすなら "<" / ">"。
        private const string SLOT_PREV_GLYPH = "\u25C0";   // ◀
        private const string SLOT_NEXT_GLYPH = "\u25B6";   // ▶
        // 初回表示時の仮値。実際の高さは DoWindow が中身を実測して決める（v0.14.3.0）。
        // 最初の1フレームだけこの値で描かれ、小さすぎると中身が一瞬切れる。
        // 実測のパネル高さは 181px（v0.14.3.1 の実機ログ。タイトル帯の上余白 20 と
        // 下余白 10 を含む）。190 は v0.8.0.2 から据え置きで余裕が 9px しか無かったので、
        // 行を足したときの余地も見て 240 へ上げた（v0.14.3.1）。
        private const int UI_HEIGHT = 240;

        // Map 行のラベル "Map:" の幅。名前が入りきらない時はこの分を名前に回す。
        private const int MAP_LABEL_W = 30;

        // CMD の設定に対応するセットのファイル名に付ける接尾辞（v0.14.0.0）。
        // 設定名とマップ名は別々の名前空間から来るため、同名だと同じファイルへ
        // 書き込んでしまう。衝突はファイル名の層でしか起きないので、そこだけ分ける。
        // 判定・表示・内部キーはすべて素の名前で扱う。
        private const string CUSTOM_KEY_SUFFIX = "_custom";

        // Data Directory の先頭に書くと、ゲームのフォルダに置き換わる（v0.14.4.0）。
        // 文書が既定の置き場所を "<GameRoot>\UserData\..." と書くため、それを
        // 書き写した値がそのまま使えるようにする。ExpandGameRoot() を参照。
        private const string GAME_ROOT_TOKEN = "<GameRoot>";

        // 行内の各要素に与える高さ。スロット行だけでなく最下段の2行にも与える。
        //
        // 【重要】行内で縦位置を揃えるには、層の違う4つを全て揃える必要がある。
        //   fontSize        文字の大きさ              v0.13.0.3 で対処
        //   縦の揃え         箱の中での文字の位置       v0.13.0.2（ラベル）/ v0.14.3.2（トグル）
        //   margin の上下    行内での箱の位置           v0.14.2.0 で対処
        //   高さの明示       箱そのものの大きさ         v0.14.3.0 で対処
        //
        // 縦の揃えは Middle に統一する。Middle なら上下の padding は対称でありさえ
        // すれば文字の中心が箱の中心に来るので、padding の数値で合わせる必要が無い。
        // スロット行は margin が抜けていた。最下段の2行は高さと縦の揃えの**両方**が
        // 抜けていた。トグル（組み込みは UpperLeft、上の padding を 0 に上書き）は自然高さが
        // 約 14px で、ボタン（約 20px）より低い。高さだけ揃えても文字は上端に貼り付いたまま、
        // 揃えだけ Middle にしても 14px の箱は中身がちょうど1行なので動かない。
        // v0.14.3.0（高さ）と v0.14.3.2（揃え）がそろって初めて直った（2026-09-27 実機確認）。
        // 【注意】最下段の GUILayout.Height を「不要」として消さないこと。ずれが戻る。
        private const int SLOT_ROW_H = 20;
        private const float DEFAULT_UI_X = 300f;
        private const float DEFAULT_UI_Y = 200f;

        // === Config項目 ===
        private static ConfigEntry<bool> EnablePlugin;
        private static ConfigEntry<bool> EnableCameraFunction;
        private static ConfigEntry<KeyCode> ToggleUIHotkey;
        private static ConfigEntry<KeyboardShortcut> CameraChangeHotkey;
        private static ConfigEntry<int> MaxCameraSlots;
        private static ConfigEntry<KeyboardShortcut> PositionChangeHotkey;
        private static ConfigEntry<KeyboardShortcut> CameraChangePrevHotkey;
        private static ConfigEntry<KeyboardShortcut> PositionChangePrevHotkey;
        private static ConfigEntry<HotkeyModifiers> CameraChangeModifiers;
        private static ConfigEntry<HotkeyModifiers> CameraChangePrevModifiers;
        private static ConfigEntry<HotkeyModifiers> PositionChangeModifiers;
        private static ConfigEntry<HotkeyModifiers> PositionChangePrevModifiers;
        private static ConfigEntry<int> MaxPositionSlots;
        private static ConfigEntry<int> MaxBasisPositionSlots;
        private static ConfigEntry<bool> AutoCameraFocusEnabled;
        private static ConfigEntry<CameraFocusKey> CameraKey;
        private static ConfigEntry<float> CameraFocusDelay;
        private static ConfigEntry<float> AnimationChangeDelay;
        private static ConfigEntry<float> UIPosX;
        private static ConfigEntry<float> UIPosY;
        private static ConfigEntry<bool> UIVisible;
        private static ConfigEntry<bool> ShowDebugInfo;
        private static ConfigEntry<bool> CamDatDiagnostics;
        private static ConfigEntry<bool> BoneDiagnostics;

        // === キャラ移動へのカメラ追従（v0.11.0.0） ===
        //
        // ゲーム標準機能でキャラを手動移動させたとき、カメラを追従させる。
        // 3軸移動と Y軸回転の両方を吸収し、キャラに対する構図を維持する。
        //
        // 【なぜ必要か】
        // CamDat.Pos は transBase（その体位の基準点）基準の相対座標であり、
        // transBase は体位変更時にしか更新されない。キャラを手動で動かすと
        // femaleChar1 だけが基準点から離れ、カメラは基準点にぶら下がったままになる。
        //
        // 仕様書 v2.1 §6 で「問題2（連続追従）」として先送りした項目にあたる。
        // 当時「アニメ中に root は動かないので実質発生しない」と結論づけたが、
        // 手動移動という経路を見落としていた。
        //
        // 【なぜ CamDat を書くのか】
        // 揺動（ApplySway）は CameraUpdate の Postfix でカメラ transform に足し、
        // CamDat には触らない。演出であって構図そのものではないため。
        //
        // 追従は逆で、CamDat を書く。これは構図の更新そのものだからである。
        //   - 保存されるべき値が変わる（動かした先の構図が正しい）
        //   - ユーザーの矢印キー操作は CamDat.Pos を基準に動くため、
        //     ここがずれていると次の操作で構図が飛ぶ
        // Postfix 方式にすると FOV の片肺書き込みと同じ構造の不具合になる。
        //
        // 【安全策】
        // 体位変更や Position 復元ではキャラが瞬間移動する。そのまま追従すると
        // カメラが振り回されるため、明示的に抑制して基準を張り直す。
        // 閾値は保険であって主たる判定ではない。

        private Vector3 followPrevCharPos;
        private Quaternion followPrevCharRot = Quaternion.identity;
        private bool followBaselineValid;
        private bool followSuppressed;   // ApplyCharacterPositions 等の実行中

        // 1フレームでこれ以上動いたらテレポートとみなす（保険）。
        // 手動移動はゲーム側の charaMoveSpeed で制御された速度なので、
        // 通常は遥かに小さい値にとどまる。
        // 手動移動の1フレームあたり移動量はゲーム側の移動量スライダで決まり、
        // 実測で最大 1.500（スライダ最大）だった。旧値 1.0 はこの範囲の内側に
        // あったため、スライダを上げると通常の移動が「テレポート」と誤判定され、
        // 追従が丸ごと止まっていた（v0.12.1.0 で修正）。
        //
        // 実測の分布:
        //   手動移動   0.025 〜 1.500
        //   （空白）
        //   体位変更   10.677 〜 62.601
        // この空白に閾値を置く。手動移動に 3.3 倍、テレポートに 2.1 倍の余裕。
        private const float FOLLOW_JUMP_POS = 5.0f;

        // === 追従診断（v0.12.0.3） ===
        //
        // SyncCameraToCharacter() は毎フレーム呼ばれるため、抜けるたびにログを出すと
        // 秒間60行のスパムになる。理由が「変わった瞬間」だけ出す。
        // 実測用の一時的な仕掛けであり、原因が判明したら削ってよい。
        private string followBlockReason;

        // 追従対象は femaleChar1 のみ。別のキャラを動かしていると「効かない」ように
        // 見えるため、それを検出して知らせる。
        private Vector3 followOtherPrevPos;
        private bool followOtherValid;
        private float followOtherNextLog;
        private const float FOLLOW_JUMP_YAW = 45f;

        // 基準を現在のキャラ姿勢に張り直す。追従はせず、次フレームから再開する。
        private void ResetFollowBaseline()
        {
            if (femaleChar1 == null)
            {
                followBaselineValid = false;
                return;
            }

            followPrevCharPos = femaleChar1.transform.position;
            followPrevCharRot = femaleChar1.transform.rotation;
            followBaselineValid = true;
        }

        // 抜けた理由を記録する。直前と同じ理由なら出力しない。
        private void FollowBlock(string reason)
        {
            if (followBlockReason == reason) return;
            followBlockReason = reason;
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[FOLLOW] blocked: {reason}");
        }

        // ゲートを全て通過した時点で呼ぶ。ブロックが解けたことを1回だけ知らせる。
        private void FollowUnblock()
        {
            if (followBlockReason == null) return;
            followBlockReason = null;
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[FOLLOW] active (target={(femaleChar1 != null ? femaleChar1.name : "?")})");
        }

        // femaleChar1 が動いていないのに他のキャラが動いた場合に知らせる。
        // アニメ中も root は動かないことを実測済みなので（仕様書 §3）、
        // ここで拾えるのは手動移動か体位変更に伴う配置換えのいずれか。
        // 1秒に1回までに絞る。
        private void DetectOtherCharacterMovement()
        {
            if (ShowDebugInfo?.Value != true) return;

            ChaControl other = maleChar1 != null ? maleChar1 : femaleChar2;
            if (other == null) { followOtherValid = false; return; }

            Vector3 p = other.transform.position;
            if (!followOtherValid)
            {
                followOtherPrevPos = p;
                followOtherValid = true;
                return;
            }

            float moved = (p - followOtherPrevPos).magnitude;
            followOtherPrevPos = p;

            if (moved > 0.002f && Time.unscaledTime >= followOtherNextLog)
            {
                followOtherNextLog = Time.unscaledTime + 1f;
                Logger?.LogInfo($"[FOLLOW] other character moved ({other.name} d={moved:F4}) "
                              + "while femaleChar1 stayed still. Follow tracks femaleChar1 only.");
            }
        }

        private void SyncCameraToCharacter()
        {
            try
            {
                if (!EnablePlugin.Value || !FollowCharacterMovement.Value)
                {
                    FollowBlock(!EnablePlugin.Value ? "plugin disabled" : "Follow Character Movement is off");
                    return;
                }
                if (!isInHScene || !isCameraControlInitialized)
                {
                    FollowBlock(!isInHScene ? "not in H scene" : "camera control not initialized");
                    return;
                }
                if (followSuppressed || isRestoringCamera)
                {
                    FollowBlock(followSuppressed ? "suppressed (applying Pos/BasisPos)" : "camera restoring");
                    ResetFollowBaseline();
                    return;
                }
                if (femaleChar1 == null || cameraControlVer2Instance == null)
                {
                    FollowBlock(femaleChar1 == null ? "femaleChar1 is null" : "cameraControlVer2Instance is null");
                    return;
                }

                Transform tb = GetTransBase();
                if (tb == null)
                {
                    FollowBlock("transBase is null");
                    return;
                }

                Transform f1 = femaleChar1.transform;

                if (!followBaselineValid)
                {
                    FollowBlock("baseline invalid (re-anchoring)");
                    ResetFollowBaseline();
                    return;
                }

                // ここまで来れば追従可能な状態
                FollowUnblock();

                Vector3 curPos = f1.position;
                Quaternion curRot = f1.rotation;

                Vector3 dPos = curPos - followPrevCharPos;
                float dYaw = Mathf.DeltaAngle(followPrevCharRot.eulerAngles.y, curRot.eulerAngles.y);

                // 動いていなければ何もしない
                if (dPos.sqrMagnitude < 1e-10f && Mathf.Abs(dYaw) < 0.0001f)
                {
                    DetectOtherCharacterMovement();
                    return;
                }

                // femaleChar1 が動いたので、他キャラ検出の基準も張り直す
                followOtherValid = false;

                // テレポート判定（保険）。基準を張り直すだけで追従はしない
                if (dPos.magnitude > FOLLOW_JUMP_POS || Mathf.Abs(dYaw) > FOLLOW_JUMP_YAW)
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"[FOLLOW] Jump ignored (dPos={dPos.magnitude:F3} dYaw={dYaw:F1})");
                    ResetFollowBaseline();
                    return;
                }

                var camDatInstance = camDatField.GetValue(cameraControlVer2Instance);
                if (camDatInstance == null) return;

                Vector3 camPos = (Vector3)posField.GetValue(camDatInstance);
                Vector3 camRot = (Vector3)rotField.GetValue(camDatInstance);

                // 注視点をキャラのローカル系で捉え直し、新しいキャラ姿勢で復元する。
                // 保存/復元で使っている変換と同じ考え方。
                Vector3 worldPivot = tb.TransformPoint(camPos);
                Vector3 relPivot = Quaternion.Inverse(followPrevCharRot) * (worldPivot - followPrevCharPos);
                Vector3 newPivot = curPos + curRot * relPivot;

                Vector3 newCamPos = tb.InverseTransformPoint(newPivot);

                // ヨーはキャラの回転分だけ足す。ピッチとロールは影響を受けない
                // （transBase の回転は Y 軸のみ。仕様書 v2.1 §2-3）
                Vector3 newCamRot = new Vector3(
                    camRot.x,
                    NormalizeAngle(camRot.y + dYaw),
                    camRot.z);

                posField.SetValue(camDatInstance, newCamPos);
                rotField.SetValue(camDatInstance, newCamRot);
                camDatField.SetValue(cameraControlVer2Instance, camDatInstance);   // 構造体ライトバック

                followPrevCharPos = curPos;
                followPrevCharRot = curRot;

                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"[FOLLOW] dPos={dPos.magnitude:F4} dYaw={dYaw:F2} -> CamDat.Pos={newCamPos}");
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"[FOLLOW] failed: {ex.Message}");
                followBaselineValid = false;
                followBlockReason = "exception";
            }
        }

        // === Camera 設定の一括コピー（v0.10.0.0） ===
        //
        // ある Position スロット配下の設定済み Camera スロットをまとめて控え、
        // 同一体位の別 Position スロットへ移植する。
        //
        // この機能が成立するのは v0.8.3.0 で相対座標化を済ませたため。
        // Camera データは femaleChar1 基準で保存されているので、コピー先の
        // キャラ配置が違っても「同じアングル」が再現される。
        // 絶対座標のままだったら、カメラが空間を向くだけで無意味だった。
        //
        // 【同一体位内に限定する】
        // 体位が違えばキャラの姿勢も違い、実用的な絵にならないため。
        // クリップボードは「ペースト実行時」と「体位変更時」にクリアする。

        private CameraState[] camClipboard;
        private bool[] camClipboardHas;
        private int camClipboardActionId = -1;
        private int camClipboardPosSlot = -1;

        private bool HasCamClipboard
        {
            get { return camClipboard != null && camClipboardActionId == currentActionId; }
        }

        private void ClearCamClipboard()
        {
            camClipboard = null;
            camClipboardHas = null;
            camClipboardActionId = -1;
            camClipboardPosSlot = -1;
        }

        // CameraState は class なので参照コピーでは共有されてしまう。必ず複製する。
        private CameraState CloneCameraState(CameraState src)
        {
            var dst = new CameraState();
            if (src == null) return dst;

            dst.CamDatPos = CloneVector3Data(src.CamDatPos);
            dst.CamDatRot = CloneVector3Data(src.CamDatRot);
            dst.CamDatDir = CloneVector3Data(src.CamDatDir);
            dst.CamDatFov = src.CamDatFov;
            dst.HasData = src.HasData;
            return dst;
        }

        private Vector3Data CloneVector3Data(Vector3Data src)
        {
            var dst = new Vector3Data();
            if (src == null) return dst;

            dst.x = src.x;
            dst.y = src.y;
            dst.z = src.z;
            dst.rotY = src.rotY;
            return dst;
        }

        private void CopyCameraSet()
        {
            try
            {
                ClampCurrentSlots();

                var set = GetCompositionSetForRead(currentActionId);
                if (set == null || currentPositionSlot >= set.PositionSlots.Length)
                {
                    Logger?.LogWarning($"[CAMSET] No data to copy (Action={currentActionId}, PosSlot={currentPositionSlot})");
                    return;
                }

                var posData = set.PositionSlots[currentPositionSlot];
                int n = posData.CameraSlots.Length;

                var buf = new CameraState[n];
                var has = new bool[n];
                int count = 0;

                for (int i = 0; i < n; i++)
                {
                    if (i >= posData.HasCameraData.Length || !posData.HasCameraData[i]) continue;

                    buf[i] = CloneCameraState(posData.CameraSlots[i]);
                    has[i] = true;
                    count++;
                }

                if (count == 0)
                {
                    ClearCamClipboard();
                    Logger?.LogInfo($"[CAMSET] Position {currentPositionSlot + 1} has no camera data. Nothing copied.");
                    return;
                }

                camClipboard = buf;
                camClipboardHas = has;
                camClipboardActionId = currentActionId;
                camClipboardPosSlot = currentPositionSlot;

                Logger?.LogInfo($"[CAMSET] Copied {count} camera(s) from Position {currentPositionSlot + 1} (Action={currentActionId})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[CAMSET] Copy failed: {ex.Message}");
                ClearCamClipboard();
            }
        }

        private void PasteCameraSet()
        {
            try
            {
                if (camClipboard == null) return;

                // 同一体位内のみ。体位変更時にはクリアされるので通常ここには来ない
                if (camClipboardActionId != currentActionId)
                {
                    Logger?.LogWarning($"[CAMSET] Clipboard belongs to Action={camClipboardActionId}, current is {currentActionId}. Discarded.");
                    ClearCamClipboard();
                    return;
                }

                ClampCurrentSlots();

                var set = GetOrCreateCompositionSet(currentActionId);
                if (currentPositionSlot >= set.PositionSlots.Length)
                {
                    Logger?.LogWarning($"[CAMSET] Paste target out of range (PosSlot={currentPositionSlot})");
                    return;
                }

                var posData = set.PositionSlots[currentPositionSlot];
                int applied = 0;

                for (int i = 0; i < camClipboard.Length; i++)
                {
                    if (!camClipboardHas[i]) continue;
                    if (i >= posData.CameraSlots.Length || i >= posData.HasCameraData.Length) continue;

                    // 控えに含まれるスロットのみ上書きする。含まれないスロットは温存
                    posData.CameraSlots[i] = CloneCameraState(camClipboard[i]);
                    posData.HasCameraData[i] = true;
                    applied++;
                }

                int fromSlot = camClipboardPosSlot;

                // 複数スロットをまとめて書き換えるため、ここで確定させる
                ExecuteSaveBlock();

                ClearCamClipboard();

                Logger?.LogInfo($"[CAMSET] Pasted {applied} camera(s) from Position {fromSlot + 1} to Position {currentPositionSlot + 1} (Action={currentActionId})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[CAMSET] Paste failed: {ex.Message}");
                ClearCamClipboard();
            }
        }

        // === カメラ揺動（v0.9.0.0） ===
        private static ConfigEntry<bool> EnableCameraSway;
        private static ConfigEntry<KeyCode> SwayToggleHotkey;
        private static ConfigEntry<SwayBone> SwayTargetBone;
        private static ConfigEntry<float> SwayAmplitude;
        private static ConfigEntry<KeyCode> SwayAmpUpHotkey;
        private static ConfigEntry<KeyCode> SwayAmpDownHotkey;
        private static ConfigEntry<float> SwayAmplitudeStep;
        private static ConfigEntry<float> SwayMaxOffset;
        private static ConfigEntry<float> SwaySuppressTail;
        private static ConfigEntry<float> SwaySmoothing;

        // === キャラ移動へのカメラ追従（v0.11.0.0） ===
        private static ConfigEntry<bool> FollowCharacterMovement;
        private static ConfigEntry<string> DataDirectory;
        private static ConfigEntry<bool> FollowImporterConfig;

        // === データ管理 ===
        private Dictionary<int, CompositionSet> actionData;
        private Dictionary<string, BasisPositionSet> basisPositionData;
        private int currentPositionSlot = 0;
        private int currentCameraSlot = 0;
        private int currentBasisPositionSlot = 0;
        // 保存先は GameRoot 基準に変えたため、DLL の位置は保持しない（v0.14.1.0）
        private string configDirectory;

        // === YGGDRA制御フロー管理変数 ===
        // currentMapKey は「いま実際に使っている鍵」。バニラのマップ名のことも、
        // CMD の設定名のこともある。保存先の決定・内部キー・表示すべてこれを使う。
        private string currentMapKey = "";
        private string lastMapKey = "";

        // === HS2 Custom Map Director (CMD) 連携（v0.14.0.0） ===
        //
        // CMD は後日リリース予定の別プラグイン（旧称 HS2-Importer）。CD は CMD より先に
        // 単独で公開する（2026-10-09 の方針）。CMD が入っていなければ、この連携は何もしない。
        // 内部の識別子（FollowImporterConfig、importer* 等）は旧称のまま残している。
        // CMD 側の型名 HS2_ImporterCore も据え置きなので、名前を揃える利点が薄いため。
        //
        // CMD は「最後に保存または読み出した設定の名前」を公開するだけで、
        // 構成が設定と一致していることは保証しない。一致を前提にした処理を書かないこと。
        // 詳細は HS2-CustomMap_Director/documents/CompositionDirector連携 CMD側API.md

        private string vanillaMapKey = "";      // ゲームから得た素のマップ名
        private bool currentKeyIsCustom;         // 現在の鍵が CMD の設定名か
        private string importerConfigName;       // CMD が公開している設定名

        private Type importerType;
        private PropertyInfo importerNameProp;
        private PropertyInfo importerSerialProp;
        private int importerLastSerial = int.MinValue;
        private float importerResolveTry = -99f;
        private int currentActionId = -1;
        private int lastActionId = -1;
        private bool isInHScene = false;
        private bool mapKeyDetected = false;

        // === CameraControl_Ver2検出戦略変数 ===
        private int cameraDetectionCount = 0;
        private const int MAX_DETECTION_ATTEMPTS = 100;
        private bool cameraDetectionInProgress = false;
        private bool cameraFunctionEnabled = true;

        // === GUI関連 ===
        // showUI の初期化子は実効値ではない。InitializeConfig() で
        // UIVisible（永続化された前回の状態）から上書きされる。
        private bool showUI = false;
        private bool lastShowUI = false;

        // GUIStyle は毎フレーム生成しない（Unity 2018.4.11f1 の IMGUI 指針）。
        // GUI.skin は OnGUI の外では有効でないため、生成は OnGUI 内で遅延実行する。
        private GUIStyle cachedWindowStyle;
        private GUIStyle cachedButtonStyle;
        private GUIStyle cachedSmallButtonStyle;
        private GUIStyle cachedToggleStyle;
        private GUIStyle cachedSlotLabelStyle;
        private GUIStyle cachedArrowStyle;
        private GUIStyle cachedMapNameStyle;
        private string cachedWindowTitle;

        // 下端をコンテンツの実寸に合わせるための測定値（v0.14.3.0）。
        // 負の間は未測定。DoWindow の Repaint パスで更新する。
        private float measuredContentHeight = -1f;

        // Map 名の表示幅の測定結果（v0.14.3.0）。CalcSize は OnGUI の中でしか
        // 正しい値を返さない。加えて毎フレーム測る必要も無い（名前が変わるのは
        // 体位変更と CMD の設定読み出しの時だけ）ので、前回測った文字列と
        // 同じなら測り直さない。GUIContent も使い回す（毎フレーム new しない）。
        private readonly GUIContent mapNameContent = new GUIContent();
        private string mapNameMeasured;
        private bool mapShowPrefix = true;
        private Rect windowRect = new Rect(DEFAULT_UI_X, DEFAULT_UI_Y, UI_WIDTH, UI_HEIGHT);

        // === キャラクター参照 ===
        private ChaControl femaleChar1;
        private ChaControl femaleChar2;
        private ChaControl maleChar1;
        private ChaControl maleChar2;
        private HScene hSceneInstance;

        // === CameraControl_Ver2参照 ===
        private Type cameraControlVer2Type;
        private object cameraControlVer2Instance;
        private FieldInfo camDatField;
        private Type camDatType;
        private FieldInfo posField, rotField, dirField, fovField;
        private bool isCameraControlInitialized = false;

        // === 状態管理 ===
        private float lastKeyPressTime = 0f;
        private const float KEY_PRESS_COOLDOWN = 0.3f;

        // 文字入力欄にキーボードの入力先があるか（v0.15.0.0）。Update の頭で毎フレーム更新する。
        private bool textInputFocused;
        private float lastHotkeySuppressLogTime = -99f;

        // 保存ファイルから読み込む際に確保するスロット数の上限。
        // 破損データに巨大な添字が入っていた場合の暴走を防ぐ。
        private const int MAX_SLOT_CAPACITY = 64;

        // === FOV永続管理（v0.8.1.0改訂） ===
        private float lastUserFov = 23f;

        // FOV操作キーを検知してから CamDat.Fov を取り込み続ける猶予フレーム数。
        // ゲーム側の FOV 更新は LateUpdate で行われ Update より後になるため、
        // キー押下フレームだけを見ると最後の変化を取りこぼす。
        private int fovSyncFrames = 0;
        private const int FOV_SYNC_GRACE_FRAMES = 10;

        // === FOV制御用リフレクション（v0.8.1.0） ===
        private PropertyInfo thisCameraProperty;
        private FieldInfo limitFovField, limitFovMinField;
        private FieldInfo transBaseField;

        // Harmonyパッチ用static変数
        private static bool isRestoringCamera = false;
        private static CompositionDirector instance;   // CameraUpdate の Postfix から呼ぶため
        private new static BepInEx.Logging.ManualLogSource Logger;

        private void Awake()
        {
            Logger = base.Logger;
            instance = this;

            try
            {
                Logger?.LogInfo($"=== START COMPOSITION_DIRECTOR v{VersionInfo.PluginVersion} INITIALIZATION ===");
                Logger?.LogInfo("=== BETA_VERSION_WITH_FOV_PERSISTENCE ===");

                // 保存先の既定は UserData 配下（v0.14.1.0）。
                //
                // 以前は DLL の位置（BepInEx\plugins\...）配下に置いていたが、そこは
                // MODパックの更新やプラグインの入れ直しで丸ごと置き換わる場所であり、
                // ユーザーが作った構図データを置いてよい場所ではない。
                // UserData は他のプラグイン（BetterHScenes 等）も使う慣習的な置き場で、
                // バックアップの対象にもなる。
                //
                // ゲームルートは Paths.GameRootPath で取る。BepInEx が起動時に確定させて
                // いる値なので、インストール先がどこでも正しい。DLL の位置から遡る方法は
                // まさに今回避けたい依存そのもの。
                configDirectory = Path.Combine(Paths.GameRootPath, "UserData", "CompositionDirector_save");

                try
                {
                    Directory.CreateDirectory(configDirectory);
                    Logger?.LogInfo($"Data directory: {configDirectory}");
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Cannot create data directory: {ex.Message}");
                    configDirectory = Path.Combine(Paths.ConfigPath, "CompositionDirector");
                    Directory.CreateDirectory(configDirectory);
                }

                InitializeConfig();

                // Config を読んだ後でなければ保存先の指定が反映できない。
                // 既定値（プラグインフォルダ配下）は上で決めてあるので、上書きだけを行う。
                ResolveDataDirectory();

                actionData = new Dictionary<int, CompositionSet>();
                basisPositionData = new Dictionary<string, BasisPositionSet>();

                InitializeCameraTypeDetection();

                var harmony = new Harmony(VersionInfo.GUID);
                harmony.PatchAll(typeof(CompositionDirector));

                // PatchAll(Type) は「そのクラス自身の注釈付きメソッド」は処理するが、
                // 入れ子クラスは列挙しない。そのため入れ子で定義したパッチクラスは
                // 明示的に登録する必要がある。
                //
                // 実測（v0.8.2.3）: [HARMONY] ログが全ログで0行、isRestoringCamera が
                // 56回立っているのに LateUpdate blocked が0回。一方 CompositionDirector
                // 直下のメソッド属性である OnAnimationChange は96回動作していた。
                //
                // なお BaseCameraControlPatch（LateUpdate 抑止）も同じ理由で
                // 一度も適用されていない。実害が無いこと、および有効化すると
                // CameraUpdate() ごと止まって CamDat の反映が遅延しスナップする
                // 懸念があることから、ここでは意図的に登録しない。
                // 詳細と今後の扱いは CLAUDE.md の該当節を参照。
                harmony.PatchAll(typeof(SetBaseTracePatch));
                harmony.PatchAll(typeof(CameraSwayPatch));

                Logger?.LogInfo($"=== COMPOSITION_DIRECTOR v{VersionInfo.PluginVersion} LOADED_SUCCESSFULLY ===");

                StartCoroutine(UnifiedPollingSystem());

            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to initialize: {ex}");
            }
        }

        private void InitializeConfig()
        {
            // 【注意】この型は CM 18.3 の DLL には無く、cmType は常に null になる（v0.15.0.0 で確認）。
            // つまり以下の Config で cmType を使っている箇所は、属性を何も付けていない。
            // 害は無いので据え置いている。並び順などの属性が要るときは CmAttr() を使うこと。
            var cmType = Type.GetType("ConfigurationManager.ConfigurationManagerAttributes, ConfigurationManager");

            EnablePlugin = Config.Bind("01. General", "Enable Plugin", false,
                new ConfigDescription("Enable Composition Director functionality", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            EnableCameraFunction = Config.Bind("02. General", "Enable Camera Function", true,
                new ConfigDescription("Enable camera control functionality", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            ToggleUIHotkey = Config.Bind("03. Hotkeys", "Toggle UI", KeyCode.None,
                new ConfigDescription("Key to toggle Composition Director UI", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            // 各キーの直下に、そのキーの修飾キー（v0.15.0.0）を並べる。
            // CM はセクション内を Order の降順、次に名前順で並べる。名前順のままだと
            // "Camera Change (Prev)" が "Camera Change Modifiers" より前に来てしまい、
            // どの修飾キーがどのキーのものか分かりにくいため、この4項目は順番を指定する。
            CameraChangeHotkey = Config.Bind("04. Cam Hotkey", "Camera Change",
                new KeyboardShortcut(KeyCode.Mouse3),
                new ConfigDescription("Hotkey to change camera slot", null,
                    CmAttr(4)));

            // 修飾キーの組み合わせ（v0.15.0.0）。多ボタンマウスのサイドボタンが
            // Ctrl+C などに固定されていても使えるようにするため。判定は IsSlotHotkeyPressed()。
            CameraChangeModifiers = Config.Bind("04. Cam Hotkey", "Camera Change Modifiers", HotkeyModifiers.None,
                new ConfigDescription("Modifier keys to hold together with Camera Change. Left/right keys are not " +
                                      "distinguished. Leave all unchecked to use the shortcut as it is.", null,
                    CmAttr(3)));

            // 戻し用は既存セクションへ相乗りさせる。新しい番号を切ると既存ユーザーの
            // .cfg に別セクションが生まれ、設定値の対応が崩れるため（実装仕様書 §11-2）。
            CameraChangePrevHotkey = Config.Bind("04. Cam Hotkey", "Camera Change (Prev)",
                new KeyboardShortcut(KeyCode.None),
                new ConfigDescription("Hotkey to go back one camera slot. Unassigned by default.", null,
                    CmAttr(2)));

            CameraChangePrevModifiers = Config.Bind("04. Cam Hotkey", "Camera Change (Prev) Modifiers", HotkeyModifiers.None,
                new ConfigDescription("Modifier keys to hold together with Camera Change (Prev). Left/right keys are " +
                                      "not distinguished. Leave all unchecked to use the shortcut as it is.", null,
                    CmAttr(1)));

            MaxCameraSlots = Config.Bind("05. Slots", "Max Camera Slots", 5,
                new ConfigDescription("Maximum number of camera slots (1-9)",
                    new AcceptableValueRange<int>(1, 9),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            PositionChangeHotkey = Config.Bind("06. Pos Hotkey", "Position Change",
                new KeyboardShortcut(KeyCode.Mouse4),
                new ConfigDescription("Hotkey to change position slot", null,
                    CmAttr(4)));

            PositionChangeModifiers = Config.Bind("06. Pos Hotkey", "Position Change Modifiers", HotkeyModifiers.None,
                new ConfigDescription("Modifier keys to hold together with Position Change. Left/right keys are not " +
                                      "distinguished. Leave all unchecked to use the shortcut as it is.", null,
                    CmAttr(3)));

            PositionChangePrevHotkey = Config.Bind("06. Pos Hotkey", "Position Change (Prev)",
                new KeyboardShortcut(KeyCode.None),
                new ConfigDescription("Hotkey to go back one position slot. Unassigned by default.", null,
                    CmAttr(2)));

            PositionChangePrevModifiers = Config.Bind("06. Pos Hotkey", "Position Change (Prev) Modifiers", HotkeyModifiers.None,
                new ConfigDescription("Modifier keys to hold together with Position Change (Prev). Left/right keys are " +
                                      "not distinguished. Leave all unchecked to use the shortcut as it is.", null,
                    CmAttr(1)));

            MaxPositionSlots = Config.Bind("07. Slots", "Max Position Slots", 4,
                new ConfigDescription("Maximum number of position slots (1-9)",
                    new AcceptableValueRange<int>(1, 9),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            MaxBasisPositionSlots = Config.Bind("08. Slots", "Max Basis Position Slots", 4,
                new ConfigDescription("Maximum number of basis position slots (1-9)",
                    new AcceptableValueRange<int>(1, 9),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            AutoCameraFocusEnabled = Config.Bind("09. Camera Focus", "Auto Camera Focus Key", true,
                new ConfigDescription("Automatically press camera focus key after position change", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            CameraKey = Config.Bind("10. Camera Focus", "Camera Focus Key", CameraFocusKey.W_Chest,
                new ConfigDescription("Camera focus key (Q=Head, W=Chest, E=Groin)", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            CameraFocusDelay = Config.Bind("11. Timing", "Camera Focus Delay", 0.2f,
                new ConfigDescription("Delay for camera operations (focus key, camera restore) in seconds",
                    new AcceptableValueRange<float>(0.01f, 2.0f),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            AnimationChangeDelay = Config.Bind("12. Timing", "Animation Change Delay", 0.6f,
                new ConfigDescription("Delay after animation change before applying positions",
                    new AcceptableValueRange<float>(0.1f, 3.0f),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            // 13 / 14 は旧 "Advanced Camera" 項目（PreferredCameraMode / CameraControlTimeout）。
            // 参照されていなかったため v0.8.2.0 で削除した。既存ユーザーの .cfg にある
            // 15-17 の設定値を維持するため、番号は詰めていない。

            UIPosX = Config.Bind("15. UI", "Window Position X", DEFAULT_UI_X,
                            new ConfigDescription("UI Window X Position", null,
                                cmType != null ? Activator.CreateInstance(cmType) : null));

            UIPosY = Config.Bind("16. UI", "Window Position Y", DEFAULT_UI_Y,
                new ConfigDescription("UI Window Y Position", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            // パネルの表示状態をゲーム再起動後も引き継ぐ（v0.11.1.0）。
            //
            // セクションは新規に切らず "15. UI" へ相乗りさせている。番号を増やすと
            // 既存ユーザーの .cfg に別セクションが生まれ、設定値の対応が崩れるため。
            // "26. Sway" が既に複数キーを持つのと同じ形。
            UIVisible = Config.Bind("15. UI", "Show Panel", true,
                new ConfigDescription("Show the panel on entering an H scene. Tracks the state you last left it in.", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            ShowDebugInfo = Config.Bind("17. Debug", "Show Debug Info", false,
                new ConfigDescription("Show debug information in console", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            CamDatDiagnostics = Config.Bind("18. Debug", "CamDat Diagnostics", false,
                new ConfigDescription("Log CamDat / transBase / character transforms every second. For relative-coordinate investigation only.", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            BoneDiagnostics = Config.Bind("19. Debug", "Bone Diagnostics", false,
                new ConfigDescription("Log male bone amplitude and HScene.ctrlFlag changes. For camera-sway investigation only.", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            EnableCameraSway = Config.Bind("20. Sway", "Enable Camera Sway", false,
                new ConfigDescription("Add a slight handheld-style shake by following a male bone. Composition is unchanged.", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            // 既定はキー無し（v0.11.2.0）。パネルは常時表示が前提で、揺動の
            // オンオフはパネル上の Shake CAM で足りるため、既定でキーを
            // 占有しない。必要な人が環境に応じて後から割り当てる。
            SwayToggleHotkey = Config.Bind("21. Sway", "Sway Toggle Hotkey", KeyCode.None,
                new ConfigDescription("Key to toggle camera sway on/off during play. Unassigned by default.", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            // 揺動量をプレイ中に増減するキー（v0.12.0.0）。既定は割り当て無し。
            // 押している間だけ連続で変化する。空きキーが乏しいので、
            // 使う人が環境に応じて決める（reference_documents の
            // ゲーム内ショートカット一覧を参照）。
            SwayAmpUpHotkey = Config.Bind("21. Sway", "Amplitude Up Hotkey", KeyCode.None,
                new ConfigDescription("Hold to increase sway strength. Unassigned by default.", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            SwayAmpDownHotkey = Config.Bind("21. Sway", "Amplitude Down Hotkey", KeyCode.None,
                new ConfigDescription("Hold to decrease sway strength. Unassigned by default.", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            SwayTargetBone = Config.Bind("22. Sway", "Target Bone", SwayBone.Kosi01,
                new ConfigDescription("Male bone to follow. Kosi01 is the most stable across pose changes.", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            SwayAmplitude = Config.Bind("23. Sway", "Amplitude", 60f,
                new ConfigDescription("Sway strength in percent. 100 = raw bone motion.",
                    new AcceptableValueRange<float>(SWAY_AMP_MIN, SWAY_AMP_MAX),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            SwayAmplitudeStep = Config.Bind("23. Sway", "Amplitude Step", 500f,
                new ConfigDescription("How fast the hotkeys change Amplitude, in percent per second.",
                    new AcceptableValueRange<float>(1f, 1000f),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            // 揺動量はホットキー調整のために内部 float を正とする（v0.12.0.0）。
            // ConfigurationManager 等から Config 側が直接書き換えられた場合も
            // 追随させないと、内部値が古いまま揺動に効き続ける。
            swayAmplitudeLive = SwayAmplitude.Value;
            SwayAmplitude.SettingChanged += (sender, args) =>
            {
                if (!swayAmpAdjusting) swayAmplitudeLive = SwayAmplitude.Value;
            };

            SwayMaxOffset = Config.Bind("24. Sway", "Max Offset", 0.4f,
                new ConfigDescription("Hard cap on the camera offset. Prevents blow-ups on violent motion.",
                    new AcceptableValueRange<float>(0f, 5f),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            SwaySuppressTail = Config.Bind("25. Sway", "Suppress Tail", 0.4f,
                new ConfigDescription("Seconds to suppress sway after a pose change",
                    new AcceptableValueRange<float>(0f, 2f),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            SwaySmoothing = Config.Bind("26. Sway", "Smoothing", 18f,
                new ConfigDescription("Baseline follow rate. Lower = the camera drifts back more slowly.",
                    new AcceptableValueRange<float>(0.5f, 20f),
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            FollowCharacterMovement = Config.Bind("27. Camera Follow", "Follow Character Movement", true,
                new ConfigDescription("Keep the camera framed on the character when you move her manually (3-axis translation and Y rotation).", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            // 説明文は v0.14.1.0 で既定を UserData 配下へ移したときに直し忘れていた（v0.14.4.0）。
            // 先頭の <GameRoot> はゲームのフォルダに置き換わる（v0.14.4.0。ExpandGameRoot）。
            DataDirectory = Config.Bind("28. Data", "Data Directory", "",
                new ConfigDescription("Where to keep the saved compositions. Leave empty to use " +
                                      "UserData\\CompositionDirector_save in the game folder. " +
                                      "A leading <GameRoot> is replaced with the game folder.", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            // v0.14.4.0 で "29. Importer / Follow Importer Config" から改名した。
            // 旧キーの値は引き継がれない（未公開なので移行処理は入れていない）。
            FollowImporterConfig = Config.Bind("29. Custom Map Director", "Follow CMD Config", true,
                new ConfigDescription("Switch the composition set to match the config loaded in HS2 Custom Map Director. " +
                                      "(HS2 Custom Map Director is a separate plugin scheduled for a later release.)", null,
                    cmType != null ? Activator.CreateInstance(cmType) : null));

            windowRect = new Rect(UIPosX.Value, UIPosY.Value, UI_WIDTH, UI_HEIGHT);

            // 前回終了時の表示状態を復元する。lastShowUI も合わせておかないと
            // 初回のトグルで「OFF から ON になった」判定が誤って走る。
            showUI = UIVisible.Value;
            lastShowUI = showUI;
        }

        private void Update()
        {
            // int の比較1回で済むので毎フレーム呼んでよい。
            if (isInHScene) PollImporter();

            // 文字入力中はキーボードに割り当てたホットキーを止める（v0.15.0.0）。
            // 1フレームに1回だけ判定し、以降のホットキー処理はこの値を見る。
            textInputFocused = IsTextInputFocused();

            if (KeyHotkeyDown(ToggleUIHotkey.Value, "Toggle UI"))
            {
                showUI = !showUI;
                UIVisible.Value = showUI;   // 次回起動へ引き継ぐ（v0.11.1.0）

                if (showUI && !lastShowUI)
                {
                    windowRect.x = UIPosX.Value;
                    windowRect.y = UIPosY.Value;
                }
                lastShowUI = showUI;
            }

            // === FOV変更監視（v0.8.1.0改訂 / 記述確定 v0.8.1.2） ===
            // v0.8.0.2 までは Camera.main.fieldOfView を毎フレーム差分監視していた。
            // これを、ゲーム側の FOV 操作キーが押されたときだけ CamDat.Fov を取り込む
            // 方式へ変更している。
            //
            // 変更理由:
            //   差分監視は「誰が FOV を書いたか」を区別できない。ゲーム側は
            //   Reset(0) でも fieldOfView を書く（R キー、および F トグルが ON のときの
            //   体位変更時リセット）。差分監視だとこれを「ユーザー操作」として取り込み、
            //   「体位が切り替わってもユーザーの FOV は維持されるべき」という
            //   本機能の要件そのものを壊す。キー起点ならこの経路は原理的に混入しない。
            //   また CamDat.Fov はゲーム側の正となる値であり、Unity 層より直接的。
            //
            //   トレードオフ: 対象キー以外の経路（他プラグイン等）による FOV 変更は
            //   拾えなくなる。現状そうした経路は確認されていない。
            //
            // 対象キーは BaseCameraControl_Ver2.InputKeyProc に対応する。
            //   Equals / RightBracket … パース変更（ゲーム内表記: れ・け・む）
            //   Semicolon             … パースリセット
            //   R                     … カメラリセット（Reset(0) で Fov も戻る）
            if (EnablePlugin.Value && isInHScene && !isRestoringCamera && isCameraControlInitialized)
            {
                if (Input.GetKey(KeyCode.Equals) || Input.GetKey(KeyCode.RightBracket) ||
                    Input.GetKeyDown(KeyCode.Semicolon) || Input.GetKeyDown(KeyCode.R))
                {
                    fovSyncFrames = FOV_SYNC_GRACE_FRAMES;
                }

                if (fovSyncFrames > 0)
                {
                    fovSyncFrames--;

                    float currentFov;
                    if (TryGetCamDatFov(out currentFov) &&
                        Mathf.Abs(currentFov - lastUserFov) > 0.001f)
                    {
                        lastUserFov = currentFov;
                        if (ShowDebugInfo?.Value == true)
                            Logger?.LogInfo($"[FOV_DETECT] User FOV changed: {lastUserFov}");
                    }
                }
            }

            if (EnablePlugin.Value && isInHScene)
            {
                PerformHotkeyDetection();
            }

            // ボーン診断のサンプリング。跳躍がスナップかブレンドかの判別には
            // フレーム単位の分解能が要るため、1秒ポーリングではなくここで採る。
            if (BoneDiagnostics.Value && isInHScene)
            {
                SampleBones();
                SampleAnimators();
            }

            // カメラ揺動のオンオフ
            if (KeyHotkeyDown(SwayToggleHotkey.Value, "Sway Toggle"))
            {
                EnableCameraSway.Value = !EnableCameraSway.Value;
                Logger?.LogInfo($"[SWAY] {(EnableCameraSway.Value ? "ON" : "OFF")}");
            }

            // 揺動量のホットキー調整（v0.12.0.0）
            //
            // ConfigEntry.Value の setter は BepInEx 既定の SaveOnConfigSet = true
            // により、値を書くたびに .cfg 全体をディスクへ書き出す。押しっぱなしで
            // 毎フレーム書くと秒間60回の書き込みになるため、調整中は内部 float だけを
            // 動かし、キーを離した時点で一度だけ Config へ反映する。
            //
            // 画面の揺れ方そのものがフィードバックなので、通常時はログを出さない。
            bool ampUp = KeyHotkeyHeld(SwayAmpUpHotkey.Value);
            bool ampDown = KeyHotkeyHeld(SwayAmpDownHotkey.Value);

            if (ampUp != ampDown)   // 同時押しは相殺して何もしない
            {
                swayAmplitudeLive = Mathf.Clamp(
                    swayAmplitudeLive + SwayAmplitudeStep.Value * Time.deltaTime * (ampUp ? 1f : -1f),
                    SWAY_AMP_MIN, SWAY_AMP_MAX);
                swayAmpAdjusting = true;
            }
            else if (swayAmpAdjusting)
            {
                // ディスクへの書き込みはここ1回だけ
                swayAmpAdjusting = false;
                SwayAmplitude.Value = swayAmplitudeLive;

                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"[SWAY] Amplitude={swayAmplitudeLive:F1}");
            }

            // キャラ移動へのカメラ追従。LateUpdate より前に CamDat を更新するため、
            // 同フレームの CameraUpdate に反映される
            if (isInHScene && isCameraControlInitialized)
            {
                // UpdateCharacterReferences() は FindObjectsOfType による全走査なので
                // 毎フレーム呼んではいけない。参照が失われたときだけ取り直す。
                // 通常は Pos/Cam の適用時と保存時に更新されている。
                if (femaleChar1 == null)
                    UpdateCharacterReferences();

                SyncCameraToCharacter();
            }

            // 揺動対象の解決。Config でボーンを変えたら取り直す
            if (EnableCameraSway.Value && isInHScene && isCameraControlInitialized)
            {
                if (!swayResolved || swayBone == null ||
                    swayBone.name != SWAY_BONE_NAMES[Mathf.Clamp((int)SwayTargetBone.Value, 0, SWAY_BONE_NAMES.Length - 1)])
                {
                    UpdateCharacterReferences();
                    ResolveSwayTargets();
                }
            }
        }

        private IEnumerator UnifiedPollingSystem()
        {
            while (true)
            {
                yield return new WaitForSeconds(1.0f);

                try
                {
                    PerformHSceneDetection();

                    if (!EnablePlugin.Value || !isInHScene)
                        continue;

                    if (!mapKeyDetected)
                    {
                        PerformMapKeyDetection();
                    }

                    PerformAnimationChangeDetection();

                    if (CamDatDiagnostics.Value)
                    {
                        UpdateCharacterReferences();
                        LogCamDatTracking();
                    }

                    if (BoneDiagnostics.Value)
                    {
                        if (!boneTargetsResolved)
                        {
                            UpdateCharacterReferences();
                            ResolveBoneTargets();
                        }

                        if (!animTargetsResolved)
                        {
                            UpdateCharacterReferences();
                            ResolveAnimators();
                        }

                        LogBoneStats();
                        LogCtrlFlagMembers();
                        TrackCtrlFlagChanges();

                        // ctrlFlag イベント起点の変位計測を締める
                        if (ctrlFlagMeasurePending > 0)
                        {
                            ctrlFlagMeasurePending--;
                            if (ctrlFlagMeasurePending == 0)
                                EndTransitionMeasure(-1);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Unified polling error: {ex.Message}");
                }
            }
        }

        private void PerformHSceneDetection()
        {
            try
            {
                bool currentlyInHScene = FindObjectOfType<HScene>() != null;

                if (!isInHScene && currentlyInHScene)
                {
                    isInHScene = true;
                    hSceneInstance = FindObjectOfType<HScene>();
                    mapKeyDetected = false;

                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo("HScene detected - starting initial delay");

                    StartCoroutine(InitialDelayAndDetection());
                }

                if (isInHScene && !currentlyInHScene)
                {
                    ExecuteEndProcessBlock();
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"HScene detection error: {ex.Message}");
            }
        }

        private void PerformMapKeyDetection()
        {
            try
            {
                string newMapKey = GetCurrentMapKey();
                if (newMapKey != vanillaMapKey && !string.IsNullOrEmpty(newMapKey))
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"MAP CHANGE DETECTED: {vanillaMapKey} → {newMapKey}");

                    vanillaMapKey = newMapKey;

                    // 実際にどちらの鍵を使うかは ApplyEffectiveKey が決める。
                    // CMD の設定が効いていれば、そちらが優先される。
                    ApplyEffectiveKey("map");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"MapKey detection error: {ex.Message}");
            }
        }

        private void PerformAnimationChangeDetection()
        {
            try
            {
                int newActionId = GetCurrentActionId();
                if (newActionId != currentActionId)
                {
                    lastActionId = currentActionId;
                    currentActionId = newActionId;

                    // Camera 設定の控えは同一体位内でのみ有効。体位が変わったら破棄する
                    ClearCamClipboard();

                    // 体位変更はキャラを瞬間移動させる。追従の基準を張り直す
                    followBaselineValid = false;
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"Animation change detected: {lastActionId} → {currentActionId}");
                    StartCoroutine(ExecutePosDataApplyBlock());
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Animation detection error: {ex.Message}");
            }
        }

        // スロットの送り／戻し。剰余式が5箇所に散っていたのをここへ集約した（v0.13.0.0）。
        // 戻しは (cur - 1 + max) % max とする。C# の % は負数に対して負を返すため、
        // + max を落とすと -1 が返って添字が壊れる。
        private static int NextSlot(int cur, int max)
        {
            if (max <= 0) return 0;
            return (cur + 1) % max;
        }

        private static int PrevSlot(int cur, int max)
        {
            if (max <= 0) return 0;
            return (cur - 1 + max) % max;
        }

        // === ホットキーの判定（v0.15.0.0） ===

        // 文字入力欄（IMGUI の TextField 等）にキーボードの入力先があるか。
        // ConfigurationManager や CMD の設定名欄で文字や Ctrl+C / Ctrl+V を打っている間に、
        // CD のホットキーが反応しないようにするため。
        //
        // keyboardControl は入力先のコントロール ID で、0 なら入力先が無い。それが
        // 文字入力欄かどうかは、その ID に TextEditor の状態が付いているかで見る
        // （GUI.TextField は描くたびに GetStateObject で TextEditor を作る）。
        // QueryStateObject は作らずに問い合わせるだけなので副作用が無い。
        //
        // uGUI の InputField は対象外。H シーンのバニラ UI に文字入力欄は無い見込み。
        // 入力欄に打った後で 3D 画面をクリックしても入力先が外れず残るかは未確認
        // （v0.15.0.0 時点）。残る場合、外れるまでキーボードのホットキーは止まったまま。
        private static bool IsTextInputFocused()
        {
            int id = GUIUtility.keyboardControl;
            return id != 0 && GUIUtility.QueryStateObject(typeof(TextEditor), id) != null;
        }

        // マウスやゲームパッドのボタンは文字入力と衝突しないので止めない。
        // KeyCode はキーボードのキーがすべて Mouse0 より前に並んでいる。
        private static bool IsKeyboardKey(KeyCode key)
        {
            return key != KeyCode.None && key < KeyCode.Mouse0;
        }

        // KeyCode 型のホットキー（Toggle UI / 揺動）用。押された瞬間だけ真。
        private bool KeyHotkeyDown(KeyCode key, string name)
        {
            if (key == KeyCode.None || !Input.GetKeyDown(key)) return false;
            if (textInputFocused && IsKeyboardKey(key))
            {
                LogHotkeySuppressed(name);
                return false;
            }
            return true;
        }

        // KeyCode 型のホットキーのうち、押している間ずっと効くもの（揺動量の増減）用。
        // 毎フレーム呼ばれるのでログは出さない。
        private bool KeyHotkeyHeld(KeyCode key)
        {
            if (key == KeyCode.None || !Input.GetKey(key)) return false;
            return !(textInputFocused && IsKeyboardKey(key));
        }

        // スロット切替ホットキーが押されているか。
        //
        // 修飾キーのチェックが1つも無ければ、従来どおり BepInEx の IsPressed() に任せる
        // （挙動は v0.14 以前と同じ）。IsPressed() は指定外の修飾キーが押されていると
        // 反応しないため、チェックボックスで修飾キーを足す場合には使えない。
        //
        // チェックがある場合は主キーだけを見て、修飾キーはここで判定する。
        // ショートカット側に記録された修飾キーは見ない（チェックボックスが優先）。
        //   - 左右は問わない（Ctrl なら左右どちらでも）
        //   - 完全一致。Ctrl だけの割り当ては Ctrl+Shift では反応しない
        //   - マウスのソフトが送る Ctrl+C のようなマクロは、押してから離すまでが
        //     1フレームに収まることがある。そのとき GetKey（押している状態）では一度も
        //     見えないので、GetKeyDown（このフレームで押された）も拾う。
        //     本当に拾えるかは実機で要確認（v0.15.0.0 時点）
        private static bool IsSlotHotkeyPressed(KeyboardShortcut shortcut, HotkeyModifiers mods)
        {
            if (mods == HotkeyModifiers.None)
                return shortcut.IsPressed();

            KeyCode main = shortcut.MainKey;
            if (main == KeyCode.None) return false;
            if (!Input.GetKey(main) && !Input.GetKeyDown(main)) return false;

            return ModifierMatches(mods, HotkeyModifiers.Shift, KeyCode.LeftShift, KeyCode.RightShift) &&
                   ModifierMatches(mods, HotkeyModifiers.Ctrl, KeyCode.LeftControl, KeyCode.RightControl) &&
                   ModifierMatches(mods, HotkeyModifiers.Alt, KeyCode.LeftAlt, KeyCode.RightAlt);
        }

        // 求められている修飾キーは押されていること、求められていないものは押されていないこと。
        private static bool ModifierMatches(HotkeyModifiers mods, HotkeyModifiers flag, KeyCode left, KeyCode right)
        {
            bool held = Input.GetKey(left) || Input.GetKey(right);
            if ((mods & flag) == 0) return !held;
            return held || Input.GetKeyDown(left) || Input.GetKeyDown(right);
        }

        // スロット切替ホットキーが発火したか。押されていても、文字入力中で
        // 主キーがキーボードのキーなら発火させない。
        private bool SlotHotkeyFired(KeyboardShortcut shortcut, HotkeyModifiers mods, string name)
        {
            if (!IsSlotHotkeyPressed(shortcut, mods)) return false;

            if (textInputFocused && IsKeyboardKey(shortcut.MainKey))
            {
                LogHotkeySuppressed(name);
                return false;
            }

            if (ShowDebugInfo?.Value == true && mods != HotkeyModifiers.None)
                Logger?.LogInfo($"[HOTKEY] {name}: {shortcut.MainKey} + {mods}");
            return true;
        }

        // 押しっぱなしで毎フレーム出ないよう、ホットキーのクールダウンと同じ間隔に間引く。
        private void LogHotkeySuppressed(string name)
        {
            if (ShowDebugInfo?.Value != true) return;
            if (Time.time - lastHotkeySuppressLogTime < KEY_PRESS_COOLDOWN) return;
            lastHotkeySuppressLogTime = Time.time;
            Logger?.LogInfo($"[HOTKEY] {name} ignored: a text input has keyboard focus");
        }

        private void PerformHotkeyDetection()
        {
            try
            {
                if (Time.time - lastKeyPressTime < KEY_PRESS_COOLDOWN)
                    return;

                if (SlotHotkeyFired(PositionChangeHotkey.Value, PositionChangeModifiers.Value, "Position Change"))
                {
                    currentPositionSlot = NextSlot(currentPositionSlot, MaxPositionSlots.Value);
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"Position slot changed to: {currentPositionSlot}");
                    StartCoroutine(ExecutePosDataApplyBlock());
                    lastKeyPressTime = Time.time;
                    return;
                }

                if (SlotHotkeyFired(PositionChangePrevHotkey.Value, PositionChangePrevModifiers.Value, "Position Change (Prev)"))
                {
                    currentPositionSlot = PrevSlot(currentPositionSlot, MaxPositionSlots.Value);
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"Position slot changed to: {currentPositionSlot} (prev)");
                    StartCoroutine(ExecutePosDataApplyBlock());
                    lastKeyPressTime = Time.time;
                    return;
                }

                if (SlotHotkeyFired(CameraChangeHotkey.Value, CameraChangeModifiers.Value, "Camera Change"))
                {
                    currentCameraSlot = NextSlot(currentCameraSlot, MaxCameraSlots.Value);
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"Camera slot changed to: {currentCameraSlot} (in Position {currentPositionSlot})");
                    StartCoroutine(ExecuteCamDataApplyBlock());
                    lastKeyPressTime = Time.time;
                    return;
                }

                if (SlotHotkeyFired(CameraChangePrevHotkey.Value, CameraChangePrevModifiers.Value, "Camera Change (Prev)"))
                {
                    currentCameraSlot = PrevSlot(currentCameraSlot, MaxCameraSlots.Value);
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"Camera slot changed to: {currentCameraSlot} (in Position {currentPositionSlot}, prev)");
                    StartCoroutine(ExecuteCamDataApplyBlock());
                    lastKeyPressTime = Time.time;
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Hotkey detection error: {ex.Message}");
            }
        }

        private IEnumerator InitialDelayAndDetection()
        {
            yield return new WaitForSeconds(5.0f);

            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo("Initial delay completed - starting camera detection");

            StartCoroutine(CameraControl_Ver2DetectionBlock());
        }

        private static string GetCategoryName(HScene.AnimationListInfo info)
        {
            try
            {
                var tables = HSceneManager.HResourceTables;
                if (tables?.lstAnimInfo != null)
                {
                    for (int category = 0; category < tables.lstAnimInfo.Length; category++)
                    {
                        if (tables.lstAnimInfo[category]?.Any(anim => anim.id == info.id) == true)
                        {
                            return GetCategoryDisplayName(category);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"Category detection failed: {ex.Message}");
            }
            return "unknown";
        }

        private static string GetCategoryDisplayName(int category)
        {
            switch (category)
            {
                case 0: return "aibu";
                case 1: return "houshi";
                case 2: return "sonyu";
                case 3: return "tokushu";
                case 4: return "les";
                case 5: return "3P_F2M1";
                case 6: return "3P";
                default: return "unknown";
            }
        }

        private static string SanitizePositionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "noname";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = name;
            foreach (char c in invalidChars)
            {
                sanitized = sanitized.Replace(c, '_');
            }

            sanitized = sanitized.Replace(" ", "_").Replace("　", "_");
            return sanitized;
        }

        private IEnumerator CameraControl_Ver2DetectionBlock()
        {
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo("[CAMERA_DETECTION] Starting CameraControl_Ver2 detection block");

            cameraDetectionInProgress = true;
            cameraDetectionCount = 0;

            while (cameraDetectionCount < MAX_DETECTION_ATTEMPTS && cameraDetectionInProgress)
            {
                cameraDetectionCount++;

                if (ShowDebugInfo?.Value == true && (cameraDetectionCount % 10 == 1 || cameraDetectionCount <= 5))
                    Logger?.LogInfo($"[DETECTION] Attempt {cameraDetectionCount}/{MAX_DETECTION_ATTEMPTS}");

                if (DetectCameraControlInstance())
                {
                    Logger?.LogInfo($"[SUCCESS] CameraControl_Ver2 detected after {cameraDetectionCount} attempts");

                    if (InitializeCamDatFields())
                    {
                        isCameraControlInitialized = true;
                        cameraDetectionInProgress = false;
                        Logger?.LogInfo("[CAMERA_READY] Camera control fully initialized");
                        yield break;
                    }
                }

                if (cameraDetectionCount >= MAX_DETECTION_ATTEMPTS)
                {
                    Logger?.LogWarning("[TIMEOUT] Camera detection timeout - disabling camera functions");
                    cameraFunctionEnabled = false;
                    cameraDetectionInProgress = false;
                    Logger?.LogInfo("[FALLBACK] Position functions remain available");
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }
        }

        private bool DetectCameraControlInstance()
        {
            try
            {
                if (cameraControlVer2Type == null) return false;

                var instances = FindObjectsOfType(cameraControlVer2Type);
                if (instances != null && instances.Length > 0)
                {
                    cameraControlVer2Instance = instances[0];
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"Instance found via type search: {cameraControlVer2Type.Name}");
                    return true;
                }

                var allObjects = FindObjectsOfType<MonoBehaviour>();
                foreach (var obj in allObjects)
                {
                    if (obj.GetType() == cameraControlVer2Type ||
                        obj.GetType().Name.Contains("CameraControl_Ver2"))
                    {
                        cameraControlVer2Instance = obj;
                        if (ShowDebugInfo?.Value == true)
                            Logger?.LogInfo($"Instance found via MonoBehaviour search: {obj.GetType().Name}");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"Camera detection error: {ex.Message}");
                return false;
            }
        }

        private bool InitializeCamDatFields()
        {
            try
            {
                if (cameraControlVer2Instance == null || camDatField == null) return false;

                var camDatInstance = camDatField.GetValue(cameraControlVer2Instance);
                if (camDatInstance == null)
                {
                    Logger?.LogWarning("[CAMDAT_INIT] CamDat instance is null");
                    return false;
                }

                camDatType = camDatInstance.GetType();
                posField = camDatType.GetField("Pos");
                rotField = camDatType.GetField("Rot");
                dirField = camDatType.GetField("Dir");
                fovField = camDatType.GetField("Fov");

                // === FOV制御用リフレクション（v0.8.1.0） ===
                // thisCamera は BaseCameraControl_Ver2 の public プロパティ（setter は protected）。
                // limitFov / limitFovMin はシーン側から変更されうる public フィールドのため、
                // 定数を直書きせずインスタンスの実値を読む。
                var ctrlType = cameraControlVer2Instance.GetType();
                thisCameraProperty = ctrlType.GetProperty("thisCamera", BindingFlags.Public | BindingFlags.Instance);
                limitFovField = ctrlType.GetField("limitFov", BindingFlags.Public | BindingFlags.Instance);
                limitFovMinField = ctrlType.GetField("limitFovMin", BindingFlags.Public | BindingFlags.Instance);
                transBaseField = ctrlType.GetField("transBase", BindingFlags.Public | BindingFlags.Instance);

                Logger?.LogInfo($"[FOV_INIT] thisCamera:{thisCameraProperty != null} limitFov:{limitFovField != null} limitFovMin:{limitFovMinField != null}");

                // === 診断ログ（v0.8.1.1） ===
                // limitFov / limitFovMin の実値を必ず記録する。これらは public フィールドで
                // シーン側の値が優先されるため、dnSpy の初期化子（40f / 10f）を実効値と
                // みなしてはならない。v0.8.1.0 の設計時にこれを取り違えた。
                // Camera.main と thisCamera の同一性も記録する。v0.8.0.2 は Camera.main を
                // 使っていたため、両者が別物かどうかが過去の不具合の解釈に直結する。
                // いずれも診断目的のみで、制御には一切使用しない。
                try
                {
                    if (limitFovField != null && limitFovMinField != null)
                    {
                        float lmin = (float)limitFovMinField.GetValue(cameraControlVer2Instance);
                        float lmax = (float)limitFovField.GetValue(cameraControlVer2Instance);
                        Logger?.LogInfo($"[FOV_INIT] limits = {lmin} - {lmax}");
                    }

                    Camera diagThis = GetTargetCamera();
                    Camera diagMain = Camera.main;
                    string thisName = (diagThis != null) ? $"{diagThis.name}#{diagThis.GetInstanceID()}" : "null";
                    string mainName = (diagMain != null) ? $"{diagMain.name}#{diagMain.GetInstanceID()}" : "null";

                    Logger?.LogInfo($"[FOV_INIT] Camera.main == thisCamera : {object.ReferenceEquals(diagMain, diagThis)} (main:{mainName} / this:{thisName})");
                }
                catch (Exception diagEx)
                {
                    Logger?.LogWarning($"[FOV_INIT] Diagnostic logging failed: {diagEx.Message}");
                }

                LogCamDatDiagnostics();

                bool success = posField != null && rotField != null && dirField != null && fovField != null;

                if (success)
                {
                    Logger?.LogInfo($"[CAMDAT_INIT] CamDat fields initialized successfully");
                }
                else
                {
                    Logger?.LogWarning("[CAMDAT_INIT] Some CamDat fields missing");
                }

                return success;
            }
            catch (Exception ex)
            {
                Logger?.LogError($"CamDat initialization failed: {ex.Message}");
                return false;
            }
        }

        private void ExecuteMapDataLoadingBlock()
        {
            try
            {
                Logger?.LogInfo($"[MAP_DATA_LOADING] Starting for MapKey: {currentMapKey}");

                LoadMapSpecificData(currentMapKey);
                lastMapKey = currentMapKey;

                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"[MAP_DATA_LOADING] Completed");
                StartCoroutine(ExecutePosDataApplyBlock());
            }
            catch (Exception ex)
            {
                Logger?.LogError($"MAP data loading error: {ex.Message}");
                StartCoroutine(ExecuteCameraAutoFocusBlock());
            }
        }

        private IEnumerator ExecutePosDataApplyBlock()
        {
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[POS_DATA_APPLY] Starting - Position slot {currentPositionSlot}");

            bool needCamDataApply = false;
            bool needAutoFocus = false;
            bool needDelay = false;
            float delayTime = 0f;

            try
            {
                UpdateCharacterReferences();

                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"Searching: Action={currentActionId}, PosSlot={currentPositionSlot}");

                ClampCurrentSlots();
                var compositionSet = GetCompositionSetForRead(currentActionId);

                if (compositionSet != null &&
                    currentPositionSlot < compositionSet.PositionSlots.Length &&
                    compositionSet.PositionSlots[currentPositionSlot].HasPositionData)
                {
                    needDelay = true;
                    delayTime = AnimationChangeDelay.Value;
                    needCamDataApply = true;
                }
                else
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"No position data found for Action={currentActionId}, PosSlot={currentPositionSlot}");
                    needAutoFocus = true;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Position apply error: {ex.Message}");
                needAutoFocus = true;
                needDelay = false;
            }

            if (needDelay)
            {
                yield return new WaitForSeconds(delayTime);

                try
                {
                    var posSlotData = actionData[currentActionId].PositionSlots[currentPositionSlot];
                    ApplyCharacterPositions(posSlotData);
                    Logger?.LogInfo($"Position data applied from slot {currentPositionSlot}");
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Position apply error during restore: {ex.Message}");
                    needCamDataApply = false;
                    needAutoFocus = true;
                }
            }

            if (needCamDataApply)
            {
                yield return StartCoroutine(ExecuteCamDataApplyBlock());
            }
            else if (needAutoFocus)
            {
                yield return StartCoroutine(ExecuteCameraAutoFocusBlock());
            }
        }

        private IEnumerator ExecuteCamDataApplyBlock()
        {
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[CAMERA_APPLY] Starting - Position {currentPositionSlot}, Camera {currentCameraSlot}");

            bool cameraApplied = false;
            bool needAutoFocus = false;
            bool needDelay = false;
            float delayTime = 0f;
            CameraState cameraStateToApply = null;

            try
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"Data search: Action={currentActionId}, PosSlot={currentPositionSlot}, CamSlot={currentCameraSlot}");

                ClampCurrentSlots();

                // 相対変換に femaleChar1 を使うため参照を最新にする（v0.8.3.0）。
                // Camera スロット単独切替ではこの経路しか通らない
                UpdateCharacterReferences();

                var camCompositionSet = GetCompositionSetForRead(currentActionId);

                if (cameraFunctionEnabled && isCameraControlInitialized &&
                    camCompositionSet != null &&
                    currentPositionSlot < camCompositionSet.PositionSlots.Length &&
                    currentCameraSlot < camCompositionSet.PositionSlots[currentPositionSlot].HasCameraData.Length &&
                    camCompositionSet.PositionSlots[currentPositionSlot].HasCameraData[currentCameraSlot])
                {
                    cameraStateToApply = camCompositionSet.PositionSlots[currentPositionSlot].CameraSlots[currentCameraSlot];
                    needDelay = true;
                    delayTime = CameraFocusDelay.Value;
                }
                else
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"Camera not ready or no data in Position {currentPositionSlot}, Camera {currentCameraSlot}");
                    needAutoFocus = true;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Camera apply error: {ex.Message}");
                needAutoFocus = true;
                needDelay = false;
            }

            if (needDelay && cameraStateToApply != null)
            {
                yield return new WaitForSeconds(delayTime);

                bool operationSuccess = TryApplyCamDatDirect(cameraStateToApply);

                if (operationSuccess)
                {
                    Logger?.LogInfo($"Camera data applied successfully from Position {currentPositionSlot}, Camera {currentCameraSlot}");
                    cameraApplied = true;
                }
                else
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"Camera data apply failed for Position {currentPositionSlot}, Camera {currentCameraSlot}");
                    needAutoFocus = true;
                }
            }

            if (cameraApplied)
            {
                yield break;
            }
            else if (needAutoFocus)
            {
                isRestoringCamera = false;
                yield return StartCoroutine(ExecuteCameraAutoFocusBlock());
            }
        }

        private IEnumerator ExecuteBasisPosDataApplyBlock()
        {
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[BASISPOS_DATA_APPLY] Starting - BasisPos slot {currentBasisPositionSlot}");

            bool needDelay = false;
            bool needAutoFocus = false;
            float delayTime = 0f;

            try
            {
                UpdateCharacterReferences();

                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"Searching: MapKey={currentMapKey}, BasisPosSlot={currentBasisPositionSlot}");

                if (!string.IsNullOrEmpty(currentMapKey) &&
                    basisPositionData.ContainsKey(currentMapKey) &&
                    EnsureBasisCapacityAndCheck(basisPositionData[currentMapKey], currentBasisPositionSlot))
                {
                    needDelay = true;
                    delayTime = AnimationChangeDelay.Value;
                }
                else
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"No BasisPos data found for MapKey={currentMapKey}, BasisPosSlot={currentBasisPositionSlot}");
                    needAutoFocus = true;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"BasisPos apply error: {ex.Message}");
                needAutoFocus = true;
                needDelay = false;
            }

            if (needDelay)
            {
                yield return new WaitForSeconds(delayTime);

                try
                {
                    ApplyBasisPositions();
                    Logger?.LogInfo($"BasisPos data applied from slot {currentBasisPositionSlot}");
                    needAutoFocus = true;
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"BasisPos apply error during restore: {ex.Message}");
                    needAutoFocus = true;
                }
            }

            if (needAutoFocus)
            {
                yield return StartCoroutine(ExecuteCameraAutoFocusBlock());
            }
        }

        private IEnumerator ExecuteCameraAutoFocusBlock()
        {
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[CAMERA_AUTO_FOCUS] Starting auto focus block");

            if (!AutoCameraFocusEnabled.Value)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"Auto focus disabled - returning to event loop");
                yield break;
            }

            yield return new WaitForSeconds(CameraFocusDelay.Value);

            byte keyCode = VK_W;

            if (CameraKey.Value == CameraFocusKey.Q_Face)
                keyCode = VK_Q;
            else if (CameraKey.Value == CameraFocusKey.E_Crotch)
                keyCode = VK_E;
            else
                keyCode = VK_W;

            bool keyPressSuccess = false;
            string errorMessage = "";

            // 文字入力欄に入力先があるときは、キー押しだけを飛ばす（v0.15.0.0）。
            // keybd_event は OS 経由でキーを送るので、入力欄に "w/" が打ち込まれてしまう。
            // マウスボタンでの体位切替は入力中でも止めないため、ここで防ぐ。
            // 判定は待機の後、送る直前の状態で行う。
            // 下の FOV 復元は飛ばさない（CamDat.Fov と fieldOfView の二重書き込みは必須）。
            // keyPressSuccess が false のままなので、キーを離す処理と '/' も一緒に飛ぶ。
            bool skipKeys = IsTextInputFocused();
            if (skipKeys && ShowDebugInfo?.Value == true)
                Logger?.LogInfo("[CAMERA_AUTO_FOCUS] Focus keys skipped: a text input has keyboard focus");

            if (!skipKeys)
            {
                try
                {
                    keybd_event(keyCode, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                    keyPressSuccess = true;

                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"Auto focus key pressed: {CameraKey.Value}");
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    keyPressSuccess = false;
                }
            }

            if (keyPressSuccess)
            {
                yield return new WaitForSeconds(0.05f);

                bool keyUpSuccess = false;
                try
                {
                    keybd_event(keyCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keyUpSuccess = true;
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Auto focus key up error: {ex.Message}");
                }

                if (keyUpSuccess)
                {
                    yield return new WaitForSeconds(0.1f);

                    try
                    {
                        keybd_event(VK_OEM_2, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"Auto focus '/' key down error: {ex.Message}");
                    }

                    yield return new WaitForSeconds(0.1f);

                    try
                    {
                        keybd_event(VK_OEM_2, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        if (ShowDebugInfo?.Value == true)
                            Logger?.LogInfo("Auto focus '/' key sent");
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"Auto focus '/' key up error: {ex.Message}");
                    }
                }
            }
            else if (!skipKeys)   // 飛ばしただけならエラーではない
            {
                Logger?.LogWarning($"Auto focus key error: {errorMessage}");
            }

            // === FOV復元（v0.8.1.0改訂） ===
            // v0.8.0.2 は Camera.main.fieldOfView だけを書いており、内部値 CamDat.Fov が
            // 同期しないままだった。そのため次にユーザーが FOV キーを押した瞬間、
            // 古い CamDat.Fov を起点に再計算されて画角が飛んでいた。
            // ここでは CamDat.Fov と thisCamera.fieldOfView の両方へ書き込む。
            if (isCameraControlInitialized)
            {
                float fovToRestore = ClampFovToGameLimits(lastUserFov);

                if (ApplyFovToGame(fovToRestore))
                {
                    lastUserFov = fovToRestore;
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"[FOV_RESTORE] User FOV restored: {fovToRestore}");
                }
                else if (ShowDebugInfo?.Value == true)
                {
                    Logger?.LogWarning($"[FOV_RESTORE] Failed to restore FOV: {fovToRestore}");
                }
            }

            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[CAMERA_AUTO_FOCUS] Auto focus block completed");
        }

        private void ExecuteSaveBlock()
        {
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[SAVE] Starting save block");
            SaveData();

            if (!string.IsNullOrEmpty(currentMapKey))
            {
                LoadMapSpecificData(currentMapKey);
            }
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[SAVE] Save block completed");
        }

        private void ExecuteEndProcessBlock()
        {
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[END_PROCESS] Starting end process block");

            isInHScene = false;
            hSceneInstance = null;
            currentMapKey = "";
            lastMapKey = "";
            vanillaMapKey = "";
            currentKeyIsCustom = false;
            currentActionId = -1;
            lastActionId = -1;
            cameraDetectionInProgress = false;
            cameraDetectionCount = 0;
            mapKeyDetected = false;
            lastUserFov = 23f; // FOVリセット
            fovSyncFrames = 0;

            // 診断状態のリセット（キャラが入れ替わるため再解決が必要）
            boneTargetsResolved = false;
            boneTargets = null;
            animTargetsResolved = false;
            animTargets = null;
            transitionMeasuring = false;
            boneTransStartPos = null;
            ctrlFlagMeasurePending = 0;

            ClearCamClipboard();

            followBaselineValid = false;
            followSuppressed = false;

            swayResolved = false;
            swayBone = null;
            swayAnimator = null;
            swayOffset = Vector3.zero;
            swayGain = 0f;
            ctrlFlagDumped = false;
            ctrlFlagSnapshot = null;

            actionData.Clear();
            basisPositionData.Clear();

            Logger?.LogInfo("[END_PROCESS] HScene ended - cleanup complete");
        }

        private void SaveCurrentPosition()
        {
            try
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"SaveCurrentPosition - PosSlot: {currentPositionSlot}, Action: {currentActionId}");

                UpdateCharacterReferences();

                ClampCurrentSlots();
                GetOrCreateCompositionSet(currentActionId);

                var posSlotData = actionData[currentActionId].PositionSlots[currentPositionSlot];

                if (femaleChar1 != null)
                    posSlotData.Female1 = new Vector3Data(femaleChar1.transform.position, femaleChar1.transform.eulerAngles.y);
                if (femaleChar2 != null)
                    posSlotData.Female2 = new Vector3Data(femaleChar2.transform.position, femaleChar2.transform.eulerAngles.y);
                if (maleChar1 != null)
                    posSlotData.Male1 = new Vector3Data(maleChar1.transform.position, maleChar1.transform.eulerAngles.y);
                if (maleChar2 != null)
                    posSlotData.Male2 = new Vector3Data(maleChar2.transform.position, maleChar2.transform.eulerAngles.y);

                posSlotData.HasPositionData = true;
                actionData[currentActionId].LastModified = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                ExecuteSaveBlock();

                Logger?.LogInfo($"Position saved to slot {currentPositionSlot}");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Save position error: {ex.Message}");
            }
        }

        private void SaveCurrentCamera()
        {
            try
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"SaveCurrentCamera - PosSlot: {currentPositionSlot}, CamSlot: {currentCameraSlot}, Action: {currentActionId}");

                ClampCurrentSlots();
                GetOrCreateCompositionSet(currentActionId);

                // 相対変換に femaleChar1 を使うため、参照を最新にしておく（v0.8.3.0）
                UpdateCharacterReferences();

                var cameraState = actionData[currentActionId].PositionSlots[currentPositionSlot].CameraSlots[currentCameraSlot];

                if (isCameraControlInitialized && cameraControlVer2Instance != null && cameraFunctionEnabled)
                {
                    try
                    {
                        var camDatInstance = camDatField.GetValue(cameraControlVer2Instance);
                        if (ShowDebugInfo?.Value == true)
                            Logger?.LogInfo($"[SAVE] CamDat instance retrieved: {camDatInstance != null}");

                        if (camDatInstance != null)
                        {
                            // Pos と Rot は femaleChar1 基準へ変換して保存する（v0.8.3.0）。
                            // 変換は Pos / Rot を組で扱うため、個別ではなくまとめて処理する。
                            if (posField != null && rotField != null)
                            {
                                Vector3 pos = (Vector3)posField.GetValue(camDatInstance);
                                Vector3 rot = (Vector3)rotField.GetValue(camDatInstance);

                                Vector3 savedPos, savedRot;
                                bool converted = TryToCharRelative(pos, rot, out savedPos, out savedRot);

                                cameraState.CamDatPos = new Vector3Data(savedPos);
                                cameraState.CamDatRot = new Vector3Data(savedRot);

                                if (!converted)
                                    Logger?.LogWarning("[RELATIVE] Saved raw CamDat (transBase or femaleChar1 unavailable)");

                                if (ShowDebugInfo?.Value == true)
                                {
                                    Logger?.LogInfo($"Saving CamDat.Pos: {pos} -> charRelative {savedPos} (converted={converted})");
                                    Logger?.LogInfo($"Saving CamDat.Rot: {rot} -> charRelative {savedRot}");
                                }
                            }

                            if (dirField != null)
                            {
                                Vector3 dir = (Vector3)dirField.GetValue(camDatInstance);
                                cameraState.CamDatDir = new Vector3Data(dir);
                                if (ShowDebugInfo?.Value == true)
                                    Logger?.LogInfo($"Saving CamDat.Dir: {dir}");
                            }

                            if (fovField != null)
                            {
                                // === FOV保存改善（v0.8.0.1）：lastUserFovを使用 ===
                                cameraState.CamDatFov = lastUserFov;
                                if (ShowDebugInfo?.Value == true)
                                    Logger?.LogInfo($"Saving User FOV: {lastUserFov}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"CamDat save failed: {ex.Message}");
                    }
                }

                cameraState.HasData = true;
                actionData[currentActionId].PositionSlots[currentPositionSlot].HasCameraData[currentCameraSlot] = true;

                ExecuteSaveBlock();

                Logger?.LogInfo($"Camera saved to Position {currentPositionSlot}, Camera {currentCameraSlot}");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Save camera error: {ex.Message}");
            }
        }

        private void SaveCurrentBasisPosition()
        {
            try
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"SaveCurrentBasisPosition - BasisPosSlot: {currentBasisPositionSlot}, MapKey: {currentMapKey}");

                UpdateCharacterReferences();

                if (string.IsNullOrEmpty(currentMapKey))
                {
                    Logger?.LogWarning("Cannot save BasisPos: currentMapKey is empty");
                    return;
                }

                ClampCurrentSlots();

                if (!basisPositionData.ContainsKey(currentMapKey))
                    basisPositionData[currentMapKey] = new BasisPositionSet(
                        Mathf.Clamp(MaxBasisPositionSlots.Value, 1, MAX_SLOT_CAPACITY));

                var basisSet = basisPositionData[currentMapKey];
                EnsureBasisCapacity(basisSet);
                int baseIndex = currentBasisPositionSlot * 4;

                if (femaleChar1 != null)
                    basisSet.BasisPositions[baseIndex] = new Vector3Data(femaleChar1.transform.position, femaleChar1.transform.eulerAngles.y);
                if (femaleChar2 != null)
                    basisSet.BasisPositions[baseIndex + 1] = new Vector3Data(femaleChar2.transform.position, femaleChar2.transform.eulerAngles.y);
                if (maleChar1 != null)
                    basisSet.BasisPositions[baseIndex + 2] = new Vector3Data(maleChar1.transform.position, maleChar1.transform.eulerAngles.y);
                if (maleChar2 != null)
                    basisSet.BasisPositions[baseIndex + 3] = new Vector3Data(maleChar2.transform.position, maleChar2.transform.eulerAngles.y);

                basisSet.HasBasisPositionData[currentBasisPositionSlot] = true;

                ExecuteSaveBlock();

                Logger?.LogInfo($"BasisPos saved to slot {currentBasisPositionSlot}");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Save BasisPos error: {ex.Message}");
            }
        }

        // Config でスロット数を減らした直後、現在のスロット番号が範囲外のまま残ることが
        // あるため丸める。ホットキーと UI ボタンは % MaxSlots で巡回するので押せば戻るが、
        // 押される前に体位変更等で参照されると添字超過になる。
        private void ClampCurrentSlots()
        {
            if (currentPositionSlot >= MaxPositionSlots.Value)
                currentPositionSlot = MaxPositionSlots.Value - 1;
            if (currentCameraSlot >= MaxCameraSlots.Value)
                currentCameraSlot = MaxCameraSlots.Value - 1;
            if (currentBasisPositionSlot >= MaxBasisPositionSlots.Value)
                currentBasisPositionSlot = MaxBasisPositionSlots.Value - 1;

            if (currentPositionSlot < 0) currentPositionSlot = 0;
            if (currentCameraSlot < 0) currentCameraSlot = 0;
            if (currentBasisPositionSlot < 0) currentBasisPositionSlot = 0;
        }

        // 配列を現在の Config 値まで拡張する（既存データは保持）。
        //
        // 配列サイズは「読み込み／生成した時点の Config 値」で固定されるため、
        // あとから Max Slots を増やしても配列は追随せず、新しいスロットへ
        // アクセスすると添字超過になっていた（v0.8.2.0 以前からの挙動）。
        // 縮小はしない。ScanSlotCapacities() と同じく、確保済みのデータは減らさない。
        private void EnsureCompositionCapacity(CompositionSet set)
        {
            if (set == null) return;

            int wantPos = Mathf.Clamp(MaxPositionSlots.Value, 1, MAX_SLOT_CAPACITY);
            int wantCam = Mathf.Clamp(MaxCameraSlots.Value, 1, MAX_SLOT_CAPACITY);

            if (set.PositionSlots.Length < wantPos)
            {
                var grown = new PositionSlotData[wantPos];
                Array.Copy(set.PositionSlots, grown, set.PositionSlots.Length);
                for (int i = set.PositionSlots.Length; i < wantPos; i++)
                    grown[i] = new PositionSlotData(wantCam);
                set.PositionSlots = grown;
            }

            foreach (var posData in set.PositionSlots)
            {
                if (posData == null) continue;

                if (posData.CameraSlots.Length < wantCam)
                {
                    var camGrown = new CameraState[wantCam];
                    Array.Copy(posData.CameraSlots, camGrown, posData.CameraSlots.Length);
                    for (int i = posData.CameraSlots.Length; i < wantCam; i++)
                        camGrown[i] = new CameraState();
                    posData.CameraSlots = camGrown;
                }

                if (posData.HasCameraData.Length < wantCam)
                {
                    var hasGrown = new bool[wantCam];
                    Array.Copy(posData.HasCameraData, hasGrown, posData.HasCameraData.Length);
                    posData.HasCameraData = hasGrown;
                }
            }
        }

        private void EnsureBasisCapacity(BasisPositionSet set)
        {
            if (set == null) return;

            int want = Mathf.Clamp(MaxBasisPositionSlots.Value, 1, MAX_SLOT_CAPACITY);

            if (set.HasBasisPositionData.Length < want)
            {
                var grown = new bool[want];
                Array.Copy(set.HasBasisPositionData, grown, set.HasBasisPositionData.Length);
                set.HasBasisPositionData = grown;
            }

            if (set.BasisPositions.Length < want * 4)
            {
                var grown = new Vector3Data[want * 4];
                Array.Copy(set.BasisPositions, grown, set.BasisPositions.Length);
                for (int i = set.BasisPositions.Length; i < want * 4; i++)
                    grown[i] = new Vector3Data();
                set.BasisPositions = grown;
            }
        }

        // BasisPos の容量を現在の Config 値まで拡張したうえで、
        // 指定スロットにデータがあるかを返す。
        private bool EnsureBasisCapacityAndCheck(BasisPositionSet set, int slot)
        {
            if (set == null) return false;

            EnsureBasisCapacity(set);

            if (slot < 0 || slot >= set.HasBasisPositionData.Length) return false;
            return set.HasBasisPositionData[slot];
        }

        // 読み取り用。存在しなければ null を返す（新規作成はしない）。
        private CompositionSet GetCompositionSetForRead(int actionId)
        {
            if (!actionData.ContainsKey(actionId)) return null;

            var set = actionData[actionId];
            EnsureCompositionCapacity(set);
            return set;
        }

        // 保存用。無ければ作り、あれば現在の Config 値まで拡張する。
        private CompositionSet GetOrCreateCompositionSet(int actionId)
        {
            if (!actionData.ContainsKey(actionId))
            {
                actionData[actionId] = new CompositionSet(
                    Mathf.Clamp(MaxPositionSlots.Value, 1, MAX_SLOT_CAPACITY),
                    Mathf.Clamp(MaxCameraSlots.Value, 1, MAX_SLOT_CAPACITY));
            }
            else
            {
                EnsureCompositionCapacity(actionData[actionId]);
            }

            return actionData[actionId];
        }

        private void UpdateCharacterReferences()
        {
            var allChars = FindObjectsOfType<ChaControl>();
            var females = new List<ChaControl>();
            var males = new List<ChaControl>();

            foreach (var chara in allChars)
            {
                if (chara.sex == 1) females.Add(chara);
                else if (chara.sex == 0) males.Add(chara);
            }

            females.Sort((a, b) => string.Compare(a.name, b.name));
            males.Sort((a, b) => string.Compare(a.name, b.name));

            femaleChar1 = females.Count > 0 ? females[0] : null;
            femaleChar2 = females.Count > 1 ? females[1] : null;
            maleChar1 = males.Count > 0 ? males[0] : null;
            maleChar2 = males.Count > 1 ? males[1] : null;
        }

        private void ApplyCharacterPositions(PositionSlotData posSlotData)
        {
            // キャラを瞬間移動させるため、追従を止めて基準を張り直す
            followSuppressed = true;
            try
            {
                if (femaleChar1 != null && posSlotData.Female1 != null)
                {
                    femaleChar1.transform.position = posSlotData.Female1.ToVector3();
                    femaleChar1.transform.rotation = Quaternion.Euler(0, posSlotData.Female1.rotY, 0);
                }

                if (femaleChar2 != null && posSlotData.Female2 != null)
                {
                    femaleChar2.transform.position = posSlotData.Female2.ToVector3();
                    femaleChar2.transform.rotation = Quaternion.Euler(0, posSlotData.Female2.rotY, 0);
                }

                if (maleChar1 != null && posSlotData.Male1 != null)
                {
                    maleChar1.transform.position = posSlotData.Male1.ToVector3();
                    maleChar1.transform.rotation = Quaternion.Euler(0, posSlotData.Male1.rotY, 0);
                }

                if (maleChar2 != null && posSlotData.Male2 != null)
                {
                    maleChar2.transform.position = posSlotData.Male2.ToVector3();
                    maleChar2.transform.rotation = Quaternion.Euler(0, posSlotData.Male2.rotY, 0);
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Apply positions error: {ex.Message}");
            }
            finally
            {
                followSuppressed = false;
                followBaselineValid = false;   // 次フレームで張り直す
            }
        }

        private void ApplyBasisPositions()
        {
            followSuppressed = true;
            try
            {
                if (!basisPositionData.ContainsKey(currentMapKey))
                    return;

                var basisSet = basisPositionData[currentMapKey];
                EnsureBasisCapacity(basisSet);

                if (currentBasisPositionSlot < 0 ||
                    currentBasisPositionSlot >= basisSet.HasBasisPositionData.Length ||
                    !basisSet.HasBasisPositionData[currentBasisPositionSlot])
                    return;

                int baseIndex = currentBasisPositionSlot * 4;
                if (baseIndex + 3 >= basisSet.BasisPositions.Length) return;

                if (femaleChar1 != null && basisSet.BasisPositions[baseIndex] != null)
                {
                    femaleChar1.transform.position = basisSet.BasisPositions[baseIndex].ToVector3();
                    femaleChar1.transform.rotation = Quaternion.Euler(0, basisSet.BasisPositions[baseIndex].rotY, 0);
                }

                if (femaleChar2 != null && basisSet.BasisPositions[baseIndex + 1] != null)
                {
                    femaleChar2.transform.position = basisSet.BasisPositions[baseIndex + 1].ToVector3();
                    femaleChar2.transform.rotation = Quaternion.Euler(0, basisSet.BasisPositions[baseIndex + 1].rotY, 0);
                }

                if (maleChar1 != null && basisSet.BasisPositions[baseIndex + 2] != null)
                {
                    maleChar1.transform.position = basisSet.BasisPositions[baseIndex + 2].ToVector3();
                    maleChar1.transform.rotation = Quaternion.Euler(0, basisSet.BasisPositions[baseIndex + 2].rotY, 0);
                }

                if (maleChar2 != null && basisSet.BasisPositions[baseIndex + 3] != null)
                {
                    maleChar2.transform.position = basisSet.BasisPositions[baseIndex + 3].ToVector3();
                    maleChar2.transform.rotation = Quaternion.Euler(0, basisSet.BasisPositions[baseIndex + 3].rotY, 0);
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Apply BasisPos positions error: {ex.Message}");
            }
            finally
            {
                followSuppressed = false;
                followBaselineValid = false;
            }
        }

        // === 完全相対座標システム（v0.8.3.0） ===
        //
        // CamDat.Pos / Rot は transBase（Hシーンの基準点アンカー。実測では "HCamera"）
        // 基準の相対値である。transBase は体位変更時に「その体位の基準点」へ設定され、
        // femaleChar1 も同じ瞬間に同じ点へ配置される。
        //
        // ただし transBase はその後 femaleChar1 を追わない。ユーザーやプラグインの
        // Position 機能でキャラを動かすと femaleChar1 だけが基準点から離れ、
        // カメラは基準点にぶら下がったままになる。これが構図ドリフトの正体。
        //
        // そこで保存時に「femaleChar1 基準」へ変換し、復元時に「その時点の transBase
        // 基準」へ戻す。ゲームの状態（transBase）には一切触れない。
        //
        // transBase == femaleChar1 のとき、この変換は恒等写像になる。
        // 実測でスケールは (1,1,1) と確認済みなので TransformPoint の往復は保存される。
        // したがって同期状態で保存された既存データは値が変わらない。
        //
        // 詳細は documents/CamDat座標系仕様書 v2.1.md を参照。

        private static float NormalizeAngle(float angle)
        {
            while (angle > 180f) angle -= 360f;
            while (angle < -180f) angle += 360f;
            return angle;
        }

        // transBase を取得する。取得できなければ null。
        private Transform GetTransBase()
        {
            try
            {
                if (cameraControlVer2Instance == null || transBaseField == null) return null;
                return transBaseField.GetValue(cameraControlVer2Instance) as Transform;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"[RELATIVE] transBase acquisition failed: {ex.Message}");
                return null;
            }
        }

        // CamDat（transBase 基準）→ 保存値（femaleChar1 基準）
        // 変換できない場合は false を返す。呼び出し側は生値をそのまま保存すること。
        private bool TryToCharRelative(Vector3 camPos, Vector3 camRot,
                                       out Vector3 savedPos, out Vector3 savedRot)
        {
            savedPos = camPos;
            savedRot = camRot;

            try
            {
                Transform tb = GetTransBase();
                if (tb == null || femaleChar1 == null) return false;

                Transform f1 = femaleChar1.transform;

                Vector3 worldPivot = tb.TransformPoint(camPos);
                savedPos = f1.InverseTransformPoint(worldPivot);

                // transBase の回転は Y 軸のみ（実測で確認済み）。
                // Unity の Euler は ZXY 順なので、Y 回転の合成はヨーの加算になり
                // ピッチとロールは影響を受けない。
                float worldYaw = tb.eulerAngles.y + camRot.y;
                savedRot = new Vector3(camRot.x, NormalizeAngle(worldYaw - f1.eulerAngles.y), camRot.z);

                return true;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[RELATIVE] ToCharRelative failed, saving raw CamDat: {ex.Message}");
                savedPos = camPos;
                savedRot = camRot;
                return false;
            }
        }

        // 保存値（femaleChar1 基準）→ CamDat（その時点の transBase 基準）
        private bool TryToCamDatSpace(Vector3 savedPos, Vector3 savedRot,
                                      out Vector3 camPos, out Vector3 camRot)
        {
            camPos = savedPos;
            camRot = savedRot;

            try
            {
                Transform tb = GetTransBase();
                if (tb == null || femaleChar1 == null) return false;

                Transform f1 = femaleChar1.transform;

                Vector3 worldPivot = f1.TransformPoint(savedPos);
                camPos = tb.InverseTransformPoint(worldPivot);

                float worldYaw = savedRot.y + f1.eulerAngles.y;
                camRot = new Vector3(savedRot.x, NormalizeAngle(worldYaw - tb.eulerAngles.y), savedRot.z);

                return true;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[RELATIVE] ToCamDatSpace failed, applying raw value: {ex.Message}");
                camPos = savedPos;
                camRot = savedRot;
                return false;
            }
        }

        // === カメラ揺動（v0.9.0.0） ===
        //
        // 男性キャラのボーンの動きを、カメラ位置に微小オフセットとして加算する。
        // 「ハメ撮り」風の演出。構図は変えず、揺れだけを乗せる。
        //
        // 【CamDat には触らない】
        // オフセットは CameraUpdate() の Postfix でカメラ transform へ直接足す。
        // CamDat を書くと保存対象の構図データを汚し、ユーザーのカメラ操作とも
        // 競合する。Postfix なら次フレームに CameraUpdate() が CamDat から
        // 再計算するため、オフセットが累積しない。
        //
        // CLAUDE.md §10 は「Unity Camera への書き込みは画面に反映されない」と
        // するが、それは CameraUpdate() より前に書いた場合の話である。
        // 直後に書けば、そのまま描画される。
        //
        // 【ボーン位置はキャラローカルで取る】
        // ワールド座標だと体位変更や Position 復元による全身移動が
        // そのまま揺れとして出てしまう。ローカルなら全身移動は差分ゼロになり、
        // 関節の動きだけが残る。
        //
        // 【ポーズ切替時は抑制する】
        // 同一体位でもステータスによりポーズが切り替わり、ボーン座標が遷移する。
        // 実測では IsInTransition が一度も true にならず、切替はスナップだった。
        // よって Animator の状態ハッシュ変化を検知し、一定時間抑制する。
        //
        // 抑制中は「出力を止める」だけでなく「基準を追従させ続ける」必要がある。
        // 止めたまま復帰すると差分が変位量そのものになり、再開時に飛ぶ。
        // gain も瞬時に切らずフェードさせる。切ると復帰時にスナップする。

        private Transform swayBone;
        private Animator swayAnimator;
        private Vector3 swaySmoothed;
        private Vector3 swayOffset;
        private float swayGain;

        // 揺動量の実効値。ホットキー調整中は Config ではなくこちらが正（v0.12.0.0）。
        // swayAmpAdjusting はキーを離した瞬間を捉えるための状態で、
        // Config への書き戻しを1回に絞る役割も持つ。
        private float swayAmplitudeLive = 60f;
        private bool swayAmpAdjusting;
        private int swayPrevAnimHash;
        private float swaySuppressUntil;
        private bool swayResolved;

        private const float SWAY_GAIN_FADE_TIME = 0.15f;   // gain の 0<->1 フェード時間（秒）
        private const float SWAY_SLEW_RATE = 2.0f;         // オフセットの変化速度上限（単位/秒）

        // Amplitude の許容範囲。Config の AcceptableValueRange と
        // ホットキー調整時のクランプで同じ値を使う（片方だけ変えると破綻する）。
        private const float SWAY_AMP_MIN = 0f;
        private const float SWAY_AMP_MAX = 1000f;

        private static readonly string[] SWAY_BONE_NAMES =
        {
            "cf_J_Kosi01", "cf_J_Kosi02", "cf_J_Kosi03",
            "cf_J_Spine01", "cf_J_Spine03",
            "cf_J_Neck", "cf_J_Head"
        };

        private void ResolveSwayTargets()
        {
            try
            {
                if (maleChar1 == null) return;

                string want = SWAY_BONE_NAMES[Mathf.Clamp((int)SwayTargetBone.Value, 0, SWAY_BONE_NAMES.Length - 1)];

                swayBone = null;
                foreach (var tr in maleChar1.GetComponentsInChildren<Transform>(true))
                {
                    if (tr.name == want) { swayBone = tr; break; }
                }

                if (swayBone == null)
                {
                    Logger?.LogWarning($"[SWAY] bone not found: {want}");
                    return;
                }

                // 抑制トリガ用の Animator。実測では p_cf_anim（controller=adv_f_01）が
                // Hシーンのモーションを駆動していた。NowLoading は除外する。
                swayAnimator = null;
                foreach (var a in FindObjectsOfType<Animator>())
                {
                    if (a == null || a.runtimeAnimatorController == null) continue;
                    if (a.layerCount <= 0) continue;
                    if (a.name == "NowLoading") continue;
                    swayAnimator = a;
                    break;
                }

                swaySmoothed = GetBoneLocal(swayBone);
                swayOffset = Vector3.zero;
                swayGain = 0f;
                swayPrevAnimHash = 0;
                swaySuppressUntil = Time.time + SwaySuppressTail.Value;
                swayResolved = true;

                Logger?.LogInfo($"[SWAY] bone={swayBone.name} animator={(swayAnimator != null ? swayAnimator.name : "(none)")}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[SWAY] resolve failed: {ex.Message}");
            }
        }

        // CameraUpdate() の Postfix から呼ばれる。カメラ transform にオフセットを加算する。
        private void ApplySway(MonoBehaviour camCtrl)
        {
            try
            {
                if (!EnablePlugin.Value || !EnableCameraSway.Value) return;
                if (!isInHScene || camCtrl == null) return;

                // 自分が制御対象としているカメラ以外には触らない
                if (!ReferenceEquals(camCtrl, cameraControlVer2Instance)) return;
                if (maleChar1 == null || swayBone == null) return;

                float dt = Time.deltaTime;
                if (dt <= 0f) return;

                Vector3 local = GetBoneLocal(swayBone);

                // --- 抑制判定: Animator の状態が変わったら一定時間止める ---
                if (swayAnimator != null && swayAnimator.runtimeAnimatorController != null)
                {
                    int hash = swayAnimator.GetCurrentAnimatorStateInfo(0).fullPathHash;
                    if (hash != swayPrevAnimHash)
                    {
                        swayPrevAnimHash = hash;
                        swaySuppressUntil = Time.time + SwaySuppressTail.Value;
                    }
                }

                bool suppress = Time.time < swaySuppressUntil;

                if (suppress)
                {
                    // 基準を即座に追従させる。ここで止めると復帰時に飛ぶ
                    swaySmoothed = local;
                    swayGain = Mathf.MoveTowards(swayGain, 0f, dt / SWAY_GAIN_FADE_TIME);
                }
                else
                {
                    swaySmoothed = Vector3.Lerp(swaySmoothed, local, Mathf.Clamp01(SwaySmoothing.Value * dt));
                    swayGain = Mathf.MoveTowards(swayGain, 1f, dt / SWAY_GAIN_FADE_TIME);
                }

                Vector3 raw = (local - swaySmoothed) * (swayAmplitudeLive / 100f) * swayGain;

                float maxOff = SwayMaxOffset.Value;
                if (maxOff > 0f && raw.magnitude > maxOff)
                    raw = raw.normalized * maxOff;

                swayOffset = Vector3.MoveTowards(swayOffset, raw, SWAY_SLEW_RATE * dt);

                if (swayOffset.sqrMagnitude > 0f)
                {
                    // ローカル空間のオフセットをワールドへ戻して加算する
                    Vector3 world = maleChar1.transform.TransformVector(swayOffset);
                    camCtrl.transform.position += world;
                }
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"[SWAY] apply failed: {ex.Message}");
            }
        }

        // === Animator 状態の診断（v0.8.3.2） ===
        //
        // 同一体位内のポーズ切替は、フレーム間移動量では検出できないことが実測で
        // 確定した。ブレンド遷移は 1フレームあたり 0.03〜0.05 しか動かず、
        // 通常モーション（0.10〜0.20）より遅いため、どんな閾値でも分離できない。
        //
        // そこで Animator の状態を直接見る。
        //   IsInTransition(0)  遷移中は true。区間全体が分かる（最重要）
        //   clip 名            適用中のモーション。可読なので診断向き
        //   fullPathHash       状態の同一性判定。確保が起きないので本番向き
        //
        // 本番実装では GetCurrentAnimatorClipInfo() を毎フレーム呼ばない
        // （配列を確保するため）。ハッシュ比較で足りる。

        private Animator[] animTargets;
        private int[] animPrevHash;
        private bool[] animPrevInTransition;
        private int[] animTransitionFrames;
        private bool animTargetsResolved;

        // 遷移区間中にボーンがどれだけ動いたかを測る。
        // 抑制機構が吸収すべき変位量そのものであり、maxOffset の根拠になる。
        private Vector3[] boneTransStartPos;
        private bool transitionMeasuring;
        private int ctrlFlagMeasurePending;

        private void ResolveAnimators()
        {
            try
            {
                if (maleChar1 == null) return;

                // v0.8.3.3 は maleChar1 の子だけを見ており、コントローラ未割当の
                // p_cf_anim しか見つからなかった。アニメーションは中央管理されている
                // 可能性があるため（ctrlFlag に lstSyncAnimLayers がある）、
                // シーン全体を探索する。
                var found = FindObjectsOfType<Animator>();
                Logger?.LogInfo($"[ANIM] animators in scene: {found.Length}");

                var list = new List<Animator>();
                foreach (var a in found)
                {
                    if (a == null) continue;

                    bool usable = a.runtimeAnimatorController != null && a.layerCount > 0;
                    string root = (a.transform.root != null) ? a.transform.root.name : "(none)";

                    Logger?.LogInfo($"[ANIM]   {a.name} root={root} layers={a.layerCount} " +
                                    $"controller={(a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "(none)")} " +
                                    $"{(usable ? "<-- usable" : "")}");

                    if (usable) list.Add(a);
                }

                // ChaControl が Animator 型フィールドを持っているかも調べる。
                // Illusion の ChaControl は animBody 等を持つことがある。
                try
                {
                    Type ct = maleChar1.GetType();
                    foreach (var f in ct.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (!typeof(Animator).IsAssignableFrom(f.FieldType)) continue;

                        var av = f.GetValue(maleChar1) as Animator;
                        Logger?.LogInfo($"[ANIM] ChaControl field {f.Name} = " +
                            (av != null
                                ? $"{av.name} layers={av.layerCount} controller={(av.runtimeAnimatorController != null ? av.runtimeAnimatorController.name : "(none)")}"
                                : "null"));
                    }
                }
                catch (Exception fx)
                {
                    Logger?.LogWarning($"[ANIM] ChaControl field scan failed: {fx.Message}");
                }

                animTargets = list.ToArray();
                animPrevHash = new int[animTargets.Length];
                animPrevInTransition = new bool[animTargets.Length];
                animTransitionFrames = new int[animTargets.Length];
                animTargetsResolved = true;

                Logger?.LogInfo($"[ANIM] tracking {animTargets.Length} animator(s)");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[ANIM] resolve failed: {ex.Message}");
            }
        }

        // 遷移開始時にボーン位置を控える。
        private void BeginTransitionMeasure()
        {
            try
            {
                if (!boneTargetsResolved || boneTargets == null) return;

                if (boneTransStartPos == null || boneTransStartPos.Length != boneTargets.Length)
                    boneTransStartPos = new Vector3[boneTargets.Length];

                for (int i = 0; i < boneTargets.Length; i++)
                    boneTransStartPos[i] = GetBoneLocal(boneTargets[i]);

                transitionMeasuring = true;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[ANIM_MOVE] begin failed: {ex.Message}");
            }
        }

        // 遷移終了時に、区間中のボーン変位を出す。
        // これが抑制機構の吸収すべき量であり、maxOffset の根拠になる。
        private void EndTransitionMeasure(int frames)
        {
            try
            {
                if (!transitionMeasuring) return;
                transitionMeasuring = false;

                if (!boneTargetsResolved || boneTargets == null || boneTransStartPos == null) return;

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < boneTargets.Length; i++)
                {
                    var tr = boneTargets[i];
                    if (tr == null) continue;

                    float moved = (GetBoneLocal(tr) - boneTransStartPos[i]).magnitude;
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append($"{tr.name}={moved:F4}");
                }

                string src = (frames >= 0) ? $"frames={frames}" : "src=ctrlFlag";
                Logger?.LogInfo($"[ANIM_MOVE] action={currentActionId} {src} moved: {sb}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[ANIM_MOVE] end failed: {ex.Message}");
            }
        }

        // 毎フレーム呼ぶ。状態が変わったときだけログを出す。
        private void SampleAnimators()
        {
            try
            {
                if (!animTargetsResolved || animTargets == null) return;

                for (int i = 0; i < animTargets.Length; i++)
                {
                    var a = animTargets[i];
                    if (a == null || a.runtimeAnimatorController == null) continue;

                    bool inTrans = a.IsInTransition(0);

                    // 遷移区間の長さを数える。実測した net=2.39 / peak=0.049 の
                    // ブレンド区間と一致するかを確認するのが目的。
                    if (inTrans)
                    {
                        animTransitionFrames[i]++;

                        if (!animPrevInTransition[i])
                        {
                            Logger?.LogInfo($"[ANIM] action={currentActionId} {a.name} transition BEGIN");
                            BeginTransitionMeasure();
                        }
                    }
                    else if (animPrevInTransition[i])
                    {
                        Logger?.LogInfo($"[ANIM] action={currentActionId} {a.name} transition END frames={animTransitionFrames[i]} ({animTransitionFrames[i] * Time.deltaTime:F3}s)");
                        EndTransitionMeasure(animTransitionFrames[i]);
                        animTransitionFrames[i] = 0;
                    }
                    animPrevInTransition[i] = inTrans;

                    var st = a.GetCurrentAnimatorStateInfo(0);
                    if (st.fullPathHash != animPrevHash[i])
                    {
                        // 診断のみ。可読なクリップ名を出す（確保が起きるので本番では使わない）
                        string clipName = "(unknown)";
                        try
                        {
                            var infos = a.GetCurrentAnimatorClipInfo(0);
                            if (infos != null && infos.Length > 0 && infos[0].clip != null)
                                clipName = infos[0].clip.name;
                        }
                        catch { }

                        Logger?.LogInfo($"[ANIM] action={currentActionId} {a.name} state changed -> hash={st.fullPathHash} clip={clipName}");
                        animPrevHash[i] = st.fullPathHash;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[ANIM] sample failed: {ex.Message}");
            }
        }

        // === カメラ揺動機能のための診断（v0.8.3.1） ===
        //
        // 目的は3点。いずれもログ出力のみで、制御には一切介入しない。
        //
        //   1. maleChar1 のボーン名を特定する（役割 -> 実名の対応付け）
        //   2. 候補ボーンの振幅と、「振動」か「移動」かの判別材料を採る
        //   3. ステータス変化を知るイベント信号が ctrlFlag にあるかを調べる
        //
        // 3 が最重要。ポーズ切替がブレンド（数フレームかけた滑らかな遷移）で
        // 行われる場合、フレーム間移動量による跳躍検知では原理的に捕まらない。
        // イベント信号が取れれば、遷移が何秒かかろうと確実に基準を付け替えられる。
        //
        // 判別の指標:
        //   rmsDelta … フレーム間移動量の実効値。振幅の大きさ
        //   netMove  … 1秒間の正味変位。|pos_now - pos_1秒前|
        //
        //   振動（突き上げ）は行って戻るので netMove が小さく rmsDelta が大きい。
        //   移動（ポーズ切替）は一方向に進むので netMove が大きくなる。
        //   周波数帯が重なっていても、この比で両者を区別できる。

        // v0.8.3.1 はキーワード一致の先頭10本を採ったため、揺れ物や当たり判定用の
        // 補助ボーン（N_Waist / k_f_kosi01_xx 等）ばかりを追跡してしまい、
        // 肝心の頭部・胸部を測れていなかった。実名で指定する。
        private static readonly string[] BONE_TARGET_NAMES =
        {
            "cf_J_Hips",
            "cf_J_Kosi01", "cf_J_Kosi02", "cf_J_Kosi03",
            "cf_J_Spine01", "cf_J_Spine02", "cf_J_Spine03",
            "cf_J_Neck", "cf_J_Head"
        };

        // 実測（v0.8.3.1）: 通常モーションのフレーム間移動量は最大 0.10〜0.20。
        // 0.05 では常時発火してノイズになったため引き上げた。
        private const float BONE_JUMP_DELTA = 0.5f;

        // ボーン位置はキャラのローカル座標で測る（v0.8.3.4）。
        //
        // v0.8.3.3 まではワールド座標で測っていたため、体位変更などによる
        // 全身の並進運動が支配的になり、関節の動きが埋もれていた。
        // 実測では大変位時に全9ボーンがほぼ同値（7.67〜7.76）で跳ね、
        // 通常時も頭部と腰部の rms が区別できなかった。
        //
        // ローカル座標なら全身移動は差分ゼロになるため、関節運動だけが残る。
        // これは揺動機能の入力としても正しい。キャラが丸ごと動いたことを
        // 「揺れ」として扱う必要はないため。
        private Transform[] boneTargets;
        private Vector3[] bonePrevPos;
        private Vector3[] boneWindowStart;
        private float[] bonePeakDelta;
        private float[] boneSumSq;
        private int[] boneSamples;
        private int[] boneJumpFrames;
        private bool boneTargetsResolved;

        private Dictionary<string, string> ctrlFlagSnapshot;
        private bool ctrlFlagDumped;

        // ボーンのキャラローカル座標。全身の並進運動を除去した関節位置。
        private Vector3 GetBoneLocal(Transform bone)
        {
            if (bone == null || maleChar1 == null) return Vector3.zero;
            return maleChar1.transform.InverseTransformPoint(bone.position);
        }

        // maleChar1 のボーンから候補を拾い、階層をログに出す。
        private void ResolveBoneTargets()
        {
            try
            {
                if (maleChar1 == null) return;

                var all = maleChar1.GetComponentsInChildren<Transform>(true);
                Logger?.LogInfo($"[BONE_DIAG] maleChar1 = {maleChar1.name}#{maleChar1.GetInstanceID()} transforms={all.Length}");

                // 実名で引く。見つからなければ警告を出す（命名が違う環境の検出用）
                var hits = new List<Transform>();
                foreach (string want in BONE_TARGET_NAMES)
                {
                    Transform found = null;
                    foreach (var tr in all)
                    {
                        if (tr.name == want) { found = tr; break; }
                    }

                    if (found != null)
                    {
                        hits.Add(found);
                        Logger?.LogInfo($"[BONE_DIAG]   {want} : OK");
                    }
                    else
                    {
                        Logger?.LogWarning($"[BONE_DIAG]   {want} : NOT FOUND");
                    }
                }

                int take = hits.Count;
                boneTargets = new Transform[take];
                for (int i = 0; i < take; i++) boneTargets[i] = hits[i];

                bonePrevPos = new Vector3[take];
                boneWindowStart = new Vector3[take];
                bonePeakDelta = new float[take];
                boneSumSq = new float[take];
                boneSamples = new int[take];
                boneJumpFrames = new int[take];

                for (int i = 0; i < take; i++)
                {
                    Vector3 lp = GetBoneLocal(boneTargets[i]);
                    bonePrevPos[i] = lp;
                    boneWindowStart[i] = lp;
                }

                boneTargetsResolved = true;
                Logger?.LogInfo($"[BONE_DIAG] tracking {take} bone(s)");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[BONE_DIAG] failed: {ex.Message}");
            }
        }

        // 毎フレーム呼ぶ。統計を貯めるだけで、ログは出さない（単発跳躍を除く）。
        private void SampleBones()
        {
            try
            {
                if (!boneTargetsResolved || boneTargets == null) return;

                for (int i = 0; i < boneTargets.Length; i++)
                {
                    var tr = boneTargets[i];
                    if (tr == null) continue;

                    Vector3 pos = GetBoneLocal(tr);
                    float delta = (pos - bonePrevPos[i]).magnitude;
                    bonePrevPos[i] = pos;

                    if (delta > bonePeakDelta[i]) bonePeakDelta[i] = delta;
                    boneSumSq[i] += delta * delta;
                    boneSamples[i]++;

                    // 大きな移動が「何フレーム続くか」を数える。
                    // 1フレームで終わればスナップ切替、数フレーム続けばブレンド遷移。
                    if (delta > BONE_JUMP_DELTA)
                    {
                        boneJumpFrames[i]++;
                    }
                    else if (boneJumpFrames[i] > 0)
                    {
                        Logger?.LogInfo($"[BONE_JUMP] action={currentActionId} bone={tr.name} " +
                                        $"frames={boneJumpFrames[i]} (1=snap / 2+=blend)");
                        boneJumpFrames[i] = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[BONE_SAMPLE] failed: {ex.Message}");
            }
        }

        // 1秒ごとに統計を出して窓をリセットする。
        private void LogBoneStats()
        {
            try
            {
                if (!boneTargetsResolved || boneTargets == null) return;

                for (int i = 0; i < boneTargets.Length; i++)
                {
                    var tr = boneTargets[i];
                    if (tr == null || boneSamples[i] == 0) continue;

                    float rms = Mathf.Sqrt(boneSumSq[i] / boneSamples[i]);
                    Vector3 cur = GetBoneLocal(tr);
                    float netMove = (cur - boneWindowStart[i]).magnitude;

                    Logger?.LogInfo($"[BONE_AMP] action={currentActionId} bone={tr.name} " +
                                    $"peak={bonePeakDelta[i]:F4} rms={rms:F4} net={netMove:F4} n={boneSamples[i]}");

                    bonePeakDelta[i] = 0f;
                    boneSumSq[i] = 0f;
                    boneSamples[i] = 0;
                    boneWindowStart[i] = cur;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[BONE_AMP] failed: {ex.Message}");
            }
        }

        // ctrlFlag のメンバ一覧を一度だけ出す。
        private void LogCtrlFlagMembers()
        {
            try
            {
                if (ctrlFlagDumped) return;
                if (hSceneInstance == null || hSceneInstance.ctrlFlag == null) return;

                ctrlFlagDumped = true;
                Type t = hSceneInstance.ctrlFlag.GetType();
                Logger?.LogInfo($"[CTRLFLAG] type = {t.FullName}");

                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    Logger?.LogInfo($"[CTRLFLAG]   field  {f.Name} : {f.FieldType.Name}");

                foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    Logger?.LogInfo($"[CTRLFLAG]   prop   {pr.Name} : {pr.PropertyType.Name}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[CTRLFLAG] dump failed: {ex.Message}");
            }
        }

        // 1秒ごとに ctrlFlag の単純型フィールドを見て、変化したものだけ出す。
        // ステータス変化を知るイベント信号が存在するかを探すのが目的。
        private void TrackCtrlFlagChanges()
        {
            try
            {
                if (hSceneInstance == null || hSceneInstance.ctrlFlag == null) return;

                if (ctrlFlagSnapshot == null) ctrlFlagSnapshot = new Dictionary<string, string>();

                object flag = hSceneInstance.ctrlFlag;
                Type t = flag.GetType();

                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    Type ft = f.FieldType;
                    // 単純型のみ。複合オブジェクトはノイズになるので見ない
                    if (!(ft.IsPrimitive || ft.IsEnum || ft == typeof(string))) continue;

                    object v = f.GetValue(flag);
                    string cur = (v != null) ? v.ToString() : "null";

                    string prev;
                    if (ctrlFlagSnapshot.TryGetValue(f.Name, out prev))
                    {
                        if (prev != cur)
                        {
                            Logger?.LogInfo($"[CTRLFLAG] action={currentActionId} {f.Name}: {prev} -> {cur}");

                            // Animator が取れなかった場合の代替トリガ候補。
                            // 離散イベントに限り、直後のローカル変位を記録する。
                            if (f.Name == "loopType" || f.Name == "nowOrgasm" ||
                                f.Name == "isInsert" || f.Name == "kindCharaCtrl")
                            {
                                BeginTransitionMeasure();
                                ctrlFlagMeasurePending = 2;   // 2ポーリング後に締める
                            }
                        }
                    }

                    ctrlFlagSnapshot[f.Name] = cur;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[CTRLFLAG] track failed: {ex.Message}");
            }
        }

        // === CamDat 座標系の診断（v0.8.2.2） ===
        // 完全相対座標システムの設計に必要な事実を実測で確定させるための一回限りの出力。
        // 制御には一切使用しない。
        //
        // 確認する4点:
        //   1. transBase は null か。非null ならアンカーの正体
        //   2. 実行時の実際の型と、CameraUpdate をどのクラスが実装しているか
        //   3. isLimitPos / limitPos / isLimitDir / limitDir の実値
        //      （limitFov が dnSpy の初期化子 40f に対し実測 60 だった前例がある）
        //   4. femaleChar1 の回転軸（周期出力の LogCamDatTracking 側で観測）
        private void LogCamDatDiagnostics()
        {
            try
            {
                if (cameraControlVer2Instance == null) return;

                Type t = cameraControlVer2Instance.GetType();

                var chain = new List<string>();
                for (Type cur = t; cur != null && cur != typeof(MonoBehaviour); cur = cur.BaseType)
                    chain.Add(cur.Name);
                Logger?.LogInfo($"[CAMDAT_DIAG] runtime type: {t.FullName} (chain: {string.Join(" <- ", chain.ToArray())})");

                var camUpdate = t.GetMethod("CameraUpdate",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                Logger?.LogInfo($"[CAMDAT_DIAG] CameraUpdate declared in: {(camUpdate != null ? camUpdate.DeclaringType.Name : "(not found)")}");

                LogCamDatField(t, "isLimitPos");
                LogCamDatField(t, "limitPos");
                LogCamDatField(t, "isLimitDir");
                LogCamDatField(t, "limitDir");
                LogCamDatField(t, "targetObj");

                var tbField = t.GetField("transBase", BindingFlags.Public | BindingFlags.Instance);
                if (tbField == null)
                {
                    Logger?.LogWarning("[CAMDAT_DIAG] transBase field not found");
                    return;
                }

                var tb = tbField.GetValue(cameraControlVer2Instance) as Transform;
                if (tb == null)
                {
                    Logger?.LogInfo("[CAMDAT_DIAG] transBase = null  --> CamDat is interpreted in WORLD space");
                }
                else
                {
                    Logger?.LogInfo($"[CAMDAT_DIAG] transBase = {tb.name} #{tb.GetInstanceID()} " +
                        $"pos={tb.position} rot={tb.rotation.eulerAngles} " +
                        $"parent={(tb.parent != null ? tb.parent.name : "(none)")}");

                    // scale は TransformPoint / InverseTransformPoint の往復に影響する。
                    // 1 でなければ方式B の変換が恒等写像にならない（仕様書 v2.1 §8-1）。
                    Logger?.LogInfo($"[CAMDAT_DIAG] transBase scale local={tb.localScale} lossy={tb.lossyScale}");
                    Logger?.LogInfo("[CAMDAT_DIAG] transBase is NOT null --> CamDat.Pos is transBase-local");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[CAMDAT_DIAG] failed: {ex.Message}");
            }
        }

        private void LogCamDatField(Type t, string fieldName)
        {
            try
            {
                var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (f == null)
                {
                    Logger?.LogInfo($"[CAMDAT_DIAG] {fieldName} = (not found)");
                    return;
                }

                var v = f.GetValue(cameraControlVer2Instance);
                Logger?.LogInfo($"[CAMDAT_DIAG] {fieldName} = {(v != null ? v.ToString() : "null")}");
            }
            catch (Exception ex)
            {
                Logger?.LogInfo($"[CAMDAT_DIAG] {fieldName} = (error: {ex.Message})");
            }
        }

        // 周期出力。transBase が femaleChar1 に追従するか、femaleChar1 が Y 軸以外へ
        // 回転するかは、単発の値では判定できないため時系列で観測する。
        //
        // 注意: 参照が同一かどうかで判定してはならない。SetBase() は transBase の参照を
        // 差し替えるのではなく、既存アンカーへ位置と回転をコピーする実装のため、
        // 参照比較は常に false になる。位置・回転が追従しているかで判断すること。
        private void LogCamDatTracking()
        {
            try
            {
                if (!isCameraControlInitialized || cameraControlVer2Instance == null) return;
                if (camDatField == null || posField == null || rotField == null || dirField == null) return;

                Type t = cameraControlVer2Instance.GetType();
                var tbField = t.GetField("transBase", BindingFlags.Public | BindingFlags.Instance);
                var tb = (tbField != null) ? tbField.GetValue(cameraControlVer2Instance) as Transform : null;

                var camDatInstance = camDatField.GetValue(cameraControlVer2Instance);
                if (camDatInstance == null) return;

                Vector3 cdPos = (Vector3)posField.GetValue(camDatInstance);
                Vector3 cdRot = (Vector3)rotField.GetValue(camDatInstance);
                Vector3 cdDir = (Vector3)dirField.GetValue(camDatInstance);

                string tbStr = (tb != null)
                    ? $"pos={tb.position} rot={tb.rotation.eulerAngles}"
                    : "null";

                string f1Str = (femaleChar1 != null)
                    ? $"{femaleChar1.name}#{femaleChar1.GetInstanceID()} pos={femaleChar1.transform.position} rot={femaleChar1.transform.eulerAngles}"
                    : "null";

                Logger?.LogInfo($"[CAMDAT_TRACK] action={currentActionId} transBase[{tbStr}] female1[{f1Str}]");

                string scaleStr = (tb != null ? $"tb={tb.lossyScale}" : "tb=null") +
                                  (femaleChar1 != null ? $" f1={femaleChar1.transform.lossyScale}" : " f1=null");
                Logger?.LogInfo($"[CAMDAT_TRACK] lossyScale {scaleStr}");
                Logger?.LogInfo($"[CAMDAT_TRACK] CamDat Pos={cdPos} Rot={cdRot} Dir={cdDir}");

                var cam = GetTargetCamera();
                if (cam != null)
                    Logger?.LogInfo($"[CAMDAT_TRACK] cameraWorld pos={cam.transform.position} rot={cam.transform.rotation.eulerAngles}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[CAMDAT_TRACK] failed: {ex.Message}");
            }
        }

        // Hシーンのカメラ本体（BaseCameraControl_Ver2.thisCamera）を返す。
        // ゲーム側が FOV を書き込む対象そのもの。
        //
        // 実測（2026-08-30 / Better Repack R16 環境）では Camera.main と完全に
        // 同一インスタンスだった（いずれも "Main Camera" #483378）。つまり
        // v0.8.0.2 の Camera.main 参照は誤ったカメラを掴んでいたわけではない。
        // それでも thisCamera を使うのは、Camera.main が「MainCameraタグを持つ
        // 最初の有効なカメラ」という環境依存の解決をするのに対し、thisCamera は
        // 制御対象を一意に指すため。同一性は起動時の [FOV_INIT] ログで確認できる。
        private Camera GetTargetCamera()
        {
            try
            {
                if (cameraControlVer2Instance == null || thisCameraProperty == null) return null;
                return thisCameraProperty.GetValue(cameraControlVer2Instance) as Camera;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"[FOV] thisCamera acquisition failed: {ex.Message}");
                return null;
            }
        }

        // ゲーム側が保持している現在の FOV（CamDat.Fov）を読む。
        private bool TryGetCamDatFov(out float fov)
        {
            fov = 0f;
            try
            {
                if (cameraControlVer2Instance == null || camDatField == null || fovField == null) return false;

                var camDatInstance = camDatField.GetValue(cameraControlVer2Instance);
                if (camDatInstance == null) return false;

                fov = (float)fovField.GetValue(camDatInstance);
                return true;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"[FOV] CamDat.Fov read failed: {ex.Message}");
                return false;
            }
        }

        // FOV を CamDat.Fov と thisCamera.fieldOfView の両方へ書く。
        // ゲーム側（BaseCameraControl_Ver2）が両方を同時に更新する実装のため、
        // 片方だけ書くと内部値と描画がずれ、次のユーザー操作時に値が飛ぶ。
        private bool ApplyFovToGame(float fov)
        {
            try
            {
                if (cameraControlVer2Instance == null || camDatField == null || fovField == null) return false;

                var camDatInstance = camDatField.GetValue(cameraControlVer2Instance);
                if (camDatInstance == null) return false;

                fovField.SetValue(camDatInstance, fov);
                camDatField.SetValue(cameraControlVer2Instance, camDatInstance); // 構造体ライトバック

                var cam = GetTargetCamera();
                if (cam != null) cam.fieldOfView = fov;

                return true;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"[FOV] ApplyFovToGame failed: {ex.Message}");
                return false;
            }
        }

        // ゲーム側の FOV 上下限でクランプする。
        // 手編集された保存データや、別環境（limitFov が異なるシーン）で保存された
        // データを適用したときに、ゲームの想定範囲外の画角になるのを防ぐ安全網。
        //
        // limitFov / limitFovMin は public フィールドでシーン側の値が優先されるため、
        // dnSpy の初期化子（40f / 10f）を定数として埋め込んではならない。必ず
        // インスタンスの実値を読む。取得できない場合はクランプせず素通しする。
        //
        // 実測（2026-08-30 / Better Repack R16 環境）では 10 - 60 だった。
        // dnSpy の初期化子は 10f / 40f であり、上限が一致しない。この取り違えが
        // v0.8.1.0 の誤診断の原因になった。詳細は
        // documents/FOV制御問題の調査記録.md を参照。
        private float ClampFovToGameLimits(float fov)
        {
            try
            {
                if (cameraControlVer2Instance == null || limitFovField == null || limitFovMinField == null)
                    return fov;

                float max = (float)limitFovField.GetValue(cameraControlVer2Instance);
                float min = (float)limitFovMinField.GetValue(cameraControlVer2Instance);
                if (max <= min) return fov;

                float clamped = Mathf.Clamp(fov, min, max);
                if (ShowDebugInfo?.Value == true && Mathf.Abs(clamped - fov) > 0.001f)
                    Logger?.LogInfo($"[FOV_CLAMP] {fov} -> {clamped} (limits {min}-{max})");

                return clamped;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"[FOV_CLAMP] Failed, passing through: {ex.Message}");
                return fov;
            }
        }

        private bool TryApplyCamDatDirect(CameraState cameraState)
        {
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"[CAMDAT_APPLY] Direct strategy called - Initialized:{isCameraControlInitialized}");

            try
            {
                if (cameraControlVer2Instance == null || camDatField == null)
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogWarning($"[CAMDAT_APPLY] Prerequisites failed");
                    return false;
                }

                isRestoringCamera = true;

                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"[CAMDAT_APPLY] LateUpdate blocking started");

                var camDatInstance = camDatField.GetValue(cameraControlVer2Instance);
                if (camDatInstance == null)
                {
                    isRestoringCamera = false;
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogWarning($"[CAMDAT_APPLY] CamDat instance is null");
                    return false;
                }

                // 保存値は femaleChar1 基準。その時点の transBase 基準へ戻してから書く（v0.8.3.0）。
                if (posField != null && rotField != null &&
                    cameraState.CamDatPos != null && cameraState.CamDatRot != null)
                {
                    Vector3 savedPos = cameraState.CamDatPos.ToVector3();
                    Vector3 savedRot = cameraState.CamDatRot.ToVector3();

                    Vector3 applyPos, applyRot;
                    bool converted = TryToCamDatSpace(savedPos, savedRot, out applyPos, out applyRot);

                    posField.SetValue(camDatInstance, applyPos);
                    rotField.SetValue(camDatInstance, applyRot);

                    if (!converted)
                        Logger?.LogWarning("[RELATIVE] Applied raw value (transBase or femaleChar1 unavailable)");

                    if (ShowDebugInfo?.Value == true)
                    {
                        Logger?.LogInfo($"CamDat.Pos set: charRelative {savedPos} -> {applyPos} (converted={converted})");
                        Logger?.LogInfo($"CamDat.Rot set: charRelative {savedRot} -> {applyRot}");
                    }
                }

                if (dirField != null && cameraState.CamDatDir != null)
                {
                    var dirVector = cameraState.CamDatDir.ToVector3();
                    dirVector.z = Mathf.Min(0f, dirVector.z);
                    dirField.SetValue(camDatInstance, dirVector);
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"CamDat.Dir set: {dirVector}");
                }

                if (fovField != null)
                {
                    // ゲーム側の上下限で丸めてから適用する（範囲内ならそのまま通る）。
                    float fovToApply = ClampFovToGameLimits(cameraState.CamDatFov);

                    fovField.SetValue(camDatInstance, fovToApply);

                    // === FOV復元改善（v0.8.1.0）：lastUserFov更新 ===
                    lastUserFov = fovToApply;

                    var thisCamera = GetTargetCamera();
                    if (thisCamera != null)
                    {
                        thisCamera.fieldOfView = fovToApply;
                        if (ShowDebugInfo?.Value == true)
                            Logger?.LogInfo($"Unity Camera.fieldOfView set: {fovToApply}");
                    }

                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"CamDat.Fov set: {fovToApply} (lastUserFov updated)");
                }

                camDatField.SetValue(cameraControlVer2Instance, camDatInstance);

                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo("CamDat updated with struct writeback");

                StartCoroutine(WaitAndReleaseCameraControl());

                return true;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"CamDat direct operation failed: {ex.Message}");
                isRestoringCamera = false;
                return false;
            }
        }

        private IEnumerator WaitAndReleaseCameraControl()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.1f);

            isRestoringCamera = false;

            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo("[CAMDAT_APPLY] LateUpdate blocking released");
        }

        private string GetCurrentMapKey()
        {
            try
            {
                if (hSceneInstance != null)
                {
                    if (HSceneManager.Instance != null && BaseMap.infoTable.TryGetValue(HSceneManager.Instance.mapID, out MapInfo.Param param))
                    {
                        string mapName = param.MapNames.FirstOrDefault();
                        if (mapName != null)
                        {
                            if (ShowDebugInfo?.Value == true)
                                Logger?.LogInfo($"[BASEMAP_SUCCESS] MapKey: {mapName}");
                            return mapName;
                        }
                    }

                    var sceneName = Manager.Scene.ActiveScene.name;
                    if (!string.IsNullOrEmpty(sceneName))
                    {
                        if (ShowDebugInfo?.Value == true)
                            Logger?.LogInfo($"[MANAGER_SCENE] MapKey: {sceneName}");
                        return sceneName;
                    }
                }

                GameObject commonSpace = GameObject.Find("CommonSpace");
                if (commonSpace != null && commonSpace.transform.childCount > 0)
                {
                    for (int i = 0; i < commonSpace.transform.childCount; i++)
                    {
                        var child = commonSpace.transform.GetChild(i);
                        if (!child.name.StartsWith("cha"))
                        {
                            if (ShowDebugInfo?.Value == true)
                                Logger?.LogInfo($"[COMMONSPACE] MapKey: {child.name}");
                            return child.name;
                        }
                    }

                    var firstChild = commonSpace.transform.GetChild(0);
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"[COMMONSPACE_FIRST] MapKey: {firstChild.name}");
                    return firstChild.name;
                }

                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"[DEFAULT_FALLBACK] MapKey: DefaultMap");
                return "DefaultMap";
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"MapKey detection failed: {ex.Message}");
                return "DefaultMap";
            }
        }

        private int GetCurrentActionId()
        {
            int actionId = -1;
            if (hSceneInstance != null && hSceneInstance.ctrlFlag != null && hSceneInstance.ctrlFlag.nowAnimationInfo != null)
            {
                actionId = hSceneInstance.ctrlFlag.nowAnimationInfo.id;
            }

            return actionId;
        }

        private void InitializeCameraTypeDetection()
        {
            try
            {
                if (!EnableCameraFunction.Value)
                {
                    Logger?.LogInfo("CameraControl_Ver2 type detection: Disabled");
                    return;
                }

                Logger?.LogInfo("[TYPE_DETECTION] Starting CameraControl_Ver2 type detection...");

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    if (assembly.GetName().Name == "Assembly-CSharp")
                    {
                        var types = assembly.GetTypes();
                        foreach (var type in types)
                        {
                            if (type.Name == "CameraControl_Ver2" || type.Name == "BaseCameraControl_Ver2")
                            {
                                cameraControlVer2Type = type;
                                Logger?.LogInfo($"[TYPE_FOUND] Camera type detected: {type.FullName}");

                                Type camDatSourceType = null;

                                if (type.Name == "CameraControl_Ver2")
                                {
                                    camDatSourceType = type.BaseType;
                                    if (ShowDebugInfo?.Value == true)
                                        Logger?.LogInfo($"Using BaseType for CamDat: {camDatSourceType?.Name}");
                                }
                                else if (type.Name == "BaseCameraControl_Ver2")
                                {
                                    camDatSourceType = type;
                                    if (ShowDebugInfo?.Value == true)
                                        Logger?.LogInfo($"Using direct type for CamDat: {camDatSourceType.Name}");
                                }

                                if (camDatSourceType != null)
                                {
                                    camDatField = camDatSourceType.GetField("CamDat",
                                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

                                    if (camDatField != null)
                                    {
                                        Logger?.LogInfo($"[FIELD_FOUND] CamDat field detected in {camDatSourceType.Name}");
                                    }
                                    else
                                    {
                                        Logger?.LogWarning($"[FIELD_MISSING] CamDat field not found in {camDatSourceType.Name}");
                                    }
                                }
                                break;
                            }
                        }
                        if (cameraControlVer2Type != null) break;
                    }
                }

                if (cameraControlVer2Type != null)
                {
                    Logger?.LogInfo("[TYPE_DETECTION] Camera type detection completed successfully");
                }
                else
                {
                    Logger?.LogWarning("[TYPE_DETECTION] Camera type not found");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Camera type detection failed: {ex.Message}");
            }
        }

        private void SaveData()
        {
            try
            {
                if (string.IsNullOrEmpty(currentMapKey))
                {
                    Logger?.LogWarning("Cannot save: currentMapKey is empty");
                    return;
                }

                if (!Directory.Exists(configDirectory))
                {
                    Logger?.LogWarning($"Config directory missing, recreating: {configDirectory}");
                    Directory.CreateDirectory(configDirectory);
                }

                string filePath = GetDataFilePath(currentMapKey, currentKeyIsCustom);

                var lines = new List<string>();

                // データフォーマット説明をコメントとして追加
                lines.Add("# Composition Director Data Format");
                lines.Add("# Position: Pos_[MapKey]_[ActionID]_[PosSlot]=f1x,f1y,f1z,f1rot|f2x,f2y,f2z,f2rot|m1x,m1y,m1z,m1rot|m2x,m2y,m2z,m2rot");
                lines.Add("# Camera: Cam_[MapKey]_[ActionID]_[PosSlot]_[CamSlot]=posx,posy,posz|rotx,roty,rotz|dirx,diry,dirz|fov");
                lines.Add("# BasisPos: BasisPos_[MapKey]_[BasisSlot]=f1x,f1y,f1z,f1rot|f2x,f2y,f2z,f2rot|m1x,m1y,m1z,m1rot|m2x,m2y,m2z,m2rot");
                lines.Add("");  // 空行

                foreach (var actionKvp in actionData)
                {
                    int actionId = actionKvp.Key;
                    var compositionSet = actionKvp.Value;

                    for (int posSlot = 0; posSlot < compositionSet.PositionSlots.Length; posSlot++)
                    {
                        var posData = compositionSet.PositionSlots[posSlot];

                        if (posData.HasPositionData)
                        {
                            string posKey = $"Pos_{currentMapKey}_{actionId}_{posSlot}";
                            string posValue = $"{posData.Female1.x},{posData.Female1.y},{posData.Female1.z},{posData.Female1.rotY}|" +
                                            $"{posData.Female2.x},{posData.Female2.y},{posData.Female2.z},{posData.Female2.rotY}|" +
                                            $"{posData.Male1.x},{posData.Male1.y},{posData.Male1.z},{posData.Male1.rotY}|" +
                                            $"{posData.Male2.x},{posData.Male2.y},{posData.Male2.z},{posData.Male2.rotY}";
                            lines.Add($"{posKey}={posValue}");
                        }

                        for (int camSlot = 0; camSlot < posData.CameraSlots.Length; camSlot++)
                        {
                            if (posData.HasCameraData[camSlot])
                            {
                                var camData = posData.CameraSlots[camSlot];
                                string camKey = $"Cam_{currentMapKey}_{actionId}_{posSlot}_{camSlot}";
                                string camValue = $"{camData.CamDatPos.x},{camData.CamDatPos.y},{camData.CamDatPos.z}|" +
                                                $"{camData.CamDatRot.x},{camData.CamDatRot.y},{camData.CamDatRot.z}|" +
                                                $"{camData.CamDatDir.x},{camData.CamDatDir.y},{camData.CamDatDir.z}|" +
                                                $"{camData.CamDatFov}";
                                lines.Add($"{camKey}={camValue}");
                            }
                        }
                    }
                }

                if (basisPositionData.ContainsKey(currentMapKey))
                {
                    var basisSet = basisPositionData[currentMapKey];
                    for (int basisSlot = 0; basisSlot < basisSet.HasBasisPositionData.Length; basisSlot++)
                    {
                        if (basisSet.HasBasisPositionData[basisSlot])
                        {
                            int baseIndex = basisSlot * 4;
                            string basisKey = $"BasisPos_{currentMapKey}_{basisSlot}";
                            string basisValue = $"{basisSet.BasisPositions[baseIndex].x},{basisSet.BasisPositions[baseIndex].y},{basisSet.BasisPositions[baseIndex].z},{basisSet.BasisPositions[baseIndex].rotY}|" +
                                              $"{basisSet.BasisPositions[baseIndex + 1].x},{basisSet.BasisPositions[baseIndex + 1].y},{basisSet.BasisPositions[baseIndex + 1].z},{basisSet.BasisPositions[baseIndex + 1].rotY}|" +
                                              $"{basisSet.BasisPositions[baseIndex + 2].x},{basisSet.BasisPositions[baseIndex + 2].y},{basisSet.BasisPositions[baseIndex + 2].z},{basisSet.BasisPositions[baseIndex + 2].rotY}|" +
                                              $"{basisSet.BasisPositions[baseIndex + 3].x},{basisSet.BasisPositions[baseIndex + 3].y},{basisSet.BasisPositions[baseIndex + 3].z},{basisSet.BasisPositions[baseIndex + 3].rotY}";
                            lines.Add($"{basisKey}={basisValue}");
                        }
                    }
                }

                File.WriteAllLines(filePath, lines);

                if (ShowDebugInfo?.Value == true)
                    Logger?.LogInfo($"Data saved to: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Save data error: {ex.Message}");
            }
        }

        // 保存ファイル内で実際に使われている最大スロット番号を調べ、配列の確保数を決める。
        // Config のスロット数を後から減らしても既存データが失われないよう、ファイルの
        // 実データに合わせて確保する。UI からは Config 数までしか巡回できないが、
        // データは保持され、Config を戻せば再び利用できる。
        private void ScanSlotCapacities(string[] lines, out int posCapacity, out int camCapacity, out int basisCapacity)
        {
            posCapacity = MaxPositionSlots.Value;
            camCapacity = MaxCameraSlots.Value;
            basisCapacity = MaxBasisPositionSlots.Value;

            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq);
                string[] keyParts = key.Split('_');

                // キーのパースは末尾からのインデックスで行う（MapKey に '_' が
                // 含まれても壊れないようにするための設計。維持すること）
                if (key.StartsWith("Pos_") && keyParts.Length >= 4)
                {
                    if (int.TryParse(keyParts[keyParts.Length - 1], out int posSlot))
                        posCapacity = Math.Max(posCapacity, posSlot + 1);
                }
                else if (key.StartsWith("Cam_") && keyParts.Length >= 5)
                {
                    if (int.TryParse(keyParts[keyParts.Length - 2], out int posSlot))
                        posCapacity = Math.Max(posCapacity, posSlot + 1);
                    if (int.TryParse(keyParts[keyParts.Length - 1], out int camSlot))
                        camCapacity = Math.Max(camCapacity, camSlot + 1);
                }
                else if (key.StartsWith("BasisPos_") && keyParts.Length >= 3)
                {
                    if (int.TryParse(keyParts[keyParts.Length - 1], out int basisSlot))
                        basisCapacity = Math.Max(basisCapacity, basisSlot + 1);
                }
            }

            posCapacity = Mathf.Clamp(posCapacity, 1, MAX_SLOT_CAPACITY);
            camCapacity = Mathf.Clamp(camCapacity, 1, MAX_SLOT_CAPACITY);
            basisCapacity = Mathf.Clamp(basisCapacity, 1, MAX_SLOT_CAPACITY);
        }

        private void LoadMapSpecificData(string mapKey)
        {
            actionData.Clear();
            if (basisPositionData.ContainsKey(mapKey))
                basisPositionData.Remove(mapKey);

            string filePath = GetDataFilePath(mapKey, currentKeyIsCustom);

            string[] lines;
            try
            {
                if (!File.Exists(filePath))
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"No data file found for map: {mapKey}");
                    return;
                }

                lines = File.ReadAllLines(filePath);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Load map data error (file read): {ex.Message}");
                return;
            }

            ScanSlotCapacities(lines, out int posCapacity, out int camCapacity, out int basisCapacity);

            if (posCapacity > MaxPositionSlots.Value || camCapacity > MaxCameraSlots.Value ||
                basisCapacity > MaxBasisPositionSlots.Value)
            {
                Logger?.LogWarning($"[LOAD] Saved data uses more slots than configured " +
                    $"(Pos {posCapacity}/{MaxPositionSlots.Value}, Cam {camCapacity}/{MaxCameraSlots.Value}, " +
                    $"Basis {basisCapacity}/{MaxBasisPositionSlots.Value}). Data is kept; " +
                    $"raise the slot count in config to access it.");
            }

            int skipped = 0;

            foreach (string line in lines)
            {
                // 行単位で例外を捕捉する。
                // v0.8.1.2 以前はループ全体を1つの try が包んでおり、1行の異常
                // （Config でスロット数を減らした場合の添字超過など）で catch が
                // actionData.Clear() を実行し、そのマップのデータが全消失していた。
                // さらに、その状態で保存するとファイル側も上書きされ復旧不能だった。
                try
                {
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                    if (!line.Contains("=")) continue;

                    string[] parts = line.Split(new char[] { '=' }, 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0];
                    string value = parts[1];

                    if (key.StartsWith("Pos_"))
                    {
                        string[] keyParts = key.Split('_');
                        if (keyParts.Length < 4) continue;

                        if (!int.TryParse(keyParts[keyParts.Length - 2], out int actionId) ||
                            !int.TryParse(keyParts[keyParts.Length - 1], out int posSlot)) continue;

                        if (!actionData.ContainsKey(actionId))
                            actionData[actionId] = new CompositionSet(posCapacity, camCapacity);

                        var compositionSet = actionData[actionId];
                        if (posSlot < 0 || posSlot >= compositionSet.PositionSlots.Length)
                        {
                            skipped++;
                            Logger?.LogWarning($"[LOAD_SKIP] Position slot {posSlot} out of range (capacity {compositionSet.PositionSlots.Length}): {key}");
                            continue;
                        }

                        string[] charData = value.Split('|');
                        if (charData.Length != 4) continue;

                        var posData = compositionSet.PositionSlots[posSlot];

                        string[] f1 = charData[0].Split(',');
                        if (f1.Length == 4)
                            posData.Female1 = new Vector3Data(new Vector3(float.Parse(f1[0]), float.Parse(f1[1]), float.Parse(f1[2])), float.Parse(f1[3]));

                        string[] f2 = charData[1].Split(',');
                        if (f2.Length == 4)
                            posData.Female2 = new Vector3Data(new Vector3(float.Parse(f2[0]), float.Parse(f2[1]), float.Parse(f2[2])), float.Parse(f2[3]));

                        string[] m1 = charData[2].Split(',');
                        if (m1.Length == 4)
                            posData.Male1 = new Vector3Data(new Vector3(float.Parse(m1[0]), float.Parse(m1[1]), float.Parse(m1[2])), float.Parse(m1[3]));

                        string[] m2 = charData[3].Split(',');
                        if (m2.Length == 4)
                            posData.Male2 = new Vector3Data(new Vector3(float.Parse(m2[0]), float.Parse(m2[1]), float.Parse(m2[2])), float.Parse(m2[3]));

                        posData.HasPositionData = true;
                    }
                    else if (key.StartsWith("Cam_"))
                    {
                        string[] keyParts = key.Split('_');
                        if (keyParts.Length < 5) continue;

                        if (!int.TryParse(keyParts[keyParts.Length - 3], out int actionId) ||
                            !int.TryParse(keyParts[keyParts.Length - 2], out int posSlot) ||
                            !int.TryParse(keyParts[keyParts.Length - 1], out int camSlot)) continue;

                        if (!actionData.ContainsKey(actionId))
                            actionData[actionId] = new CompositionSet(posCapacity, camCapacity);

                        var compositionSet = actionData[actionId];
                        if (posSlot < 0 || posSlot >= compositionSet.PositionSlots.Length ||
                            camSlot < 0 || camSlot >= compositionSet.PositionSlots[posSlot].CameraSlots.Length)
                        {
                            skipped++;
                            Logger?.LogWarning($"[LOAD_SKIP] Camera slot out of range (Pos {posSlot}, Cam {camSlot}): {key}");
                            continue;
                        }

                        string[] camParts = value.Split('|');
                        if (camParts.Length != 4) continue;

                        var camData = compositionSet.PositionSlots[posSlot].CameraSlots[camSlot];

                        string[] pos = camParts[0].Split(',');
                        if (pos.Length == 3)
                            camData.CamDatPos = new Vector3Data(new Vector3(float.Parse(pos[0]), float.Parse(pos[1]), float.Parse(pos[2])));

                        string[] rot = camParts[1].Split(',');
                        if (rot.Length == 3)
                            camData.CamDatRot = new Vector3Data(new Vector3(float.Parse(rot[0]), float.Parse(rot[1]), float.Parse(rot[2])));

                        string[] dir = camParts[2].Split(',');
                        if (dir.Length == 3)
                            camData.CamDatDir = new Vector3Data(new Vector3(float.Parse(dir[0]), float.Parse(dir[1]), float.Parse(dir[2])));

                        camData.CamDatFov = float.Parse(camParts[3]);
                        camData.HasData = true;
                        compositionSet.PositionSlots[posSlot].HasCameraData[camSlot] = true;
                    }
                    else if (key.StartsWith("BasisPos_"))
                    {
                        string[] keyParts = key.Split('_');
                        if (keyParts.Length < 3) continue;

                        if (!int.TryParse(keyParts[keyParts.Length - 1], out int basisSlot)) continue;

                        if (!basisPositionData.ContainsKey(mapKey))
                            basisPositionData[mapKey] = new BasisPositionSet(basisCapacity);

                        var basisSet = basisPositionData[mapKey];
                        if (basisSlot < 0 || basisSlot >= basisSet.HasBasisPositionData.Length)
                        {
                            skipped++;
                            Logger?.LogWarning($"[LOAD_SKIP] BasisPos slot {basisSlot} out of range (capacity {basisSet.HasBasisPositionData.Length}): {key}");
                            continue;
                        }

                        string[] charData = value.Split('|');
                        if (charData.Length != 4) continue;

                        int baseIndex = basisSlot * 4;

                        string[] f1 = charData[0].Split(',');
                        if (f1.Length == 4)
                            basisSet.BasisPositions[baseIndex] = new Vector3Data(new Vector3(float.Parse(f1[0]), float.Parse(f1[1]), float.Parse(f1[2])), float.Parse(f1[3]));

                        string[] f2 = charData[1].Split(',');
                        if (f2.Length == 4)
                            basisSet.BasisPositions[baseIndex + 1] = new Vector3Data(new Vector3(float.Parse(f2[0]), float.Parse(f2[1]), float.Parse(f2[2])), float.Parse(f2[3]));

                        string[] m1 = charData[2].Split(',');
                        if (m1.Length == 4)
                            basisSet.BasisPositions[baseIndex + 2] = new Vector3Data(new Vector3(float.Parse(m1[0]), float.Parse(m1[1]), float.Parse(m1[2])), float.Parse(m1[3]));

                        string[] m2 = charData[3].Split(',');
                        if (m2.Length == 4)
                            basisSet.BasisPositions[baseIndex + 3] = new Vector3Data(new Vector3(float.Parse(m2[0]), float.Parse(m2[1]), float.Parse(m2[2])), float.Parse(m2[3]));

                        basisSet.HasBasisPositionData[basisSlot] = true;
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    Logger?.LogWarning($"[LOAD_SKIP] Malformed line skipped: {ex.Message}");
                }
            }

            if (skipped > 0)
                Logger?.LogWarning($"[LOAD] {skipped} line(s) skipped in {Path.GetFileName(filePath)}. Other data was loaded normally.");

            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo($"Map data loaded from: {Path.GetFileName(filePath)}, Actions: {actionData.Count}, BasisPos: {(basisPositionData.ContainsKey(mapKey) ? "Yes" : "No")}");
        }

        // 保存先をConfigの指定で上書きする（v0.14.0.0）。
        // 空なら既定のまま。使えないパスなら警告して既定を維持する。
        // 先頭の <GameRoot> はゲームのフォルダに置き換える（v0.14.4.0）。
        // ConfigurationManager の属性を、並び順つきで作る（v0.15.0.0）。
        // CM はセクション内を Order の降順、次に名前順で並べる。
        // CM が入っていなければ、この属性は誰にも読まれないだけで害は無い。
        private static object CmAttr(int order)
        {
            return new ConfigurationManagerAttributes { Order = order };
        }

        private void ResolveDataDirectory()
        {
            string custom = DataDirectory?.Value;
            if (string.IsNullOrEmpty(custom) || custom.Trim().Length == 0) return;

            try
            {
                string full = Path.GetFullPath(ExpandGameRoot(custom.Trim()));
                Directory.CreateDirectory(full);
                configDirectory = full;
                Logger?.LogInfo($"Data directory (from config): {configDirectory}");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Data Directory is unusable, keeping default: {ex.Message}");
            }
        }

        // 先頭の <GameRoot> をゲームのフォルダへ置き換える（v0.14.4.0）。
        //
        // v0.14.3.2 までは置き換えておらず、"<" ">" がパスに使えない文字として弾かれていた。
        // 起動のたびに "Data Directory is unusable" の警告を出して既定へ戻り、それが
        // たまたま同じ場所だったので気づかれなかった（実機の cfg で実際に起きていた）。
        //
        // 置き換えるのは先頭だけ。途中に書かれても意味を持たないのでそのまま渡す
        // （結果としてパスに使えない文字で弾かれ、既定へ戻る）。
        // 区切り文字の有無は問わない。<GameRoot>\x / <GameRoot>/x / <GameRoot>x のどれも同じ。
        // 大文字小文字も問わない。
        private static string ExpandGameRoot(string path)
        {
            if (!path.StartsWith(GAME_ROOT_TOKEN, StringComparison.OrdinalIgnoreCase))
                return path;

            string rest = path.Substring(GAME_ROOT_TOKEN.Length).TrimStart('\\', '/');
            return rest.Length == 0 ? Paths.GameRootPath : Path.Combine(Paths.GameRootPath, rest);
        }

        // データファイルのパス。組み立てをここ1箇所に集約している。
        // 保存側と読込側で食い違うと、保存したものが読めなくなる。
        private string GetDataFilePath(string key, bool isCustom)
        {
            string name = SanitizeFileName(key) + (isCustom ? CUSTOM_KEY_SUFFIX : "") + ".txt";
            return Path.Combine(configDirectory, name);
        }

        // === CMD の状態を読む ===
        //
        // GUID ではなく型名で引く。GUID は変更されうるが、名前空間とクラス名は
        // 据え置きの方針のため（プラグイン名が HS2 Importer → HS2 Custom Map Director と
        // 変わった後も型は HS2_ImporterCore のまま）。アセンブリ参照は張らない。
        private Type ResolveImporterType()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType("HS2_ImporterCore.HS2_ImporterCore", false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private void PollImporter()
        {
            try
            {
                // 見つかるまで再試行する。全アセンブリ走査なので1秒に1回まで。
                if (importerSerialProp == null &&
                    Time.realtimeSinceStartup - importerResolveTry > 1f)
                {
                    importerResolveTry = Time.realtimeSinceStartup;
                    importerType = ResolveImporterType();
                    if (importerType != null)
                    {
                        const BindingFlags F = BindingFlags.Public | BindingFlags.Static;
                        importerNameProp = importerType.GetProperty("CurrentConfigName", F);
                        importerSerialProp = importerType.GetProperty("ConfigLoadSerial", F);
                        if (importerSerialProp != null)
                            Logger?.LogInfo("[LINK] HS2 Custom Map Director detected");
                    }
                }
                if (importerSerialProp == null) return;   // CMD が無い環境

                int serial = (int)importerSerialProp.GetValue(null, null);
                if (serial == importerLastSerial) return;
                importerLastSerial = serial;

                importerConfigName = importerNameProp.GetValue(null, null) as string;

                // CMD で設定を読む／保存するという行為自体が「追従したい」という
                // 意思表示なので、切っていたら自動で戻す（API仕様書 §3「粘る」）。
                if (!FollowImporterConfig.Value)
                    FollowImporterConfig.Value = true;

                ApplyEffectiveKey("importer");
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"[LINK] poll failed: {ex.Message}");
            }
        }

        // いま使うべき鍵を決めて、変わっていれば読み替える。
        //
        // 【重要】判定は「鍵が変わったか」であって「serial が変わったか」ではない。
        // CMD は保存でも通知するため、serial で読み直すと、同じ設定を保存し
        // 直すたびに CD がディスクから読み戻して編集中の内容が消える。
        private void ApplyEffectiveKey(string reason)
        {
            bool useImporter = FollowImporterConfig.Value &&
                               !string.IsNullOrEmpty(importerConfigName);

            string newKey = useImporter ? importerConfigName : vanillaMapKey;
            bool newIsCustom = useImporter;

            if (string.IsNullOrEmpty(newKey)) return;
            if (newKey == currentMapKey && newIsCustom == currentKeyIsCustom) return;

            currentMapKey = newKey;
            currentKeyIsCustom = newIsCustom;
            mapKeyDetected = true;

            Logger?.LogInfo($"[LINK] key -> {currentMapKey}" +
                            $"{(newIsCustom ? " (custom)" : "")} ({reason})");

            ExecuteMapDataLoadingBlock();
        }

        // いまメモリにあるセットを、CMD の設定名で保存する。
        // ベースの構図を複製して新しい空間の出発点にするための操作。
        // 空から作り直させないための機能であり、これが無いと差し替えのたびに
        // 全体位を作り直すことになる。
        private void SaveAsImporterSet()
        {
            if (string.IsNullOrEmpty(importerConfigName))
            {
                Logger?.LogWarning("[LINK] No CMD config name to save as");
                return;
            }

            string prevKey = currentMapKey;

            // BasisPos はマップ鍵で引く辞書に入っている。鍵を変える前に移し替える。
            if (!string.IsNullOrEmpty(prevKey) &&
                basisPositionData.ContainsKey(prevKey) &&
                !basisPositionData.ContainsKey(importerConfigName))
            {
                basisPositionData[importerConfigName] = basisPositionData[prevKey];
            }

            currentMapKey = importerConfigName;
            currentKeyIsCustom = true;

            SaveData();

            Logger?.LogInfo($"[LINK] Saved current set as '{currentMapKey}' (from '{prevKey}')");
        }

        private string SanitizeFileName(string fileName)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
            {
                fileName = fileName.Replace(c, '_');
            }
            return fileName;
        }

        private void OnGUI()
        {
            if (!showUI || !isInHScene) return;

            // GUIStyle は毎フレーム生成しない（v0.8.2.0）。
            // GUI.skin は OnGUI の外では有効でないため、初回の OnGUI で生成して以降は使い回す。
            // 幅 160px に収めるため、タイトルのフォントは 9 に落としている（v0.12.1.0）。
            // パネル内の文字はすべて fontSize 12 で揃えてある（スロット行 v0.13.0.3、最下段 v0.13.0.4）。
            if (cachedWindowStyle == null)
                cachedWindowStyle = new GUIStyle(GUI.skin.window) { fontSize = 9 };
            if (cachedButtonStyle == null)
                // 【重要】行内の縦位置は fontSize だけでは揃わない。margin も揃える。
                // GUILayout の横並びでは、各要素が自分の margin.top ぶん下へずれて
                // 配置される。既定のボタン margin は 4 で、ラベル(0) や矢印(2) と
                // 食い違っていたため、文字の基準線が最大 4px ずれていた（v0.14.2.0）。
                cachedButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 12,
                    margin = new RectOffset(4, 4, 2, 2)
                };
            // 最下段（CAM Copy / Make Pair）のボタン。fontSize はスロット行と同じ 12。
            // こちらも行内で同一サイズを保つこと。
            if (cachedSmallButtonStyle == null)
                cachedSmallButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 12,
                    margin = new RectOffset(4, 4, 3, 3),
                    padding = new RectOffset(2, 2, 3, 3)
                };

            // 揺動のオンオフはトグル（ラジオボタン式）。状態が見た目に出るので、
            // 別途 ON/OFF ラベルを置く必要がなくなり、その分の幅を節約できる。
            //
            // GUI.skin.toggle から派生させ、fontSize / margin / padding / 縦の揃えを変える。
            // Unity 2018.4.11f1 では GUIStyleState に背景テクスチャを割り当てない限り
            // 配色指定が無視されるため（reference_documents の RadioStyle ガイド）、
            // 背景は組み込みのものをそのまま継承する。
            if (cachedToggleStyle == null)
            {
                cachedToggleStyle = new GUIStyle(GUI.skin.toggle)
                {
                    fontSize = 12,
                    margin = new RectOffset(0, 0, 3, 3),
                    // 左余白はチェックボックスの描画領域。字を大きくした分だけ広げる。
                    padding = new RectOffset(16, 0, 0, 0)
                };

                // 【重要】縦を中央揃えにする（v0.14.3.2）。
                // 組み込みの toggle は上揃えで、しかも上の padding を 0 に上書きして
                // いるので、文字が箱の上端に貼り付き、隣のボタン（中央揃え）より
                // 上に浮いていた。最下段2行のベースラインが崩れていた原因の半分で、
                // もう半分は高さ（v0.14.3.0。SLOT_ROW_H の注記を参照）。2026-09-27 実機で解消を確認。
                // 組み込み toggle の値（UpperLeft、padding 15/0/3/0）は、ゲームの
                // unity default resources を解析して実測済み。
                // スロット行のラベルは v0.13.0.2 でボタンから派生させて中央揃えを
                // 受け継いでいたが、トグルはその手当てを受けていなかった。
                //
                // チェックボックスは style の背景として描かれるので、揃えを変えても
                // 動かない。動くのは文字だけ。
                TrySetAlignment(cachedToggleStyle, "MiddleLeft", "toggle");

                // 組み込みスキンの値は DLL からは読めない。揃えが崩れたときに
                // 推測ではなく数値で判断できるよう、実機のログへ出す。
                // スタイルはプロセス内で1回しか作らないので、ログが出るのは
                // **最初にパネルを描く時点で** Show Debug Info が ON のときだけ。
                // 途中で ON にしても出ない（ゲームの再起動が要る）。
                if (ShowDebugInfo?.Value == true)
                {
                    LogStyleMetrics("builtin toggle", GUI.skin.toggle);
                    LogStyleMetrics("builtin button", GUI.skin.button);
                    LogStyleMetrics("CD toggle", cachedToggleStyle);
                    LogStyleMetrics("CD small button", cachedSmallButtonStyle);
                }
            }

            // スロット行のラベル（CAM / POS / BAS とスロット番号）。
            // 番号は固定幅にする。プロポーショナルフォントだと文字幅で
            // 右隣のボタン位置が動いてしまうため（v0.13.0.0）。
            //
            // ベースラインを隣のボタンと揃えるため、ラベルだが GUI.skin.label ではなく
            // **GUI.skin.button から派生**させている（v0.13.0.2）。
            //
            // 理由: 縦中央揃えは TextAnchor で指定するのが正攻法だが、この列挙型は
            // UnityEngine.TextRenderingModule 側にあり、そのためだけに参照を増やしたくない。
            // ボタンのスタイルは既定で中央揃えを持っているので、そこから複製すれば
            // 列挙型に触れずに同じ配置を受け継げる。padding で位置を作り込むより確実で、
            // 隣り合うボタンと文字の基準線が原理的に一致する。
            //
            // 背景は normal を null にして消す。ラベルは normal 状態しか描かないため
            // これでボタンの枠が出なくなる。
            if (cachedSlotLabelStyle == null)
                cachedSlotLabelStyle = new GUIStyle(GUI.skin.button)
                {
                    // 同じ行の他の要素と必ず同じ値にすること（下の注記を参照）。
                    fontSize = 12,
                    // 上下は同じ行の矢印・SAVE と揃える。左右は要素間の間隔なので別。
                    margin = new RectOffset(0, 0, 2, 2),
                    padding = new RectOffset(0, 0, 0, 0),
                    normal = { background = null }
                };

            // 【重要】スロット行の要素は全て同じ fontSize にすること。
            // 文字サイズが違うと文字の基準線も変わり、行の高さを揃えても
            // 上下位置が合わない。v0.13.0.0〜0.13.0.2 で padding や
            // スタイル派生で合わせようとしたが、いずれも近似にしかならなかった。
            // ラベル・矢印・SAVE を同一サイズにするのが確実で単純な解。
            if (cachedArrowStyle == null)
                cachedArrowStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 12,
                    margin = new RectOffset(1, 1, 2, 2),
                    padding = new RectOffset(0, 0, 2, 2)
                };
            // Map 名は長さが不定。スロット行のラベルと見た目を揃えたうえで、
            // 溢れた分はそのまま切る（v0.14.3.0）。省略記号は付けない。付けるなら
            // 全角混在で幅計算を繰り返すことになり、割に合わない。
            //
            // wordWrap を false にしているのは描画のため。折り返すと 20px の箱に
            // 2行が入り、上下が半端に切れる。なお高さの実測には関係しない。
            // この行には GUILayout.Height が付いており、固定高は wordWrap による
            // 高さ計算を上書きするため（v0.14.3.1 で訂正。v0.14.3.0 のコメントは
            // 「折り返すと下端の実測値が動く」としていたが誤りだった）。
            if (cachedMapNameStyle == null)
            {
                cachedMapNameStyle = new GUIStyle(cachedSlotLabelStyle)
                {
                    clipping = TextClipping.Clip,
                    wordWrap = false
                };

                // 左揃えにする（縦は中央のまま）。派生元はボタンなので中央揃えで、
                // 溢れると末尾ではなく先頭から削れてしまう。マップ名は頭に識別情報が
                // 寄るので、それでは困る（v0.14.3.1）。
                //
                // 縦は Middle のままなので、行内の縦位置（SLOT_ROW_H の注記）は崩れない。
                // 型名を書かずに設定する理由は TrySetAlignment() の注記を参照。
                TrySetAlignment(cachedMapNameStyle, "MiddleLeft", "map name");

                // 160px に全要素が収まるかは、ウィンドウスタイルの余白の実値で決まる。
                // 組み込みスキンの値は DLL からは読めないので、実機のログで確かめる。
                // 実測 L10 R10 T20 B10、内側の幅 140px（v0.14.3.1）。出る条件は
                // LogStyleMetrics と同じで、最初にパネルを描く時点で ON のときだけ。
                if (ShowDebugInfo?.Value == true)
                {
                    RectOffset wp = cachedWindowStyle.padding;
                    Logger?.LogInfo($"[UI] window padding L{wp.left} R{wp.right} T{wp.top} B{wp.bottom}" +
                                    $" / inner width {UI_WIDTH - wp.horizontal}");
                }
            }

            if (cachedWindowTitle == null)
                cachedWindowTitle = $"Composition Director v{VersionInfo.PluginVersion}";

            // GUI.skin は全プラグインとゲーム本体が共有するオブジェクト。差し替えたまま
            // 例外で抜けると他の UI まで崩れるので、戻しは finally で保証する（v0.14.3.1）。
            var originalWindowStyle = GUI.skin.window;
            GUI.skin.window = cachedWindowStyle;
            try
            {
                // GUI.Window へ戻した（v0.14.3.0）。
                //
                // GUILayout.Window は中身に合わせて大きさを決めるため、要素の最小幅が
                // 要求幅を上回ると窓ごと広がる。GUILayout.Width() は「要求」であって
                // 「上限」ではない。幅 160px 固定が要件である以上、道具の選択が誤りだった。
                // GUI.Window は渡した矩形をそのまま使うので、幅は構造的に保証される。
                //
                // 高さは DoWindow が実測した値を使う。数値で詰めると行を足すたびに
                // 調整し直しになるが、実測ならその必要が無い。反映は1フレーム遅れるが、
                // 行数が変わるのは表示条件が切り替わる瞬間だけなので見た目には出ない。
                if (measuredContentHeight > 0f)
                    windowRect.height = measuredContentHeight;

                windowRect = GUI.Window(498959632, windowRect, DoWindow, cachedWindowTitle);
            }
            finally
            {
                GUI.skin.window = originalWindowStyle;
            }
        }

        // GUI.skin.button の差し替えと GUI.enabled の変更を、例外が出ても必ず戻す
        // ための外枠（v0.14.3.1）。どちらもプロセス全体で共有される状態で、
        // 戻し損ねるとゲーム本体と他プラグインの UI まで巻き込む。
        private void DoWindow(int windowID)
        {
            var originalButtonStyle = GUI.skin.button;
            bool originalEnabled = GUI.enabled;
            GUI.skin.button = cachedButtonStyle;
            try
            {
                DoWindowContents();
            }
            finally
            {
                GUI.skin.button = originalButtonStyle;
                GUI.enabled = originalEnabled;
            }
        }

        // GUIStyle.alignment を、型名 TextAnchor を書かずに設定する（v0.14.3.1 で導入、
        // v0.14.3.2 でヘルパーに切り出し）。
        //
        // TextAnchor は UnityEngine.TextRenderingModule 側の型で、lib/ には無い。
        // そのためだけに参照を増やさず、実行時に解決する。ゲーム本体はこのモジュールを
        // 読み込んでいるので必ず解決できる（v0.14.3.1 の Map 名で実機確認済み）。
        // 値は数値ではなく名前で引く。列挙値の並びに依存しないため。
        // 失敗しても例外は外へ出さず、元の揃えのまま残す。
        private static bool TrySetAlignment(GUIStyle style, string anchorName, string what)
        {
            try
            {
                PropertyInfo p = typeof(GUIStyle).GetProperty("alignment");
                p.SetValue(style, Enum.Parse(p.PropertyType, anchorName), null);
                return true;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[UI] {what}: set alignment {anchorName} failed: {ex.Message}");
                return false;
            }
        }

        // 縦位置を決めるスタイルの値をログへ出す（デバッグ用、v0.14.3.2）。
        // alignment は型名を書けないので TrySetAlignment() と同じく実行時に読む。
        private static void LogStyleMetrics(string what, GUIStyle s)
        {
            string align;
            try { align = typeof(GUIStyle).GetProperty("alignment").GetValue(s, null).ToString(); }
            catch (Exception ex) { align = "?(" + ex.GetType().Name + ")"; }

            RectOffset p = s.padding, m = s.margin, o = s.overflow;
            Logger?.LogInfo($"[UI] {what}: align {align} font {s.fontSize}" +
                            $" padding L{p.left} R{p.right} T{p.top} B{p.bottom}" +
                            $" margin T{m.top} B{m.bottom}" +
                            $" overflow T{o.top} B{o.bottom}" +
                            $" contentOffset {s.contentOffset} fixedHeight {s.fixedHeight}");
        }

        private void DoWindowContents()
        {
            GUILayout.BeginVertical();

            // 縦に伸びる要素を入れてはいけない。入れると中身が窓いっぱいに引き伸ばされ、
            // 下端の実測値が窓の高さそのものになって縮まなくなる（末尾の注記を参照）。
            // この2つはスタイルも高さも指定していない素のラベルで、GUI.skin.label の
            // stretchHeight は組み込みスキン側の値のため DLL からは読めない。
            // 明示で止めておく（v0.14.3.1）。
            if (!EnablePlugin.Value)
            {
                GUILayout.Label("Plugin Functions Disabled", GUILayout.ExpandHeight(false));
                GUILayout.Space(4);
            }

            if (EnablePlugin.Value && !cameraFunctionEnabled)
            {
                GUILayout.Label("Camera Functions Unavailable", GUILayout.ExpandHeight(false));
                GUILayout.Space(4);
            }

            GUI.enabled = EnablePlugin.Value;

            // スロット行は  ラベル [<] 番号 [>] [SAVE]  の形（v0.13.0.0）。
            // 番号を固定幅ラベルにすることで、桁の見た目幅に関わらず
            // 右側のボタン位置が動かない。
            GUILayout.BeginHorizontal();
            GUI.enabled = EnablePlugin.Value && cameraFunctionEnabled;
            GUILayout.Label("CAM", cachedSlotLabelStyle, GUILayout.Width(28), GUILayout.Height(SLOT_ROW_H));
            if (GUILayout.Button(SLOT_PREV_GLYPH, cachedArrowStyle, GUILayout.Width(20), GUILayout.Height(SLOT_ROW_H)))
            {
                currentCameraSlot = PrevSlot(currentCameraSlot, MaxCameraSlots.Value);
                StartCoroutine(ExecuteCamDataApplyBlock());
            }
            GUILayout.Label($"{currentCameraSlot + 1}", cachedSlotLabelStyle, GUILayout.Width(10), GUILayout.Height(SLOT_ROW_H));
            if (GUILayout.Button(SLOT_NEXT_GLYPH, cachedArrowStyle, GUILayout.Width(20), GUILayout.Height(SLOT_ROW_H)))
            {
                currentCameraSlot = NextSlot(currentCameraSlot, MaxCameraSlots.Value);
                StartCoroutine(ExecuteCamDataApplyBlock());
            }
            if (GUILayout.Button("SAVE", GUILayout.Height(SLOT_ROW_H)))
                SaveCurrentCamera();
            GUI.enabled = EnablePlugin.Value;
            GUILayout.EndHorizontal();

            GUILayout.Space(3);

            GUILayout.BeginHorizontal();
            GUILayout.Label("POS", cachedSlotLabelStyle, GUILayout.Width(28), GUILayout.Height(SLOT_ROW_H));
            if (GUILayout.Button(SLOT_PREV_GLYPH, cachedArrowStyle, GUILayout.Width(20), GUILayout.Height(SLOT_ROW_H)))
            {
                currentPositionSlot = PrevSlot(currentPositionSlot, MaxPositionSlots.Value);
                StartCoroutine(ExecutePosDataApplyBlock());
            }
            GUILayout.Label($"{currentPositionSlot + 1}", cachedSlotLabelStyle, GUILayout.Width(10), GUILayout.Height(SLOT_ROW_H));
            if (GUILayout.Button(SLOT_NEXT_GLYPH, cachedArrowStyle, GUILayout.Width(20), GUILayout.Height(SLOT_ROW_H)))
            {
                currentPositionSlot = NextSlot(currentPositionSlot, MaxPositionSlots.Value);
                StartCoroutine(ExecutePosDataApplyBlock());
            }
            if (GUILayout.Button("SAVE", GUILayout.Height(SLOT_ROW_H)))
                SaveCurrentPosition();
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            GUILayout.BeginHorizontal();
            GUILayout.Label("BAS", cachedSlotLabelStyle, GUILayout.Width(28), GUILayout.Height(SLOT_ROW_H));
            if (GUILayout.Button(SLOT_PREV_GLYPH, cachedArrowStyle, GUILayout.Width(20), GUILayout.Height(SLOT_ROW_H)))
            {
                currentBasisPositionSlot = PrevSlot(currentBasisPositionSlot, MaxBasisPositionSlots.Value);
                StartCoroutine(ExecuteBasisPosDataApplyBlock());
            }
            GUILayout.Label($"{currentBasisPositionSlot + 1}", cachedSlotLabelStyle, GUILayout.Width(10), GUILayout.Height(SLOT_ROW_H));
            if (GUILayout.Button(SLOT_NEXT_GLYPH, cachedArrowStyle, GUILayout.Width(20), GUILayout.Height(SLOT_ROW_H)))
            {
                currentBasisPositionSlot = NextSlot(currentBasisPositionSlot, MaxBasisPositionSlots.Value);
                StartCoroutine(ExecuteBasisPosDataApplyBlock());
            }
            if (GUILayout.Button("SAVE", GUILayout.Height(SLOT_ROW_H)))
                SaveCurrentBasisPosition();
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            // 最下段: Camera設定の一括コピー と カメラ揺動のオンオフ。
            // 揺動はトグルなので、状態表示のための別ラベルは要らない。
            //
            // 高さの明示は必須（v0.14.3.0）。指定が無いとボタン（約 20px）とトグル
            // （約 14px）がそれぞれの自然高さを取り、箱の中心が 3px 食い違う。
            // 高さだけでは足りず、トグルの縦の揃えも Middle にして初めて揃った
            // （v0.14.3.2。SLOT_ROW_H の注記を参照）。**この Height を消さないこと。**
            GUILayout.BeginHorizontal();

            bool canPaste = HasCamClipboard;
            if (GUILayout.Button(canPaste ? "CAM Paste" : "CAM Copy",
                                 cachedSmallButtonStyle,
                                 GUILayout.Width(64), GUILayout.Height(SLOT_ROW_H)))
            {
                if (canPaste) PasteCameraSet();
                else CopyCameraSet();
            }

            // CAM Copy と近すぎたので間隔を入れる（v0.13.0.2）
            GUILayout.Space(6);

            bool swayOn = GUILayout.Toggle(EnableCameraSway.Value, "Shake",
                                           cachedToggleStyle,
                                           GUILayout.Width(66), GUILayout.Height(SLOT_ROW_H));
            if (swayOn != EnableCameraSway.Value)
            {
                EnableCameraSway.Value = swayOn;
                Logger?.LogInfo($"[SWAY] {(swayOn ? "ON" : "OFF")}");
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            // === HS2 Custom Map Director (CMD) 連携（v0.14.0.0） ===
            // いま何を鍵にしているかを常に見せる。対応が成立しているかが一目で分かり、
            // 「対になるファイルが無い」ことの専用警告が要らなくなる。
            GUILayout.BeginHorizontal();

            // Map 名は入りきらないことがある。その時は "Map:" を省いて、その分の
            // 30px を名前に回す（v0.14.3.0）。窓の内側は 140px（実測）なので、
            // 2割強にあたる。見切れるのは許容するが、稼げる幅は稼ぐ。
            //
            // 名前の幅は CalcSize で測る。フォントが確定している OnGUI の中で
            // しか正しい値を返さないのでここで呼ぶ。ただし毎フレーム測る必要は無く、
            // 前回と同じ文字列なら前回の判定をそのまま使う。
            //
            // 【重要】測り直しは Layout パスでだけ行う（v0.14.3.1）。
            // mapShowPrefix は "Map:" ラベルを出すかどうか、つまりこの行の要素数を
            // 決める。GUILayout は Layout パスで記録した要素数を後続のパス（入力・
            // Repaint）でも同じだけ読み出すので、途中で数が変わると例外になる。
            // 名前は Link トグルや Make Pair のクリックでこの行を描いた後に変わりうる。
            // 各イベントの前には Layout パスが挟まるので実際には揃うはずだが、
            // その順序は Unity のネイティブ側の挙動で裏が取れない。条件1つで
            // 構造的に塞いでおく。
            float mapRowW = UI_WIDTH - cachedWindowStyle.padding.horizontal - 2;
            string mapText = string.IsNullOrEmpty(currentMapKey) ? "-" : currentMapKey;
            if (Event.current.type == EventType.Layout && mapText != mapNameMeasured)
            {
                mapNameMeasured = mapText;
                mapNameContent.text = mapText;
                mapShowPrefix = cachedMapNameStyle.CalcSize(mapNameContent).x
                                <= mapRowW - MAP_LABEL_W;
            }

            if (mapShowPrefix)
                GUILayout.Label("Map:", cachedSlotLabelStyle,
                                GUILayout.Width(MAP_LABEL_W), GUILayout.Height(SLOT_ROW_H));
            GUILayout.Label(mapNameContent, cachedMapNameStyle,
                            GUILayout.Width(mapShowPrefix ? mapRowW - MAP_LABEL_W : mapRowW),
                            GUILayout.Height(SLOT_ROW_H));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            bool hasName = !string.IsNullOrEmpty(importerConfigName);
            bool pairExists = hasName && File.Exists(GetDataFilePath(importerConfigName, true));

            GUI.enabled = EnablePlugin.Value && hasName;
            if (GUILayout.Button(pairExists ? "Save Pair" : "Make Pair",
                                 cachedSmallButtonStyle,
                                 GUILayout.Width(64), GUILayout.Height(SLOT_ROW_H)))
            {
                SaveAsImporterSet();
            }
            GUI.enabled = EnablePlugin.Value;

            GUILayout.Space(6);

            bool follow = GUILayout.Toggle(FollowImporterConfig.Value, "Link",
                                           cachedToggleStyle,
                                           GUILayout.Width(66), GUILayout.Height(SLOT_ROW_H));
            if (follow != FollowImporterConfig.Value)
            {
                FollowImporterConfig.Value = follow;
                Logger?.LogInfo($"[LINK] follow {(follow ? "ON" : "OFF")}");
                ApplyEffectiveKey("toggle");
            }

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            // 下端をコンテンツの実寸へ裁ち落とす（v0.14.3.0）。
            // 直前に閉じた縦グループの矩形が中身そのものなので、その下端に
            // ウィンドウスタイルの下余白を足したものが必要な高さになる。
            // GetLastRect() は Layout パスでは無効値を返すため Repaint でのみ測る。
            //
            // 測った高さを次フレームの窓の高さにしているが、1フレームで収束する。
            // 条件は「縦に伸びる要素が1つも無いこと」（stretchHeight が全て 0）。
            // これが成り立つ限り、中身の高さは窓の高さに依存しない。
            // 伸びる要素（GUILayout.ExpandHeight(true)、FlexibleSpace 等）を入れると、
            // 中身が窓いっぱいに引き伸ばされて測った高さ＝窓の高さになり、
            // **縮まなくなる**（初回の UI_HEIGHT のまま固着する）。発振ではない。
            // v0.14.3.0 のコメントは条件を「全ての行が固定高」と書いていたが、
            // 正しくはこちら（v0.14.3.1 で訂正）。
            if (Event.current.type == EventType.Repaint)
            {
                Rect content = GUILayoutUtility.GetLastRect();

                // GetLastRect() は失敗すると 1px のダミー矩形を返す。それを採ると
                // パネルが数 px に潰れて中身が消える。中身は必ず1行以上あるので、
                // 1行分に満たない値は捨てる（v0.14.3.1）。
                if (content.height >= SLOT_ROW_H)
                {
                    float h = content.yMax + cachedWindowStyle.padding.bottom;
                    if (ShowDebugInfo?.Value == true && Mathf.Abs(h - measuredContentHeight) > 0.5f)
                        Logger?.LogInfo($"[UI] panel height {measuredContentHeight:0.#} -> {h:0.#}" +
                                        $" (content yMax {content.yMax:0.#})");
                    measuredContentHeight = h;
                }
            }

            if (Event.current.type == EventType.MouseUp)
            {
                UIPosX.Value = windowRect.x;
                UIPosY.Value = windowRect.y;
            }

            GUI.DragWindow();
        }

        private static string GenerateDetailedPositionId(HScene.AnimationListInfo info)
        {
            if (info == null) return "unknown";

            try
            {
                string categoryName = GetCategoryName(info);
                string sanitizedName = SanitizePositionName(info.nameAnimation);
                string maleFile = Path.GetFileNameWithoutExtension(info.fileMale ?? "");
                string femaleFile = Path.GetFileNameWithoutExtension(info.fileFemale ?? "");

                return $"{categoryName}_{info.id:00}_{sanitizedName}_{maleFile}_{femaleFile}";
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo?.Value == true)
                    Logger?.LogWarning($"DetailedID generation failed: {ex.Message}");
                return $"{info.id}_error";
            }
        }

        [HarmonyPatch(typeof(HScene), "ChangeAnimation")]
        [HarmonyPostfix]
        public static void OnAnimationChange(HScene __instance, HScene.AnimationListInfo _info, bool _isForceResetCamera, bool _isForceLoopAction, bool _UseFade)
        {
            if (ShowDebugInfo?.Value == true)
            {
                Logger?.LogInfo($"=== ANIMATION CHANGE DETECTION v{VersionInfo.PluginVersion} ===");

                if (_info != null)
                {
                    Logger?.LogInfo($"[ANIMATION_INFO] nameAnimation: {_info.nameAnimation ?? "null"}");
                    Logger?.LogInfo($"[ANIMATION_INFO] id (中分類): {_info.id}");
                    Logger?.LogInfo($"[ANIMATION_INFO] fileMale: {_info.fileMale ?? "null"}");
                    Logger?.LogInfo($"[ANIMATION_INFO] fileFemale: {_info.fileFemale ?? "null"}");

                    string detailedId = GenerateDetailedPositionId(_info);
                    Logger?.LogInfo($"[DETAILED_ID] Generated: {detailedId}");
                }
                else
                {
                    Logger?.LogWarning("[ANIMATION_INFO] AnimationListInfo is null");
                }
            }
        }

        [HarmonyPatch(typeof(HScene), "OnDestroy")]
        [HarmonyPrefix]
        public static void OnHSceneDestroy()
        {
            if (ShowDebugInfo?.Value == true)
                Logger?.LogInfo("HScene destroyed");
        }

        // SetBase() の呼び出しタイミングを記録する（仕様書 v2.1 §8-2）。
        // transBase が femaleChar1 へ同期される瞬間を特定するための診断。
        // Postfix でログを出すだけで、ゲームの挙動には介入しない。
        // CameraUpdate() の直後にカメラ transform を微小移動させる。
        // ここより前に書いても CameraUpdate() に上書きされるため、Postfix である必要がある。
        // CamDat には触らないので、保存される構図データは汚れない。
        [HarmonyPatch]
        public class CameraSwayPatch
        {
            static bool Prepare()
            {
                return AccessTools.TypeByName("BaseCameraControl_Ver2") != null;
            }

            static MethodBase TargetMethod()
            {
                var targetType = AccessTools.TypeByName("BaseCameraControl_Ver2");
                if (targetType == null) return null;

                var method = AccessTools.Method(targetType, "CameraUpdate");
                if (method != null)
                    Logger?.LogInfo("[HARMONY] Patching BaseCameraControl_Ver2.CameraUpdate for sway");
                return method;
            }

            [HarmonyPostfix]
            static void Postfix(MonoBehaviour __instance)
            {
                instance?.ApplySway(__instance);
            }
        }

        [HarmonyPatch]
        public class SetBaseTracePatch
        {
            static bool Prepare()
            {
                return AccessTools.TypeByName("BaseCameraControl_Ver2") != null;
            }

            static MethodBase TargetMethod()
            {
                var targetType = AccessTools.TypeByName("BaseCameraControl_Ver2");
                if (targetType == null) return null;

                var method = AccessTools.Method(targetType, "SetBase");
                if (method != null)
                    Logger?.LogInfo("[HARMONY] Tracing BaseCameraControl_Ver2.SetBase");
                return method;
            }

            [HarmonyPostfix]
            static void Postfix(Transform _trans, bool __result)
            {
                if (CamDatDiagnostics?.Value != true) return;

                string arg = (_trans != null)
                    ? $"{_trans.name} pos={_trans.position} rot={_trans.rotation.eulerAngles}"
                    : "null";
                Logger?.LogInfo($"[SETBASE] called: {arg} -> result={__result}");
            }
        }

        [HarmonyPatch]
        public class BaseCameraControlPatch
        {
            static bool Prepare()
            {
                var targetType = AccessTools.TypeByName("BaseCameraControl_Ver2");
                if (targetType != null)
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"[HARMONY] BaseCameraControl_Ver2 patch target found");
                    return true;
                }

                targetType = AccessTools.TypeByName("CameraControl_Ver2");
                if (targetType != null)
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo($"[HARMONY] CameraControl_Ver2 patch target found");
                    return true;
                }

                Logger?.LogWarning($"[HARMONY] No patch target found");
                return false;
            }

            static MethodBase TargetMethod()
            {
                var targetType = AccessTools.TypeByName("BaseCameraControl_Ver2");
                if (targetType != null)
                {
                    var method = AccessTools.Method(targetType, "LateUpdate");
                    if (method != null)
                    {
                        Logger?.LogInfo($"[HARMONY] Patching BaseCameraControl_Ver2.LateUpdate");
                        return method;
                    }
                }

                targetType = AccessTools.TypeByName("CameraControl_Ver2");
                if (targetType != null)
                {
                    var method = AccessTools.Method(targetType, "LateUpdate");
                    if (method != null)
                    {
                        Logger?.LogInfo($"[HARMONY] Patching CameraControl_Ver2.LateUpdate");
                        return method;
                    }
                }

                Logger?.LogWarning($"[HARMONY] No LateUpdate method found");
                return null;
            }

            [HarmonyPrefix]
            static bool LateUpdatePrefix()
            {
                if (isRestoringCamera)
                {
                    if (ShowDebugInfo?.Value == true)
                        Logger?.LogInfo("[HARMONY] LateUpdate blocked for CamDat operation");
                    return false;
                }
                return true;
            }
        }
    }
}