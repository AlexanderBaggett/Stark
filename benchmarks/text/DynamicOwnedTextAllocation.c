#include <stdint.h>
#include <stdlib.h>

typedef struct {
    int8_t *items;
    size_t count;
    size_t capacity;
} ByteText;

typedef struct {
    int32_t *items;
    size_t count;
    size_t capacity;
} UnicodeText;

static int byte_reserve(ByteText *text, size_t additional) {
    size_t required = text->count + additional;
    if (required <= text->capacity) {
        return 1;
    }

    size_t next_capacity = text->capacity == 0 ? 8 : text->capacity;
    while (next_capacity < required) {
        next_capacity *= 2;
    }

    int8_t *next = (int8_t *)realloc(text->items, next_capacity * sizeof(int8_t));
    if (next == NULL) {
        return 0;
    }

    text->items = next;
    text->capacity = next_capacity;
    return 1;
}

static int unicode_reserve(UnicodeText *text, size_t additional) {
    size_t required = text->count + additional;
    if (required <= text->capacity) {
        return 1;
    }

    size_t next_capacity = text->capacity == 0 ? 8 : text->capacity;
    while (next_capacity < required) {
        next_capacity *= 2;
    }

    int32_t *next = (int32_t *)realloc(text->items, next_capacity * sizeof(int32_t));
    if (next == NULL) {
        return 0;
    }

    text->items = next;
    text->capacity = next_capacity;
    return 1;
}

static int byte_push(ByteText *text, int8_t value) {
    if (!byte_reserve(text, 1)) {
        return 0;
    }

    text->items[text->count] = value;
    text->count += 1;
    return 1;
}

static int unicode_push(UnicodeText *text, int32_t value) {
    if (!unicode_reserve(text, 1)) {
        return 0;
    }

    text->items[text->count] = value;
    text->count += 1;
    return 1;
}

static int digit_count(int32_t value) {
    if (value < 10) return 1;
    if (value < 100) return 2;
    if (value < 1000) return 3;
    if (value < 10000) return 4;
    if (value < 100000) return 5;
    if (value < 1000000) return 6;
    if (value < 10000000) return 7;
    if (value < 100000000) return 8;
    if (value < 1000000000) return 9;
    return 10;
}

static int append_ascii_digits(ByteText *text, int32_t value) {
    int digits = digit_count(value);
    int32_t divisor = 1;
    for (int index = 1; index < digits; index += 1) {
        divisor *= 10;
    }

    int32_t remaining = value;
    while (divisor > 0) {
        int32_t digit = remaining / divisor;
        if (!byte_push(text, (int8_t)('0' + digit))) {
            return 0;
        }

        remaining %= divisor;
        divisor /= 10;
    }

    return 1;
}

static int append_unicode_digits(UnicodeText *text, int32_t value) {
    int digits = digit_count(value);
    int32_t divisor = 1;
    for (int index = 1; index < digits; index += 1) {
        divisor *= 10;
    }

    int32_t remaining = value;
    while (divisor > 0) {
        int32_t digit = remaining / divisor;
        if (!unicode_push(text, '0' + digit)) {
            return 0;
        }

        remaining %= divisor;
        divisor /= 10;
    }

    return 1;
}

static int append_ascii_score_prefix(ByteText *text) {
    static const int8_t prefix[] = {'S', 'c', 'o', 'r', 'e', ':', ' '};
    for (size_t index = 0; index < sizeof(prefix); index += 1) {
        if (!byte_push(text, prefix[index])) {
            return 0;
        }
    }

    return 1;
}

static int append_unicode_score_prefix(UnicodeText *text) {
    static const int32_t prefix[] = {'S', 'c', 'o', 'r', 'e', ':', ' '};
    for (size_t index = 0; index < sizeof(prefix) / sizeof(prefix[0]); index += 1) {
        if (!unicode_push(text, prefix[index])) {
            return 0;
        }
    }

    return 1;
}

static int append_ascii_slice(ByteText *destination, const ByteText *source) {
    if (!byte_reserve(destination, source->count)) {
        return 0;
    }

    for (size_t index = 0; index < source->count; index += 1) {
        destination->items[destination->count] = source->items[index];
        destination->count += 1;
    }

    return 1;
}

static int append_unicode_slice(UnicodeText *destination, const UnicodeText *source) {
    if (!unicode_reserve(destination, source->count)) {
        return 0;
    }

    for (size_t index = 0; index < source->count; index += 1) {
        destination->items[destination->count] = source->items[index];
        destination->count += 1;
    }

    return 1;
}

static int64_t ascii_checksum(const ByteText *text) {
    int64_t checksum = (int64_t)text->count;
    for (size_t index = 0; index < text->count; index += 1) {
        checksum += text->items[index];
    }

    return checksum;
}

static int64_t unicode_checksum(const UnicodeText *text) {
    int64_t checksum = (int64_t)text->count;
    for (size_t index = 0; index < text->count; index += 1) {
        checksum += text->items[index];
    }

    return checksum;
}

int main(void) {
    int64_t checksum = 0;

    for (int32_t i = 0; i < 1000; i += 1) {
        ByteText ascii_text = {0};
        UnicodeText unicode_text = {0};
        ByteText ascii_label = {0};
        UnicodeText unicode_label = {0};

        if (!append_ascii_digits(&ascii_text, i)) {
            return 1;
        }
        checksum += ascii_checksum(&ascii_text);

        if (!append_unicode_digits(&unicode_text, i)) {
            free(ascii_text.items);
            return 2;
        }
        checksum += unicode_checksum(&unicode_text);

        if (!append_ascii_score_prefix(&ascii_label) || !append_ascii_slice(&ascii_label, &ascii_text)) {
            free(unicode_text.items);
            free(ascii_text.items);
            return 3;
        }
        checksum += ascii_checksum(&ascii_label);

        if (!append_unicode_score_prefix(&unicode_label) || !append_unicode_slice(&unicode_label, &unicode_text)) {
            free(ascii_label.items);
            free(unicode_text.items);
            free(ascii_text.items);
            return 4;
        }
        checksum += unicode_checksum(&unicode_label);

        free(unicode_label.items);
        free(ascii_label.items);
        free(unicode_text.items);
        free(ascii_text.items);
    }

    return checksum == 1830440 ? 0 : 5;
}
