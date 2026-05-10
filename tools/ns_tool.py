"""
Namespace cleanup tool for Basis codebase.

Phase 1: Wrap files in `namespace X.Y { ... }` and collect type → ns mapping.
Phase 2: Add `using X.Y;` to all files referencing a now-namespaced type.

Usage:
    python ns_tool.py wrap <plan.json>      # apply namespace wraps from plan
    python ns_tool.py propagate <plan.json> # add using-statements to consumers

plan.json format:
    {
        "files": [
            {"path": "Basis/Packages/.../Foo.cs", "namespace": "Basis.Scripts.X"}
        ]
    }
"""
import sys, os, re, json, argparse
from collections import defaultdict

REPO_ROOT = "."

def read_text(path):
    with open(path, 'rb') as f:
        raw = f.read()
    bom = b''
    if raw.startswith(b'\xef\xbb\xbf'):
        bom = raw[:3]
        raw = raw[3:]
    nl = '\r\n' if b'\r\n' in raw[:200] else '\n'
    return raw.decode('utf-8'), bom, nl

def write_text(path, text, bom, nl):
    if nl != '\n':
        text = text.replace('\n', nl)
    with open(path, 'wb') as f:
        f.write(bom + text.encode('utf-8'))

def has_namespace(text):
    return bool(re.search(r'^\s*namespace\s+[\w.]+', text, re.M))

def find_top_level_types(text):
    # Strip strings/comments first to avoid matching inside them
    # Simple stripping — good enough for our codebase
    stripped = re.sub(r'//.*$', '', text, flags=re.M)
    stripped = re.sub(r'/\*.*?\*/', '', stripped, flags=re.S)
    types = []
    for m in re.finditer(
        r'^\s*(?:\[[^\]]*\]\s*)*'
        r'(?:public|internal|private|protected|sealed|abstract|static|partial|readonly|unsafe|\s)*'
        r'\b(class|struct|interface|enum|record|delegate)\s+(\w+)',
        stripped, re.M
    ):
        types.append(m.group(2))
    return types

def find_using_statements(text):
    return set(m.group(1) for m in re.finditer(r'^\s*using\s+(?!static\b)([\w.]+)\s*;', text, re.M))

def references_type(text, type_name):
    # Strip comments and strings (rough)
    stripped = re.sub(r'//.*$', '', text, flags=re.M)
    stripped = re.sub(r'/\*.*?\*/', '', stripped, flags=re.S)
    # Strip string literals (simple)
    stripped = re.sub(r'"(?:[^"\\]|\\.)*"', '""', stripped)
    return bool(re.search(rf'\b{re.escape(type_name)}\b', stripped))

def wrap_file(path, ns, dry_run=False):
    """Wrap the body of a .cs file in namespace ns { ... }.
    Inserts opening after using statements, closing at end.
    Skips if file already has any namespace."""
    text, bom, nl = read_text(path)
    if has_namespace(text):
        return None, "already_has_ns"
    types = find_top_level_types(text)
    if not types:
        return None, "no_types"

    # Find insertion point for opening brace: after last `using ...;`, but
    # before any [attribute] or class/struct declaration. Also after #if/#region.
    lines = text.split('\n')
    open_idx = 0
    for i, line in enumerate(lines):
        s = line.strip()
        # Skip blank, single-line comments, using stmts, preprocessor
        if not s or s.startswith('//') or s.startswith('/*') or s.startswith('*') \
           or s.startswith('#') or s.startswith('using ') or s.startswith('extern '):
            continue
        open_idx = i
        break

    # Insert opening
    lines.insert(open_idx, f"namespace {ns}\n{{")
    # Find the matching close brace position: end of file, before any trailing #endif
    # Walk backward to find last non-comment/non-blank line
    close_idx = len(lines)
    while close_idx > 0:
        s = lines[close_idx - 1].strip()
        if not s or s.startswith('//'):
            close_idx -= 1
            continue
        break
    # Special case: the LAST meaningful line might be #endif. We want close BEFORE it.
    while close_idx > 0 and lines[close_idx - 1].strip().startswith('#endif'):
        close_idx -= 1
    lines.insert(close_idx, "}")
    new_text = '\n'.join(lines)
    if not dry_run:
        write_text(path, new_text, bom, nl)
    return types, "ok"

def add_using(path, using_ns, dry_run=False):
    text, bom, nl = read_text(path)
    using_line = f"using {using_ns};"
    if re.search(rf'^\s*using\s+{re.escape(using_ns)}\s*;', text, re.M):
        return False
    lines = text.split('\n')
    last_using_idx = -1
    for i, line in enumerate(lines):
        s = line.strip()
        if s.startswith('using ') and s.rstrip().endswith(';'):
            last_using_idx = i
        elif s.startswith('namespace ') or re.match(
            r'^\s*(\[|public\s|internal\s|private\s|sealed\s|abstract\s|static\s|partial\s|class\s|struct\s|interface\s|enum\s)',
            s
        ):
            break
    if last_using_idx >= 0:
        lines.insert(last_using_idx + 1, using_line)
    else:
        # No usings — insert after #if header / preprocessor block at top
        insert_idx = 0
        for i, line in enumerate(lines):
            s = line.strip()
            if s and not s.startswith('#') and not s.startswith('//'):
                insert_idx = i
                break
        lines.insert(insert_idx, using_line)
    new_text = '\n'.join(lines)
    if not dry_run:
        write_text(path, new_text, bom, nl)
    return True

def cmd_wrap(plan_path):
    with open(plan_path) as f:
        plan = json.load(f)
    type_to_ns = {}
    for entry in plan["files"]:
        path = entry["path"]
        ns = entry["namespace"]
        types, status = wrap_file(path, ns)
        if status == "ok":
            for t in types:
                if t in type_to_ns and type_to_ns[t] != ns:
                    print(f"WARN: type {t} mapped to multiple namespaces: {type_to_ns[t]} and {ns}")
                type_to_ns[t] = ns
            print(f"+ wrapped {path}  [ns={ns}]  types={types}")
        else:
            print(f"= skipped {path}  reason={status}")
    out = plan_path.replace(".json", ".typemap.json")
    with open(out, 'w') as f:
        json.dump(type_to_ns, f, indent=2)
    print(f"\nType→namespace map written to {out}")

def cmd_propagate(plan_path, scan_root="Basis"):
    typemap_path = plan_path.replace(".json", ".typemap.json")
    with open(typemap_path) as f:
        type_to_ns = json.load(f)
    # Group types by namespace for efficient scanning
    ns_to_types = defaultdict(set)
    for t, ns in type_to_ns.items():
        ns_to_types[ns].add(t)

    # Walk every .cs file
    edits = 0
    for dirpath, dirs, files in os.walk(scan_root):
        for f in files:
            if not f.endswith('.cs'):
                continue
            full = os.path.join(dirpath, f)
            try:
                text, bom, nl = read_text(full)
            except Exception:
                continue
            # Determine the file's own namespace (so we don't add using for own ns)
            own_ns_match = re.search(r'^\s*namespace\s+([\w.]+)', text, re.M)
            own_ns = own_ns_match.group(1) if own_ns_match else None
            existing_usings = find_using_statements(text)

            for ns, types in ns_to_types.items():
                if ns == own_ns:
                    continue
                if ns in existing_usings:
                    continue
                # Does this file reference any type in this ns?
                if any(references_type(text, t) for t in types):
                    if add_using(full, ns):
                        edits += 1
                        existing_usings.add(ns)
    print(f"\nAdded {edits} using-statements across {scan_root}")

if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    w = sub.add_parser('wrap')
    w.add_argument('plan')
    p = sub.add_parser('propagate')
    p.add_argument('plan')
    p.add_argument('--root', default='Basis')
    args = ap.parse_args()
    if args.cmd == 'wrap':
        cmd_wrap(args.plan)
    elif args.cmd == 'propagate':
        cmd_propagate(args.plan, args.root)
