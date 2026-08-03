import sys, re, os
def ratio(path):
	comment=0; code=0; blank=0
	inblock=False
	for line in open(path, encoding='utf-8-sig'):
		s=line.strip()
		if not s:
			blank+=1; continue
		if inblock:
			comment+=1
			if '*/' in s: inblock=False
			continue
		if s.startswith('///') or s.startswith('//'):
			comment+=1; continue
		if s.startswith('/*'):
			comment+=1
			if '*/' not in s: inblock=True
			continue
		code+=1
	return comment, code, blank
tot_c=tot_k=0
for p in sys.argv[1:]:
	c,k,b=ratio(p)
	tot_c+=c; tot_k+=k
	print(f"{os.path.basename(p):35s} comments={c:5d} code={k:5d} ratio={c/k:.3f}")
print(f"{'TOTAL':35s} comments={tot_c:5d} code={tot_k:5d} ratio={tot_c/tot_k:.3f}")
