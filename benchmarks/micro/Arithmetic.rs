fn main() {
    let seed = std::process::id() as i64;
    let mut total = seed % 97;

    for i in 0_i32..200_000 {
        let value = i as i64 + total;
        total = (total + (value * 3) - (value / 3) + (value % 11)) % 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
