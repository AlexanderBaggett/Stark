const ELEMENT_COUNT: usize = 32;
const SMALL_COUNT: usize = 4;
const ITERATIONS: i32 = 200_000;
const CHECKSUM_MODULUS: i64 = 1_000_000_007;

fn copy_i64(input: &[i64; ELEMENT_COUNT], output: &mut [i64; ELEMENT_COUNT]) {
    output.copy_from_slice(input);
}

fn copy_with_scalar_work(input: &[i64; ELEMENT_COUNT], output: &mut [i64; ELEMENT_COUNT]) {
    output.copy_from_slice(input);
    output[0] += ELEMENT_COUNT as i64;
}

fn overlap_safe_copy_with_scalar_work(input: &[i64], output: &mut [i64]) {
    let mut temporary = [0_i64; SMALL_COUNT];
    temporary.copy_from_slice(&input[..SMALL_COUNT]);
    output[..SMALL_COUNT].copy_from_slice(&temporary);
    output[0] += 1;
}

fn fill_i64(output: &mut [i64; ELEMENT_COUNT], value: i64) {
    for (index, slot) in output.iter_mut().enumerate() {
        *slot = value + index as i64;
    }
}

fn fill_bytes_with_scalar_work(output: &mut [i8; ELEMENT_COUNT], value: i8) {
    output.fill(value);
    output[0] = ELEMENT_COUNT as i8;
}

fn transform_i64(input: &[i64; ELEMENT_COUNT], output: &mut [i64; ELEMENT_COUNT]) {
    for (slot, value) in output.iter_mut().zip(input.iter()) {
        *slot = *value + 1;
    }
}

fn main() {
    let mut input = [
        1_i64, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24,
        25, 26, 27, 28, 29, 30, 31, 32,
    ];
    let mut scratch = [0_i64; ELEMENT_COUNT];
    let mut output = [0_i64; ELEMENT_COUNT];
    let mut bytes = [0_i8; ELEMENT_COUNT];
    let mut checksum = std::process::id() as i64;

    for iteration in 0..ITERATIONS {
        input[0] += 1;
        fill_i64(&mut scratch, iteration as i64);
        copy_i64(&input, &mut output);
        copy_with_scalar_work(&input, &mut scratch);
        overlap_safe_copy_with_scalar_work(&scratch[..SMALL_COUNT], &mut output[..SMALL_COUNT]);
        fill_bytes_with_scalar_work(&mut bytes, (iteration % 127) as i8);
        transform_i64(&scratch, &mut output);
        checksum += output[0] + output[31] + bytes[0] as i64;
        checksum %= CHECKSUM_MODULUS;
    }

    if checksum <= 0 {
        std::process::exit(1);
    }
}
