/**
 * XIAO ESP32S3 Plus
 *
 * BLE Xbox Gamepad
 * + 2 physical buttons
 * + FFat waveform playback
 * + DAC121S101
 *
 * Unity:
 *   Gamepad.SetMotorSpeeds(amplitude, waveformCommand)
 *
 * amplitude:
 *   0.0 .. 1.0
 *
 * waveformCommand:
 *   16,32,...,192 / 255
 *
 * waveform ID:
 *   0..3   RAW A..D
 *   4..7   ISM A..D
 *   8..11  BPF A..D
 *
 * Button:
 *   BTN_A -> Xbox A -> vertical cursor
 *   BTN_B -> Xbox B -> horizontal selection / Render confirm
 */

#include <Arduino.h>
#include <SPI.h>
#include <FS.h>
#include <FFat.h>
#include "sfx_audio_config.h"

#include <BleCompositeHID.h>
#include <XboxGamepadDevice.h>

#include <esp_timer.h>
#include <esp_sleep.h>
#include <driver/rtc_io.h>
#include <esp_heap_caps.h>

// ================================================================
// Device setting
// ================================================================

// 1台目 = 100
// 2台目 = 101
#define DEVICE_ID 100

#define STRINGIFY(x) #x
#define TOSTRING(x) STRINGIFY(x)

#define DEVICE_NAME "ETGame " TOSTRING(DEVICE_ID)

// Unity SetMotorSpeeds:
// lowFrequency  -> strong motor -> amplitude
// highFrequency -> weak motor   -> waveform command
//
// 実機で逆なら0/1を変更
#define UNITY_AMPLITUDE_IS_STRONG_MOTOR 0

#define DEBUG_ENABLED 1

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

#define SPI_SCLK 7
#define SPI_SDO  8
#define SPI_SDI  9

#define IMU_CS 38
#define DAC_CS 39

#define AMP_SD 12

#define LED_CON    11
#define LED_VIB_EN 41
#define LED_VIB    40

#define BTN_A 3
#define BTN_B 4

#define PIN_SLEEP   6
#define BATTERY_PIN 10

// ================================================================
// Audio
// ================================================================

constexpr uint32_t SAMPLE_RATE_HZ =
    SFX_AUDIO_SAMPLE_RATE_HZ;

constexpr uint32_t DAC_TIMER_US =
    1000000UL / SAMPLE_RATE_HZ;

constexpr uint32_t DAC_SPI_HZ = 8000000;

constexpr uint16_t IDLE_DAC_CODE =
    SFX_AUDIO_CENTER_VALUE & 0x0FFF;

constexpr uint8_t DEFAULT_VOLUME = 50;
constexpr uint8_t VIB_MIN_THRESH = 3;

constexpr uint32_t PLAYBACK_TIMEOUT_MS = 10000;

constexpr int WAVEFORM_COUNT = 12;

static_assert(
    SFX_AUDIO_CLIP_COUNT >= WAVEFORM_COUNT,
    "sfx_audio_config.h needs at least 12 clips."
);

SPISettings dacSpiSettings(
    DAC_SPI_HZ,
    MSBFIRST,
    SPI_MODE1
);

// ================================================================
// Loaded waveform
// ================================================================

struct LoadedClip {
    uint8_t* data = nullptr;
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

struct BleCommand {
    int waveformId;
    uint8_t amplitude;
};

portMUX_TYPE bleMux = portMUX_INITIALIZER_UNLOCKED;

volatile bool bleCommandPending = false;
volatile bool bleCommandArmed = true;

BleCommand pendingCommand = {-1, 0};

// ================================================================
// Gamepad
// ================================================================

XboxGamepadDevice* gamepad = nullptr;

BleCompositeHID compositeHID(
    DEVICE_NAME,
    "Mystfit",
    100
);

unsigned long lastHidReportMs = 0;
unsigned long lastBatteryMs = 0;

bool wasConnected = false;

// ================================================================
// Button debounce
// ================================================================

struct DebouncedButton {
    bool stable = false;
    bool raw = false;
    uint32_t changedAt = 0;
};

DebouncedButton buttonA;
DebouncedButton buttonB;

constexpr uint32_t BUTTON_DEBOUNCE_MS = 25;
constexpr uint32_t HID_INTERVAL_MS = 20;

// ================================================================
// Sleep
// ================================================================

bool lastSleepState = true;
unsigned long sleepDebounceMs = 0;

constexpr uint32_t SLEEP_DEBOUNCE_MS = 50;

// ================================================================
// Battery
// ================================================================

constexpr float R_TOP = 47000.0f;
constexpr float R_BOTTOM = 100000.0f;

// ================================================================
// Serial
// ================================================================

String serialLine;

// ================================================================
// DAC
// ================================================================

void dacWriteRaw(uint16_t value)
{
    digitalWrite(DAC_CS, LOW);
    SPI.transfer16(value & 0x0FFF);
    digitalWrite(DAC_CS, HIGH);
}

void dacPowerDown()
{
    digitalWrite(DAC_CS, LOW);
    SPI.transfer16(0x3000);
    digitalWrite(DAC_CS, HIGH);
}

uint16_t applyAmplitude(
    uint16_t sample,
    uint8_t amplitude)
{
    int32_t centered =
        static_cast<int32_t>(sample) -
        static_cast<int32_t>(IDLE_DAC_CODE);

    int64_t scaled =
        static_cast<int64_t>(centered) *
        amplitude *
        masterVolume /
        (255LL * 100LL);

    int32_t result =
        static_cast<int32_t>(IDLE_DAC_CODE) +
        static_cast<int32_t>(scaled);

    return static_cast<uint16_t>(
        constrain(result, 0, 4095));
}

// ================================================================
// DAC timer / task
// ================================================================

void dacTimerCallback(void*)
{
    if (dacTaskHandle != nullptr) {
        xTaskNotifyGive(dacTaskHandle);
    }
}

void dacTask(void*)
{
    for (;;) {
        ulTaskNotifyTake(
            pdTRUE,
            portMAX_DELAY);

        uint16_t output = IDLE_DAC_CODE;
        bool consumed = false;

        if (playing &&
            playData != nullptr &&
            playAmplitude >= VIB_MIN_THRESH &&
            playPos + 1 < playSize)
        {
            size_t pos = playPos;

            uint16_t raw =
                static_cast<uint16_t>(playData[pos]) |
                (static_cast<uint16_t>(
                    playData[pos + 1]) << 8);

            uint16_t sample = raw & 0x0FFF;

            output =
                applyAmplitude(
                    sample,
                    playAmplitude);

            consumed = true;
        }

        if (xSemaphoreTake(
                spiMutex,
                0) == pdTRUE)
        {
            SPI.beginTransaction(dacSpiSettings);
            dacWriteRaw(output);
            SPI.endTransaction();

            xSemaphoreGive(spiMutex);

            if (consumed) {
                playPos += 2;
            }
        }

        if (playing &&
            (playData == nullptr ||
             playAmplitude < VIB_MIN_THRESH ||
             playPos + 1 >= playSize))
        {
            playing = false;
            playbackFinished = true;
        }
    }
}

// ================================================================
// FFat / waveform loading
// ================================================================

bool loadClip(int id)
{
    if (id < 0 || id >= WAVEFORM_COUNT) {
        return false;
    }

    const char* path =
        SFX_AUDIO_CLIPS[id].path;

    File file =
        FFat.open(path, FILE_READ);

    if (!file || file.isDirectory()) {
        Serial.print("MISSING: ");
        Serial.println(path);

        if (file) {
            file.close();
        }

        return false;
    }

    size_t size = file.size();

    if (size < 2) {
        Serial.print("EMPTY: ");
        Serial.println(path);
        file.close();
        return false;
    }

    uint8_t* buffer =
        static_cast<uint8_t*>(
            heap_caps_malloc(
                size,
                MALLOC_CAP_SPIRAM |
                MALLOC_CAP_8BIT));

    if (!buffer) {
        Serial.print("PSRAM allocation failed: ");
        Serial.println(path);
        file.close();
        return false;
    }

    size_t total = 0;

    while (total < size) {
        size_t n =
            file.read(
                buffer + total,
                size - total);

        if (n == 0) {
            break;
        }

        total += n;
    }

    file.close();

    if (total != size) {
        Serial.print("READ FAILED: ");
        Serial.println(path);

        free(buffer);
        return false;
    }

    clips[id].data = buffer;
    clips[id].sizeBytes = size;
    clips[id].loaded = true;

    Serial.printf(
        "Loaded [%d] %s (%u bytes)\n",
        id,
        path,
        static_cast<unsigned>(size));

    return true;
}

void loadAllClips()
{
    Serial.println(
        "Loading waveform files to PSRAM...");

    for (int i = 0;
         i < WAVEFORM_COUNT;
         i++)
    {
        loadClip(i);
    }
}

void printClipList()
{
    Serial.println("Waveform list:");

    for (int i = 0;
         i < WAVEFORM_COUNT;
         i++)
    {
        Serial.printf(
            "%d: %s  %s\n",
            i,
            SFX_AUDIO_CLIPS[i].path,
            clips[i].loaded
                ? "OK"
                : "NOT LOADED");
    }
}

// ================================================================
// Playback
// ================================================================

void stopPlayback(bool powerDownAmp)
{
    playing = false;

    playData = nullptr;
    playSize = 0;
    playPos = 0;

    playAmplitude = 0;
    currentWaveformId = -1;

    if (spiMutex &&
        xSemaphoreTake(
            spiMutex,
            pdMS_TO_TICKS(10)) == pdTRUE)
    {
        SPI.beginTransaction(dacSpiSettings);
        dacWriteRaw(IDLE_DAC_CODE);
        SPI.endTransaction();

        xSemaphoreGive(spiMutex);
    }

    digitalWrite(LED_VIB, LOW);

    if (powerDownAmp) {
        digitalWrite(AMP_SD, LOW);
        digitalWrite(LED_VIB_EN, LOW);
    }
}

bool startPlayback(
    int waveformId,
    uint8_t amplitude)
{
    if (waveformId < 0 ||
        waveformId >= WAVEFORM_COUNT)
    {
        return false;
    }

    if (!clips[waveformId].loaded) {
        DBG(
            "Waveform %d not loaded\n",
            waveformId);

        return false;
    }

    // Position端では一方が振幅0になる
    if (amplitude < VIB_MIN_THRESH) {
        stopPlayback(false);
        return true;
    }

    playing = false;

    playData =
        clips[waveformId].data;

    playSize =
        clips[waveformId].sizeBytes;

    playPos = 0;
    playAmplitude = amplitude;

    currentWaveformId = waveformId;

    playbackFinished = false;
    playbackStartedMs = millis();

    digitalWrite(AMP_SD, HIGH);
    digitalWrite(LED_VIB_EN, HIGH);
    digitalWrite(LED_VIB, HIGH);

    playing = true;

    DBG(
        "PLAY id=%d amp=%u\n",
        waveformId,
        amplitude);

    return true;
}

void servicePlaybackFinished()
{
    if (!playbackFinished) {
        return;
    }

    playbackFinished = false;

    digitalWrite(LED_VIB, LOW);

    DBG(
        "Finished waveform %d\n",
        currentWaveformId);

    playData = nullptr;
    playSize = 0;
    playPos = 0;

    playAmplitude = 0;
    currentWaveformId = -1;
}

void checkPlaybackTimeout()
{
    if (!playing) {
        return;
    }

    if (millis() - playbackStartedMs >
        PLAYBACK_TIMEOUT_MS)
    {
        DBGLN("Playback timeout");
        stopPlayback(false);
    }
}

// ================================================================
// BLE vibration command
// ================================================================

int decodeWaveformCommand(uint8_t value)
{
    if (value == 0) {
        return -1;
    }

    // waveform ID 0..11
    // command = 5,10,15,...,60
    int step = (static_cast<int>(value) + 2) / 5;
    int id = step - 1;

    if (id < 0 || id >= WAVEFORM_COUNT) {
        return -1;
    }

    int expected = 5 * (id + 1);

    // 多少の丸め誤差を許容
    if (abs(static_cast<int>(value) - expected) > 2) {
        return -1;
    }

    return id;
}

void onVibrate(
    XboxGamepadOutputReportData data)
{
#if UNITY_AMPLITUDE_IS_STRONG_MOTOR

    uint8_t amplitude =
        data.strongMotorMagnitude;

    uint8_t command =
        data.weakMotorMagnitude;

#else

    uint8_t amplitude =
        data.weakMotorMagnitude;

    uint8_t command =
        data.strongMotorMagnitude;

#endif

    DBG(
        "RUMBLE weak=%u strong=%u "
        "amp=%u cmd=%u\n",
        data.weakMotorMagnitude,
        data.strongMotorMagnitude,
        amplitude,
        command);

    // Unityからの0,0は次回受信のre-arm
    if (amplitude == 0 &&
        command == 0)
    {
        bleCommandArmed = true;
        return;
    }

    if (!bleCommandArmed) {
        return;
    }

    int id =
        decodeWaveformCommand(command);

    if (id < 0) {
        return;
    }

    bleCommandArmed = false;

    portENTER_CRITICAL(&bleMux);

    pendingCommand.waveformId = id;
    pendingCommand.amplitude = amplitude;

    bleCommandPending = true;

    portEXIT_CRITICAL(&bleMux);
}

void serviceBleCommand()
{
    if (!bleCommandPending) {
        return;
    }

    BleCommand cmd;

    portENTER_CRITICAL(&bleMux);

    cmd = pendingCommand;
    bleCommandPending = false;

    portEXIT_CRITICAL(&bleMux);

    startPlayback(
        cmd.waveformId,
        cmd.amplitude);
}

// ================================================================
// Buttons
// ================================================================

bool readButton(
    int pin,
    DebouncedButton& button)
{
    uint32_t now = millis();

    bool raw =
        digitalRead(pin) == HIGH;

    if (raw != button.raw) {
        button.raw = raw;
        button.changedAt = now;
    }

    if (button.stable != raw &&
        now - button.changedAt >=
        BUTTON_DEBOUNCE_MS)
    {
        button.stable = raw;
    }

    return button.stable;
}

// ================================================================
// Battery
// ================================================================

uint8_t batteryPercent(float voltage)
{
    if (voltage >= 4.10f) {
        return 100;
    }

    if (voltage <= 3.30f) {
        return 0;
    }

    float value =
        (voltage - 3.30f) *
        100.0f /
        0.80f;

    return static_cast<uint8_t>(
        constrain(
            static_cast<int>(
                value + 0.5f),
            0,
            100));
}

void serviceBattery()
{
    if (millis() - lastBatteryMs <
        3000)
    {
        return;
    }

    int adcMv =
        analogReadMilliVolts(
            BATTERY_PIN);

    float batteryMv =
        adcMv *
        (R_TOP + R_BOTTOM) /
        R_BOTTOM;

    float voltage =
        batteryMv /
        1000.0f;

    compositeHID.setBatteryLevel(
        batteryPercent(voltage));

    lastBatteryMs = millis();
}

// ================================================================
// Deep sleep
// ================================================================

void enterDeepSleep()
{
    stopPlayback(true);

    digitalWrite(LED_CON, LOW);
    digitalWrite(LED_VIB, LOW);
    digitalWrite(LED_VIB_EN, LOW);

    if (xSemaphoreTake(
            spiMutex,
            portMAX_DELAY) == pdTRUE)
    {
        SPI.beginTransaction(
            dacSpiSettings);

        dacWriteRaw(IDLE_DAC_CODE);
        dacPowerDown();

        SPI.endTransaction();

        xSemaphoreGive(spiMutex);
    }

    esp_sleep_enable_ext0_wakeup(
        GPIO_NUM_6,
        1);

    delay(100);
    esp_deep_sleep_start();
}

void checkSleep()
{
    bool current =
        digitalRead(PIN_SLEEP);

    if (current != lastSleepState &&
        millis() - sleepDebounceMs >
        SLEEP_DEBOUNCE_MS)
    {
        sleepDebounceMs = millis();

        if (!current &&
            lastSleepState)
        {
            enterDeepSleep();
        }

        lastSleepState = current;
    }
}

// ================================================================
// Simple Serial commands
// ================================================================

void handleSerialCommand(String line)
{
    line.trim();

    if (line == "list") {
        printClipList();
        return;
    }

    if (line == "stop") {
        stopPlayback(false);
        return;
    }

    if (line.startsWith("volume ")) {
        int value =
            line.substring(7).toInt();

        masterVolume =
            constrain(
                value,
                0,
                100);

        Serial.printf(
            "Volume = %u%%\n",
            masterVolume);

        return;
    }

    if (line.startsWith("play ")) {
        int space =
            line.indexOf(
                ' ',
                5);

        int id;
        int amp = 255;

        if (space < 0) {
            id =
                line.substring(5).toInt();
        }
        else {
            id =
                line.substring(
                    5,
                    space).toInt();

            amp =
                line.substring(
                    space + 1).toInt();
        }

        startPlayback(
            id,
            constrain(
                amp,
                0,
                255));

        return;
    }

    Serial.println(
        "Commands: list, play <id> [amp], stop, volume <0-100>");
}

void serviceSerial()
{
    while (Serial.available()) {

        char c =
            static_cast<char>(
                Serial.read());

        if (c == '\r') {
            continue;
        }

        if (c == '\n') {

            if (serialLine.length()) {
                handleSerialCommand(
                    serialLine);
            }

            serialLine = "";
        }
        else if (serialLine.length() < 100) {
            serialLine += c;
        }
    }
}

// ================================================================
// Setup
// ================================================================

void setup()
{
    Serial.begin(115200);
    delay(300);

    Serial.println();
    Serial.println(
        "Bracelet BLE + DAC player");

    Serial.print("Device: ");
    Serial.println(DEVICE_NAME);

    Serial.printf(
        "PSRAM found: %s\n",
        psramFound()
            ? "YES"
            : "NO");

    // ------------------------------------------------------------
    // Pins
    // ------------------------------------------------------------

    pinMode(IMU_CS, OUTPUT);
    digitalWrite(IMU_CS, HIGH);

    pinMode(DAC_CS, OUTPUT);
    digitalWrite(DAC_CS, HIGH);

    pinMode(AMP_SD, OUTPUT);
    digitalWrite(AMP_SD, LOW);

    pinMode(LED_CON, OUTPUT);
    pinMode(LED_VIB_EN, OUTPUT);
    pinMode(LED_VIB, OUTPUT);

    digitalWrite(LED_CON, LOW);
    digitalWrite(LED_VIB_EN, LOW);
    digitalWrite(LED_VIB, LOW);

    pinMode(BTN_A, INPUT_PULLDOWN);
    pinMode(BTN_B, INPUT_PULLDOWN);

    pinMode(PIN_SLEEP, INPUT_PULLUP);
    pinMode(BATTERY_PIN, INPUT);

    lastSleepState =
        digitalRead(PIN_SLEEP);

    // ------------------------------------------------------------
    // ADC
    // ------------------------------------------------------------

    analogReadResolution(12);

    analogSetPinAttenuation(
        BATTERY_PIN,
        ADC_11db);

    // ------------------------------------------------------------
    // SPI
    // ------------------------------------------------------------

    spiMutex =
        xSemaphoreCreateMutex();

    if (!spiMutex) {
        Serial.println(
            "ERROR: SPI mutex");
        while (true) {
            delay(1000);
        }
    }

    SPI.begin(
        SPI_SCLK,
        SPI_SDO,
        SPI_SDI);

    SPI.beginTransaction(
        dacSpiSettings);

    dacWriteRaw(
        IDLE_DAC_CODE);

    SPI.endTransaction();

    // ------------------------------------------------------------
    // FFat
    // ------------------------------------------------------------

    if (!FFat.begin(false)) {
        Serial.println(
            "ERROR: FFat mount failed");
    }
    else {
        Serial.println(
            "FFat mounted");

        loadAllClips();
        printClipList();
    }

    // ------------------------------------------------------------
    // DAC task
    // ------------------------------------------------------------

    xTaskCreatePinnedToCore(
        dacTask,
        "dac",
        4096,
        nullptr,
        configMAX_PRIORITIES - 1,
        &dacTaskHandle,
        1);

    esp_timer_create_args_t args = {};

    args.callback =
        dacTimerCallback;

    args.arg =
        nullptr;

    args.dispatch_method =
        ESP_TIMER_TASK;

    args.name =
        "dac10k";

    args.skip_unhandled_events =
        true;

    esp_timer_create(
        &args,
        &dacTimer);

    esp_timer_start_periodic(
        dacTimer,
        DAC_TIMER_US);

    // ------------------------------------------------------------
    // BLE
    // ------------------------------------------------------------

    auto* cfg =
        new XboxSeriesXControllerDeviceConfiguration();

    BLEHostConfiguration hostCfg =
        cfg->getIdealHostConfiguration();

    gamepad =
        new XboxGamepadDevice(cfg);

    compositeHID.addDevice(
        gamepad);

    gamepad->onVibrate.attach(
        FunctionSlot<
            XboxGamepadOutputReportData>(
                onVibrate));

    compositeHID.begin(
        hostCfg);

    gamepad->setLeftThumb(0, 0);
    gamepad->setRightThumb(0, 0);

    gamepad->release(
        XBOX_BUTTON_A);

    gamepad->release(
        XBOX_BUTTON_B);

    gamepad->release(
        XBOX_BUTTON_X);

    gamepad->sendGamepadReport();

    lastHidReportMs = millis();
    lastBatteryMs = millis();

    Serial.println(
        "System ready");
}

// ================================================================
// Loop
// ================================================================

void loop()
{
    uint32_t now = millis();

    serviceSerial();
    checkSleep();

    serviceBleCommand();
    servicePlaybackFinished();
    checkPlaybackTimeout();

    bool connected =
        compositeHID.isConnected();

    if (!connected) {

        if (wasConnected) {
            DBGLN(
                "BLE disconnected");

            stopPlayback(true);

            wasConnected = false;
        }

        digitalWrite(
            LED_CON,
            LOW);

        digitalWrite(
            AMP_SD,
            LOW);

        digitalWrite(
            LED_VIB_EN,
            LOW);

        delay(5);
        return;
    }

    if (!wasConnected) {

        wasConnected = true;

        DBGLN(
            "BLE connected");

        digitalWrite(
            LED_CON,
            HIGH);

        digitalWrite(
            AMP_SD,
            HIGH);

        digitalWrite(
            LED_VIB_EN,
            HIGH);
    }

    // ------------------------------------------------------------
    // Physical buttons
    // ------------------------------------------------------------

    bool a =
        readButton(
            BTN_A,
            buttonA);

    bool b =
        readButton(
            BTN_B,
            buttonB);

    if (a) {
        gamepad->press(
            XBOX_BUTTON_A);
    }
    else {
        gamepad->release(
            XBOX_BUTTON_A);
    }

    if (b) {
        gamepad->press(
            XBOX_BUTTON_B);
    }
    else {
        gamepad->release(
            XBOX_BUTTON_B);
    }

    // 人体通信は使用しない
    gamepad->release(
        XBOX_BUTTON_X);

    // ------------------------------------------------------------
    // HID
    // ------------------------------------------------------------

    if (now - lastHidReportMs >=
        HID_INTERVAL_MS)
    {
        gamepad->
            sendGamepadReport();

        lastHidReportMs = now;
    }

    serviceBattery();

    delay(1);
}