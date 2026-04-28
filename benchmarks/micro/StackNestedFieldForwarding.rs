struct Inner {
    value: i64,
    salt: i64,
}

struct Outer {
    left: Inner,
    right: Inner,
}

fn accumulate(seed: i64, salt: i64) -> i64 {
    let mut outer = Outer {
        left: Inner { value: seed, salt },
        right: Inner {
            value: salt,
            salt: seed,
        },
    };
    outer.left.value = outer.left.value + outer.right.salt;
    let first = outer.left.value;
    outer.right.value = first ^ (outer.right.value >> 5);
    outer.left.value + outer.right.value + outer.left.salt
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
