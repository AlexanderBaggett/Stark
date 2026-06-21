// stark-bench: skip-c-windows
#include <stdint.h>
#include <unistd.h>

#define ITERATIONS UINT64_C(300000)
#define SELF_TEST_ITERATIONS UINT64_C(64)
#define SELF_TEST_SEED UINT64_C(17)
#define SELF_TEST_EXPECTED UINT64_C(318451075583008527)

__attribute__((noinline)) static uint64_t
run_tail_state_machine(uint64_t remaining, uint64_t value) {
  uint8_t state = 0;

  while (remaining != 0) {
    switch (state) {
    case 0:
      value = ((value * UINT64_C(1664525)) + remaining) ^ UINT64_C(1013904223);
      state = 1;
      break;

    case 1:
      value = ((value + (remaining * UINT64_C(1103515245))) ^ (value >> 7)) +
              UINT64_C(12345);
      state = 2;
      break;

    case 2:
      value =
          ((value ^ (remaining + UINT64_C(2654435769))) * UINT64_C(22695477)) +
          UINT64_C(1);
      state = 3;
      break;

    default:
      value =
          (value + (remaining ^ UINT64_C(747796405))) * UINT64_C(2891336453);
      state = 0;
      break;
    }

    remaining -= 1;
  }

  return value;
}

int main(void) {
  if (run_tail_state_machine(SELF_TEST_ITERATIONS, SELF_TEST_SEED) !=
      SELF_TEST_EXPECTED) {
    return 1;
  }

  uint64_t seed = ((uint64_t)getpid()) + SELF_TEST_SEED;
  uint64_t result = run_tail_state_machine(ITERATIONS, seed);
  return (result == 0 || result == seed) ? 1 : 0;
}
