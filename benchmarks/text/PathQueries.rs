fn is_separator(value: u8) -> bool {
    value == b'/'
}

struct PathFacts {
    end: usize,
    segment_start: usize,
    extension_start: usize,
    directory_length: usize,
    has_extension: bool,
}

impl PathFacts {
    fn new(path: &str) -> Self {
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
            end,
            segment_start,
            extension_start,
            directory_length,
            has_extension,
        }
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
    let mut checksum = 0_i64;

    for _ in 0_i32..10_000 {
        let beta = PathFacts::new("alpha/beta.txt");
        checksum += beta.extension_len() as i64;
        checksum += beta.base_name_len() as i64;
        checksum += beta.directory_name_len() as i64;

        let archive = PathFacts::new("archive.tar.gz");
        checksum += archive.extension_len() as i64;
        checksum += archive.base_name_len() as i64;
        checksum += archive.directory_name_len() as i64;
    }

    if checksum != 270_000 {
        std::process::exit(1);
    }
}
