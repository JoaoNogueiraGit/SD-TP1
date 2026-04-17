@echo off
echo [UrbanHealth] A preparar o ambiente de simulacao...

:: Guarda o caminho exato onde este ficheiro .bat esta guardado
set "BASE_DIR=%~dp0"

echo 1. A iniciar o Gateway...
:: Muda para a pasta do Gateway ANTES de abrir a janela
cd /d "%BASE_DIR%Gateway"
start "Gateway" cmd /k "title Gateway G101 && color 0B && dotnet run"

:: Esperar 30 segundos para dar tempo ao Gateway de ler o CSV
timeout /t 30 /nobreak > NUL

echo 2. A iniciar os Sensores...
:: Muda para a pasta do Sensor ANTES de abrir as janelas
cd /d "%BASE_DIR%Sensor"
start "Sensor 1" cmd /k "title Sensor S101 && color 0A && dotnet run --no-build S101 127.0.0.1"
start "Sensor 2" cmd /k "title Sensor S102 && color 0E && dotnet run --no-build S102 127.0.0.1"
start "Sensor 3" cmd /k "title Sensor S103 && color 0D && dotnet run --no-build S103 127.0.0.1"

echo Tudo a correr! Podes fechar esta janela principal.