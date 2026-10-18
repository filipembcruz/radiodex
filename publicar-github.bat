@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
set "REPO=filipembcruz/radiodex"
set "NAME=radiodex"
set "DESC=Internet radio for Windows with self-repairing stream links and song-marked recordings"

rem ------------------------------------------------------------------
rem  Publica esta pasta no GitHub.
rem  A pasta e a versao "certa": o que estiver aqui vira a nova versao
rem  no GitHub, por cima do historico que ja existe la (nada e apagado
rem  do historico, so e adicionada uma versao nova).
rem ------------------------------------------------------------------

where git >nul 2>nul
if errorlevel 1 (
  echo  Git nao encontrado. Instale com: winget install Git.Git
  echo  Depois feche e abra esta janela de novo.
  pause
  exit /b 1
)

rem Git precisa de nome e e-mail para registrar o envio
git config user.email >nul 2>nul
if errorlevel 1 goto :askident
goto :identok
:askident
set /p "GITNAME= Seu nome para o Git: "
set /p "GITMAIL= Seu e-mail do GitHub: "
git config --global user.name "!GITNAME!"
git config --global user.email "!GITMAIL!"
:identok

if not exist ".git" git init -q
git symbolic-ref HEAD refs/heads/main

rem Repositorio no GitHub
git remote get-url origin >nul 2>nul
if not errorlevel 1 goto :remoteok
where gh >nul 2>nul
if errorlevel 1 goto :manualrepo
gh repo view %REPO% >nul 2>nul
if errorlevel 1 gh repo create %REPO% --public --description "%DESC%" >nul
goto :addremote
:manualrepo
echo.
echo  Se o repositorio "%NAME%" ainda nao existe, crie-o VAZIO e PUBLICO em:
echo    https://github.com/new
echo  ^(sem README, .gitignore ou licenca^). Depois pressione uma tecla.
start "" "https://github.com/new?name=%NAME%&visibility=public"
pause
:addremote
git remote add origin https://github.com/%REPO%.git
:remoteok

rem Busca o que ja esta no GitHub e coloca esta versao por cima
echo Verificando o GitHub...
git fetch -q origin 2>nul
git rev-parse --verify -q origin/main >nul 2>nul
if not errorlevel 1 git update-ref refs/heads/main origin/main

git add -A
git diff --cached --quiet
if not errorlevel 1 (
  echo.
  echo  Nada mudou desde o ultimo envio.
  pause
  exit /b 0
)

for /f "tokens=1,2" %%a in ('powershell -NoProfile -Command "Get-Date -Format \"yyyy-MM-dd HH:mm\""') do set "NOW=%%a %%b"
git commit -q -m "Atualizacao !NOW!"

echo Enviando para https://github.com/%REPO% ...
git push -u origin main
if errorlevel 1 (
  echo.
  echo  O envio falhou. Veja a mensagem acima.
  pause
  exit /b 1
)

echo.
echo  Pronto! O GitHub esta compilando o Radiodex.exe agora (leva uns 3 minutos).
echo  Depois ele aparece em: https://github.com/%REPO%/releases
start "" "https://github.com/%REPO%/actions"
pause
endlocal
