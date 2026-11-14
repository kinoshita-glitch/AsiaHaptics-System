#pragma once

#include <Arduino.h>

struct SfxAudioClip {
    const char* path;
    uint32_t sampleN;
    uint32_t durationMs;
    uint32_t fileSizeBytes;
};

constexpr uint32_t SFX_AUDIO_SAMPLE_RATE_HZ = 10000;
constexpr uint16_t SFX_AUDIO_CENTER_VALUE = 2048;
constexpr uint16_t SFX_AUDIO_MAX_VALUE = 4095;
constexpr uint32_t SFX_AUDIO_CLIP_COUNT = 24;
/* old ver. (simple ISM)
static const SfxAudioClip SFX_AUDIO_CLIPS[SFX_AUDIO_CLIP_COUNT] = {
    {"/sfx/normalized_kuppafire_u12le_10k.bin", 13000, 1300, 26000}, //0
    {"/sfx/normalized_1up2_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/normalized_fireball_triple_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/normalized_stepon_triple_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/normalized_chaotic_battle_BGM_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/normalized_fun_vibe_BGM_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/normalized_snowy_course_BGM_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/normalized_sine_170hz_u12le_10k.bin", 50000, 5000, 100000}, //7
    
    {"/sfx/kuppafire_ism_170_u12le_10k.bin", 13000, 1300, 26000}, //8
    {"/sfx/1up2_ism_170_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/fireball_triple_ism_170_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/stepon_triple_ism_170_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/chaotic_battle_BGM_ism_170_u12le_10k.bin", 300000, 30000, 600000}, //12
    {"/sfx/fun_vibe_BGM_ism_170_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/snowy_course_BGM_ism_170_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/sine_170hz_ism_170_u12le_10k.bin", 50000, 5000, 100000}, //15
    
    {"/sfx/kuppafire_bpf_170Hz_u12le_10k.bin", 13000, 1300, 26000}, //16
    {"/sfx/1up2_bpf_170Hz_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/fireball_triple_bpf_170Hz_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/stepon_triple_bpf_170Hz_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/chaotic_battle_BGM_bpf_170Hz_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/fun_vibe_BGM_bpf_170Hz_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/snowy_course_BGM_bpf_170Hz_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/sine_170hz_bpf_170Hz_u12le_10k.bin", 50000, 5000, 100000}, //23
};
*/

/* AHISM (RMS正規化したRAW信号にそのままAHISMを適用)
static const SfxAudioClip SFX_AUDIO_CLIPS[SFX_AUDIO_CLIP_COUNT] = {
    {"/sfx/normalized_kuppafire_u12le_10k.bin", 13000, 1300, 26000}, //0
    {"/sfx/normalized_1up2_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/normalized_fireball_triple_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/normalized_stepon_triple_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/normalized_fun_vibe_BGM_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/normalized_chaotic_battle_BGM_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/normalized_snowy_course_BGM_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/normalized_sine_170hz_u12le_10k.bin", 50000, 5000, 100000}, //7
    
    {"/sfx/normalized_kuppafire_ahism_u12le_10k.bin", 13000, 1300, 26000}, //8
    {"/sfx/normalized_1up2_ahism_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/normalized_fireball_triple_ahism_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/normalized_stepon_triple_ahism_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/normalized_fun_vibe_BGM_ahism_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/normalized_chaotic_battle_BGM_ahism_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/normalized_snowy_course_BGM_ahism_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/normalized_sine_170hz_ahism_u12le_10k.bin", 50000, 5000, 100000}, //15
    
    {"/sfx/kuppafire_bpf_170Hz_u12le_10k.bin", 13000, 1300, 26000}, //16
    {"/sfx/1up2_bpf_170Hz_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/fireball_triple_bpf_170Hz_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/stepon_triple_bpf_170Hz_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/fun_vibe_BGM_bpf_170Hz_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/chaotic_battle_BGM_bpf_170Hz_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/snowy_course_BGM_bpf_170Hz_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/sine_170hz_bpf_170Hz_u12le_10k.bin", 50000, 5000, 100000}, //23
};
*/

// AHISM (RAW，AHISMともに最大値で正規化．BPFは無視)
static const SfxAudioClip SFX_AUDIO_CLIPS[SFX_AUDIO_CLIP_COUNT] = {
    {"/sfx/max_normalized_kuppafire_u12le_10k.bin", 13000, 1300, 26000}, //0
    {"/sfx/max_normalized_1up2_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/max_normalized_fireball_triple_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/max_normalized_stepon_triple_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/max_normalized_fun_vibe_BGM_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/max_normalized_chaotic_battle_BGM_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/max_normalized_snowy_course_BGM_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/max_normalized_sine_170hz_u12le_10k.bin", 50000, 5000, 100000}, //7
    
    {"/sfx/max_normalized_kuppafire_ahism_u12le_10k.bin", 13000, 1300, 26000}, //8
    {"/sfx/max_normalized_1up2_ahism_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/max_normalized_fireball_triple_ahism_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/max_normalized_stepon_triple_ahism_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/max_normalized_fun_vibe_BGM_ahism_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/max_normalized_chaotic_battle_BGM_ahism_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/max_normalized_snowy_course_BGM_ahism_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/max_normalized_sine_170hz_ahism_u12le_10k.bin", 50000, 5000, 100000}, //15
    
    {"/sfx/kuppafire_bpf_170Hz_u12le_10k.bin", 13000, 1300, 26000}, //16
    {"/sfx/1up2_bpf_170Hz_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/fireball_triple_bpf_170Hz_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/stepon_triple_bpf_170Hz_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/fun_vibe_BGM_bpf_170Hz_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/chaotic_battle_BGM_bpf_170Hz_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/snowy_course_BGM_bpf_170Hz_u12le_10k.bin", 300000, 30000, 600000},
    {"/sfx/sine_170hz_bpf_170Hz_u12le_10k.bin", 50000, 5000, 100000}, //23
};
