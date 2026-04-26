#include <stdint.h>
#include <unistd.h>

int main(void) {
    int32_t seed = (int32_t)(getpid() % 13);
    int64_t score = 0;

    for (int32_t i = 0; i < 200000; i += 1) {
        int32_t value = (i + seed) % 10;

        if (value < 3) {
            score += 3;
        } else if (value < 7) {
            score += 5;
        } else {
            score += 7;
        }

        switch (value) {
            case 0:
                score += 11;
                break;
            case 1:
                score += 13;
                break;
            default:
                score += 17;
                break;
        }
    }

    return score <= 0 ? 1 : 0;
}
