#!/usr/bin/env python3
"""Extract map layout data (names, class names, 2D world positions, ids/links)
from an Among Us (Unity 2022.3, Addressables) map bundle.

Usage:
  uv run --with UnityPy python -I tools/extract_map_layout.py BUNDLE OUT_DIR [--cs-dir DIR]

BUNDLE   path to initialmaps_assets_all.bundle (read-only)
OUT_DIR  directory that receives <map>.json and summary.json
--cs-dir optional directory of decompiled enum sources (SystemTypes.cs,
         TaskTypes.cs, ImageNames.cs) used only to turn integer enum values
         into names. Without it, only the integers are written.

Only names, class names, ids and numbers are written. No sprites, textures,
audio or meshes are exported.
"""
import argparse
import collections
import json
import math
import os
import re
import sys

import UnityPy

# Root prefab -> output file stem.
MAP_STEMS = {
    "SkeldShip": "skeld", "MiraShip": "mira", "PolusShip": "polus",
    "Airship": "airship", "FungleShip": "fungle", "AprilShip": "dleks",
    "Lobby": "lobby",
}
ENUM_FIELDS = {"Room": "SystemTypes", "RoomId": "SystemTypes",
               "TaskTypes": "TaskTypes", "useIcon": "ImageNames",
               "taskType": "TaskTypes", "TaskType": "TaskTypes"}
SKIP_FIELDS = {"m_GameObject", "m_Script", "m_Enabled", "m_Name", "m_ObjectHideFlags"}
REF_TYPES = {"GameObject", "MonoBehaviour", "Transform", "RectTransform",
             "BoxCollider2D", "PolygonCollider2D", "CircleCollider2D",
             "EdgeCollider2D", "CapsuleCollider2D"}


def r3(v):
    return round(float(v) + 0.0, 3)


def load_enums(cs_dir):
    enums = {}
    if not cs_dir:
        return enums
    for name in ("SystemTypes", "TaskTypes", "ImageNames"):
        p = os.path.join(cs_dir, name + ".cs")
        if not os.path.isfile(p):
            continue
        d = {}
        for m in re.finditer(r"^\s*(\w+)\s*=\s*(-?\d+)", open(p, encoding="utf-8").read(), re.M):
            d[int(m.group(2))] = m.group(1)
        enums[name] = d
    return enums


class Scene:
    def __init__(self, path):
        env = UnityPy.load(path)
        self.objs = {}
        for o in env.objects:
            self.objs[o.path_id] = o
        self.cache = {}
        self.tf = {}        # transform path_id -> dict
        self.go = {}        # gameobject path_id -> dict
        self.scripts = {}
        for pid, o in self.objs.items():
            t = o.type.name
            if t in ("Transform", "RectTransform"):
                d = o.read_typetree()
                self.tf[pid] = {
                    "go": d["m_GameObject"]["m_PathID"],
                    "father": d["m_Father"]["m_PathID"],
                    "pos": d["m_LocalPosition"], "rot": d["m_LocalRotation"],
                    "scale": d["m_LocalScale"], "rect": t == "RectTransform",
                    "children": [c["m_PathID"] for c in d["m_Children"]],
                }
            elif t == "MonoScript":
                self.scripts[pid] = o.read_typetree().get("m_ClassName", "?")
        for pid, o in self.objs.items():
            if o.type.name == "GameObject":
                d = o.read_typetree()
                comps = [c["component"]["m_PathID"] for c in d["m_Component"]]
                self.go[pid] = {"name": d["m_Name"], "comps": comps, "active": d.get("m_IsActive", 1)}
        self.go_tf = {}
        for pid, t in self.tf.items():
            self.go_tf[t["go"]] = pid
        self._world = {}

    def cls(self, pid):
        o = self.objs.get(pid)
        if o is None:
            return None
        if o.type.name == "MonoBehaviour":
            d = self.tree(pid)
            return self.scripts.get(d["m_Script"]["m_PathID"], "MonoBehaviour")
        return o.type.name

    def tree(self, pid):
        if pid not in self.cache:
            try:
                self.cache[pid] = self.objs[pid].read_typetree()
            except Exception:
                self.cache[pid] = {}
        return self.cache[pid]

    def world(self, tpid):
        """2D affine (a,b,c,d,tx,ty) mapping local -> world. Rotation about Z only."""
        if tpid in self._world:
            return self._world[tpid]
        t = self.tf[tpid]
        q = t["rot"]
        ang = 2.0 * math.atan2(q["z"], q["w"])
        cs, sn = math.cos(ang), math.sin(ang)
        sx, sy = t["scale"]["x"], t["scale"]["y"]
        # local matrix = R * S, translation = pos
        a, b, c, d = cs * sx, sn * sx, -sn * sy, cs * sy
        tx, ty = t["pos"]["x"], t["pos"]["y"]
        if t["father"] and t["father"] in self.tf:
            pa, pb, pc, pd, ptx, pty = self.world(t["father"])
            m = (pa * a + pc * b, pb * a + pd * b, pa * c + pc * d, pb * c + pd * d,
                 pa * tx + pc * ty + ptx, pb * tx + pd * ty + pty)
        else:
            m = (a, b, c, d, tx, ty)
        self._world[tpid] = m
        return m

    def gopos(self, gpid):
        tp = self.go_tf.get(gpid)
        if tp is None:
            return None
        m = self.world(tp)
        return [r3(m[4]), r3(m[5])]

    def apply(self, tpid, x, y):
        m = self.world(tpid)
        return [r3(m[0] * x + m[2] * y + m[4]), r3(m[1] * x + m[3] * y + m[5])]

    def path(self, gpid):
        parts = []
        tp = self.go_tf.get(gpid)
        while tp:
            parts.append(self.go[self.tf[tp]["go"]]["name"])
            f = self.tf[tp]["father"]
            tp = f if f in self.tf else None
        return "/".join(reversed(parts))

    def descendants(self, tpid):
        out, stack = [], [tpid]
        while stack:
            x = stack.pop()
            out.append(x)
            stack.extend(c for c in self.tf[x]["children"] if c in self.tf)
        return out

    def ref_name(self, pid):
        """Name of the GameObject owning component/GO pid, or None."""
        o = self.objs.get(pid)
        if o is None or o.type.name not in REF_TYPES:
            return None
        if o.type.name == "GameObject":
            return self.go[pid]["name"]
        g = self.tree(pid).get("m_GameObject", {}).get("m_PathID")
        return self.go[g]["name"] if g in self.go else None

    def ref_go(self, pid):
        o = self.objs.get(pid)
        if o is None:
            return None
        if o.type.name == "GameObject":
            return pid
        g = self.tree(pid).get("m_GameObject", {}).get("m_PathID")
        return g if g in self.go else None


def is_pptr(v):
    return isinstance(v, dict) and set(v) == {"m_FileID", "m_PathID"}


def ser_value(sc, key, v, enums):
    """Compact JSON-safe form of a typetree value; None means drop it."""
    if is_pptr(v):
        if v["m_PathID"] == 0:
            return None
        n = sc.ref_name(v["m_PathID"])
        return {"ref": n} if n is not None else None
    if isinstance(v, bool):
        return v
    if isinstance(v, float):
        return r3(v)
    if isinstance(v, int):
        en = enums.get(ENUM_FIELDS.get(key))
        return {"v": v, "name": en[v]} if en and v in en else v
    if isinstance(v, str):
        return v or None
    if isinstance(v, dict):
        if set(v) <= {"x", "y", "z", "w"} and v:
            return [r3(v[k]) for k in sorted(v) if k in "xyz"]
        return None
    if isinstance(v, list):
        if len(v) > 40:
            return {"count": len(v)}
        if v and all(is_pptr(i) for i in v):
            out = [sc.ref_name(i["m_PathID"]) for i in v if i["m_PathID"]]
            out = [o for o in out if o is not None]
            return out or None
        if v and all(isinstance(i, int) and not isinstance(i, bool) for i in v):
            en = enums.get(ENUM_FIELDS.get(key))
            return [{"v": i, "name": en[i]} if en and i in en else i for i in v]
        return None
    return None


def comp_fields(sc, pid, enums):
    out = {}
    for k, v in sc.tree(pid).items():
        if k in SKIP_FIELDS:
            continue
        s = ser_value(sc, k, v, enums)
        if s is not None and s != 0 and s is not False and s != []:
            out[k] = s
    return out


def classify(cls):
    if cls.endswith("ShipRoom") or cls == "PlainShipRoom":
        return "rooms"
    if cls == "Vent":
        return "vents"
    if cls.endswith("Console") and cls not in ("DoorConsole", "OpenDoorConsole",
                                                 "ZiplineConsole", "PlatformConsole",
                                                 "VentCleaningConsole", "MapConsole"):
        return "consoles"
    if cls in ("VentCleaningConsole", "MapConsole", "DeconControl"):
        return "consoles_other"
    if cls in ("DoorConsole", "OpenDoorConsole") or cls.endswith("Door") and cls != "MushroomWallDoor":
        return "doors"
    if cls == "MushroomWallDoor":
        return "doors"
    if cls in ("SurvCamera", "FungleSecurityCamera"):
        return "cameras"
    if cls in ("Ladder", "ZiplineBehaviour", "ZiplineConsole", "MovingPlatformBehaviour",
               "PlatformConsole"):
        return "transport"
    return None


def extract_map(sc, root_tpid, enums):
    gos = {}
    for tp in sc.descendants(root_tpid):
        gp = sc.tf[tp]["go"]
        gos[gp] = tp
    hist = collections.Counter()
    res = collections.defaultdict(list)
    ship_pid = None
    for gp in gos:
        for cp in sc.go[gp]["comps"]:
            cls = sc.cls(cp)
            if not cls:
                continue
            hist[cls] += 1
            if cls.endswith("ShipStatus") or cls == "LobbyBehaviour":
                ship_pid = cp
            cat = classify(cls)
            if not cat:
                continue
            e = {"name": sc.go[gp]["name"], "class": cls, "pos": sc.gopos(gp),
                 "path": sc.path(gp)}
            f = comp_fields(sc, cp, enums)
            if f:
                e["fields"] = f
            if cat == "rooms":
                area = sc.tree(cp).get("roomArea", {}).get("m_PathID")
                e["area"] = room_area(sc, area)
            if cat == "vents":
                d = sc.tree(cp)
                e["id"] = d.get("Id")
                e["links"] = {k.lower(): vent_id(sc, d.get(k)) for k in ("Left", "Right", "Center")}
                e["links"] = {k: v for k, v in e["links"].items() if v is not None}
                e["fields"] = {k: v for k, v in f.items() if k not in
                               ("Left", "Right", "Center", "Buttons", "CleaningIndicators")}
                if not e["fields"]:
                    del e["fields"]
            res[cat].append(e)
    meta = {}
    if ship_pid:
        d = sc.tree(ship_pid)
        sg = sc.ref_go(d["m_GameObject"]["m_PathID"]) if False else d["m_GameObject"]["m_PathID"]
        tp = sc.go_tf.get(sg)
        meta["ship_status_class"] = sc.cls(ship_pid)
        for k in ("MapScale", "MaxLightRadius", "MinLightRadius", "EmergencyCooldown",
                  "SpawnRadius", "Type", "Timer"):
            if k in d and isinstance(d[k], (int, float)):
                meta[k] = r3(d[k]) if isinstance(d[k], float) else d[k]
        for k in ("InitialSpawnCenter", "MeetingSpawnCenter", "MeetingSpawnCenter2"):
            if k in d and tp:
                v = d[k]
                meta[k] = {"local": [r3(v["x"]), r3(v["y"])], "world": sc.apply(tp, v["x"], v["y"])}
        eb = d.get("EmergencyButton", {}).get("m_PathID")
        if eb:
            g = sc.ref_go(eb)
            if g:
                meta["emergency_button"] = {"name": sc.go[g]["name"], "pos": sc.gopos(g),
                                            "path": sc.path(g)}
        for k in ("CommonTasks", "LongTasks", "ShortTasks", "SpecialTasks"):
            names = [sc.ref_name(i["m_PathID"]) for i in d.get(k, []) if i["m_PathID"]]
            names = [n for n in names if n]
            if names:
                meta[k] = sorted(names)
    for lst in res.values():
        lst.sort(key=lambda e: (e["path"], e["class"], e["pos"] or [0, 0]))
    return meta, res, hist


def vent_id(sc, ref):
    if not ref or not ref.get("m_PathID"):
        return None
    d = sc.tree(ref["m_PathID"])
    return d.get("Id")


def room_area(sc, pid):
    if not pid or pid not in sc.objs:
        return None
    o = sc.objs[pid]
    d = sc.tree(pid)
    g = d.get("m_GameObject", {}).get("m_PathID")
    tp = sc.go_tf.get(g)
    if tp is None:
        return None
    off = d.get("m_Offset", {"x": 0, "y": 0})
    out = {"collider": o.type.name}
    if o.type.name == "PolygonCollider2D":
        pts = []
        for path in d.get("m_Points", {}).get("m_Paths", []):
            pts.append([sc.apply(tp, p["x"] + off["x"], p["y"] + off["y"]) for p in path])
        out["paths"] = pts
    elif o.type.name == "BoxCollider2D":
        sz = d.get("m_Size", {"x": 0, "y": 0})
        hx, hy = sz["x"] / 2, sz["y"] / 2
        out["paths"] = [[sc.apply(tp, off["x"] + sx * hx, off["y"] + sy * hy)
                         for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]]
    else:
        return {"collider": o.type.name, "center": sc.gopos(g)}
    xs = [p[0] for path in out["paths"] for p in path]
    ys = [p[1] for path in out["paths"] for p in path]
    if xs:
        out["bbox"] = [min(xs), min(ys), max(xs), max(ys)]
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("bundle")
    ap.add_argument("out_dir")
    ap.add_argument("--cs-dir", default=None)
    a = ap.parse_args()
    enums = load_enums(a.cs_dir)
    sc = Scene(a.bundle)
    os.makedirs(a.out_dir, exist_ok=True)
    summary = {}
    for tp, t in sorted(sc.tf.items()):
        if t["father"] in sc.tf:
            continue
        name = sc.go[t["go"]]["name"]
        if name not in MAP_STEMS:
            continue
        meta, res, hist = extract_map(sc, tp, enums)
        stem = MAP_STEMS[name]
        wp = sc.world(tp)
        doc = {"map": stem, "prefab": name, "units": "unity_world_units",
               "root_world_pos": [r3(wp[4]), r3(wp[5])], "meta": meta}
        for k in ("rooms", "vents", "consoles", "consoles_other", "doors", "cameras", "transport"):
            doc[k] = res.get(k, [])
        with open(os.path.join(a.out_dir, stem + ".json"), "w", encoding="utf-8") as fh:
            json.dump(doc, fh, indent=None, separators=(",", ":"), sort_keys=True)
            fh.write("\n")
        summary[stem] = {"prefab": name,
                         "counts": {k: len(doc[k]) for k in ("rooms", "vents", "consoles",
                                    "consoles_other", "doors", "cameras", "transport")},
                         "class_histogram": dict(sorted(hist.items(), key=lambda kv: (-kv[1], kv[0])))}
        print(stem, summary[stem]["counts"])
    with open(os.path.join(a.out_dir, "summary.json"), "w", encoding="utf-8") as fh:
        json.dump(summary, fh, indent=1, sort_keys=True)
        fh.write("\n")


if __name__ == "__main__":
    sys.exit(main())
