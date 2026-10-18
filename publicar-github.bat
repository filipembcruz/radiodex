@echo off
setlocal
cd /d "%~dp0"
set "REPO=filipembcruz/radiodex"
set "NAME=radiodex"
set "DESC=Internet radio for Windows with self-repairing stream links and song-marked recordings"

where git >nul 2>nul
if errorlevel 1 (
  echo  Git nao encontrado. Instale com: winget install Git.Git
  echo  Depois feche e abra esta janela de novo.
  pause
  exit /b 1
)

rem Git precisa de nome e e-mail para registrar o envio
git config user.email >nul 2>nul
if errorlevel 1 (
  set /p GITNAME= Seu nome para o Git: 
  set /p GITMAIL= Seu e-mail do GitHub: 
  call git config --global user.name "%%GITNAME%%"
  call git config --global user.email "%%GITMAIL%%"
)

if not exist ".git" (
  git init >nul
  git checkout -b main >nul 2>nul
)
git add -A
git commit -m "Radiodex" >nul 2>nul
git branch -M main

git remote get-url origin >nul 2>nul
if errorlevel 1 (
  where gh >nul 2>nul
  if not errorlevel 1 (
    rem GitHub CLI instalado: cria o repositorio publico automaticamente
    gh repo view %REPO% >nul 2>nul
    if errorlevel 1 gh repo create %REPO% --public --description "%DESC%" >nul
  ) else (
    echo.
    echo  Crie um repositorio VAZIO e PUBLICO chamado "%NAME%" em:
    echo    https://github.com/new
    echo  ^(sem README, .gitignore ou licenca^). Depois pressione uma tecla.
    start "" "https://github.com/new?name=%NAME%&visibility=public&description=Internet+radio+for+Windows"
    pause
  )
  git remote add origin https://github.com/%REPO%.git
)

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
