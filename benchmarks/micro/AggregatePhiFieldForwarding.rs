#[allow(dead_code)]
struct Pair {
    value: i64,
    tag: i64,
}

fn read_value(flag: bool, value: i64, tag: i64) -> i64 {
    let pair = if flag {
        Pair { value, tag }
    } else {
        Pair {
            value,
            tag: tag + 1,
        }
    };
    pair.value
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..300_000 {
        total += read_value((i & 1) == 0, total % 1_000_000_007, i as i64);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
