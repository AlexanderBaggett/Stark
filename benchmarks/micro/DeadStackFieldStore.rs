#![allow(unused_assignments, unused_variables)]

struct Pair {
    left: i64,
    right: i64,
}

fn accumulate(seed: i64, salt: i64) -> i64 {
    let mut pair = Pair {
        left: seed,
        right: salt,
    };
    pair.left = seed + salt;
    pair.right = seed ^ salt;
    ((seed * 3) + salt) % 1_000_000_007
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..250_000 {
        total += accumulate(total, i as i64);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
