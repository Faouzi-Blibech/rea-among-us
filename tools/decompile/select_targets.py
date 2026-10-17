# Pick methods to decompile: lines of methods.tsv whose "Type<TAB>Method" matches a regex in the patterns file.
# Usage: python -I select_targets.py <methods.tsv> <patterns.txt> <out.tsv> [already_done.tsv]
import re, sys
methods, patterns, out = sys.argv[1:4]
done = {l.split("\t")[0] for l in open(sys.argv[4])} if len(sys.argv) > 4 else set()
pats = [re.compile(l.rstrip("\n")) for l in open(patterns) if l.strip()]
n = 0
with open(out, "w") as fo:
    for line in open(methods, encoding="utf-8"):
        a, asm, t, m, sig = line.rstrip("\n").split("\t")
        if asm != "Assembly-CSharp" or a in done:
            continue
        if any(p.search(f"{t}\t{m}") for p in pats):
            fo.write(f"{a}\t{t}\t{m}\n"); n += 1
print(n)
