#!/usr/bin/env python3
"""
Static, read-only inventory of a macOS .app bundle for interoperability research.

Never executes the target. Prints: Info.plist keys, bundled helper tools and their sizes/hashes, license files,
Java libraries, Swift source-file names recorded in the binary, SwiftUI/AppKit view type names, and the
user-facing strings that name editors, tools and settings. Output is a summary for a human to read; the
bundle and the raw string dump are NOT committed to this repository (they are the vendor's copyrighted work).

Usage: bundle_inventory.py /path/to/Tangerine.app > inventory.txt
"""
import hashlib, os, plistlib, re, sys

def strings(path, n=4):
    data = open(path, 'rb').read()
    out = set(m.group().decode() for m in re.finditer(rb'[\x20-\x7e]{%d,}' % n, data))
    for m in re.finditer(rb'(?:[\x20-\x7e]|[\xc2-\xf4][\x80-\xbf]{1,3}){6,}', data):
        try: out.add(m.group().decode('utf-8'))
        except UnicodeDecodeError: pass
    return out

def main(app):
    c = os.path.join(app, 'Contents')
    info = plistlib.load(open(os.path.join(c, 'Info.plist'), 'rb'))
    print('## Info.plist')
    for k in ['CFBundleIdentifier', 'CFBundleShortVersionString', 'CFBundleVersion', 'LSMinimumSystemVersion', 'LSUIElement', 'LSApplicationCategoryType']:
        print(f'{k}: {info.get(k)}')
    print('\n## Files (size, sha256 prefix)')
    for root, _, files in os.walk(c):
        if '/ExifTool/lib' in root or '/PowerPoint/arm64' in root or '/PowerPoint/x86_64' in root: continue
        for f in sorted(files):
            p = os.path.join(root, f)
            h = hashlib.sha256(open(p, 'rb').read()).hexdigest()[:16]
            print(f'{os.path.relpath(p, c)}\t{os.path.getsize(p)}\t{h}')
    exe = os.path.join(c, 'MacOS', info['CFBundleExecutable'])
    s = strings(exe)
    print('\n## Swift source files named in the binary')
    print(' '.join(sorted({m for x in s for m in re.findall(r'[A-Za-z]+/[A-Za-z/]+\.swift', x)})))
    print('\n## View / window / editor types')
    print(' '.join(sorted(x for x in s if re.fullmatch(r'[A-Z][A-Za-z]+(View|Editor|Panel|Window|Sheet|Controller)', x))))
    print('\n## User-facing editor and tool strings')
    pat = re.compile(r'^(Save .* Copy|Save .*|Drag .*|Trim .*|Bleep.*|Compress.*|Convert .*|Normalize.*|Redact.*|Annotate.*|Add BG|Create .*|Change .*|Metadata.*|Remove metadata.*|Results are saved.*|Step [0-9].*|Tap the .*|No QR codes found|Reading QR codes)$')
    for x in sorted(x for x in s if pat.match(x)): print(x)
    ff = os.path.join(c, 'Resources', 'Tools', 'ffmpeg')
    if os.path.exists(ff):
        m = re.search(rb'--disable-shared[\x20-\x7e]+', open(ff, 'rb').read())
        print('\n## Bundled FFmpeg configure flags (paths elided)')
        print(m.group().decode() if m else '(not found)')

if __name__ == '__main__':
    main(sys.argv[1])
