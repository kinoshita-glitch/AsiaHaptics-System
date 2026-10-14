using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[RequireComponent(typeof(AudioSource))]
public class SoundManager_DualBracelet : MonoBehaviour
{
    private enum ProcessType { RAW = 0, ISM = 1, BPF = 2 } //信号処理の種類（RAW/ISM/BPF）
    private enum DemoState { Selecting, Playing } //現在の状態（選択中か，再生中か）

    private sealed class SelectionData //現在の選択状態（どのボタンが選ばれているか）をまとめて保存しておくための箱
    {
        public ProcessType process = ProcessType.RAW; //現在の信号処理．初期値はRAW
        public int se = 0; //効果音．0～7 = A～H
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
    [Header("UI: Stop")] public Button stopButton;
    [Header("UI: Text")] public Text statusText;

    [Header("UI: Active Images (Selection Highlights)")]
    [Tooltip("Processの選択時に表示するハイライト枠 (3個)")]
    public GameObject[] processActiveImages = new GameObject[3];
    [Tooltip("SEの選択時に表示するハイライト枠 (8個)")]
    public GameObject[] seActiveImages = new GameObject[8];
    [Tooltip("Audioの選択時に表示するハイライト枠 (2個)")]
    public GameObject[] audioActiveImages = new GameObject[2];
    [Tooltip("Positionの選択時に表示するハイライト枠 (7個)")]
    public GameObject[] positionActiveImages = new GameObject[7];

    [Header("Bracelet Gamepad Assignment")] //左右のデバイスがGamepad.allのリストの何番目にあるかを指定
    public int leftGamepadIndex = 0;
    public int rightGamepadIndex = 1;

    [Header("Timing")]
    public float commandHoldSeconds = 0.06f; //BLEの振動命令を送った後，0.06秒待ってからSetMotorSpeeds(0, 0)を送る．次の命令を受け付けられる状態へ戻すための信号
    public float hapticStartDelaySeconds = 0.05f; //音声に対して振動開始を遅らせる時間．0.05 = 50 ms

    private readonly SelectionData selection = new SelectionData();
    private Gamepad leftGamepad;
    private Gamepad rightGamepad;
    private DemoState state = DemoState.Selecting; //開始時は条件選択状態

    private float stimulusEndsAt; //刺激終了予定時刻
    private float stimulusStartedAt; //Audio ON/OFF切替時の再生位置計算に使用
    private float playingWaveformCommand;

    private Coroutine stimulusCoroutine;
    private Coroutine positionUpdateCoroutine;
    private Coroutine zeroCommandCoroutine;

    private void Awake() //UnityがGameObjectを初期化するとき，最初期に1回呼ばれる
    {
        Application.runInBackground = true; //Unityウィンドウがフォーカスを失っても（バックグラウンドでも）処理を継続させる

        if (!audioSource) audioSource = GetComponent<AudioSource>();
        audioSource.spatialBlend = 0f; //3D音響ではなく完全な2D音響にする．0 = 2D，1 = 3D
        audioSource.playOnAwake = false; //シーン開始時に勝手に音を再生しない設定

        //遅延対策のため，使用する音声ファイルをあらかじめメモリ（PCのRAM）へ読み込んでおく
        if (headphoneClipsRaw != null)
        {
            foreach (AudioClip clip in headphoneClipsRaw)
            {
                if (clip != null && clip.loadState != AudioDataLoadState.Loaded) clip.LoadAudioData();
            }
        }

        WireUI(); //ボタンを押したときの処理を登録
        RefreshGamepads(); //接続中Gamepadを取得
        SendZeroHaptics(); //起動時に念のため振動命令を0にする
        UpdateAllUI(); //現在状態をUIへ反映
    }

    private void OnEnable()
    {
        InputSystem.onDeviceChange += OnDeviceChange; //Gamepad接続・切断などのデバイス変化イベントを監視
    }

    private void OnDisable()
    {
        InputSystem.onDeviceChange -= OnDeviceChange; //無効化時にイベント登録を解除
        SendStopHaptics(); //Unity終了時はマイコン側の波形再生自体を完全に停止
        if (audioSource) audioSource.Stop(); //音声も停止
    }

    private void Update() //Unityで毎フレーム1回実行
    {
        if (state == DemoState.Playing && Time.unscaledTime >= stimulusEndsAt) FinishTrial(); //刺激時間終了
    }

    private void WireUI() //UIボタンへの処理登録
    {
        for (int i = 0; i < processButtons.Length; i++)
        {
            int idx = i;
            if (processButtons[i])
            {
                processButtons[i].onClick.AddListener(() =>
                {
                    selection.process = (ProcessType)idx; //選択した信号処理方法（RAW / ISM / BPF）に選択変更
                    UpdateAllUI();
                    if (state == DemoState.Playing) StartStimulus(); //再生中なら新しい波形を先頭から再生
                });
            }
        }

        for (int i = 0; i < seButtons.Length; i++)
        {
            int idx = i;
            if (seButtons[i])
            {
                seButtons[i].onClick.AddListener(() =>
                {
                    selection.se = idx;
                    UpdateAllUI();
                    if (state == DemoState.Playing) StartStimulus(); //再生中なら新しい波形を先頭から再生
                });
            }
        }

        for (int i = 0; i < audioButtons.Length; i++)
        {
            int idx = i;
            if (audioButtons[i])
            {
                audioButtons[i].onClick.AddListener(() =>
                {
                    selection.audio = idx;
                    UpdateAllUI();
                    if (state == DemoState.Playing && stimulusCoroutine == null) UpdateAudioWhilePlaying();
                });
            }
        }

        for (int i = 0; i < positionButtons.Length; i++)
        {
            int idx = i;
            if (positionButtons[i])
            {
                positionButtons[i].onClick.AddListener(() =>
                {
                    selection.position = idx;
                    UpdateAllUI();

                    if (state == DemoState.Selecting)
                    {
                        StartStimulus(); //停止中ならPosition選択で刺激開始
                    }
                    else
                    {
                        if (stimulusCoroutine != null) return;

                        if (positionUpdateCoroutine != null) StopCoroutine(positionUpdateCoroutine);
                        positionUpdateCoroutine = StartCoroutine(UpdatePositionWhilePlayingRoutine());
                    }
                });
            }
        }

        if (stopButton) stopButton.onClick.AddListener(StopCurrentStimulus);
    }

    private void OnDeviceChange(InputDevice device, InputDeviceChange change) //InputDeviceはキーボード・マウス・Gamepadなど全入力機器の基底クラス
    {
        if (device is Gamepad) RefreshGamepads(); //変更された機器がGamepadなら再取得
    }

    private void RefreshGamepads()
    {
        leftGamepad = GetGamepadByIndex(leftGamepadIndex); //指定番号のGamepadを左用として保存
        rightGamepad = GetGamepadByIndex(rightGamepadIndex);
        UpdateAllUI();
    }

    private Gamepad GetGamepadByIndex(int index)
    {
        return (index < 0 || index >= Gamepad.all.Count) ? null : Gamepad.all[index]; //indexが範囲外→null
    }

    private void StartStimulus()
    {
        if (stimulusCoroutine != null) StopCoroutine(stimulusCoroutine);
        if (positionUpdateCoroutine != null) StopCoroutine(positionUpdateCoroutine);
        if (zeroCommandCoroutine != null) StopCoroutine(zeroCommandCoroutine);

        stimulusCoroutine = null;
        positionUpdateCoroutine = null;
        zeroCommandCoroutine = null;

        stimulusCoroutine = StartCoroutine(ExecuteStimulusRoutine());
    }

    private IEnumerator ExecuteStimulusRoutine() //IEnumeratorを返すため，Coroutineとして動かせる
    {
        state = DemoState.Playing;
        stimulusEndsAt = float.PositiveInfinity; //実際の刺激終了時刻を設定するまでUpdate()による誤終了を防ぐ
        InputSystem.ResumeHaptics(); //Input System側のhaptics出力を有効にする

        //マイコン側を次の命令を受け付けられる状態へ戻す
        SendZeroHaptics();
        yield return null; //0,0と次の振動命令を別フレームに分ける

        int seIndex = selection.se;
        int positionIndex = selection.position;
        ProcessType process = selection.process;

        /*
         * 24通りの波形ID
         * RAW A～H = 0～7
         * ISM A～H = 8～15
         * BPF A～H = 16～23
         */
        int waveformId = ((int)process * 8) + seIndex;

        /*
         * waveform IDをBLE送信用commandへ変換
         * ID 0 → 4，ID 1 → 8，...，ID 23 → 96
         */
        int waveformCommandValue = 4 * (waveformId + 1);
        float waveformCommand = waveformCommandValue / 100.0f; //SetMotorSpeeds()用に0～1へ変換
        playingWaveformCommand = waveformCommand;

        //Position 1～7を左右振幅へ変換
        float x = Mathf.Clamp01(positionIndex / 6.0f);
        float leftAmplitude = 1.0f - x;
        float rightAmplitude = x;

        if (audioSource) //AudioSourceが存在するか確認
        {
            audioSource.Stop(); //前の音が残っていれば停止

            if (selection.audio == 0 &&
                headphoneClipsRaw != null &&
                seIndex >= 0 &&
                seIndex < headphoneClipsRaw.Length &&
                headphoneClipsRaw[seIndex] != null)
            {
                audioSource.clip = headphoneClipsRaw[seIndex]; //選択された音声を設定
                audioSource.Play(); //再生開始
            }
        }

        //PC音声の出力遅延を補正するため，指定時間待ってから振動を開始
        yield return new WaitForSecondsRealtime(hapticStartDelaySeconds);

        Debug.Log($"[Haptics] Play -> ID:{waveformId}, L_Amp:{leftAmplitude:F2}, R_Amp:{rightAmplitude:F2}, Cmd:{waveformCommand:F2}");

        //SetMotorSpeeds()を振幅＋波形IDを送る通信チャンネルとして使用
        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(leftAmplitude, waveformCommand);
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(rightAmplitude, waveformCommand);

        stimulusStartedAt = Time.unscaledTime;
        stimulusEndsAt = Time.unscaledTime + GetStimulusDurationSeconds(seIndex);

        zeroCommandCoroutine = StartCoroutine(ReturnHapticsToZeroAfterDelay());
        UpdateAllUI();
        stimulusCoroutine = null;
    }

    private IEnumerator UpdatePositionWhilePlayingRoutine()
    {
        if (zeroCommandCoroutine != null)
        {
            StopCoroutine(zeroCommandCoroutine);
            zeroCommandCoroutine = null;
        }

        //0,0で次のBLE命令を受け付け可能にする．波形再生位置playPosはリセットされない
        SendZeroHaptics();
        yield return null;

        if (state != DemoState.Playing)
        {
            positionUpdateCoroutine = null;
            yield break;
        }

        float x = Mathf.Clamp01(selection.position / 6.0f);
        float leftAmplitude = 1.0f - x;
        float rightAmplitude = x;

        Debug.Log($"[Haptics] Update Position -> L_Amp:{leftAmplitude:F2}, R_Amp:{rightAmplitude:F2}, Cmd:{playingWaveformCommand:F2}");

        //同じwaveform IDなので，マイコン側では再生位置を維持したまま振幅だけ変更される
        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(leftAmplitude, playingWaveformCommand);
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(rightAmplitude, playingWaveformCommand);

        zeroCommandCoroutine = StartCoroutine(ReturnHapticsToZeroAfterDelay());
        positionUpdateCoroutine = null;
    }

    private void UpdateAudioWhilePlaying()
    {
        if (!audioSource) return;

        if (selection.audio != 0)
        {
            audioSource.Stop();
            return;
        }

        int seIndex = selection.se;
        if (headphoneClipsRaw == null || seIndex < 0 || seIndex >= headphoneClipsRaw.Length || headphoneClipsRaw[seIndex] == null) return;

        AudioClip clip = headphoneClipsRaw[seIndex];
        float elapsed = Mathf.Max(0f, Time.unscaledTime - stimulusStartedAt);

        audioSource.Stop();
        audioSource.clip = clip;

        //途中からAudio ONにした場合は，現在の刺激時間に対応する位置から音声を再開
        if (elapsed < clip.length)
        {
            audioSource.time = Mathf.Min(elapsed, clip.length - 0.001f);
            audioSource.Play();
        }
    }

    private void StopCurrentStimulus()
    {
        if (state != DemoState.Playing) return;

        if (stimulusCoroutine != null) StopCoroutine(stimulusCoroutine);
        if (positionUpdateCoroutine != null) StopCoroutine(positionUpdateCoroutine);
        if (zeroCommandCoroutine != null) StopCoroutine(zeroCommandCoroutine);

        stimulusCoroutine = null;
        positionUpdateCoroutine = null;
        zeroCommandCoroutine = null;

        if (audioSource) audioSource.Stop();

        SendStopHaptics(); //マイコン側のplayPosも0へ戻す

        state = DemoState.Selecting;
        stimulusEndsAt = float.PositiveInfinity;
        selection.position = -1;
        playingWaveformCommand = 0f;
        UpdateAllUI();
    }

    private void FinishTrial() //刺激時間終了時の処理
    {
        if (positionUpdateCoroutine != null) StopCoroutine(positionUpdateCoroutine);
        if (zeroCommandCoroutine != null) StopCoroutine(zeroCommandCoroutine);

        positionUpdateCoroutine = null;
        zeroCommandCoroutine = null;

        SendStopHaptics(); //マイコン側の再生状態も初期化
        if (audioSource) audioSource.Stop(); //音声を止める

        state = DemoState.Selecting; //次の条件を選択できる状態へ戻す
        selection.position = -1; //刺激終了後はPositionを未選択状態へ戻す
        playingWaveformCommand = 0f;
        UpdateAllUI();
    }

    private float GetStimulusDurationSeconds(int seIndex)
    {
        //RAW / ISM / BPFで同じSEは同じ長さなので，SE番号だけで再生時間を決定
        switch (seIndex)
        {
            case 0:
            case 1: return 1.300f;
            case 2: return 1.403f;
            case 3: return 1.472f;
            case 4:
            case 5:
            case 6: return 30.000f;
            case 7: return 5.000f;
            default: return 1.500f;
        }
    }

    private IEnumerator ReturnHapticsToZeroAfterDelay()
    {
        yield return new WaitForSecondsRealtime(commandHoldSeconds); //Time.timeScaleの影響を受けない
        SendZeroHaptics(); //両Gamepadへ0,0を送る
        zeroCommandCoroutine = null;
    }

    private void SendZeroHaptics()
    {
        //0,0は停止ではなく，次のBLE命令を受け付けられる状態へ戻すために使用
        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(0f, 0f);
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(0f, 0f);
    }

    private void SendStopHaptics()
    {
        InputSystem.ResumeHaptics();

        //command=1.0 → マイコン側で100となり，STOP専用commandとして扱われる
        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(0f, 1.0f);
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(0f, 1.0f);
    }

    private void UpdateAllUI()
    {
        UpdateHighlights(); //各項目のハイライト枠を更新
        UpdateStatusText(); //文字情報を更新
    }

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
        for (int i = 0; i < images.Length; i++) if (images[i]) images[i].SetActive(i == activeIdx);
    }

    private void UpdateStatusText()
    {
        if (!statusText) return;

        string lConnected = leftGamepad != null ? "Connected" : "Not found";
        string rConnected = rightGamepad != null ? "Connected" : "Not found";
        char seChar = (char)('A' + selection.se); //selection.se = 0 → A，1 → B
        string audioName = selection.audio == 0 ? "ON" : "OFF";
        string positionName = selection.position >= 0 ? $"Pos{selection.position + 1}" : "Pos-";

        statusText.text = $"State: {state}\n" +
                          $"Selected: {selection.process}, {seChar}, {audioName}, {positionName}\n" +
                          $"Left (Idx {leftGamepadIndex}): {lConnected}\n" +
                          $"Right (Idx {rightGamepadIndex}): {rConnected}";
    }
}