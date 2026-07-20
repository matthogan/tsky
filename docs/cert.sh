#!/usr/bin/env bash
set -euo pipefail

# Run from repo root (or adjust TEMP_DIR)
TEMP_DIR="./.temp"
mkdir -p "$TEMP_DIR"

SAN_CNF="$TEMP_DIR/gateway-san.cnf"
KEY="$TEMP_DIR/gateway.key"
CSR="$TEMP_DIR/gateway.csr"
CRT="$TEMP_DIR/gateway.crt"
PFX="$TEMP_DIR/gateway.pfx"
CA_CRT="$TEMP_DIR/private-ca.crt"
CA_KEY="$TEMP_DIR/private-ca.key"
CA_SRL="$TEMP_DIR/private-ca.srl"

# 1) Create SAN config
cat > "$SAN_CNF" <<'EOF'
[req]
default_bits = 2048
prompt = no
default_md = sha256
distinguished_name = dn
req_extensions = req_ext

[dn]
CN = XEON

[req_ext]
subjectAltName = @alt_names

[alt_names]
DNS.1 = XEON
DNS.2 = localhost
DNS.3 = XEON.local
IP.1 = 127.0.0.1
IP.2 = ::1
IP.3 = 172.24.128.1
IP.4 = 192.168.1.108
EOF

# 2) Generate key + CSR with SAN
openssl genrsa -out "$KEY" 2048
openssl req -new -key "$KEY" -out "$CSR" -config "$SAN_CNF"

# 3) Sign with your private CA (assumes CA files already exist)
openssl x509 -req \
  -in "$CSR" \
  -CA "$CA_CRT" \
  -CAkey "$CA_KEY" \
  -CAcreateserial \
  -CAserial "$CA_SRL" \
  -out "$CRT" \
  -days 825 \
  -sha256 \
  -extfile "$SAN_CNF" \
  -extensions req_ext

# 4) Export PFX used by desktop gateway
# Prompts for export password interactively
openssl pkcs12 -export \
  -out "$PFX" \
  -inkey "$KEY" \
  -in "$CRT" \
  -certfile "$CA_CRT"

echo "Done:"
echo "  Cert: $CRT"
echo "  PFX : $PFX"
echo "  SAN config: $SAN_CNF"
