struct BoxValue {
    value: i64,
}

#[inline(never)]
fn read_value(value: &BoxValue) -> i64 {
    value.value
}

fn accumulate(seed: i64, salt: i64) -> i64 {
    let mut left = BoxValue { value: seed };
    let right = BoxValue { value: salt };
    left.value = seed + salt;
    let observed = read_value(&right);
    left.value = (seed ^ observed) + 17;
    left.value + observed
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
