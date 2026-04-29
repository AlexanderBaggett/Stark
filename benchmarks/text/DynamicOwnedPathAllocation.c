#include <stdint.h>
#include <stdlib.h>

typedef struct {
    int8_t *items;
    size_t count;
    size_t capacity;
} PathBuffer;

static int is_separator(int8_t value) {
    return value == '/';
}

static int path_reserve(PathBuffer *path, size_t additional) {
    size_t required = path->count + additional;
    if (required <= path->capacity) {
        return 1;
    }

    size_t next_capacity = path->capacity == 0 ? 8 : path->capacity;
    while (next_capacity < required) {
        next_capacity *= 2;
    }

    int8_t *next = (int8_t *)realloc(path->items, next_capacity * sizeof(int8_t));
    if (next == NULL) {
        return 0;
    }

    path->items = next;
    path->capacity = next_capacity;
    return 1;
}

static int path_push(PathBuffer *path, int8_t value) {
    if (!path_reserve(path, 1)) {
        return 0;
    }

    path->items[path->count] = value;
    path->count += 1;
    return 1;
}

static int append_alpha(PathBuffer *path) {
    static const int8_t text[] = {'a', 'l', 'p', 'h', 'a'};
    if (!path_reserve(path, sizeof(text))) {
        return 0;
    }

    for (size_t index = 0; index < sizeof(text); index += 1) {
        path->items[path->count] = text[index];
        path->count += 1;
    }

    return 1;
}

static int append_beta_txt(PathBuffer *path) {
    static const int8_t text[] = {'b', 'e', 't', 'a', '.', 't', 'x', 't'};
    if (!path_reserve(path, sizeof(text))) {
        return 0;
    }

    for (size_t index = 0; index < sizeof(text); index += 1) {
        path->items[path->count] = text[index];
        path->count += 1;
    }

    return 1;
}

static int join_alpha_beta(PathBuffer *path, int left_has_trailing_separator, int right_has_leading_separator) {
    if (!append_alpha(path)) {
        return 0;
    }

    if (left_has_trailing_separator && !path_push(path, '/')) {
        return 0;
    }

    if (!left_has_trailing_separator && !right_has_leading_separator && !path_push(path, '/')) {
        return 0;
    }

    if (!left_has_trailing_separator && right_has_leading_separator && !path_push(path, '/')) {
        return 0;
    }

    return append_beta_txt(path);
}

static int64_t path_facts_checksum(const PathBuffer *path) {
    size_t end = path->count;
    while (end > 1 && is_separator(path->items[end - 1])) {
        end -= 1;
    }

    size_t separator = (size_t)-1;
    for (size_t index = end; index > 0; index -= 1) {
        if (is_separator(path->items[index - 1])) {
            separator = index - 1;
            break;
        }
    }

    size_t segment_start = separator == (size_t)-1 ? 0 : separator + 1;
    size_t directory_length = 0;
    if (separator == 0) {
        directory_length = 1;
    } else if (separator != (size_t)-1) {
        directory_length = separator;
    }

    size_t extension_start = end;
    int has_extension = 0;
    for (size_t index = end; index > segment_start + 1; index -= 1) {
        if (path->items[index - 1] == '.') {
            extension_start = index - 1;
            has_extension = 1;
            break;
        }
    }

    size_t extension_length = has_extension ? end - extension_start : 0;
    size_t base_name_end = has_extension ? extension_start : end;
    size_t base_name_length = segment_start < end ? base_name_end - segment_start : 0;
    return (int64_t)path->count + (int64_t)extension_length + (int64_t)base_name_length + (int64_t)directory_length;
}

int main(void) {
    int64_t checksum = 0;

    for (int32_t i = 0; i < 2000; i += 1) {
        PathBuffer first = {0};
        if (!join_alpha_beta(&first, 0, 0)) {
            return 1;
        }
        checksum += path_facts_checksum(&first);

        PathBuffer second = {0};
        if (!join_alpha_beta(&second, 1, 1)) {
            free(first.items);
            return 2;
        }
        checksum += path_facts_checksum(&second);

        free(second.items);
        free(first.items);
    }

    return checksum == 108000 ? 0 : 3;
}
