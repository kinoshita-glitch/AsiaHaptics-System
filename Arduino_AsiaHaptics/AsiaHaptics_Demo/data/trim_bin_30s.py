from pathlib import Path
import numpy as np

# ==============================
# 設定
# ==============================

INPUT_DIR = Path("sfx")
OUTPUT_DIR = Path("sfx_30s")

SAMPLE_RATE = 10000      # 10 kHz
TARGET_SECONDS = 30

# 30秒分のサンプル数
TARGET_SAMPLES = SAMPLE_RATE * TARGET_SECONDS

# 出力フォルダ作成
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)


# ==============================
# sfx内の全binを処理
# ==============================

bin_files = sorted(INPUT_DIR.glob("*.bin"))

if len(bin_files) == 0:
    print("binファイルが見つかりません。")

else:
    for bin_path in bin_files:

        # Little Endian / uint16として読み込み
        data = np.fromfile(bin_path, dtype="<u2")

        original_samples = len(data)
        original_seconds = original_samples / SAMPLE_RATE

        print("=" * 60)
        print(f"File             : {bin_path.name}")
        print(f"Original samples : {original_samples}")
        print(f"Original duration: {original_seconds:.3f} s")

        # ------------------------------
        # 30秒を超えている場合
        # ------------------------------

        if original_samples > TARGET_SAMPLES:

            data_out = data[:TARGET_SAMPLES]

            print(f"Trimmed samples  : {len(data_out)}")
            print(f"Trimmed duration : {len(data_out) / SAMPLE_RATE:.3f} s")

        # ------------------------------
        # 30秒以下の場合
        # ------------------------------

        else:

            data_out = data

            print("30秒以下なので変更しません。")


        # ==============================
        # 保存
        # ==============================

        output_path = OUTPUT_DIR / bin_path.name

        data_out.astype("<u2").tofile(output_path)

        print(f"Output size      : {output_path.stat().st_size} bytes")
        print(f"Saved to         : {output_path}")


print()
print("Finished.")