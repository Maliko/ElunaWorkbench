#!/usr/bin/env python3
"""
Generates the Eluna API database (eluna_api.json) used by Eluna Workbench.

Usage:
    python3 tools/generate_eluna_api.py <path-to-Eluna-checkout> <output.json>

It parses the doxygen-style documentation comments of the method headers in
Eluna/methods/<Core>/*.h (the same comments ElunaDoc uses), the method
registration tables (to know which method exists on which core) and the
event lists in Eluna/hooks/Hooks.h (event ids).
"""
import json
import os
import re
import sys

CORES = ["TrinityCore", "AzerothCore", "Mangos", "CMangos", "VMangos"]

FILE_TO_CLASS = {
    "GlobalMethods.h": None,
    "ObjectMethods.h": "Object",
    "WorldObjectMethods.h": "WorldObject",
    "UnitMethods.h": "Unit",
    "PlayerMethods.h": "Player",
    "CreatureMethods.h": "Creature",
    "GameObjectMethods.h": "GameObject",
    "CorpseMethods.h": "Corpse",
    "ItemMethods.h": "Item",
    "VehicleMethods.h": "Vehicle",
    "GroupMethods.h": "Group",
    "GuildMethods.h": "Guild",
    "AuraMethods.h": "Aura",
    "AuraEffectMethods.h": "AuraEffect",
    "ElunaProcInfoMethods.h": "ElunaProcInfo",
    "SpellInfoMethods.h": "ElunaSpellInfo",
    "SpellMethods.h": "Spell",
    "QuestMethods.h": "Quest",
    "MapMethods.h": "Map",
    "BattleGroundMethods.h": "BattleGround",
    "WorldPacketMethods.h": "WorldPacket",
    "ElunaQueryMethods.h": "ElunaQuery",
    "AchievementMethods.h": "Achievement",
    "ItemTemplateMethods.h": "ItemTemplate",
    "RollMethods.h": "Roll",
    "TicketMethods.h": "Ticket",
    "SpellEntryMethods.h": "SpellEntry",
    "ChatHandlerMethods.h": "ChatHandler",
    "GemPropertiesEntryMethods.h": "GemPropertiesEntry",
    "LootMethods.h": "Loot",
}

DOC_FUNC_RE = re.compile(r"/\*\*(?!\*)(.*?)\*/\s*(?:static\s+)?int\s+(\w+)\s*\(\s*Eluna\s*\*", re.S)
CLASS_DOC_RE = re.compile(r"/\*\*\*(.*?)\*/\s*namespace\s+\w+", re.S)
REG_RE = re.compile(r'\{\s*"(\w+)"\s*,\s*([^}]*)\}')
PARAM_RE = re.compile(r"^(\S+)(?:\s+(\w+|\.\.\.))?(?:\s*=\s*([^:]+?))?\s*(?::\s*(.*))?$")


def clean_comment(body):
    lines = []
    for line in body.splitlines():
        line = line.strip()
        if line.startswith("*"):
            line = line[1:]
            if line.startswith(" "):
                line = line[1:]
        lines.append(line.rstrip())
    return lines


def norm_type(t):
    t = t.strip()
    m = re.match(r"^\[(\w+)\]$", t)
    if m:
        return m.group(1)
    return t


def parse_param(text):
    text = text.strip()
    if text == "...":
        return {"type": "...", "name": "...", "default": None, "description": ""}
    m = PARAM_RE.match(text)
    if not m:
        # e.g. "[ElunaQuery] results or nil if no rows found"
        parts = text.split(None, 2)
        if len(parts) >= 2:
            return {"type": norm_type(parts[0]), "name": re.sub(r"\W", "", parts[1]) or "value",
                    "default": None, "description": parts[2] if len(parts) > 2 else ""}
        return {"type": "any", "name": text, "default": None, "description": ""}
    typ, name, default, desc = m.groups()
    if typ == "...":
        name = "..."
    return {
        "type": norm_type(typ),
        "name": name or "value",
        "default": default.strip() if default else None,
        "description": (desc or "").strip(),
    }


def parse_values(text):
    # [EVENT_NAME, "STATE", <a: number, b: Player>, "comment"]
    m = re.match(r'^\[\s*(\w+)\s*,\s*"([^"]*)"\s*,\s*<([^>]*)>\s*,\s*"(.*)"\s*\]\s*$', text.strip())
    if not m:
        return None
    name, state, params, comment = m.groups()
    plist = []
    for p in params.split(","):
        p = p.strip()
        if not p:
            continue
        if ":" in p:
            pn, pt = p.split(":", 1)
            plist.append({"name": pn.strip(), "type": pt.strip()})
        else:
            plist.append({"name": p, "type": "any"})
    return {"name": name, "state": state, "params": plist, "comment": comment}


def parse_doc(body):
    lines = clean_comment(body)
    desc_lines, params, returns, protos, values, hook = [], [], [], [], [], None
    for line in lines:
        if line.startswith("@param"):
            params.append(parse_param(line[len("@param"):]))
        elif line.startswith("@return"):
            returns.append(parse_param(line[len("@return"):]))
        elif line.startswith("@proto"):
            protos.append(line[len("@proto"):].strip())
        elif line.startswith("@values"):
            v = parse_values(line[len("@values"):])
            if v:
                values.append(v)
        elif line.startswith("@hook"):
            hook = line[len("@hook"):].strip()
        elif line.startswith("@table") or line.startswith("@columns"):
            continue
        elif line.startswith("@"):
            continue
        else:
            desc_lines.append(line)
    desc = "\n".join(desc_lines).strip()
    desc = re.sub(r"\n{3,}", "\n\n", desc)
    return desc, params, returns, protos, values, hook


def parse_event_ids(hooks_h):
    ids = {}
    with open(hooks_h, encoding="utf-8", errors="replace") as f:
        txt = f.read()
    for m in re.finditer(r"X\(\s*(\w+)\s*,\s*(\d+)\s*,", txt):
        ids[m.group(1)] = int(m.group(2))
    # classic enum style fallback: NAME = 1,
    for m in re.finditer(r"^\s*(\w+_EVENT_\w+)\s*=\s*(\d+)", txt, re.M):
        ids.setdefault(m.group(1), int(m.group(2)))
    return ids


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(1)
    root, out = sys.argv[1], sys.argv[2]
    event_ids = parse_event_ids(os.path.join(root, "hooks", "Hooks.h"))

    classes = {}   # name -> dict
    globals_ = {}  # name -> dict
    hooks = {}     # register fn -> dict

    for core in CORES:
        d = os.path.join(root, "methods", core)
        if not os.path.isdir(d):
            continue
        for fn in sorted(os.listdir(d)):
            if not fn.endswith("Methods.h") or fn not in FILE_TO_CLASS:
                continue
            cls = FILE_TO_CLASS[fn]
            with open(os.path.join(d, fn), encoding="utf-8", errors="replace") as f:
                txt = f.read()

            registered = {}
            for m in REG_RE.finditer(txt):
                registered[m.group(1)] = "METHOD_REG_NONE" not in m.group(2)

            docs = {}
            for m in DOC_FUNC_RE.finditer(txt):
                docs[m.group(2)] = m.group(1)

            if cls is not None:
                c = classes.setdefault(cls, {"name": cls, "inherits": [], "description": "", "methods": {}})
                cm = CLASS_DOC_RE.search(txt)
                if cm:
                    cdesc = "\n".join(clean_comment(cm.group(1))).strip()
                    inh = re.search(r"Inherits all methods from:\s*(.*)", cdesc)
                    if inh:
                        c["inherits"] = re.findall(r"\[(\w+)\]", inh.group(1))
                        cdesc = cdesc.replace(inh.group(0), "").strip()
                    if cdesc and not c["description"]:
                        c["description"] = cdesc
                target = c["methods"]
            else:
                target = globals_

            for name, available in registered.items():
                if not available:
                    continue
                entry = target.get(name)
                if entry is None:
                    body = docs.get(name)
                    desc, params, returns, protos, values, hook = parse_doc(body) if body else ("", [], [], [], [], None)
                    entry = {"name": name, "description": desc, "params": params, "returns": returns,
                             "protos": protos, "cores": []}
                    target[name] = entry
                    if values and cls is None:
                        evs = []
                        for v in values:
                            evs.append({"id": event_ids.get(v["name"], -1), "name": v["name"], "state": v["state"],
                                        "params": v["params"], "comment": v["comment"]})
                        hooks[name] = {"register": name, "hook": hook or "", "events": evs}
                entry["cores"].append(core)

    data = {
        "source": "https://github.com/ElunaLuaEngine/Eluna",
        "cores": CORES,
        "classes": [
            {"name": c["name"], "inherits": c["inherits"], "description": c["description"],
             "methods": sorted(c["methods"].values(), key=lambda m: m["name"])}
            for c in sorted(classes.values(), key=lambda c: c["name"])
        ],
        "globals": sorted(globals_.values(), key=lambda m: m["name"]),
        "hooks": sorted(hooks.values(), key=lambda h: h["register"]),
    }
    with open(out, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1, ensure_ascii=False)
    nm = sum(len(c["methods"]) for c in data["classes"])
    print(f"classes={len(data['classes'])} methods={nm} globals={len(data['globals'])} hooks={len(data['hooks'])}")


if __name__ == "__main__":
    main()
