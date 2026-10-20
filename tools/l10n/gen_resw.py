"""Builds src/Orange.App/Strings/{en-US,hi-IN,te-IN}/Resources.resw from every L.T("…")/L.F("…") literal in the
app's source plus the dynamic section names below. Keys must match L.Key(): "S" + first 10 hex of SHA-1(english).
Run after adding UI text:  python3 tools/l10n/gen_resw.py"""
import hashlib, json, os, re, glob
from xml.sax.saxutils import escape
root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXTRA = ["Activation", "Output", "Presets", "Engines", "Language", "Updates", "Privacy", "About", "Your data", "File Explorer",
         "Appearance", "Performance and limits", "Watch folders", "Wheel editor", "Troubleshooting", "Converters"]
lit = re.compile(r'L\.[TF]\("((?:[^"\\]|\\.)*)"')
strings = set(EXTRA)
for f in glob.glob(os.path.join(root, "src/Orange.App/**/*.cs"), recursive=True):
    for m in lit.finditer(open(f, encoding="utf-8").read()):
        strings.add(m.group(1).encode().decode("unicode_escape") if "\\" in m.group(1) else m.group(1))
tr = json.load(open(os.path.join(root, "tools/l10n/translations.json"), encoding="utf-8"))
def key(s): return "S" + hashlib.sha1(s.encode("utf-8")).hexdigest()[:10].upper()
header = '<?xml version="1.0" encoding="utf-8"?>\n<root>\n  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>\n  <resheader name="version"><value>2.0</value></resheader>\n  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n'
for lang, idx in (("en-US", None), ("hi-IN", 0), ("te-IN", 1)):
    out = [header]
    for s in sorted(strings):
        v = s if idx is None else (tr.get(s) or [None, None])[idx]
        if v is None: continue
        out.append(f'  <data name="{key(s)}" xml:space="preserve"><value>{escape(v)}</value><comment>{escape(s)}</comment></data>\n')
    out.append("</root>\n")
    d = os.path.join(root, "src/Orange.App/Strings", lang); os.makedirs(d, exist_ok=True)
    open(os.path.join(d, "Resources.resw"), "w", encoding="utf-8").write("".join(out))
    print(lang, len(out) - 2)
