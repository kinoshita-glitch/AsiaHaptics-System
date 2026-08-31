using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[RequireComponent(typeof(AudioSource))]
public class SoundManager_DualBracelet : MonoBehaviour
{
    private enum ProcessType { RAW = 0, ISM = 1, BPF = 2 }
    private enum SelectRow { Process = 0, SE = 1, Audio = 2, Position = 3 }
    private enum DemoState { Selecting, Playing }

    private sealed class SelectionData
    {
        public ProcessType process = ProcessType.RAW;
        public int se = 0;
        public int audio = 0;
        public int position = 0;
    }

    [Header("Headphone Audio")]
    public AudioSource audioSource;
    public AudioClip[] headphoneClipsRaw = new AudioClip[4];

    [Header("UI: Process (RAW / ISM / BPF)")] public Button[] processButtons = new Button[3];
    [Header("UI: SE (A / B / C / D)")] public Button[] seButtons = new Button[4];
    [Header("UI: Audio (ON / OFF)")] public Button[] audioButtons = new Button[2];
    [Header("UI: Position (1..7)")] public Button[] positionButtons = new Button[7];
    [Header("UI: Render")] public Button renderButton;
    [Header("UI: Text")] public Text statusText;
    public GameObject[] oldActiveImages;

    [Header("Selected Frame Appearance")]
    public Canvas canvas;
    public Color frameColor = new Color(0.0f, 0.65f, 1.0f, 1.0f);
    [Range(0.05f, 1.0f)] public float frameAlpha = 0.5f;
    public float frameLineWidth = 3.0f;
    public float framePadding = 8.0f;

    [Header("Bracelet Gamepad Assignment")]
    public int leftGamepadIndex = 0;
    public int rightGamepadIndex = 1;

    [Header("Timing")]
    public float commandHoldSeconds = 0.06f; // 元のコード通り0.06秒に戻しました
    public float defaultStimulusDurationSeconds = 1.5f;

    private readonly SelectionData selection = new SelectionData();
    private Gamepad leftGamepad;
    private Gamepad rightGamepad;
    private readonly RectTransform[] selectedFrames = new RectTransform[4];
    private DemoState state = DemoState.Selecting;

    private float stimulusEndsAt;
    private Coroutine zeroCommandCoroutine;

    private void Awake()
    {
        Application.runInBackground = true;
        if (!audioSource) audioSource = GetComponent<AudioSource>();
        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake = false;
        if (!canvas) canvas = GetComponentInParent<Canvas>();
        if (oldActiveImages != null) foreach (GameObject obj in oldActiveImages) if (obj) obj.SetActive(false);

        CreateFramesIfNeeded();
        WireUI();
        RefreshGamepads();
        SendZeroHaptics();
        UpdateAllUI();
    }

    private void OnEnable() { InputSystem.onDeviceChange += OnDeviceChange; }
    private void OnDisable() { InputSystem.onDeviceChange -= OnDeviceChange; SendZeroHaptics(); if (audioSource) audioSource.Stop(); }

    private void Update()
    {
        if (state == DemoState.Playing && Time.unscaledTime >= stimulusEndsAt)
        {
            FinishTrial();
        }
    }

    private void WireUI()
    {
        for (int i = 0; i < processButtons.Length; i++) { int idx = i; if (processButtons[i]) processButtons[i].onClick.AddListener(() => { selection.process = (ProcessType)idx; UpdateAllUI(); }); }
        for (int i = 0; i < seButtons.Length; i++) { int idx = i; if (seButtons[i]) seButtons[i].onClick.AddListener(() => { selection.se = idx; UpdateAllUI(); }); }
        for (int i = 0; i < audioButtons.Length; i++) { int idx = i; if (audioButtons[i]) audioButtons[i].onClick.AddListener(() => { selection.audio = idx; UpdateAllUI(); }); }
        for (int i = 0; i < positionButtons.Length; i++) { int idx = i; if (positionButtons[i]) positionButtons[i].onClick.AddListener(() => { selection.position = idx; UpdateAllUI(); }); }
        if (renderButton) renderButton.onClick.AddListener(OnClickRender);
    }

    private void OnDeviceChange(InputDevice device, InputDeviceChange change) { if (device is Gamepad) RefreshGamepads(); }

    [ContextMenu("Refresh Bracelet Gamepads")]
    public void RefreshGamepads()
    {
        leftGamepad = GetGamepadByIndex(leftGamepadIndex);
        rightGamepad = GetGamepadByIndex(rightGamepadIndex);
        UpdateAllUI();
    }

    private Gamepad GetGamepadByIndex(int index) { return (index < 0 || index >= Gamepad.all.Count) ? null : Gamepad.all[index]; }

    public void OnClickRender()
    {
        if (state == DemoState.Playing) return;
        // 解決策：マウスクリックのUIイベントループから切り離し、1フレーム待機してから振動させる
        StartCoroutine(ExecuteStimulusRoutine());
    }

    private IEnumerator ExecuteStimulusRoutine()
    {
        state = DemoState.Playing;
        InputSystem.ResumeHaptics();

        // UIイベント（マウス操作）の影響を断ち切るために1フレーム待つ
        yield return null;

        int seIndex = selection.se;
        int positionIndex = selection.position;
        ProcessType process = selection.process;

        int waveformId = ((int)process * 4) + seIndex;
        int waveformCommandValue = 5 * (waveformId + 1);
        float waveformCommand = waveformCommandValue / 100.0f;

        float x = Mathf.Clamp01(positionIndex / 6.0f);
        float leftAmplitude = 1.0f - x;
        float rightAmplitude = x;

        Debug.Log($"[Haptics] Play -> ID:{waveformId}, L_Amp:{leftAmplitude:F2}, R_Amp:{rightAmplitude:F2}, Cmd:{waveformCommand:F2}");

        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(leftAmplitude, waveformCommand);
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(rightAmplitude, waveformCommand);

        if (audioSource)
        {
            audioSource.Stop();
            if (selection.audio == 0 && headphoneClipsRaw != null && seIndex >= 0 && seIndex < headphoneClipsRaw.Length && headphoneClipsRaw[seIndex] != null)
            {
                audioSource.clip = headphoneClipsRaw[seIndex];
                audioSource.Play();
            }
        }

        stimulusEndsAt = Time.unscaledTime + GetStimulusDurationSeconds(waveformId, seIndex);

        if (zeroCommandCoroutine != null) StopCoroutine(zeroCommandCoroutine);
        zeroCommandCoroutine = StartCoroutine(ReturnHapticsToZeroAfterDelay());

        UpdateAllUI();
    }

    private IEnumerator ReturnHapticsToZeroAfterDelay()
    {
        // 元のコード通りの待機時間でリセット
        yield return new WaitForSecondsRealtime(commandHoldSeconds);
        SendZeroHaptics();
        zeroCommandCoroutine = null;
    }

    private void FinishTrial()
    {
        SendZeroHaptics();
        if (audioSource) audioSource.Stop();
        state = DemoState.Selecting;
        UpdateAllUI();
    }

    private float GetStimulusDurationSeconds(int waveformId, int seIndex)
    {
        if (headphoneClipsRaw != null && seIndex >= 0 && seIndex < headphoneClipsRaw.Length && headphoneClipsRaw[seIndex] != null)
            return Mathf.Max(0.1f, headphoneClipsRaw[seIndex].length);
        return Mathf.Max(0.1f, defaultStimulusDurationSeconds);
    }

    private void SendZeroHaptics()
    {
        if (leftGamepad != null) leftGamepad.SetMotorSpeeds(0f, 0f);
        if (rightGamepad != null) rightGamepad.SetMotorSpeeds(0f, 0f);
    }

    private void UpdateAllUI()
    {
        UpdateSelectedFrames();
        UpdateStatusText();
        if (renderButton) renderButton.interactable = (state == DemoState.Selecting);
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
            if (i >= selectedFrames.Length || !selectedFrames[i]) continue;
            RectTransform target = GetSelectedTarget((SelectRow)i);
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
        string lConnected = leftGamepad != null ? "Connected" : "Not found";
        string rConnected = rightGamepad != null ? "Connected" : "Not found";
        char seChar = (char)('A' + selection.se);
        string audioName = selection.audio == 0 ? "ON" : "OFF";

        statusText.text = $"State: {state}\n" +
                          $"Selected: {selection.process}, {seChar}, {audioName}, Pos{selection.position + 1}\n" +
                          $"Left (Idx {leftGamepadIndex}): {lConnected}\n" +
                          $"Right (Idx {rightGamepadIndex}): {rConnected}";
    }
}