#!/bin/bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROTO_DIR="$ROOT_DIR/Framework/Proto"
GOOGLE_PROTO_DIR="$ROOT_DIR/../../Dependencies/protobuf/src"

mkdir -p "$PROTO_DIR/Shared/compiled"
mkdir -p "$PROTO_DIR/Message/compiled"
mkdir -p "$PROTO_DIR/Command/compiled"
mkdir -p "$PROTO_DIR/Item/compiled"

echo "Compiling framework shared protos"
protoc --proto_path="$PROTO_DIR" --proto_path="$GOOGLE_PROTO_DIR" --csharp_out="$PROTO_DIR/Shared/compiled" "$PROTO_DIR/Shared/FrameworkShared.proto"

echo "Compiling framework message protos"
protoc --proto_path="$PROTO_DIR" --proto_path="$GOOGLE_PROTO_DIR" --csharp_out="$PROTO_DIR/Message/compiled" "$PROTO_DIR/Message/FrameworkMessagePacket.proto"

echo "Compiling framework command protos"
protoc --proto_path="$PROTO_DIR" --proto_path="$GOOGLE_PROTO_DIR" --csharp_out="$PROTO_DIR/Command/compiled" "$PROTO_DIR/Command/FrameworkCommandPacket.proto"

echo "Compiling framework item protos"
protoc --proto_path="$PROTO_DIR" --proto_path="$GOOGLE_PROTO_DIR" --csharp_out="$PROTO_DIR/Item/compiled" "$PROTO_DIR/Item/FrameworkItemPacket.proto"

echo "Done"
