fn main() {
    let seed = (std::process::id() % 13) as i32;
    let mut score = 0_i64;

    for i in 0_i32..200_000 {
        let value = (i + seed) % 10;

        if value < 3 {
            score += 3;
        } else if value < 7 {
            score += 5;
        } else {
            score += 7;
        }

        match value {
            0 => score += 11,
            1 => score += 13,
            _ => score += 17,
        }
    }

    if score <= 0 {
        std::process::exit(1);
    }
}
