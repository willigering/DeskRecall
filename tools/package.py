"""Packt Quellcode und Builds (lokal). GitHub-Releases liefern EIN portables ZIP ohne Teilung; siehe .github/workflows/windows-release.yml.
Archive über 23 MB werden hier optional in eigenständige ZIP-Teile (<23 MB) zerlegt."""
import os, zipfile, hashlib, sys
ROOT = "/workspace"
SRC = os.path.join(ROOT, "DeskRecall")
LIMIT = 23 * 1000 * 1000 - 200_000   # Puffer für ZIP-Header
SKIP_DIRS = {"bin", "obj", ".vs", "publish"}

def add_tree(z, base, arc_prefix, skip=SKIP_DIRS):
    for d, dirs, files in os.walk(base):
        dirs[:] = sorted(x for x in dirs if x not in skip)
        for f in sorted(files):
            p = os.path.join(d, f)
            z.write(p, os.path.join(arc_prefix, os.path.relpath(p, base)))

def zipit(path, fill):
    if os.path.exists(path): os.remove(path)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z: fill(z)
    return os.path.getsize(path)

# 1) Quellcode
src_zip = os.path.join(ROOT, "DeskRecall-1.1.1-source.zip")
print("source", zipit(src_zip, lambda z: add_tree(z, SRC, "DeskRecall")))

# 2) Framework-abhängige Version (klein)
fd_zip = os.path.join(ROOT, "DeskRecall-1.1.1-win-x64.zip")
print("fd", zipit(fd_zip, lambda z: add_tree(z, os.path.join(SRC, "publish", "DeskRecall-win-x64"), "DeskRecall", skip=set())))

# 3) Portable Version
pdir = os.path.join(SRC, "publish", "DeskRecall-portable-win-x64")
port_zip = os.path.join(ROOT, "DeskRecall-1.1.1-portable-win-x64.zip")
size = zipit(port_zip, lambda z: add_tree(z, pdir, "DeskRecall", skip=set()))
print("portable", size)
if size > LIMIT:
    os.remove(port_zip)
    exe = open(os.path.join(pdir, "DeskRecall.exe"), "rb").read()
    sha = hashlib.sha256(exe).hexdigest().upper()
    chunk = 20 * 1000 * 1000   # EXE ist bereits komprimiert → ZIP-Teil ≈ Chunkgröße
    chunks = [exe[i:i + chunk] for i in range(0, len(exe), chunk)]
    n = len(chunks)
    names = [f"DeskRecall.exe.teil{i+1}" for i in range(n)]
    cmd = ("@echo off\r\nchcp 65001 >nul\r\ncd /d \"%~dp0\"\r\n"
           "echo DeskRecall wird zusammengesetzt ...\r\n"
           + "".join(f"if not exist \"{nm}\" (echo FEHLT: {nm} - bitte alle {n} ZIP-Teile in denselben Ordner entpacken. & pause & exit /b 1)\r\n" for nm in names)
           + "copy /b " + "+".join(f"\"{nm}\"" for nm in names) + " \"DeskRecall.exe\" >nul\r\n"
           f"for /f \"tokens=* delims=\" %%h in ('certutil -hashfile \"DeskRecall.exe\" SHA256 ^| findstr /v \":\"') do (set H=%%h & goto :chk)\r\n"
           ":chk\r\nset H=%H: =%\r\n"
           f"if /i \"%H%\"==\"{sha}\" (\r\n  del " + " ".join(f"\"{nm}\"" for nm in names) + "\r\n  echo OK - DeskRecall.exe ist bereit. & pause & exit /b 0\r\n)\r\n"
           "echo FEHLER: Pruefsumme stimmt nicht. Bitte die ZIP-Teile erneut herunterladen. & pause & exit /b 1\r\n")
    readme_split = (f"DeskRecall 1.1.1 – portable Version (in {n} Teile aufgeteilt)\r\n\r\n"
        f"1. Alle {n} ZIP-Dateien (…-teil1von{n}.zip bis …-teil{n}von{n}.zip) in DENSELBEN Ordner entpacken.\r\n"
        "2. ZUSAMMENSETZEN.cmd doppelklicken – erzeugt DeskRecall.exe und prüft die SHA-256-Prüfsumme.\r\n"
        "3. DeskRecall.exe starten.\r\n\r\n"
        "Alternativ in PowerShell:  cmd /c copy /b " + "+".join(names) + " DeskRecall.exe\r\n"
        f"SHA-256 von DeskRecall.exe: {sha}\r\n")
    out = []
    for i, data in enumerate(chunks):
        part = os.path.join(ROOT, f"DeskRecall-1.1.1-portable-win-x64-teil{i+1}von{n}.zip")
        def fill(z, i=i, data=data):
            z.writestr(f"DeskRecall/{names[i]}", data, compress_type=zipfile.ZIP_STORED)
            if i == 0:
                z.writestr("DeskRecall/ZUSAMMENSETZEN.cmd", cmd)
                z.writestr("DeskRecall/BITTE-LESEN.txt", readme_split)
                for f in os.listdir(pdir):
                    if f != "DeskRecall.exe": z.write(os.path.join(pdir, f), "DeskRecall/" + f)
        s = zipit(part, fill); out.append((part, s))
        assert s < 23 * 1000 * 1000, (part, s)
    for p, s in out: print(p, s)
    print("sha256", sha)
