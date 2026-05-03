use std::fs::{create_dir, read_dir, remove_dir, remove_file, File};

const ENTRIES: usize = 128;
const ASCII_ENTRIES: usize = 120;
const UNICODE_ENTRIES: usize = 8;
const ITERATIONS: usize = 128;
const EXPECTED_CHECKSUM: i64 = 212_992;
const ROOT_NAME: &str = "dir-enum-bench";

fn ascii_entry_path(index: usize) -> String {
    format!("{ROOT_NAME}/entry-{index:03}.tmp")
}

fn unicode_entry_path(index: usize) -> String {
    format!("{ROOT_NAME}/wide-é-{index}.tmp")
}

fn delete_entries() {
    for index in 0..ASCII_ENTRIES {
        let _ = remove_file(ascii_entry_path(index));
    }

    for index in 0..UNICODE_ENTRIES {
        let _ = remove_file(unicode_entry_path(index));
    }

    let _ = remove_dir(ROOT_NAME);
}

fn create_entries() -> bool {
    delete_entries();
    if create_dir(ROOT_NAME).is_err() {
        return false;
    }

    for index in 0..ASCII_ENTRIES {
        if File::create(ascii_entry_path(index)).is_err() {
            return false;
        }
    }

    for index in 0..UNICODE_ENTRIES {
        if File::create(unicode_entry_path(index)).is_err() {
            return false;
        }
    }

    true
}

fn enumerate_once() -> i64 {
    let mut checksum = 0_i64;
    let mut count = 0_usize;

    let entries = match read_dir(ROOT_NAME) {
        Ok(entries) => entries,
        Err(_) => return -1,
    };

    for entry in entries {
        let entry = match entry {
            Ok(entry) => entry,
            Err(_) => return -1,
        };

        checksum += entry.file_name().to_string_lossy().len() as i64;
        count += 1;
    }

    if count == ENTRIES {
        checksum
    } else {
        -1
    }
}

fn main() {
    if !create_entries() {
        delete_entries();
        std::process::exit(1);
    }

    let mut checksum = 0_i64;
    for _ in 0..ITERATIONS {
        let iteration_checksum = enumerate_once();
        if iteration_checksum < 0 {
            delete_entries();
            std::process::exit(2);
        }

        checksum += iteration_checksum;
    }

    delete_entries();
    if checksum != EXPECTED_CHECKSUM {
        std::process::exit(3);
    }
}
