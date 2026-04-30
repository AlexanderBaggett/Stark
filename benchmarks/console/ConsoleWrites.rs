use std::io::{self, Write};

fn main() {
    const ITERATIONS: usize = 64;
    let mut stdout = io::stdout().lock();
    let mut stderr = io::stderr().lock();

    for _ in 0..ITERATIONS {
        stdout.write_all(b"small").unwrap();
        stdout.write_all(b" line\n").unwrap();
        stdout.write_all("wide α\n".as_bytes()).unwrap();
        stdout
            .write_all(b"buffer payload 0123456789ABCDEF\n")
            .unwrap();
        stderr.write_all(b"err\n").unwrap();
    }

    stdout.flush().unwrap();
    stderr.flush().unwrap();
}
