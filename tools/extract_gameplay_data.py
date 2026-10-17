#!/usr/bin/env python3
"""Extract serialized gameplay tuning data (ScriptableObject / MonoBehaviour field
values) from an Among Us Android build (Unity 2022.3, IL2CPP).

Scene/shared-asset files in the base build have their typetrees stripped, so
MonoBehaviour field layouts are generated from libil2cpp.so + global-metadata.dat
with UnityPy's TypeTreeGenerator (needs the TypeTreeGeneratorAPI package).

Usage:
  uv run --with UnityPy --with TypeTreeGeneratorAPI python -I tools/extract_gameplay_data.py \\
      --data-dir BASE_APK/assets/bin/Data --data-dir ASSETPACK_APK/assets/bin/Data \\
      --bundle initialmaps_assets_all.bundle \\
      --bundle referencedatagroup_assets_all_<hash>.bundle \\
      --il2cpp libil2cpp.so --metadata global-metadata.dat \\
      --cs-dir DiffableCs/Assembly-CSharp --out-dir docs/data/gameplay

--data-dir  (repeatable) extracted assets/bin/Data directories. Files named
            X.splitN are concatenated in numeric order into --work-dir.
--bundle    (repeatable) Addressables bundles, read only.
--cs-dir    optional decompiled class stubs, used only to turn integer enum values
            and class hierarchies into names. Without it enums stay integers.
Only names, class names and numbers are written; no sprites/audio/meshes/text dumps.
"""
import argparse
import collections
import json
import os
import re
import shutil
import sys
import tempfile

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

UNITY_VERSION = "2022.3.62f3"
SKIP_KEYS = {"m_GameObject", "m_Script", "m_Enabled", "m_ObjectHideFlags", "m_Name",
             "SpawnId", "NetId", "SpawnFlags", "sendMode", "OwnerId", "DontDestroy"}
# object references are kept only when the target class matches this
REF_OK = re.compile(r"Role$|GameSetting$|AbilityButtonSettings$|Task$|Minigame$|Console$|Tag$|Settings$")
INLINE = re.compile(r"GameSetting$|AbilityButtonSettings$|UseButtonSettings$")


class Gen(TypeTreeGenerator):
    """Adapter: Il2cpp assembly names have no .dll suffix, and the generated
    MonoBehaviour header lacks the alignment flag on m_Enabled."""

    def get_nodes(self, assembly, fullname):
        ns = super().get_nodes(assembly[:-4] if assembly.endswith(".dll") else assembly, fullname)
        for n in ns[:8]:
            if n.m_Level == 1 and n.m_Name == "m_Enabled":
                n.m_MetaFlag = 0x4000
        return ns

    def get_nodes_up(self, assembly, fullname):
        return super().get_nodes_up(assembly[:-4] if assembly.endswith(".dll") else assembly, fullname)


def merge_splits(data_dirs, work):
    os.makedirs(work, exist_ok=True)
    groups = collections.defaultdict(list)
    for d in data_dirs:
        for f in sorted(os.listdir(d)):
            p = os.path.join(d, f)
            m = re.match(r"(.*)\.split(\d+)$", f)
            if m:
                groups[m.group(1)].append((int(m.group(2)), p))
            elif os.path.isfile(p) and not os.path.exists(os.path.join(work, f)):
                shutil.copyfile(p, os.path.join(work, f))
    for base, parts in groups.items():
        with open(os.path.join(work, base), "wb") as out:
            for _, p in sorted(parts):
                with open(p, "rb") as fh:
                    shutil.copyfileobj(fh, out)


class CsInfo:
    """Class stubs -> enum tables, field enum types, base classes."""

    def __init__(self, cs_dir):
        self.enums, self.bases, self.fields, self.files = {}, {}, {}, {}
        self.cs_dir = cs_dir
        if not cs_dir:
            return
        for f in os.listdir(cs_dir):
            if f.endswith(".cs"):
                self.files[f[:-3]] = os.path.join(cs_dir, f)
        self._enum_names = None

    def _scan_enums(self):
        if self._enum_names is not None:
            return
        self._enum_names = {}
        for stem, p in self.files.items():
            txt = open(p, encoding="utf-8", errors="replace").read()
            for m in re.finditer(r"^\s*(?:public |internal |private |protected )*enum (\w+)", txt, re.M):
                self._enum_names[m.group(1)] = p

    def enum(self, name):
        if name in self.enums:
            return self.enums[name]
        self._scan_enums()
        p = self._enum_names.get(name)
        tbl = None
        if p:
            txt = open(p, encoding="utf-8", errors="replace").read()
            m = re.search(r"enum %s\b[^{]*\{(.*?)\n\s*\}" % name, txt, re.S)
            if m:
                tbl = {}
                nxt = 0
                for mm in re.finditer(r"^\s*(\w+)\s*(?:=\s*(-?\w+))?\s*,?\s*$", m.group(1), re.M):
                    if mm.group(2) is not None:
                        try:
                            nxt = int(mm.group(2), 0)
                        except ValueError:
                            continue
                    tbl.setdefault(nxt, mm.group(1))
                    nxt += 1
        self.enums[name] = tbl
        return tbl

    def parse(self, cls):
        if cls in self.fields:
            return
        self.fields[cls] = {}
        self.bases[cls] = None
        p = self.files.get(cls)
        if not p:
            return
        txt = open(p, encoding="utf-8", errors="replace").read()
        m = re.search(r"^public (?:abstract |sealed |static )*class %s\b[^:{\n]*(?::\s*([\w.]+))?" % re.escape(cls), txt, re.M)
        if m and m.group(1):
            self.bases[cls] = m.group(1)
        # only the top-level class body fields (tab depth 1)
        for fm in re.finditer(r"^\tpublic (?:readonly )?([\w.<>\[\], ]+?) (\w+);", txt, re.M):
            t = re.sub(r"\[\]|List<|>", "", fm.group(1)).strip()
            self.fields[cls].setdefault(fm.group(2), t)

    def chain(self, cls):
        out, seen = [], set()
        while cls and cls not in seen:
            seen.add(cls)
            out.append(cls)
            self.parse(cls)
            cls = self.bases.get(cls)
        return out

    def field_enum(self, cls, field):
        for c in self.chain(cls):
            t = self.fields.get(c, {}).get(field)
            if t:
                return self.enum(t)
        return None

    def is_a(self, cls, base):
        return base in self.chain(cls)


def r4(v):
    return round(float(v) + 0.0, 4)


class Store:
    def __init__(self, gen, cs):
        self.gen, self.cs = gen, cs
        self.files = {}      # key -> (env, kind)
        self.gm_scripts = {}
        self.local_scripts = {}
        self.cache = {}
        self.mbs = []        # (fkey, path_id, class, tree)
        self.objs = {}       # (fkey, pid) -> ObjectReader
        self.ext_names = {}  # fkey -> [basename of externals]
        self.unreadable = collections.Counter()

    def load_gm(self, path):
        env = UnityPy.load(path)
        for o in env.objects:
            if o.type.name == "MonoScript":
                self.gm_scripts[o.path_id] = o.read_typetree()["m_ClassName"]

    def add(self, key, path, is_bundle):
        env = UnityPy.load(path)
        env.typetree_generator = self.gen
        sf = list(env.files.values())[0]
        self.ext_names[key] = [os.path.basename(e.path) for e in getattr(sf, "externals", [])]
        loc = {}
        for o in env.objects:
            self.objs[(key, o.path_id)] = o
            if o.type.name == "MonoScript":
                loc[o.path_id] = o.read_typetree()["m_ClassName"]
        self.local_scripts[key] = loc
        n_err = 0
        for o in env.objects:
            if o.type.name != "MonoBehaviour":
                continue
            try:
                t = o.read_typetree()
            except Exception:
                n_err += 1
                try:
                    h = o.parse_monobehaviour_head()
                    sp = h.m_Script
                    cn = (loc if sp.m_FileID == 0 and loc else self.gm_scripts).get(sp.m_PathID, "?")
                except Exception:
                    cn = "?"
                self.unreadable[cn] += 1
                continue
            sp = t.get("m_Script", {})
            tbl = self.local_scripts[key] if (sp.get("m_FileID") == 0 and loc) else self.gm_scripts
            cls = tbl.get(sp.get("m_PathID"))
            if cls is None:
                cls = self.gm_scripts.get(sp.get("m_PathID")) or loc.get(sp.get("m_PathID"))
            if cls is None:
                continue
            self.cache[(key, o.path_id)] = (cls, t)
            self.mbs.append((key, o.path_id, cls, t))
        return n_err

    # ---- reference resolution
    def resolve(self, fkey, ptr):
        pid, fid = ptr["m_PathID"], ptr["m_FileID"]
        if pid == 0:
            return None
        if fid == 0:
            return (fkey, pid)
        names = self.ext_names.get(fkey, [])
        if fid - 1 < len(names):
            tgt = names[fid - 1]
            if (tgt, pid) in self.objs:
                return (tgt, pid)
        return None

    def go_name(self, key, mbtree):
        g = mbtree.get("m_GameObject", {})
        r = self.resolve(key, g) if g else None
        o = self.objs.get(r) if r else None
        if o is None or o.type.name != "GameObject":
            return mbtree.get("m_Name") or ""
        return o.read_typetree()["m_Name"]

    def root_of(self, key, mbtree):
        """Name of the hierarchy root GameObject of this MonoBehaviour."""
        g = self.resolve(key, mbtree.get("m_GameObject", {}))
        o = self.objs.get(g) if g else None
        if o is None:
            return None
        t = None
        for c in o.read_typetree()["m_Component"]:
            co = self.objs.get((key, c["component"]["m_PathID"]))
            if co is not None and co.type.name in ("Transform", "RectTransform"):
                t = co
                break
        seen = 0
        while t is not None and seen < 64:
            td = t.read_typetree()
            f = td["m_Father"]["m_PathID"]
            if not f:
                go = self.objs.get((key, td["m_GameObject"]["m_PathID"]))
                return go.read_typetree()["m_Name"] if go else None
            t = self.objs.get((key, f))
            seen += 1
        return None

    def ser(self, key, cls, field, v, depth=0):
        cs = self.cs
        if isinstance(v, bool):
            return v
        if isinstance(v, float):
            return r4(v)
        if isinstance(v, int):
            en = cs.field_enum(cls, field) if cs.files else None
            if en is not None and v in en:
                return {"v": v, "name": en[v]}
            return v
        if isinstance(v, str):
            return v if 0 < len(v) <= 40 else None
        if isinstance(v, dict):
            if set(v) == {"m_FileID", "m_PathID"}:
                return self.ser_ref(key, v, depth)
            ks = set(v)
            if ks <= {"x", "y", "z", "w"}:
                return [r4(v[k]) for k in sorted(v)]
            if ks == {"r", "g", "b", "a"}:
                return [r4(v[k]) for k in ("r", "g", "b", "a")]
            if "m_PersistentCalls" in v:
                return [c.get("m_MethodName") for c in v["m_PersistentCalls"].get("m_Calls", [])] or None
            if "m_Curve" in v:
                c = v["m_Curve"]
                return {"curve_keys": len(c), "t_end": r4(c[-1]["time"]) if c else None,
                        "v_end": r4(c[-1]["value"]) if c else None}
            out = {}
            for k, x in v.items():
                s = self.ser(key, cls, k, x, depth)
                if s is not None:
                    out[k] = s
            return out or None
        if isinstance(v, list):
            if not v:
                return None
            out = [self.ser(key, cls, field, x, depth) for x in v[:64]]
            out = [x for x in out if x is not None]
            if len(v) > 64:
                out.append({"truncated_total": len(v)})
            return out or None
        return None

    def ser_ref(self, key, ptr, depth):
        r = self.resolve(key, ptr)
        if r is None or r not in self.cache:
            return None
        cls, t = self.cache[r]
        if INLINE.search(cls) and depth < 1:
            d = self.dump(r[0], cls, t, depth + 1)
            d["class"] = cls
            d["name"] = t.get("m_Name") or self.go_name(r[0], t)
            return d
        if REF_OK.search(cls):
            return {"ref": cls + "/" + (t.get("m_Name") or self.go_name(r[0], t))}
        return None

    def dump(self, key, cls, t, depth=0):
        out = {}
        for k, v in t.items():
            if k in SKIP_KEYS:
                continue
            s = self.ser(key, cls, k, v, depth)
            if s is not None:
                out[k] = s
        return out

    def entry(self, key, pid, cls, t, with_root=False):
        e = {"name": t.get("m_Name") or self.go_name(key, t), "class": cls, "source": key}
        if with_root:
            e["root"] = self.root_of(key, t)
        e["fields"] = self.dump(key, cls, t)
        return e


CONST_CLASSES = ("PlayerControl", "PlayerPhysics", "MeetingHud", "ShipStatus", "ReactorSystemType",
                 "LifeSuppSystemType", "HeliSabotageSystem", "SwitchSystem", "DoorsSystemType",
                 "AutoDoorsSystemType", "SabotageSystemType", "HudOverrideSystemType",
                 "HqHudSystemType", "SecurityCameraSystemType", "MedScanSystem", "DeconSystem",
                 "VentilationSystem", "MushroomMixupSabotageSystem", "KillButton", "GameManager",
                 "HideAndSeekManager", "NormalGameManager", "ExileController", "Vent", "Console",
                 "GameData", "NormalPlayerTask", "EmergencyMinigame", "AmongUsClient")


def code_constants(cs):
    """Literal 'const' values from decompiled class stubs (no method bodies exist)."""
    out = {}
    if not cs.files:
        return out
    names = [c for c in CONST_CLASSES if c in cs.files]
    names += [c for c in cs.files if re.search(r"GameOptions(V\d+)?$|^NormalGameOptions|^HideAndSeekGameOptions", c)]
    for c in sorted(set(names)):
        txt = open(cs.files[c], encoding="utf-8", errors="replace").read()
        d = {}
        for m in re.finditer(r"^	(public|private|internal|protected)(?: static)? const ([\w.]+) (\w+) = ([^;/]+?);", txt, re.M):
            v = m.group(4).strip()
            try:
                v = int(v) if re.fullmatch(r"-?\d+", v) else float(v)
            except ValueError:
                v = v.strip('"')[:40]
            d[m.group(3)] = {"type": m.group(2), "value": v, "visibility": m.group(1)}
        if d:
            out[c] = d
    return out


def write(out_dir, name, obj):
    with open(os.path.join(out_dir, name), "w", encoding="utf-8") as fh:
        json.dump(obj, fh, separators=(",", ":"), sort_keys=True)
        fh.write("\n")


def by_name(lst):
    return sorted(lst, key=lambda e: (e.get("root") or "", e["name"], e["class"], json.dumps(e["fields"], sort_keys=True)))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data-dir", action="append", default=[])
    ap.add_argument("--bundle", action="append", default=[])
    ap.add_argument("--il2cpp", required=True)
    ap.add_argument("--metadata", required=True)
    ap.add_argument("--cs-dir", default=None)
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--work-dir", default=None, help="scratch dir for merged split files (default: temp)")
    a = ap.parse_args()

    tmp = None
    work = a.work_dir
    if not work:
        tmp = tempfile.mkdtemp(prefix="au_gameplay_")
        work = tmp
    try:
        run(a, work)
    finally:
        if tmp:
            shutil.rmtree(tmp, ignore_errors=True)


def run(a, work):
    os.makedirs(a.out_dir, exist_ok=True)
    gen = Gen(UNITY_VERSION)
    with open(a.il2cpp, "rb") as f1, open(a.metadata, "rb") as f2:
        gen.load_il2cpp(f1.read(), f2.read())
    merge_splits(a.data_dir, work)
    cs = CsInfo(a.cs_dir)
    st = Store(gen, cs)
    st.load_gm(os.path.join(work, "globalgamemanagers.assets"))
    errs = {}
    for f in sorted(os.listdir(work)):
        if re.match(r"(level\d+|sharedassets\d+\.assets)$", f):
            errs[f] = st.add(f, os.path.join(work, f), False)
    bundle_keys = []
    for b in a.bundle:
        k = os.path.basename(b)
        errs[k] = st.add(k, b, True)
        bundle_keys.append(k)
    print("loaded", len(st.mbs), "MonoBehaviours; unreadable:", {k: v for k, v in errs.items() if v})

    def is_a(cls, base):
        if cs.files:
            return cs.is_a(cls, base)
        return cls.endswith(base.replace("Behaviour", ""))

    maps = next((k for k in bundle_keys if k.startswith("initialmaps")), None)
    ref = next((k for k in bundle_keys if k.startswith("referencedatagroup")), None)
    summary = {"unreadable_mono_behaviours": {k: v for k, v in errs.items() if v},
               "unreadable_by_class": dict(st.unreadable.most_common())}
    hist = collections.Counter(c for _, _, c, _ in st.mbs)

    # ---- game option settings (ScriptableObjects) and ability button settings
    settings = [st.entry(k, p, c, t) for k, p, c, t in st.mbs
                if c.endswith("GameSetting") or c in ("BaseGameSetting",)]
    write(a.out_dir, "game_settings.json", by_name(settings))
    abil = [st.entry(k, p, c, t) for k, p, c, t in st.mbs if c in ("AbilityButtonSettings", "UseButtonSettings")]
    write(a.out_dir, "ability_buttons.json", by_name(abil))

    # ---- roles
    roles = [st.entry(k, p, c, t) for k, p, c, t in st.mbs
             if c != "RoleBehaviour" and is_a(c, "RoleBehaviour") and k.startswith("sharedassets")]
    rm = [st.entry(k, p, c, t) for k, p, c, t in st.mbs if c == "RoleManager"]
    write(a.out_dir, "roles.json", {"roles": by_name(roles), "role_manager": rm})

    # ---- player prefab tuning
    ptypes = ("PlayerControl", "PlayerPhysics", "CosmeticsLayer", "LightSource", "PlayerAnimations",
              "CustomNetworkTransform", "KillOverlay", "DeadBody")
    pt = [st.entry(k, p, c, t) for k, p, c, t in st.mbs if c in ptypes[:2] + ptypes[3:]]
    write(a.out_dir, "player_tuning.json", by_name(pt))

    # ---- meeting / exile / game flow managers
    flow_classes = ("MeetingHud", "HideAndSeekManager", "NormalGameManager", "GameManager", "GameData",
                    "EndGameManager", "GameStartManager", "HudManager", "EmergencyMinigame",
                    "VoteSpreader", "PlayerVoteArea", "HideAndSeekTimerBar", "TaskPanelBehaviour")
    flow = [st.entry(k, p, c, t) for k, p, c, t in st.mbs
            if c in flow_classes or c.endswith("ExileController") or c == "ExileController"]
    write(a.out_dir, "meeting_and_flow.json", by_name(flow))

    # ---- tasks (from the maps bundle)
    tasks, sab_sys = [], []
    for k, p, c, t in st.mbs:
        if k != maps:
            continue
        if is_a(c, "PlayerTask") or c.endswith("Task"):
            e = st.entry(k, p, c, t, with_root=True)
            mg = st.cache.get(st.resolve(k, t.get("MinigamePrefab", {"m_PathID": 0, "m_FileID": 0})) or ())
            if mg:
                e["minigame_class"] = mg[0]
            tasks.append(e)
        elif re.search(r"System(Type)?$|Sabotage|Reactor|Oxy|Decon|ElectricalDoors|Autodoor|AutoDoor", c) and not c.endswith("Task"):
            sab_sys.append(st.entry(k, p, c, t, with_root=True))
    # task minigame prefab names
    write(a.out_dir, "tasks.json", by_name(tasks))

    # ---- per-map ship tuning
    ships = []
    for k, p, c, t in st.mbs:
        if k == maps and c.endswith("ShipStatus") or (k == maps and c == "AirshipStatus"):
            e = st.entry(k, p, c, t, with_root=True)
            ships.append(e)
    write(a.out_dir, "ship_tuning.json", {"ship_status": by_name(ships), "system_components": by_name(sab_sys)})

    # ---- quick chat structure (no phrase text dump)
    qc_tags = [st.entry(k, p, c, t) for k, p, c, t in st.mbs if c == "QuickChatTag"]
    qc_ph = []
    samples = []
    for k, p, c, t in st.mbs:
        if c != "QuickChatPhraseAsset":
            continue
        d = {kk: vv for kk, vv in t.items() if kk not in ("text", "icon")}
        e = {"name": t.get("m_Name"), "key": t.get("key"), "order": t.get("order"),
             "isInput": t.get("isInput"), "isNegative": t.get("isNegative"),
             "tags": sorted({st.cache[r][1].get("m_Name") for x in t.get("tags", [])
                             for r in [st.resolve(k, x)] if r in st.cache}),
             "n_inputs": len(t.get("inputs", [])),
             "rules": st.dump(k, "QuickChatPhraseAsset", t.get("classicRules", {})) if t.get("classicRules") else None}
        if len(samples) < 5:
            samples.append(t.get("text", "")[:60])
        qc_ph.append(e)
    qc_ph.sort(key=lambda e: (e["order"] if e["order"] is not None else 0, e["name"]))
    write(a.out_dir, "quickchat.json", {"tags": by_name(qc_tags), "phrase_count": len(qc_ph),
                                         "phrases": qc_ph, "text_samples": samples})

    # ---- cosmetics counts
    cos = collections.Counter()
    if ref:
        for k, p, c, t in st.mbs:
            if k == ref:
                cos[c] += 1
    write(a.out_dir, "cosmetics_counts.json", dict(sorted(cos.items())))

    summary["counts"] = {
        "game_settings": len(settings), "ability_buttons": len(abil), "roles": len(roles),
        "player_tuning": len(pt), "meeting_and_flow": len(flow), "tasks": len(tasks),
        "ship_status": len(ships), "system_components": len(sab_sys),
        "quickchat_phrases": len(qc_ph), "quickchat_tags": len(qc_tags),
    }
    summary["monobehaviour_class_histogram_top200"] = dict(hist.most_common(200))
    consts = code_constants(cs)
    write(a.out_dir, "code_constants.json", consts)
    summary["counts"]["code_constants_classes"] = len(consts)
    write(a.out_dir, "summary.json", summary)
    print(json.dumps(summary["counts"]))


if __name__ == "__main__":
    sys.exit(main())
