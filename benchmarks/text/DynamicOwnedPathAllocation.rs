fn is_separator(value: u8) -> bool {
    value == b'/'
}

fn join_alpha_beta(path: &mut Vec<u8>, left_has_trailing_separator: bool, right_has_leading_separator: bool) {
    path.extend_from_slice(b"alpha");

    if left_has_trailing_separator {
        path.push(b'/');
    }

    if !left_has_trailing_separator && !right_has_leading_separator {
        path.push(b'/');
    }

    if !left_has_trailing_separator && right_has_leading_separator {
        path.push(b'/');
    }

    path.extend_from_slice(b"beta.txt");
}

fn path_facts_checksum(path: &[u8]) -> i64 {
    let mut end = path.len();
    while end > 1 && is_separator(path[end - 1]) {
        end -= 1;
    }

    let separator = (0..end).rev().find(|&index| is_separator(path[index]));
    let segment_start = separator.map_or(0, |index| index + 1);
    let directory_length = match separator {
        None => 0,
        Some(0) => 1,
        Some(index) => index,
    };

    let mut extension_start = end;
    let mut has_extension = false;
    for index in ((segment_start + 1)..end).rev() {
        if path[index] == b'.' {
            extension_start = index;
            has_extension = true;
            break;
        }
    }

    let extension_length = if has_extension { end - extension_start } else { 0 };
    let base_name_end = if has_extension { extension_start } else { end };
    let base_name_length = if segment_start < end { base_name_end - segment_start } else { 0 };

    (path.len() + extension_length + base_name_length + directory_length) as i64
}

fn main() {
    let mut checksum = 0_i64;

    for _ in 0_i32..2000 {
        let mut first = Vec::with_capacity(14);
        join_alpha_beta(&mut first, false, false);
        checksum += path_facts_checksum(&first);

        let mut second = Vec::with_capacity(14);
        join_alpha_beta(&mut second, true, true);
        checksum += path_facts_checksum(&second);
    }

    if checksum != 108_000 {
        std::process::exit(3);
    }
}
