const ELEMENT_COUNT: usize = 32;
const ITERATIONS: i32 = 200_000;
const CHECKSUM_MODULUS: i64 = 1_000_000_007;

fn add_i64(left: &[i64; ELEMENT_COUNT], right: &[i64; ELEMENT_COUNT], output: &mut [i64; ELEMENT_COUNT]) {
    for ((slot, left_value), right_value) in output.iter_mut().zip(left.iter()).zip(right.iter()) {
        *slot = *left_value + *right_value;
    }
}

fn main() {
    let mut left = [
        1_i64, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32,
    ];
    let mut right = [
        32_i64, 31, 30, 29, 28, 27, 26, 25,
        24, 23, 22, 21, 20, 19, 18, 17,
        16, 15, 14, 13, 12, 11, 10, 9,
        8, 7, 6, 5, 4, 3, 2, 1,
    ];
    let mut output = [0_i64; ELEMENT_COUNT];
    let mut checksum = std::process::id() as i64;

    for _ in 0..ITERATIONS {
        left[0] += 1;
        right[31] += 1;
        add_i64(&left, &right, &mut output);
        checksum += output[0] + output[31];
        checksum %= CHECKSUM_MODULUS;
    }

    if checksum <= 0 {
        std::process::exit(1);
    }
}
