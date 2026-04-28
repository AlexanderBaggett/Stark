fn identity<T>(value: T) -> T {
    value
}

fn mix_core(value: i64, salt: i64) -> i64 {
    (((value * 31) + salt) ^ (value >> 3)) % 1_000_003
}

fn mix<T>(value: i64, salt: i64, _tag: T) -> i64 {
    mix_core(identity(value), identity(salt))
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += mix(i as i64, total % 97, i);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
