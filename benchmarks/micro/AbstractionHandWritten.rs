fn score(value: i64, salt: i64) -> i64 {
    (((value * 31) + salt) ^ (value >> 3)) % 1_000_003
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += score(i as i64, total % 97);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
