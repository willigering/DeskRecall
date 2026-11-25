"""Prüft, dass jede {StaticResource X} in den XAML-Dateien definiert ist (WPF meldet das sonst erst zur Laufzeit)."""
import re, sys, glob, os
root = sys.argv[1] if len(sys.argv) > 1 else "src/DeskRecall"
files = glob.glob(os.path.join(root, "**", "*.xaml"), recursive=True)
glob_keys = set()
for f in files:
    if f.endswith("Theme.xaml") or f.endswith("App.xaml"):
        glob_keys |= set(re.findall(r'x:Key="([^"]+)"', open(f, encoding="utf-8").read()))
bad = 0
for f in files:
    s = open(f, encoding="utf-8").read()
    local = set(re.findall(r'x:Key="([^"]+)"', s))
    for m in re.finditer(r'StaticResource\s+([\w.]+)', s):
        k = m.group(1)
        if k not in glob_keys | local:
            line = s[:m.start()].count("\n") + 1
            print(f"{f}:{line}: unbekannte Ressource '{k}'"); bad += 1
    # Theme.xaml: Ressourcen müssen vor der Verwendung definiert sein
    if f.endswith("Theme.xaml"):
        for m in re.finditer(r'StaticResource\s+([\w.]+)', s):
            d = s.find(f'x:Key="{m.group(1)}"')
            if d > m.start():
                print(f"{f}: '{m.group(1)}' wird vor der Definition verwendet"); bad += 1
for k in ["FindResource(\"", "FindResource(_vm"]:
    pass
cs = glob.glob(os.path.join(root, "**", "*.cs"), recursive=True)
for f in cs:
    for m in re.finditer(r'FindResource\("([^"]+)"\)', open(f, encoding="utf-8").read()):
        if m.group(1) not in glob_keys: print(f"{f}: FindResource unbekannt '{m.group(1)}'"); bad += 1
print("OK" if bad == 0 else f"{bad} Probleme"); sys.exit(1 if bad else 0)
