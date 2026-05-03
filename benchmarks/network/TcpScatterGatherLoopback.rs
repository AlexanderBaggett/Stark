use std::io::{IoSlice, IoSliceMut, Read, Write};
use std::net::{Ipv4Addr, Shutdown, SocketAddrV4, TcpListener, TcpStream};
use std::process;

const FIRST_CHUNK_BYTES: usize = 1536;
const SECOND_CHUNK_BYTES: usize = 2560;
const CHUNK_BYTES: usize = FIRST_CHUNK_BYTES + SECOND_CHUNK_BYTES;
const ITERATIONS: usize = 256;
const EXPECTED_BYTES: usize = CHUNK_BYTES * ITERATIONS;

fn endpoint(attempt: u16) -> SocketAddrV4 {
    let pid = process::id();
    let port = 41_000 + (pid % 20_000) as u16 + attempt;
    SocketAddrV4::new(Ipv4Addr::LOCALHOST, port)
}

fn consume(first_done: &mut usize, first_len: usize, second_done: &mut usize, mut amount: usize) {
    if *first_done < first_len {
        let remaining_first = first_len - *first_done;
        if amount <= remaining_first {
            *first_done += amount;
            return;
        }

        *first_done = first_len;
        amount -= remaining_first;
    }

    *second_done += amount;
}

fn write_all_vectored(stream: &mut TcpStream, first: &[u8], second: &[u8]) -> Result<(), i32> {
    let mut first_written = 0_usize;
    let mut second_written = 0_usize;
    while first_written < first.len() || second_written < second.len() {
        let written = if first_written < first.len() {
            let slices = [
                IoSlice::new(&first[first_written..]),
                IoSlice::new(&second[second_written..]),
            ];
            stream.write_vectored(&slices).map_err(|_| 4)?
        } else {
            stream.write(&second[second_written..]).map_err(|_| 4)?
        };

        if written == 0 {
            return Err(4);
        }

        consume(
            &mut first_written,
            first.len(),
            &mut second_written,
            written,
        );
    }

    Ok(())
}

fn read_exact_vectored(stream: &mut TcpStream, first: &mut [u8], second: &mut [u8]) -> Result<(), i32> {
    let mut first_read = 0_usize;
    let mut second_read = 0_usize;
    while first_read < first.len() || second_read < second.len() {
        let read = if first_read < first.len() {
            let mut slices = [
                IoSliceMut::new(&mut first[first_read..]),
                IoSliceMut::new(&mut second[second_read..]),
            ];
            stream.read_vectored(&mut slices).map_err(|_| 5)?
        } else {
            stream.read(&mut second[second_read..]).map_err(|_| 5)?
        };

        if read == 0 {
            return Err(5);
        }

        consume(&mut first_read, first.len(), &mut second_read, read);
    }

    Ok(())
}

fn run() -> Result<(), i32> {
    let write_first = [0_u8; FIRST_CHUNK_BYTES];
    let write_second = [0_u8; SECOND_CHUNK_BYTES];
    let mut read_first = [0_u8; FIRST_CHUNK_BYTES];
    let mut read_second = [0_u8; SECOND_CHUNK_BYTES];
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
        write_all_vectored(&mut client, &write_first, &write_second)?;
        read_exact_vectored(&mut server, &mut read_first, &mut read_second)?;
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
