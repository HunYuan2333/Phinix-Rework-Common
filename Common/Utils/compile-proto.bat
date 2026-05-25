@echo off
setlocal

set "ROOT_DIR=%~dp0"
if "%ROOT_DIR:~-1%"=="\" set "ROOT_DIR=%ROOT_DIR:~0,-1%"
set "PROTO_DIR=%ROOT_DIR%\Framework\Proto"
set "GOOGLE_PROTO_DIR=%ROOT_DIR%\..\..\Dependencies\protobuf\src"

if not exist "%PROTO_DIR%\Shared\compiled" mkdir "%PROTO_DIR%\Shared\compiled"
if not exist "%PROTO_DIR%\Message\compiled" mkdir "%PROTO_DIR%\Message\compiled"
if not exist "%PROTO_DIR%\Command\compiled" mkdir "%PROTO_DIR%\Command\compiled"
if not exist "%PROTO_DIR%\Item\compiled" mkdir "%PROTO_DIR%\Item\compiled"

echo Compiling framework shared protos
protoc --proto_path="%PROTO_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%PROTO_DIR%\Shared\compiled" "%PROTO_DIR%\Shared\FrameworkShared.proto"
if errorlevel 1 exit /b 1

echo Compiling framework message protos
protoc --proto_path="%PROTO_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%PROTO_DIR%\Message\compiled" "%PROTO_DIR%\Message\FrameworkMessagePacket.proto"
if errorlevel 1 exit /b 1

echo Compiling framework command protos
protoc --proto_path="%PROTO_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%PROTO_DIR%\Command\compiled" "%PROTO_DIR%\Command\FrameworkCommandPacket.proto"
if errorlevel 1 exit /b 1

echo Compiling framework item protos
protoc --proto_path="%PROTO_DIR%" --proto_path="%GOOGLE_PROTO_DIR%" --csharp_out="%PROTO_DIR%\Item\compiled" "%PROTO_DIR%\Item\FrameworkItemPacket.proto"
if errorlevel 1 exit /b 1

echo Done
