"""Verify a Chrome extension private key yields the expected stable extension ID.

Usage: python verify_ext_id.py <key.pem> <expected-id>
ID = first 128 bits of sha256(SPKI DER), mapped nibble-by-nibble to a-p.
"""
import hashlib
import sys
from cryptography.hazmat.primitives import serialization


def main() -> None:
    key_path, expected = sys.argv[1], sys.argv[2]
    with open(key_path, "rb") as f:
        pem = f.read()
    public_key = serialization.load_pem_private_key(pem, None).public_key()
    der = public_key.public_bytes(
        serialization.Encoding.DER,
        serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    digest = hashlib.sha256(der).digest()[:16]
    ext_id = "".join(chr(97 + (b >> 4)) + chr(97 + (b & 15)) for b in digest)
    if ext_id != expected:
        raise SystemExit("ID mismatch: got " + ext_id + ", want " + expected)
    print("ID OK: " + ext_id)


if __name__ == "__main__":
    main()
