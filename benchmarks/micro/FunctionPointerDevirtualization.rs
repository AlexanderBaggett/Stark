#[inline(never)]
fn mix(value: i64, salt: i64) -> i64 {
    ((value * 31) + salt) % 1_000_003
}

fn main() {
    let mut total = 17_i64;

    for i in 0_i32..200_000 {
        let mut op: fn(i64, i64) -> i64 = mix;
        if (i % 2) != 0 {
            op = mix;
        }

        total += op(i as i64, total % 97);
        total %= 1_000_000_007;
    }

    if total != 420_921_655 {
        std::process::exit(1);
    }
}
