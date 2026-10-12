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

//ESP32をBLE XboxゲームパッドとしてPCへ見せるためのライブラリ
#include <BleCompositeHID.h>
#include <XboxGamepadDevice.h>

#include <esp_timer.h> //高精度タイマー．10 kHz DAC再生タイミングに利用
#include <esp_sleep.h>
#include <driver/rtc_io.h>
#include <esp_heap_caps.h> //ESP32のメモリ割り当て機能．特にMALLOC_CAP_SPIRAMを指定してPSRAMへ波形を配置するために使う

// ================================================================
// Device setting
// ================================================================

#define DEVICE_ID 0 //ブレスレットの識別番号

#define STRINGIFY(x) #x
#define TOSTRING(x) STRINGIFY(x)

#define DEVICE_NAME "ETGame " TOSTRING(DEVICE_ID) //BLEデバイス名

#define UNITY_AMPLITUDE_IS_STRONG_MOTOR 0 //UnityのSetMotorSpeeds(amplitude,command)の各値がXboxのどちらのMotor Magnitudeへ入るかを定義
#define DEBUG_ENABLED 1 //デバック表示ON

#if DEBUG_ENABLED
#define DBG(...) Serial.printf(__VA_ARGS__) //DBG(...)と書けばSerial.printfとして動く．...は任意個数の引数を受け取る可変長マクロ
#define DBGLN(x) Serial.println(x) //1行表示用
#else //デバッグOFF時は何もしない
#define DBG(...) ((void)0)
#define DBGLN(x) ((void)0)
#endif

// ================================================================
// Pins
// ================================================================
//SPI端子
#define SPI_SCLK 7 //SCLK = Clock
#define SPI_SDO  8 //Serial Data Out側
#define SPI_SDI  9 //Serial Data In側

//CSはChip Select．どのSPI機器と通信するか選択
#define IMU_CS 38
#define DAC_CS 39

//アンプのShutdown制御ピン．HIGHで有効，LOWで停止する構成
#define AMP_SD 12

//LED類
#define LED_CON    11 //接続状態
#define LED_VIB_EN 41 //振動回路有効
#define LED_VIB    40 //振動中

#define PIN_SLEEP   6 //電源/スリープスイッチ用
#define BATTERY_PIN 10 //バッテリー電圧をADCで測るピン

// ================================================================
// Audio
// ================================================================

constexpr uint32_t SAMPLE_RATE_HZ = SFX_AUDIO_SAMPLE_RATE_HZ;
constexpr uint32_t DAC_TIMER_US = 1000000UL / SAMPLE_RATE_HZ; //1秒1,000,000 µsなのでサンプリング周波数10 kHzなら1,000,000 / 10,000 = 100 µsごとに1サンプル送る
constexpr uint32_t DAC_SPI_HZ = 8000000; //DACとのSPI通信速度を8 MHzにする
constexpr uint16_t IDLE_DAC_CODE = SFX_AUDIO_CENTER_VALUE & 0x0FFF; //0x0FFFは16進数で4095．&はbit ANDなので下位12 bitだけ取り出す．DACが12 bitなので0～4095へ限定
constexpr uint8_t DEFAULT_VOLUME = 100; //初期音量50% → 100%
constexpr uint8_t VIB_MIN_THRESH = 3; //振幅3未満は実質0として扱う
constexpr uint32_t PLAYBACK_TIMEOUT_MS = 120000; //異常時でも120秒経過したら再生を止める

constexpr int WAVEFORM_COUNT = 24; //波形は24種類

static_assert( //sfx_audio_config.hに24個未満しか登録されていなければコンパイルエラーにする
    SFX_AUDIO_CLIP_COUNT >= WAVEFORM_COUNT,
    "sfx_audio_config.h needs at least 24 clips."
);

SPISettings dacSpiSettings(DAC_SPI_HZ, MSBFIRST, SPI_MODE1); //SPISettingsはSPI通信条件をまとめる型

// ================================================================
// Loaded waveform
// ================================================================

struct LoadedClip { //struct（構造体）は複数のデータを1つにまとめる型
    uint8_t* data = nullptr; //PSRAM上に配置された波形データの先頭アドレスを保存．nullptrは，まだ何も指していないという意味
    size_t sizeBytes = 0; //size_tはメモリサイズや配列サイズを表すための符号なし整数型
    bool loaded = false; //ロード成功済みか記録
};

LoadedClip clips[WAVEFORM_COUNT]; //24個分のLoadedClipを作る

// ================================================================
// Playback state
// ================================================================

SemaphoreHandle_t spiMutex = nullptr;
TaskHandle_t dacTaskHandle = nullptr;
esp_timer_handle_t dacTimer = nullptr;

//volatileは，この変数は別Taskや割り込みなどによって突然変更される可能性があるので毎回メモリから読み直すようコンパイラへ伝える
//DAC Taskとmain loopの両方から使う変数に付けている
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

constexpr uint8_t STOP_COMMAND = 100; //Unityからの停止専用command．0は受付可能状態，4～96は波形ID用なので100を停止用として使用

struct BleCommand { //BLEから受け取った，どの波形か，どの振幅かをまとめる
    int waveformId;
    uint8_t amplitude;
};

portMUX_TYPE bleMux = portMUX_INITIALIZER_UNLOCKED; //BLE callbackとloopが同時にpendingCommandへアクセスしないようにする

volatile bool bleCommandPending = false; //まだ処理していないBLE命令があるか
volatile bool bleCommandArmed = true; //新しい命令を受け付けられる状態か．Unityから(0,0)を受け取るとtrueになる

BleCommand pendingCommand = {-1, 0};

// ================================================================
// Gamepad
// ================================================================

XboxGamepadDevice* gamepad = nullptr; //Xbox Gamepadデバイスオブジェクトへのポインタ
BleCompositeHID compositeHID(DEVICE_NAME, "Mystfit", 100); //BLE HIDデバイス本体．引数は概ね，(デバイス名,メーカー名,初期Battery Level)

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
    digitalWrite(DAC_CS, LOW); //DACを選択
    SPI.transfer16(value & 0x0FFF); //16 bit SPI送信．ただし値としては12 bitに限定
    digitalWrite(DAC_CS, HIGH); //通信終了
}

void dacPowerDown() { //DAC121S101にPower Down用制御wordを送る．Deep Sleep前に利用
    digitalWrite(DAC_CS, LOW);
    SPI.transfer16(0x3000);
    digitalWrite(DAC_CS, HIGH);
}

uint16_t applyAmplitude(uint16_t sample, uint8_t amplitude) { //振幅調整
    //例えばsampleが3000なら3000 - 2048 = +952，sampleが1000なら1000 - 2048 = -1048．つまり2048を0とみなした交流波形へ変換
    int32_t centered = static_cast<int32_t>(sample) - static_cast<int32_t>(IDLE_DAC_CODE);
    //掛け算時のオーバーフローを避けるため大きな型．振幅を，BLE amplitude 0～255 × masterVolume 0～100% でスケーリング
    int64_t scaled = static_cast<int64_t>(centered) * amplitude * masterVolume / (100LL * 100LL); //受け取るMotor Magnitudeの最大値は100なので，最大amplitudeは100．255LL → 100LLに変更
    int32_t result = static_cast<int32_t>(IDLE_DAC_CODE) + static_cast<int32_t>(scaled);
    return static_cast<uint16_t>(constrain(result, 0, 4095)); //DAC範囲へ制限
}

void dacTimerCallback(void*) { //タイマーが100 µsごとに呼ぶ関数．void*は任意の型のアドレスを受け取れる汎用ポインタ
    if (dacTaskHandle != nullptr) {
        xTaskNotifyGive(dacTaskHandle); //DAC Taskへ．次の1サンプルを出力するように通知
    }
}

void dacTask(void*) { //10 kHz波形生成専用DAC Task
    for (;;) { //無限ループ．Arduinoのloop()とは独立して動き続ける
        ulTaskNotifyTake(pdTRUE, portMAX_DELAY); //Timerから通知が来るまで待機

        uint16_t output = IDLE_DAC_CODE; //デフォルト出力は中心値2048
        bool consumed = false; //今回のサンプルを正常消費したか記録

        if (playing && playData != nullptr && playPos + 1 < playSize) { //再生中，データあり，まだ2 byte以上データあり，であるかを確認
            size_t pos = playPos;
            //.binファイルは1 sampleを2 byteで保存．Little Endianなので，1 byte目 = 下位8 bit，2 byte目 = 上位8 bitとして復元
            uint16_t raw = static_cast<uint16_t>(playData[pos]) | (static_cast<uint16_t>(playData[pos + 1]) << 8);
            uint16_t sample = raw & 0x0FFF; //下位12 bitだけ取り出す

            if (playAmplitude >= VIB_MIN_THRESH) {
                output = applyAmplitude(sample, playAmplitude); //振幅調整
            } else {
                output = IDLE_DAC_CODE;
            }

            consumed = true;
        }

        if (xSemaphoreTake(spiMutex, 0) == pdTRUE) { //SPIを使う権利を取得
            //DACへ1 sample送信
            SPI.beginTransaction(dacSpiSettings);
            dacWriteRaw(output);
            SPI.endTransaction();
            xSemaphoreGive(spiMutex); //SPIのロックを解除
            if (consumed) {
                playPos += 2; //1 sample = 2 byteなので次のsampleへ進む
            }
        }

        if (playing && (playData == nullptr || playPos + 1 >= playSize)) {
            playing = false;
            playbackFinished = true;
        }
    }
}

bool loadClip(int id) { //FFatからPSRAMへロード
    if (id < 0 || id >= WAVEFORM_COUNT) return false;
    const char* path = SFX_AUDIO_CLIPS[id].path; //const char*はC/C++形式の文字列へのポインタ
    File file = FFat.open(path, FILE_READ); //FFatからファイルを読み込み専用で開く．FileはArduinoのファイルオブジェクト

    if (!file || file.isDirectory()) {
        Serial.print("MISSING: "); Serial.println(path);
        if (file) file.close();
        return false;
    }

    size_t size = file.size(); //ファイルサイズを取得
    if (size < 2) {
        Serial.print("EMPTY: "); Serial.println(path);
        file.close(); return false;
    }

    uint8_t* buffer = static_cast<uint8_t*>(heap_caps_malloc(size, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT)); //OPI PSRAM設定が必要
    if (!buffer) {
        Serial.print("PSRAM allocation failed: "); Serial.println(path);
        file.close(); return false;
    }

    size_t total = 0;
    while (total < size) {
        size_t n = file.read(buffer + total, size - total); //ファイル内容をPSRAMへ順番に読み込み
        if (n == 0) break;
        total += n;
    }
    file.close();

    if (total != size) {
        Serial.print("READ FAILED: "); Serial.println(path);
        free(buffer); return false;
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

void stopPlayback(bool powerDownAmp) {
    playing = false;
    playData = nullptr;
    playSize = 0;
    playPos = 0;
    playAmplitude = 0;
    currentWaveformId = -1;

    if (spiMutex && xSemaphoreTake(spiMutex, pdMS_TO_TICKS(10)) == pdTRUE) {
        SPI.beginTransaction(dacSpiSettings);
        dacWriteRaw(IDLE_DAC_CODE);
        SPI.endTransaction();
        xSemaphoreGive(spiMutex);
    }
    digitalWrite(LED_VIB, LOW);
    
    // PAM-CS対応: 再生停止時はフラグに関係なく常にアンプをOFFにして発熱・PWMノイズを防ぐ
    digitalWrite(AMP_SD, LOW);
    digitalWrite(LED_VIB_EN, LOW);
}

bool startPlayback(int waveformId, uint8_t amplitude) { //指定した波形を指定振幅で開始．戻り値は成功/失敗
    if (waveformId < 0 || waveformId >= WAVEFORM_COUNT) return false;
    if (!clips[waveformId].loaded) {
        DBG("Waveform %d not loaded\n", waveformId);
        return false;
    }

    playing = false;
    playData = clips[waveformId].data; //対象波形のPSRAMアドレスを設定．ESP32起動時にFFatに保存されている.binファイルをPSRAMへ読み込んである
    //再生情報を初期化
    playSize = clips[waveformId].sizeBytes;
    playPos = 0;
    playAmplitude = amplitude;
    currentWaveformId = waveformId;
    playbackFinished = false;
    playbackStartedMs = millis(); //再生開始時刻を保存

    if (amplitude >= VIB_MIN_THRESH) {
        // PAM-CS対応: 再生開始時のみアンプの電源をONにする
        digitalWrite(AMP_SD, HIGH);
        digitalWrite(LED_VIB_EN, HIGH);
        digitalWrite(LED_VIB, HIGH);
    } else {
        digitalWrite(AMP_SD, LOW);
        digitalWrite(LED_VIB_EN, LOW);
        digitalWrite(LED_VIB, LOW);
    }

    playing = true; //DAC Taskが波形を読み始め，出力できる状態になる

    DBG("PLAY id=%d amp=%u\n", waveformId, amplitude);
    return true;
}

void servicePlaybackFinished() {
    if (!playbackFinished) return;
    playbackFinished = false;
    digitalWrite(LED_VIB, LOW);
    
    // PAM-CS対応: 再生完了と同時にアンプを自動OFFにして発熱を防ぐ
    digitalWrite(AMP_SD, LOW);
    digitalWrite(LED_VIB_EN, LOW);
    
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
        stopPlayback(false);
    }
}

int decodeWaveformCommand(uint8_t value) { //Unityの4,8,12,...96を0,1,2,...23へ戻す
    if (value == 0) return -1;
    int step = (static_cast<int>(value) + 2) / 4; //+2は丸め誤差を多少許容するため
    int id = step - 1; //例えば，4 → (4+2)/4 → 1 → ID 0
    if (id < 0 || id >= WAVEFORM_COUNT) return -1;
    int expected = 4 * (id + 1); //本来あるべき値を再計算
    if (abs(static_cast<int>(value) - expected) > 1) return -1; //±1を超えてずれていれば無効Commandとする
    return id;
}

//XboxGamepadOutputReportDataはライブラリが定義している構造体で，weakMotorMagnitude，strongMotorMagnitudeなどが入っている
void onVibrate(XboxGamepadOutputReportData data) { //PCからXbox rumble output reportを受信したとき自動的に呼ばれるコールバック関数
#if UNITY_AMPLITUDE_IS_STRONG_MOTOR
    uint8_t amplitude = data.strongMotorMagnitude;
    uint8_t command = data.weakMotorMagnitude;
#else //今回はUNITY_AMPLITUDE_IS_STRONG_MOTOR = 0なのでこっち
    uint8_t amplitude = data.weakMotorMagnitude;
    uint8_t command = data.strongMotorMagnitude;
#endif

    DBG("RUMBLE weak=%u strong=%u amp=%u cmd=%u\n", data.weakMotorMagnitude, data.strongMotorMagnitude, amplitude, command);

    if (amplitude == 0 && command == STOP_COMMAND) { //Unityから停止専用commandを受信
        portENTER_CRITICAL(&bleMux);
        pendingCommand.waveformId = -2; //-2を停止専用IDとして使用
        pendingCommand.amplitude = 0;
        bleCommandPending = true;
        portEXIT_CRITICAL(&bleMux);
        return;
    }

    if (amplitude == 0 && command == 0) { //Unityの0,0は，再生停止命令ではなく次の新規BLE命令を受付可能にするために使っている
        bleCommandArmed = true;
        return;
    }
    if (!bleCommandArmed) return; //同じrumble packetが何度も来ても，同じ波形を何度も開始しないようにしている

    int id = decodeWaveformCommand(command); //4～96をID 0～23へ変換
    if (id < 0) return;

    bleCommandArmed = false; //一度受理したらロック
    portENTER_CRITICAL(&bleMux); //ここから他処理に割り込まれず，安全に共有データを書き込み
    //BLE callbackでは直接再生せず，再生命令が来ていますというデータだけ保存
    //BLE受信処理は短時間で終わらせ，実際の重い再生処理は通常のloop側で行う方が安全だから
    pendingCommand.waveformId = id;
    pendingCommand.amplitude = amplitude;
    bleCommandPending = true;
    portEXIT_CRITICAL(&bleMux); //Critical Section終了
}

void serviceBleCommand() { //Arduinoのloop()側でpending commandを確認（onVibrate()の中でそのままstartPlayback()を呼ぶのではない）
    if (!bleCommandPending) return; //保留中のBLE命令がなければ何もしない
    BleCommand cmd; //ローカルコピー用変数
    portENTER_CRITICAL(&bleMux);
    cmd = pendingCommand; //共有領域から安全に取り出し
    bleCommandPending = false;
    portEXIT_CRITICAL(&bleMux);

    if (cmd.waveformId == -2) { //停止専用commandの場合
    stopPlayback(false); //波形再生を完全に停止し，playPosなどを初期化
    bleCommandArmed = true; //次のBLE命令を受け付けられる状態へ戻す
    DBGLN("STOP playback");
    return;
    }

    //現在再生中の波形と同じ波形なら，再生位置をリセットせず振幅だけ変更
    if (playing && currentWaveformId == cmd.waveformId) {
        playAmplitude = cmd.amplitude;

        if (cmd.amplitude >= VIB_MIN_THRESH) {
            digitalWrite(AMP_SD, HIGH);
            digitalWrite(LED_VIB_EN, HIGH);
            digitalWrite(LED_VIB, HIGH);
        } else {
            digitalWrite(AMP_SD, LOW);
            digitalWrite(LED_VIB_EN, LOW);
            digitalWrite(LED_VIB, LOW);
        }

        DBG("UPDATE id=%d amp=%u pos=%u\n",
            cmd.waveformId,
            cmd.amplitude,
            static_cast<unsigned>(playPos));

        return;
    }

    startPlayback(cmd.waveformId, cmd.amplitude); //実際の再生を開始
}

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

void enterDeepSleep() {
    stopPlayback(true);
    digitalWrite(LED_CON, LOW);
    digitalWrite(LED_VIB, LOW);
    digitalWrite(LED_VIB_EN, LOW);

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

void handleSerialCommand(String line) { //StringはArduinoの文字列クラス．Serial Monitorから送られた1行を保存
    line.trim(); //前後の空白や改行を除去
    if (line == "list") { printClipList(); return; } //波形一覧表示
    if (line == "stop") { stopPlayback(false); return; } //再生停止
    if (line.startsWith("volume ")) { //例えば，volume 50を処理
        int value = line.substring(7).toInt(); //7文字目以降を取り出して整数化
        masterVolume = constrain(value, 0, 100);
        Serial.printf("Volume = %u%%\n", masterVolume);
        return;
    }
    if (line.startsWith("play ")) { //play 0 255のように，waveform ID，amplitudeを指定
        int space = line.indexOf(' ', 5);
        int id; int amp = 255;
        if (space < 0) id = line.substring(5).toInt();
        else { id = line.substring(5, space).toInt(); amp = line.substring(space + 1).toInt(); }
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

    analogReadResolution(12); //ADCを12 bit，0～4095にする
    analogSetPinAttenuation(BATTERY_PIN, ADC_11db); //比較的高い電圧までADCで測れる設定

    spiMutex = xSemaphoreCreateMutex(); //SPI用Mutexを作る
    SPI.begin(SPI_SCLK, SPI_SDO, SPI_SDI); //SPIを開始
    SPI.beginTransaction(dacSpiSettings);
    dacWriteRaw(IDLE_DAC_CODE);
    SPI.endTransaction();

    if (!FFat.begin(false)) { //FFatをマウント．falseなのでマウント失敗時に自動フォーマットしない
        Serial.println("ERROR: FFat mount failed");
    } else {
        Serial.println("FFat mounted");
        loadAllClips(); //24波形をFFatからPSRAMへ読み込み
        printClipList();
    }

    // コアの分離：オーディオ処理(Core 0)とゲームコントローラー/通信系(Core 1)を分離するため、第6引数を 1 から 0 に変更
    xTaskCreatePinnedToCore(dacTask, "dac", 4096, nullptr, configMAX_PRIORITIES - 1, &dacTaskHandle, 0);
    
    //Timerは，一定時間ごとに自動的にイベントを発生させる機能．今回なら100 μsおきにdacTimerCallback()が呼ばれる
    esp_timer_create_args_t args = {}; //Timer設定用構造体．{}でゼロ初期化
    args.callback = dacTimerCallback; //Timer発火時に呼ぶ関数
    args.dispatch_method = ESP_TIMER_TASK; //Timer callbackをESP Timer Task上で実行
    args.name = "dac10k";
    esp_timer_create(&args, &dacTimer);
    esp_timer_start_periodic(dacTimer, DAC_TIMER_US); //100 µs周期，つまり10 kHzでTimerを開始

    auto* cfg = new XboxSeriesXControllerDeviceConfiguration();
    BLEHostConfiguration hostCfg = cfg->getIdealHostConfiguration(); //Xbox Gamepadに適したBLE Host設定を取得
    #if DEVICE_ID == 0
    hostCfg.setSerialNumber("ETGAME-0");
    #elif DEVICE_ID == 255
    hostCfg.setSerialNumber("ETGAME-255");
    #endif
    gamepad = new XboxGamepadDevice(cfg); //Gamepad本体を生成

    compositeHID.addDevice(gamepad); //BLE HIDへGamepadを追加
    gamepad->onVibrate.attach(FunctionSlot<XboxGamepadOutputReportData>(onVibrate)); //PCからrumble命令が来たとき，onVibrate()を呼ぶよう登録
    compositeHID.begin(hostCfg); //BLE HIDを開始．ここからPCにXbox Controllerとして見えるようになる

    gamepad->setLeftThumb(0, 0); //左スティックを中央に
    gamepad->setRightThumb(0, 0);
    gamepad->release(XBOX_BUTTON_A); //Aボタンを押していない状態に
    gamepad->release(XBOX_BUTTON_B);
    gamepad->release(XBOX_BUTTON_X);
    gamepad->sendGamepadReport(); //その初期状態をPCへ送る

    lastHidReportMs = millis();
    lastBatteryMs = millis();
    Serial.println("System ready");
}

// ================================================================
// Loop
// ================================================================

void loop() {
    uint32_t now = millis(); //起動後経過時間を保存

    //毎ループ以下を順に処理
    serviceSerial();
    checkSleep();
    serviceBleCommand();
    servicePlaybackFinished();
    checkPlaybackTimeout();

    bool connected = compositeHID.isConnected(); //PCとのBLE接続状態．未接続ならLED_CON LOW，AMP_SD LOW，LED_VIB_EN LOWとして回路を休ませる

    if (!connected) {
        if (wasConnected) {
            DBGLN("BLE disconnected");
            stopPlayback(true);
            wasConnected = false;
        }
        digitalWrite(LED_CON, LOW);
        digitalWrite(AMP_SD, LOW);
        digitalWrite(LED_VIB_EN, LOW);
        delay(5);
        return;
    }

    if (!wasConnected) { //接続直後にはBLE connectedを表示
        wasConnected = true;
        DBGLN("BLE connected");
        digitalWrite(LED_CON, HIGH);
        // PAM-CS対応: ここでのAMP_SD常時ONを廃止（発熱・ノイズ対策）
    }

    // --- 追加する心拍信号 ---
    // Windowsにデバイスが生きている（アクティブである）と認識させるためのダミー入力
    gamepad->setLeftThumb((now % 100 < 50) ? 1 : 0, 0); 
    // ----------------------

    // Keep connection alive
    if (now - lastHidReportMs >= HID_INTERVAL_MS) {
        gamepad->sendGamepadReport();
        lastHidReportMs = now;
    }

    serviceBattery();
    delay(1);
}