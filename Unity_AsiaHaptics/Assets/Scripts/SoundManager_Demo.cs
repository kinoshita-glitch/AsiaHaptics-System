/*
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(AudioSource))]
public class SoundManager_Demo : MonoBehaviour
{
    // ======= Inspector: Audio Data =======
    [Header("Audio Sets (Mono clips)")]
    public AudioSource audioSource;                // AudioSource (2D / volume=1)

    [Tooltip("セット1用の音声ファイル4つ (ヘッドフォン音声は常にこれを使用)")]
    public AudioClip[] clipsSet1 = new AudioClip[4];

    [Tooltip("セット2用の音声ファイル4つ (振動のみに使用)")]
    public AudioClip[] clipsSet2 = new AudioClip[4];

    // ======= Inspector: UI =======
    [Header("UI: Control")]
    public Button renderButton;     // 再生ボタン

    [Header("UI: Set Selection")]
    public Button set1Button;
    public Button set2Button;
    public GameObject set1ActiveImage; // Set1選択時に表示する枠など
    public GameObject set2ActiveImage; // Set2選択時に表示する枠など

    [Header("UI: Audio Mode")]
    public Button audioOnButton;
    public Button audioOffButton;
    public GameObject audioOnActiveImage;  // AudioON選択時の枠
    public GameObject audioOffActiveImage; // AudioOFF選択時の枠

    [Header("UI: Parameters Selection")]
    [Tooltip("音声選択ボタン 4個")]
    public Button[] clipSelectButtons = new Button[4];
    [Tooltip("振幅比選択ボタン 7個")]
    public Button[] panSelectButtons = new Button[7];

    // 選択状態のハイライト表示用オブジェクト
    public GameObject[] clipActiveImages = new GameObject[4];
    public GameObject[] panActiveImages = new GameObject[7];

    // ======= Internal State =======
    private int currentSetIndex = 0; // 0: Set1, 1: Set2
    private bool isAudioOn = true;   // true: Headphoneあり, false: なし

    private int selectedClipIdx = -1; // 0..3 (未選択は-1)
    private int selectedPanIdx = -1;  // 0..6 (未選択は-1)

    private bool isPlaying = false;

    // PCM Cache
    private float[][] clipData1 = new float[4][];
    private float[][] clipData2 = new float[4][];

    // Linear Pan Logic
    private float leftGain = 0.5f;
    private float rightGain = 0.5f;

    // Audio Engine Buffers
    private float[] actuatorPCM;
    private int actuatorPos, actuatorEnd;
    private volatile bool actuatorPlaying;

    // Headphone PCM (Stimulus Mirror)
    private float[] headphonePCM;
    private int headphonePos, headphoneEnd;
    private volatile bool headphonePlaying;

    // Thread Flags
    private volatile bool playbackEndedFlag;

    // ======= Unity Lifecycle =======
    private void Awake()
    {
        if (!audioSource) audioSource = GetComponent<AudioSource>();
        audioSource.spatialBlend = 0f; // 2D

        // PCMデータのロード
        LoadAllClipData();

        // UIイベントの登録
        WireUI();

        // 初期状態の設定（Set1, Audio ON, 未選択）
        OnSelectSet(0);
        OnSelectAudioMode(true);
        ResetSelection();
    }

    private void Update()
    {
        // オーディオスレッドからの終了通知をメインスレッドで処理
        if (playbackEndedFlag)
        {
            playbackEndedFlag = false;
            isPlaying = false;
            UpdateRenderButtonState();
        }
    }

    // ======= Data Loading =======
    private void LoadAllClipData()
    {
        // Set 1
        for (int i = 0; i < 4; i++)
        {
            if (i < clipsSet1.Length && clipsSet1[i] != null)
            {
                clipData1[i] = new float[clipsSet1[i].samples];
                clipsSet1[i].GetData(clipData1[i], 0);
            }
        }
        // Set 2
        for (int i = 0; i < 4; i++)
        {
            if (i < clipsSet2.Length && clipsSet2[i] != null)
            {
                clipData2[i] = new float[clipsSet2[i].samples];
                clipsSet2[i].GetData(clipData2[i], 0);
            }
        }
    }

    // ======= UI Wiring =======
    private void WireUI()
    {
        // Set Select
        if (set1Button) set1Button.onClick.AddListener(() => OnSelectSet(0));
        if (set2Button) set2Button.onClick.AddListener(() => OnSelectSet(1));

        // Audio Mode
        if (audioOnButton) audioOnButton.onClick.AddListener(() => OnSelectAudioMode(true));
        if (audioOffButton) audioOffButton.onClick.AddListener(() => OnSelectAudioMode(false));

        // Clip Select (0-3)
        for (int i = 0; i < clipSelectButtons.Length; i++)
        {
            int idx = i;
            if (clipSelectButtons[i])
            {
                clipSelectButtons[i].onClick.AddListener(() =>
                {
                    selectedClipIdx = idx;
                    UpdateHighlight(clipActiveImages, idx);
                    UpdateRenderButtonState();
                });
            }
        }

        // Pan Select (0-6)
        for (int i = 0; i < panSelectButtons.Length; i++)
        {
            int idx = i;
            if (panSelectButtons[i])
            {
                panSelectButtons[i].onClick.AddListener(() =>
                {
                    selectedPanIdx = idx;
                    UpdateHighlight(panActiveImages, idx);
                    UpdateRenderButtonState();
                });
            }
        }

        // Render
        if (renderButton)
        {
            renderButton.onClick.AddListener(OnClickRender);
        }
    }

    // ======= UI Actions =======
    private void OnSelectSet(int setIndex)
    {
        if (isPlaying) return; // 再生中は変更不可
        currentSetIndex = setIndex;

        // ハイライト更新
        if (set1ActiveImage) set1ActiveImage.SetActive(setIndex == 0);
        if (set2ActiveImage) set2ActiveImage.SetActive(setIndex == 1);
    }

    private void OnSelectAudioMode(bool on)
    {
        isAudioOn = on;
        if (audioOnActiveImage) audioOnActiveImage.SetActive(on);
        if (audioOffActiveImage) audioOffActiveImage.SetActive(!on);
    }

    private void UpdateHighlight(GameObject[] images, int activeIdx)
    {
        if (images == null) return;
        for (int i = 0; i < images.Length; i++)
        {
            if (images[i]) images[i].SetActive(i == activeIdx);
        }
    }

    private void ResetSelection()
    {
        selectedClipIdx = -1;
        selectedPanIdx = -1;
        UpdateHighlight(clipActiveImages, -1);
        UpdateHighlight(panActiveImages, -1);
        UpdateRenderButtonState();
    }

    private void UpdateRenderButtonState()
    {
        bool ready = (selectedClipIdx >= 0 && selectedPanIdx >= 0 && !isPlaying);
        if (renderButton) renderButton.interactable = ready;
    }

    // ======= Render Logic (Modified) =======
    public void OnClickRender()
    {
        if (isPlaying) return;
        if (selectedClipIdx < 0 || selectedPanIdx < 0) return;

        // 1. パンの計算
        float x = Mathf.Clamp01(selectedPanIdx / 6f);
        leftGain = 1f - x;
        rightGain = x;

        // 2. アクチュエータ用データの取得 (選択されたセットから取得)
        float[][] actuatorSetData = (currentSetIndex == 0) ? clipData1 : clipData2;
        float[] actPCM = actuatorSetData[selectedClipIdx];

        if (actPCM == null)
        {
            Debug.LogError("Actuator PCM Data is null.");
            return;
        }

        // 3. ヘッドフォン用データの取得 (常にセット1から取得)
        float[] hpPCM = clipData1[selectedClipIdx];

        // 4. 再生開始

        // アクチュエータ再生開始
        int actLen = actPCM.Length;
        StartActuator(actPCM, actLen, leftGain, rightGain);

        // ヘッドフォン再生開始 (Audio ON かつ データがある場合)
        if (isAudioOn && hpPCM != null)
        {
            StartHeadphone(hpPCM, hpPCM.Length);
        }
        else
        {
            StopHeadphone();
        }

        isPlaying = true;
        UpdateRenderButtonState();
    }

    // ======= Audio Engine (4ch Routing) =======

    // Actuator: RL(ch2), RR(ch3)
    private void StartActuator(float[] data, int samplesToUse, float lg, float rg)
    {
        actuatorPCM = data;
        actuatorPos = 0;
        actuatorEnd = Mathf.Min(samplesToUse, data?.Length ?? 0);
        leftGain = lg; rightGain = rg;
        actuatorPlaying = (actuatorPCM != null && actuatorEnd > 0);
        playbackEndedFlag = false;
    }

    // Headphone: FL(ch0), FR(ch1)
    private void StartHeadphone(float[] data, int samplesToUse)
    {
        headphonePCM = data;
        headphonePos = 0;
        headphoneEnd = Mathf.Min(samplesToUse, data?.Length ?? 0);
        headphonePlaying = (headphonePCM != null && headphoneEnd > 0);
    }

    private void StopHeadphone()
    {
        headphonePlaying = false;
        headphonePos = headphoneEnd = 0;
    }

    // Unity Audio Thread Callback
    private void OnAudioFilterRead(float[] data, int channels)
    {
        for (int n = 0; n < data.Length; n += channels)
        {
            // データ取得
            float actSample = 0f;
            if (actuatorPlaying && actuatorPos < actuatorEnd)
            {
                actSample = actuatorPCM[actuatorPos++];
            }

            float hpSample = 0f;
            if (headphonePlaying && headphonePos < headphoneEnd)
            {
                hpSample = headphonePCM[headphonePos++];
            }

            // 出力書き込み
            if (channels >= 4)
            {
                // Ch 0,1 : Headphone
                data[n + 0] = hpSample;
                data[n + 1] = hpSample;

                // Ch 2,3 : Actuator (Vibration)
                data[n + 2] = actSample * leftGain;
                data[n + 3] = actSample * rightGain;
            }
            else if (channels == 2)
            {
                // 2ch環境用デバッグ出力
                data[n + 0] = hpSample;
                data[n + 1] = hpSample;
            }
        }

        // 再生終了判定（アクチュエータ基準）
        // ※ヘッドフォンの音声が長くても、振動が終われば強制終了します
        if (actuatorPlaying && actuatorPos >= actuatorEnd)
        {
            actuatorPlaying = false;
            headphonePlaying = false;
            playbackEndedFlag = true;
        }
    }
}
*/




using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.UI;

[RequireComponent(typeof(AudioSource))]
public class SoundManager_Demo_BLE : MonoBehaviour
{
    public enum ProcessType
    {
        RAW = 0,
        ISM = 1,
        BPF = 2
    }

    private const int SeCount = 4;
    private const int PositionCount = 7;
    private const int WaveformCount = 12;
    private const int WaveformCommandStep = 16;

    // =========================================================
    // Inspector: Headphone audio
    // =========================================================
    [Header("Headphone Audio")]
    [Tooltip("ヘッドホン再生用AudioSource。RAW/ISM/BPFのどれを選んでも、このRAW音声を使用します。")]
    public AudioSource audioSource;

    [Tooltip("ヘッドホンで再生する元音声A～D。Element 0=A, 1=B, 2=C, 3=D")]
    public AudioClip[] headphoneClipsRaw = new AudioClip[SeCount];

    // =========================================================
    // Inspector: UI
    // =========================================================
    [Header("UI: Control")]
    public Button renderButton;
    public Text statusText;

    [Header("UI: Process Selection")]
    public Button rawButton;
    public Button ismButton;
    public Button bpfButton;
    public GameObject rawActiveImage;
    public GameObject ismActiveImage;
    public GameObject bpfActiveImage;

    [Header("UI: Audio Mode")]
    public Button audioOnButton;
    public Button audioOffButton;
    public GameObject audioOnActiveImage;
    public GameObject audioOffActiveImage;

    [Header("UI: SE Selection")]
    [Tooltip("SE選択ボタン4個。Element 0=A, 1=B, 2=C, 3=D")]
    public Button[] clipSelectButtons = new Button[SeCount];

    public GameObject[] clipActiveImages = new GameObject[SeCount];

    [Header("UI: Position Selection")]
    [Tooltip("位置選択ボタン7個。Element 0=左端, Element 6=右端")]
    public Button[] positionSelectButtons = new Button[PositionCount];

    public GameObject[] positionActiveImages = new GameObject[PositionCount];

    // =========================================================
    // Inspector: BLE gamepads
    // =========================================================
    [Header("BLE Bracelet Gamepads")]
    [Tooltip("Gamepad.allの中で左ブレスレットに対応する番号")]
    [Min(0)]
    public int leftGamepadListIndex = 0;

    [Tooltip("Gamepad.allの中で右ブレスレットに対応する番号")]
    [Min(0)]
    public int rightGamepadListIndex = 1;

    [Tooltip("本コマンド前に0,0を送ってから待つ時間")]
    [Range(0.01f, 0.5f)]
    public float commandResetDelaySeconds = 0.05f;

    [Tooltip("本コマンドを送ってから0,0へ戻すまでの時間")]
    [Range(0.01f, 0.5f)]
    public float commandPulseSeconds = 0.05f;

    [Header("Playback Duration")]
    [Tooltip("12波形の再生時間（秒）。0以下なら対応するRAW音声の長さを使用します。順番はRAW A-D, ISM A-D, BPF A-D。")]
    public float[] vibrationDurationsSeconds = new float[WaveformCount];

    // =========================================================
    // Internal state
    // =========================================================
    private ProcessType currentProcess = ProcessType.RAW;
    private bool isAudioOn = true;
    private int selectedClipIdx = -1;
    private int selectedPositionIdx = -1;
    private bool isPlaying;

    private Gamepad leftBracelet;
    private Gamepad rightBracelet;
    private Coroutine renderCoroutine;

    // =========================================================
    // Unity lifecycle
    // =========================================================
    private void Awake()
    {
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f;

        WireUI();

        OnSelectProcess(ProcessType.RAW);
        OnSelectAudioMode(true);
        ResetSelection();
    }

    private void OnEnable()
    {
        InputSystem.onDeviceChange += OnInputDeviceChange;
    }

    private void Start()
    {
        RefreshBraceletGamepads();
    }

    private void OnDisable()
    {
        InputSystem.onDeviceChange -= OnInputDeviceChange;

        if (renderCoroutine != null)
        {
            StopCoroutine(renderCoroutine);
            renderCoroutine = null;
        }

        StopBraceletCommands();

        if (audioSource != null)
        {
            audioSource.Stop();
        }

        isPlaying = false;
    }

    private void OnApplicationQuit()
    {
        StopBraceletCommands();
    }

    // =========================================================
    // UI wiring
    // =========================================================
    private void WireUI()
    {
        if (rawButton != null)
        {
            rawButton.onClick.AddListener(() => OnSelectProcess(ProcessType.RAW));
        }

        if (ismButton != null)
        {
            ismButton.onClick.AddListener(() => OnSelectProcess(ProcessType.ISM));
        }

        if (bpfButton != null)
        {
            bpfButton.onClick.AddListener(() => OnSelectProcess(ProcessType.BPF));
        }

        if (audioOnButton != null)
        {
            audioOnButton.onClick.AddListener(() => OnSelectAudioMode(true));
        }

        if (audioOffButton != null)
        {
            audioOffButton.onClick.AddListener(() => OnSelectAudioMode(false));
        }

        for (int i = 0; i < clipSelectButtons.Length; i++)
        {
            int capturedIndex = i;
            if (clipSelectButtons[i] != null)
            {
                clipSelectButtons[i].onClick.AddListener(() => OnSelectClip(capturedIndex));
            }
        }

        for (int i = 0; i < positionSelectButtons.Length; i++)
        {
            int capturedIndex = i;
            if (positionSelectButtons[i] != null)
            {
                positionSelectButtons[i].onClick.AddListener(() => OnSelectPosition(capturedIndex));
            }
        }

        if (renderButton != null)
        {
            renderButton.onClick.AddListener(OnClickRender);
        }
    }

    // =========================================================
    // UI actions
    // =========================================================
    private void OnSelectProcess(ProcessType process)
    {
        if (isPlaying)
        {
            return;
        }

        currentProcess = process;

        SetActive(rawActiveImage, process == ProcessType.RAW);
        SetActive(ismActiveImage, process == ProcessType.ISM);
        SetActive(bpfActiveImage, process == ProcessType.BPF);

        UpdateStatusText();
    }

    private void OnSelectAudioMode(bool on)
    {
        isAudioOn = on;

        SetActive(audioOnActiveImage, on);
        SetActive(audioOffActiveImage, !on);

        UpdateStatusText();
    }

    private void OnSelectClip(int index)
    {
        if (isPlaying || index < 0 || index >= SeCount)
        {
            return;
        }

        selectedClipIdx = index;
        UpdateHighlight(clipActiveImages, index);
        UpdateRenderButtonState();
        UpdateStatusText();
    }

    private void OnSelectPosition(int index)
    {
        if (isPlaying || index < 0 || index >= PositionCount)
        {
            return;
        }

        selectedPositionIdx = index;
        UpdateHighlight(positionActiveImages, index);
        UpdateRenderButtonState();
        UpdateStatusText();
    }

    private static void SetActive(GameObject target, bool active)
    {
        if (target != null)
        {
            target.SetActive(active);
        }
    }

    private static void UpdateHighlight(GameObject[] images, int activeIndex)
    {
        if (images == null)
        {
            return;
        }

        for (int i = 0; i < images.Length; i++)
        {
            if (images[i] != null)
            {
                images[i].SetActive(i == activeIndex);
            }
        }
    }

    private void ResetSelection()
    {
        selectedClipIdx = -1;
        selectedPositionIdx = -1;

        UpdateHighlight(clipActiveImages, -1);
        UpdateHighlight(positionActiveImages, -1);

        UpdateRenderButtonState();
        UpdateStatusText();
    }

    private void UpdateRenderButtonState()
    {
        bool selectionReady = selectedClipIdx >= 0 && selectedPositionIdx >= 0;
        bool devicesReady = leftBracelet != null && rightBracelet != null;
        bool ready = selectionReady && devicesReady && !isPlaying;

        if (renderButton != null)
        {
            renderButton.interactable = ready;
        }
    }

    private void UpdateStatusText()
    {
        if (statusText == null)
        {
            return;
        }

        string stateText = isPlaying ? "Playing" : "Ready";

        string processText = currentProcess.ToString();

        string seText = selectedClipIdx >= 0
            ? ((char)('A' + selectedClipIdx)).ToString()
            : "-";

        string positionText = selectedPositionIdx >= 0
            ? (selectedPositionIdx + 1).ToString()
            : "-";

        string audioText = isAudioOn ? "ON" : "OFF";

        string leftState = leftBracelet != null
            ? "Connected"
            : "Not found";

        string rightState = rightBracelet != null
            ? "Connected"
            : "Not found";

        statusText.text =
            $"State:{stateText}\n" +
            $"Process:{processText}\n" +
            $"SE:{seText}\n" +
            $"Position:{positionText}\n" +
            $"Audio:{audioText}\n" +
            $"L:{leftState}\n" +
            $"R:{rightState}";
    }

    // =========================================================
    // Gamepad discovery
    // =========================================================
    private void OnInputDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (device is Gamepad)
        {
            RefreshBraceletGamepads();
        }
    }

    [ContextMenu("Refresh Bracelet Gamepads")]
    public void RefreshBraceletGamepads()
    {
        leftBracelet = null;
        rightBracelet = null;

        int count = Gamepad.all.Count;

        if (leftGamepadListIndex >= 0 && leftGamepadListIndex < count)
        {
            leftBracelet = Gamepad.all[leftGamepadListIndex];
        }

        if (rightGamepadListIndex >= 0 && rightGamepadListIndex < count)
        {
            rightBracelet = Gamepad.all[rightGamepadListIndex];
        }

        if (leftBracelet != null && rightBracelet != null && leftBracelet == rightBracelet)
        {
            Debug.LogError("左右のブレスレットに同じGamepadが割り当てられています。Inspectorの番号を変更してください。");
            rightBracelet = null;
        }

        UpdateRenderButtonState();
        UpdateStatusText();
    }

    [ContextMenu("Log Connected Gamepads")]
    public void LogConnectedGamepads()
    {
        Debug.Log($"Connected Gamepads: {Gamepad.all.Count}");

        for (int i = 0; i < Gamepad.all.Count; i++)
        {
            Gamepad gamepad = Gamepad.all[i];
            InputDeviceDescription description = gamepad.description;

            Debug.Log(
                $"[{i}] deviceId={gamepad.deviceId}, " +
                $"displayName={gamepad.displayName}, " +
                $"product={description.product}, " +
                $"manufacturer={description.manufacturer}, " +
                $"serial={description.serial}, " +
                $"interface={description.interfaceName}");
        }
    }

    // =========================================================
    // Render
    // =========================================================
    public void OnClickRender()
    {
        if (isPlaying || selectedClipIdx < 0 || selectedPositionIdx < 0)
        {
            return;
        }

        RefreshBraceletGamepads();

        if (leftBracelet == null || rightBracelet == null)
        {
            Debug.LogError("左右のブレスレットがGamepadとして認識されていません。");
            return;
        }

        if (renderCoroutine != null)
        {
            StopCoroutine(renderCoroutine);
        }

        renderCoroutine = StartCoroutine(RenderSequence());
    }

    private IEnumerator RenderSequence()
    {
        isPlaying = true;
        UpdateRenderButtonState();
        UpdateStatusText();

        int waveformId = ((int)currentProcess * SeCount) + selectedClipIdx;
        byte commandByte = EncodeWaveformCommand(waveformId);
        float waveformCommand = commandByte / 255f;

        float position = Mathf.Clamp01(selectedPositionIdx / 6f);
        float leftGain = 1f - position;
        float rightGain = position;

        // 同じコマンドを連続送信した場合でも新しいHID出力レポートを発生させるため、
        // 先に0,0を送ってマイコン側を再アームします。
        StopBraceletCommands();
        yield return new WaitForSecondsRealtime(commandResetDelaySeconds);

        if (leftBracelet == null || rightBracelet == null)
        {
            FinishRender();
            yield break;
        }

        // 第1引数: 各ブレスレットの振幅
        // 第2引数: RAW/ISM/BPF × A/B/C/Dを表す波形コード
        SendMotorCommand(leftBracelet, leftGain, waveformCommand);
        SendMotorCommand(rightBracelet, rightGain, waveformCommand);

        if (isAudioOn)
        {
            PlayHeadphoneAudio(selectedClipIdx);
        }

        float playbackDuration = GetPlaybackDurationSeconds(waveformId, selectedClipIdx);

        // HID値は保持する必要がないため、短時間後に0,0へ戻します。
        // マイコン側ではこの0,0を「停止」ではなく「次のコマンド受付可能」として扱います。
        yield return new WaitForSecondsRealtime(commandPulseSeconds);
        StopBraceletCommands();

        float remainingSeconds = playbackDuration - commandPulseSeconds;
        if (remainingSeconds > 0f)
        {
            yield return new WaitForSecondsRealtime(remainingSeconds);
        }

        FinishRender();
    }

    private void PlayHeadphoneAudio(int clipIndex)
    {
        if (audioSource == null || headphoneClipsRaw == null ||
            clipIndex < 0 || clipIndex >= headphoneClipsRaw.Length)
        {
            return;
        }

        AudioClip clip = headphoneClipsRaw[clipIndex];
        if (clip == null)
        {
            Debug.LogWarning($"Headphone RAW clip {clipIndex} is not assigned.");
            return;
        }

        audioSource.Stop();
        audioSource.clip = clip;
        audioSource.Play();
    }

    private float GetPlaybackDurationSeconds(int waveformId, int clipIndex)
    {
        if (vibrationDurationsSeconds != null &&
            waveformId >= 0 && waveformId < vibrationDurationsSeconds.Length &&
            vibrationDurationsSeconds[waveformId] > 0f)
        {
            return vibrationDurationsSeconds[waveformId];
        }

        if (headphoneClipsRaw != null &&
            clipIndex >= 0 && clipIndex < headphoneClipsRaw.Length &&
            headphoneClipsRaw[clipIndex] != null)
        {
            return headphoneClipsRaw[clipIndex].length;
        }

        return Mathf.Max(0.1f, commandPulseSeconds);
    }

    private static byte EncodeWaveformCommand(int waveformId)
    {
        if (waveformId < 0 || waveformId >= WaveformCount)
        {
            throw new ArgumentOutOfRangeException(nameof(waveformId));
        }

        // waveformId 0～11を16, 32, ..., 192へ変換します。
        return (byte)(WaveformCommandStep * (waveformId + 1));
    }

    private static void SendMotorCommand(Gamepad gamepad, float amplitude, float waveformCommand)
    {
        if (gamepad == null)
        {
            return;
        }

        try
        {
            gamepad.SetMotorSpeeds(
                Mathf.Clamp01(amplitude),
                Mathf.Clamp01(waveformCommand));
        }
        catch (Exception exception)
        {
            Debug.LogError($"Gamepad command failed: {exception.Message}");
        }
    }

    private void StopBraceletCommands()
    {
        SendMotorCommand(leftBracelet, 0f, 0f);
        SendMotorCommand(rightBracelet, 0f, 0f);
    }

    private void FinishRender()
    {
        StopBraceletCommands();

        isPlaying = false;
        renderCoroutine = null;

        UpdateRenderButtonState();
        UpdateStatusText();
    }
}