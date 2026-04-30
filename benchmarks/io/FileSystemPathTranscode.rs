use std::fs::{create_dir, read_dir, remove_dir, remove_file, rename, File};
use std::io::Write;

fn write_utf16_line(file: &mut File, text: &str) {
    for unit in text.encode_utf16() {
        file.write_all(&unit.to_le_bytes()).expect("write utf16");
    }
    file.write_all(&10_u16.to_le_bytes()).expect("write newline");
}

fn first_entry_length(path: &str) -> i64 {
    for entry in read_dir(path).expect("read_dir") {
        let entry = entry.expect("entry");
        return entry.file_name().to_string_lossy().len() as i64;
    }

    -1
}

fn main() {
    const ITERATIONS: usize = 16;
    let _ = remove_file("experimental-io-bench-root/renamed.txt");
    let _ = remove_file("experimental-io-bench-root/child.txt");
    let _ = remove_dir("experimental-io-bench-root");
    let mut checksum = 0_i64;

    for _ in 0..ITERATIONS {
        create_dir("experimental-io-bench-root").expect("create_dir");
        let child_path = "experimental-io-bench-root/child.txt";
        checksum += child_path.len() as i64;

        {
            let mut file = File::create(child_path).expect("create child");
            write_utf16_line(&mut file, "child");
            write_utf16_line(&mut file, "wide");
        }

        checksum += child_path.len() as i64;
        checksum += first_entry_length("experimental-io-bench-root");

        rename(child_path, "experimental-io-bench-root/renamed.txt").expect("rename");
        remove_file("experimental-io-bench-root/renamed.txt").expect("remove file");
        remove_dir("experimental-io-bench-root").expect("remove dir");
    }

    if checksum != 1296 {
        std::process::exit(1);
    }
}
