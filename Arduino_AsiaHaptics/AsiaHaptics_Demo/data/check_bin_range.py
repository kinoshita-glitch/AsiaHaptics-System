from pathlib import Path
import numpy as np

# ==============================
# 設定
# ==============================

SFX_DIR = Path("sfx")

DAC_MIN = 0
DAC_CENTER = 2048
DAC_MAX = 4095


# ==============================
# .binを解析する関数
# ==============================

def check_bin_range(bin_path):

    # 1サンプル = 2 byte
    # Little Endian unsigned 16-bit として読み込み
    data = np.fromfile(bin_path, dtype="<u2")

    if len(data) == 0:
        print(f"{bin_path.name}: empty file")
        return

    # 有効なのは下位12 bit
    data = data & 0x0FFF

    sample_count = len(data)

    min_val = int(data.min())
    max_val = int(data.max())

    # 中心2048からの振幅
    center_min = DAC_CENTER - min_val
    max_center = max_val - DAC_CENTER

    # ==============================
    # 安全に掛けられる最大ゲイン
    # ==============================

    # 下側
    if center_min > 0:
        gain_lower = (DAC_CENTER - DAC_MIN) / center_min
    else:
        gain_lower = float("inf")

    # 上側
    if max_center > 0:
        gain_upper = (DAC_MAX - DAC_CENTER) / max_center
    else:
        gain_upper = float("inf")

    # 0～4095のどちらにも当たらない最大倍率
    safe_gain = min(gain_lower, gain_upper)

    safe_volume = safe_gain * 100


    # ==============================
    # railに達しているサンプル数
    # ==============================

    rail_samples = int(
        np.sum((data == DAC_MIN) | (data == DAC_MAX))
    )


    # ==============================
    # 結果表示
    # ==============================

    print("=" * 60)
    print(f"file        : {bin_path.name}")
    print(f"samples     : {sample_count}")
    print(f"min         : {min_val}")
    print(f"max         : {max_val}")
    print(f"center-min  : {center_min}")
    print(f"max-center  : {max_center}")
    print(f"safe gain   : {safe_gain:.3f} x")
    print(f"safe volume : {safe_volume:.1f} %")
    print(f"rail samples: {rail_samples}")


# ==============================
# sfx内の全binを解析
# ==============================

if not SFX_DIR.exists():
    print(f"ERROR: '{SFX_DIR}' folder was not found.")

else:

    bin_files = sorted(SFX_DIR.glob("*.bin"))

    if len(bin_files) == 0:
        print(f"No .bin files were found in '{SFX_DIR}'.")

    else:

        print(f"Found {len(bin_files)} bin files.")
        print()

        for bin_path in bin_files:
            check_bin_range(bin_path)

        print("=" * 60)
        print("Finished.")