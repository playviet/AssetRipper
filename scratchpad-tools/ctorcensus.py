# ctorcensus.py <export>/ExportedProject/Assets/Scripts
#
# Two shapes of the "il2cpp inlined the base constructor" family, counted over a whole export:
#
#   1. a constructor with `: base(N args)` where N is not an arity of the DIRECT base - the decompiler
#      hoisted a `.ctor` call that names a strict ANCESTOR, because the level between was inlined away.
#      C# has no syntax for skipping a level. `DesignEvent` is the case; see GATEFLOOR.md.
#   2. a constructor with NO base initializer whose direct base has no 0-argument constructor - the base
#      `.ctor` call was either commented out (`//base._002Ector(...)`) or inlined out of existence.
#
# CAUTION: this matches on ARITY ONLY and does not know about OPTIONAL parameters, so shape 1 over-reports.
# `BaseAnalyticsEvent(string, Dictionary<string,object> = null)` accepts `base(1)` and is not an error.
# Intersect the output with the compiler's CS1729/CS7036 before believing any count from it - on Snacky
# Dash `_4` it reported 9 of shape 1 and only ONE of the nine (DesignEvent) is real.
import re,sys,os,collections
root=sys.argv[1]
# gather: type -> base type name (first in base list, best effort), and type -> set of ctor arities
decl=re.compile(r'^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|protected|private|\s)*\s*(?:sealed\s+|abstract\s+|static\s+|partial\s+|unsafe\s+)*(class|struct)\s+([A-Za-z_]\w*)(<[^{]*?>)?\s*(?::\s*([^{]+?))?\s*$')
types={}   # name -> (base, file)
ctors=collections.defaultdict(list)  # typename -> list of (nargs, baseargs or None, file, line)
files=[]
for dp,dn,fn in os.walk(root):
    for f in fn:
        if f.endswith('.cs'): files.append(os.path.join(dp,f))
def splitargs(s):
    s=s.strip()
    if not s: return 0
    d=0;n=1
    for c in s:
        if c in '([<{': d+=1
        elif c in ')]>}': d-=1
        elif c==',' and d==0: n+=1
    return n
for path in files:
    lines=open(path,encoding='utf-8',errors='replace').read().split('\n')
    stack=[]  # (indent, typename)
    for i,ln in enumerate(lines):
        if not ln.strip() or ln.strip().startswith('//'): continue
        ind=len(ln)-len(ln.lstrip('\t'))
        m=decl.match(ln.rstrip())
        if m and '(' not in ln.split(':')[0]:
            kind,name,gen,bases=m.group(1),m.group(2),m.group(3),m.group(4)
            while stack and stack[-1][0]>=ind: stack.pop()
            stack.append((ind,name))
            b=None
            if bases:
                # first base entry
                d=0;cur=''
                for c in bases:
                    if c in '<([': d+=1
                    elif c in '>)]': d-=1
                    if c==',' and d==0: break
                    cur+=c
                b=cur.strip().split('<')[0].split('.')[-1]
            types[name]=(b,path)
            continue
        while stack and stack[-1][0]>=ind and not ln.strip().startswith(('{','}')): stack.pop()
        if not stack: continue
        tn=stack[-1][1]
        # ctor:  <mods> TypeName(args)  optionally  : base(...)/: this(...)
        cm=re.match(r'^\s*(?:public|internal|protected|private|static|extern|unsafe|\s)*\b'+re.escape(tn)+r'\s*\((.*)\)\s*$',ln.rstrip())
        if cm:
            nxt=lines[i+1].strip() if i+1<len(lines) else ''
            binit=None
            bm=re.match(r'^:\s*(base|this)\s*\((.*)\)\s*$',nxt)
            if bm: binit=(bm.group(1),splitargs(bm.group(2)))
            ctors[tn].append((splitargs(cm.group(1)),binit,path,i+1))
print(f'types={len(types)} ctor-bearing types={len(ctors)}')
# find derived ctors whose base is a type we know, and check arity
mismatch=[];missing=[];okc=0
for tn,cl in ctors.items():
    base=types.get(tn,(None,None))[0]
    if not base or base not in ctors: continue
    baseari={c[0] for c in ctors[base]}
    for (n,binit,path,line) in cl:
        if binit is None:
            if 0 not in baseari:
                missing.append((tn,base,n,sorted(baseari),path,line))
        elif binit[0]=='base':
            if binit[1] not in baseari:
                mismatch.append((tn,base,binit[1],sorted(baseari),path,line))
            else: okc+=1
print('\n== ctor with : base(N) whose N is not an arity of the DIRECT base ==',len(mismatch))
for m in mismatch: print('  %s : %s  base(%d) but %s has %s   %s:%d'%(m[0],m[1],m[2],m[1],m[3],os.path.relpath(m[4],root),m[5]))
print('\n== ctor with NO : base(...) but direct base has no 0-arg ctor ==',len(missing))
for m in missing: print('  %s : %s  ctor(%d args), %s has %s   %s:%d'%(m[0],m[1],m[2],m[1],m[3],os.path.relpath(m[4],root),m[5]))
print('\nbase(...) that DO match:',okc)
