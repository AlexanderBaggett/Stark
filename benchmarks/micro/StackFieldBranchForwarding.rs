struct Pair {
    left: i64,
    right: i64,
}

fn accumulate(seed: i64, salt: i64, choose_left: bool) -> i64 {
    let mut pair = Pair {
        left: seed,
        right: salt,
    };
    pair.left = pair.left + pair.right;
    if choose_left {
        return pair.left + pair.right;
    }

    pair.left - pair.right
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += accumulate(total % 1_000_000_007, i as i64, i % 2 == 0);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
