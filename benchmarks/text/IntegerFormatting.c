#include <limits.h>
#include <stdint.h>
#include <stdio.h>

#define U1024_LIMBS 16
#define TEXT_CAPACITY 320

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

static size_t format_u1024_ascii(char *destination, size_t capacity, U1024 value) {
    char reversed_digits[TEXT_CAPACITY];
    size_t length = 0;

    if (u1024_is_zero(&value)) {
        if (capacity < 2) {
            return 0;
        }

        destination[0] = '0';
        destination[1] = '\0';
        return 1;
    }

    while (!u1024_is_zero(&value)) {
        if (length >= sizeof(reversed_digits)) {
            return 0;
        }

        reversed_digits[length] = (char)('0' + u1024_divide_by_10(&value));
        length += 1;
    }

    if (length + 1 > capacity) {
        return 0;
    }

    for (size_t i = 0; i < length; i += 1) {
        destination[i] = reversed_digits[length - 1 - i];
    }

    destination[length] = '\0';
    return length;
}

static size_t format_i1024_ascii(char *destination, size_t capacity, U1024 magnitude) {
    if (capacity < 3) {
        return 0;
    }

    destination[0] = '-';
    size_t magnitude_length = format_u1024_ascii(destination + 1, capacity - 1, magnitude);
    if (magnitude_length == 0) {
        return 0;
    }

    return magnitude_length + 1;
}

static size_t format_i64_ascii(char *destination, size_t capacity, long long value) {
    int written = snprintf(destination, capacity, "%lld", value);
    if (written < 0 || (size_t)written >= capacity) {
        return 0;
    }

    return (size_t)written;
}

static size_t format_u64_ascii(char *destination, size_t capacity, unsigned long long value) {
    int written = snprintf(destination, capacity, "%llu", value);
    if (written < 0 || (size_t)written >= capacity) {
        return 0;
    }

    return (size_t)written;
}

static int64_t checksum_text(const char *text, size_t length) {
    return (int64_t)length + (int64_t)text[0] + (int64_t)text[length - 1];
}

int main(void) {
    char text[TEXT_CAPACITY];
    int64_t checksum = 0;

    for (int32_t i = 0; i < 50; i += 1) {
        size_t length = format_i64_ascii(text, sizeof(text), LLONG_MIN);
        if (length == 0) {
            return 1;
        }
        checksum += checksum_text(text, length);

        length = format_u64_ascii(text, sizeof(text), ULLONG_MAX);
        if (length == 0) {
            return 2;
        }
        checksum += checksum_text(text, length);

        length = format_i1024_ascii(text, sizeof(text), I1024_MIN_MAGNITUDE);
        if (length == 0) {
            return 3;
        }
        checksum += checksum_text(text, length);

        length = format_u1024_ascii(text, sizeof(text), U1024_MAX_VALUE);
        if (length == 0) {
            return 4;
        }
        checksum += checksum_text(text, length);
    }

    return checksum == 53200 ? 0 : 5;
}
