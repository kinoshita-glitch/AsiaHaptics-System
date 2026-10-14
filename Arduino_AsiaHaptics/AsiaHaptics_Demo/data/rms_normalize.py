from pathlib import Path
import numpy as np
import matplotlib.pyplot as plt

# ==============================
# 設定
# ==============================

INPUT_DIR = Path("sfx")
OUTPUT_DIR = Path("sfx_rms_normalized")
WAVEFORM_DIR = Path("sfx_rms_normalized_waveform")

DAC_MIN = 0
DAC_CENTER = 2048
DAC_MAX = 4095

SAMPLE_RATE = 10000

OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
WAVEFORM_DIR.mkdir(parents=True, exist_ok=True)


# ==============================
# binファイル取得
# ==============================

bin_files = sorted(INPUT_DIR.glob("*.bin"))

if len(bin_files) == 0:
    print("binファイルが見つかりません。")
    raise SystemExit


# ==============================
# 各ファイルを解析
# ==============================

file_info = []

print("=== Analysis ===")
print()

for bin_path in bin_files:

    # Little Endian uint16で読み込み
    data = np.fromfile(bin_path, dtype="<u2")

    if len(data) == 0:
        print(f"空ファイルなのでスキップ: {bin_path.name}")
        continue

    # 下位12 bitのみ使用
    data = data & 0x0FFF

    # 中心2048を0として扱う
    signal = data.astype(np.float64) - DAC_CENTER

    # RMS
    rms = np.sqrt(np.mean(signal ** 2))

    # 正負それぞれの最大振幅
    min_signal = signal.min()
    max_signal = signal.max()

    negative_peak = abs(min_signal) if min_signal < 0 else 0
    positive_peak = max_signal if max_signal > 0 else 0

    # ------------------------------
    # クリッピングしない最大ゲイン
    # ------------------------------

    if negative_peak > 0:
        gain_negative = (DAC_CENTER - DAC_MIN) / negative_peak
    else:
        gain_negative = float("inf")

    if positive_peak > 0:
        gain_positive = (DAC_MAX - DAC_CENTER) / positive_peak
    else:
        gain_positive = float("inf")

    safe_gain = min(gain_negative, gain_positive)

    # このファイルがクリッピングせず到達可能な最大RMS
    max_safe_rms = rms * safe_gain

    file_info.append({
        "path": bin_path,
        "data": data,
        "signal": signal,
        "rms": rms,
        "safe_gain": safe_gain,
        "max_safe_rms": max_safe_rms,
    })

    print(f"File         : {bin_path.name}")
    print(f"Samples      : {len(data)}")
    print(f"Original RMS : {rms:.3f}")
    print(f"Min          : {data.min()}")
    print(f"Max          : {data.max()}")
    print(f"Safe gain    : {safe_gain:.4f} x")
    print(f"Max safe RMS : {max_safe_rms:.3f}")
    print("-" * 60)


# ==============================
# 共通の最大RMSを決定
# ==============================

valid_info = [
    info for info in file_info
    if info["rms"] > 0
]

if len(valid_info) == 0:
    print("有効な波形がありません。")
    raise SystemExit


# 全ファイルが到達可能な最大共通RMS
TARGET_RMS = min(
    info["max_safe_rms"]
    for info in valid_info
)


print()
print("=" * 60)
print(f"TARGET RMS = {TARGET_RMS:.3f}")
print("=" * 60)
print()


# ==============================
# RMS正規化して保存
# ==============================

for info in valid_info:

    bin_path = info["path"]
    signal = info["signal"]
    original_rms = info["rms"]

    # RMSをTARGET_RMSに合わせるゲイン
    gain = TARGET_RMS / original_rms

    # 追加ゲイン
    EXTRA_GAIN = 2.0
    
    # RMS正規化後にさらに2倍
    normalized_signal = signal * gain * EXTRA_GAIN

    # DAC中心2048へ戻す
    dac_float = DAC_CENTER + normalized_signal

    # 念のためクリップ
    dac_float = np.clip(
        dac_float,
        DAC_MIN,
        DAC_MAX
    )

    # 四捨五入 → uint16 Little Endian
    dac = np.round(dac_float).astype("<u2")

    # ==============================
    # BIN出力
    # ==============================

    output_path = OUTPUT_DIR / bin_path.name
    dac.tofile(output_path)


    # ==============================
    # 出力後のRMS確認
    # ==============================

    output_signal = dac.astype(np.float64) - DAC_CENTER

    output_rms = np.sqrt(
        np.mean(output_signal ** 2)
    )

    rail_samples = np.sum(
        (dac == DAC_MIN) |
        (dac == DAC_MAX)
    )


    # ==============================
    # 波形画像出力
    # ==============================

    time = np.arange(len(output_signal)) / SAMPLE_RATE

    plt.figure(figsize=(12, 4))
    plt.plot(time, output_signal)

    plt.xlabel("Time [s]")
    plt.ylabel("Amplitude (DAC - 2048)")
    plt.title(
        f"{bin_path.name}\n"
        f"RMS = {output_rms:.2f}, Gain = {gain:.3f}x"
    )

    plt.grid(True)
    plt.tight_layout()

    waveform_path = (
        WAVEFORM_DIR /
        f"{bin_path.stem}_waveform.png"
    )

    plt.savefig(
        waveform_path,
        dpi=150
    )

    plt.close()


    # ==============================
    # 結果表示
    # ==============================

    print("=" * 60)
    print(f"File         : {bin_path.name}")
    print(f"Original RMS : {original_rms:.3f}")
    print(f"Gain         : {gain:.4f} x")
    print(f"Output RMS   : {output_rms:.3f}")
    print(f"Output min   : {dac.min()}")
    print(f"Output max   : {dac.max()}")
    print(f"Rail samples : {rail_samples}")
    print(f"BIN saved    : {output_path}")
    print(f"Waveform     : {waveform_path}")


print()
print("Finished.")