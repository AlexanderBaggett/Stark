fn normalize(value: i64) -> i64 {
    let add = value + 0;
    let multiply = add * 1;
    let masked = multiply & -1;
    let shifted = masked << 0;
    let divided = shifted / 1;
    let right_shifted = divided >> 0;
    let same_and = shifted & shifted;
    let same_or = same_and | same_and;
    let zero_xor = same_or ^ same_or;
    let zero_and = value & 0;
    let zero_multiply = value * 0;
    let zero_subtract = value - value;
    let zero_modulo = value % 1;
    let all_ones = value | -1;
    ((right_shifted ^ 0) + zero_xor + zero_and + zero_multiply + zero_subtract + zero_modulo + (all_ones & 1)) - 1
}

fn normalize_slot(slot: i32, salt: i64) -> i64 {
    let modulo = slot % 8;
    let divided = slot / 8;
    salt + modulo as i64 + divided as i64
}

fn normalize_nonzero_slot(slot: i32, salt: i64) -> i64 {
    let divided = slot / slot;
    let modulo = slot % slot;
    salt + divided as i64 + modulo as i64
}

fn normalize_comparisons(value: i32, salt: i64) -> i64 {
    let copy = value;
    let equal = if value == copy { 1 } else { 100 };
    let not_equal = if value != copy { 100 } else { 1 };
    let less = if value < copy { 100 } else { 1 };
    let less_or_equal = if value <= copy { 1 } else { 100 };
    let greater = if value > copy { 100 } else { 1 };
    let greater_or_equal = if value >= copy { 1 } else { 100 };
    salt + (equal + not_equal + less + less_or_equal + greater + greater_or_equal) as i64
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += normalize(((i as i64) * 17) + (total % 97));
        total += normalize_slot(i & 7, total % 41);
        total += normalize_nonzero_slot((i & 7) + 1, total % 43);
        total += normalize_comparisons(i, total % 47);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
