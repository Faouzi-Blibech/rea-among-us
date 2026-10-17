import struct, sys, collections
so, tsv, out = sys.argv[1:4]
data = open(so, 'rb').read()
def rd(va):  # code segment: vaddr 0x1c3725c, offset 0x1c3325c
    return struct.unpack_from('<I', data, va - 0x4000)[0]
starts = sorted({int(l.split('\t')[0], 16) for l in open(tsv, encoding='utf-8')})
c = collections.Counter()
for s in starts:
    if s < 0x1c37260: continue
    ins = rd(s - 4)
    if ins & 0xFC000000 == 0x94000000:
        imm = ins & 0x3FFFFFF
        if imm & 0x2000000: imm -= 1 << 26
        c[s - 4 + imm * 4] += 1
names = {int(l.split('\t')[0], 16): l.split('\t')[2] + '.' + l.split('\t')[3] for l in open(tsv, encoding='utf-8')}
with open(out, 'w') as fo:
    for a, n in c.most_common():
        if n < 10: break
        # Named methods qualify only if they are throw helpers (drops e.g. a converter that is often tail-called).
        if names.get(a) and "Throw" not in names[a]: continue
        fo.write(f'{a:x}\n')
        print(f'{a:x} {n} {names.get(a, "")}')
