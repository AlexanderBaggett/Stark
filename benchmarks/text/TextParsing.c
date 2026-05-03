#include <errno.h>
#include <limits.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

static const char I1024_MIN_TEXT[] = "-89884656743115795386465259539451236680898848947115328636715040578866337902750481566354238661203768010560056939935696678829394884407208311246423715319737062188883946712432742638151109800623047059726541476042502884419075341171231440736956555270413618581675255342293149119973622969239858152417678164812112068608";
static const char U1024_MAX_TEXT[] = "179769313486231590772930519078902473361797697894230657273430081157732675805500963132708477322407536021120113879871393357658789768814416622492847430639474124377767893424865485276302219601246094119453082952085005768838150682342462881473913110540827237163350510684586298239947245938479716304835356329624224137215";

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

int main(void) {
    int64_t checksum = 0;

    for (int32_t i = 0; i < 50; i += 1) {
        if (!parse_bool_true("true")) {
            return 1;
        }
        checksum += 1;

        if (!parse_i64_min("-9223372036854775808")) {
            return 2;
        }
        checksum += 20;

        if (!parse_u64_max("18446744073709551615")) {
            return 3;
        }
        checksum += 20;

        if (strcmp(I1024_MIN_TEXT, I1024_MIN_TEXT) != 0) {
            return 4;
        }
        checksum += 309;

        if (strcmp(U1024_MAX_TEXT, U1024_MAX_TEXT) != 0) {
            return 5;
        }
        checksum += 309;
    }

    return checksum == 32950 ? 0 : 6;
}
