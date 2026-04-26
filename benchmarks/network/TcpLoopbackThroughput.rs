use std::io::{Read, Write};
use std::net::{Ipv4Addr, Shutdown, SocketAddrV4, TcpListener, TcpStream};
use std::process;

const CHUNK_BYTES: usize = 4096;
const ITERATIONS: usize = 256;
const EXPECTED_BYTES: usize = CHUNK_BYTES * ITERATIONS;

fn endpoint(attempt: u16) -> SocketAddrV4 {
    let pid = process::id();
    let port = 41_000 + (pid % 20_000) as u16 + attempt;
    SocketAddrV4::new(Ipv4Addr::LOCALHOST, port)
}

fn run() -> Result<(), i32> {
    let write_buffer = [0_u8; CHUNK_BYTES];
    let mut read_buffer = [0_u8; CHUNK_BYTES];
    let mut listener = None;
    let mut bound_endpoint = endpoint(0);
    for attempt in 0..64 {
        bound_endpoint = endpoint(attempt);
        match TcpListener::bind(bound_endpoint) {
            Ok(value) => {
                listener = Some(value);
                break;
            }
            Err(_) => {}
        }
    }

    let listener = listener.ok_or(1)?;
    let mut client = TcpStream::connect(bound_endpoint).map_err(|_| 2)?;
    let (mut server, _) = listener.accept().map_err(|_| 3)?;
    drop(listener);

    let mut total_read = 0_usize;
    for _ in 0..ITERATIONS {
        client.write_all(&write_buffer).map_err(|_| 4)?;
        server.read_exact(&mut read_buffer).map_err(|_| 5)?;
        total_read += CHUNK_BYTES;
    }

    client.shutdown(Shutdown::Write).map_err(|_| 6)?;
    if total_read != EXPECTED_BYTES {
        return Err(7);
    }

    Ok(())
}

fn main() {
    if let Err(code) = run() {
        process::exit(code);
    }
}
