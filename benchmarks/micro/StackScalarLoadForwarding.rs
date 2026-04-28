fn accumulate(seed: i64, salt: i64) -> i64 {
    let mut local = seed;
    local = local + salt;
    let first = local;
    local = first ^ (first >> 7);
    local + first
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += accumulate(total % 1_000_000_007, i as i64);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
