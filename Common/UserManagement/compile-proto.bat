@echo off
setlocal

set "ROOT_DIR=%~dp0"
if "%ROOT_DIR:~-1%"=="\" set "ROOT_DIR=%ROOT_DIR:~0,-1%"
set "GOOGLE_PROTO_DIR=%ROOT_DIR%\..\..\Dependencies\protobuf\src"

if not exist "%ROOT_DIR%\Packets\compiled" mkdir "%ROOT_DIR%\Packets\compiled"
if not exist "%ROOT_DIR%\Users\compiled" mkdir "%ROOT_DIR%\Users\compiled"

echo Compiling user management packet protos
protoc --proto_path="%ROOT_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%ROOT_DIR%\Packets\compiled" "%ROOT_DIR%\Packets\LoginFailureReason.proto" "%ROOT_DIR%\Packets\LoginPacket.proto" "%ROOT_DIR%\Packets\LoginResponsePacket.proto" "%ROOT_DIR%\Packets\UserSyncPacket.proto" "%ROOT_DIR%\Packets\UserUpdatePacket.proto"
if errorlevel 1 exit /b 1

echo Compiling user management user protos
protoc --proto_path="%ROOT_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%ROOT_DIR%\Users\compiled" "%ROOT_DIR%\Users\User.proto" "%ROOT_DIR%\Users\UserStore.proto"
if errorlevel 1 exit /b 1

echo Done
