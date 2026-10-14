/**
 * XIAO ESP32S3 Plus
 *
 * BLE Xbox Gamepad (Receiver Only)
 * + FFat waveform playback
 * + DAC121S101
 */

#include <Arduino.h>
#include <SPI.h>
#include <FS.h>
#include <FFat.h> //FFatはESP32のFlash Memory上にFAT形式のファイルシステムを作る機能．.bin波形をここへ保存
#include "sfx_audio_config.h" //自作ヘッダ．各波形のパス，サンプル数，長さ，ファイルサイズなどが定義

#include <BleCompositeHID.h> //ESP32をBLE XboxゲームパッドとしてPCへ見せるためのライブラリ
#include <XboxGamepadDevice.h>

#include <esp_timer.h> //高精度タイマー．10 kHz DAC再生タイミングに利用
#include <esp_sleep.h>
#include <esp_heap_caps.h> //PSRAMへ波形を配置するために使う

// ================================================================
// Device setting
// ================================================================

#define DEVICE_ID 0 //ブレスレットの識別番号

#define STRINGIFY(x) #x
#define TOSTRING(x) STRINGIFY(x)
#define DEVICE_NAME "ETGame " TOSTRING(DEVICE_ID) //BLEデバイス名

#define DEBUG_ENABLED 1 //デバッグ表示ON

#if DEBUG_ENABLED
#define DBG(...) Serial.printf(__VA_ARGS__)
#define DBGLN(x) Serial.println(x)
#else
#define DBG(...) ((void)0)
#define DBGLN(x) ((void)0)
#endif

// ================================================================
// Pins
// ================================================================

#define SPI_SCLK 7 //SCLK = Clock
#define SPI_SDO  8 //Serial Data Out側
#define SPI_SDI  9 //Serial Data In側

#define IMU_CS 38 //CSはChip Select．どのSPI機器と通信するか選択
#define DAC_CS 39

#define AMP_SD 12 //アンプのShutdown制御ピン．HIGHで有効，LOWで停止

#define LED_CON    11 //接続状態
#define LED_VIB_EN 41 //振動回路有効
#define LED_VIB    40 //振動中

#define PIN_SLEEP   6 //電源/スリープスイッチ用
#define BATTERY_PIN 10 //バッテリー電圧をADCで測るピン

// ================================================================
// Audio
// ================================================================

constexpr uint32_t SAMPLE_RATE_HZ = SFX_AUDIO_SAMPLE_RATE_HZ;
constexpr uint32_t DAC_TIMER_US = 1000000UL / SAMPLE_RATE_HZ; //10 kHzなら100 µsごとに1サンプル送る
constexpr uint32_t DAC_SPI_HZ = 8000000; //DACとのSPI通信速度8 MHz
constexpr uint16_t IDLE_DAC_CODE = SFX_AUDIO_CENTER_VALUE & 0x0FFF; //DACの中心値
constexpr uint8_t DEFAULT_VOLUME = 100; //初期音量100%
constexpr uint8_t VIB_MIN_THRESH = 3; //振幅3未満は実質0
constexpr uint32_t PLAYBACK_TIMEOUT_MS = 120000; //異常時でも120秒で停止
constexpr int WAVEFORM_COUNT = 24; //波形は24種類

static_assert(SFX_AUDIO_CLIP_COUNT >= WAVEFORM_COUNT, "sfx_audio_config.h needs at least 24 clips.");

SPISettings dacSpiSettings(DAC_SPI_HZ, MSBFIRST, SPI_MODE1);

// ================================================================
// Loaded waveform
// ================================================================

struct LoadedClip {
    uint8_t* data = nullptr; //PSRAM上に配置された波形データの先頭アドレス
    size_t sizeBytes = 0;
    bool loaded = false;
};

LoadedClip clips[WAVEFORM_COUNT];

// ================================================================
// Playback state
// ================================================================

SemaphoreHandle_t spiMutex = nullptr;
TaskHandle_t dacTaskHandle = nullptr;
esp_timer_handle_t dacTimer = nullptr;

//volatileは別Taskなどから突然変更される可能性がある変数に付ける
volatile const uint8_t* playData = nullptr;
volatile size_t playSize = 0;
volatile size_t playPos = 0;
volatile uint8_t playAmplitude = 0;
volatile bool playing = false;
volatile bool playbackFinished = false;
volatile uint32_t playbackStartedMs = 0;
volatile int currentWaveformId = -1;

uint8_t masterVolume = DEFAULT_VOLUME;

// ================================================================
// BLE command
// ================================================================

/*
 * Unity → ESP32
 * 0       : 次の命令を受け付け可能にする
 * 4～96   : waveform ID 0～23
 * 100     : STOP
 */
constexpr uint8_t STOP_COMMAND = 100;
constexpr int STOP_REQUEST_ID = -2; //内部処理用STOP ID

struct BleCommand {
    int waveformId;
    uint8_t amplitude;
};

portMUX_TYPE bleMux = portMUX_INITIALIZER_UNLOCKED; //BLE callbackとloopの同時アクセスを防ぐ
volatile bool bleCommandPending = false;
volatile bool bleCommandArmed = true;
BleCommand pendingCommand = {-1, 0};

// ================================================================
// Gamepad
// ================================================================

XboxGamepadDevice* gamepad = nullptr;
BleCompositeHID compositeHID(DEVICE_NAME, "Mystfit", 100);

unsigned long lastHidReportMs = 0;
unsigned long lastBatteryMs = 0;
bool wasConnected = false;

constexpr uint32_t HID_INTERVAL_MS = 20;

// ================================================================
// Sleep & Battery
// ================================================================

bool lastSleepState = true;
unsigned long sleepDebounceMs = 0;
constexpr uint32_t SLEEP_DEBOUNCE_MS = 50;

constexpr float R_TOP = 47000.0f;
constexpr float R_BOTTOM = 100000.0f;

String serialLine;

// ================================================================
// DAC & Playback Logic
// ================================================================

void dacWriteRaw(uint16_t value) { //DAC書き込み
    digitalWrite(DAC_CS, LOW);
    SPI.transfer16(value & 0x0FFF); //DACが12 bitなので下位12 bitだけ送る
    digitalWrite(DAC_CS, HIGH);
}

void dacPowerDown() { //DAC121S101にPower Down用制御wordを送る
    digitalWrite(DAC_CS, LOW);
    SPI.transfer16(0x3000);
    digitalWrite(DAC_CS, HIGH);
}

void setVibrationOutput(bool enabled) { //アンプとLEDをまとめてON/OFF
    digitalWrite(AMP_SD, enabled ? HIGH : LOW);
    digitalWrite(LED_VIB_EN, enabled ? HIGH : LOW);
    digitalWrite(LED_VIB, enabled ? HIGH : LOW);
}

uint16_t applyAmplitude(uint16_t sample, uint8_t amplitude) { //振幅調整
    //2048を0とみなした交流波形へ変換
    int32_t centered = static_cast<int32_t>(sample) - static_cast<int32_t>(IDLE_DAC_CODE);

    //Unityから受け取るamplitude 0～100 × masterVolume 0～100%でスケーリング
    int64_t scaled = static_cast<int64_t>(centered) * amplitude * masterVolume / (100LL * 100LL);

    int32_t result = static_cast<int32_t>(IDLE_DAC_CODE) + static_cast<int32_t>(scaled);
    return static_cast<uint16_t>(constrain(result, 0, 4095));
}

void dacTimerCallback(void*) { //100 µsごとにDAC Taskへ通知
    if (dacTaskHandle != nullptr) xTaskNotifyGive(dacTaskHandle);
}

void dacTask(void*) { //10 kHz波形生成専用DAC Task
    for (;;) {
        ulTaskNotifyTake(pdTRUE, portMAX_DELAY); //Timerから通知が来るまで待機

        uint16_t output = IDLE_DAC_CODE;
        bool consumed = false;

        if (playing && playData != nullptr && playPos + 1 < playSize) {
            size_t pos = playPos;

            //.binは1 sample = 2 byte，Little Endian
            uint16_t raw = static_cast<uint16_t>(playData[pos]) |
                           (static_cast<uint16_t>(playData[pos + 1]) << 8);
            uint16_t sample = raw & 0x0FFF;

            if (playAmplitude >= VIB_MIN_THRESH) output = applyAmplitude(sample, playAmplitude);

            //amplitude=0でもplayPosは進める．Position変更時も同じ時間位置を維持できる
            consumed = true;
        }

        if (xSemaphoreTake(spiMutex, 0) == pdTRUE) {
            SPI.beginTransaction(dacSpiSettings);
            dacWriteRaw(output);
            SPI.endTransaction();
            xSemaphoreGive(spiMutex);

            if (consumed) playPos += 2; //1 sample = 2 byte
        }

        if (playing && (playData == nullptr || playPos + 1 >= playSize)) {
            playing = false;
            playbackFinished = true;
        }
    }
}

// ================================================================
// Waveform loading
// ================================================================

bool loadClip(int id) { //FFatからPSRAMへロード
    if (id < 0 || id >= WAVEFORM_COUNT) return false;

    const char* path = SFX_AUDIO_CLIPS[id].path;
    File file = FFat.open(path, FILE_READ);

    if (!file || file.isDirectory()) {
        Serial.print("MISSING: "); Serial.println(path);
        if (file) file.close();
        return false;
    }

    size_t size = file.size();
    if (size < 2) {
        Serial.print("EMPTY: "); Serial.println(path);
        file.close();
        return false;
    }

    uint8_t* buffer = static_cast<uint8_t*>(heap_caps_malloc(size, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
    if (!buffer) {
        Serial.print("PSRAM allocation failed: "); Serial.println(path);
        file.close();
        return false;
    }

    size_t total = 0;
    while (total < size) {
        size_t n = file.read(buffer + total, size - total);
        if (n == 0) break;
        total += n;
    }
    file.close();

    if (total != size) {
        Serial.print("READ FAILED: "); Serial.println(path);
        free(buffer);
        return false;
    }

    clips[id].data = buffer;
    clips[id].sizeBytes = size;
    clips[id].loaded = true;

    Serial.printf("Loaded [%d] %s (%u bytes)\n", id, path, static_cast<unsigned>(size));
    return true;
}

void loadAllClips() {
    Serial.println("Loading waveform files to PSRAM...");
    for (int i = 0; i < WAVEFORM_COUNT; i++) loadClip(i);
}

void printClipList() {
    Serial.println("Waveform list:");
    for (int i = 0; i < WAVEFORM_COUNT; i++) {
        Serial.printf("%d: %s  %s\n", i, SFX_AUDIO_CLIPS[i].path, clips[i].loaded ? "OK" : "NOT LOADED");
    }
}

// ================================================================
// Playback control
// ================================================================

void stopPlayback() {
    playing = false;
    playbackFinished = false;
    playData = nullptr;
    playSize = 0;
    playPos = 0;
    playAmplitude = 0;
    playbackStartedMs = 0;
    currentWaveformId = -1;

    if (spiMutex && xSemaphoreTake(spiMutex, pdMS_TO_TICKS(10)) == pdTRUE) {
        SPI.beginTransaction(dacSpiSettings);
        dacWriteRaw(IDLE_DAC_CODE);
        SPI.endTransaction();
        xSemaphoreGive(spiMutex);
    }

    setVibrationOutput(false); //アンプをOFFにして発熱・PWMノイズを防ぐ
}

bool startPlayback(int waveformId, uint8_t amplitude) { //指定した波形を指定振幅で開始
    if (waveformId < 0 || waveformId >= WAVEFORM_COUNT) return false;

    if (!clips[waveformId].loaded) {
        DBG("Waveform %d not loaded\n", waveformId);
        return false;
    }

    playing = false;
    playData = clips[waveformId].data; //対象波形のPSRAMアドレス
    playSize = clips[waveformId].sizeBytes;
    playPos = 0;
    playAmplitude = amplitude;
    currentWaveformId = waveformId;
    playbackFinished = false;
    playbackStartedMs = millis();

    setVibrationOutput(amplitude >= VIB_MIN_THRESH);
    playing = true; //DAC Taskが波形を読み始める

    DBG("PLAY id=%d amp=%u\n", waveformId, amplitude);
    return true;
}

void servicePlaybackFinished() {
    if (!playbackFinished) return;

    playbackFinished = false;
    setVibrationOutput(false); //再生完了と同時にアンプOFF

    DBG("Finished waveform %d\n", currentWaveformId);

    playData = nullptr;
    playSize = 0;
    playPos = 0;
    playAmplitude = 0;
    currentWaveformId = -1;
}

void checkPlaybackTimeout() {
    if (!playing) return;

    if (millis() - playbackStartedMs > PLAYBACK_TIMEOUT_MS) {
        DBGLN("Playback timeout");
        stopPlayback();
    }
}

// ================================================================
// BLE command
// ================================================================

int decodeWaveformCommand(uint8_t value) { //4,8,12,...96をID 0～23へ戻す
    if (value == 0) return -1;

    int step = (static_cast<int>(value) + 2) / 4; //+2は丸め誤差を多少許容
    int id = step - 1;

    if (id < 0 || id >= WAVEFORM_COUNT) return -1;

    int expected = 4 * (id + 1);
    if (abs(static_cast<int>(value) - expected) > 1) return -1;

    return id;
}

//XboxGamepadOutputReportDataはライブラリが定義している構造体
void onVibrate(XboxGamepadOutputReportData data) { //PCからrumble output reportを受信したとき呼ばれる
    //UnityのSetMotorSpeeds(amplitude, command)に対応
    uint8_t amplitude = data.weakMotorMagnitude;
    uint8_t command = data.strongMotorMagnitude;

    DBG("RUMBLE weak=%u strong=%u amp=%u cmd=%u\n",
        data.weakMotorMagnitude, data.strongMotorMagnitude, amplitude, command);

    //STOP専用command
    if (amplitude == 0 && command == STOP_COMMAND) {
        portENTER_CRITICAL(&bleMux);
        pendingCommand.waveformId = STOP_REQUEST_ID;
        pendingCommand.amplitude = 0;
        bleCommandPending = true;
        portEXIT_CRITICAL(&bleMux);
        return;
    }

    //0,0は再生停止ではなく，次のBLE命令を受け付け可能にする
    if (amplitude == 0 && command == 0) {
        bleCommandArmed = true;
        return;
    }

    //同じrumble packetが繰り返し届いても何度も処理しない
    if (!bleCommandArmed) return;

    int id = decodeWaveformCommand(command);
    if (id < 0) return;

    bleCommandArmed = false;

    portENTER_CRITICAL(&bleMux);
    //BLE callbackでは再生せず，命令だけ保存してloop()側で処理する
    pendingCommand.waveformId = id;
    pendingCommand.amplitude = amplitude;
    bleCommandPending = true;
    portEXIT_CRITICAL(&bleMux);
}

void serviceBleCommand() {
    if (!bleCommandPending) return;

    BleCommand cmd;

    portENTER_CRITICAL(&bleMux);
    cmd = pendingCommand;
    bleCommandPending = false;
    portEXIT_CRITICAL(&bleMux);

    //STOP専用command
    if (cmd.waveformId == STOP_REQUEST_ID) {
        stopPlayback(); //波形再生を完全に停止し，playPosなどを初期化
        bleCommandArmed = true;
        DBGLN("STOP playback");
        return;
    }

    //同じwaveform IDなら再生位置を維持したまま振幅だけ変更
    if (playing && currentWaveformId == cmd.waveformId) {
        playAmplitude = cmd.amplitude;
        setVibrationOutput(cmd.amplitude >= VIB_MIN_THRESH);

        DBG("UPDATE id=%d amp=%u pos=%u\n",
            cmd.waveformId, cmd.amplitude, static_cast<unsigned>(playPos));
        return;
    }

    //別のwaveform IDなら先頭から再生
    startPlayback(cmd.waveformId, cmd.amplitude);
}

// ================================================================
// Battery
// ================================================================

uint8_t batteryPercent(float voltage) {
    if (voltage >= 4.10f) return 100;
    if (voltage <= 3.30f) return 0;

    float value = (voltage - 3.30f) * 100.0f / 0.80f;
    return static_cast<uint8_t>(constrain(static_cast<int>(value + 0.5f), 0, 100));
}

void serviceBattery() {
    if (millis() - lastBatteryMs < 3000) return;

    int adcMv = analogReadMilliVolts(BATTERY_PIN);
    float batteryMv = adcMv * (R_TOP + R_BOTTOM) / R_BOTTOM;
    float voltage = batteryMv / 1000.0f;

    compositeHID.setBatteryLevel(batteryPercent(voltage));
    lastBatteryMs = millis();
}

// ================================================================
// Sleep
// ================================================================

void enterDeepSleep() {
    stopPlayback();
    digitalWrite(LED_CON, LOW);

    if (xSemaphoreTake(spiMutex, portMAX_DELAY) == pdTRUE) {
        SPI.beginTransaction(dacSpiSettings);
        dacWriteRaw(IDLE_DAC_CODE);
        dacPowerDown();
        SPI.endTransaction();
        xSemaphoreGive(spiMutex);
    }

    esp_sleep_enable_ext0_wakeup(GPIO_NUM_6, 1);
    delay(100);
    esp_deep_sleep_start();
}

void checkSleep() {
    bool current = digitalRead(PIN_SLEEP);

    if (current != lastSleepState && millis() - sleepDebounceMs > SLEEP_DEBOUNCE_MS) {
        sleepDebounceMs = millis();

        if (!current && lastSleepState) enterDeepSleep();
        lastSleepState = current;
    }
}

// ================================================================
// Serial commands
// ================================================================

void handleSerialCommand(String line) {
    line.trim();

    if (line == "list") {
        printClipList();
        return;
    }

    if (line == "stop") {
        stopPlayback();
        return;
    }

    if (line.startsWith("volume ")) {
        int value = line.substring(7).toInt();
        masterVolume = constrain(value, 0, 100);
        Serial.printf("Volume = %u%%\n", masterVolume);
        return;
    }

    if (line.startsWith("play ")) { //例えばplay 0 255
        int space = line.indexOf(' ', 5);
        int id;
        int amp = 255;

        if (space < 0) id = line.substring(5).toInt();
        else {
            id = line.substring(5, space).toInt();
            amp = line.substring(space + 1).toInt();
        }

        startPlayback(id, constrain(amp, 0, 255));
        return;
    }

    Serial.println("Commands: list, play <id> [amp], stop, volume <0-100>");
}

void serviceSerial() {
    while (Serial.available()) {
        char c = static_cast<char>(Serial.read());

        if (c == '\r') continue;

        if (c == '\n') {
            if (serialLine.length()) handleSerialCommand(serialLine);
            serialLine = "";
        } else if (serialLine.length() < 100) {
            serialLine += c;
        }
    }
}

// ================================================================
// Setup
// ================================================================

void setup() {
    Serial.begin(115200); //USB Serial通信を115200 baudで開始
    delay(300);

    Serial.println();
    Serial.println("Bracelet BLE + DAC player (Receiver Only)");
    Serial.print("Device: "); Serial.println(DEVICE_NAME);

    //各ピンの入出力と初期値を設定
    pinMode(IMU_CS, OUTPUT); digitalWrite(IMU_CS, HIGH);
    pinMode(DAC_CS, OUTPUT); digitalWrite(DAC_CS, HIGH);
    pinMode(AMP_SD, OUTPUT); digitalWrite(AMP_SD, LOW);

    pinMode(LED_CON, OUTPUT);
    pinMode(LED_VIB_EN, OUTPUT);
    pinMode(LED_VIB, OUTPUT);
    digitalWrite(LED_CON, LOW);
    digitalWrite(LED_VIB_EN, LOW);
    digitalWrite(LED_VIB, LOW);

    pinMode(PIN_SLEEP, INPUT_PULLUP);
    pinMode(BATTERY_PIN, INPUT);
    lastSleepState = digitalRead(PIN_SLEEP);

    analogReadResolution(12);
    analogSetPinAttenuation(BATTERY_PIN, ADC_11db);

    //SPI初期化
    spiMutex = xSemaphoreCreateMutex();
    SPI.begin(SPI_SCLK, SPI_SDO, SPI_SDI);
    SPI.beginTransaction(dacSpiSettings);
    dacWriteRaw(IDLE_DAC_CODE);
    SPI.endTransaction();

    //FFat上の波形をPSRAMへ読み込む
    if (!FFat.begin(false)) {
        Serial.println("ERROR: FFat mount failed");
    } else {
        Serial.println("FFat mounted");
        loadAllClips();
        printClipList();
    }

    //DAC再生専用TaskをCore 0へ配置し，BLE通信系と分離
    xTaskCreatePinnedToCore(dacTask, "dac", 4096, nullptr, configMAX_PRIORITIES - 1, &dacTaskHandle, 0);

    //100 µs周期 = 10 kHzのTimer
    esp_timer_create_args_t args = {};
    args.callback = dacTimerCallback;
    args.dispatch_method = ESP_TIMER_TASK;
    args.name = "dac10k";
    esp_timer_create(&args, &dacTimer);
    esp_timer_start_periodic(dacTimer, DAC_TIMER_US);

    //BLE Xbox Gamepad設定
    auto* cfg = new XboxSeriesXControllerDeviceConfiguration();
    BLEHostConfiguration hostCfg = cfg->getIdealHostConfiguration();

    //2台をWindows側で別々のXbox Gamepadとして識別させる
#if DEVICE_ID == 0
    hostCfg.setSerialNumber("ETGAME-0");
#elif DEVICE_ID == 255
    hostCfg.setSerialNumber("ETGAME-255");
#endif

    gamepad = new XboxGamepadDevice(cfg);
    compositeHID.addDevice(gamepad);
    gamepad->onVibrate.attach(FunctionSlot<XboxGamepadOutputReportData>(onVibrate));
    compositeHID.begin(hostCfg);

    //Gamepad入力の初期状態
    gamepad->setLeftThumb(0, 0);
    gamepad->setRightThumb(0, 0);
    gamepad->release(XBOX_BUTTON_A);
    gamepad->release(XBOX_BUTTON_B);
    gamepad->release(XBOX_BUTTON_X);
    gamepad->sendGamepadReport();

    lastHidReportMs = millis();
    lastBatteryMs = millis();

    Serial.println("System ready");
}

// ================================================================
// Loop
// ================================================================

void loop() {
    uint32_t now = millis();

    serviceSerial();
    checkSleep();
    serviceBleCommand();
    servicePlaybackFinished();
    checkPlaybackTimeout();

    bool connected = compositeHID.isConnected();

    if (!connected) {
        if (wasConnected) {
            DBGLN("BLE disconnected");
            stopPlayback();
            wasConnected = false;
        }

        digitalWrite(LED_CON, LOW);
        setVibrationOutput(false);
        delay(5);
        return;
    }

    if (!wasConnected) {
        wasConnected = true;
        DBGLN("BLE connected");
        digitalWrite(LED_CON, HIGH);
    }

    //WindowsにGamepadが生きていると認識させるためのダミー入力
    gamepad->setLeftThumb((now % 100 < 50) ? 1 : 0, 0);

    //一定周期でGamepad入力Reportを送信
    if (now - lastHidReportMs >= HID_INTERVAL_MS) {
        gamepad->sendGamepadReport();
        lastHidReportMs = now;
    }

    serviceBattery();
    delay(1);
}