#include <stdint.h>
#include <string.h>
#include <uchar.h>

#define U1024_LIMBS 16
#define TEXT_CAPACITY 320

typedef enum {
    ENCODING_UTF16
} Encoding;

typedef enum {
    TEXT_ERROR_INVALID_FORMAT
} TextError;

typedef struct {
    uint64_t words[U1024_LIMBS];
} U1024;

static const U1024 I1024_MIN_MAGNITUDE = {{
    0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, UINT64_C(0x8000000000000000)
}};

static const U1024 U1024_MAX_VALUE = {{
    UINT64_MAX, UINT64_MAX, UINT64_MAX, UINT64_MAX,
    UINT64_MAX, UINT64_MAX, UINT64_MAX, UINT64_MAX,
    UINT64_MAX, UINT64_MAX, UINT64_MAX, UINT64_MAX,
    UINT64_MAX, UINT64_MAX, UINT64_MAX, UINT64_MAX
}};

static int u1024_is_zero(const U1024 *value) {
    for (size_t i = 0; i < U1024_LIMBS; i += 1) {
        if (value->words[i] != 0) {
            return 0;
        }
    }

    return 1;
}

static uint8_t u1024_divide_by_10(U1024 *value) {
    unsigned __int128 carry = 0;

    for (size_t offset = 0; offset < U1024_LIMBS; offset += 1) {
        size_t index = U1024_LIMBS - 1 - offset;
        unsigned __int128 current = (carry << 64) | value->words[index];
        value->words[index] = (uint64_t)(current / 10);
        carry = current % 10;
    }

    return (uint8_t)carry;
}

static size_t copy_ascii_to_unicode(char32_t *destination, size_t capacity, const char *source) {
    size_t length = strlen(source);
    if (length > capacity) {
        return 0;
    }

    for (size_t i = 0; i < length; i += 1) {
        destination[i] = (char32_t)source[i];
    }

    return length;
}

static size_t format_encoding_unicode(char32_t *destination, size_t capacity, Encoding value) {
    switch (value) {
        case ENCODING_UTF16:
            return copy_ascii_to_unicode(destination, capacity, "UTF16");
    }

    return 0;
}

static size_t format_text_error_unicode(char32_t *destination, size_t capacity, TextError value) {
    switch (value) {
        case TEXT_ERROR_INVALID_FORMAT:
            return copy_ascii_to_unicode(destination, capacity, "InvalidFormat");
    }

    return 0;
}

static size_t format_u1024_unicode(char32_t *destination, size_t capacity, U1024 value) {
    char reversed_digits[TEXT_CAPACITY];
    size_t length = 0;

    if (u1024_is_zero(&value)) {
        if (capacity == 0) {
            return 0;
        }

        destination[0] = U'0';
        return 1;
    }

    while (!u1024_is_zero(&value)) {
        if (length >= sizeof(reversed_digits)) {
            return 0;
        }

        reversed_digits[length] = (char)('0' + u1024_divide_by_10(&value));
        length += 1;
    }

    if (length > capacity) {
        return 0;
    }

    for (size_t i = 0; i < length; i += 1) {
        destination[i] = (char32_t)reversed_digits[length - 1 - i];
    }

    return length;
}

static size_t format_i1024_unicode(char32_t *destination, size_t capacity, U1024 magnitude) {
    if (capacity < 2) {
        return 0;
    }

    destination[0] = U'-';
    size_t magnitude_length = format_u1024_unicode(destination + 1, capacity - 1, magnitude);
    if (magnitude_length == 0) {
        return 0;
    }

    return magnitude_length + 1;
}

static int64_t checksum_unicode_text(const char32_t *text, size_t length) {
    return (int64_t)length + (int64_t)text[0] + (int64_t)text[length - 1];
}

int main(void) {
    char32_t text[TEXT_CAPACITY];
    int64_t checksum = 0;

    for (int32_t i = 0; i < 50; i += 1) {
        size_t length = format_encoding_unicode(text, TEXT_CAPACITY, ENCODING_UTF16);
        if (length == 0) {
            return 1;
        }
        checksum += checksum_unicode_text(text, length);

        length = format_text_error_unicode(text, TEXT_CAPACITY, TEXT_ERROR_INVALID_FORMAT);
        if (length == 0) {
            return 2;
        }
        checksum += checksum_unicode_text(text, length);

        length = format_i1024_unicode(text, TEXT_CAPACITY, I1024_MIN_MAGNITUDE);
        if (length == 0) {
            return 3;
        }
        checksum += checksum_unicode_text(text, length);

        length = format_u1024_unicode(text, TEXT_CAPACITY, U1024_MAX_VALUE);
        if (length == 0) {
            return 4;
        }
        checksum += checksum_unicode_text(text, length);
    }

    return checksum == 58350 ? 0 : 5;
}
