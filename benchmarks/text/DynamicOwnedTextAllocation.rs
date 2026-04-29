fn digit_count(value: i32) -> usize {
    if value < 10 {
        1
    } else if value < 100 {
        2
    } else if value < 1000 {
        3
    } else if value < 10_000 {
        4
    } else if value < 100_000 {
        5
    } else if value < 1_000_000 {
        6
    } else if value < 10_000_000 {
        7
    } else if value < 100_000_000 {
        8
    } else if value < 1_000_000_000 {
        9
    } else {
        10
    }
}

fn append_ascii_digits(text: &mut Vec<u8>, value: i32) {
    let digits = digit_count(value);
    let mut divisor = 1_i32;
    for _ in 1..digits {
        divisor *= 10;
    }

    let mut remaining = value;
    while divisor > 0 {
        let digit = remaining / divisor;
        text.push(b'0' + digit as u8);
        remaining %= divisor;
        divisor /= 10;
    }
}

fn append_unicode_digits(text: &mut Vec<char>, value: i32) {
    let digits = digit_count(value);
    let mut divisor = 1_i32;
    for _ in 1..digits {
        divisor *= 10;
    }

    let mut remaining = value;
    while divisor > 0 {
        let digit = remaining / divisor;
        text.push(char::from(b'0' + digit as u8));
        remaining %= divisor;
        divisor /= 10;
    }
}

fn ascii_checksum(text: &[u8]) -> i64 {
    text.len() as i64 + text.iter().map(|value| i64::from(*value)).sum::<i64>()
}

fn unicode_checksum(text: &[char]) -> i64 {
    text.len() as i64 + text.iter().map(|value| i64::from(*value as u32)).sum::<i64>()
}

fn main() {
    let mut checksum = 0_i64;

    for i in 0_i32..1000 {
        let digit_count = digit_count(i);

        let mut ascii_text = Vec::with_capacity(digit_count);
        append_ascii_digits(&mut ascii_text, i);
        checksum += ascii_checksum(&ascii_text);

        let mut unicode_text = Vec::with_capacity(digit_count);
        append_unicode_digits(&mut unicode_text, i);
        checksum += unicode_checksum(&unicode_text);

        let mut ascii_label = Vec::with_capacity(7 + ascii_text.len());
        ascii_label.extend_from_slice(b"Score: ");
        ascii_label.extend_from_slice(&ascii_text);
        checksum += ascii_checksum(&ascii_label);

        let mut unicode_label = Vec::with_capacity(7 + unicode_text.len());
        unicode_label.extend("Score: ".chars());
        unicode_label.extend(unicode_text.iter().copied());
        checksum += unicode_checksum(&unicode_label);
    }

    if checksum != 1_830_440 {
        std::process::exit(5);
    }
}
