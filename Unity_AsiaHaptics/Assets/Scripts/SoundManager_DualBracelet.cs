/*
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[RequireComponent(typeof(AudioSource))]
public class SoundManager_DualBracelet : MonoBehaviour
{
    private enum ProcessType
    {
        RAW = 0,
        ISM = 1,
        BPF = 2
    }

    private enum SelectRow
    {
        Process = 0,
        SE = 1,
        Audio = 2,
        Position = 3
    }

    private enum DemoState
    {
        Selecting,
        Countdown,
        Playing,
        AwaitRelease
    }

    private sealed class PlayerSelection
    {
        public SelectRow row = SelectRow.Process;
        public ProcessType process = ProcessType.RAW;
        public int se = 0;          // 0:A, 1:B, 2:C, 3:D
        public int audio = 0;       // 0:ON, 1:OFF
        public int position = 0;    // 0..6
    }

    // =====================================================================
    // Inspector: Headphone audio
    // =====================================================================
    [Header("Headphone Audio")]
    public AudioSource audioSource;

    [Tooltip("A, B, C, D の元音声。Process が RAW/ISM/BPF のどれでもヘッドホンにはこれを使う。")]
    public AudioClip[] headphoneClipsRaw = new AudioClip[4];

    // =====================================================================
    // Inspector: UI targets
    // =====================================================================
    [Header("UI: Process (RAW / ISM / BPF)")]
    public Button[] processButtons = new Button[3];

    [Header("UI: SE (A / B / C / D)")]
    public Button[] seButtons = new Button[4];

    [Header("UI: Audio (ON / OFF)")]
    public Button[] audioButtons = new Button[2];

    [Header("UI: Position (1..7)")]
    public Button[] positionButtons = new Button[7];

    [Header("UI: Text")]
    public Text statusText;
    public Text countdownText;

    [Header("UI: Old Render Button (optional)")]
    public Button renderButton;
    public bool hideRenderButton = true;

    [Tooltip("以前の RAW_Active / ISM_Active / BPF_Active 等を使わない場合、ここへ入れると起動時に非表示にする。")]
    public GameObject[] oldActiveImages;

    // =====================================================================
    // Inspector: Cursor appearance
    // =====================================================================
    [Header("Cursor Appearance")]
    public Canvas canvas;
    public Color user1CursorColor = new Color(0.0f, 0.65f, 1.0f, 1.0f);
    public Color user2CursorColor = new Color(1.0f, 0.2f, 0.55f, 1.0f);
    public float cursorLineWidth = 4.0f;
    public float user1CursorPadding = 8.0f;
    public float user2CursorPadding = 16.0f;

    // =====================================================================
    // Inspector: Gamepad assignment
    // =====================================================================
    [Header("Bracelet Gamepad Assignment")]
    [Tooltip("Gamepad.all の何番目を User 1 に使うか。")]
    public int user1GamepadIndex = 0;

    [Tooltip("Gamepad.all の何番目を User 2 に使うか。")]
    public int user2GamepadIndex = 1;

    [Tooltip("人体通信の受信側(RX)が User 1 ならON。User 2がRXならOFF。")]
    public bool contactReceiverIsUser1 = true;

    // =====================================================================
    // Inspector: Timing
    // =====================================================================
    [Header("Timing")]
    [Tooltip("手をつないでから刺激開始までのカウントダウン。")]
    public float countdownSeconds = 3.0f;

    [Tooltip("Xボタン(人体通信)のチャタリング対策。")]
    public float contactDebounceSeconds = 0.08f;

    [Tooltip("A/Bボタンの二重入力防止。")]
    public float inputGuardSeconds = 0.08f;

    [Tooltip("SetMotorSpeedsで刺激コマンドを送った後、0に戻すまでの時間。")]
    public float commandHoldSeconds = 0.06f;

    [Tooltip("音声長が取得できない場合に使う刺激時間。")]
    public float defaultStimulusDurationSeconds = 1.5f;

    // =====================================================================
    // Runtime state
    // =====================================================================
    private readonly PlayerSelection user1 = new PlayerSelection();
    private readonly PlayerSelection user2 = new PlayerSelection();

    private Gamepad user1Gamepad;
    private Gamepad user2Gamepad;

    private RectTransform user1Cursor;
    private RectTransform user2Cursor;

    private DemoState state = DemoState.Selecting;

    private float user1LastInputTime = -100f;
    private float user2LastInputTime = -100f;

    private bool rawContact;
    private bool contactCandidate;
    private bool stableContact;
    private float contactCandidateChangedAt;
    private bool lastStableContact;
    private bool contactArmed;

    private float countdownStartedAt;
    private float stimulusEndsAt;
    private Coroutine zeroCommandCoroutine;

    // =====================================================================
    // Unity lifecycle
    // =====================================================================
    private void Awake()
    {
        if (!audioSource)
            audioSource = GetComponent<AudioSource>();

        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake = false;

        if (!canvas)
            canvas = GetComponentInParent<Canvas>();

        if (renderButton)
        {
            renderButton.interactable = false;
            if (hideRenderButton)
                renderButton.gameObject.SetActive(false);
        }

        if (oldActiveImages != null)
        {
            foreach (GameObject obj in oldActiveImages)
            {
                if (obj)
                    obj.SetActive(false);
            }
        }

        CreateCursorsIfNeeded();
        RefreshGamepads();
        SendZeroHaptics();
        UpdateAllUI();
    }

    private void OnEnable()
    {
        InputSystem.onDeviceChange += OnDeviceChange;
    }

    private void OnDisable()
    {
        InputSystem.onDeviceChange -= OnDeviceChange;
        SendZeroHaptics();

        if (audioSource)
            audioSource.Stop();
    }

    private void Update()
    {
        UpdateContactDebounce();

        switch (state)
        {
            case DemoState.Selecting:
                UpdateSelecting();
                break;

            case DemoState.Countdown:
                UpdateCountdown();
                break;

            case DemoState.Playing:
                UpdatePlaying();
                break;

            case DemoState.AwaitRelease:
                UpdateAwaitRelease();
                break;
        }

        lastStableContact = stableContact;
        UpdateAllUI();
    }

    // =====================================================================
    // Gamepad discovery
    // =====================================================================
    private void OnDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (device is Gamepad)
            RefreshGamepads();
    }

    [ContextMenu("Refresh Bracelet Gamepads")]
    public void RefreshGamepads()
    {
        user1Gamepad = GetGamepadByIndex(user1GamepadIndex);
        user2Gamepad = GetGamepadByIndex(user2GamepadIndex);

        if (user1Gamepad != null && user2Gamepad != null &&
            user1Gamepad.deviceId == user2Gamepad.deviceId)
        {
            Debug.LogError("User 1 と User 2 に同じGamepadが割り当てられています。");
            user2Gamepad = null;
        }

        UpdateAllUI();
    }

    private Gamepad GetGamepadByIndex(int index)
    {
        if (index < 0 || index >= Gamepad.all.Count)
            return null;

        return Gamepad.all[index];
    }

    [ContextMenu("Log Connected Gamepads")]
    public void LogConnectedGamepads()
    {
        Debug.Log($"Connected Gamepads: {Gamepad.all.Count}");

        for (int i = 0; i < Gamepad.all.Count; i++)
        {
            Gamepad g = Gamepad.all[i];
            Debug.Log(
                $"[{i}] deviceId={g.deviceId}, " +
                $"displayName={g.displayName}, " +
                $"product={g.description.product}, " +
                $"manufacturer={g.description.manufacturer}, " +
                $"serial={g.description.serial}");
        }
    }

    private bool DevicesReady()
    {
        return user1Gamepad != null && user2Gamepad != null;
    }

    // =====================================================================
    // Selection input
    // =====================================================================
    private void UpdateSelecting()
    {
        if (!DevicesReady())
            return;

        HandlePlayerInput(user1Gamepad, user1, ref user1LastInputTime);
        HandlePlayerInput(user2Gamepad, user2, ref user2LastInputTime);

        // 一度「非接触」を確認してから、次の接触をトリガとして受け付ける。
        if (!stableContact)
            contactArmed = true;

        bool contactRising = stableContact && !lastStableContact;

        if (contactRising && contactArmed && SelectionsMatch())
        {
            contactArmed = false;
            BeginCountdown();
        }
    }

    private void HandlePlayerInput(
        Gamepad pad,
        PlayerSelection selection,
        ref float lastInputTime)
    {
        if (pad == null)
            return;

        bool verticalPressed = pad.aButton.wasPressedThisFrame;
        bool horizontalPressed = pad.bButton.wasPressedThisFrame;

        if (!verticalPressed && !horizontalPressed)
            return;

        if (Time.unscaledTime - lastInputTime < inputGuardSeconds)
            return;

        // A: Process -> SE -> Audio -> Position -> Process ...
        if (verticalPressed)
        {
            selection.row = (SelectRow)(((int)selection.row + 1) % 4);
        }

        // B: 現在行の選択肢を横方向に循環
        if (horizontalPressed)
        {
            AdvanceHorizontal(selection);
        }

        lastInputTime = Time.unscaledTime;
    }

    private void AdvanceHorizontal(PlayerSelection selection)
    {
        switch (selection.row)
        {
            case SelectRow.Process:
                selection.process = (ProcessType)(((int)selection.process + 1) % 3);
                break;

            case SelectRow.SE:
                selection.se = (selection.se + 1) % 4;
                break;

            case SelectRow.Audio:
                selection.audio = (selection.audio + 1) % 2;
                break;

            case SelectRow.Position:
                selection.position = (selection.position + 1) % 7;
                break;
        }
    }

    private bool SelectionsMatch()
    {
        return user1.process == user2.process &&
               user1.se == user2.se &&
               user1.audio == user2.audio &&
               user1.position == user2.position;
    }

    // =====================================================================
    // Human-body communication / contact
    // =====================================================================
    private bool ReadRawContact()
    {
        Gamepad receiver = contactReceiverIsUser1 ? user1Gamepad : user2Gamepad;
        return receiver != null && receiver.xButton.isPressed;
    }

    private void UpdateContactDebounce()
    {
        rawContact = ReadRawContact();

        if (rawContact != contactCandidate)
        {
            contactCandidate = rawContact;
            contactCandidateChangedAt = Time.unscaledTime;
        }

        if (stableContact != contactCandidate &&
            Time.unscaledTime - contactCandidateChangedAt >= contactDebounceSeconds)
        {
            stableContact = contactCandidate;
        }
    }

    // =====================================================================
    // State machine
    // =====================================================================
    private void BeginCountdown()
    {
        state = DemoState.Countdown;
        countdownStartedAt = Time.unscaledTime;

        // 前回と同じ刺激でも次の非ゼロコマンドが確実に変化として送られるようにする。
        SendZeroHaptics();
    }

    private void UpdateCountdown()
    {
        // 3秒の間に手を離した、接続が切れた、条件が不一致になった場合は中止。
        if (!DevicesReady() || !stableContact || !SelectionsMatch())
        {
            CancelCountdown();
            return;
        }

        float elapsed = Time.unscaledTime - countdownStartedAt;
        if (elapsed >= countdownSeconds)
        {
            StartStimulus();
        }
    }

    private void CancelCountdown()
    {
        state = DemoState.Selecting;
        SendZeroHaptics();

        // 接触したまま再スタートしない。いったん手を離す必要がある。
        contactArmed = false;
    }

    private void StartStimulus()
    {
        if (!DevicesReady() || !SelectionsMatch())
        {
            CancelCountdown();
            return;
        }

        state = DemoState.Playing;

        int seIndex = user1.se;
        int positionIndex = user1.position;
        ProcessType process = user1.process;

        // 0..11
        // RAW A-D = 0..3
        // ISM A-D = 4..7
        // BPF A-D = 8..11
        int waveformId = ((int)process * 4) + seIndex;

        // 0は「リセット用」に空ける。
        // waveformId 0..11 -> 16,32,...,192
        byte waveformCommandByte = (byte)(16 * (waveformId + 1));
        float waveformCommand = waveformCommandByte / 255.0f;

        float x = Mathf.Clamp01(positionIndex / 6.0f);
        float user1Amplitude = 1.0f - x;
        float user2Amplitude = x;

        // Unity Input SystemのlowFrequency側を振幅、highFrequency側を波形コードとして使用。
        user1Gamepad.SetMotorSpeeds(user1Amplitude, waveformCommand);
        user2Gamepad.SetMotorSpeeds(user2Amplitude, waveformCommand);

        // Audio ONなら、Processによらず元のRAW音声を再生。
        if (audioSource)
        {
            audioSource.Stop();

            if (user1.audio == 0 &&
                headphoneClipsRaw != null &&
                seIndex >= 0 &&
                seIndex < headphoneClipsRaw.Length &&
                headphoneClipsRaw[seIndex] != null)
            {
                audioSource.clip = headphoneClipsRaw[seIndex];
                audioSource.Play();
            }
        }

        float duration = GetStimulusDurationSeconds(waveformId, seIndex);
        stimulusEndsAt = Time.unscaledTime + duration;

        if (zeroCommandCoroutine != null)
            StopCoroutine(zeroCommandCoroutine);

        zeroCommandCoroutine = StartCoroutine(ReturnHapticsToZeroAfterDelay());
    }

    private IEnumerator ReturnHapticsToZeroAfterDelay()
    {
        yield return new WaitForSecondsRealtime(commandHoldSeconds);
        SendZeroHaptics();
        zeroCommandCoroutine = null;
    }

    private void UpdatePlaying()
    {
        if (Time.unscaledTime >= stimulusEndsAt)
        {
            state = DemoState.AwaitRelease;
            SendZeroHaptics();
        }
    }

    private void UpdateAwaitRelease()
    {
        // 再生後は、2人が手を離すまで次の選択操作へ進まない。
        if (!stableContact)
        {
            state = DemoState.Selecting;
            contactArmed = true;

            // 次の試行はProcess行から操作開始。
            // 各条件値そのものは前回値を保持する。
            user1.row = SelectRow.Process;
            user2.row = SelectRow.Process;
        }
    }

    private float GetStimulusDurationSeconds(int waveformId, int seIndex)
    {
        // RAW/ISM/BPFで長さが同じ前提なら元音声長を使える。
        // 取得できない場合はdefaultStimulusDurationSeconds。
        if (headphoneClipsRaw != null &&
            seIndex >= 0 &&
            seIndex < headphoneClipsRaw.Length &&
            headphoneClipsRaw[seIndex] != null)
        {
            return Mathf.Max(0.1f, headphoneClipsRaw[seIndex].length);
        }

        return Mathf.Max(0.1f, defaultStimulusDurationSeconds);
    }

    private void SendZeroHaptics()
    {
        if (user1Gamepad != null)
            user1Gamepad.SetMotorSpeeds(0f, 0f);

        if (user2Gamepad != null)
            user2Gamepad.SetMotorSpeeds(0f, 0f);
    }

    // =====================================================================
    // Cursor UI
    // =====================================================================
    private void CreateCursorsIfNeeded()
    {
        if (!canvas)
        {
            Debug.LogError("Canvas が設定されていません。Cursorを作成できません。");
            return;
        }

        if (!user1Cursor)
            user1Cursor = CreateFrame("User1Cursor", user1CursorColor);

        if (!user2Cursor)
            user2Cursor = CreateFrame("User2Cursor", user2CursorColor);
    }

    private RectTransform CreateFrame(string objectName, Color color)
    {
        GameObject root = new GameObject(objectName, typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);

        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);

        CreateFrameLine(rootRect, "Top", color,
            new Vector2(0f, 1f), new Vector2(1f, 1f),
            new Vector2(0f, cursorLineWidth));

        CreateFrameLine(rootRect, "Bottom", color,
            new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(0f, cursorLineWidth));

        CreateFrameLine(rootRect, "Left", color,
            new Vector2(0f, 0f), new Vector2(0f, 1f),
            new Vector2(cursorLineWidth, 0f));

        CreateFrameLine(rootRect, "Right", color,
            new Vector2(1f, 0f), new Vector2(1f, 1f),
            new Vector2(cursorLineWidth, 0f));

        root.transform.SetAsLastSibling();
        return rootRect;
    }

    private void CreateFrameLine(
        RectTransform parent,
        string lineName,
        Color color,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 sizeDelta)
    {
        GameObject line = new GameObject(lineName, typeof(RectTransform), typeof(Image));
        line.transform.SetParent(parent, false);

        RectTransform rt = line.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = sizeDelta;

        Image image = line.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    private void UpdateAllUI()
    {
        UpdateCursor(user1Cursor, user1, user1CursorPadding);
        UpdateCursor(user2Cursor, user2, user2CursorPadding);
        UpdateStatusText();
        UpdateCountdownText();
    }

    private void UpdateCursor(RectTransform cursor, PlayerSelection selection, float padding)
    {
        if (!cursor || !canvas)
            return;

        RectTransform target = GetCurrentTarget(selection);
        if (!target)
        {
            cursor.gameObject.SetActive(false);
            return;
        }

        cursor.gameObject.SetActive(state == DemoState.Selecting);
        if (state != DemoState.Selecting)
            return;

        RectTransform canvasRect = canvas.transform as RectTransform;
        if (!canvasRect)
            return;

        cursor.position = target.position;
        cursor.rotation = target.rotation;

        float scaleX = Mathf.Abs(target.lossyScale.x / canvasRect.lossyScale.x);
        float scaleY = Mathf.Abs(target.lossyScale.y / canvasRect.lossyScale.y);

        cursor.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Horizontal,
            target.rect.width * scaleX + padding);

        cursor.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Vertical,
            target.rect.height * scaleY + padding);

        cursor.SetAsLastSibling();
    }

    private RectTransform GetCurrentTarget(PlayerSelection selection)
    {
        switch (selection.row)
        {
            case SelectRow.Process:
                return GetButtonRect(processButtons, (int)selection.process);

            case SelectRow.SE:
                return GetButtonRect(seButtons, selection.se);

            case SelectRow.Audio:
                return GetButtonRect(audioButtons, selection.audio);

            case SelectRow.Position:
                return GetButtonRect(positionButtons, selection.position);
        }

        return null;
    }

    private RectTransform GetButtonRect(Button[] buttons, int index)
    {
        if (buttons == null || index < 0 || index >= buttons.Length || buttons[index] == null)
            return null;

        return buttons[index].GetComponent<RectTransform>();
    }

    // =====================================================================
    // Status UI
    // =====================================================================
    private void UpdateStatusText()
    {
        if (!statusText)
            return;

        string stateText;
        switch (state)
        {
            case DemoState.Selecting:
                stateText = "Selecting";
                break;
            case DemoState.Countdown:
                stateText = "Countdown";
                break;
            case DemoState.Playing:
                stateText = "Playing";
                break;
            case DemoState.AwaitRelease:
                stateText = "Release hands";
                break;
            default:
                stateText = "-";
                break;
        }

        string p1Connected = user1Gamepad != null ? "Connected" : "Not found";
        string p2Connected = user2Gamepad != null ? "Connected" : "Not found";
        string match = SelectionsMatch() ? "YES" : "NO";
        string touch = stableContact ? "YES" : "NO";

        statusText.text =
            $"State:{stateText}\n" +
            $"P1 Row:{RowName(user1.row)}\n" +
            $"P1:{SelectionSummary(user1)}\n" +
            $"P2 Row:{RowName(user2.row)}\n" +
            $"P2:{SelectionSummary(user2)}\n" +
            $"Match:{match}\n" +
            $"Touch:{touch}\n" +
            $"P1:{p1Connected}\n" +
            $"P2:{p2Connected}";
    }

    private string SelectionSummary(PlayerSelection s)
    {
        char seChar = (char)('A' + s.se);
        string audioName = s.audio == 0 ? "ON" : "OFF";
        return $"{s.process}, {seChar}, {audioName}, Pos{s.position + 1}";
    }

    private string RowName(SelectRow row)
    {
        switch (row)
        {
            case SelectRow.Process: return "Process";
            case SelectRow.SE: return "SE";
            case SelectRow.Audio: return "Audio";
            case SelectRow.Position: return "Position";
            default: return "-";
        }
    }

    private void UpdateCountdownText()
    {
        if (!countdownText)
            return;

        if (state != DemoState.Countdown)
        {
            countdownText.text = "";
            return;
        }

        float remaining = countdownSeconds - (Time.unscaledTime - countdownStartedAt);
        int number = Mathf.Max(1, Mathf.CeilToInt(remaining));
        countdownText.text = number.ToString();
    }
}
*/


using System.Collections; //CoroutineをIEnumerator ReturnHapticsToZeroAfterDelay()で使うため
using UnityEngine;
using UnityEngine.InputSystem; //Gamepadの入力に必要
using UnityEngine.UI; //Unity UI用

[RequireComponent(typeof(AudioSource))] //このスクリプトが付いているGameObjectにはAudioSourceが必要という指定
public class SoundManager_DualBracelet : MonoBehaviour
{
    private enum ProcessType //Processの3種類を名前で管理
    {
        RAW = 0,
        ISM = 1,
        BPF = 2
    }

    private enum SelectRow //今カーソルがどの「行」にいるか
    {
        Process = 0,
        SE = 1,
        Audio = 2,
        Position = 3,
        Render = 4
    }

    private enum DemoState //システム全体の状態
    {
        Selecting,
        Countdown,
        Playing
    }

    private sealed class PlayerSelection //参加者1人分の状態をすべてまとめたクラス
    {
        //初期状態
        public SelectRow row = SelectRow.Process;
        public ProcessType process = ProcessType.RAW;

        // 0:A, 1:B, 2:C, 3:D
        public int se = 0;

        // 0:ON, 1:OFF
        public int audio = 0;

        // 0..6
        public int position = 0;

        public bool renderConfirmed = false;
    }

    // =====================================================================
    // Inspector: Headphone audio
    // =====================================================================

    [Header("Headphone Audio")]
    public AudioSource audioSource;

    [Tooltip(
        "A, B, C, D の元音声。Process が RAW / ISM / BPF のどれでも" +
        "ヘッドホンにはこの音声を使用する。")]
    public AudioClip[] headphoneClipsRaw = new AudioClip[4];

    // =====================================================================
    // Inspector: UI targets
    // =====================================================================

    [Header("UI: Process (RAW / ISM / BPF)")]
    public Button[] processButtons = new Button[3];

    [Header("UI: SE (A / B / C / D)")]
    public Button[] seButtons = new Button[4];

    [Header("UI: Audio (ON / OFF)")]
    public Button[] audioButtons = new Button[2];

    [Header("UI: Position (1..7)")]
    public Button[] positionButtons = new Button[7];

    [Header("UI: Render")]
    [Tooltip("Positionの下に配置するRenderボタン。")]
    public Button renderButton;

    [Header("UI: Text")]
    public Text statusText;
    public Text countdownText;

    [Tooltip(
        "以前の RAW_Active / ISM_Active / BPF_Active などを使用しない場合、" +
        "ここに登録すると起動時に非表示にする。")]
    public GameObject[] oldActiveImages;

    // =====================================================================
    // Inspector: Cursor appearance
    // =====================================================================

    [Header("Cursor Appearance")]
    public Canvas canvas;

    public Color user1CursorColor =
        new Color(0.0f, 0.65f, 1.0f, 1.0f);

    public Color user2CursorColor =
        new Color(1.0f, 0.2f, 0.55f, 1.0f);

    [Tooltip("現在操作中のカーソル枠の太さ。")]
    public float cursorLineWidth = 4.0f;

    public float user1CursorPadding = 8.0f;
    public float user2CursorPadding = 16.0f;

    // =====================================================================
    // Inspector: Selected frame appearance
    // =====================================================================

    [Header("Selected Frame Appearance")]

    [Tooltip(
        "選択済み枠の透明度。0で完全透明、1で操作カーソルと同じ濃さ。")]
    [Range(0.05f, 1.0f)]
    public float selectedFrameAlpha = 0.35f;

    [Tooltip("選択済み枠の線の太さ。")]
    public float selectedLineWidth = 3.0f;

    // =====================================================================
    // Inspector: Gamepad assignment
    // =====================================================================

    [Header("Bracelet Gamepad Assignment")] //UnityのGamepad.allに認識されたゲームパッドの割り当て

    [Tooltip("Gamepad.all の何番目を User 1 に使用するか。")]
    public int user1GamepadIndex = 0;

    [Tooltip("Gamepad.all の何番目を User 2 に使用するか。")]
    public int user2GamepadIndex = 1;

    // =====================================================================
    // Inspector: Timing
    // =====================================================================

    [Header("Timing")]

    [Tooltip(
        "2人ともRenderで決定してから刺激開始までのカウントダウン時間。")]
    public float countdownSeconds = 3.0f;

    [Tooltip("A/Bボタンの二重入力防止時間。")]
    public float inputGuardSeconds = 0.08f;

    [Tooltip(
        "SetMotorSpeedsで刺激コマンドを送った後、0へ戻すまでの時間。")]
    public float commandHoldSeconds = 0.06f; //UnityからSetMotorSpeeds(...)で命令を送ったあと、60 ms待ってSetMotorSpeeds(0f, 0f);へ戻す
    
    [Tooltip(
        "音声長が取得できない場合に使用する刺激時間。")]
    public float defaultStimulusDurationSeconds = 1.5f;

    // =====================================================================
    // Runtime state
    // =====================================================================

    private readonly PlayerSelection user1 = new PlayerSelection();
    private readonly PlayerSelection user2 = new PlayerSelection();

    private Gamepad user1Gamepad;
    private Gamepad user2Gamepad;

    // 操作用の濃いカーソル
    private RectTransform user1Cursor;
    private RectTransform user2Cursor;

    // 選択済み状態を示す薄い枠
    // 0:Process, 1:SE, 2:Audio, 3:Position
    private readonly RectTransform[] user1SelectedFrames =
        new RectTransform[4];

    private readonly RectTransform[] user2SelectedFrames =
        new RectTransform[4];

    private DemoState state = DemoState.Selecting;

    private float user1LastInputTime = -100f;
    private float user2LastInputTime = -100f;

    private float countdownStartedAt;
    private float stimulusEndsAt;

    private Coroutine zeroCommandCoroutine;

    // =====================================================================
    // Unity lifecycle
    // =====================================================================

    private void Awake()
    {
        if (!audioSource)
            audioSource = GetComponent<AudioSource>();

        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake = false;

        if (!canvas)
            canvas = GetComponentInParent<Canvas>();

        if (renderButton)
            renderButton.gameObject.SetActive(true);

        if (oldActiveImages != null)
        {
            foreach (GameObject obj in oldActiveImages)
            {
                if (obj)
                    obj.SetActive(false);
            }
        }

        CreateFramesIfNeeded();

        RefreshGamepads();
        SendZeroHaptics();
        UpdateAllUI();
    }

    private void OnEnable()
    {
        InputSystem.onDeviceChange += OnDeviceChange;
    }

    private void OnDisable()
    {
        InputSystem.onDeviceChange -= OnDeviceChange;

        SendZeroHaptics();

        if (audioSource)
            audioSource.Stop();
    }

    private void Update()
    {
        switch (state)
        {
            case DemoState.Selecting:
                UpdateSelecting();
                break;

            case DemoState.Countdown:
                UpdateCountdown();
                break;

            case DemoState.Playing:
                UpdatePlaying();
                break;
        }

        UpdateAllUI();
    }

    // =====================================================================
    // Gamepad discovery
    // =====================================================================

    private void OnDeviceChange(
        InputDevice device,
        InputDeviceChange change)
    {
        if (device is Gamepad)
            RefreshGamepads();
    }

    [ContextMenu("Refresh Bracelet Gamepads")]
    public void RefreshGamepads()
    {
        user1Gamepad =
            GetGamepadByIndex(user1GamepadIndex);

        user2Gamepad =
            GetGamepadByIndex(user2GamepadIndex);

        if (user1Gamepad != null &&
            user2Gamepad != null &&
            user1Gamepad.deviceId == user2Gamepad.deviceId)
        {
            Debug.LogError(
                "User 1 と User 2 に同じGamepadが割り当てられています。");

            user2Gamepad = null;
        }

        UpdateAllUI();
    }

    private Gamepad GetGamepadByIndex(int index)
    {
        if (index < 0 || index >= Gamepad.all.Count)
            return null;

        return Gamepad.all[index];
    }

    [ContextMenu("Log Connected Gamepads")]
    public void LogConnectedGamepads()
    {
        Debug.Log($"Connected Gamepads: {Gamepad.all.Count}");

        for (int i = 0; i < Gamepad.all.Count; i++)
        {
            Gamepad g = Gamepad.all[i];

            Debug.Log(
                $"[{i}] " +
                $"deviceId={g.deviceId}, " +
                $"displayName={g.displayName}, " +
                $"product={g.description.product}, " +
                $"manufacturer={g.description.manufacturer}, " +
                $"serial={g.description.serial}");
        }
    }

    private bool DevicesReady()
    {
        return
            user1Gamepad != null &&
            user2Gamepad != null;
    }

    // =====================================================================
    // Selecting state
    // =====================================================================

    private void UpdateSelecting()
    {
        if (!DevicesReady())
            return;

        HandlePlayerInput(
            user1Gamepad,
            user1,
            ref user1LastInputTime);

        HandlePlayerInput(
            user2Gamepad,
            user2,
            ref user2LastInputTime);

        TryBeginCountdown();
    }

    // =====================================================================
    // Player input
    // =====================================================================

    private void HandlePlayerInput(
        Gamepad pad,
        PlayerSelection selection,
        ref float lastInputTime)
    {
        if (pad == null)
            return;

        bool verticalPressed =
            pad.aButton.wasPressedThisFrame;

        bool horizontalPressed =
            pad.bButton.wasPressedThisFrame;

        if (!verticalPressed &&
            !horizontalPressed)
            return;

        if (Time.unscaledTime - lastInputTime <
            inputGuardSeconds)
            return;

        // A:
        // Process -> SE -> Audio -> Position -> Render -> Process ...
        if (verticalPressed)
        {
            selection.row =
                (SelectRow)(((int)selection.row + 1) % 5);

            ClearRenderConfirmations();

            lastInputTime =
                Time.unscaledTime;

            return;
        }

        // B
        if (horizontalPressed)
        {
            if (selection.row == SelectRow.Render)
            {
                selection.renderConfirmed = true;

                Debug.Log(
                    selection == user1
                        ? "User 1 confirmed Render."
                        : "User 2 confirmed Render.");
            }
            else
            {
                AdvanceHorizontal(selection);

                // 選択内容が変化したので
                // Render確定を解除
                ClearRenderConfirmations();
            }

            lastInputTime =
                Time.unscaledTime;
        }
    }

    // =====================================================================
    // Horizontal selection
    // =====================================================================

    private void AdvanceHorizontal(
        PlayerSelection selection)
    {
        switch (selection.row)
        {
            case SelectRow.Process:

                selection.process =
                    (ProcessType)(
                        ((int)selection.process + 1) % 3);

                break;

            case SelectRow.SE:

                selection.se =
                    (selection.se + 1) % 4;

                break;

            case SelectRow.Audio:

                selection.audio =
                    (selection.audio + 1) % 2;

                break;

            case SelectRow.Position:

                selection.position =
                    (selection.position + 1) % 7;

                break;
        }
    }

    // =====================================================================
    // Render confirmation
    // =====================================================================

    private void ClearRenderConfirmations()
    {
        user1.renderConfirmed = false;
        user2.renderConfirmed = false;
    }

    private bool BothPlayersOnRender()
    {
        return
            user1.row == SelectRow.Render &&
            user2.row == SelectRow.Render;
    }

    private bool BothPlayersConfirmedRender()
    {
        return
            user1.renderConfirmed &&
            user2.renderConfirmed;
    }

    private void TryBeginCountdown()
    {
        if (!DevicesReady())
            return;

        if (!BothPlayersOnRender())
            return;

        if (!BothPlayersConfirmedRender())
            return;

        if (!SelectionsMatch())
            return;

        BeginCountdown();
    }

    // =====================================================================
    // Selection matching
    // =====================================================================

    private bool SelectionsMatch()
    {
        return
            user1.process == user2.process &&
            user1.se == user2.se &&
            user1.audio == user2.audio &&
            user1.position == user2.position;
    }

    // =====================================================================
    // State machine
    // =====================================================================

    private void BeginCountdown()
    {
        if (!DevicesReady())
            return;

        if (!SelectionsMatch())
            return;

        if (!BothPlayersOnRender())
            return;

        if (!BothPlayersConfirmedRender())
            return;

        state = DemoState.Countdown;
        countdownStartedAt = Time.unscaledTime;

        SendZeroHaptics();

        Debug.Log(
            "Both players confirmed Render. Countdown started.");
    }

    private void UpdateCountdown()
    {
        if (!DevicesReady())
        {
            CancelCountdown();
            return;
        }

        if (!SelectionsMatch())
        {
            CancelCountdown();
            return;
        }

        float elapsed =
            Time.unscaledTime -
            countdownStartedAt;

        if (elapsed >= countdownSeconds)
            StartStimulus();
    }

    private void CancelCountdown()
    {
        state = DemoState.Selecting;

        SendZeroHaptics();
        ClearRenderConfirmations();

        Debug.Log("Countdown cancelled.");
    }

    // =====================================================================
    // Stimulus
    // =====================================================================

    private void StartStimulus()
    {
        if (!DevicesReady() ||
            !SelectionsMatch())
        {
            CancelCountdown();
            return;
        }

        state = DemoState.Playing;

        int seIndex = user1.se;
        int positionIndex = user1.position;
        ProcessType process = user1.process;

        // RAW A-D = 0..3
        // ISM A-D = 4..7
        // BPF A-D = 8..11
        int waveformId =
            ((int)process * 4) +
            seIndex;

        // Xbox BLEのmotor magnitudeは0..100。
        // waveformId 0..11 -> 5,10,...,60
        int waveformCommandValue =
            5 * (waveformId + 1);

        float waveformCommand =
            waveformCommandValue / 100.0f;

        // Position
        float x =
            Mathf.Clamp01(
                positionIndex / 6.0f);

        float user1Amplitude =
            1.0f - x;

        float user2Amplitude =
            x;

        Debug.Log(
            $"Stimulus: " +
            $"Process={process}, " +
            $"SE={(char)('A' + seIndex)}, " +
            $"Position={positionIndex + 1}, " +
            $"WaveformID={waveformId}, " +
            $"P1Amp={user1Amplitude:F3}, " +
            $"P2Amp={user2Amplitude:F3}");

        user1Gamepad.SetMotorSpeeds(
            user1Amplitude,
            waveformCommand);

        user2Gamepad.SetMotorSpeeds(
            user2Amplitude,
            waveformCommand);

        // Audio
        if (audioSource)
        {
            audioSource.Stop();

            if (user1.audio == 0 &&
                headphoneClipsRaw != null &&
                seIndex >= 0 &&
                seIndex < headphoneClipsRaw.Length &&
                headphoneClipsRaw[seIndex] != null)
            {
                audioSource.clip =
                    headphoneClipsRaw[seIndex];

                audioSource.Play();
            }
        }

        float duration =
            GetStimulusDurationSeconds(
                waveformId,
                seIndex);

        stimulusEndsAt =
            Time.unscaledTime +
            duration;

        if (zeroCommandCoroutine != null)
            StopCoroutine(zeroCommandCoroutine);

        zeroCommandCoroutine =
            StartCoroutine(
                ReturnHapticsToZeroAfterDelay());
    }

    private IEnumerator ReturnHapticsToZeroAfterDelay()
    {
        yield return
            new WaitForSecondsRealtime(
                commandHoldSeconds);

        SendZeroHaptics();

        zeroCommandCoroutine = null;
    }

    private void UpdatePlaying()
    {
        if (Time.unscaledTime >= stimulusEndsAt)
            FinishTrial();
    }

    private void FinishTrial()
    {
        SendZeroHaptics();

        if (audioSource)
            audioSource.Stop();

        state = DemoState.Selecting;

        // 次の試行はProcessから開始
        user1.row = SelectRow.Process;
        user2.row = SelectRow.Process;

        // 選択値は保持する
        ClearRenderConfirmations();

        Debug.Log(
            "Stimulus finished. Ready for next trial.");
    }

    // =====================================================================
    // Stimulus duration
    // =====================================================================

    private float GetStimulusDurationSeconds(
        int waveformId,
        int seIndex)
    {
        if (headphoneClipsRaw != null &&
            seIndex >= 0 &&
            seIndex < headphoneClipsRaw.Length &&
            headphoneClipsRaw[seIndex] != null)
        {
            return Mathf.Max(
                0.1f,
                headphoneClipsRaw[seIndex].length);
        }

        return Mathf.Max(
            0.1f,
            defaultStimulusDurationSeconds);
    }

    // =====================================================================
    // Haptic reset
    // =====================================================================

    private void SendZeroHaptics()
    {
        if (user1Gamepad != null)
        {
            user1Gamepad.SetMotorSpeeds(
                0f,
                0f);
        }

        if (user2Gamepad != null)
        {
            user2Gamepad.SetMotorSpeeds(
                0f,
                0f);
        }
    }

    // =====================================================================
    // Frame creation
    // =====================================================================

    private void CreateFramesIfNeeded()
    {
        if (!canvas)
        {
            Debug.LogError(
                "Canvas が設定されていません。Frameを作成できません。");

            return;
        }

        // -------------------------------------------------------------
        // 操作用カーソル
        // -------------------------------------------------------------

        if (!user1Cursor)
        {
            user1Cursor =
                CreateFrame(
                    "User1Cursor",
                    user1CursorColor,
                    cursorLineWidth);
        }

        if (!user2Cursor)
        {
            user2Cursor =
                CreateFrame(
                    "User2Cursor",
                    user2CursorColor,
                    cursorLineWidth);
        }

        // -------------------------------------------------------------
        // 選択済み枠
        // Process / SE / Audio / Position の4つ
        // -------------------------------------------------------------

        Color user1SelectedColor =
            GetSelectedFrameColor(
                user1CursorColor);

        Color user2SelectedColor =
            GetSelectedFrameColor(
                user2CursorColor);

        for (int i = 0; i < 4; i++)
        {
            SelectRow row =
                (SelectRow)i;

            if (!user1SelectedFrames[i])
            {
                user1SelectedFrames[i] =
                    CreateFrame(
                        $"User1Selected_{row}",
                        user1SelectedColor,
                        selectedLineWidth);
            }

            if (!user2SelectedFrames[i])
            {
                user2SelectedFrames[i] =
                    CreateFrame(
                        $"User2Selected_{row}",
                        user2SelectedColor,
                        selectedLineWidth);
            }
        }
    }

    private Color GetSelectedFrameColor(
        Color cursorColor)
    {
        Color result = cursorColor;

        result.a =
            Mathf.Clamp01(
                cursorColor.a *
                selectedFrameAlpha);

        return result;
    }

    private RectTransform CreateFrame(
        string objectName,
        Color color,
        float lineWidth)
    {
        GameObject root =
            new GameObject(
                objectName,
                typeof(RectTransform));

        root.transform.SetParent(
            canvas.transform,
            false);

        RectTransform rootRect =
            root.GetComponent<RectTransform>();

        rootRect.anchorMin =
            new Vector2(0.5f, 0.5f);

        rootRect.anchorMax =
            new Vector2(0.5f, 0.5f);

        rootRect.pivot =
            new Vector2(0.5f, 0.5f);

        CreateFrameLine(
            rootRect,
            "Top",
            color,
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(
                0f,
                lineWidth));

        CreateFrameLine(
            rootRect,
            "Bottom",
            color,
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(
                0f,
                lineWidth));

        CreateFrameLine(
            rootRect,
            "Left",
            color,
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(
                lineWidth,
                0f));

        CreateFrameLine(
            rootRect,
            "Right",
            color,
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(
                lineWidth,
                0f));

        root.transform.SetAsLastSibling();

        return rootRect;
    }

    private void CreateFrameLine(
        RectTransform parent,
        string lineName,
        Color color,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 sizeDelta)
    {
        GameObject line =
            new GameObject(
                lineName,
                typeof(RectTransform),
                typeof(Image));

        line.transform.SetParent(
            parent,
            false);

        RectTransform rt =
            line.GetComponent<RectTransform>();

        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = sizeDelta;

        Image image =
            line.GetComponent<Image>();

        image.color = color;

        // UI操作を邪魔しない
        image.raycastTarget = false;
    }

    // =====================================================================
    // UI update
    // =====================================================================

    private void UpdateAllUI()
    {
        // 先に薄い選択済み枠を更新
        UpdateSelectedFrames(
            user1SelectedFrames,
            user1,
            user1CursorPadding);

        UpdateSelectedFrames(
            user2SelectedFrames,
            user2,
            user2CursorPadding);

        // その後に濃い操作カーソルを描画することで
        // 操作カーソルを最前面にする
        UpdateCursor(
            user1Cursor,
            user1,
            user1CursorPadding);

        UpdateCursor(
            user2Cursor,
            user2,
            user2CursorPadding);

        UpdateStatusText();
        UpdateCountdownText();
    }

    // =====================================================================
    // Selected frames
    // =====================================================================

    private void UpdateSelectedFrames(
        RectTransform[] frames,
        PlayerSelection selection,
        float padding)
    {
        if (frames == null)
            return;

        // Renderを除く4項目
        for (int i = 0; i < 4; i++)
        {
            if (i >= frames.Length ||
                !frames[i])
                continue;

            SelectRow row =
                (SelectRow)i;

            RectTransform target =
                GetSelectedTarget(
                    selection,
                    row);

            PositionFrameOnTarget(
                frames[i],
                target,
                padding,
                true);
        }
    }

    private RectTransform GetSelectedTarget(
        PlayerSelection selection,
        SelectRow row)
    {
        switch (row)
        {
            case SelectRow.Process:

                return GetButtonRect(
                    processButtons,
                    (int)selection.process);

            case SelectRow.SE:

                return GetButtonRect(
                    seButtons,
                    selection.se);

            case SelectRow.Audio:

                return GetButtonRect(
                    audioButtons,
                    selection.audio);

            case SelectRow.Position:

                return GetButtonRect(
                    positionButtons,
                    selection.position);

            default:
                return null;
        }
    }

    // =====================================================================
    // Current cursor
    // =====================================================================

    private void UpdateCursor(
        RectTransform cursor,
        PlayerSelection selection,
        float padding)
    {
        if (!cursor || !canvas)
            return;

        RectTransform target =
            GetCurrentTarget(selection);

        bool showCursor =
            state == DemoState.Selecting &&
            target != null;

        PositionFrameOnTarget(
            cursor,
            target,
            padding,
            showCursor);
    }

    // =====================================================================
    // Common frame positioning
    // =====================================================================

    private void PositionFrameOnTarget(
        RectTransform frame,
        RectTransform target,
        float padding,
        bool visible)
    {
        if (!frame || !canvas)
            return;

        if (!visible || !target)
        {
            frame.gameObject.SetActive(false);
            return;
        }

        frame.gameObject.SetActive(true);

        RectTransform canvasRect =
            canvas.transform as RectTransform;

        if (!canvasRect)
            return;

        frame.position =
            target.position;

        frame.rotation =
            target.rotation;

        float scaleX =
            Mathf.Abs(
                target.lossyScale.x /
                canvasRect.lossyScale.x);

        float scaleY =
            Mathf.Abs(
                target.lossyScale.y /
                canvasRect.lossyScale.y);

        frame.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Horizontal,
            target.rect.width *
            scaleX +
            padding);

        frame.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Vertical,
            target.rect.height *
            scaleY +
            padding);

        frame.SetAsLastSibling();
    }

    private RectTransform GetCurrentTarget(
        PlayerSelection selection)
    {
        switch (selection.row)
        {
            case SelectRow.Process:

                return GetButtonRect(
                    processButtons,
                    (int)selection.process);

            case SelectRow.SE:

                return GetButtonRect(
                    seButtons,
                    selection.se);

            case SelectRow.Audio:

                return GetButtonRect(
                    audioButtons,
                    selection.audio);

            case SelectRow.Position:

                return GetButtonRect(
                    positionButtons,
                    selection.position);

            case SelectRow.Render:

                if (renderButton)
                {
                    return renderButton
                        .GetComponent<RectTransform>();
                }

                return null;
        }

        return null;
    }

    private RectTransform GetButtonRect(
        Button[] buttons,
        int index)
    {
        if (buttons == null ||
            index < 0 ||
            index >= buttons.Length ||
            buttons[index] == null)
        {
            return null;
        }

        return buttons[index]
            .GetComponent<RectTransform>();
    }

    // =====================================================================
    // Status UI
    // =====================================================================

    private void UpdateStatusText()
    {
        if (!statusText)
            return;

        string stateText;

        switch (state)
        {
            case DemoState.Selecting:
                stateText = "Selecting";
                break;

            case DemoState.Countdown:
                stateText = "Countdown";
                break;

            case DemoState.Playing:
                stateText = "Playing";
                break;

            default:
                stateText = "-";
                break;
        }

        string p1Connected =
            user1Gamepad != null
                ? "Connected"
                : "Not found";

        string p2Connected =
            user2Gamepad != null
                ? "Connected"
                : "Not found";

        string match =
            SelectionsMatch()
                ? "YES"
                : "NO";

        string p1Ready =
            user1.renderConfirmed
                ? "READY"
                : "WAIT";

        string p2Ready =
            user2.renderConfirmed
                ? "READY"
                : "WAIT";

        statusText.text =
            $"State:{stateText}\n" +
            $"P1 Row:{RowName(user1.row)}\n" +
            $"P1:{SelectionSummary(user1)}\n" +
            $"P1 Render:{p1Ready}\n" +
            $"P2 Row:{RowName(user2.row)}\n" +
            $"P2:{SelectionSummary(user2)}\n" +
            $"P2 Render:{p2Ready}\n" +
            $"Match:{match}\n" +
            $"P1:{p1Connected}\n" +
            $"P2:{p2Connected}";
    }

    private string SelectionSummary(
        PlayerSelection s)
    {
        char seChar =
            (char)('A' + s.se);

        string audioName =
            s.audio == 0
                ? "ON"
                : "OFF";

        return
            $"{s.process}, " +
            $"{seChar}, " +
            $"{audioName}, " +
            $"Pos{s.position + 1}";
    }

    private string RowName(
        SelectRow row)
    {
        switch (row)
        {
            case SelectRow.Process:
                return "Process";

            case SelectRow.SE:
                return "SE";

            case SelectRow.Audio:
                return "Audio";

            case SelectRow.Position:
                return "Position";

            case SelectRow.Render:
                return "Render";

            default:
                return "-";
        }
    }

    // =====================================================================
    // Countdown UI
    // =====================================================================

    private void UpdateCountdownText()
    {
        if (!countdownText)
            return;

        if (state != DemoState.Countdown)
        {
            countdownText.text = "";
            return;
        }

        float remaining =
            countdownSeconds -
            (Time.unscaledTime -
             countdownStartedAt);

        int number =
            Mathf.Max(
                1,
                Mathf.CeilToInt(remaining));

        countdownText.text =
            number.ToString();
    }
}