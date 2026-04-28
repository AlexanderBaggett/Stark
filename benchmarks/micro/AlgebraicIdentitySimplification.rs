fn normalize(value: i64) -> i64 {
    let add = value + 0;
    let multiply = add * 1;
    let masked = multiply & -1;
    let shifted = masked << 0;
    let same_and = shifted & shifted;
    let same_or = same_and | same_and;
    let zero_xor = same_or ^ same_or;
    let zero_and = value & 0;
    let zero_multiply = value * 0;
    let zero_subtract = value - value;
    let all_ones = value | -1;
    ((shifted ^ 0) + zero_xor + zero_and + zero_multiply + zero_subtract + (all_ones & 1)) - 1
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += normalize(((i as i64) * 17) + (total % 97));
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
