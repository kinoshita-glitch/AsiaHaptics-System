using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[RequireComponent(typeof(AudioSource))]
public class SoundManager_DualBracelet : MonoBehaviour
{
    private enum ProcessType { RAW = 0, ISM = 1, BPF = 2 } //信号処理の種類（RAW/ISM/BPF）
    private enum SelectRow { Process = 0, SE = 1, Audio = 2, Position = 3 } //選択項目の行
    private enum DemoState { Selecting, Playing } //現在の状態（選択中か，再生中か）

    private sealed class SelectionData //現在の選択状態（どのボタンが選ばれているか）をまとめて保存しておくための箱
    {
        public ProcessType process = ProcessType.RAW; //現在の信号処理．初期値はRAW
        public int se = 0;
        public int audio = 0; //音声提示のON/OFF．0 = ON，1 = OFF
        public int position = -1; //開始時はPositionだけ未選択
    }

    [Header("Headphone Audio")]
    public AudioSource audioSource; //音声を再生するコンポーネント
    public AudioClip[] headphoneClipsRaw = new AudioClip[8];

    [Header("UI: Process (RAW / ISM / BPF)")] public Button[] processButtons = new Button[3];
    [Header("UI: SE (A / B / C / D / E / F / G / H)")] public Button[] seButtons = new Button[8];
    [Header("UI: Audio (ON / OFF)")] public Button[] audioButtons = new Button[2];
    [Header("UI: Position (1..7)")] public Button[] positionButtons = new Button[7];
    [Header("UI: Text")] public Text statusText;
    public GameObject[] oldActiveImages;

    // ======= 変更箇所：動的生成用の変数を削除し、Active切り替え用の配列を追加 =======
    [Header("UI: Active Images (Selection Highlights)")]
    [Tooltip("Processの選択時に表示するハイライト枠 (3個)")]
    public GameObject[] processActiveImages = new GameObject[3];
    [Tooltip("SEの選択時に表示するハイライト枠 (8個)")]
    public GameObject[] seActiveImages = new GameObject[8];
    [Tooltip("Audioの選択時に表示するハイライト枠 (2個)")]
    public GameObject[] audioActiveImages = new GameObject[2];
    [Tooltip("Positionの選択時に表示するハイライト枠 (7個)")]
    public GameObject[] positionActiveImages = new GameObject[7];
    // =================================================================================

    [Header("Bracelet Gamepad Assignment")] //左右のデバイスが Gamepad.all のリストの何番目にあるかを指定
    public int leftGamepadIndex = 0;
    public int rightGamepadIndex = 1;

    [Header("Timing")]
    public float commandHoldSeconds = 0.06f; //BLEの振動命令を送った後，0.06秒待ってから，SetMotorSpeeds(0, 0)を送る．次の命令を受け付けられる状態へ戻すための信号
    public float defaultStimulusDurationSeconds = 1.5f; //AudioClipの長さが分からない場合に，Unity側でPlaying状態を何秒続けるか

    private readonly SelectionData selection = new SelectionData();
    private Gamepad leftGamepad;
    private Gamepad rightGamepad;
    private DemoState state = DemoState.Selecting; //開始時は条件選択状態

    private float stimulusEndsAt; //刺激終了予定時刻を保存
    private Coroutine zeroCommandCoroutine;
    private Coroutine positionUpdateCoroutine;

    private int playingWaveformId = -1;
    private float playingWaveformCommand = 0f;

    private void Awake() //UnityがGameObjectを初期化するとき，最初期に1回呼ばれる
    {
        Application.runInBackground = true; //Unityウィンドウがフォーカスを失っても処理を継続させる
        if (!audioSource) audioSource = GetComponent<AudioSource>();
        audioSource.spatialBlend = 0f; //3D音響ではなく完全な2D音響にする．0 = 2D，1 = 3D
        audioSource.playOnAwake = false; //シーン開始時に勝手に音を再生しない設定
        if (oldActiveImages != null) foreach (GameObject obj in oldActiveImages) if (obj) obj.SetActive(false);

        WireUI(); //ボタンを押したときの処理を登録
        RefreshGamepads(); //接続中Gamepadを取得
        SendZeroHaptics(); //起動時に念のため振動を0にする
        UpdateAllUI(); //現在状態をUIへ反映
    }

    private void OnEnable() { InputSystem.onDeviceChange += OnDeviceChange; } //スクリプトが有効になったとき，Gamepad接続，Gamepad切断などのデバイス変化イベントを監視
    private void OnDisable()
    {
        InputSystem.onDeviceChange -= OnDeviceChange; //無効化時にイベント登録を解除
        SendZeroHaptics(); //終了時に振動を停止
        if (audioSource) audioSource.Stop(); //音声も停止
    }

    private void Update() //Unityで毎フレーム1回実行
    {
        if (state == DemoState.Playing && Time.unscaledTime >= stimulusEndsAt) //現在が再生中で、現在時刻が終了予定時刻を超えたか確認
        {
            FinishTrial(); //刺激を終了
        }
    }

    private void WireUI() //UIボタンへの処理登録
    {
        for (int i = 0; i < processButtons.Length; i++)
        {
            int idx = i;
            if (processButtons[i]) //そのボタンが実際に設定されている場合だけ処理
                processButtons[i].onClick.AddListener(() => { selection.process = (ProcessType)idx; UpdateAllUI(); }); //() =>はラムダ式．クリック時の処理を記述する無名関数
        }
        for (int i = 0; i < seButtons.Length; i++) { int idx = i; if (seButtons[i]) seButtons[i].onClick.AddListener(() => { selection.se = idx; UpdateAllUI(); }); }
        for (int i = 0; i < audioButtons.Length; i++) { int idx = i; if (audioButtons[i]) audioButtons[i].onClick.AddListener(() => { selection.audio = idx; UpdateAllUI(); }); }

        for (int i = 0; i < positionButtons.Length; i++)
        {
            int idx = i;
            if (positionButtons[i])
                positionButtons[i].onClick.AddListener(() =>
                {
                    selection.position = idx;
                    UpdateAllUI();

                    if (state == DemoState.Selecting)
                    {
                        StartCoroutine(ExecuteStimulusRoutine());
                    }
                    else
                    {
                        if (positionUpdateCoroutine != null)
                            StopCoroutine(positionUpdateCoroutine);

                        positionUpdateCoroutine = StartCoroutine(UpdatePositionWhilePlayingRoutine());
                    }
                });
        }
    }

    private void OnDeviceChange(InputDevice device, InputDeviceChange change) //InputDeviceはキーボード・マウス・Gamepadなど全入力機器の基底クラス
    {
        if (device is Gamepad) RefreshGamepads(); //変更された機器がGamepadなら再取得
    }

    [ContextMenu("Refresh Bracelet Gamepads")] //Inspectorのコンポーネントメニューから，Refresh Bracelet Gamepadsを手動実行できるようにする
    public void RefreshGamepads()
    {
        leftGamepad = GetGamepadByIndex(leftGamepadIndex); //指定番号のGamepadを左用として保存
        rightGamepad = GetGamepadByIndex(rightGamepadIndex);
        UpdateAllUI();
    }

    private Gamepad GetGamepadByIndex(int index) { return (index < 0 || index >= Gamepad.all.Count) ? null : Gamepad.all[index]; } //indexが範囲外→ null，範囲内→ Gamepad.all[index]

    private IEnumerator ExecuteStimulusRoutine() //IEnumeratorを返すため、Coroutineとして動かせる
    {
        state = DemoState.Playing;
        stimulusEndsAt = float.PositiveInfinity; //実際の刺激終了時刻を設定するまで，Update()による誤終了を防ぐ
        InputSystem.ResumeHaptics(); //Input System側で一時停止されている可能性のあるhaptics（振動出力）を再開．これから振動命令を出すので，GamepadへのHaptics出力を有効な状態に戻しておく

        yield return null; //UIイベント（マウス操作）の影響を断ち切るために1フレーム待つ．マウスのUIクリック処理とGamepadへの振動出力を同じフレームで行わず切り離した

        //現在の選択値をローカル変数へコピー
        int seIndex = selection.se;
        int positionIndex = selection.position;
        ProcessType process = selection.process;

        /*
         * 24通りの波形ID
         * RAW A = 0×8+0 = 0
         * RAW B = 0×8+1 = 1
         * RAW C = 0×8+2 = 2
         * RAW D = 0×8+3 = 3
         * RAW E = 0×8+4 = 4
         * RAW F = 0×8+5 = 5
         * RAW G = 0×8+6 = 6
         * RAW H = 0×8+7 = 7
         *・・・
         * ISM A = 1×8+0 = 8
         * ISM H = 1×8+7 = 15
         * BPF A = 2×8+0 = 16
         * BPF H = 2×8+7 = 23
         */
        int waveformId = ((int)process * 8) + seIndex;

        /*
         * waveform IDをBLE送信用コマンドへ変換．0を特殊な値（次の命令を受け付けられる状態へ戻すための信号）として空け，また通信値同士に余裕を持たせるため
         * ID 0 → 4
         * ID 1 → 8
         *・・・
         * ID 23 → 96
         */
        int waveformCommandValue = 4 * (waveformId + 1);

        /*
         * SetMotorSpeeds()は0～1なので、
         * 4  → 0.04
         * 8  → 0.08
         *・・・
         * 96 → 0.96
         * へ変換
         */
        float waveformCommand = waveformCommandValue / 100.0f;

        playingWaveformId = waveformId;
        playingWaveformCommand = waveformCommand;

        float x = Mathf.Clamp01(positionIndex / 6.0f); //Positionから振幅へ変換．MathfはUnityの数学関数群．Clamp01()は値を0～1に制限
        float leftAmplitude = 1.0f - x;
        float rightAmplitude = x;

        Debug.Log($"[Haptics] Play -> ID:{waveformId}, L_Amp:{leftAmplitude:F2}, R_Amp:{rightAmplitude:F2}, Cmd:{waveformCommand:F2}"); //Unity Consoleへ送信内容を表示．$"..."は文字列補間．:F2は小数点以下2桁表示

        //SetMotorSpeeds()は本来ゲームパッド内部の2つの振動モーターの強さを設定するための関数だが，本システムではGamepad振動機能を通信チャンネルとして利用
        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(leftAmplitude, waveformCommand); //左ブレスレットへGamepad rumble命令を送る．第1引数 = 振幅，第2引数 = 波形コマンド
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(rightAmplitude, waveformCommand);

        if (audioSource) //AudioSourceが存在するか確認
        {
            audioSource.Stop(); //前の音が残っていれば停止
            //Audio ONであり，配列が存在し，範囲内で，実際にAudioClipが設定されている，という4条件を確認
            if (selection.audio == 0 && headphoneClipsRaw != null && seIndex >= 0 && seIndex < headphoneClipsRaw.Length && headphoneClipsRaw[seIndex] != null)
            {
                audioSource.clip = headphoneClipsRaw[seIndex]; //選択された音声を設定
                audioSource.Play(); //再生開始
            }
        }

        //現在時刻に刺激時間を加えて終了予定時刻を設定．waveformIdは将来波形ごとに時間を変えるために残っているが，現状はseIndexだけで時間を決定
        stimulusEndsAt = Time.unscaledTime + GetStimulusDurationSeconds(waveformId, seIndex);

        if (zeroCommandCoroutine != null) StopCoroutine(zeroCommandCoroutine); //以前の0送信Coroutineが残っていれば停止
        zeroCommandCoroutine = StartCoroutine(ReturnHapticsToZeroAfterDelay()); //新しい0送信Coroutineを開始

        UpdateAllUI();
    }

    private IEnumerator UpdatePositionWhilePlayingRoutine()
    {
        if (zeroCommandCoroutine != null)
        {
            StopCoroutine(zeroCommandCoroutine);
            zeroCommandCoroutine = null;
        }

        //マイコン側を次の命令を受け付けられる状態へ戻す
        SendZeroHaptics();

        //0,0と新しい振幅命令を別フレームに分ける
        yield return null;

        if (state != DemoState.Playing)
        {
            positionUpdateCoroutine = null;
            yield break;
        }

        float x = Mathf.Clamp01(selection.position / 6.0f);
        float leftAmplitude = 1.0f - x;
        float rightAmplitude = x;

        Debug.Log($"[Haptics] Update Position -> ID:{playingWaveformId}, L_Amp:{leftAmplitude:F2}, R_Amp:{rightAmplitude:F2}, Cmd:{playingWaveformCommand:F2}");

        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(leftAmplitude, playingWaveformCommand);
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(rightAmplitude, playingWaveformCommand);

        zeroCommandCoroutine = StartCoroutine(ReturnHapticsToZeroAfterDelay());

        positionUpdateCoroutine = null;
    }

    private IEnumerator ReturnHapticsToZeroAfterDelay()
    {
        //実時間で0.06秒待つ．WaitForSecondsRealtimeはTime.timeScaleの影響を受けない
        yield return new WaitForSecondsRealtime(commandHoldSeconds);
        SendZeroHaptics(); //両Gamepadへ，0,0を送る
        zeroCommandCoroutine = null; //Coroutine終了済みであることを示す
    }

    private void FinishTrial() //刺激時間終了時の処理
    {
        if (positionUpdateCoroutine != null)
        {
            StopCoroutine(positionUpdateCoroutine);
            positionUpdateCoroutine = null;
        }

        if (zeroCommandCoroutine != null)
        {
            StopCoroutine(zeroCommandCoroutine);
            zeroCommandCoroutine = null;
        }

        SendZeroHaptics(); //安全のためもう一度0を送る
        if (audioSource) audioSource.Stop(); //音声を止める
        state = DemoState.Selecting; //次の条件を選択できる状態へ戻す
        selection.position = -1; //刺激終了後はPositionを未選択状態へ戻す
        playingWaveformId = -1;
        playingWaveformCommand = 0f;
        UpdateAllUI();
    }

    private float GetStimulusDurationSeconds(int waveformId, int seIndex)
    {
        //マイコン側の.bin波形の再生時間に合わせる
        switch (waveformId)
        {
            case 0:
            case 1:
            case 8:
            case 9:
            case 16:
            case 17:
                return 1.300f;

            case 2:
            case 10:
            case 18:
                return 1.403f;

            case 3:
            case 11:
            case 19:
                return 1.472f;

            case 4:
            case 12:
            case 20:
                return 30.000f;

            case 5:
            case 13:
            case 21:
                return 30.000f;

            case 6:
            case 14:
            case 22:
                return 30.000f;

            case 7:
            case 15:
            case 23:
                return 5.000f;
        }

        return Mathf.Max(0.1f, defaultStimulusDurationSeconds);
    }

    private void SendZeroHaptics()
    {
        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(0f, 0f); //振幅と波形commandを両方0に
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(0f, 0f);
    }

    private void UpdateAllUI()
    {
        UpdateHighlights(); //各項目のハイライト枠を更新
        UpdateStatusText(); //文字情報を更新
    }

    // ======= 変更箇所：オブジェクトのアクティブ/非アクティブでハイライトを表示する処理 =======
    private void UpdateHighlights()
    {
        UpdateHighlightArray(processActiveImages, (int)selection.process);
        UpdateHighlightArray(seActiveImages, selection.se);
        UpdateHighlightArray(audioActiveImages, selection.audio);
        UpdateHighlightArray(positionActiveImages, selection.position);
    }

    private void UpdateHighlightArray(GameObject[] images, int activeIdx)
    {
        if (images == null) return;
        for (int i = 0; i < images.Length; i++)
        {
            if (images[i]) images[i].SetActive(i == activeIdx);
        }
    }
    // =========================================================================================

    private void UpdateStatusText()
    {
        if (!statusText) return;
        //stringは文字列型．GamepadがあればConnected，なければNot found
        string lConnected = leftGamepad != null ? "Connected" : "Not found";
        string rConnected = rightGamepad != null ? "Connected" : "Not found";
        char seChar = (char)('A' + selection.se); //charは1文字を表す型．例えばselection.se = 0 → A，selection.se = 1 → Bとなる
        string audioName = selection.audio == 0 ? "ON" : "OFF";
        string positionName = selection.position >= 0 ? $"Pos{selection.position + 1}" : "Pos-";

        //状態を画面表示
        statusText.text = $"State: {state}\n" +
                          $"Selected: {selection.process}, {seChar}, {audioName}, {positionName}\n" +
                          $"Left (Idx {leftGamepadIndex}): {lConnected}\n" +
                          $"Right (Idx {rightGamepadIndex}): {rConnected}";
    }
}