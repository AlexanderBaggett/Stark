fn score(value: i32, salt: i64) -> i64 {
    let masked = value & 255;
    let shifted = masked << 1;
    let folded = shifted | (value & 3);
    let forced = folded | 2048;

    if forced == 0 {
        return salt - 1;
    }

    if folded < 512 {
        salt + folded as i64
    } else {
        salt - folded as i64
    }
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += score(i % 1024, total % 97);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
