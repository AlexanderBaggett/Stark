use std::fmt::{self, Write};

const U1024_LIMBS: usize = 16;
const TEXT_CAPACITY: usize = 320;

#[derive(Clone, Copy)]
struct U1024([u64; U1024_LIMBS]);

#[derive(Clone, Copy)]
struct I1024 {
    magnitude: U1024,
}

const I1024_MIN_VALUE: I1024 = I1024 {
    magnitude: U1024([
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0x8000_0000_0000_0000,
    ]),
};

const U1024_MAX_VALUE: U1024 = U1024([u64::MAX; U1024_LIMBS]);

impl U1024 {
    fn is_zero(self) -> bool {
        self.0.iter().all(|word| *word == 0)
    }

    fn divide_by_10(&mut self) -> u8 {
        let mut carry = 0_u128;

        for word in self.0.iter_mut().rev() {
            let current = (carry << 64) | u128::from(*word);
            *word = (current / 10) as u64;
            carry = current % 10;
        }

        carry as u8
    }
}

impl fmt::Display for U1024 {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let mut value = *self;
        if value.is_zero() {
            return formatter.write_char('0');
        }

        let mut reversed_digits = [0_u8; TEXT_CAPACITY];
        let mut length = 0_usize;
        while !value.is_zero() {
            reversed_digits[length] = b'0' + value.divide_by_10();
            length += 1;
        }

        let mut digits = [0_u8; TEXT_CAPACITY];
        for index in 0..length {
            digits[index] = reversed_digits[length - 1 - index];
        }

        let text = std::str::from_utf8(&digits[..length]).map_err(|_| fmt::Error)?;
        formatter.write_str(text)
    }
}

impl fmt::Display for I1024 {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_char('-')?;
        self.magnitude.fmt(formatter)
    }
}

fn checksum_text(text: &str) -> i64 {
    let bytes = text.as_bytes();
    text.len() as i64 + i64::from(bytes[0]) + i64::from(bytes[bytes.len() - 1])
}

fn main() {
    let mut text = String::with_capacity(TEXT_CAPACITY);
    let mut checksum = 0_i64;

    for _ in 0_i32..50 {
        text.clear();
        write!(&mut text, "{}", i64::MIN).unwrap();
        checksum += checksum_text(&text);

        text.clear();
        write!(&mut text, "{}", u64::MAX).unwrap();
        checksum += checksum_text(&text);

        text.clear();
        write!(&mut text, "{I1024_MIN_VALUE}").unwrap();
        checksum += checksum_text(&text);

        text.clear();
        write!(&mut text, "{U1024_MAX_VALUE}").unwrap();
        checksum += checksum_text(&text);
    }

    if checksum != 53_200 {
        std::process::exit(5);
    }
}
