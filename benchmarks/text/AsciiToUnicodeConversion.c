#include <stdint.h>

int main(void) {
    static const unsigned char source[] = "alpha/beta.txt";
    int32_t unicode[32] = {0};
    int64_t checksum = 0;

    for (int32_t i = 0; i < 200000; i += 1) {
        int ascii_only = 1;
        int32_t length = 0;
        for (; source[length] != '\0'; length += 1) {
            unsigned char unit = source[length];
            if ((unit & 0x80u) != 0) {
                ascii_only = 0;
                break;
            }

            unicode[length] = (int32_t)unit;
        }

        if (!ascii_only) {
            return 1;
        }

        checksum += length;
        checksum += unicode[0];
        checksum += unicode[length - 1];
    }

    return checksum == 45400000 ? 0 : 2;
}
