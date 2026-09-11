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
        public int position = 0;
    }

    [Header("Headphone Audio")]
    public AudioSource audioSource; //音声を再生するコンポーネント
    public AudioClip[] headphoneClipsRaw = new AudioClip[4];

    [Header("UI: Process (RAW / ISM / BPF)")] public Button[] processButtons = new Button[3];
    [Header("UI: SE (A / B / C / D)")] public Button[] seButtons = new Button[4];
    [Header("UI: Audio (ON / OFF)")] public Button[] audioButtons = new Button[2];
    [Header("UI: Position (1..7)")] public Button[] positionButtons = new Button[7];
    [Header("UI: Render")] public Button renderButton;
    [Header("UI: Text")] public Text statusText;
    public GameObject[] oldActiveImages;

    [Header("Selected Frame Appearance")]
    public Canvas canvas; //CanvasはUnity UI全体を配置するための親オブジェクト．選択中のボタンを囲む青い枠をこのCanvasの子として生成
    public Color frameColor = new Color(0.0f, 0.65f, 1.0f, 1.0f);
    [Range(0.05f, 1.0f)] public float frameAlpha = 0.5f;
    public float frameLineWidth = 3.0f;
    public float framePadding = 8.0f;

    [Header("Bracelet Gamepad Assignment")] //左右のデバイスが Gamepad.all のリストの何番目にあるかを指定
    public int leftGamepadIndex = 0;
    public int rightGamepadIndex = 1;

    [Header("Timing")]
    public float commandHoldSeconds = 0.06f; //BLEの振動命令を送った後，0.06秒待ってから，SetMotorSpeeds(0, 0)を送る．次の命令を受け付けられる状態へ戻すための信号
    public float defaultStimulusDurationSeconds = 1.5f; //AudioClipの長さが分からない場合に，Unity側でPlaying状態を何秒続けるか

    private readonly SelectionData selection = new SelectionData();
    private Gamepad leftGamepad;
    private Gamepad rightGamepad;
    private readonly RectTransform[] selectedFrames = new RectTransform[4];
    private DemoState state = DemoState.Selecting; //開始時は条件選択状態

    private float stimulusEndsAt; //刺激終了予定時刻を保存
    private Coroutine zeroCommandCoroutine;

    private void Awake() //UnityがGameObjectを初期化するとき，最初期に1回呼ばれる
    {
        Application.runInBackground = true; //Unityウィンドウがフォーカスを失っても処理を継続させる
        if (!audioSource) audioSource = GetComponent<AudioSource>();
        audioSource.spatialBlend = 0f; //3D音響ではなく完全な2D音響にする．0 = 2D，1 = 3D
        audioSource.playOnAwake = false; //シーン開始時に勝手に音を再生しない設定
        if (!canvas) canvas = GetComponentInParent<Canvas>(); //CanvasがInspectorで指定されていなければ，親オブジェクトを遡ってCanvasを探す
        if (oldActiveImages != null) foreach (GameObject obj in oldActiveImages) if (obj) obj.SetActive(false);

        CreateFramesIfNeeded(); //青い選択枠を生成
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
        for (int i = 0; i < positionButtons.Length; i++) { int idx = i; if (positionButtons[i]) positionButtons[i].onClick.AddListener(() => { selection.position = idx; UpdateAllUI(); }); }
        if (renderButton) renderButton.onClick.AddListener(OnClickRender); //Renderボタンを押すと，OnClickRender()が呼ばれる
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

    private Gamepad GetGamepadByIndex(int index){return (index < 0 || index >= Gamepad.all.Count) ? null : Gamepad.all[index]; } //indexが範囲外→ null，範囲内→ Gamepad.all[index]

    public void OnClickRender()
    {
        if (state == DemoState.Playing) return; //すでに刺激再生中なら重複実行しない
        StartCoroutine(ExecuteStimulusRoutine()); //IEnumeratorを返す関数ExecuteStimulusRoutine()をStartCoroutine()に渡すことで，Coroutineとして実行
    }

    private IEnumerator ExecuteStimulusRoutine() //IEnumeratorを返すため、Coroutineとして動かせる
    {
        state = DemoState.Playing;
        InputSystem.ResumeHaptics(); //Input System側で一時停止されている可能性のあるhaptics（振動出力）を再開

        yield return null; //UIイベント（マウス操作）の影響を断ち切るために1フレーム待つ

        //現在の選択値をローカル変数へコピー
        int seIndex = selection.se;
        int positionIndex = selection.position;
        ProcessType process = selection.process;

        /*
         * 12通りの波形ID
         * RAW A = 0×4+0 = 0
         * RAW B = 0×4+1 = 1
         * RAW C = 0×4+2 = 2
         * RAW D = 0×4+3 = 3
         *・・・
         * ISM A = 1×4+0 = 4
         * ISM D = 1×4+3 = 7
         * BPF A = 2×4+0 = 8
         * BPF D = 2×4+3 = 11
         */
        int waveformId = ((int)process * 4) + seIndex;

        /*
         * waveform IDをBLE送信用コマンドへ変換
         * ID 0 → 5
         * ID 1 → 10
         *・・・
         * ID 11 → 60
         */
        int waveformCommandValue = 5 * (waveformId + 1);

        /*
         * SetMotorSpeeds()は0～1なので、
         * 5  → 0.05
         * 10 → 0.10
         *・・・
         * 60 → 0.60
         * へ変換
         */
        float waveformCommand = waveformCommandValue / 100.0f;

        float x = Mathf.Clamp01(positionIndex / 6.0f); //Positionから振幅へ変換．MathfはUnityの数学関数群．Clamp01()は値を0～1に制限
        float leftAmplitude = 1.0f - x;
        float rightAmplitude = x;

        Debug.Log($"[Haptics] Play -> ID:{waveformId}, L_Amp:{leftAmplitude:F2}, R_Amp:{rightAmplitude:F2}, Cmd:{waveformCommand:F2}"); //Unity Consoleへ送信内容を表示．$"..."は文字列補間．:F2は小数点以下2桁表示

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

    private IEnumerator ReturnHapticsToZeroAfterDelay()
    {
        //実時間で0.06秒待つ．WaitForSecondsRealtimeはTime.timeScaleの影響を受けない
        yield return new WaitForSecondsRealtime(commandHoldSeconds);
        SendZeroHaptics(); //両Gamepadへ，0,0を送る
        zeroCommandCoroutine = null; //Coroutine終了済みであることを示す
    }

    private void FinishTrial() //刺激時間終了時の処理
    {
        SendZeroHaptics(); //安全のためもう一度0を送る
        if (audioSource) audioSource.Stop(); //音声を止める
        state = DemoState.Selecting; //次の条件を選択できる状態へ戻す
        UpdateAllUI();
    }

    private float GetStimulusDurationSeconds(int waveformId, int seIndex)
    {
        if (headphoneClipsRaw != null && seIndex >= 0 && seIndex < headphoneClipsRaw.Length && headphoneClipsRaw[seIndex] != null)
            return Mathf.Max(0.1f, headphoneClipsRaw[seIndex].length); //AudioClipの長さを使用．Mathf.Max()によって最低0.1秒を保証
        return Mathf.Max(0.1f, defaultStimulusDurationSeconds); //AudioClipがなければ，デフォルト1.5秒を使う
    }

    private void SendZeroHaptics()
    {
        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(0f, 0f); //振幅と波形commandを両方0に
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(0f, 0f);
    }

    private void UpdateAllUI()
    {
        UpdateSelectedFrames(); //選択枠を更新
        UpdateStatusText(); //文字情報を更新
        if (renderButton) renderButton.interactable = (state == DemoState.Selecting); //選択状態のときだけRenderをクリックできる
    }

    private void CreateFramesIfNeeded()
    {
        if (!canvas) return;
        Color c = frameColor;
        c.a = Mathf.Clamp01(c.a * frameAlpha);
        for (int i = 0; i < 4; i++) if (!selectedFrames[i]) selectedFrames[i] = CreateFrame($"Selected_{((SelectRow)i)}", c, frameLineWidth);
    }

    private RectTransform CreateFrame(string objectName, Color color, float lineWidth)
    {
        GameObject root = new GameObject(objectName, typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.5f, 0.5f); rootRect.anchorMax = new Vector2(0.5f, 0.5f); rootRect.pivot = new Vector2(0.5f, 0.5f);
        CreateFrameLine(rootRect, "Top", color, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, lineWidth));
        CreateFrameLine(rootRect, "Bottom", color, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, lineWidth));
        CreateFrameLine(rootRect, "Left", color, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(lineWidth, 0f));
        CreateFrameLine(rootRect, "Right", color, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(lineWidth, 0f));
        root.transform.SetAsLastSibling();
        return rootRect;
    }

    private void CreateFrameLine(RectTransform parent, string lineName, Color color, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta)
    {
        GameObject line = new GameObject(lineName, typeof(RectTransform), typeof(Image));
        line.transform.SetParent(parent, false);
        RectTransform rt = line.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = Vector2.zero; rt.sizeDelta = sizeDelta;
        line.GetComponent<Image>().color = color; line.GetComponent<Image>().raycastTarget = false;
    }

    private void UpdateSelectedFrames()
    {
        for (int i = 0; i < 4; i++)
        {
            if (i >= selectedFrames.Length || !selectedFrames[i]) continue; //配列範囲外，またはその場所に選択枠が存在しないなら，その枠については何もしないで次へ進む
            RectTransform target = GetSelectedTarget((SelectRow)i); //現在選ばれているButtonのRectTransformを取得
            PositionFrameOnTarget(selectedFrames[i], target, framePadding);
        }
    }

    private RectTransform GetSelectedTarget(SelectRow row)
    {
        switch (row)
        {
            case SelectRow.Process: return GetButtonRect(processButtons, (int)selection.process);
            case SelectRow.SE: return GetButtonRect(seButtons, selection.se);
            case SelectRow.Audio: return GetButtonRect(audioButtons, selection.audio);
            case SelectRow.Position: return GetButtonRect(positionButtons, selection.position);
            default: return null;
        }
    }

    private RectTransform GetButtonRect(Button[] buttons, int index) { return (buttons == null || index < 0 || index >= buttons.Length || buttons[index] == null) ? null : buttons[index].GetComponent<RectTransform>(); }

    private void PositionFrameOnTarget(RectTransform frame, RectTransform target, float padding)
    {
        if (!frame || !canvas) return;
        if (!target) { frame.gameObject.SetActive(false); return; }
        frame.gameObject.SetActive(true);
        RectTransform canvasRect = canvas.transform as RectTransform;
        if (!canvasRect) return;
        frame.position = target.position; frame.rotation = target.rotation;
        float scaleX = Mathf.Abs(target.lossyScale.x / canvasRect.lossyScale.x);
        float scaleY = Mathf.Abs(target.lossyScale.y / canvasRect.lossyScale.y);
        frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, target.rect.width * scaleX + padding);
        frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, target.rect.height * scaleY + padding);
        frame.SetAsLastSibling();
    }

    private void UpdateStatusText()
    {
        if (!statusText) return;
        //stringは文字列型．GamepadがあればConnected，なければNot found
        string lConnected = leftGamepad != null ? "Connected" : "Not found";
        string rConnected = rightGamepad != null ? "Connected" : "Not found";
        char seChar = (char)('A' + selection.se); //charは1文字を表す型．例えばselection.se = 0 → A，selection.se = 1 → Bとなる
        string audioName = selection.audio == 0 ? "ON" : "OFF";
        //状態を画面表示
        statusText.text = $"State: {state}\n" +
                          $"Selected: {selection.process}, {seChar}, {audioName}, Pos{selection.position + 1}\n" +
                          $"Left (Idx {leftGamepadIndex}): {lConnected}\n" +
                          $"Right (Idx {rightGamepadIndex}): {rConnected}";
    }
}