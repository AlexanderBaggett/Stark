fn score(left: i32, right: i32, salt: i64) -> i64 {
    let saturated = left.saturating_add(right);
    let wrapped = left.wrapping_add(right);

    if saturated > 15 {
        return salt - 10_000;
    }

    if wrapped > 15 {
        return salt - 20_000;
    }

    (((saturated as i64) * 37) + ((wrapped as i64) * 17) + salt) % 1_000_003
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += score(i % 11, i % 6, total % 97);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
