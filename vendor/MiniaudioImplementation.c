#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#define MINIAUDIO_IMPLEMENTATION
#include "native/miniaudio/miniaudio.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef struct stark_ma_decoder
{
    unsigned char *data;
    size_t data_size;
    ma_decoder decoder;
} stark_ma_decoder;

typedef struct stark_ma_playback_device
{
    ma_device device;
    float *samples;
    ma_uint64 frame_count;
    ma_uint64 cursor;
    ma_uint32 channels;
    ma_bool32 initialized;
    ma_bool32 completed;
} stark_ma_playback_device;

static ma_bool32 stark_ma_is_supported_format(int format)
{
    return format == ma_format_f32 || format == ma_format_s16;
}

static ma_bool32 stark_ma_is_supported_channels(ma_uint32 channels)
{
    return channels <= MA_MAX_CHANNELS;
}

static ma_result stark_ma_decoder_create_from_config(
    stark_ma_decoder *handle,
    const ma_decoder_config *config)
{
    ma_format format;
    ma_uint32 channels;
    ma_uint32 sample_rate;
    ma_result result = ma_decoder_get_data_format(&handle->decoder, &format, &channels, &sample_rate, NULL, 0);
    if (result != MA_SUCCESS)
    {
        return result;
    }

    if (!stark_ma_is_supported_format(format) || channels == 0 || !stark_ma_is_supported_channels(channels) || sample_rate == 0)
    {
        (void)config;
        return MA_INVALID_DATA;
    }

    return MA_SUCCESS;
}

int stark_ma_decoder_create_memory(
    const unsigned char *data,
    size_t data_size,
    int format,
    ma_uint32 channels,
    ma_uint32 sample_rate,
    stark_ma_decoder **decoder)
{
    if (decoder == NULL)
    {
        return MA_INVALID_ARGS;
    }

    *decoder = NULL;
    if (data == NULL || data_size == 0 || !stark_ma_is_supported_format(format) || !stark_ma_is_supported_channels(channels))
    {
        return MA_INVALID_ARGS;
    }

    stark_ma_decoder *handle = (stark_ma_decoder *)calloc(1, sizeof(*handle));
    if (handle == NULL)
    {
        return MA_OUT_OF_MEMORY;
    }

    handle->data = (unsigned char *)malloc(data_size);
    if (handle->data == NULL)
    {
        free(handle);
        return MA_OUT_OF_MEMORY;
    }

    memcpy(handle->data, data, data_size);
    handle->data_size = data_size;

    ma_decoder_config config = ma_decoder_config_init((ma_format)format, channels, sample_rate);
    ma_result result = ma_decoder_init_memory(handle->data, handle->data_size, &config, &handle->decoder);
    if (result != MA_SUCCESS)
    {
        free(handle->data);
        free(handle);
        return result;
    }

    result = stark_ma_decoder_create_from_config(handle, &config);
    if (result != MA_SUCCESS)
    {
        ma_decoder_uninit(&handle->decoder);
        free(handle->data);
        free(handle);
        return result;
    }

    *decoder = handle;
    return MA_SUCCESS;
}

int stark_ma_decoder_create_file(
    const char *path,
    int format,
    ma_uint32 channels,
    ma_uint32 sample_rate,
    stark_ma_decoder **decoder)
{
    if (decoder == NULL)
    {
        return MA_INVALID_ARGS;
    }

    *decoder = NULL;
    if (path == NULL || !stark_ma_is_supported_format(format) || !stark_ma_is_supported_channels(channels))
    {
        return MA_INVALID_ARGS;
    }

    stark_ma_decoder *handle = (stark_ma_decoder *)calloc(1, sizeof(*handle));
    if (handle == NULL)
    {
        return MA_OUT_OF_MEMORY;
    }

    ma_decoder_config config = ma_decoder_config_init((ma_format)format, channels, sample_rate);
    ma_result result = ma_decoder_init_file(path, &config, &handle->decoder);
    if (result != MA_SUCCESS)
    {
        free(handle);
        return result;
    }

    result = stark_ma_decoder_create_from_config(handle, &config);
    if (result != MA_SUCCESS)
    {
        ma_decoder_uninit(&handle->decoder);
        free(handle);
        return result;
    }

    *decoder = handle;
    return MA_SUCCESS;
}

void stark_ma_decoder_destroy(stark_ma_decoder *decoder)
{
    if (decoder == NULL)
    {
        return;
    }

    ma_decoder_uninit(&decoder->decoder);
    free(decoder->data);
    free(decoder);
}

int stark_ma_decoder_get_info(
    stark_ma_decoder *decoder,
    int *format,
    ma_uint32 *channels,
    ma_uint32 *sample_rate,
    ma_uint64 *length_in_pcm_frames)
{
    if (decoder == NULL || format == NULL || channels == NULL || sample_rate == NULL || length_in_pcm_frames == NULL)
    {
        return MA_INVALID_ARGS;
    }

    ma_format native_format;
    ma_result result = ma_decoder_get_data_format(&decoder->decoder, &native_format, channels, sample_rate, NULL, 0);
    if (result != MA_SUCCESS)
    {
        return result;
    }

    *format = (int)native_format;
    *length_in_pcm_frames = 0;
    result = ma_decoder_get_length_in_pcm_frames(&decoder->decoder, length_in_pcm_frames);
    if (result != MA_SUCCESS)
    {
        *length_in_pcm_frames = 0;
    }

    return MA_SUCCESS;
}

int stark_ma_decoder_read_pcm_frames(
    stark_ma_decoder *decoder,
    void *output,
    ma_uint64 frame_count,
    ma_uint64 *frames_read)
{
    if (decoder == NULL || frames_read == NULL || (frame_count != 0 && output == NULL))
    {
        return MA_INVALID_ARGS;
    }

    *frames_read = 0;
    return ma_decoder_read_pcm_frames(&decoder->decoder, output, frame_count, frames_read);
}

int stark_ma_decoder_seek_to_pcm_frame(stark_ma_decoder *decoder, ma_uint64 frame_index)
{
    if (decoder == NULL)
    {
        return MA_INVALID_ARGS;
    }

    return ma_decoder_seek_to_pcm_frame(&decoder->decoder, frame_index);
}

static void stark_ma_playback_callback(ma_device *device, void *output, const void *input, ma_uint32 frame_count)
{
    (void)input;

    stark_ma_playback_device *playback = (stark_ma_playback_device *)device->pUserData;
    if (playback == NULL || output == NULL || playback->channels == 0)
    {
        return;
    }

    float *out = (float *)output;
    ma_uint64 requested_samples = (ma_uint64)frame_count * playback->channels;
    memset(out, 0, (size_t)requested_samples * sizeof(float));

    if (playback->cursor >= playback->frame_count)
    {
        playback->completed = MA_TRUE;
        return;
    }

    ma_uint64 remaining = playback->frame_count - playback->cursor;
    ma_uint64 frames_to_copy = remaining < frame_count ? remaining : frame_count;
    ma_uint64 samples_to_copy = frames_to_copy * playback->channels;
    memcpy(out, playback->samples + (playback->cursor * playback->channels), (size_t)samples_to_copy * sizeof(float));
    playback->cursor += frames_to_copy;
    if (playback->cursor >= playback->frame_count)
    {
        playback->completed = MA_TRUE;
    }
}

int stark_ma_playback_create_f32(
    const float *samples,
    ma_uint64 frame_count,
    ma_uint32 channels,
    ma_uint32 sample_rate,
    stark_ma_playback_device **device)
{
    if (device == NULL)
    {
        return MA_INVALID_ARGS;
    }

    *device = NULL;
    if (samples == NULL || frame_count == 0 || channels == 0 || !stark_ma_is_supported_channels(channels) || sample_rate == 0)
    {
        return MA_INVALID_ARGS;
    }

    if (frame_count > SIZE_MAX / channels / sizeof(float))
    {
        return MA_TOO_BIG;
    }

    stark_ma_playback_device *playback = (stark_ma_playback_device *)calloc(1, sizeof(*playback));
    if (playback == NULL)
    {
        return MA_OUT_OF_MEMORY;
    }

    size_t sample_count = (size_t)(frame_count * channels);
    playback->samples = (float *)malloc(sample_count * sizeof(float));
    if (playback->samples == NULL)
    {
        free(playback);
        return MA_OUT_OF_MEMORY;
    }

    memcpy(playback->samples, samples, sample_count * sizeof(float));
    playback->frame_count = frame_count;
    playback->channels = channels;

    ma_device_config config = ma_device_config_init(ma_device_type_playback);
    config.playback.format = ma_format_f32;
    config.playback.channels = channels;
    config.sampleRate = sample_rate;
    config.dataCallback = stark_ma_playback_callback;
    config.pUserData = playback;

    ma_result result = ma_device_init(NULL, &config, &playback->device);
    if (result != MA_SUCCESS)
    {
        free(playback->samples);
        free(playback);
        return result;
    }

    playback->initialized = MA_TRUE;
    *device = playback;
    return MA_SUCCESS;
}

void stark_ma_playback_destroy(stark_ma_playback_device *device)
{
    if (device == NULL)
    {
        return;
    }

    if (device->initialized)
    {
        ma_device_uninit(&device->device);
    }

    free(device->samples);
    free(device);
}

int stark_ma_playback_start(stark_ma_playback_device *device)
{
    if (device == NULL || !device->initialized)
    {
        return MA_INVALID_ARGS;
    }

    return ma_device_start(&device->device);
}

int stark_ma_playback_stop(stark_ma_playback_device *device)
{
    if (device == NULL || !device->initialized)
    {
        return MA_INVALID_ARGS;
    }

    return ma_device_stop(&device->device);
}

int stark_ma_playback_is_complete(stark_ma_playback_device *device)
{
    if (device == NULL)
    {
        return 1;
    }

    return device->completed ? 1 : 0;
}
