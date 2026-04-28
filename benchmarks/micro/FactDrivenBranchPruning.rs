fn score(value: i32, salt: i64) -> i64 {
    if value < 20 {
        (((value as i64) * 37) + salt) % 1_000_003
    } else {
        salt - 1000
    }
}

fn switch_score(value: i32, salt: i64) -> i64 {
    match value {
        10 => salt + 10,
        40 => salt + 40,
        41 => salt + 41,
        _ => salt - 3,
    }
}

fn nested_score(value: i32, salt: i64) -> i64 {
    if value < 10 {
        if value >= 10 {
            return salt - 10_000;
        }

        (((value as i64) * 13) + salt) % 1_000_003
    } else {
        salt + value as i64
    }
}

fn main() {
    let mut total = std::process::id() as i64;

    for i in 0_i32..200_000 {
        total += score(i % 11, total % 97);
        total += switch_score(10 + (i % 3), total % 31);
        total += nested_score(i % 21, total % 53);
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
