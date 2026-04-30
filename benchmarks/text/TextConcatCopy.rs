fn concat_ascii(output: &mut String, left: &str, right: &str) -> bool {
    output.clear();
    if left.len() + right.len() > output.capacity() {
        return false;
    }

    output.push_str(left);
    output.push_str(right);
    true
}

fn concat_unicode(output: &mut Vec<char>, left: &str, right: &str) -> bool {
    output.clear();
    if left.chars().count() + right.chars().count() > output.capacity() {
        return false;
    }

    output.extend(left.chars());
    output.extend(right.chars());
    true
}

fn checksum_ascii(text: &str) -> i64 {
    let bytes = text.as_bytes();
    text.len() as i64 + i64::from(bytes[0]) + i64::from(bytes[bytes.len() - 1])
}

fn checksum_unicode(text: &[char]) -> i64 {
    text.len() as i64 + i64::from(text[0] as u32) + i64::from(text[text.len() - 1] as u32)
}

fn main() {
    let mut ascii = String::with_capacity(32);
    let mut unicode: Vec<char> = Vec::with_capacity(32);
    let mut checksum = 0_i64;

    for _ in 0_i32..5000 {
        if !concat_ascii(&mut ascii, "prefix/", "body") {
            std::process::exit(1);
        }

        checksum += checksum_ascii(&ascii);

        if !concat_unicode(&mut unicode, "Value:", "body") {
            std::process::exit(2);
        }

        checksum += checksum_unicode(&unicode);
    }

    if checksum != 2_305_000 {
        std::process::exit(3);
    }
}
