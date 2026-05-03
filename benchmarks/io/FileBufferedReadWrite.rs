use std::fs::{remove_file, File};
use std::io::{Read, Seek, SeekFrom, Write};

fn sum_bytes(values: &[i8]) -> i64 {
    let mut checksum = 0_i64;
    for value in values {
        checksum += *value as i64;
    }

    checksum
}

fn main() {
    const ITERATIONS: usize = 64;
    const CHUNKS: usize = 33;
    let source: [i8; 32] = [
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
    ];
    let mut destination = [0_u8; 32];
    let bytes: Vec<u8> = source.iter().map(|value| *value as u8).collect();
    let mut checksum = 0_i64;

    let _ = remove_file("experimental-buffered-rw.tmp");

    for _ in 0..ITERATIONS {
        {
            let mut writer = File::create("experimental-buffered-rw.tmp").expect("create");
            for _ in 0..32 {
                writer.write_all(&bytes).expect("write");
            }
            writer.write_all(&bytes).expect("write tail");
        }

        {
            let mut reader = File::open("experimental-buffered-rw.tmp").expect("open");
            reader.seek(SeekFrom::Start(0)).expect("seek");
            for _ in 0..CHUNKS {
                reader.read_exact(&mut destination).expect("read");
                let signed: Vec<i8> = destination.iter().map(|value| *value as i8).collect();
                checksum += sum_bytes(&signed);
            }
        }

        remove_file("experimental-buffered-rw.tmp").expect("delete");
    }

    if checksum != 574_464 {
        std::process::exit(1);
    }
}
