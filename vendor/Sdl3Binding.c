#include <SDL3/SDL.h>

#include <stdint.h>

typedef struct stark_sdl3_event
{
    uint32_t type;
    uint64_t timestamp;
    uint32_t window_id;
    uint32_t which;
    int32_t a;
    int32_t b;
    int32_t c;
    int32_t d;
    float x;
    float y;
    float xrel;
    float yrel;
} stark_sdl3_event;

static void stark_sdl3_clear_event(stark_sdl3_event *destination, uint32_t type, uint64_t timestamp)
{
    destination->type = type;
    destination->timestamp = timestamp;
    destination->window_id = 0;
    destination->which = 0;
    destination->a = 0;
    destination->b = 0;
    destination->c = 0;
    destination->d = 0;
    destination->x = 0.0f;
    destination->y = 0.0f;
    destination->xrel = 0.0f;
    destination->yrel = 0.0f;
}

static void stark_sdl3_translate_event(const SDL_Event *source, stark_sdl3_event *destination)
{
    stark_sdl3_clear_event(destination, source->type, source->common.timestamp);

    if (source->type >= SDL_EVENT_WINDOW_FIRST && source->type <= SDL_EVENT_WINDOW_LAST)
    {
        destination->window_id = source->window.windowID;
        destination->a = source->window.data1;
        destination->b = source->window.data2;
        return;
    }

    switch (source->type)
    {
        case SDL_EVENT_KEY_DOWN:
        case SDL_EVENT_KEY_UP:
            destination->window_id = source->key.windowID;
            destination->which = source->key.which;
            destination->a = (int32_t)source->key.scancode;
            destination->b = (int32_t)source->key.key;
            destination->c = (int32_t)source->key.mod;
            destination->d = source->key.repeat ? 1 : 0;
            return;

        case SDL_EVENT_MOUSE_MOTION:
            destination->window_id = source->motion.windowID;
            destination->which = source->motion.which;
            destination->a = (int32_t)source->motion.state;
            destination->x = source->motion.x;
            destination->y = source->motion.y;
            destination->xrel = source->motion.xrel;
            destination->yrel = source->motion.yrel;
            return;

        case SDL_EVENT_MOUSE_BUTTON_DOWN:
        case SDL_EVENT_MOUSE_BUTTON_UP:
            destination->window_id = source->button.windowID;
            destination->which = source->button.which;
            destination->a = (int32_t)source->button.button;
            destination->b = source->button.down ? 1 : 0;
            destination->c = (int32_t)source->button.clicks;
            destination->x = source->button.x;
            destination->y = source->button.y;
            return;

        case SDL_EVENT_MOUSE_WHEEL:
            destination->window_id = source->wheel.windowID;
            destination->which = source->wheel.which;
            destination->a = (int32_t)source->wheel.direction;
            destination->b = source->wheel.integer_x;
            destination->c = source->wheel.integer_y;
            destination->x = source->wheel.x;
            destination->y = source->wheel.y;
            destination->xrel = source->wheel.mouse_x;
            destination->yrel = source->wheel.mouse_y;
            return;

        case SDL_EVENT_AUDIO_DEVICE_ADDED:
        case SDL_EVENT_AUDIO_DEVICE_REMOVED:
        case SDL_EVENT_AUDIO_DEVICE_FORMAT_CHANGED:
            destination->which = source->adevice.which;
            destination->a = source->adevice.recording ? 1 : 0;
            return;

        default:
            return;
    }
}

int stark_sdl3_init(uint32_t flags)
{
    return SDL_Init((SDL_InitFlags)flags) ? 1 : 0;
}

void stark_sdl3_quit(void)
{
    SDL_Quit();
}

uint32_t stark_sdl3_was_init(uint32_t flags)
{
    return (uint32_t)SDL_WasInit((SDL_InitFlags)flags);
}

int stark_sdl3_set_app_metadata(const char *app_name, const char *app_version, const char *app_identifier)
{
    return SDL_SetAppMetadata(app_name, app_version, app_identifier) ? 1 : 0;
}

const char *stark_sdl3_get_error(void)
{
    return SDL_GetError();
}

int stark_sdl3_get_version(void)
{
    return SDL_GetVersion();
}

SDL_Window *stark_sdl3_create_window(const char *title, int width, int height, uint64_t flags)
{
    return SDL_CreateWindow(title, width, height, (SDL_WindowFlags)flags);
}

void stark_sdl3_destroy_window(SDL_Window *window)
{
    SDL_DestroyWindow(window);
}

int stark_sdl3_set_window_size(SDL_Window *window, int width, int height)
{
    return SDL_SetWindowSize(window, width, height) ? 1 : 0;
}

int stark_sdl3_get_window_size(SDL_Window *window, int *width, int *height)
{
    return SDL_GetWindowSize(window, width, height) ? 1 : 0;
}

int stark_sdl3_show_window(SDL_Window *window)
{
    return SDL_ShowWindow(window) ? 1 : 0;
}

int stark_sdl3_hide_window(SDL_Window *window)
{
    return SDL_HideWindow(window) ? 1 : 0;
}

SDL_Renderer *stark_sdl3_create_renderer(SDL_Window *window, const char *name)
{
    return SDL_CreateRenderer(window, name);
}

void stark_sdl3_destroy_renderer(SDL_Renderer *renderer)
{
    SDL_DestroyRenderer(renderer);
}

int stark_sdl3_set_render_draw_color(SDL_Renderer *renderer, uint8_t r, uint8_t g, uint8_t b, uint8_t a)
{
    return SDL_SetRenderDrawColor(renderer, r, g, b, a) ? 1 : 0;
}

int stark_sdl3_render_clear(SDL_Renderer *renderer)
{
    return SDL_RenderClear(renderer) ? 1 : 0;
}

int stark_sdl3_render_present(SDL_Renderer *renderer)
{
    return SDL_RenderPresent(renderer) ? 1 : 0;
}

void stark_sdl3_pump_events(void)
{
    SDL_PumpEvents();
}

int stark_sdl3_poll_event(stark_sdl3_event *destination)
{
    SDL_Event event;

    if (destination == NULL)
    {
        return 0;
    }

    if (!SDL_PollEvent(&event))
    {
        return 0;
    }

    stark_sdl3_translate_event(&event, destination);
    return 1;
}

int stark_sdl3_wait_event_timeout(stark_sdl3_event *destination, int timeout_ms)
{
    SDL_Event event;

    if (destination == NULL)
    {
        return 0;
    }

    if (!SDL_WaitEventTimeout(&event, timeout_ms))
    {
        return 0;
    }

    stark_sdl3_translate_event(&event, destination);
    return 1;
}

int stark_sdl3_push_quit_event(void)
{
    SDL_Event event;
    SDL_zero(event);
    event.type = SDL_EVENT_QUIT;
    return SDL_PushEvent(&event) ? 1 : 0;
}

SDL_AudioStream *stark_sdl3_open_audio_device_stream(uint32_t device_id, const SDL_AudioSpec *spec)
{
    return SDL_OpenAudioDeviceStream((SDL_AudioDeviceID)device_id, spec, NULL, NULL);
}

void stark_sdl3_destroy_audio_stream(SDL_AudioStream *stream)
{
    SDL_DestroyAudioStream(stream);
}

int stark_sdl3_put_audio_stream_data(SDL_AudioStream *stream, const void *data, int length)
{
    return SDL_PutAudioStreamData(stream, data, length) ? 1 : 0;
}

int stark_sdl3_get_audio_stream_data(SDL_AudioStream *stream, void *data, int length)
{
    return SDL_GetAudioStreamData(stream, data, length);
}

int stark_sdl3_get_audio_stream_available(SDL_AudioStream *stream)
{
    return SDL_GetAudioStreamAvailable(stream);
}

int stark_sdl3_flush_audio_stream(SDL_AudioStream *stream)
{
    return SDL_FlushAudioStream(stream) ? 1 : 0;
}

int stark_sdl3_clear_audio_stream(SDL_AudioStream *stream)
{
    return SDL_ClearAudioStream(stream) ? 1 : 0;
}

int stark_sdl3_pause_audio_stream_device(SDL_AudioStream *stream)
{
    return SDL_PauseAudioStreamDevice(stream) ? 1 : 0;
}

int stark_sdl3_resume_audio_stream_device(SDL_AudioStream *stream)
{
    return SDL_ResumeAudioStreamDevice(stream) ? 1 : 0;
}

int stark_sdl3_audio_stream_device_paused(SDL_AudioStream *stream)
{
    return SDL_AudioStreamDevicePaused(stream) ? 1 : 0;
}
