fn score(mut salt: i64) -> i64 {
    if "stark-performance".len() == 17 {
        salt = (salt + 51) % 1_000_003;
    } else {
        salt -= 100_000;
    }

    if "llvm-output".chars().count() != 11 {
        return salt - 77_777;
    }

    ((salt + 11) * 17) % 1_000_003
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += score(total + i as i64);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
