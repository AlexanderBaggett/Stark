fn is_separator(value: u8) -> bool {
    value == b'/'
}

fn join_path(output: &mut String, left: &str, right: &str) -> bool {
    let left_bytes = left.as_bytes();
    let right_bytes = right.as_bytes();
    let mut right_start = 0_usize;
    let mut insert_separator = false;

    if !left_bytes.is_empty() && !right_bytes.is_empty() {
        if is_separator(left_bytes[left_bytes.len() - 1]) {
            if is_separator(right_bytes[0]) {
                right_start = 1;
            }
        } else if !is_separator(right_bytes[0]) {
            insert_separator = true;
        }
    }

    output.clear();
    if left.len() + right.len() + usize::from(insert_separator) > output.capacity() {
        return false;
    }

    output.push_str(left);
    if insert_separator {
        output.push('/');
    }
    output.push_str(&right[right_start..]);
    true
}

fn main() {
    let mut output = String::with_capacity(64);
    let mut checksum = 0_i64;

    for _ in 0_i32..5000 {
        if !join_path(&mut output, "alpha", "beta.txt") {
            std::process::exit(1);
        }

        checksum += output.len() as i64;

        if !join_path(&mut output, "alpha/beta", "gamma.txt") {
            std::process::exit(2);
        }

        checksum += output.len() as i64;
    }

    if checksum != 170_000 {
        std::process::exit(3);
    }
}
