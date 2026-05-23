@echo off
setlocal

set "ROOT_DIR=%~dp0"
if "%ROOT_DIR:~-1%"=="\" set "ROOT_DIR=%ROOT_DIR:~0,-1%"
set "GOOGLE_PROTO_DIR=%ROOT_DIR%\..\..\Dependencies\protobuf\src"

if not exist "%ROOT_DIR%\Packets\compiled" mkdir "%ROOT_DIR%\Packets\compiled"
if not exist "%ROOT_DIR%\Stores\compiled" mkdir "%ROOT_DIR%\Stores\compiled"

echo Compiling trading packet protos
protoc --proto_path="%ROOT_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%ROOT_DIR%\Packets\compiled" "%ROOT_DIR%\Packets\CompleteTradePacket.proto" "%ROOT_DIR%\Packets\CreateTradePacket.proto" "%ROOT_DIR%\Packets\CreateTradeResponsePacket.proto" "%ROOT_DIR%\Packets\Quality.proto" "%ROOT_DIR%\Packets\SyncTradesPacket.proto" "%ROOT_DIR%\Packets\Thing.proto" "%ROOT_DIR%\Packets\TradeFailureReason.proto" "%ROOT_DIR%\Packets\TradeProto.proto" "%ROOT_DIR%\Packets\UpdateTradeItemsPacket.proto" "%ROOT_DIR%\Packets\UpdateTradeItemsResponsePacket.proto" "%ROOT_DIR%\Packets\UpdateTradeStatusPacket.proto"
if errorlevel 1 exit /b 1

echo Compiling trading store protos
protoc --proto_path="%ROOT_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%ROOT_DIR%\Stores\compiled" "%ROOT_DIR%\Stores\ActiveTradesStore.proto" "%ROOT_DIR%\Stores\CompletedTradeStore.proto" "%ROOT_DIR%\Stores\ProtoThings.proto" "%ROOT_DIR%\Stores\TradeStore.proto"
if errorlevel 1 exit /b 1

echo Done
