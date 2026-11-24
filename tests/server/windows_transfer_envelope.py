"""Encrypted relay for synthetic Windows snapshot fixtures on independent CI hosts."""
import argparse
import os
from pathlib import Path
from shutil import make_archive, unpack_archive

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

MAX_BYTES = 160 * 1024 * 1024
LABEL = b'SP Backup Windows snapshot fixture v1'
KEY_BYTES = 384
OAEP = padding.OAEP(mgf=padding.MGF1(hashes.SHA256()), algorithm=hashes.SHA256(), label=LABEL)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['key', 'seal', 'open'])
    parser.add_argument('work', type=Path)
    args = parser.parse_args()
    work = args.work
    work.mkdir(parents=True, exist_ok=True)
    private = work / 'destination.pem'
    public = work / 'public.pem'
    if args.action == 'key':
        key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
        with private.open('xb') as stream:
            stream.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                           serialization.NoEncryption()))
        public.write_bytes(key.public_key().public_bytes(serialization.Encoding.PEM,
                                                         serialization.PublicFormat.SubjectPublicKeyInfo))
        return

    sealed = work / 'fixture.enc'
    zip_path = work / 'fixture.zip'
    try:
        if args.action == 'seal':
            recipient = serialization.load_pem_public_key(public.read_bytes())
            if not isinstance(recipient, rsa.RSAPublicKey) or recipient.key_size != KEY_BYTES * 8:
                raise ValueError('Unexpected destination key.')
            make_archive(str(zip_path.with_suffix('')), 'zip', work / 'fixture')
            if zip_path.stat().st_size > MAX_BYTES:
                raise ValueError('Unexpected fixture size.')
            key = AESGCM.generate_key(bit_length=256)
            nonce = os.urandom(12)
            ciphertext = AESGCM(key).encrypt(nonce, zip_path.read_bytes(), LABEL)
            sealed.write_bytes(recipient.encrypt(key, OAEP) + nonce + ciphertext)
        else:
            if not KEY_BYTES + 28 < sealed.stat().st_size <= MAX_BYTES + KEY_BYTES + 28:
                raise ValueError('Unexpected encrypted fixture size.')
            key = serialization.load_pem_private_key(private.read_bytes(), password=None)
            blob = sealed.read_bytes()
            secret = key.decrypt(blob[:KEY_BYTES], OAEP)
            zip_path.write_bytes(AESGCM(secret).decrypt(blob[KEY_BYTES:KEY_BYTES+12], blob[KEY_BYTES+12:], LABEL))
            unpack_archive(zip_path, work / 'fixture', 'zip')
    finally:
        zip_path.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
