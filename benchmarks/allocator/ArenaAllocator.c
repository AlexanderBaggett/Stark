// stark-bench: skip-c-windows
#include <stdint.h>
#include <stdlib.h>
#include <unistd.h>

#define ROUNDS UINT16_C(1200)
#define ITEMS_PER_ROUND UINT16_C(256)
#define SELF_TEST_ROUNDS UINT16_C(7)
#define SELF_TEST_SEED UINT64_C(19)
#define SELF_TEST_EXPECTED UINT64_C(2928019188430704367)

typedef struct {
  uint64_t left;
  uint64_t right;
} Pair;

typedef struct {
  uint8_t *data;
  size_t capacity;
  size_t offset;
} Arena;

static size_t align_up(size_t value, size_t alignment) {
  return (value + alignment - 1u) & ~(alignment - 1u);
}

static void *arena_alloc(Arena *arena, size_t size, size_t alignment) {
  size_t aligned = align_up(arena->offset, alignment);
  size_t end = aligned + size;
  if (end > arena->capacity) {
    return NULL;
  }

  arena->offset = end;
  return arena->data + aligned;
}

__attribute__((noinline)) static uint64_t run_round(uint64_t seed) {
  enum {
    PAIR_ALIGN = _Alignof(Pair),
    U64_ALIGN = _Alignof(uint64_t),
    MAX_ALIGN = PAIR_ALIGN > U64_ALIGN ? PAIR_ALIGN : U64_ALIGN
  };
  size_t capacity = align_up(sizeof(uint64_t) * ITEMS_PER_ROUND, MAX_ALIGN) +
                    (sizeof(Pair) + MAX_ALIGN) * ITEMS_PER_ROUND;
  Arena arena = {
      .data = (uint8_t *)malloc(capacity),
      .capacity = capacity,
      .offset = 0,
  };
  if (arena.data == NULL) {
    return 0;
  }

  uint64_t *values =
      (uint64_t *)arena_alloc(&arena, sizeof(uint64_t) * ITEMS_PER_ROUND,
                              _Alignof(uint64_t));
  if (values == NULL) {
    free(arena.data);
    return 0;
  }

  uint64_t checksum = seed + UINT64_C(1469598103934665603);
  for (uint16_t index = 0; index < ITEMS_PER_ROUND; index += 1) {
    uint64_t wide_index = (uint64_t)index;
    Pair *pair = (Pair *)arena_alloc(&arena, sizeof(Pair), _Alignof(Pair));
    if (pair == NULL) {
      free(arena.data);
      return 0;
    }

    pair->left = checksum + ((wide_index + UINT64_C(1)) * UINT64_C(1099511628211));
    pair->right =
        (checksum ^ (wide_index * UINT64_C(780291637))) + UINT64_C(1442695040888963407);
    uint64_t mixed =
        ((pair->left * UINT64_C(6364136223846793005)) ^ pair->right) + wide_index;
    values[index] = mixed;
    checksum = (checksum ^ mixed) * UINT64_C(1099511628211);
  }

  for (uint16_t index = 0; index < ITEMS_PER_ROUND; index += 1) {
    checksum = (checksum + values[index]) ^
               ((uint64_t)index * UINT64_C(11400714819323198485));
  }

  free(arena.data);
  return checksum;
}

__attribute__((noinline)) static uint64_t run(uint16_t rounds, uint64_t seed) {
  uint64_t checksum = seed;
  for (uint16_t round = 0; round < rounds; round += 1) {
    checksum = run_round(checksum + ((uint64_t)round * UINT64_C(7046029254386353131)));
  }

  return checksum;
}

int main(void) {
  if (run(SELF_TEST_ROUNDS, SELF_TEST_SEED) != SELF_TEST_EXPECTED) {
    return 1;
  }

  uint64_t seed = ((uint64_t)getpid()) + SELF_TEST_SEED;
  uint64_t result = run(ROUNDS, seed);
  return (result == 0 || result == seed) ? 2 : 0;
}
