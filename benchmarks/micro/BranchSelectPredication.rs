#[inline(never)]
fn choose_value(flag: bool, left: i64, right: i64) -> i64 {
    if flag {
        left
    } else {
        right
    }
}

fn main() {
    let seed = std::process::id() as i64;
    let mut total = seed & 1023;

    for i in 0_i32..500_000 {
        let wide = i as i64;
        let flag = ((wide + total) & 1) == 0;
        total += choose_value(flag, wide + 3, total - wide);
        total %= 1_000_000_007;
    }

    if total == 0 {
        std::process::exit(1);
    }
}
