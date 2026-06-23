#include <GLFW/glfw3.h>

#include <stdint.h>
#include <stddef.h>

enum
{
    STARK_GLFW_EVENT_CLOSE = 1,
    STARK_GLFW_EVENT_WINDOW_SIZE = 2,
    STARK_GLFW_EVENT_FRAMEBUFFER_SIZE = 3,
    STARK_GLFW_EVENT_KEY = 4,
    STARK_GLFW_EVENT_MOUSE_BUTTON = 5,
    STARK_GLFW_EVENT_CURSOR_POSITION = 6,
    STARK_GLFW_EVENT_SCROLL = 7,
    STARK_GLFW_EVENT_FOCUS = 8
};

typedef struct stark_glfw_event
{
    uint64_t window_token;
    int32_t kind;
    int32_t a;
    int32_t b;
    int32_t c;
    int32_t d;
    double x;
    double y;
} stark_glfw_event;

enum
{
    STARK_GLFW_EVENT_CAPACITY = 256
};

static stark_glfw_event stark_glfw_events[STARK_GLFW_EVENT_CAPACITY];
static uint32_t stark_glfw_event_head;
static uint32_t stark_glfw_event_tail;
static uint64_t stark_glfw_dropped_events;

static uint64_t stark_glfw_window_token(GLFWwindow *window)
{
    return (uint64_t)(uintptr_t)window;
}

static void stark_glfw_push_event(
    GLFWwindow *window,
    int32_t kind,
    int32_t a,
    int32_t b,
    int32_t c,
    int32_t d,
    double x,
    double y)
{
    uint32_t next_tail = (stark_glfw_event_tail + 1u) % STARK_GLFW_EVENT_CAPACITY;
    if (next_tail == stark_glfw_event_head)
    {
        ++stark_glfw_dropped_events;
        return;
    }

    stark_glfw_event *event = &stark_glfw_events[stark_glfw_event_tail];
    event->window_token = stark_glfw_window_token(window);
    event->kind = kind;
    event->a = a;
    event->b = b;
    event->c = c;
    event->d = d;
    event->x = x;
    event->y = y;
    stark_glfw_event_tail = next_tail;
}

static void stark_glfw_window_close_callback(GLFWwindow *window)
{
    stark_glfw_push_event(window, STARK_GLFW_EVENT_CLOSE, 0, 0, 0, 0, 0.0, 0.0);
}

static void stark_glfw_window_size_callback(GLFWwindow *window, int width, int height)
{
    stark_glfw_push_event(window, STARK_GLFW_EVENT_WINDOW_SIZE, width, height, 0, 0, 0.0, 0.0);
}

static void stark_glfw_framebuffer_size_callback(GLFWwindow *window, int width, int height)
{
    stark_glfw_push_event(window, STARK_GLFW_EVENT_FRAMEBUFFER_SIZE, width, height, 0, 0, 0.0, 0.0);
}

static void stark_glfw_key_callback(GLFWwindow *window, int key, int scancode, int action, int mods)
{
    stark_glfw_push_event(window, STARK_GLFW_EVENT_KEY, key, scancode, action, mods, 0.0, 0.0);
}

static void stark_glfw_mouse_button_callback(GLFWwindow *window, int button, int action, int mods)
{
    stark_glfw_push_event(window, STARK_GLFW_EVENT_MOUSE_BUTTON, button, action, mods, 0, 0.0, 0.0);
}

static void stark_glfw_cursor_position_callback(GLFWwindow *window, double x, double y)
{
    stark_glfw_push_event(window, STARK_GLFW_EVENT_CURSOR_POSITION, 0, 0, 0, 0, x, y);
}

static void stark_glfw_scroll_callback(GLFWwindow *window, double x, double y)
{
    stark_glfw_push_event(window, STARK_GLFW_EVENT_SCROLL, 0, 0, 0, 0, x, y);
}

static void stark_glfw_focus_callback(GLFWwindow *window, int focused)
{
    stark_glfw_push_event(window, STARK_GLFW_EVENT_FOCUS, focused, 0, 0, 0, 0.0, 0.0);
}

int stark_glfw_install_event_bridge(GLFWwindow *window)
{
    if (window == NULL)
    {
        return 0;
    }

    glfwSetWindowCloseCallback(window, stark_glfw_window_close_callback);
    glfwSetWindowSizeCallback(window, stark_glfw_window_size_callback);
    glfwSetFramebufferSizeCallback(window, stark_glfw_framebuffer_size_callback);
    glfwSetKeyCallback(window, stark_glfw_key_callback);
    glfwSetMouseButtonCallback(window, stark_glfw_mouse_button_callback);
    glfwSetCursorPosCallback(window, stark_glfw_cursor_position_callback);
    glfwSetScrollCallback(window, stark_glfw_scroll_callback);
    glfwSetWindowFocusCallback(window, stark_glfw_focus_callback);
    return 1;
}

void stark_glfw_uninstall_event_bridge(GLFWwindow *window)
{
    if (window == NULL)
    {
        return;
    }

    glfwSetWindowCloseCallback(window, NULL);
    glfwSetWindowSizeCallback(window, NULL);
    glfwSetFramebufferSizeCallback(window, NULL);
    glfwSetKeyCallback(window, NULL);
    glfwSetMouseButtonCallback(window, NULL);
    glfwSetCursorPosCallback(window, NULL);
    glfwSetScrollCallback(window, NULL);
    glfwSetWindowFocusCallback(window, NULL);
}

int stark_glfw_poll_event(stark_glfw_event *event)
{
    if (event == NULL || stark_glfw_event_head == stark_glfw_event_tail)
    {
        return 0;
    }

    *event = stark_glfw_events[stark_glfw_event_head];
    stark_glfw_event_head = (stark_glfw_event_head + 1u) % STARK_GLFW_EVENT_CAPACITY;
    return 1;
}

void stark_glfw_clear_events(void)
{
    stark_glfw_event_head = 0;
    stark_glfw_event_tail = 0;
    stark_glfw_dropped_events = 0;
}

uint64_t stark_glfw_dropped_event_count(void)
{
    return stark_glfw_dropped_events;
}
