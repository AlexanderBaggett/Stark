const ROUNDS: u16 = 1200;
const ITEMS_PER_ROUND: usize = 256;
const SELF_TEST_ROUNDS: u16 = 7;
const SELF_TEST_SEED: u64 = 19;
const SELF_TEST_EXPECTED: u64 = 2_928_019_188_430_704_367;

struct Pair {
    left: u64,
    right: u64,
}

#[inline(never)]
fn run_round(seed: u64) -> u64 {
    let mut pairs: Vec<Pair> = Vec::with_capacity(ITEMS_PER_ROUND);
    let mut values: Vec<u64> = Vec::with_capacity(ITEMS_PER_ROUND);
    let mut checksum = seed.wrapping_add(1_469_598_103_934_665_603);

    for index in 0..ITEMS_PER_ROUND {
        let wide_index = index as u64;
        pairs.push(Pair {
            left: checksum.wrapping_add((wide_index.wrapping_add(1)).wrapping_mul(1_099_511_628_211)),
            right: (checksum ^ wide_index.wrapping_mul(780_291_637))
                .wrapping_add(1_442_695_040_888_963_407),
        });

        let pair = &pairs[index];
        let mixed = (pair
            .left
            .wrapping_mul(6_364_136_223_846_793_005)
            ^ pair.right)
            .wrapping_add(wide_index);
        values.push(mixed);
        checksum = (checksum ^ mixed).wrapping_mul(1_099_511_628_211);
    }

    for (index, value) in values.iter().enumerate() {
        checksum = checksum.wrapping_add(*value)
            ^ ((index as u64).wrapping_mul(11_400_714_819_323_198_485));
    }

    checksum
}

#[inline(never)]
fn run(rounds: u16, seed: u64) -> u64 {
    let mut checksum = seed;
    for round in 0..rounds {
        checksum = run_round(
            checksum.wrapping_add((round as u64).wrapping_mul(7_046_029_254_386_353_131)),
        );
    }

    checksum
}

fn main() {
    if run(SELF_TEST_ROUNDS, SELF_TEST_SEED) != SELF_TEST_EXPECTED {
        std::process::exit(1);
    }

    let seed = (std::process::id() as u64).wrapping_add(SELF_TEST_SEED);
    let result = run(ROUNDS, seed);
    if result == 0 || result == seed {
        std::process::exit(2);
    }
}
