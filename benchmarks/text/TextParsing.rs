use std::hint::black_box;

const U1024_LIMBS: usize = 16;
const TRUE_TEXT: &str = "true";
const I64_MIN_TEXT: &str = "-9223372036854775808";
const U64_MAX_TEXT: &str = "18446744073709551615";
const I1024_MIN_TEXT: &str = "-89884656743115795386465259539451236680898848947115328636715040578866337902750481566354238661203768010560056939935696678829394884407208311246423715319737062188883946712432742638151109800623047059726541476042502884419075341171231440736956555270413618581675255342293149119973622969239858152417678164812112068608";
const U1024_MAX_TEXT: &str = "179769313486231590772930519078902473361797697894230657273430081157732675805500963132708477322407536021120113879871393357658789768814416622492847430639474124377767893424865485276302219601246094119453082952085005768838150682342462881473913110540827237163350510684586298239947245938479716304835356329624224137215";

#[derive(Clone, Copy, Eq, PartialEq)]
struct U1024([u64; U1024_LIMBS]);

const I1024_MIN_MAGNITUDE: U1024 = U1024([
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
]);

const U1024_MAX_VALUE: U1024 = U1024([u64::MAX; U1024_LIMBS]);

impl U1024 {
    fn mul10_add(&mut self, digit: u8) -> bool {
        let mut carry = u128::from(digit);

        for word in &mut self.0 {
            let current = (u128::from(*word) * 10) + carry;
            *word = current as u64;
            carry = current >> 64;
        }

        carry == 0
    }
}

fn parse_u1024_decimal(text: &str) -> Option<U1024> {
    if text.is_empty() {
        return None;
    }

    let mut value = U1024([0; U1024_LIMBS]);
    for byte in text.bytes() {
        if !byte.is_ascii_digit() {
            return None;
        }

        if !value.mul10_add(byte - b'0') {
            return None;
        }
    }

    Some(value)
}

fn parse_i1024_min(text: &str) -> bool {
    let Some(magnitude) = text.strip_prefix('-') else {
        return false;
    };

    parse_u1024_decimal(magnitude) == Some(I1024_MIN_MAGNITUDE)
}

fn parse_u1024_max(text: &str) -> bool {
    parse_u1024_decimal(text) == Some(U1024_MAX_VALUE)
}

fn main() {
    let inputs = black_box([
        TRUE_TEXT,
        I64_MIN_TEXT,
        U64_MAX_TEXT,
        I1024_MIN_TEXT,
        U1024_MAX_TEXT,
    ]);
    let mut checksum = 0_i64;

    for _ in 0_i32..50 {
        if inputs[0].parse::<bool>() != Ok(true) {
            std::process::exit(1);
        }
        checksum += 1;

        if inputs[1].parse::<i64>() != Ok(i64::MIN) {
            std::process::exit(2);
        }
        checksum += 20;

        if inputs[2].parse::<u64>() != Ok(u64::MAX) {
            std::process::exit(3);
        }
        checksum += 20;

        if !parse_i1024_min(inputs[3]) {
            std::process::exit(4);
        }
        checksum += 309;

        if !parse_u1024_max(inputs[4]) {
            std::process::exit(5);
        }
        checksum += 309;
    }

    if checksum != 32_950 {
        std::process::exit(6);
    }
}
