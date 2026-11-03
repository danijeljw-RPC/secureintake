#!/usr/bin/env python3
"""Generate src/SecureIntake.Contracts/Catalogue/permissions.json from docs/architecture/28-permission-catalogue.md.

The markdown tables are the reviewed source of truth. CI runs this with --check to fail on drift.
"""
import json, re, sys, pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
DOC = ROOT / "docs/architecture/28-permission-catalogue.md"
OUT = ROOT / "src/SecureIntake.Contracts/Catalogue/permissions.json"
LEVELS = {"S": "standard", "E": "elevated", "X": "sensitive", "Recovery procedure": "recovery"}
ROLE_IDS = {
    "Owner": "owner", "Admin": "admin", "SecAdmin": "security_admin", "Operator": "operator",
    "Auditor": "auditor", "Provisioning Client": "provisioning_client", "Dropbox Mgr": "dropbox_manager",
    "Reviewer": "reviewer", "SecOfficer": "security_officer", "API Client": "api_client",
}

SERVICE_ROLES = {"provisioning_client", "api_client"}

def tables(text):
    section, rows = None, []
    for line in text.splitlines():
        if line.startswith("## "):
            if rows: yield section, rows
            section, rows = line[3:].strip(), []
        elif line.startswith("|"):
            rows.append([c.strip() for c in line.strip().strip("|").split("|")])
    if rows: yield section, rows

def main():
    perms, roles = [], {}
    for section, rows in tables(DOC.read_text(encoding="utf-8")):
        scope = {"Platform scope": "platform", "Organisation scope": "organisation"}.get(section)
        if not scope: continue
        header = rows[0]
        role_cols = [ROLE_IDS[h] for h in header[2:]]
        for r in rows[2:]:
            m = re.match(r"`([a-z_.]+)`\s*(?:\((.*)\))?", r[0])
            name, note = m.group(1), m.group(2)
            entry = {"name": name, "scope": scope, "level": LEVELS[r[1]]}
            if note: entry["note"] = note
            for role, cell in zip(role_cols, r[2:]):
                if not cell: continue
                key = f"{scope}:{role}"
                roles.setdefault(key, {"id": role, "scope": scope, "grants": [], "eligible": []})
                if cell.startswith("✓*"):
                    # API clients are never granted implicitly; they may be assigned an explicit subset.
                    roles[key]["eligible"].append(name)
                else:
                    g = {"permission": name}
                    q = re.search(r"\((.*)\)", cell)
                    if q: g["qualifier"] = q.group(1)
                    roles[key]["grants"].append(g)
            perms.append(entry)
    # ADR-0004: service accounts can't perform JIT elevation, so they never hold Elevated, Sensitive or
    # recovery-procedure permissions. Those cells in doc 28 are moved to an explicit blocked list.
    level = {p["name"]: p["level"] for p in perms}
    for role in roles.values():
        if role["id"] in SERVICE_ROLES:
            blocked = [g["permission"] for g in role["grants"] if level[g["permission"]] != "standard"]
            blocked += [p for p in role["eligible"] if level[p] != "standard"]
            role["grants"] = [g for g in role["grants"] if level[g["permission"]] == "standard"]
            role["eligible"] = [p for p in role["eligible"] if level[p] == "standard"]
            role["blockedForServiceAccounts"] = blocked
    out = {"format": "secure-intake-permissions@1", "catalogueVersion": "2026-11-03",
           "permissions": perms, "roles": sorted(roles.values(), key=lambda r: (r["scope"], r["id"]))}
    text = json.dumps(out, indent=2, ensure_ascii=False) + "\n"
    if "--check" in sys.argv:
        if not OUT.exists() or OUT.read_text(encoding="utf-8") != text:
            sys.exit(f"{OUT.relative_to(ROOT)} is out of date. Run tools/gen_permissions.py")
        return
    OUT.write_text(text, encoding="utf-8")
    print(f"wrote {len(perms)} permissions, {len(roles)} roles")

main()
