const ITERATIONS: u64 = 300_000;
const SELF_TEST_ITERATIONS: u64 = 64;
const SELF_TEST_SEED: u64 = 17;
const SELF_TEST_EXPECTED: u64 = 318_451_075_583_008_527;

#[inline(never)]
fn run_tail_state_machine(mut remaining: u64, mut value: u64) -> u64 {
    let mut state = 0_u8;

    while remaining != 0 {
        match state {
            0 => {
                value = value.wrapping_mul(1_664_525).wrapping_add(remaining) ^ 1_013_904_223;
                state = 1;
            }
            1 => {
                value = (value.wrapping_add(remaining.wrapping_mul(1_103_515_245)) ^ (value >> 7))
                    .wrapping_add(12_345);
                state = 2;
            }
            2 => {
                value = (value ^ remaining.wrapping_add(2_654_435_769))
                    .wrapping_mul(22_695_477)
                    .wrapping_add(1);
                state = 3;
            }
            _ => {
                value = value
                    .wrapping_add(remaining ^ 747_796_405)
                    .wrapping_mul(2_891_336_453);
                state = 0;
            }
        }

        remaining -= 1;
    }

    value
}

fn main() {
    if run_tail_state_machine(SELF_TEST_ITERATIONS, SELF_TEST_SEED) != SELF_TEST_EXPECTED {
        std::process::exit(1);
    }

    let seed = (std::process::id() as u64).wrapping_add(SELF_TEST_SEED);
    let result = run_tail_state_machine(ITERATIONS, seed);
    if result == 0 || result == seed {
        std::process::exit(1);
    }
}
