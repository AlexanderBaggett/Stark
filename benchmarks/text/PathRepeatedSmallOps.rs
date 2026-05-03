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

struct PathFacts<'a> {
    path: &'a str,
    end: usize,
    segment_start: usize,
    extension_start: usize,
    directory_length: usize,
    has_extension: bool,
}

impl<'a> PathFacts<'a> {
    fn new(path: &'a str) -> Self {
        let bytes = path.as_bytes();
        let mut end = bytes.len();
        while end > 1 && is_separator(bytes[end - 1]) {
            end -= 1;
        }

        let separator = (0..end).rev().find(|&index| is_separator(bytes[index]));
        let segment_start = separator.map_or(0, |index| index + 1);
        let directory_length = match separator {
            None => 0,
            Some(0) => 1,
            Some(index) => index,
        };

        let mut extension_start = end;
        let mut has_extension = false;
        for index in ((segment_start + 1)..end).rev() {
            if bytes[index] == b'.' {
                extension_start = index;
                has_extension = true;
                break;
            }
        }

        Self {
            path,
            end,
            segment_start,
            extension_start,
            directory_length,
            has_extension,
        }
    }

    fn path_len(&self) -> usize {
        self.path.len()
    }

    fn extension_len(&self) -> usize {
        if self.has_extension {
            self.end - self.extension_start
        } else {
            0
        }
    }

    fn base_name_len(&self) -> usize {
        if self.segment_start >= self.end {
            return 0;
        }

        let end = if self.has_extension {
            self.extension_start
        } else {
            self.end
        };
        end - self.segment_start
    }

    fn directory_name_len(&self) -> usize {
        self.directory_length
    }
}

fn main() {
    let mut output = String::with_capacity(64);
    let mut checksum = 0_i64;

    for _ in 0_i32..5000 {
        if !join_path(&mut output, "alpha", "beta.txt") {
            std::process::exit(1);
        }

        checksum += output.len() as i64;
        let beta = PathFacts::new("alpha/beta.txt");
        checksum += beta.path_len() as i64;
        checksum += beta.extension_len() as i64;
        checksum += beta.base_name_len() as i64;
        checksum += beta.directory_name_len() as i64;

        if !join_path(&mut output, "alpha/beta", "gamma.txt") {
            std::process::exit(2);
        }

        checksum += output.len() as i64;
        checksum += PathFacts::new("archive.tar.gz").extension_len() as i64;
    }

    if checksum != 320_000 {
        std::process::exit(3);
    }
}
