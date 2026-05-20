#include <errno.h>
#include <limits.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define U1024_LIMBS 16

typedef struct {
    uint64_t words[U1024_LIMBS];
} U1024;

static const char TRUE_TEXT[] = "true";
static const char I64_MIN_TEXT[] = "-9223372036854775808";
static const char U64_MAX_TEXT[] = "18446744073709551615";
static const char I1024_MIN_TEXT[] = "-89884656743115795386465259539451236680898848947115328636715040578866337902750481566354238661203768010560056939935696678829394884407208311246423715319737062188883946712432742638151109800623047059726541476042502884419075341171231440736956555270413618581675255342293149119973622969239858152417678164812112068608";
static const char U1024_MAX_TEXT[] = "179769313486231590772930519078902473361797697894230657273430081157732675805500963132708477322407536021120113879871393357658789768814416622492847430639474124377767893424865485276302219601246094119453082952085005768838150682342462881473913110540827237163350510684586298239947245938479716304835356329624224137215";

static const char *volatile TEXT_INPUTS[] = {
    TRUE_TEXT,
    I64_MIN_TEXT,
    U64_MAX_TEXT,
    I1024_MIN_TEXT,
    U1024_MAX_TEXT,
};

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

static const char *runtime_input(size_t index) {
    return TEXT_INPUTS[index];
}

static int parse_bool_true(const char *text) {
    return strcmp(text, "true") == 0;
}

static int parse_i64_min(const char *text) {
    errno = 0;
    char *end = NULL;
    long long value = strtoll(text, &end, 10);
    return errno == 0 && end != text && *end == '\0' && value == LLONG_MIN;
}

static int parse_u64_max(const char *text) {
    errno = 0;
    char *end = NULL;
    unsigned long long value = strtoull(text, &end, 10);
    return errno == 0 && end != text && *end == '\0' && value == ULLONG_MAX;
}

static int u1024_equal(const U1024 *left, const U1024 *right) {
    for (size_t i = 0; i < U1024_LIMBS; i += 1) {
        if (left->words[i] != right->words[i]) {
            return 0;
        }
    }

    return 1;
}

static int u1024_mul10_add(U1024 *value, uint8_t digit) {
    unsigned __int128 carry = digit;

    for (size_t i = 0; i < U1024_LIMBS; i += 1) {
        unsigned __int128 current = ((unsigned __int128)value->words[i] * 10) + carry;
        value->words[i] = (uint64_t)current;
        carry = current >> 64;
    }

    return carry == 0;
}

static int parse_u1024_decimal(const char *text, U1024 *value) {
    memset(value, 0, sizeof(*value));
    if (*text == '\0') {
        return 0;
    }

    for (const char *current = text; *current != '\0'; current += 1) {
        if (*current < '0' || *current > '9') {
            return 0;
        }

        if (!u1024_mul10_add(value, (uint8_t)(*current - '0'))) {
            return 0;
        }
    }

    return 1;
}

static int parse_i1024_min(const char *text) {
    if (*text != '-') {
        return 0;
    }

    U1024 parsed;
    return parse_u1024_decimal(text + 1, &parsed)
        && u1024_equal(&parsed, &I1024_MIN_MAGNITUDE);
}

static int parse_u1024_max(const char *text) {
    U1024 parsed;
    return parse_u1024_decimal(text, &parsed)
        && u1024_equal(&parsed, &U1024_MAX_VALUE);
}

int main(void) {
    int64_t checksum = 0;

    for (int32_t i = 0; i < 50; i += 1) {
        if (!parse_bool_true(runtime_input(0))) {
            return 1;
        }
        checksum += 1;

        if (!parse_i64_min(runtime_input(1))) {
            return 2;
        }
        checksum += 20;

        if (!parse_u64_max(runtime_input(2))) {
            return 3;
        }
        checksum += 20;

        if (!parse_i1024_min(runtime_input(3))) {
            return 4;
        }
        checksum += 309;

        if (!parse_u1024_max(runtime_input(4))) {
            return 5;
        }
        checksum += 309;
    }

    return checksum == 32950 ? 0 : 6;
}
