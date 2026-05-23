@echo off
setlocal

set "ROOT_DIR=%~dp0"
if "%ROOT_DIR:~-1%"=="\" set "ROOT_DIR=%ROOT_DIR:~0,-1%"
set "GOOGLE_PROTO_DIR=%ROOT_DIR%\..\..\Dependencies\protobuf\src"

if not exist "%ROOT_DIR%\Credentials\compiled" mkdir "%ROOT_DIR%\Credentials\compiled"
if not exist "%ROOT_DIR%\Packets\compiled" mkdir "%ROOT_DIR%\Packets\compiled"

echo Compiling authentication credentials protos
protoc --proto_path="%ROOT_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%ROOT_DIR%\Credentials\compiled" "%ROOT_DIR%\Credentials\Credential.proto" "%ROOT_DIR%\Credentials\CredentialStore.proto"
if errorlevel 1 exit /b 1

echo Compiling authentication packet protos
protoc --proto_path="%ROOT_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%ROOT_DIR%\Packets\compiled" "%ROOT_DIR%\Packets\AuthenticatePacket.proto" "%ROOT_DIR%\Packets\AuthResponsePacket.proto" "%ROOT_DIR%\Packets\AuthTypes.proto" "%ROOT_DIR%\Packets\AuthFailureReason.proto" "%ROOT_DIR%\Packets\ExtendSessionPacket.proto" "%ROOT_DIR%\Packets\ExtendSessionResponsePacket.proto" "%ROOT_DIR%\Packets\HelloPacket.proto"
if errorlevel 1 exit /b 1

echo Done
