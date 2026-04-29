#include <stdint.h>

int main(void) {
    static const unsigned char source[] = "alpha/beta.txt";
    int32_t unicode[32] = {0};
    int64_t checksum = 0;
    const int32_t length = 14;

    for (int32_t i = 0; i < 200000; i += 1) {
        for (int32_t index = 0; index < length; index += 1) {
            unicode[index] = (int32_t)source[index];
        }

        checksum += length;
        checksum += unicode[0];
        checksum += unicode[length - 1];
    }

    return checksum == 45400000 ? 0 : 2;
}
