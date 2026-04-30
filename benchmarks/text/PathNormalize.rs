fn normalize_separators(output: &mut String, source: &str) -> bool {
    output.clear();
    let mut previous_separator = false;

    for mut value in source.bytes() {
        if value == b'\\' {
            value = b'/';
        }

        let separator = value == b'/';
        if separator && previous_separator {
            continue;
        }

        if output.len() + 1 > output.capacity() {
            return false;
        }

        output.push(char::from(value));
        previous_separator = separator;
    }

    true
}

fn main() {
    let mut output = String::with_capacity(64);
    let mut checksum = 0_i64;

    for _ in 0_i32..5000 {
        if !normalize_separators(&mut output, "alpha//beta///gamma.txt") {
            std::process::exit(2);
        }

        checksum += output.len() as i64;

        if !normalize_separators(&mut output, "alpha///beta.txt") {
            std::process::exit(3);
        }

        checksum += output.len() as i64;
    }

    if checksum != 170_000 {
        std::process::exit(4);
    }
}
