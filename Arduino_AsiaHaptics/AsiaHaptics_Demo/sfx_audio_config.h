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
constexpr uint32_t SFX_AUDIO_CLIP_COUNT = 12;

static const SfxAudioClip SFX_AUDIO_CLIPS[SFX_AUDIO_CLIP_COUNT] = {
    {"/sfx/normalized_kuppafire_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/normalized_1up2_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/normalized_fireball_triple_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/normalized_stepon_triple_u12le_10k.bin", 14720, 1472, 29440},
    
    {"/sfx/kuppafire_ism_170_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/1up2_ism_170_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/fireball_triple_ism_170_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/stepon_triple_ism_170_u12le_10k.bin", 14720, 1472, 29440},
    
    {"/sfx/kuppafire_bpf_170Hz_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/1up2_bpf_170Hz_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/fireball_triple_bpf_170Hz_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/stepon_triple_bpf_170Hz_u12le_10k.bin", 14720, 1472, 29440},
};

/*
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
constexpr uint32_t SFX_AUDIO_CLIP_COUNT = 27;

static const SfxAudioClip SFX_AUDIO_CLIPS[SFX_AUDIO_CLIP_COUNT] = {
    {"/sfx/footstep_u12le_10k.bin", 2000, 200, 4000},
    {"/sfx/cherry_u12le_10k.bin", 10558, 1056, 21116},
    {"/sfx/bomb_u12le_10k.bin", 27373, 2737, 54746},
    {"/sfx/footstep_ism_u12le_10k.bin", 2000, 200, 4000},
    {"/sfx/cherry_ism_u12le_10k.bin", 10558, 1056, 21116},
    {"/sfx/bomb_ism_u12le_10k.bin", 27372, 2737, 54744},
    {"/sfx/yoru_no_odoriko_u12le_10k.bin", 320880, 32088, 641760},
    {"/sfx/1up2_ism_150_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/1up2_ism_170_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/1up2_ism_200_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/1up2_ism_250_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/fireball_triple_ism_150_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/fireball_triple_ism_170_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/fireball_triple_ism_200_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/fireball_triple_ism_250_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/kuppafire_ism_150_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/kuppafire_ism_170_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/kuppafire_ism_200_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/kuppafire_ism_250_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/normalized_1up2_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/normalized_fireball_triple_u12le_10k.bin", 14027, 1403, 28054},
    {"/sfx/normalized_kuppafire_u12le_10k.bin", 13000, 1300, 26000},
    {"/sfx/normalized_stepon_triple_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/stepon_triple_ism_150_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/stepon_triple_ism_170_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/stepon_triple_ism_200_u12le_10k.bin", 14720, 1472, 29440},
    {"/sfx/stepon_triple_ism_250_u12le_10k.bin", 14720, 1472, 29440},
};
*/

//#pragma once

/*
  既存のsfx_audio_config.hで、SFX_AUDIO_CLIPSの並びを必ず次の順にしてください。
  durationMsとfileSizeBytesは、実際に生成された値を使用してください。
*/

// 例:
// static const SfxAudioClip SFX_AUDIO_CLIPS[] = {
//   {"/raw_A.bin", ...},  // 0
//   {"/raw_B.bin", ...},  // 1
//   {"/raw_C.bin", ...},  // 2
//   {"/raw_D.bin", ...},  // 3
//
//   {"/ism_A.bin", ...},  // 4
//   {"/ism_B.bin", ...},  // 5
//   {"/ism_C.bin", ...},  // 6
//   {"/ism_D.bin", ...},  // 7
//
//   {"/bpf_A.bin", ...},  // 8
//   {"/bpf_B.bin", ...},  // 9
//   {"/bpf_C.bin", ...},  // 10
//   {"/bpf_D.bin", ...},  // 11
// };