fn main() {
    let mut checksum = 0_i64;

    for i in 0_i32..1000 {
        let ascii = i.to_string();
        checksum += ascii.len() as i64;

        let unicode: Vec<char> = i.to_string().chars().collect();
        checksum += unicode.len() as i64;

        let label = format!("Score: {}", ascii);
        checksum += label.len() as i64;

        let unicode_label: Vec<char> = format!("Score: {}", unicode.iter().collect::<String>())
            .chars()
            .collect();
        checksum += unicode_label.len() as i64;
    }

    if checksum != 25_560 {
        std::process::exit(5);
    }
}
