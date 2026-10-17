import os, re, sys
root, out = sys.argv[1], sys.argv[2]
seen = set(); n = 0
with open(out, "w", encoding="utf-8") as fo:
    for d, _, files in os.walk(root):
        asm = os.path.relpath(d, root).split(os.sep)[0]
        for fn in files:
            typ = meth = None; want = False
            for line in open(os.path.join(d, fn), encoding="utf-8", errors="replace"):
                if line.startswith("Type: "): typ = line[6:].strip()
                elif line.startswith("Method: "): meth = line[8:].strip(); want = False
                elif line.startswith("Disassembly:"): want = True
                elif want:
                    m = re.match(r"\s+0x([0-9A-F]+)\s", line)
                    if m:
                        want = False
                        a = int(m.group(1), 16)
                        if a in seen: continue
                        seen.add(a)
                        name = re.sub(r"^\S+\s+", "", meth, count=1)
                        name = name.split("(")[0]
                        fo.write(f"{a:x}\t{asm}\t{typ}\t{name}\t{meth}\n"); n += 1
print(n)
